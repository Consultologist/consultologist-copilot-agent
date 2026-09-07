using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;

namespace Consultologist.CopilotAgent.Core.Intake;

/// <summary>
/// Builds the Adaptive Card intake form from a package's declared inputs — the
/// agent-side equivalent of the SPA setup form. Pure: it turns a
/// <see cref="WorkflowPackageResponse"/> into card JSON and touches nothing
/// else, so it is unit-tested without Teams or Azure.
///
/// <para>Each declared input becomes one typed field, keyed by a path id so the
/// reader (<see cref="IntakeCardReader"/>) can walk the same spec and reconstruct
/// the value. Both sides compute the path the same way, so the mapping is
/// symmetric by construction.</para>
///
/// <para>Type → control: <c>text</c>→Input.Text, <c>date</c>→Input.Date,
/// <c>enum</c>→Input.ChoiceSet, <c>boolean</c>→Input.Toggle,
/// <c>number</c>→Input.Number, <c>object</c>→a titled container of nested fields
/// (recursive), <c>array</c> of a scalar → a multiline Input.Text (one element
/// per line). Array-of-object is not represented inline — the card shows a note;
/// such packages are handled in the SPA until a later card iteration.</para>
/// </summary>
public static class IntakeCardBuilder
{
    /// <summary>The separator joining an object field's id to its parent's — the
    /// path key the reader splits the spec on. Chosen not to collide with the
    /// kebab/snake ids packages use.</summary>
    public const string PathSeparator = "~";

    public const string SubmitVerb = "submitIntake";

    private const string CardVersion = "1.4";

    /// <summary>Build the full Adaptive Card for a package's inputs.</summary>
    public static JsonObject Build(WorkflowPackageResponse package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = package.Title ?? package.Ref,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["wrap"] = true,
            },
        };

        foreach (var input in package.Inputs ?? [])
        {
            AppendField(body, input.Id, input.Label, input.Required, input.Type, input.Values, input.Items, input.Fields);
        }

        return new JsonObject
        {
            ["$schema"] = "https://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = CardVersion,
            ["body"] = body,
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "Generate consult",
                    ["verb"] = SubmitVerb,
                },
            },
        };
    }

    private static void AppendField(
        JsonArray body,
        string path,
        string label,
        bool required,
        string? type,
        IReadOnlyList<string>? values,
        WorkflowPackageElementResponse? items,
        IReadOnlyList<WorkflowPackageFieldResponse>? fields)
    {
        // A v5–v7 slot sends a null type: it is the frozen text convention.
        switch (type ?? WorkflowInputTypes.Text)
        {
            case WorkflowInputTypes.Text:
                body.Add(TextInput(path, label, required, multiline: true));
                break;

            case WorkflowInputTypes.Date:
                body.Add(Labelled(new JsonObject { ["type"] = "Input.Date", ["id"] = path }, label, required));
                break;

            case WorkflowInputTypes.Enum:
                body.Add(ChoiceSet(path, label, required, values ?? []));
                break;

            case WorkflowInputTypes.Boolean:
                body.Add(new JsonObject
                {
                    ["type"] = "Input.Toggle",
                    ["id"] = path,
                    ["title"] = label,
                    ["valueOn"] = "true",
                    ["valueOff"] = "false",
                });
                break;

            case WorkflowInputTypes.Number:
                body.Add(Labelled(new JsonObject { ["type"] = "Input.Number", ["id"] = path }, label, required));
                break;

            case WorkflowInputTypes.Object:
                AppendObject(body, path, label, fields ?? []);
                break;

            case WorkflowInputTypes.Array:
                AppendArray(body, path, label, required, items);
                break;

            default:
                // An unknown future type falls back to a text field rather than
                // dropping the slot silently.
                body.Add(TextInput(path, label, required, multiline: true));
                break;
        }
    }

    private static void AppendObject(JsonArray body, string path, string label, IReadOnlyList<WorkflowPackageFieldResponse> fields)
    {
        body.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = label,
            ["weight"] = "Bolder",
            ["wrap"] = true,
        });

        foreach (var field in fields)
        {
            AppendField(
                body,
                $"{path}{PathSeparator}{field.Id}",
                field.Label,
                field.Required,
                field.Type,
                field.Values,
                field.Items,
                field.Fields);
        }
    }

    private static void AppendArray(JsonArray body, string path, string label, bool required, WorkflowPackageElementResponse? items)
    {
        var elementType = items?.Type ?? WorkflowInputTypes.Text;
        if (elementType is WorkflowInputTypes.Object or WorkflowInputTypes.Array)
        {
            // Repeating structured rows are not modelled inline on the card;
            // the note keeps the slot visible and honest rather than dropped.
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"{label}: multiple structured entries — use the web app for this package.",
                ["wrap"] = true,
                ["isSubtle"] = true,
            });
            return;
        }

        body.Add(TextInput(path, $"{label} (one per line)", required, multiline: true));
    }

    private static JsonObject TextInput(string path, string label, bool required, bool multiline) =>
        Labelled(
            new JsonObject { ["type"] = "Input.Text", ["id"] = path, ["isMultiline"] = multiline },
            label,
            required);

    private static JsonObject ChoiceSet(string path, string label, bool required, IReadOnlyList<string> values)
    {
        var choices = new JsonArray();
        foreach (var value in values)
        {
            choices.Add(new JsonObject { ["title"] = value, ["value"] = value });
        }

        return Labelled(
            new JsonObject { ["type"] = "Input.ChoiceSet", ["id"] = path, ["style"] = "compact", ["choices"] = choices },
            label,
            required);
    }

    private static JsonObject Labelled(JsonObject input, string label, bool required)
    {
        input["label"] = label;
        if (required)
        {
            input["isRequired"] = true;
            input["errorMessage"] = $"{label} is required.";
        }

        return input;
    }
}
