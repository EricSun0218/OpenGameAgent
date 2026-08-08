using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class StreamingJsonTests
{
    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("{\"path\":\"README.md\"}", "{\"path\":\"README.md\"}")]
    [InlineData("{\"path\":\"READ", "{\"path\":\"READ\"}")]
    [InlineData("{\"depth\":", "{\"depth\":null}")]
    [InlineData("{\"depth\":2,", "{\"depth\":2}")]
    [InlineData("{\"items\":[1,2", "{\"items\":[1,2]}")]
    [InlineData("not-json", "{}")]
    public void ParseObjectAlwaysReturnsAValidObject(string? input, string expected)
    {
        var actual = StreamingJson.ParseObject(input);

        Assert.Equal(expected, actual);
        using var document = JsonDocument.Parse(actual);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void RepairEscapesRawControlsAndInvalidStringEscapes()
    {
        var repaired = StreamingJson.Repair("{\"path\":\"a\\q\nb\"}");

        Assert.Equal("{\"path\":\"a\\\\q\\nb\"}", repaired);
        Assert.Equal("{\"path\":\"a\\\\q\\nb\"}", StreamingJson.ParseObject(repaired));
        var parsed = StreamingJson.ParseWithRepair("{\"path\":\"a\\q\nb\"}");
        Assert.Equal("a\\q\nb", parsed.GetProperty("path").GetString());
    }

    [Fact]
    public void ParseWithRepairDoesNotHideStructuralJsonErrors()
    {
        Assert.ThrowsAny<JsonException>(() => StreamingJson.ParseWithRepair("{\"value\":}"));
    }

    [Fact]
    public void ParseObjectRejectsExcessiveDepth()
    {
        var input = "{\"value\":" + new string('[', 129);

        Assert.Equal("{}", StreamingJson.ParseObject(input));
    }
}
