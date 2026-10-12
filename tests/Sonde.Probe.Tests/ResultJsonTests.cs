using System.Text.Json.Nodes;

namespace Sonde.Probe.Tests;

public class ResultJsonTests
{
    [Fact]
    public void SerializesSimpleScalars()
    {
        Assert.Equal(42, ResultJson.Serialize(42)!.GetValue<int>());
        Assert.Equal("hi", ResultJson.Serialize("hi")!.GetValue<string>());
        Assert.True(ResultJson.Serialize(true)!.GetValue<bool>());
        Assert.Null(ResultJson.Serialize(null));
        Assert.Equal("Monday", ResultJson.Serialize(DayOfWeek.Monday)!.ToJsonString().Trim('"').Replace("1", "Monday", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatesLongStrings()
    {
        var huge = new string('x', ResultJson.MaxStringLength + 500);
        var s = ResultJson.Serialize(huge)!.GetValue<string>();
        Assert.True(s.Length < huge.Length);
        Assert.Contains("truncated 500 chars", s, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsLargeCollections()
    {
        var list = Enumerable.Range(0, ResultJson.MaxCollectionItems + 50).ToList();
        var array = ResultJson.Serialize(list)!.AsArray();
        Assert.Equal(ResultJson.MaxCollectionItems + 1, array.Count); // capped items + one marker
        Assert.Contains("truncated", array[^1]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void StopsAtMaxDepth()
    {
        var deep = new Nested { Child = new Nested { Child = new Nested { Child = new Nested() } } };
        var node = ResultJson.Serialize(deep, maxDepth: 2);
        var grandchild = node!["Child"]!["Child"]!;
        Assert.Contains("max-depth", grandchild.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void BreaksReferenceCycles()
    {
        var a = new Nested();
        a.Child = a;
        var node = ResultJson.Serialize(a, maxDepth: 10);
        Assert.Contains("cycle", node!["Child"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackWhenGetterThrows()
    {
        var node = ResultJson.Serialize(new Throws());
        Assert.Contains("error", node!["Bad"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(7, node["Ok"]!.GetValue<int>());
    }

    [Fact]
    public void PassesJsonNodeThroughWithoutReflectingIt()
    {
        JsonNode input = new JsonObject { ["name"] = "world", ["n"] = 3 };
        var node = ResultJson.Serialize(input)!;
        Assert.Equal("world", node["name"]!.GetValue<string>());
        Assert.Equal(3, node["n"]!.GetValue<int>());
        Assert.Null(node["Parent"]);
    }

    [Fact]
    public void SerializesDictionary()
    {
        var dictionary = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var node = ResultJson.Serialize(dictionary)!;
        Assert.Equal(1, node["a"]!.GetValue<int>());
        Assert.Equal(2, node["b"]!.GetValue<int>());
    }

    [Fact]
    public void SerializesAnonymousObjects()
    {
        var node = ResultJson.Serialize(new { Name = "x", Items = new[] { 1, 2 } })!;
        Assert.Equal("x", node["Name"]!.GetValue<string>());
        Assert.Equal(2, node["Items"]!.AsArray().Count);
    }

    private sealed class Nested
    {
        public Nested? Child { get; set; }
    }

    private sealed class Throws
    {
        public string Bad => throw new InvalidOperationException("boom");
        public int Ok => 7;
    }
}
