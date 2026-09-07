using System.Text.Json.Serialization;

namespace Consultologist.CopilotAgent.Core.Engine;

// The engine DTOs the agent needs, mirrored on the client side. Field names
// match the engine records so System.Text.Json binds them by name; only the
// members the agent actually sends or reads are modelled. Sources (engine repo
// Consultologist/Consultologist-Blazor):
//   - ConsultGenerationRequest, InputFilePayload, ConsultGenerationJobStartResponse
//       src/Consultologist.Api/Models/ConsultGenerationRequest.cs
//   - DocumentExtractionResponse
//       src/Consultologist.Api/Documents/DocumentExtractions.cs
//   - WorkflowPackage* responses
//       src/Consultologist.Api/Workflow/WorkflowPackageModels.cs

/// <summary>
/// The body of <c>POST ConsultGenerationJobs</c>. The agent assembles only the
/// three fields the intake flow fills: the typed <see cref="Inputs"/> map, the
/// document <see cref="InputFiles"/>, and the <see cref="WorkflowPackage"/> ref
/// it discovered. The engine computes extraction, canonicalisation, the
/// effective-input hash and provenance itself at job start — the agent never
/// supplies them (the verifiability boundary).
/// </summary>
public sealed record ConsultGenerationRequest
{
    /// <summary>The typed named-input map (declared id → value).</summary>
    public Dictionary<string, ConsultInputValue>? Inputs { get; init; }

    /// <summary>Slots filled by documents; per slot a list in supplied order.</summary>
    public Dictionary<string, List<InputFilePayload>>? InputFiles { get; init; }

    /// <summary>The package ref (e.g. <c>name@vYYYY.MM.N</c>); null lets the
    /// engine use the account's pin. The agent echoes the ref it discovered.</summary>
    public string? WorkflowPackage { get; init; }
}

/// <summary>
/// A document supplied for an input slot. System.Text.Json serialises
/// <see cref="Content"/> as base64, so it rides the JSON body — no multipart.
/// No filename: it can itself be PHI and the engine dispatches on content.
/// </summary>
public sealed record InputFilePayload(string ContentType, byte[] Content);

/// <summary>The result of <c>POST DocumentExtractions</c> (preview).</summary>
public sealed record DocumentExtractionResponse(string Text, string Extractor, int? PageCount);

/// <summary>The <c>202</c> body of <c>POST ConsultGenerationJobs</c>.</summary>
public sealed record ConsultGenerationJobStartResponse(string JobId, string StatusUrl);

/// <summary>
/// The poll result of <c>GET ConsultGenerationJobs/{jobId}</c> — only the
/// members the agent surfaces to the clinician. The engine record is larger.
/// </summary>
public sealed record ConsultGenerationJobResponse
{
    public string Status { get; init; } = "";

    public bool Success { get; init; }

    /// <summary>The single rendered deliverable (Completed single-result jobs).</summary>
    public string? AssembledDocument { get; init; }

    /// <summary>The per-deliverable documents (v7+ multi-result packages).</summary>
    public IReadOnlyList<ConsultGenerationResultDocumentResponse>? AssembledDocuments { get; init; }

    /// <summary>The engine's effective-input hash — proof the agent surfaces, never supplies.</summary>
    public string? EffectiveInputHash { get; init; }

    public string? WorkflowPackage { get; init; }

    public string? PackageTitle { get; init; }

    // Progress + the error fields the agent reports on a failed run.
    public int? TotalBlockCount { get; init; }

    public int? CompletedBlockCount { get; init; }

    public int? FailedBlockCount { get; init; }

    public string? RuntimeFailureError { get; init; }

    public string? StartFailure { get; init; }

    public string? AnalysisError { get; init; }
}

/// <summary>One rendered deliverable of a completed multi-result job.</summary>
public sealed record ConsultGenerationResultDocumentResponse(
    string ResultId,
    string Label,
    string? Text = null,
    string? DocumentHash = null);

// ----- Package discovery (GET WorkflowPackages/Current) -----

/// <summary>
/// The pin-resolved package as the setup form (and now the agent) sees it. The
/// agent renders <see cref="Inputs"/> into the Adaptive Card and offers
/// <see cref="Macros"/> as per-run choices. Mirrors the engine's
/// <c>WorkflowPackageResponse</c>.
/// </summary>
public sealed record WorkflowPackageResponse(
    string Name,
    string Version,
    int SpecVersion,
    IReadOnlyList<WorkflowPackageInputResponse>? Inputs = null,
    IReadOnlyList<WorkflowPackageMacroResponse>? Macros = null,
    string? Title = null)
{
    /// <summary>The ref the agent echoes back in <see cref="ConsultGenerationRequest.WorkflowPackage"/>.</summary>
    [JsonIgnore]
    public string Ref => $"{Name}@{Version}";
}

/// <summary>One declared input slot — one typed field on the intake card.</summary>
public sealed record WorkflowPackageInputResponse(
    string Id,
    string Label,
    bool Required,
    string? Type = null,
    IReadOnlyList<string>? Values = null,
    WorkflowPackageElementResponse? Items = null,
    IReadOnlyList<WorkflowPackageFieldResponse>? Fields = null);

/// <summary>One declared field of an object input.</summary>
public sealed record WorkflowPackageFieldResponse(
    string Id,
    string Label,
    bool Required,
    string? Type = null,
    IReadOnlyList<string>? Values = null,
    WorkflowPackageElementResponse? Items = null,
    IReadOnlyList<WorkflowPackageFieldResponse>? Fields = null);

/// <summary>The resolved shape of one array element.</summary>
public sealed record WorkflowPackageElementResponse(
    string Type,
    WorkflowPackageElementResponse? Items = null,
    IReadOnlyList<WorkflowPackageFieldResponse>? Fields = null,
    IReadOnlyList<string>? Values = null);

/// <summary>One optional macro offered as a per-run choice.</summary>
public sealed record WorkflowPackageMacroResponse(string Id, string Label, bool Default);

/// <summary>The declared input type names (engine's <c>WorkflowInputTypes</c>).</summary>
public static class WorkflowInputTypes
{
    public const string Text = "text";
    public const string Date = "date";
    public const string Enum = "enum";
    public const string Boolean = "boolean";
    public const string Number = "number";
    public const string Object = "object";
    public const string Array = "array";
}
