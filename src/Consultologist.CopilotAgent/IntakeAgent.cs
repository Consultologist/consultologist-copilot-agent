using System.Text.Json;
using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;
using Consultologist.CopilotAgent.Core.Intake;

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace Consultologist.CopilotAgent;

/// <summary>
/// The custom-engine agent. It signs the clinician in (Teams SSO), exchanges
/// that token for a delegated <c>access_as_user</c> bearer to the Consultologist
/// engine (On-Behalf-Of, configured on the "api" handler), discovers the
/// account's pinned package, presents its declared inputs as an Adaptive Card,
/// and on submit assembles a <see cref="ConsultGenerationRequest"/> and runs it.
///
/// <para>The agent is a delivery surface only: it assembles inputs and submits
/// bytes/values. Extraction, canonicalisation, the effective-input hash and
/// provenance are the engine's, computed at job start. See
/// <c>docs/COPILOT_AGENT_SPIKE.md</c> and <c>docs/SATELLITE_CALLERS.md</c> in the
/// engine repo.</para>
///
/// <para><b>Async delivery.</b> A consult run takes minutes, far longer than an
/// <c>Action.Execute</c> invoke may be held open, so the submit handler
/// acknowledges immediately and the run (start → poll → deliverable) happens
/// off-turn, posting results back with a proactive message
/// (<see cref="IChannelAdapter.ContinueConversationAsync(string, ConversationReference, AgentCallbackHandler, CancellationToken)"/>).
/// The background task is fine for this hosted agent; a large deployment would
/// move it onto the SDK's hosted task queue with a durable store.</para>
/// </summary>
public sealed class IntakeAgent : AgentApplication
{
    /// <summary>The OBO/SSO handler name — must match
    /// <c>AgentApplication:UserAuthorization:Handlers</c> in appsettings.</summary>
    private const string OboHandler = "api";

    private const string PendingFilesKey = "pendingReferralFiles";
    private const string CurrentPackageKey = "currentPackage";

    private static readonly JsonSerializerOptions SubmitOptions = new(JsonSerializerDefaults.Web);

    private readonly EngineApiClient _engine;
    private readonly IChannelAdapter _adapter;
    private readonly string _botAppId;
    private readonly string _documentSlot;
    private readonly TimeSpan _pollInterval;

    public IntakeAgent(
        AgentApplicationOptions options,
        EngineApiClient engine,
        IChannelAdapter adapter,
        IConfiguration configuration)
        : base(options)
    {
        _engine = engine;
        _adapter = adapter;
        _botAppId = configuration["TokenValidation:Audiences:0"]
            ?? configuration["Connections:ServiceConnection:Settings:ClientId"]
            ?? throw new InvalidOperationException("The bot app id (TokenValidation:Audiences) is not configured.");
        _documentSlot = configuration["Engine:DocumentInputSlot"] ?? "referral";
        _pollInterval = TimeSpan.FromSeconds(
            double.TryParse(configuration["Engine:PollIntervalSeconds"], out var s) ? s : 3);

        // Greet on join (no token needed yet).
        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);

        // Any inbound message: auto sign-in on the "api" handler runs first, so
        // the OBO token is available; then discover the package and show the card
        // (also stashing any attached documents for the run).
        OnActivity(ActivityTypes.Message, OnMessageAsync, autoSignInHandlers: [OboHandler], rank: RouteRank.Last);

        // The card's Action.Execute submit — same auto sign-in, so the token is
        // present when we start the job.
        AdaptiveCards.OnActionExecute(IntakeCardBuilder.SubmitVerb, OnIntakeSubmitAsync, autoSignInHandlers: [OboHandler]);
    }

    private async Task WelcomeAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient?.Id)
            {
                await turnContext.SendActivityAsync(
                    "Welcome to Consultologist. Send a message to load your consult intake form, "
                    + "and attach a referral document if you have one.",
                    cancellationToken: ct);
            }
        }
    }

    private async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
    {
        var token = await UserAuthorization.GetTurnTokenAsync(turnContext, OboHandler);

        // Preview and stash any attached documents; they ride the next submit.
        await StashAttachedDocumentsAsync(turnContext, turnState, token, ct);

        WorkflowPackageResponse package;
        try
        {
            package = await _engine.GetCurrentPackageAsync(token, ct);
        }
        catch (EngineApiException ex)
        {
            await turnContext.SendActivityAsync($"Could not load your workflow package: {ex.Message}", cancellationToken: ct);
            return;
        }

        // Cache the resolved package so the submit handler maps inputs without a
        // second (slow) round-trip inside the invoke turn.
        turnState.Conversation.SetValue(CurrentPackageKey, JsonSerializer.Serialize(package, SubmitOptions));

        var card = IntakeCardBuilder.Build(package);
        var attachment = new AdaptiveCardCard(card.ToJsonString()).ToAttachment();
        await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), ct);
    }

    private async Task<AdaptiveCardInvokeResponse> OnIntakeSubmitAsync(
        ITurnContext turnContext, ITurnState turnState, object data, CancellationToken ct)
    {
        // Keep this handler fast: an Action.Execute invoke must return in seconds.
        // No polling here — the run happens off-turn (DeliverConsultInBackground).
        var token = await UserAuthorization.GetTurnTokenAsync(turnContext, OboHandler);

        var package = ReadCachedPackage(turnState);
        if (package is null)
        {
            try
            {
                package = await _engine.GetCurrentPackageAsync(token, ct);
            }
            catch (EngineApiException ex)
            {
                return MessageResponse($"Could not load your workflow package: {ex.Message}");
            }
        }

        var submit = JsonNode.Parse(JsonSerializer.Serialize(data, SubmitOptions))?.AsObject() ?? new JsonObject();
        var read = IntakeCardReader.Read(package, submit);
        if (!read.IsValid)
        {
            var problems = read.MissingRequired.Select(id => $"{id} (required)")
                .Concat(read.Invalid.Select(id => $"{id} (invalid)"));
            return MessageResponse("Please complete the form: " + string.Join(", ", problems) + ".");
        }

        var request = new ConsultGenerationRequest
        {
            WorkflowPackage = package.Ref,
            Inputs = read.Inputs,
            InputFiles = ReadPendingFiles(turnState),
        };
        ClearPendingFiles(turnState);

        // Hand the run to a background task and reply immediately. The turn's
        // CancellationToken dies with the turn, so the background work uses None.
        var reference = turnContext.Activity.GetConversationReference();
        DeliverConsultInBackground(reference, token, request);

        return MessageResponse("Working on your consult — I'll post the result here as soon as it's ready.");
    }

    /// <summary>Start the job, poll it to completion, and post the deliverable
    /// back as proactive messages — all off the invoke turn.</summary>
    private void DeliverConsultInBackground(ConversationReference reference, string token, ConsultGenerationRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var start = await _engine.StartJobAsync(token, request, CancellationToken.None).ConfigureAwait(false);
                await SendProactiveAsync(reference, $"Consult started (job {start.JobId}). Generating…").ConfigureAwait(false);

                var job = await _engine.PollToCompletionAsync(token, start.JobId, _pollInterval, CancellationToken.None).ConfigureAwait(false);
                await SendProactiveAsync(reference, DescribeOutcome(job)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await SendProactiveAsync(reference, $"The consult run could not be completed: {ex.Message}").ConfigureAwait(false);
                }
                catch
                {
                    // Nothing more we can do from the background task.
                }
            }
        });
    }

    private Task SendProactiveAsync(ConversationReference reference, string text) =>
        // The agent-id overload is what a background proactive send needs — the
        // ClaimsIdentity overload the SDK now prefers wants a turn identity we no
        // longer hold here; the SDK synthesises one from the agent id for us.
#pragma warning disable CS0618
        _adapter.ContinueConversationAsync(
            _botAppId,
            reference,
            (proactive, ct) => proactive.SendActivityAsync(text, cancellationToken: ct),
            CancellationToken.None);
#pragma warning restore CS0618

    private static WorkflowPackageResponse? ReadCachedPackage(ITurnState turnState)
    {
        var raw = turnState.Conversation.GetValue<string>(CurrentPackageKey);
        return string.IsNullOrEmpty(raw)
            ? null
            : JsonSerializer.Deserialize<WorkflowPackageResponse>(raw, SubmitOptions);
    }

    private async Task StashAttachedDocumentsAsync(
        ITurnContext turnContext, ITurnState turnState, string token, CancellationToken ct)
    {
        var files = turnState.Temp?.InputFiles;
        if (files is null || files.Count == 0)
        {
            return;
        }

        var pending = ReadPendingArray(turnState);
        foreach (var file in files)
        {
            var bytes = file.Content.ToArray();
            try
            {
                var preview = await _engine.PreviewDocumentAsync(token, bytes, file.ContentType, ct);
                await turnContext.SendActivityAsync(
                    $"Attached document read ({preview.Extractor}, {preview.PageCount?.ToString() ?? "?"} page(s)). "
                    + "It will be included when you submit the form.",
                    cancellationToken: ct);
            }
            catch (EngineApiException ex)
            {
                await turnContext.SendActivityAsync($"Could not read an attached document: {ex.Message}", cancellationToken: ct);
                continue;
            }

            pending.Add(new JsonObject
            {
                ["contentType"] = file.ContentType,
                ["content"] = Convert.ToBase64String(bytes),
            });
        }

        turnState.Conversation.SetValue(PendingFilesKey, pending.ToJsonString());
    }

    private Dictionary<string, List<InputFilePayload>>? ReadPendingFiles(ITurnState turnState)
    {
        var pending = ReadPendingArray(turnState);
        if (pending.Count == 0)
        {
            return null;
        }

        var payloads = new List<InputFilePayload>();
        foreach (var entry in pending.OfType<JsonObject>())
        {
            var contentType = (string?)entry["contentType"] ?? "application/octet-stream";
            var content = Convert.FromBase64String((string?)entry["content"] ?? "");
            payloads.Add(new InputFilePayload(contentType, content));
        }

        return new Dictionary<string, List<InputFilePayload>> { [_documentSlot] = payloads };
    }

    private static JsonArray ReadPendingArray(ITurnState turnState)
    {
        var raw = turnState.Conversation.GetValue<string>(PendingFilesKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        return JsonNode.Parse(raw)?.AsArray() ?? [];
    }

    private static void ClearPendingFiles(ITurnState turnState) =>
        turnState.Conversation.SetValue(PendingFilesKey, string.Empty);

    private static string DescribeOutcome(ConsultGenerationJobResponse job)
    {
        if (job.Success)
        {
            var hash = job.EffectiveInputHash is null ? "" : $"\n\nEffective-input hash: `{job.EffectiveInputHash}`";
            if (!string.IsNullOrEmpty(job.AssembledDocument))
            {
                return job.AssembledDocument + hash;
            }

            if (job.AssembledDocuments is { Count: > 0 })
            {
                var labels = string.Join(", ", job.AssembledDocuments.Select(d => d.Label));
                return $"Generated {job.AssembledDocuments.Count} deliverable(s): {labels}.{hash}";
            }

            return "The consult completed." + hash;
        }

        var error = job.RuntimeFailureError ?? job.StartFailure ?? job.AnalysisError ?? job.Status;
        return $"The consult failed: {error}";
    }

    private static AdaptiveCardInvokeResponse MessageResponse(string text) =>
        new()
        {
            StatusCode = 200,
            Type = AdaptiveCardCard.ContentType,
            Value = new JsonObject
            {
                ["type"] = "AdaptiveCard",
                ["version"] = "1.4",
                ["body"] = new JsonArray
                {
                    new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true },
                },
            },
        };
}
