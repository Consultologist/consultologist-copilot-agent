using System.Text.Json;
using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;

namespace Consultologist.CopilotAgent.Core.Intake;

/// <summary>The outcome of reading a submitted intake card.</summary>
/// <param name="Inputs">The typed input map ready for
///   <see cref="ConsultGenerationRequest.Inputs"/>.</param>
/// <param name="MissingRequired">Ids of required inputs left blank — the agent
///   refuses to start the run while any remain.</param>
/// <param name="Invalid">Ids whose supplied value does not fit the declared type
///   on the wire (e.g. a number in exponent form) — also blocks the start.</param>
public sealed record IntakeReadResult(
    Dictionary<string, ConsultInputValue> Inputs,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> Invalid)
{
    /// <summary>True when the input set is safe to submit.</summary>
    public bool IsValid => MissingRequired.Count == 0 && Invalid.Count == 0;
}

/// <summary>
/// Reads an <c>Action.Execute</c> submit payload back into a typed
/// <see cref="ConsultInputValue"/> map, driven by the same package spec the
/// card was built from (<see cref="IntakeCardBuilder"/>). Pure and
/// dependency-free, so the wire contract is unit-tested directly.
///
/// <para>The engine re-validates every value against the declaration at job
/// start (its 422); this reader only enforces the wire shape — required-ness and
/// the type→JSON-kind mapping — so the agent can refuse an obviously incomplete
/// set before calling out.</para>
/// </summary>
public static class IntakeCardReader
{
    /// <summary>Read the submit payload into a typed input map.</summary>
    public static IntakeReadResult Read(WorkflowPackageResponse package, JsonObject submit)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(submit);

        var inputs = new Dictionary<string, ConsultInputValue>(StringComparer.Ordinal);
        var missing = new List<string>();
        var invalid = new List<string>();

        foreach (var input in package.Inputs ?? [])
        {
            var value = ReadField(
                submit, input.Id, input.Required, input.Type, input.Values, input.Items, input.Fields, missing, invalid);
            if (value is not null)
            {
                inputs[input.Id] = value;
            }
        }

        return new IntakeReadResult(inputs, missing, invalid);
    }

    private static ConsultInputValue? ReadField(
        JsonObject submit,
        string path,
        bool required,
        string? type,
        IReadOnlyList<string>? values,
        WorkflowPackageElementResponse? items,
        IReadOnlyList<WorkflowPackageFieldResponse>? fields,
        List<string> missing,
        List<string> invalid)
    {
        switch (type ?? WorkflowInputTypes.Text)
        {
            case WorkflowInputTypes.Text:
            case WorkflowInputTypes.Date:
            case WorkflowInputTypes.Enum:
            {
                var text = ReadString(submit, path);
                if (string.IsNullOrEmpty(text))
                {
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                return ConsultInputValue.OfText(text);
            }

            case WorkflowInputTypes.Boolean:
            {
                // A toggle submits "true"/"false"; an untouched one submits its
                // valueOff, so absence reads as false rather than missing.
                var text = ReadString(submit, path);
                return ConsultInputValue.OfBoolean(string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));
            }

            case WorkflowInputTypes.Number:
            {
                var raw = ReadNumberRaw(submit, path);
                if (string.IsNullOrEmpty(raw))
                {
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                if (!ConsultInputValue.TryOfNumber(raw, out var number))
                {
                    invalid.Add(path);
                    return null;
                }

                return number;
            }

            case WorkflowInputTypes.Object:
            {
                var entries = new List<ConsultInputEntry>();
                foreach (var field in fields ?? [])
                {
                    var child = ReadField(
                        submit,
                        $"{path}{IntakeCardBuilder.PathSeparator}{field.Id}",
                        field.Required,
                        field.Type,
                        field.Values,
                        field.Items,
                        field.Fields,
                        missing,
                        invalid);
                    if (child is not null)
                    {
                        entries.Add(new ConsultInputEntry(field.Id, child));
                    }
                }

                if (entries.Count == 0)
                {
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                return ConsultInputValue.OfObject(entries);
            }

            case WorkflowInputTypes.Array:
            {
                var elementType = items?.Type ?? WorkflowInputTypes.Text;
                if (elementType is WorkflowInputTypes.Object or WorkflowInputTypes.Array)
                {
                    // Not fillable from the card (see IntakeCardBuilder). A
                    // required one blocks the start, pointing the clinician to
                    // the web app rather than starting an incomplete run.
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                var lines = ReadLines(submit, path);
                if (lines.Count == 0)
                {
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                var elements = new List<ConsultInputValue>();
                foreach (var line in lines)
                {
                    if (elementType == WorkflowInputTypes.Number)
                    {
                        if (!ConsultInputValue.TryOfNumber(line, out var number))
                        {
                            invalid.Add(path);
                            return null;
                        }

                        elements.Add(number);
                    }
                    else
                    {
                        elements.Add(ConsultInputValue.OfText(line));
                    }
                }

                return ConsultInputValue.OfArray(elements);
            }

            default:
            {
                var text = ReadString(submit, path);
                if (string.IsNullOrEmpty(text))
                {
                    if (required)
                    {
                        missing.Add(path);
                    }

                    return null;
                }

                return ConsultInputValue.OfText(text);
            }
        }
    }

    private static string? ReadString(JsonObject submit, string path) =>
        submit.TryGetPropertyValue(path, out var node) && node is not null && node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;

    /// <summary>The number's raw token text, preserving the caller's exact
    /// spelling whether the platform sent it as a JSON number or a string.</summary>
    private static string? ReadNumberRaw(JsonObject submit, string path)
    {
        if (!submit.TryGetPropertyValue(path, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.String => node.GetValue<string>(),
            _ => null,
        };
    }

    private static List<string> ReadLines(JsonObject submit, string path)
    {
        var text = ReadString(submit, path);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
