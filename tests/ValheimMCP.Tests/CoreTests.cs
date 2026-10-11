using System.Linq;
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

public class WriteGateTests
{
    const string Flag = "/srv/BepInEx/hubner-ext/ALLOW_SERVER_WRITES";

    static string Check(bool dry, bool honoursDry, bool exists = true, bool fresh = true, string confirm = WriteGate.Confirm)
        => WriteGate.Check(dry, honoursDry, exists, fresh, 30, Flag, confirm);

    [Fact] public void OpenGateWithConfirmPasses() => Assert.Null(Check(false, false));
    [Fact] public void MissingFlagRefuses() => Assert.Contains("server writes are off", Check(false, false, exists: false));
    [Fact] public void ExpiredFlagRefuses() => Assert.Contains("expired", Check(false, false, fresh: false));
    [Fact] public void WrongConfirmRefuses() => Assert.Contains("confirm", Check(false, false, confirm: "yes"));
    [Fact] public void MissingConfirmRefuses() => Assert.Contains("confirm", Check(false, false, confirm: null));

    [Fact]
    public void DryRunSkipsTheGateOnlyWhenTheHandlerHonoursIt()
    {
        Assert.Null(Check(true, true, exists: false));                       // plan_apply dry:true only reports
        // zdo_delete / zdo_set / container_set / doors_set / undo_group ignore 'dry': with the gate closed they must refuse
        Assert.Contains("server writes are off", Check(true, false, exists: false));
        Assert.Contains("confirm", Check(true, false, confirm: null));
    }
}

public class SafeNamesTests
{
    [Theory]
    [InlineData("base-1")]
    [InlineData("castle_x.v2")]
    [InlineData("A")]
    public void FileStemsPass(string n) => Assert.True(SafeNames.IsFileStem(n));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../journal")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("name with space")]
    [InlineData("a..b")]
    public void PathsAndOddNamesAreRefused(string n) => Assert.False(SafeNames.IsFileStem(n));

    [Fact] public void LongNamesAreRefused() => Assert.False(SafeNames.IsFileStem(new string('a', 65)));
}

public class WorldBoundsTests
{
    [Fact] public void InsideAndOutside()
    {
        Assert.True(WorldBounds.InWorld(0, 30, 0));
        Assert.True(WorldBounds.InWorld(-10500, 0, 10500));
        Assert.False(WorldBounds.InWorld(1e9, 0, 0));
        Assert.False(WorldBounds.InWorld(0, 0, -1e9));
        Assert.False(WorldBounds.InWorld(0, 6000, 0));
    }

    [Fact] public void NonFiniteIsOutside()
    {
        Assert.False(WorldBounds.InWorld(double.NaN, 0, 0));
        Assert.False(WorldBounds.InWorld(0, double.PositiveInfinity, 0));
        Assert.False(WorldBounds.InWorld(0, 0, double.NegativeInfinity));
    }

    [Fact] public void ClampKeepsABoxInsideTheWorld()
    {
        Assert.Equal(WorldBounds.Limit, WorldBounds.Clamp(1e9f));
        Assert.Equal(-WorldBounds.Limit, WorldBounds.Clamp(-1e9f));
        Assert.Equal(123f, WorldBounds.Clamp(123f));
        Assert.Equal(0f, WorldBounds.Clamp(float.NaN));
    }
}

public class IdentityTests
{
    static ObjFacts O(string id, string prefab, double x, double z, string plan = "", string key = "") => new ObjFacts { Id = id, Prefab = prefab, X = x, Y = 40, Z = z, Plan = plan, Key = key };
    static Match Find(string entryBoot, string boot, string id, string prefab, string plan, string key, double x, double z, bool posReliable, out ObjFacts f, params ObjFacts[] world)
        => Identity.Find(entryBoot, boot, id, prefab, plan, key, x, 40, z, posReliable, world, out f);

    [Fact]
    public void SameLoadFindsByIdExactly()
    {
        Assert.Equal(Match.Found, Find("77", "77", "5:77", "woodwall", "", "", 1, 1, true, out var f, O("5:77", "woodwall", 1, 1)));
        Assert.Equal("5:77", f.Id);
    }

    [Fact]
    public void SameLoadIdNamingSomethingElseIsNotAMatch()
        => Assert.Equal(Match.NotFound, Find("77", "77", "5:77", "woodwall", "", "", 1, 1, true, out _, O("5:77", "stone_wall", 1, 1)));

    [Fact]
    public void AfterARestartTheIdIsIgnoredAndThePlanTagDecides()
    {
        // the entry says id 9:123 (an old session user), but after the load the object is 1:4711
        Assert.Equal(Match.Found, Find("77", "99", "9:123", "woodwall", "castle", "n:1", 5, 5, false, out var f,
            O("1:4711", "woodwall", 5, 5, "castle", "n:1"), O("9:123", "stone_wall", 0, 0, "castle", "other"), O("1:5", "woodwall", 5, 5, "castle", "n:2")));
        Assert.Equal("1:4711", f.Id);
    }

    [Fact]
    public void AfterARestartAnOldIdThatNowNamesAnotherObjectIsNeverUsed()
        => Assert.Equal(Match.NotFound, Find("77", "99", "1:4711", "woodwall", "castle", "n:1", 5, 5, false, out _, O("1:4711", "woodwall", 50, 50, "castle", "n:9")));

    [Fact]
    public void DuplicatePlanKeysPickTheNearestOnlyWhenItIsClearlyNearest()
    {
        Assert.Equal(Match.Found, Find("", "99", "x", "woodwall", "p", "k", 5, 5, false, out var f, O("1:1", "woodwall", 5.1, 5, "p", "k"), O("1:2", "woodwall", 9, 9, "p", "k")));
        Assert.Equal("1:1", f.Id);
        Assert.Equal(Match.Ambiguous, Find("", "99", "x", "woodwall", "p", "k", 5, 5, false, out _, O("1:1", "woodwall", 6, 5, "p", "k"), O("1:2", "woodwall", 4, 5, "p", "k")));      // a tie
        Assert.Equal(Match.Ambiguous, Find("", "99", "x", "woodwall", "p", "k", 5, 5, false, out _, O("1:1", "woodwall", 30, 5, "p", "k"), O("1:2", "woodwall", 40, 5, "p", "k")));     // both far
    }

    [Fact]
    public void AnUntaggedSpawnIsFoundByPrefabAndPosition()
    {
        Assert.Equal(Match.Found, Find("77", "99", "9:1", "chair", "", "", 10, 10, true, out var f, O("1:8", "chair", 10.01, 10), O("1:9", "chair", 12, 10), O("1:10", "table", 10, 10)));
        Assert.Equal("1:8", f.Id);
    }

    [Fact]
    public void TwoIdenticalObjectsAtOneSpotAreNotGuessed()
        => Assert.Equal(Match.Ambiguous, Find("77", "99", "9:1", "chair", "", "", 10, 10, true, out _, O("1:8", "chair", 10, 10), O("1:9", "chair", 10.02, 10)));

    [Fact]
    public void AnEditedUntaggedObjectHasNothingToGoBy()
        => Assert.Equal(Match.NotFound, Find("77", "99", "9:1", "chair", "", "", 10, 10, false, out _, O("1:8", "chair", 10, 10)));       // a modify entry holds the OLD position

    [Fact]
    public void EntriesWithoutABootCountAsOldRuns()
        => Assert.Equal(Match.Found, Find("", "99", "5:77", "woodwall", "p", "k", 0, 0, false, out _, O("1:3", "woodwall", 80, 80, "p", "k")));

    [Fact] public void NoCandidatesIsNotFound() => Assert.Equal(Match.NotFound, Find("77", "77", "5:77", "woodwall", "", "", 1, 1, true, out _));
}

public class StallLadderTests
{
    static StallAction Pick(int spot = 1, int total = 1, float rise = float.NaN, float slope = 0, bool up = false, bool water = false, bool hazard = false, float stam = 1f, bool running = false, int side = 0, int rer = 0)
        => StallLadder.Pick(spot, total, rise, slope, up, water, hazard, stam, running, side, rer);

    [Fact] public void ProbeMeasuredAHoppableTopSoHop() => Assert.Equal(StallAction.Hop, Pick(rise: 0.8f));
    [Fact] public void TooLowOrTooHighATopIsNotHopped() { Assert.NotEqual(StallAction.Hop, Pick(rise: 0.2f)); Assert.NotEqual(StallAction.Hop, Pick(rise: 1.6f)); }
    [Fact] public void NoProbeNoBlindJump() => Assert.NotEqual(StallAction.Hop, Pick());
    [Fact] public void NeverHopInWaterOrTowardAHazard() { Assert.NotEqual(StallAction.Hop, Pick(rise: 0.8f, water: true)); Assert.NotEqual(StallAction.Hop, Pick(rise: 0.8f, hazard: true)); }
    [Fact] public void ASteepBankThatTheRouteClimbsIsRunUp() => Assert.Equal(StallAction.RunUp, Pick(slope: 50, up: true));
    [Fact] public void AWallSteeperThanSixtyIsNotRunUp() => Assert.NotEqual(StallAction.RunUp, Pick(slope: 70, up: true));
    [Fact] public void NoRunUpWhenAlreadyRunningOrTired() { Assert.NotEqual(StallAction.RunUp, Pick(slope: 50, up: true, running: true)); Assert.NotEqual(StallAction.RunUp, Pick(slope: 50, up: true, stam: 0.4f)); }
    [Fact] public void TiredBodyRests() => Assert.Equal(StallAction.Rest, Pick(stam: 0.2f));
    [Fact] public void ThenSideStepThenRerouteThenGiveUp()
    {
        Assert.Equal(StallAction.SideStep, Pick(side: 0));
        Assert.Equal(StallAction.SideStep, Pick(side: 1));
        Assert.Equal(StallAction.Reroute, Pick(side: 2, rer: 0));
        Assert.Equal(StallAction.Reroute, Pick(side: 2, rer: 1));
        Assert.Equal(StallAction.GiveUp, Pick(side: 2, rer: 2));
    }
    [Fact] public void FourStallsAtOneSpotOrFiveInTotalEndTheWalk() { Assert.Equal(StallAction.GiveUp, Pick(spot: 4, rise: 0.8f)); Assert.Equal(StallAction.GiveUp, Pick(total: 5)); }
    [Fact] public void ProgressNeedsBothTheRouteAndTheFeetToMove() { Assert.True(StallLadder.Progressed(0.3f, 0.3f)); Assert.False(StallLadder.Progressed(0.0f, 0.5f)); Assert.False(StallLadder.Progressed(0.5f, 0.0f)); }
}

public class DoorRulesTests
{
    [Fact] public void WardOrMissingKeyLocksADoor() { Assert.True(DoorRules.Locked(true, false, false)); Assert.True(DoorRules.Locked(false, true, false)); Assert.False(DoorRules.Locked(false, true, true)); Assert.False(DoorRules.Locked(false, false, false)); }

    [Fact]
    public void OnlyDoorsOnTheLineAheadCount()
    {
        Assert.True(DoorRules.OnLineAhead(0, 0, 1, 0, 2.0, 0.3));
        Assert.False(DoorRules.OnLineAhead(0, 0, 1, 0, 3.5, 0));       // too far ahead
        Assert.False(DoorRules.OnLineAhead(0, 0, 1, 0, -1, 0));        // behind
        Assert.False(DoorRules.OnLineAhead(0, 0, 1, 0, 1.5, 2.0));     // beside the line
        Assert.False(DoorRules.OnLineAhead(0, 0, 0, 0, 1, 0));         // not walking
    }

    [Fact]
    public void DecisionOrder()
    {
        Assert.Equal(DoorDecision.SkipLocked, DoorRules.Decide(true, true, true, false, 10));
        Assert.Equal(DoorDecision.SkipOffRoute, DoorRules.Decide(false, true, false, false, 10));
        Assert.Equal(DoorDecision.SkipUnneeded, DoorRules.Decide(false, true, true, true, 10));
        Assert.Equal(DoorDecision.SkipNotReady, DoorRules.Decide(false, false, true, false, 10));
        Assert.Equal(DoorDecision.SkipHeld, DoorRules.Decide(false, true, true, false, 0.5));
        Assert.Equal(DoorDecision.Press, DoorRules.Decide(false, true, true, false, 5));
    }
}

public class UserOverrideTests
{
    [Fact]
    public void TheUserHasTheControlsUntilTheyStopTouchingForOneAndAHalfSeconds()
    {
        var u = new UserOverride();
        Assert.False(u.Active(10));
        u.Touch(10);
        Assert.True(u.Active(11.4)); Assert.False(u.Active(11.6));
    }

    [Fact]
    public void WalkerClocksDoNotRunWhileTheUserDrives()
    {
        var u = new UserOverride();
        Assert.Equal(0, u.Yielded(100));                // first call
        u.Touch(100.1);
        Assert.Equal(0.6, u.Yielded(100.6), 6);         // the user is active: the 0.6 s since the last call are added to the walker's clocks
        Assert.Equal(0.5, u.Yielded(101.1), 6);
        Assert.Equal(0.0, u.Yielded(103.0), 6);         // the user stopped long ago: clocks run again
    }
}

public class PursuitTests
{
    static List<double[]> L(params double[][] p) => p.ToList();

    [Fact] public void LookAheadGrowsWithSpeedAndIsClamped() { Assert.Equal(1.0, Pursuit.LookAhead(0.1), 6); Assert.Equal(1.8, Pursuit.LookAhead(3), 6); Assert.Equal(2.4, Pursuit.LookAhead(20), 6); }

    [Fact]
    public void SteersAtAPointAheadOnTheLineNotAtTheWaypoint()
    {
        var line = L(new[] { 0.0, 0 }, new[] { 10.0, 0 });
        int seg = 0; double t = 0;
        Pursuit.Aim(line, ref seg, ref t, 2, 0.4, 2.0, out var ax, out var az);
        Assert.Equal(4.0, ax, 6); Assert.Equal(0.0, az, 6);
    }

    [Fact]
    public void RoundsACornerAlongTheLine()
    {
        var line = L(new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 10.0, 10 });
        int seg = 0; double t = 0;
        Pursuit.Aim(line, ref seg, ref t, 9.0, 0, 3.0, out var ax, out var az);
        Assert.Equal(10.0, ax, 6); Assert.Equal(2.0, az, 6);              // 1 m to the corner, then 2 m up the next leg
    }

    [Fact]
    public void ProgressNeverMovesBackward()
    {
        var line = L(new[] { 0.0, 0 }, new[] { 10.0, 0 });
        int seg = 0; double t = 0;
        Pursuit.Aim(line, ref seg, ref t, 6, 0, 1.0, out _, out _);
        var before = t;
        Pursuit.Aim(line, ref seg, ref t, 3, 0, 1.0, out var ax, out _);       // pushed back by a stall
        Assert.True(t >= before); Assert.Equal(7.0, ax, 6);
    }

    [Fact]
    public void NearTheEndItAimsAtTheEnd()
    {
        var line = L(new[] { 0.0, 0 }, new[] { 10.0, 0 });
        int seg = 0; double t = 0;
        Pursuit.Aim(line, ref seg, ref t, 9.5, 0, 2.4, out var ax, out var az);
        Assert.Equal(10.0, ax, 6); Assert.Equal(0.0, az, 6);
    }
}

public class WalkGuardTests
{
    [Fact] public void AJumpOfThirtyMetresIsATeleport() { Assert.True(TeleportGuard.Jumped(30, 0, 0)); Assert.True(TeleportGuard.Jumped(22, 22, 0)); Assert.False(TeleportGuard.Jumped(5, 1, 5)); }

    [Fact]
    public void RunDecisions()
    {
        Assert.True(RunRules.WantRun(false, 30, 0.9, 0, false, false));          // far and fresh
        Assert.False(RunRules.WantRun(false, 10, 0.9, 0, false, false));         // not far enough to start
        Assert.True(RunRules.WantRun(true, 10, 0.9, 0, false, false));           // hold it down to 8 m
        Assert.False(RunRules.WantRun(true, 6, 0.9, 0, false, false));
        Assert.False(RunRules.WantRun(true, 30, 0.4, 0, false, false));          // tired
        Assert.True(RunRules.WantRun(false, 3, 0.9, 50, true, false));           // a steep bank the route climbs is always run
        Assert.False(RunRules.WantRun(false, 3, 0.4, 50, true, false));          // ... unless there is no stamina
        Assert.True(RunRules.WantRun(false, 3, 0.9, 0, false, true));            // the caller asked for run
    }

    [Fact] public void CapsuleNumbersAreThePlayersNotAGuess() { Assert.Equal(0.49f, WalkFacts.CapsuleRadius); Assert.Equal(1.85f, WalkFacts.CapsuleHeight); Assert.Equal(0.45f, WalkFacts.StepUp); Assert.Equal(38f, WalkFacts.SlideDeg); }
}
