using System.Text.Json.Nodes;

using Consultologist.CopilotAgent.Core.Engine;
using Consultologist.CopilotAgent.Core.Intake;

using Xunit;

namespace Consultologist.CopilotAgent.Tests;

/// <summary>
/// Behavioural edge cases of the mapping: null guards, empty declarations, and
/// the array element paths. These pin the branches the happy-path tests do not
/// reach.
/// </summary>
public class IntakeMappingEdgeCaseTests
{
    private static WorkflowPackageResponse Package(params WorkflowPackageInputResponse[] inputs) =>
        new("acct-demo", "v2026.09.1", SpecVersion: 12, Inputs: inputs);

    // ---- null-argument guards ----

    [Fact]
    public void Builder_rejects_a_null_package()
    {
        Assert.Throws<ArgumentNullException>(() => IntakeCardBuilder.Build(null!));
    }

    [Fact]
    public void Reader_rejects_null_package_or_submit()
    {
        Assert.Throws<ArgumentNullException>(() => IntakeCardReader.Read(null!, new JsonObject()));
        Assert.Throws<ArgumentNullException>(() => IntakeCardReader.Read(Package(), null!));
    }

    // ---- empty / null declarations ----

    [Fact]
    public void Package_with_no_inputs_builds_a_card_and_reads_an_empty_valid_set()
    {
        var package = new WorkflowPackageResponse("acct-demo", "v1", SpecVersion: 12, Inputs: null);

        var card = IntakeCardBuilder.Build(package);
        Assert.Equal("AdaptiveCard", (string?)card["type"]);

        var result = IntakeCardReader.Read(package, new JsonObject());
        Assert.True(result.IsValid);
        Assert.Empty(result.Inputs);
    }

    [Fact]
    public void Object_input_with_null_fields_neither_throws_nor_yields_a_value()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "patient", "Patient", Required: false, WorkflowInputTypes.Object, Fields: null));

        // Builder must not throw on the missing field list.
        var card = IntakeCardBuilder.Build(package);
        Assert.Equal("AdaptiveCard", (string?)card["type"]);

        var result = IntakeCardReader.Read(package, new JsonObject());
        Assert.True(result.IsValid);
        Assert.DoesNotContain("patient", result.Inputs.Keys);
    }

    // ---- array element paths ----

    [Fact]
    public void Array_of_number_reads_each_line_as_a_number_keeping_spelling()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "doses", "Doses", Required: false, WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(WorkflowInputTypes.Number)));

        var result = IntakeCardReader.Read(package, new JsonObject { ["doses"] = "1.50\n2.5" });

        var doses = result.Inputs["doses"];
        Assert.Equal(ConsultInputKind.Array, doses.Kind);
        Assert.Equal(ConsultInputKind.Number, doses.Elements![0].Kind);
        Assert.Equal(["1.50", "2.5"], doses.Elements!.Select(e => e.Number));
    }

    [Fact]
    public void Array_of_number_with_a_bad_element_is_invalid()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "doses", "Doses", Required: false, WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(WorkflowInputTypes.Number)));

        var result = IntakeCardReader.Read(package, new JsonObject { ["doses"] = "1.5\n1e3" });

        Assert.False(result.IsValid);
        Assert.Contains("doses", result.Invalid);
    }

    [Fact]
    public void Array_of_scalar_drops_blank_and_whitespace_only_lines()
    {
        var package = Package(new WorkflowPackageInputResponse(
            "meds", "Meds", Required: false, WorkflowInputTypes.Array,
            Items: new WorkflowPackageElementResponse(WorkflowInputTypes.Text)));

        var result = IntakeCardReader.Read(package, new JsonObject { ["meds"] = "aspirin\n   \n\nmetformin" });

        Assert.Equal(["aspirin", "metformin"], result.Inputs["meds"].Elements!.Select(e => e.Text));
    }

    // ---- null / non-string submit values ----

    [Fact]
    public void A_json_null_for_a_required_text_field_reads_as_missing()
    {
        var package = Package(new WorkflowPackageInputResponse("reason", "Reason", Required: true, WorkflowInputTypes.Text));

        var submit = new JsonObject { ["reason"] = null };
        var result = IntakeCardReader.Read(package, submit);

        Assert.Contains("reason", result.MissingRequired);
        Assert.DoesNotContain("reason", result.Inputs.Keys);
    }

    [Fact]
    public void A_json_null_for_a_required_number_field_reads_as_missing()
    {
        var package = Package(new WorkflowPackageInputResponse("dose", "Dose", Required: true, WorkflowInputTypes.Number));

        var result = IntakeCardReader.Read(package, new JsonObject { ["dose"] = null });

        Assert.Contains("dose", result.MissingRequired);
    }
}
