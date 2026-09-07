using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;
using Consultologist.CopilotAgent.Core.Intake;

using Xunit;

namespace Consultologist.CopilotAgent.Tests;

public class IntakeCardBuilderTests
{
    private static WorkflowPackageResponse Package(params WorkflowPackageInputResponse[] inputs) =>
        new("acct-demo", "v2026.09.1", SpecVersion: 12, Inputs: inputs, Title: "Demo package");

    private static JsonArray Body(WorkflowPackageResponse package) =>
        IntakeCardBuilder.Build(package)["body"]!.AsArray();

    private static JsonObject? FieldById(WorkflowPackageResponse package, string id) =>
        Body(package)
            .OfType<JsonObject>()
            .FirstOrDefault(node => (string?)node["id"] == id);

    [Fact]
    public void Card_has_schema_type_and_submit_action()
    {
        var card = IntakeCardBuilder.Build(Package(new WorkflowPackageInputResponse("reason", "Reason", true, WorkflowInputTypes.Text)));

        Assert.Equal("AdaptiveCard", (string?)card["type"]);
        Assert.Equal("1.4", (string?)card["version"]);
        var action = card["actions"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Action.Execute", (string?)action["type"]);
        Assert.Equal(IntakeCardBuilder.SubmitVerb, (string?)action["verb"]);
    }

    [Fact]
    public void Text_input_is_multiline_text_with_id_and_required_flag()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("reason", "Reason for referral", true, WorkflowInputTypes.Text)), "reason")!;

        Assert.Equal("Input.Text", (string?)field["type"]);
        Assert.True((bool?)field["isMultiline"]);
        Assert.Equal("Reason for referral", (string?)field["label"]);
        Assert.True((bool?)field["isRequired"]);
    }

    [Fact]
    public void Null_type_falls_back_to_text_the_frozen_v5_v7_convention()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("consult_draft", "Draft", true, Type: null)), "consult_draft")!;

        Assert.Equal("Input.Text", (string?)field["type"]);
    }

    [Fact]
    public void Optional_input_carries_no_required_flag()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("note", "Note", false, WorkflowInputTypes.Text)), "note")!;

        Assert.Null(field["isRequired"]);
    }

    [Fact]
    public void Date_input_maps_to_input_date()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("seen_on", "Seen on", false, WorkflowInputTypes.Date)), "seen_on")!;

        Assert.Equal("Input.Date", (string?)field["type"]);
    }

    [Fact]
    public void Enum_input_maps_to_choiceset_carrying_its_values()
    {
        var field = FieldById(
            Package(new WorkflowPackageInputResponse("urgency", "Urgency", true, WorkflowInputTypes.Enum, Values: ["routine", "urgent"])),
            "urgency")!;

        Assert.Equal("Input.ChoiceSet", (string?)field["type"]);
        var choices = field["choices"]!.AsArray();
        Assert.Equal(2, choices.Count);
        Assert.Equal("routine", (string?)choices[0]!["value"]);
        Assert.Equal("urgent", (string?)choices[1]!["value"]);
    }

    [Fact]
    public void Boolean_input_maps_to_toggle_with_string_on_off()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("urgent", "Urgent", false, WorkflowInputTypes.Boolean)), "urgent")!;

        Assert.Equal("Input.Toggle", (string?)field["type"]);
        Assert.Equal("true", (string?)field["valueOn"]);
        Assert.Equal("false", (string?)field["valueOff"]);
    }

    [Fact]
    public void Number_input_maps_to_input_number()
    {
        var field = FieldById(Package(new WorkflowPackageInputResponse("age", "Age", false, WorkflowInputTypes.Number)), "age")!;

        Assert.Equal("Input.Number", (string?)field["type"]);
    }

    [Fact]
    public void Object_input_emits_nested_fields_under_the_path_separator()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "patient",
            "Patient",
            true,
            WorkflowInputTypes.Object,
            Fields:
            [
                new WorkflowPackageFieldResponse("name", "Name", true, WorkflowInputTypes.Text),
                new WorkflowPackageFieldResponse("dob", "DOB", false, WorkflowInputTypes.Date),
            ]));

        Assert.NotNull(FieldById(package, $"patient{IntakeCardBuilder.PathSeparator}name"));
        var dob = FieldById(package, $"patient{IntakeCardBuilder.PathSeparator}dob")!;
        Assert.Equal("Input.Date", (string?)dob["type"]);
    }

    [Fact]
    public void Array_of_scalar_maps_to_a_single_multiline_text_field()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "meds",
            "Medications",
            false,
            WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(WorkflowInputTypes.Text)));

        var field = FieldById(package, "meds")!;
        Assert.Equal("Input.Text", (string?)field["type"]);
        Assert.True((bool?)field["isMultiline"]);
    }

    [Fact]
    public void Array_of_object_renders_a_note_not_an_input()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "problems",
            "Problem list",
            false,
            WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(
                WorkflowInputTypes.Object,
                Fields: [new WorkflowPackageFieldResponse("label", "Label", true, WorkflowInputTypes.Text)])));

        // No input keyed by the slot id; a subtle TextBlock stands in its place.
        Assert.Null(FieldById(package, "problems"));
        Assert.Contains(
            Body(package).OfType<JsonObject>(),
            node => (string?)node["type"] == "TextBlock" && ((string?)node["text"])?.Contains("Problem list") == true);
    }
}
