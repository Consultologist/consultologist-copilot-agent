using System.Text;
using System.Text.Json;

using Consultologist.CopilotAgent.Core.Engine;

using Xunit;

namespace Consultologist.CopilotAgent.Tests;

/// <summary>
/// The wire contract the engine's <c>ConsultInputValue</c> expects: text/date/
/// enum → JSON string, boolean → JSON boolean, number → a JSON number carrying
/// the exact spelling, structure → structure. A drift here is a 400/422 at the
/// engine, so it is pinned directly.
/// </summary>
public class ConsultInputValueWireTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Json(ConsultInputValue value) => JsonSerializer.Serialize(value, Options);

    [Fact]
    public void Text_serialises_as_a_json_string()
    {
        Assert.Equal("\"chest pain\"", Json(ConsultInputValue.OfText("chest pain")));
    }

    [Fact]
    public void Boolean_serialises_as_a_json_boolean_not_a_string()
    {
        Assert.Equal("true", Json(ConsultInputValue.OfBoolean(true)));
        Assert.Equal("false", Json(ConsultInputValue.OfBoolean(false)));
    }

    [Fact]
    public void Number_serialises_as_a_bare_number_keeping_its_spelling()
    {
        Assert.Equal("1.50", Json(ConsultInputValue.OfNumber("1.50")));
        Assert.Equal("-3", Json(ConsultInputValue.OfNumber("-3")));
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData("abc")]
    public void Number_rejects_forms_the_format_cannot_carry(string spelling)
    {
        Assert.False(ConsultInputValue.TryOfNumber(spelling, out _));
        Assert.Throws<ArgumentException>(() => ConsultInputValue.OfNumber(spelling));
    }

    [Fact]
    public void Object_serialises_fields_in_supplied_order()
    {
        var value = ConsultInputValue.OfObject(
        [
            new ConsultInputEntry("name", ConsultInputValue.OfText("Jane")),
            new ConsultInputEntry("age", ConsultInputValue.OfNumber("40")),
        ]);

        Assert.Equal("{\"name\":\"Jane\",\"age\":40}", Json(value));
    }

    [Fact]
    public void Object_rejects_a_repeated_field_id()
    {
        Assert.Throws<ArgumentException>(() => ConsultInputValue.OfObject(
        [
            new ConsultInputEntry("x", ConsultInputValue.OfText("a")),
            new ConsultInputEntry("x", ConsultInputValue.OfText("b")),
        ]));
    }

    [Fact]
    public void Array_serialises_elements_in_order_and_carries_nulls_inside()
    {
        var value = ConsultInputValue.OfArray(
        [
            ConsultInputValue.OfText("a"),
            ConsultInputValue.NullElement,
            ConsultInputValue.OfText("b"),
        ]);

        Assert.Equal("[\"a\",null,\"b\"]", Json(value));
    }

    [Fact]
    public void Input_map_serialises_as_a_typed_object()
    {
        var request = new ConsultGenerationRequest
        {
            WorkflowPackage = "acct-demo@v2026.09.1",
            Inputs = new Dictionary<string, ConsultInputValue>
            {
                ["reason"] = ConsultInputValue.OfText("referral"),
                ["urgent"] = ConsultInputValue.OfBoolean(true),
            },
        };

        var json = JsonSerializer.Serialize(request, Options);
        Assert.Contains("\"reason\":\"referral\"", json);
        Assert.Contains("\"urgent\":true", json);
        Assert.Contains("\"workflowPackage\":\"acct-demo@v2026.09.1\"", json);
    }

    [Fact]
    public void Input_file_rides_as_base64_bytes_with_a_content_type_and_no_filename()
    {
        var payload = new InputFilePayload("application/pdf", Encoding.ASCII.GetBytes("PDF"));
        var request = new ConsultGenerationRequest
        {
            InputFiles = new Dictionary<string, List<InputFilePayload>> { ["referral"] = [payload] },
        };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.Contains("\"contentType\":\"application/pdf\"", json);
        Assert.Contains($"\"content\":\"{Convert.ToBase64String(Encoding.ASCII.GetBytes("PDF"))}\"", json);
        Assert.DoesNotContain("filename", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name", json, StringComparison.OrdinalIgnoreCase);
    }
}
