using System.Collections.Generic;
using HubnerExt;
using ValheimMCP;
using Xunit;

public class RequestPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1:8731")]
    [InlineData("localhost")]
    [InlineData("[::1]:8741")]
    public void LoopbackHostsPass(string host) => Assert.Null(RequestPolicy.Reject(null, host, null, null, 10, "127.0.0.1", ""));

    [Fact] public void OriginRefused() => Assert.StartsWith("cross-origin", RequestPolicy.Reject("http://evil.example", "127.0.0.1", null, null, 0, "127.0.0.1", ""));
    [Fact] public void ForeignHostRefused() => Assert.StartsWith("Host header refused", RequestPolicy.Reject(null, "evil.example:8731", null, null, 0, "127.0.0.1", ""));
    [Fact] public void ConfiguredHostPasses() => Assert.Null(RequestPolicy.Reject(null, "game.lan:8731", null, null, 0, "game.lan", ""));
    [Fact] public void BigBodyRefused() => Assert.StartsWith("request body too large", RequestPolicy.Reject(null, "127.0.0.1", null, null, RequestPolicy.MaxBodyBytes + 1, "127.0.0.1", ""));

    [Fact]
    public void TokenRules()
    {
        Assert.Equal("missing or wrong token", RequestPolicy.Reject(null, "127.0.0.1", null, null, 0, "127.0.0.1", "s3cret"));
        Assert.Equal("missing or wrong token", RequestPolicy.Reject(null, "127.0.0.1", "Bearer nope", null, 0, "127.0.0.1", "s3cret"));
        Assert.Null(RequestPolicy.Reject(null, "127.0.0.1", "Bearer s3cret", null, 0, "127.0.0.1", "s3cret"));
        Assert.Null(RequestPolicy.Reject(null, "127.0.0.1", null, "s3cret", 0, "127.0.0.1", "s3cret"));
        Assert.Equal(401, RequestPolicy.StatusFor("missing or wrong token"));
        Assert.Equal(403, RequestPolicy.StatusFor("Host header refused (not a loopback name): x"));
    }
}

public class MiniJsonTests
{
    [Fact]
    public void RoundTripsTypes()
    {
        var d = (Dictionary<string, object>)MiniJson.Parse("{\"a\":1,\"b\":\"x\\n\\u0041\",\"c\":[1,2.5,true,null],\"d\":{\"e\":false}}");
        Assert.Equal(1.0, d["a"]);
        Assert.Equal("x\nA", d["b"]);
        var c = (List<object>)d["c"]; Assert.Equal(4, c.Count); Assert.Equal(2.5, c[1]); Assert.Equal(true, c[2]); Assert.Null(c[3]);
        Assert.Equal(false, ((Dictionary<string, object>)d["d"])["e"]);
    }

    [Fact] public void RejectsGarbage() => Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\":}"));

    [Fact]
    public void WriterEscapes()
    {
        var s = Json.Str("he said \"hi\"\n\t\\ \u0001");
        Assert.Equal("\"he said \\\"hi\\\"\\n\\t\\\\ \\u0001\"", s);
        Assert.Equal("null", Json.Str(null));
        var back = MiniJson.Parse(Json.Str("round\ntrip \"x\""));
        Assert.Equal("round\ntrip \"x\"", back);
    }
}

public class MiniYamlTests
{
    [Fact]
    public void ReadsSectionsListsAndComments()
    {
        var y = MiniYaml.Parse("server:\n  host: 127.0.0.1   # keep local\n  port: 8731\nhubner:\n  sandboxNames: [Odev, MaRkO]\n  writeFlagMinutes: 30\ncommands:\n  allow:\n    - pos\n    - goto\n");
        Assert.Equal("127.0.0.1", y.Get("server.host", ""));
        Assert.Equal(8731, y.GetInt("server.port", 0));
        Assert.Equal(30, y.GetInt("hubner.writeFlagMinutes", 0));
        Assert.Equal(new List<string> { "Odev", "MaRkO" }, y.GetList("hubner.sandboxNames"));
        Assert.Equal(new List<string> { "pos", "goto" }, y.GetList("commands.allow"));
        Assert.Equal("dflt", y.Get("missing.key", "dflt"));
    }
}

public class FlatJsonTests
{
    [Fact]
    public void ParsesJournalLine()
    {
        var d = FlatJson.Parse("{\"seq\":12,\"t\":\"2026-10-06T10:00:00Z\",\"kind\":\"modify\",\"pos\":[-1.5,40.23,-318],\"rot\":[0,0,0,1],\"text\":\"GREAT\\nHALL\",\"ints\":{\"state\":1},\"floats\":{\"fuel\":1.79},\"strings\":{\"hub_ctag\":\"decor:x\"}}");
        Assert.NotNull(d);
        Assert.Equal(12.0, FlatJson.N(d, "seq"));
        Assert.Equal("modify", FlatJson.S(d, "kind"));
        Assert.Equal(new[] { -1.5f, 40.23f, -318f }, FlatJson.A(d, "pos"));
        Assert.Equal("GREAT\nHALL", FlatJson.S(d, "text"));
        Assert.Equal(1.0, FlatJson.O(d, "ints")["state"]);
        Assert.Equal(1.79, (double)FlatJson.O(d, "floats")["fuel"], 3);
        Assert.Equal("decor:x", FlatJson.O(d, "strings")["hub_ctag"]);
    }

    [Fact] public void UndoneMarker() => Assert.Equal(7.0, FlatJson.N(FlatJson.Parse("{\"undone\":7}"), "undone"));
    [Fact] public void GarbageIsNull() => Assert.Null(FlatJson.Parse("not json"));
}

public class PlanKeyTests
{
    [Fact] public void ExplicitKeyWins() => Assert.Equal("tower:NW:wall:3", PlanKeys.Of("tower:NW:wall:3", "x", 1, 2, 3, 90, false));
    [Fact] public void RoundsPlaneToTenthHeightToHalf() => Assert.Equal("W|-1.5|-318.0|40.5", PlanKeys.Of(null, "W", -1.51, 40.3, -318.04, 0, false));
    [Fact] public void SeatIgnoresHeight() => Assert.Equal("W|1.0|2.0|s", PlanKeys.Of(null, "W", 1, 99, 2, 0, true));
    [Fact] public void YawDistinguishes() { Assert.NotEqual(PlanKeys.Of(null, "W", 1, 2, 3, 0, false), PlanKeys.Of(null, "W", 1, 2, 3, 90, false)); Assert.Equal(PlanKeys.Of(null, "W", 1, 2, 3, 0, false), PlanKeys.Of(null, "W", 1, 2, 3, 360, false)); }
}
