using System;
using System.Collections.Generic;
using System.Linq;
using HubnerExt;
using Xunit;

public class ObbTests
{
    static Obb Box(double x, double y, double z, double hx, double hy, double hz, double yaw = 0) => Obb.Make(new V3(x, y, z), new V3(hx, hy, hz), Quat.Yaw(yaw));

    [Fact] public void OverlappingAxisAlignedBoxesReportTheSmallestOverlap() => Assert.Equal(0.5, Obb.Depth(Box(0, 0, 0, 1, 1, 1), Box(1.5, 0, 0, 1, 1, 1)), 6);
    [Fact] public void TouchingBoxesHaveZeroDepth() => Assert.Equal(0.0, Obb.Depth(Box(0, 0, 0, 1, 1, 1), Box(2, 0, 0, 1, 1, 1)), 6);
    [Fact] public void ApartBoxesReportMinusTheGap() => Assert.Equal(-1.0, Obb.Depth(Box(0, 0, 0, 1, 1, 1), Box(3, 0, 0, 1, 1, 1)), 6);
    [Fact] public void ARotatedSquareReachesFurtherAlongTheDiagonal()
    {
        // a 2x2 square turned 45 degrees has a corner 1.414 from its centre
        Assert.True(Obb.Overlaps(Box(0, 0, 0, 1, 1, 1, 45), Box(2.3, 0, 0, 1, 1, 1)));
        Assert.False(Obb.Overlaps(Box(0, 0, 0, 1, 1, 1), Box(2.3, 0, 0, 1, 1, 1)));
    }
    [Fact] public void ClosestPointIsOnTheSurface()
    {
        var p = Box(0, 0, 0, 1, 1, 1).ClosestPoint(new V3(5, 0.5, 0));
        Assert.Equal(1.0, p.X, 6); Assert.Equal(0.5, p.Y, 6);
        var inside = Box(0, 0, 0, 1, 1, 1).ClosestPoint(new V3(0.2, 0.3, 0.4));
        Assert.Equal(0.2, inside.X, 6);
    }
    [Fact] public void EulerMatchesUnitysOrder()
    {
        var v = Quat.Euler(90, 0, 0).Rotate(new V3(0, 0, 1));            // forward tipped down by 90 degrees about x: +z -> -y
        Assert.Equal(-1.0, v.Y, 6); Assert.Equal(0.0, v.Z, 6);
        var w = Quat.Euler(0, 90, 0).Rotate(new V3(0, 0, 1));            // yaw 90: +z -> +x
        Assert.Equal(1.0, w.X, 6);
    }
    [Fact] public void GrownBoxesTouchAcrossASmallGap() => Assert.True(Obb.Overlaps(Box(0, 0, 0, 1, 1, 1).Grown(0.15), Box(2.1, 0, 0, 1, 1, 1)));
}

public class SupportModelTests
{
    static PieceGeo Beam(string material, double halfHeight = 1.0, bool supports = true) => new PieceGeo { Prefab = material + "_beam", Material = material, Supports = supports, ComOffset = new V3(0, halfHeight, 0), Colliders = { new ColliderBox { Centre = new V3(0, halfHeight, 0), Half = new V3(0.2, halfHeight, 0.2) } } };
    static PlacedPiece At(PieceGeo g, double x, double y, double z, string id) => new PlacedPiece { Id = id, Geo = g, Pos = new V3(x, y, z) };
    static readonly Func<double, double, double> Flat = (x, z) => 0.0;

    [Fact]
    public void APieceOnTheGroundHasFullSupport()
    {
        var r = SupportModel.Run(new[] { At(Beam("Wood"), 0, -0.05, 0, "a"), At(Beam("Stone"), 5, 0, 0, "b") }, Flat, null);
        Assert.Equal(100, r.Values.First().Support, 6); Assert.Equal(1000, r.Values.Last().Support, 6);
        Assert.All(r.Values, v => Assert.True(v.Grounded));
    }

    [Fact]
    public void APieceAboveTheGroundReachWithoutAContactHasNoSupport()
    {
        var p = At(Beam("Wood"), 0, 0.5, 0, "a");             // bottom 0.5 above ground, beyond the 0.15 contact reach
        var r = SupportModel.Run(new[] { p }, Flat, null);
        Assert.False(r[p].Grounded); Assert.Equal(0, r[p].Support, 6); Assert.False(r[p].Holds);
    }

    [Fact]
    public void AStackOfWoodLosesSupportWithHeightAndFailsPastSixteenMetres()
    {
        // 2 m posts on top of each other: the game's own rule gives 100, 73.75, 54.4, 40.1 ... 11.9 and then 8.8, under the minimum of 10: the eighth post (top at 16 m) holds, the ninth does not
        var g = Beam("Wood");
        var stack = Enumerable.Range(0, 10).Select(i => At(g, 0, i * 2.0 - 0.05, 0, "p" + i)).ToList();
        var r = SupportModel.Run(stack, Flat, null);
        Assert.Equal(100, r[stack[0]].Support, 3);
        Assert.Equal(73.75, r[stack[1]].Support, 1);
        Assert.Equal(54.39, r[stack[2]].Support, 1);
        Assert.True(r[stack[7]].Holds, "the post whose top is at 16 m holds");
        Assert.False(r[stack[8]].Holds, "the next one up does not");
        Assert.Equal(8, stack.Count(p => r[p].Holds));
    }

    [Fact]
    public void AColumnOfStoneHoldsAsManyPostsAsWoodBecauseTheRatioIsTheSameButItCannotReachSideways()
    {
        var column = Enumerable.Range(0, 12).Select(i => At(Beam("Stone"), 0, i * 2.0 - 0.05, 0, "s" + i)).ToList();
        var rc = SupportModel.Run(column, Flat, null);
        Assert.Equal(8, column.Count(p => rc[p].Holds));              // max 1000 / min 100 is the same ratio as wood's 100 / 10
        PlacedPiece[] Chain(string mat) { var g = new PieceGeo { Prefab = mat, Material = mat, Supports = true, Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(1.0, 0.1, 0.2) } } }; return Enumerable.Range(0, 3).Select(i => At(g, i * 2.0, 3.1, 0, mat + i)).ToArray(); }
        Func<double, double, double> ledge = (x, z) => x < 0.5 ? 3.0 : -50.0;
        var stone = Chain("Stone"); var wood = Chain("Wood");
        var rs = SupportModel.Run(stone, ledge, null); var rw = SupportModel.Run(wood, ledge, null);
        Assert.False(rs[stone[1]].Holds && rs[stone[2]].Holds);       // horizontal loss 1.0: stone does not span sideways
        Assert.True(rw[wood[1]].Holds);
    }

    [Fact]
    public void ASidewaysChainDecaysFasterForWoodThanForIron()
    {
        PlacedPiece[] Chain(string mat)
        {
            var g = new PieceGeo { Prefab = mat, Material = mat, Supports = true, Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(1.0, 0.1, 0.2) } } };
            return Enumerable.Range(0, 6).Select(i => At(g, i * 2.0, 0.1 + 3, 0, mat + i)).ToArray();
        }
        var wood = Chain("Wood"); var iron = Chain("Iron");
        // the first one rests on a pillar of its own material
        Func<double, double, double> ground = (x, z) => 3.0;       // a ledge at 3 m: only the first beam (x=0) is over it, so make the ground end
        Func<double, double, double> ledge = (x, z) => x < 0.5 ? 3.0 : -50.0;
        var rw = SupportModel.Run(wood, ledge, null); var ri = SupportModel.Run(iron, ledge, null);
        Assert.True(rw[wood[0]].Grounded); Assert.False(rw[wood[1]].Grounded);
        Assert.True(rw[wood[3]].Support < ri[iron[3]].Support);
        Assert.True(rw[wood[1]].Support > rw[wood[2]].Support && rw[wood[2]].Support > rw[wood[3]].Support);
    }

    [Fact]
    public void ARockGivesFullSupportToWhatTouchesIt()
    {
        var p = At(Beam("Wood"), 0, 5, 0, "a");
        var rock = Obb.Make(new V3(0, 4.9, 0), new V3(1, 0.2, 1), Quat.Identity);
        Assert.True(SupportModel.Run(new[] { p }, null, new[] { rock })[p].Grounded);
    }

    [Fact] public void NonWearNTearThingsTakePartInNothing() { var deco = new PlacedPiece { Id = "d", Geo = new PieceGeo { Prefab = "chair", Material = "" }, Pos = new V3(0, 0, 0) }; Assert.Empty(SupportModel.Run(new[] { deco }, Flat, null)); }
    [Fact] public void ThereIsATableForEveryMaterial() { foreach (var m in new[] { "Wood", "HardWood", "Stone", "Iron", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood" }) Assert.NotNull(Material.Of(m)); Assert.Null(Material.Of("Nothing")); }
}

public class PlanChecksTests
{
    static PieceGeo Box(string name, double hx, double hy, double hz, bool noClip = false) => new PieceGeo { Prefab = name, NoClipping = noClip, Colliders = { new ColliderBox { Centre = new V3(0, hy, 0), Half = new V3(hx, hy, hz) } } };
    static PlacedPiece At(PieceGeo g, double x, double y, double z, string id, bool planned = true) => new PlacedPiece { Id = id, Geo = g, Pos = new V3(x, y, z), Planned = planned };

    [Fact] public void TwoPiecesSharingAFaceDoNotOverlap() => Assert.Empty(PlanChecks.Overlaps(new[] { At(Box("wall", 1, 1, 0.1), 0, 0, 0, "a"), At(Box("wall", 1, 1, 0.1), 2, 0, 0, "b") }));
    [Fact] public void CrossingBuildingPiecesAreWarned() { var p = PlanChecks.Overlaps(new[] { At(Box("wall", 1, 1, 0.1), 0, 0, 0, "a"), At(Box("wall", 1, 1, 0.1), 1.5, 0, 0, "b") }); Assert.Single(p); Assert.Equal("warning", p[0].Severity); }
    [Fact] public void ACrossingPieceThatCannotClipIsAnError() { var p = PlanChecks.Overlaps(new[] { At(Box("wall", 1, 1, 0.1), 0, 0, 0, "a"), At(Box("table", 0.5, 0.4, 0.5, true), 0.5, 0, 0, "b") }); Assert.Equal("error", p.Single().Severity); }
    [Fact] public void PiecesAlreadyInTheWorldAreNotCheckedAgainstEachOther() => Assert.Empty(PlanChecks.Overlaps(new[] { At(Box("wall", 1, 1, 0.1), 0, 0, 0, "a", false), At(Box("wall", 1, 1, 0.1), 1.5, 0, 0, "b", false) }));
    [Fact] public void ASmallGraceIsAllowed() => Assert.Empty(PlanChecks.Overlaps(new[] { At(Box("wall", 1, 1, 0.1), 0, 0, 0, "a"), At(Box("wall", 1, 1, 0.1), 1.97, 0, 0, "b") }));

    [Fact]
    public void TiltWaterAndGrid()
    {
        var g = Box("x", 1, 1, 1); g.NotOnTilting = true; g.NoInWater = true;
        var p = At(g, 0, 0, 0, "a");
        Assert.NotNull(PlanChecks.Tilt(p, 0.7)); Assert.Null(PlanChecks.Tilt(p, 0.9));
        Assert.NotNull(PlanChecks.Water(p, 29.0)); Assert.Null(PlanChecks.Water(p, 31.0));
        Assert.Null(PlanChecks.Grid(p, 45)); Assert.Null(PlanChecks.Grid(p, 22.5 * 5 + 0.2)); Assert.NotNull(PlanChecks.Grid(p, 30)); Assert.Null(PlanChecks.Grid(p, -45));
    }

    [Fact]
    public void AFireWantsAMetreAllRoundAsAWarning()
    {
        var fire = Box("fire_pit", 0.5, 0.3, 0.5); fire.IsFire = true;
        var wall = Box("stone_wall", 1, 1, 0.1); wall.IsWall = true;
        var chest = Box("chest", 0.5, 0.4, 0.4);
        var near = PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(wall, 0, 0, 1.0, "w") });                      // gap 0.4 m
        Assert.Equal("warning", near.Single().Severity);                                                                       // our preference, not a game rule
        var soft = PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(chest, 1.5, 0, 0, "c") });                      // gap 0.5 m, a chest: warning
        Assert.Equal("warning", soft.Single().Severity);
        Assert.Empty(PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(chest, 3, 0, 0, "c") }));
    }
}

public class PlanRuleBookTests
{
    static PieceGeo Slab(string name, double hx, double hy, double hz, double y, Action<PieceGeo> more = null)
    {
        var g = new PieceGeo { Prefab = name, Colliders = { new ColliderBox { Centre = new V3(0, y, 0), Half = new V3(hx, hy, hz), Layer = "piece" } } };
        if (more != null) more(g);
        return g;
    }
    static PlacedPiece At(PieceGeo g, double x, double y, double z, string id) => new PlacedPiece { Id = id, Geo = g, Pos = new V3(x, y, z) };

    [Fact] public void ATasteRuleIsNeverAnError()
    {
        var p = PlanRuleBook.Stamp(new PlanProblem { Rule = "fire-clearance", Severity = "error" });
        Assert.Equal("warning", p.Severity); Assert.Equal("taste", p.Origin);
    }
    [Fact] public void EveryRuleHasAnOriginAndASource()
    {
        Assert.All(PlanRuleBook.Rules, r => { Assert.Contains(r.Origin, new[] { "hard", "ghost", "taste" }); Assert.False(string.IsNullOrWhiteSpace(r.Source)); });
        Assert.Equal(PlanRuleBook.Rules.Count, PlanRuleBook.Rules.Select(r => r.Id).Distinct().Count());
    }
    [Fact] public void TheCodesAreTheGamesOwnStatusNames()
    {
        Assert.Equal("MoreSpace", PlanRuleBook.Get("extension-spacing").Code);
        Assert.Equal("ExtensionMissingStation", PlanRuleBook.Get("extension-no-station").Code);
        Assert.Equal("NoBuildZone", PlanRuleBook.Get("no-build-zone").Code);
        Assert.Equal("PrivateZone", PlanRuleBook.Get("private-zone").Code);
    }

    [Fact] public void ANoClippingPieceIsAnErrorOnlyPastTheGamesTwoTenths()
    {
        var a = Slab("a", 1, 1, 1, 0, g => g.NoClipping = true); var b = Slab("b", 1, 1, 1, 0);
        var small = PlanChecks.Overlaps(new[] { At(a, 0, 0, 0, "a"), At(b, 1.9, 0, 0, "b") });      // 0.1 m deep
        Assert.Single(small); Assert.Equal("warning", small[0].Severity);
        var deep = PlanChecks.Overlaps(new[] { At(a, 0, 0, 0, "a"), At(b, 1.5, 0, 0, "b") });       // 0.5 m deep
        Assert.Single(deep); Assert.Equal("error", deep[0].Severity); Assert.Equal("ghost", deep[0].Origin);
    }
    [Fact] public void ARoundingErrorIsNotReported() => Assert.Empty(PlanChecks.Overlaps(new[] { At(Slab("a", 1, 1, 1, 0), 0, 0, 0, "a"), At(Slab("b", 1, 1, 1, 0), 1.97, 0, 0, "b") }));

    [Theory]
    [InlineData("Wood", 1.667)] [InlineData("HardWood", 1.5)] [InlineData("Iron", 1.182)] [InlineData("Ashstone", 3.0)] [InlineData("Stone", 1.333)] [InlineData("Marble", 1.333)]
    public void TheWeakRatioIsWhatOneMoreModuleNeeds(string material, double ratio) => Assert.Equal(ratio, Material.Of(material).WeakRatio, 2);

    [Fact] public void ARockUnderAPieceGivesItFullSupport()
    {
        var post = new PieceGeo { Prefab = "post", Material = "Wood", Supports = true, Colliders = { new ColliderBox { Centre = new V3(0, 1, 0), Half = new V3(0.2, 1, 0.2), Layer = "piece" } } };
        var rock = new PieceGeo { Prefab = "rock", Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(1, 0.5, 1), Layer = "static_solid" } } };      // no WearNTear
        var p = At(post, 0, 4, 0, "p"); var r = At(rock, 0, 3.5, 0, "r");        // the post stands on a rock four metres up, away from the ground
        var res = SupportModel.Run(new[] { p, r }, (x, z) => 0.0, null);
        Assert.True(res[p].Grounded); Assert.Equal(100, res[p].Support, 6);
    }
    [Fact] public void AColliderOnANonSupportLayerCarriesNothing()
    {
        var post = new PieceGeo { Prefab = "post", Material = "Wood", Supports = true, Colliders = { new ColliderBox { Centre = new V3(0, 1, 0), Half = new V3(0.2, 1, 0.2), Layer = "piece" } } };
        var sapling = new PieceGeo { Prefab = "sapling", Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(1, 0.5, 1), Layer = "piece_nonsolid" } } };
        var p = At(post, 0, 4, 0, "p");
        Assert.False(SupportModel.Run(new[] { p, At(sapling, 0, 3.5, 0, "s") }, (x, z) => 0.0, null)[p].Grounded);
    }

    [Fact] public void AFireWithABeamHalfAMetreAboveItIsBlocked()
    {
        var fire = Slab("brazier", 0.3, 0.3, 0.3, 0.3, g => { g.IsFireplace = true; g.CoverCheckOffset = 0.5; });
        var beam = Slab("beam", 1, 0.1, 0.1, 0, null);
        var blocked = PlanChecks.FireBlocked(new[] { At(fire, 0, 0, 0, "f"), At(beam, 0, 0.9, 0, "b") }, null);        // beam bottom 0.8 m: inside the 0.5 m ray that starts at 0.5
        Assert.Single(blocked); Assert.Equal("hard", blocked[0].Origin); Assert.Equal("error", blocked[0].Severity);
        Assert.Empty(PlanChecks.FireBlocked(new[] { At(fire, 0, 0, 0, "f"), At(beam, 0, 1.6, 0, "b") }, null));        // beam bottom 1.5: out of reach
    }
    [Fact] public void AFireUnderTheTerrainIsBlocked() => Assert.Single(PlanChecks.FireBlocked(new[] { At(Slab("fire", 0.3, 0.3, 0.3, 0.3, g => g.IsFireplace = true), 0, 0, 0, "f") }, (x, z) => 0.5));
    [Fact] public void AFireWithTheCoverCheckOffIsNeverBlocked() => Assert.Empty(PlanChecks.FireBlocked(new[] { At(Slab("fire", 0.3, 0.3, 0.3, 0.3, g => { g.IsFireplace = true; g.DisableCoverCheck = true; }), 0, 0, 0, "f") }, (x, z) => 5));
    [Fact] public void ARayThatStartsInsideABoxDoesNotHitIt() => Assert.False(Obb.Make(new V3(0, 0, 0), new V3(1, 1, 1), Quat.Identity).RayHits(new V3(0, 0, 0), new V3(0, 1, 0), 5));

    [Fact] public void ACauldronOnAHearthIsNotInTheHearthsWay()
    {
        var hearth = Slab("hearth", 1, 0.3, 1, 0.3, g => { g.IsFire = true; g.IsFireplace = true; });
        var cauldron = Slab("cauldron", 0.5, 0.4, 0.5, 0.4, g => { g.IsStation = true; });
        var res = PlanChecks.FireClearance(new[] { At(hearth, 0, 0, 0, "h"), At(cauldron, 0, 0.6, 0, "c") });      // the cauldron stands on the hearth's top
        Assert.Empty(res);
        Assert.Single(PlanChecks.FireClearance(new[] { At(hearth, 0, 0, 0, "h"), At(cauldron, 1.7, 0, 0, "c") }));  // beside it, 0.2 m apart: a warning
        Assert.All(PlanChecks.FireClearance(new[] { At(hearth, 0, 0, 0, "h"), At(cauldron, 1.7, 0, 0, "c") }), p => Assert.Equal("warning", p.Severity));
    }

    static PieceGeo Station(string name) => Slab(name, 0.5, 0.5, 0.5, 0.5, g => { g.ProvidesStation = "$piece_workbench"; g.StationRangeBuild = 20; g.StationExtraRange = 4; });
    static PieceGeo Extension(double max = 5, double space = 2) => Slab("ext", 0.3, 0.3, 0.3, 0.3, g => { g.ExtensionOf = "$piece_workbench"; g.ExtensionMaxDistance = max; g.ExtensionSpace = space; });

    [Fact] public void AnExtensionWithoutAStationInRangeDoesNothing()
    {
        var far = PlanChecks.Extensions(new[] { At(Station("wb"), 0, 0, 0, "s"), At(Extension(), 6, 0, 0, "e") });
        Assert.Single(far); Assert.Equal("extension-no-station", far[0].Rule); Assert.Equal("error", far[0].Severity);
        Assert.Empty(PlanChecks.Extensions(new[] { At(Station("wb"), 0, 0, 0, "s"), At(Extension(), 4, 0, 0, "e") }));
    }
    [Fact] public void TwoExtensionsTooCloseAreReportedOnce()
    {
        var res = PlanChecks.Extensions(new[] { At(Station("wb"), 0, 0, 0, "s"), At(Extension(), 3, 0, 0, "e1"), At(Extension(), 3.5, 0, 0, "e2") });
        Assert.Single(res, p => p.Rule == "extension-spacing");
    }
    [Fact] public void TheBuildRangeGrowsWithLinkedExtensionsMeasuredFlat()
    {
        var s = At(Station("wb"), 0, 0, 0, "s");
        var e1 = At(Extension(), 3, 0, 0, "e1"); var e2 = At(Extension(), 0, 0, 3, "e2");            // same prefab name "ext": one counts unless it stacks
        Assert.Equal(24, PlanChecks.StationBuildRange(s, new[] { s, e1, e2 }), 6);
        var stack = Slab("ext", 0.3, 0.3, 0.3, 0.3, g => { g.ExtensionOf = "$piece_workbench"; g.ExtensionMaxDistance = 5; g.ExtensionStack = true; });
        Assert.Equal(28, PlanChecks.StationBuildRange(s, new[] { s, At(stack, 3, 0, 0, "a"), At(stack, 0, 0, 3, "b") }), 6);
    }
    [Fact] public void APieceThatNeedsAStationNeedsOneNearEnoughToStandBetween()
    {
        var bench = Slab("bench_piece", 1, 0.5, 1, 0.5, g => g.StationName = "$piece_workbench");
        Assert.Single(PlanChecks.StationsNeeded(new[] { At(bench, 0, 0, 0, "p") }));                  // no station in the plan
        Assert.Empty(PlanChecks.StationsNeeded(new[] { At(Station("wb"), 25, 0, 0, "s"), At(bench, 0, 0, 0, "p") }));      // 25 m: build range 20 plus the 8 m the builder can stand away
        Assert.Single(PlanChecks.StationsNeeded(new[] { At(Station("wb"), 40, 0, 0, "s"), At(bench, 0, 0, 0, "p") }));
    }

    [Fact] public void AKilnOnAWoodFloorIsRefused()
    {
        var floor = new PieceGeo { Prefab = "wood_floor", Material = "Wood", Supports = true, Colliders = { new ColliderBox { Centre = new V3(0, 0.1, 0), Half = new V3(1, 0.1, 1), Layer = "piece" } } };
        var kiln = Slab("charcoal_kiln", 0.5, 0.5, 0.5, 0.5, g => g.NotOnWood = true);
        Assert.Single(PlanChecks.NotOnWood(new[] { At(floor, 0, 0, 0, "f"), At(kiln, 0, 0.2, 0, "k") }));
        Assert.Empty(PlanChecks.NotOnWood(new[] { At(floor, 0, 0, 0, "f"), At(kiln, 0, 3, 0, "k") }));
    }

    [Fact] public void ALocationWhosePrefabAllowsBuildingNeverBlocks() => Assert.False(LocationRules.NoBuildHere(new LocationRules.Facts { Known = true, NoBuild = false, Exterior = 50 }, 10));
    [Fact] public void ALocationUsesItsExteriorRadiusUnlessItHasAnInterior()
    {
        var plain = new LocationRules.Facts { Known = true, NoBuild = true, Exterior = 30, Interior = 80 };
        Assert.False(LocationRules.NoBuildHere(plain, 50));                      // interior radius is ignored without m_hasInterior
        plain.HasInterior = true; Assert.True(LocationRules.NoBuildHere(plain, 50));
    }
    [Fact] public void AnUnreadablePrefabIsAssumedToBlockInsideItsLargerRadius()
    {
        var f = new LocationRules.Facts { Known = false, Exterior = 30, Interior = 80 };
        Assert.True(LocationRules.NoBuildHere(f, 60)); Assert.False(LocationRules.NoBuildHere(f, 90));
    }
}

public class SleepRulesTests
{
    [Fact] public void ExposureNeedsARoofAndEightyPercentCover() { Assert.True(SleepRules.Exposure(0.8, true)); Assert.False(SleepRules.Exposure(0.79, true)); Assert.False(SleepRules.Exposure(1.0, false)); }
    [Fact] public void TheGamesMessageOfTheFirstFailingRuleComesInTheGamesOrder()
    {
        Assert.Equal("$msg_cantsleep", SleepRules.FirstFailure(false, true, 0.1, false, false, true));
        Assert.Equal("$msg_bedenemiesnearby", SleepRules.FirstFailure(true, true, 0.1, false, false, true));
        Assert.Equal("$msg_bedneedroof", SleepRules.FirstFailure(true, false, 0.9, false, false, true));        // no roof, whatever the cover
        Assert.Equal("$msg_bedtooexposed", SleepRules.FirstFailure(true, false, 0.7, true, false, true));       // a roof but too open
        Assert.Equal("$msg_bednofire", SleepRules.FirstFailure(true, false, 0.9, true, false, true));
        Assert.Equal("$msg_bedwet", SleepRules.FirstFailure(true, false, 0.9, true, true, true));
        Assert.Null(SleepRules.FirstFailure(true, false, 0.8, true, true, false));
    }
}

public class PlanItemRulesTests
{
    [Fact] public void ATypoedKeyIsReported() => Assert.Equal(new[] { "rotY" }, PlanItemRules.Unknown(new[] { "prefab", "x", "y", "z", "rotY" }));
    [Fact] public void EveryKeyTheToolReadsIsKnown() => Assert.Empty(PlanItemRules.Unknown(new[] { "prefab", "x", "y", "z", "yaw", "rot", "seat", "key", "label", "text", "tag", "ctag", "ints", "floats" }));
    [Fact] public void OnlyANumberIsAY() { Assert.True(PlanItemRules.HasY(3.5)); Assert.False(PlanItemRules.HasY(double.NaN)); Assert.False(PlanItemRules.HasY(double.PositiveInfinity)); }
    [Fact] public void AMissingYNeedsSeatGround() { Assert.True(PlanItemRules.MissingY(false, null)); Assert.False(PlanItemRules.MissingY(false, "ground")); Assert.False(PlanItemRules.MissingY(true, null)); }
}

public class FireRulesTests
{
    [Fact] public void ABurningFireIsOnAndHasFuelOrNeedsNone() { Assert.True(FireRules.Burning(1, 3, false)); Assert.True(FireRules.Burning(1, 0, true)); Assert.False(FireRules.Burning(1, 0, false)); }
    [Fact] public void ASwitchedOffFireDoesNotBurnWhateverItHolds() { Assert.False(FireRules.Burning(0, 5, false)); Assert.False(FireRules.Burning(0, 0, true)); }
    [Fact] public void FuelToFullRoundsDownBecauseTheGameRefusesAboveMaxMinusOne() { Assert.Equal(7, FireRules.FuelToFull(2.5, 10, false)); Assert.Equal(0, FireRules.FuelToFull(0, 10, true)); }
}

public class RemovalImpactTests
{
    static PieceGeo Post() => new PieceGeo { Prefab = "post", Material = "Wood", Supports = true, ComOffset = new V3(0, 1, 0), Colliders = { new ColliderBox { Centre = new V3(0, 1, 0), Half = new V3(0.2, 1, 0.2), Layer = "piece" } } };
    static PlacedPiece At(double x, double y, string id) => new PlacedPiece { Id = id, Geo = Post(), Pos = new V3(x, y, 0) };
    static readonly Func<double, double, double> Flat = (x, z) => 0.0;

    [Fact] public void RemovingTheFootOfATowerBringsDownWhatRestsOnIt()
    {
        var all = new List<PlacedPiece> { At(0, -0.05, "a"), At(0, 1.95, "b"), At(0, 3.95, "c") };
        var falls = RemovalImpact.Collapses(all, new HashSet<string> { "a" }, Flat);
        Assert.Equal(new[] { "b", "c" }, falls.Select(p => p.Id).OrderBy(i => i).ToArray());
    }
    [Fact] public void RemovingTheTopBringsDownNothing()
    {
        var all = new List<PlacedPiece> { At(0, -0.05, "a"), At(0, 1.95, "b"), At(0, 3.95, "c") };
        Assert.Empty(RemovalImpact.Collapses(all, new HashSet<string> { "c" }, Flat));
    }
    [Fact] public void ARemovalFarAwayChangesNothing()
    {
        var all = new List<PlacedPiece> { At(0, -0.05, "a"), At(0, 1.95, "b"), At(20, -0.05, "x") };
        Assert.Empty(RemovalImpact.Collapses(all, new HashSet<string> { "x" }, Flat));
    }
    [Fact] public void APieceThatAlreadyFallsIsNotBlamedOnTheRemoval()
    {
        var all = new List<PlacedPiece> { At(0, 1.95, "floating"), At(5, -0.05, "a") };            // nothing under "floating"
        Assert.Empty(RemovalImpact.Collapses(all, new HashSet<string> { "a" }, Flat));
    }
}

public class PlanOrderTests
{
    [Fact] public void LowestFirstKeepsSupportersAheadOfWhatTheyCarry()
    {
        var items = new[] { (y: 8.0, id: "roof"), (y: 0.0, id: "floor"), (y: 4.0, id: "wall"), (y: 0.0, id: "floor2") };
        var sorted = PlanOrder.LowestFirst(items, i => PlanOrder.Rank(i.y, null));
        Assert.Equal(new[] { "floor", "floor2", "wall", "roof" }, sorted.Select(i => i.id).ToArray());          // equal heights keep the plan's order
    }
    [Fact] public void SeatedItemsGoFirstWhateverTheirY() => Assert.True(PlanOrder.Rank(50, "ground") < PlanOrder.Rank(0, null));
    [Fact] public void AnItemWithoutYRanksAsGroundLevel() => Assert.Equal(0, PlanOrder.Rank(null, null));
}

public class NoSupportWearTests
{
    [Fact] public void APieceThatNeverWearsFromMissingSupportNeverFalls()
    {
        var g = new PieceGeo { Prefab = "ornament", Material = "Wood", Supports = true, WearsWithoutSupport = false, Colliders = { new ColliderBox { Centre = new V3(0, 1, 0), Half = new V3(0.2, 1, 0.2), Layer = "piece" } } };
        var p = new PlacedPiece { Id = "o", Geo = g, Pos = new V3(0, 50, 0) };                      // floating
        var r = SupportModel.Run(new[] { p }, (x, z) => 0.0, null)[p];
        Assert.Equal(100, r.Support, 6); Assert.True(r.Holds);                                      // WearNTear.Awake: m_support = max, and nothing updates it
        g.WearsWithoutSupport = true; Assert.False(SupportModel.Run(new[] { p }, (x, z) => 0.0, null)[p].Holds);
    }
}

public class PlacementFixTests
{
    static PieceGeo Box(string name, double hx, double hy, double hz, bool noClip = false, string material = "Wood")
        => new PieceGeo { Prefab = name, Material = material, Supports = true, NoClipping = noClip, Colliders = { new ColliderBox { Centre = new V3(0, hy, 0), Half = new V3(hx, hy, hz), Layer = "piece" } } };

    [Fact] public void ANeverWearingPieceCarriesAFloatingNeighbour()
    {
        var anchor = new PlacedPiece { Id = "a", Geo = new PieceGeo { Prefab = "ornament", Material = "Wood", Supports = true, WearsWithoutSupport = false, Colliders = { new ColliderBox { Centre = new V3(0, 1, 0), Half = new V3(0.5, 1, 0.5), Layer = "piece" } } }, Pos = new V3(0, 50, 0) };
        var beam = new PlacedPiece { Id = "b", Geo = Box("beam", 0.5, 0.1, 0.5), Pos = new V3(0, 52, 0) };         // sits on top of the anchor
        var r = SupportModel.Run(new[] { anchor, beam }, (x, z) => 0.0, null);
        Assert.True(r[beam].Holds);
    }

    [Fact] public void OnlyThePieceBeingPlacedCanBeRefusedForClipping()
    {
        var old = new PlacedPiece { Id = "chair", Geo = Box("chair", 0.5, 0.5, 0.5, noClip: true), Pos = new V3(0, 0, 0), Planned = false };
        var wall = new PlacedPiece { Id = "wall", Geo = Box("wall", 1, 1, 0.1), Pos = new V3(0.3, 0, 0) };
        Assert.Equal("warning", PlanChecks.Overlaps(new[] { old, wall }).Single().Severity);                     // the wall may go through a chair that stands there
        var chair = new PlacedPiece { Id = "chair2", Geo = Box("chair", 0.5, 0.5, 0.5, noClip: true), Pos = new V3(0, 0, 0) };
        var existingWall = new PlacedPiece { Id = "w", Geo = Box("wall", 1, 1, 0.1), Pos = new V3(0.3, 0, 0), Planned = false };
        Assert.Equal("error", PlanChecks.Overlaps(new[] { chair, existingWall }).Single().Severity);              // a chair placed into a wall is refused
    }

    [Fact] public void ABoxOnASlopeTouchesTheGroundBetweenTheOldSamples()
    {
        var b = Obb.Make(new V3(0, 0.05, 0), new V3(2, 0.05, 2), Quat.Identity);                                  // a thin floor slab
        Assert.True(SupportModel.TouchesGround(b, (x, z) => 0.05 + 0.4 * x));                                    // the ground crosses it along the slope
        Assert.False(SupportModel.TouchesGround(b, (x, z) => 1.0 + 0.1 * x));                                    // all of it is above the slab
        Assert.False(SupportModel.TouchesGround(b, (x, z) => -1.0 + 0.1 * x));                                   // all of it is below
    }

    [Fact] public void ARotatedBoxIsSampledInsideItself()
    {
        var b = Obb.Make(new V3(0, 0.5, 0), new V3(0.1, 0.5, 3), Quat.Yaw(45));                                  // a long thin beam on the diagonal
        Assert.False(SupportModel.TouchesGround(b, (x, z) => (x > 1.5 && z < -1.5) || (x < -1.5 && z > 1.5) ? 0.5 : 10.0));      // ground meets the AABB corners, not the beam
    }

    [Fact] public void AScaledWorldPieceHasScaledColliders()
    {
        var tree = new PlacedPiece { Id = "t", Geo = Box("tree", 1, 1, 1), Pos = new V3(0, 0, 0), Scale = new V3(2, 2, 2) };
        var b = tree.Boxes().Single();
        Assert.Equal(2, b.Half.X, 6); Assert.Equal(2, b.C.Y, 6);
    }
}

public class SnapSeamTests
{
    static PieceGeo Floor() { var g = new PieceGeo { Prefab = "floor_2x2", Material = "Wood", Supports = true }; foreach (var x in new[] { -1.0, 1.0 }) foreach (var z in new[] { -1.0, 1.0 }) g.SnapPoints.Add(new V3(x, 0, z)); return g; }

    [Fact] public void AnExactNeighbourIsNotASeam()
    {
        var a = new PlacedPiece { Id = "a", Geo = Floor(), Pos = new V3(0, 0, 0) }; var b = new PlacedPiece { Id = "b", Geo = Floor(), Pos = new V3(2, 0, 0) };
        Assert.Empty(SnapSeams.Find(new[] { a, b }));
    }

    [Fact] public void AFloorLeftTenCentimetresOffIsASeam()
    {
        var a = new PlacedPiece { Id = "a", Geo = Floor(), Pos = new V3(0, 0, 0), Planned = false }; var b = new PlacedPiece { Id = "b", Geo = Floor(), Pos = new V3(2.1, 0, 0) };
        var r = SnapSeams.Find(new[] { a, b }).Single();
        Assert.Equal("b", r.Piece); Assert.Equal("warning", r.Severity); Assert.Equal("snap-seam", r.Rule);
    }

    [Fact] public void FarApartIsNothing()
    {
        var a = new PlacedPiece { Id = "a", Geo = Floor(), Pos = new V3(0, 0, 0) }; var b = new PlacedPiece { Id = "b", Geo = Floor(), Pos = new V3(5, 0, 0) };
        Assert.Empty(SnapSeams.Find(new[] { a, b }));
    }

    [Fact] public void ARotatedPieceUsesItsRotatedSnapPoints()
    {
        var a = new PlacedPiece { Id = "a", Geo = Floor(), Pos = new V3(0, 0, 0), Planned = false }; var b = new PlacedPiece { Id = "b", Geo = Floor(), Pos = new V3(2, 0, 0), Rot = Quat.Yaw(90) };
        Assert.Empty(SnapSeams.Find(new[] { a, b }));
    }
}

public class PlacementFlagTests
{
    static PieceGeo Geo(string prefab, string name = null) => new PieceGeo { Prefab = prefab, PieceName = name ?? prefab, Colliders = { new ColliderBox { Centre = new V3(0, 0.5, 0), Half = new V3(0.5, 0.5, 0.5), Layer = "piece" } } };

    [Fact] public void OnlyInBiomeRefusesAnotherBiome()
    {
        var g = Geo("flag"); g.OnlyInBiome = 8;                                              // Black Forest only
        var p = new PlacedPiece { Id = "a", Geo = g, Pos = new V3(0, 0, 0) };
        Assert.Null(PlanChecks.Biome(p, 8)); Assert.Null(PlanChecks.Biome(p, 8 | 1));
        var bad = PlanChecks.Biome(p, 1);
        Assert.Equal("wrong-biome", bad.Rule); Assert.Equal("error", bad.Severity); Assert.Equal("WrongBiome", bad.Code);
        Assert.Null(PlanChecks.Biome(new PlacedPiece { Id = "b", Geo = Geo("any"), Pos = new V3(0, 0, 0) }, 1));      // no restriction
    }

    [Fact] public void CultivatedGroundOnlyNeedsPaintButUnknownIsNoError()
    {
        var g = Geo("crop"); g.CultivatedGroundOnly = true; var p = new PlacedPiece { Id = "a", Geo = g, Pos = new V3(0, 0, 0) };
        Assert.Equal("needs-cultivated", PlanChecks.Cultivated(p, false).Rule);
        Assert.Null(PlanChecks.Cultivated(p, true)); Assert.Null(PlanChecks.Cultivated(p, null));
    }

    [Fact] public void ABlockedRadiusRefusesAPieceOfAListedName()
    {
        var g = Geo("beehive"); g.BlockRadius = 4; g.BlockingNames.Add("beehive");
        var a = new PlacedPiece { Id = "new", Geo = g, Pos = new V3(0, 0, 0) };
        var near = new PlacedPiece { Id = "old", Geo = g, Pos = new V3(3, 0, 0), Planned = false };
        var far = new PlacedPiece { Id = "far", Geo = g, Pos = new V3(10, 0, 0), Planned = false };
        Assert.Single(PlanChecks.BlockRadius(new[] { a, near }));
        Assert.Empty(PlanChecks.BlockRadius(new[] { a, far }));
        Assert.Empty(PlanChecks.BlockRadius(new[] { a, new PlacedPiece { Id = "other", Geo = Geo("wall"), Pos = new V3(1, 0, 0), Planned = false } }));      // a different name does not block
    }

    [Fact] public void MustConnectNeedsTheNamedObjectNear()
    {
        var g = Geo("collector"); g.MustConnectTo = "Root"; g.ConnectRadius = 1;
        var c = new PlacedPiece { Id = "c", Geo = g, Pos = new V3(0, 0, 0) };
        var root = new PlacedPiece { Id = "r", Geo = Geo("YggdrasilRoot"), Pos = new V3(0.2, 0, 0), Planned = false };
        Assert.Empty(PlanChecks.MustConnect(new[] { c, root }));
        Assert.Single(PlanChecks.MustConnect(new[] { c }));
        Assert.Single(PlanChecks.MustConnect(new[] { c, new PlacedPiece { Id = "r2", Geo = Geo("YggdrasilRoot"), Pos = new V3(5, 0, 0), Planned = false } }));
    }

    [Fact] public void MustBeAboveConnectedNeedsItDirectlyBelow()
    {
        var g = Geo("collector"); g.MustConnectTo = "Root"; g.ConnectRadius = 3; g.MustBeAboveConnected = true;
        var c = new PlacedPiece { Id = "c", Geo = g, Pos = new V3(0, 2, 0) };
        var rootBelow = new PlacedPiece { Id = "r", Geo = Geo("YggdrasilRoot"), Pos = new V3(0, 0.5, 0), Planned = false };
        Assert.Empty(PlanChecks.MustConnect(new[] { c, rootBelow }));
        var rootAside = new PlacedPiece { Id = "r", Geo = Geo("YggdrasilRoot"), Pos = new V3(1.8, 1.5, 0), Planned = false };
        Assert.Single(PlanChecks.MustConnect(new[] { c, rootAside }));                     // within reach but nothing under the piece
    }

    [Fact] public void RayTGivesTheEntryDistanceAndNothingFromInside()
    {
        var b = Obb.Make(new V3(0, 0, 0), new V3(1, 1, 1), Quat.Identity);
        Assert.Equal(4, b.RayT(new V3(5, 0, 0), new V3(-1, 0, 0), 100).Value, 6);
        Assert.Null(b.RayT(new V3(0, 0, 0), new V3(1, 0, 0), 100)); Assert.Null(b.RayT(new V3(5, 5, 0), new V3(-1, 0, 0), 100));
    }
}

public class SupportBoxTests
{
    [Fact] public void ANonBoxColliderIsItsWorldAxisBounds()
    {
        var g = new PieceGeo { Prefab = "log", Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(0.1, 0.1, 3), Layer = "piece", NonBox = true } } };
        var p = new PlacedPiece { Id = "l", Geo = g, Pos = new V3(0, 0, 0), Rot = Quat.Yaw(45) };
        var plain = p.Boxes().Single(); var aabb = p.SupportBoxes(null).Single();
        Assert.Equal(plain.Extent(new V3(1, 0, 0)), aabb.Half.X, 6);                    // the axis-aligned box that holds the turned log
        Assert.True(aabb.Half.X > 2 && aabb.Half.Z > 2);
        Assert.Equal(1, Math.Abs(aabb.Axes[0].X), 6);                                   // world axes
    }

    [Fact] public void ABoxColliderStaysOriented()
    {
        var g = new PieceGeo { Prefab = "beam", Colliders = { new ColliderBox { Centre = new V3(0, 0, 0), Half = new V3(0.1, 0.1, 3), Layer = "piece" } } };
        var p = new PlacedPiece { Id = "b", Geo = g, Pos = new V3(0, 0, 0), Rot = Quat.Yaw(45) };
        Assert.Equal(0.1, p.SupportBoxes(null).Single().Half.X, 6);
    }
}

public class BedHeatTests
{
    static PieceGeo Bed() => new PieceGeo { Prefab = "bed", CoverPoints = { new CoverPoint { Kind = "bed", Local = new V3(0, 0.5, 0), MinCover = 0.8 } } };
    static PieceGeo Fire() => new PieceGeo { Prefab = "fire_pit", HeatAreas = { new KeyValuePair<V3, double>(new V3(0, 0.5, 0), 3) } };

    [Fact] public void ABedNeedsAHeatAreaOverIt()
    {
        var bed = new PlacedPiece { Id = "bed", Geo = Bed(), Pos = new V3(0, 0, 0) };
        Assert.Equal("bed-heat", Assert.Single(PlanChecks.BedHeat(new[] { bed })).Rule);
        Assert.Empty(PlanChecks.BedHeat(new[] { bed, new PlacedPiece { Id = "f", Geo = Fire(), Pos = new V3(2, 0, 0) } }));
        Assert.Single(PlanChecks.BedHeat(new[] { bed, new PlacedPiece { Id = "f", Geo = Fire(), Pos = new V3(6, 0, 0) } }));                // too far
    }

    [Fact] public void AnUnlitWorldFireGivesNoHeat()
    {
        var bed = new PlacedPiece { Id = "bed", Geo = Bed(), Pos = new V3(0, 0, 0) };
        Assert.Single(PlanChecks.BedHeat(new[] { bed, new PlacedPiece { Id = "f", Geo = Fire(), Pos = new V3(2, 0, 0), Planned = false, Lit = false } }));
    }

    [Fact] public void OnlyPlannedBedsAreJudged() { Assert.Empty(PlanChecks.BedHeat(new[] { new PlacedPiece { Id = "bed", Geo = Bed(), Pos = new V3(0, 0, 0), Planned = false } })); }
}
