using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Consultologist.CopilotAgent.Core.Engine;

/// <summary>
/// The satellite caller. A thin typed wrapper over the engine's existing doors,
/// presenting the clinician's delegated <c>access_as_user</c> bearer (obtained by
/// the host via Teams SSO + On-Behalf-Of) on every call. It opens no new engine
/// surface — <c>docs/SATELLITE_CALLERS.md</c>.
/// </summary>
/// <remarks>
/// The caller supplies the per-turn bearer through <paramref name="bearer"/>
/// rather than baking a token into the client: a token is a single clinician's
/// for a few minutes, while the client is shared. #610 refuses app-only tokens,
/// so this must always carry a delegated user token.
/// </remarks>
public sealed class EngineApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Discover the account's pinned package and its declared inputs —
    /// the same door the SPA setup form uses. The intake card is built from the
    /// returned <see cref="WorkflowPackageResponse.Inputs"/>.</summary>
    public async Task<WorkflowPackageResponse> GetCurrentPackageAsync(string bearer, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Get, "WorkflowPackages/Current", bearer);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<WorkflowPackageResponse>(JsonOptions, ct).ConfigureAwait(false))
            ?? throw new EngineApiException(response.StatusCode, "Empty package response.");
    }

    /// <summary>Preview a document's extracted text (raw bytes). The engine
    /// re-extracts authoritatively at job start; this is only for the clinician
    /// to confirm before submitting.</summary>
    public async Task<DocumentExtractionResponse> PreviewDocumentAsync(
        string bearer, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Post, "DocumentExtractions", bearer);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<DocumentExtractionResponse>(JsonOptions, ct).ConfigureAwait(false))
            ?? throw new EngineApiException(response.StatusCode, "Empty extraction response.");
    }

    /// <summary>Start a consult run. Returns <c>202 {JobId, StatusUrl}</c>.</summary>
    public async Task<ConsultGenerationJobStartResponse> StartJobAsync(
        string bearer, ConsultGenerationRequest body, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Post, "ConsultGenerationJobs", bearer);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ConsultGenerationJobStartResponse>(JsonOptions, ct).ConfigureAwait(false))
            ?? throw new EngineApiException(response.StatusCode, "Empty job-start response.");
    }

    /// <summary>Poll a job once.</summary>
    public async Task<ConsultGenerationJobResponse> GetJobAsync(string bearer, string jobId, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Get, $"ConsultGenerationJobs/{jobId}", bearer);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ConsultGenerationJobResponse>(JsonOptions, ct).ConfigureAwait(false))
            ?? throw new EngineApiException(response.StatusCode, "Empty job response.");
    }

    /// <summary>Poll until the job leaves a running state (results are fetched,
    /// never pushed — there is no webhook). The scaffold polls; a build pass may
    /// swap in the SSE <c>/events</c> stream.</summary>
    public async Task<ConsultGenerationJobResponse> PollToCompletionAsync(
        string bearer,
        string jobId,
        TimeSpan interval,
        CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var job = await GetJobAsync(bearer, jobId, ct).ConfigureAwait(false);
            if (!IsRunning(job.Status))
            {
                return job;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private static bool IsRunning(string status) =>
        status.Equals("Pending", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Running", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Scheduled", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage Build(HttpMethod method, string relativeUrl, string bearer)
    {
        var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new EngineApiException(response.StatusCode, body);
    }
}

/// <summary>A non-success response from the engine, carrying its status and body.</summary>
public sealed class EngineApiException(HttpStatusCode statusCode, string body)
    : Exception($"Engine returned {(int)statusCode} {statusCode}: {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Body { get; } = body;
}
