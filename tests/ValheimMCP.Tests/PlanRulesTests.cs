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
    public void AFireNeedsAMetreAllRound()
    {
        var fire = Box("fire_pit", 0.5, 0.3, 0.5); fire.IsFire = true;
        var wall = Box("stone_wall", 1, 1, 0.1); wall.IsWall = true;
        var chest = Box("chest", 0.5, 0.4, 0.4);
        var near = PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(wall, 0, 0, 1.0, "w") });                      // gap 0.4 m
        Assert.Equal("error", near.Single().Severity);
        var soft = PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(chest, 1.5, 0, 0, "c") });                      // gap 0.5 m, a chest: warning
        Assert.Equal("warning", soft.Single().Severity);
        Assert.Empty(PlanChecks.FireClearance(new[] { At(fire, 0, 0, 0, "f"), At(chest, 3, 0, 0, "c") }));
    }
}
