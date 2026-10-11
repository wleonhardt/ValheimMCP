using System;
using System.Collections.Generic;
using System.Linq;
using HubnerExt;
using Xunit;

public class CoverTests
{
    static CoverSolid Solid(double cx, double cy, double cz, double hx, double hy, double hz, bool leaky = false) => new CoverSolid { Box = Obb.Make(new V3(cx, cy, cz), new V3(hx, hy, hz), Quat.Identity), Leaky = leaky };

    // a closed 6 x 6 hut, 3 m high: four walls and a roof
    static List<CoverSolid> Hut(bool roof = true, bool leakyRoof = false)
    {
        var l = new List<CoverSolid>
        {
            Solid(3.1, 1.5, 0, 0.1, 1.5, 3.2), Solid(-3.1, 1.5, 0, 0.1, 1.5, 3.2), Solid(0, 1.5, 3.1, 3.2, 1.5, 0.1), Solid(0, 1.5, -3.1, 3.2, 1.5, 0.1)
        };
        if (roof) l.Add(Solid(0, 3.1, 0, 3.4, 0.1, 3.4, leakyRoof));
        return l;
    }

    [Fact] public void ABedInAClosedHutIsFullyCovered()
    {
        var c = CoverModel.At(new V3(0, 0.5, 0), Hut(), null);
        Assert.True(c.UnderRoof); Assert.Equal(1.0, c.Percent, 6); Assert.Equal(17, c.Rays);
        Assert.Null(CoverModel.Verdict("bed", c, 0.8));
    }

    [Fact] public void NoRoofIsTheFirstFailure()
    {
        var c = CoverModel.At(new V3(0, 0.5, 0), Hut(roof: false), null);
        Assert.False(c.UnderRoof);
        Assert.Equal("$msg_bedneedroof", CoverModel.Verdict("bed", c, 0.8));
        Assert.Equal("$msg_stationneedroof", CoverModel.Verdict("station", c, 0.7));
    }

    [Fact] public void ALeakyRoofIsNotARoofButStillStopsRays()
    {
        var c = CoverModel.At(new V3(0, 0.5, 0), Hut(leakyRoof: true), null);
        Assert.False(c.UnderRoof); Assert.True(c.Percent > 0.9);
    }

    [Fact] public void ARoofOnPolesIsTooExposed()
    {
        var open = new List<CoverSolid> { Solid(0, 3.1, 0, 3.4, 0.1, 3.4) };               // a flat roof, no walls
        var c = CoverModel.At(new V3(0, 0.5, 0), open, null);
        Assert.True(c.UnderRoof); Assert.True(c.Percent < 0.8);
        Assert.Equal("$msg_bedtooexposed", CoverModel.Verdict("bed", c, 0.8));
    }

    [Fact] public void TheStationLimitIsLowerThanTheBedLimit()
    {
        var walls = Hut().Where(s => s.Box.C.Y < 2).ToList(); walls.Add(Solid(0, 3.1, 0, 3.4, 0.1, 3.4));
        walls.RemoveAt(3);                                                                // one wall missing
        var c = CoverModel.At(new V3(0, 0.5, 0), walls, null);
        Assert.True(c.Percent >= 0.7 && c.Percent < 0.95, c.Percent.ToString());
        Assert.Null(CoverModel.Verdict("station", c, 0.7));
    }

    [Fact] public void TheGroundCountsAsCoverAndAsARoofOnlyAboveThePoint()
    {
        Func<double, double, double> flat = (x, z) => 0.0;
        var c = CoverModel.At(new V3(0, 1, 0), new List<CoverSolid>(), flat);
        Assert.False(c.UnderRoof); Assert.Equal(0.0, c.Percent, 6);                       // rays go up and sideways: none meets flat ground below
        Func<double, double, double> hill = (x, z) => x > 3 ? 50.0 : 0.0;                 // a cliff 3 m to the east
        var d = CoverModel.At(new V3(0, 1, 0), new List<CoverSolid>(), hill);
        Assert.True(d.Hits > 0 && d.Hits < d.Rays);
        Assert.True(CoverModel.IsUnderRoof(new V3(0, 1, 0), new List<CoverSolid>(), (x, z) => 5.0));       // a cave: ground above the point
    }

    [Fact] public void ARaySpendsItsFirstHalfMetreAwayFromTheStart()
    {
        // a wall 0.3 m from the point is inside the start offset of the side rays: the ray starts beyond it and misses
        var wall = new List<CoverSolid> { Solid(0.3, 1, 0, 0.05, 2, 5) };
        var c = CoverModel.At(new V3(0, 1, 0), wall, null);
        Assert.Equal(0, c.Hits);
    }

    [Fact] public void CheckReportsABedWithoutARoofAndNotOneWithIt()
    {
        var geo = new PieceGeo { Prefab = "bed", Material = "Wood", CoverPoints = { new CoverPoint { Kind = "bed", Local = new V3(0, 0.5, 0), MinCover = 0.8 } } };
        var hutGeo = new PieceGeo { Prefab = "hut" };
        foreach (var s in Hut()) hutGeo.Colliders.Add(new ColliderBox { Centre = s.Box.C, Half = s.Box.Half, Layer = "piece", Leaky = s.Leaky });
        var bed = new PlacedPiece { Id = "bed", Geo = geo, Pos = new V3(0, 0, 0) };
        var withHut = CoverChecks.Check(new[] { bed, new PlacedPiece { Id = "hut", Geo = hutGeo, Pos = new V3(0, 0, 0), Planned = false } }, null);
        Assert.Empty(withHut);
        var alone = CoverChecks.Check(new[] { bed }, null);
        var p = Assert.Single(alone);
        Assert.Equal("bed-cover", p.Rule); Assert.Equal("error", p.Severity); Assert.Contains("$msg_bedneedroof", p.Detail);
    }

    [Fact] public void ASmelterOnlyNeedsARoof()
    {
        var geo = new PieceGeo { Prefab = "smelter", CoverPoints = { new CoverPoint { Kind = "smelter", Local = new V3(0, 1, 0), MinCover = 0 } } };
        var roofGeo = new PieceGeo { Prefab = "roof", Colliders = { new ColliderBox { Centre = new V3(0, 3, 0), Half = new V3(2, 0.1, 2), Layer = "piece" } } };
        var s = new PlacedPiece { Id = "s", Geo = geo, Pos = new V3(0, 0, 0) };
        Assert.Empty(CoverChecks.Check(new[] { s, new PlacedPiece { Id = "r", Geo = roofGeo, Pos = new V3(0, 0, 0), Planned = false } }, null));
        Assert.Equal("smelter-roof", Assert.Single(CoverChecks.Check(new[] { s }, null)).Rule);
    }

    [Fact] public void ABeehiveWantsOpenAirNotARoof()
    {
        var open = CoverModel.At(new V3(0, 1, 0), new List<CoverSolid>(), null);
        Assert.Null(CoverModel.Verdict("beehive", open, 0, 0.25));
        var closed = CoverModel.At(new V3(0, 0.5, 0), Hut(), null);
        Assert.Equal("$piece_beehive_freespace", CoverModel.Verdict("beehive", closed, 0, 0.25));
    }

    [Fact] public void AFermenterNeedsARoofAndSevenTenthsCover()
    {
        var c = CoverModel.At(new V3(0, 0.5, 0), Hut(roof: false), null);
        Assert.Equal("$piece_fermenter_needroof", CoverModel.Verdict("fermenter", c, 0.7));
        var d = CoverModel.At(new V3(0, 0.5, 0), new List<CoverSolid> { Solid(0, 3.1, 0, 3.4, 0.1, 3.4) }, null);
        Assert.Equal("$piece_fermenter_exposed", CoverModel.Verdict("fermenter", d, 0.7));
    }
}
