using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;
using Consultologist.CopilotAgent.Core.Intake;

using Xunit;

namespace Consultologist.CopilotAgent.Tests;

public class IntakeCardReaderTests
{
    private static WorkflowPackageResponse Package(params WorkflowPackageInputResponse[] inputs) =>
        new("acct-demo", "v2026.09.1", SpecVersion: 12, Inputs: inputs);

    [Fact]
    public void Required_blank_input_is_reported_missing_and_blocks_start()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("reason", "Reason", true, WorkflowInputTypes.Text)),
            new JsonObject());

        Assert.False(result.IsValid);
        Assert.Contains("reason", result.MissingRequired);
        Assert.Empty(result.Inputs);
    }

    [Fact]
    public void Text_input_reads_as_text_value()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("reason", "Reason", true, WorkflowInputTypes.Text)),
            new JsonObject { ["reason"] = "chest pain" });

        Assert.True(result.IsValid);
        var value = result.Inputs["reason"];
        Assert.Equal(ConsultInputKind.Text, value.Kind);
        Assert.Equal("chest pain", value.Text);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Toggle_string_reads_as_boolean(string submitted, bool expected)
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("urgent", "Urgent", false, WorkflowInputTypes.Boolean)),
            new JsonObject { ["urgent"] = submitted });

        Assert.Equal(ConsultInputKind.Boolean, result.Inputs["urgent"].Kind);
        Assert.Equal(expected, result.Inputs["urgent"].Flag);
    }

    [Fact]
    public void Number_preserves_exact_spelling()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("dose", "Dose", false, WorkflowInputTypes.Number)),
            new JsonObject { ["dose"] = "1.50" });

        Assert.Equal("1.50", result.Inputs["dose"].Number);
    }

    [Fact]
    public void Number_in_exponent_form_is_invalid_and_blocks_start()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("dose", "Dose", false, WorkflowInputTypes.Number)),
            new JsonObject { ["dose"] = "1e3" });

        Assert.False(result.IsValid);
        Assert.Contains("dose", result.Invalid);
        Assert.DoesNotContain("dose", result.Inputs.Keys);
    }

    [Fact]
    public void Number_sent_as_json_number_keeps_its_token_spelling()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("age", "Age", false, WorkflowInputTypes.Number)),
            new JsonObject { ["age"] = 42 });

        Assert.Equal("42", result.Inputs["age"].Number);
    }

    [Fact]
    public void Enum_reads_as_the_chosen_string()
    {
        var result = IntakeCardReader.Read(
            Package(new WorkflowPackageInputResponse("urgency", "Urgency", true, WorkflowInputTypes.Enum, Values: ["routine", "urgent"])),
            new JsonObject { ["urgency"] = "urgent" });

        Assert.Equal(ConsultInputKind.Text, result.Inputs["urgency"].Kind);
        Assert.Equal("urgent", result.Inputs["urgency"].Text);
    }

    [Fact]
    public void Object_reconstructs_from_nested_path_ids()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "patient",
            "Patient",
            true,
            WorkflowInputTypes.Object,
            Fields:
            [
                new WorkflowPackageFieldResponse("name", "Name", true, WorkflowInputTypes.Text),
                new WorkflowPackageFieldResponse("age", "Age", false, WorkflowInputTypes.Number),
            ]));

        var sep = IntakeCardBuilder.PathSeparator;
        var result = IntakeCardReader.Read(package, new JsonObject
        {
            [$"patient{sep}name"] = "Jane",
            [$"patient{sep}age"] = "40",
        });

        var patient = result.Inputs["patient"];
        Assert.Equal(ConsultInputKind.Object, patient.Kind);
        Assert.Collection(
            patient.Fields!,
            f => { Assert.Equal("name", f.Id); Assert.Equal("Jane", f.Value.Text); },
            f => { Assert.Equal("age", f.Id); Assert.Equal("40", f.Value.Number); });
    }

    [Fact]
    public void Object_with_no_supplied_fields_is_absent_and_missing_when_required()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "patient",
            "Patient",
            true,
            WorkflowInputTypes.Object,
            Fields: [new WorkflowPackageFieldResponse("name", "Name", false, WorkflowInputTypes.Text)]));

        var result = IntakeCardReader.Read(package, new JsonObject());

        Assert.Contains("patient", result.MissingRequired);
        Assert.DoesNotContain("patient", result.Inputs.Keys);
    }

    [Fact]
    public void Array_of_scalar_splits_lines_into_elements()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "meds",
            "Medications",
            false,
            WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(WorkflowInputTypes.Text)));

        var result = IntakeCardReader.Read(package, new JsonObject { ["meds"] = "aspirin\nmetformin\n" });

        var meds = result.Inputs["meds"];
        Assert.Equal(ConsultInputKind.Array, meds.Kind);
        Assert.Equal(["aspirin", "metformin"], meds.Elements!.Select(e => e.Text));
    }

    [Fact]
    public void Array_of_object_cannot_be_filled_from_the_card_and_blocks_when_required()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "problems",
            "Problems",
            true,
            WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(
                WorkflowInputTypes.Object,
                Fields: [new WorkflowPackageFieldResponse("label", "Label", true, WorkflowInputTypes.Text)])));

        var result = IntakeCardReader.Read(package, new JsonObject());

        Assert.Contains("problems", result.MissingRequired);
    }
}
