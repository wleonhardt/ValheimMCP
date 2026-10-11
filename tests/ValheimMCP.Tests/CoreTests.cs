using System.Collections.Generic;
using System.Diagnostics;
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
        Assert.Equal(413, RequestPolicy.StatusFor(RequestPolicy.TooLarge));
    }

    [Theory]
    [InlineData("LOCALHOST:8731")]
    [InlineData("LocalHost")]
    public void HostNamesAreCaseInsensitive(string host) => Assert.Null(RequestPolicy.Reject(null, host, null, null, 0, "127.0.0.1", ""));

    [Fact] public void EmptyConfigHostDoesNotMatchEmptyHostHeader() => Assert.NotNull(RequestPolicy.Reject(null, "", null, null, 0, "", ""));

    [Fact]
    public void FixedTimeEqualsComparesContent()
    {
        Assert.True(RequestPolicy.FixedTimeEquals("abc", "abc"));
        Assert.False(RequestPolicy.FixedTimeEquals("abc", "abd"));
        Assert.False(RequestPolicy.FixedTimeEquals("abc", "ab"));
        Assert.False(RequestPolicy.FixedTimeEquals(null, "x"));
        Assert.True(RequestPolicy.FixedTimeEquals(null, ""));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.0.2.7", false)]
    [InlineData("+", false)]
    [InlineData("*", false)]
    public void LoopbackBind(string host, bool expected) => Assert.Equal(expected, RequestPolicy.IsLoopbackBind(host));
}

public class CommandPolicyTests
{
    static readonly string[] None = new string[0];

    [Fact] public void EmptyListsAllowEverything() => Assert.True(CommandPolicy.IsAllowed("spawn Boar", None, None, out _));

    [Fact]
    public void DenyWinsOverAllow()
    {
        Assert.False(CommandPolicy.IsAllowed("kick bob", new[] { "kick" }, new[] { "kick" }, out var why));
        Assert.Contains("commands.deny", why);
    }

    [Fact]
    public void NonEmptyAllowIsExclusive()
    {
        var allow = new[] { "pos", "goto" };
        Assert.True(CommandPolicy.IsAllowed("pos", allow, None, out _));
        Assert.False(CommandPolicy.IsAllowed("spawn Boar", allow, None, out var why));
        Assert.Contains("allowlist", why);
    }

    [Theory]
    [InlineData("SPAWN Boar")]
    [InlineData("  spawn\tBoar")]
    [InlineData("spawn\nBoar")]
    public void NameIsFirstTokenAnyWhitespaceAnyCase(string line) => Assert.False(CommandPolicy.IsAllowed(line, None, new[] { "spawn" }, out _));

    [Fact]
    public void TrailingStarIsAPrefixWildcard()
    {
        Assert.False(CommandPolicy.IsAllowed("kickall", None, new[] { "kick*" }, out _));
        Assert.True(CommandPolicy.IsAllowed("pos", None, new[] { "kick*" }, out _));
        Assert.False(CommandPolicy.IsAllowed("anything", None, new[] { "*" }, out _));
    }

    [Fact] public void NullPatternListsAreEmpty() => Assert.True(CommandPolicy.IsAllowed("pos", null, null, out _));
}

public class JsonRpcTests
{
    [Fact]
    public void NegotiateKeepsASupportedVersionAndOffersTheNewestOtherwise()
    {
        Assert.Equal("2025-06-18", JsonRpc.Negotiate("2025-06-18"));
        Assert.Equal("2024-11-05", JsonRpc.Negotiate("2024-11-05"));
        Assert.Equal(JsonRpc.SupportedProtocols[0], JsonRpc.Negotiate("1999-01-01"));
        Assert.Equal(JsonRpc.SupportedProtocols[0], JsonRpc.Negotiate(null));
    }

    [Fact]
    public void ProtocolHeader()
    {
        Assert.True(JsonRpc.HeaderAcceptable(null));
        Assert.True(JsonRpc.HeaderAcceptable(""));
        Assert.True(JsonRpc.HeaderAcceptable("2025-11-25"));
        Assert.False(JsonRpc.HeaderAcceptable("2030-01-01"));
    }

    [Fact]
    public void IdsKeepTheirJsonType()
    {
        Assert.Equal("7", JsonRpc.FormatId(7.0));
        Assert.Equal("\"abc\"", JsonRpc.FormatId("abc"));
        Assert.Equal("null", JsonRpc.FormatId(null));
        Assert.Equal("1.5", JsonRpc.FormatId(1.5));
    }

    [Fact]
    public void EnvelopesAreValidJson()
    {
        var ok = (Dictionary<string, object>)MiniJson.Parse(JsonRpc.Result(3.0, JsonRpc.ToolText("hi \"there\"", false)));
        Assert.Equal(3.0, ok["id"]);
        var res = (Dictionary<string, object>)ok["result"];
        Assert.Equal(false, res["isError"]);
        var err = (Dictionary<string, object>)MiniJson.Parse(JsonRpc.ErrorResponse("x", -32601, "nope"));
        Assert.Equal("x", err["id"]);
        Assert.Equal(-32601.0, ((Dictionary<string, object>)err["error"])["code"]);
        var img = (Dictionary<string, object>)MiniJson.Parse(JsonRpc.ToolImage("QUJD", "image/png", "cap"));
        Assert.Equal(2, ((List<object>)img["content"]).Count);
    }
}

public class SafeRegexTests
{
    [Fact]
    public void CatastrophicPatternTimesOutAsNoMatch()
    {
        var rx = SafeRegex.Create("(a+)+$");
        var sw = Stopwatch.StartNew();
        Assert.False(SafeRegex.IsMatch(rx, new string('a', 60) + "!"));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact] public void OrdinaryPatternMatches() => Assert.True(SafeRegex.IsMatch(SafeRegex.Create("Error:.*Foo"), "[Error:Bar] Foo happened"));
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
    public void DeepNestingIsRefusedNotAStackOverflow()
    {
        Assert.Throws<System.FormatException>(() => MiniJson.Parse(new string('[', 200000)));
        Assert.Throws<System.FormatException>(() => MiniJson.Parse(string.Concat(System.Linq.Enumerable.Repeat("{\"a\":", 5000)) + "1" + new string('}', 5000)));
        // exactly at the limit still parses
        MiniJson.Parse(new string('[', MiniJson.MaxDepth) + new string(']', MiniJson.MaxDepth));
    }

    [Fact] public void TrailingCharactersAreRefused() => Assert.Throws<System.FormatException>(() => MiniJson.Parse("{} x"));
    [Fact] public void NonFiniteNumbersAreRefused() => Assert.Throws<System.FormatException>(() => MiniJson.Parse("[1e999]"));
    [Fact] public void TrailingWhitespaceIsFine() => Assert.NotNull(MiniJson.Parse("{\"a\":1}  \n"));

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

    [Fact]
    public void QuotedValuesKeepTheirHash()
    {
        var y = MiniYaml.Parse("server:\n  token: \"ab #cd\"   # comment\n  other: 'x #y'\n  plain: value # trailing\n  nohash: a#b\n");
        Assert.Equal("ab #cd", y.Get("server.token", ""));
        Assert.Equal("x #y", y.Get("server.other", ""));
        Assert.Equal("value", y.Get("server.plain", ""));
        Assert.Equal("a#b", y.Get("server.nohash", ""));
    }

    [Fact]
    public void BooleansAcceptAnyCaseAndWarnOnGarbage()
    {
        var y = MiniYaml.Parse("tools:\n  write: True\n  terse: maybe\n  off: no\n");
        Assert.True(y.GetBool("tools.write", false));
        Assert.False(y.GetBool("tools.off", true));
        Assert.True(y.GetBool("tools.missing", true));
        Assert.False(y.GetBool("tools.terse", false));
        Assert.Single(y.Warnings);
        Assert.Contains("tools.terse", y.Warnings[0]);
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

public class ToolProfileTests
{
    [Fact] public void AllIsNull() { Assert.Null(ToolProfiles.Categories("all")); Assert.Null(ToolProfiles.Categories(null)); Assert.Null(ToolProfiles.Categories("nonsense")); }
    [Fact] public void ObserveHasNoWrites() { var c = ToolProfiles.Categories("Observe"); Assert.Contains("read", c); Assert.DoesNotContain("build", c); Assert.DoesNotContain("control", c); }
    [Fact] public void BuildAndControlAreDisjointOnWrites() { Assert.Contains("build", ToolProfiles.Categories("build")); Assert.DoesNotContain("control", ToolProfiles.Categories("build")); Assert.Contains("control", ToolProfiles.Categories("control")); }
    [Fact] public void TerseCutsAtFirstSentence() { Assert.Equal("Walk the character with the game's real movement.", ToolProfiles.Terse("Walk the character with the game's real movement. points:[[x,z]] waypoints. More.")); Assert.Equal("short", ToolProfiles.Terse("short")); }
}
