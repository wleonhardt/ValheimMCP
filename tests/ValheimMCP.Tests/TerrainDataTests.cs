using System;
using System.Linq;
using HubnerExt;
using Xunit;

public class TerrainDataTests
{
    static TcData Sample()
    {
        var d = TcData.Empty(4);                                            // 25 nodes
        d.Operations = 3; d.Lx = 1.5f; d.Ly = 40; d.Lz = -2; d.LastRadius = 2.5f;
        d.ModHeight[7] = true; d.Level[7] = 1.25f; d.Smooth[7] = -0.5f;
        d.ModPaint[3] = true; d.Paint[3] = new[] { 0f, 1f, 0f, 1f };
        return d;
    }

    [Fact] public void RoundTrips()
    {
        var d = Sample(); var e = TcData.Decode(d.Encode());
        Assert.Equal(3, e.Operations); Assert.Equal(25, e.ModHeight.Length); Assert.True(e.ModHeight[7]); Assert.Equal(1.25f, e.Level[7]); Assert.Equal(-0.5f, e.Smooth[7]);
        Assert.True(e.ModPaint[3]); Assert.Equal(new[] { 0f, 1f, 0f, 1f }, e.Paint[3]); Assert.Equal(1, e.HeightNodes); Assert.Equal(1, e.PaintNodes);
        Assert.Equal(d.Encode(), e.Encode());                              // byte for byte
    }

    [Fact] public void TheLayoutIsTheGamesSave()
    {
        var b = Sample().Encode();
        Assert.Equal(1, BitConverter.ToInt32(b, 0)); Assert.Equal(3, BitConverter.ToInt32(b, 4));                  // version, operations
        Assert.Equal(25, BitConverter.ToInt32(b, 4 + 4 + 16));                                                      // then the point (3 floats), the radius, the node count
    }

    [Fact] public void EmptyHasNoEdits() { var e = TcData.Empty(); Assert.Equal(65 * 65, e.ModHeight.Length); Assert.Equal(0, e.HeightNodes); Assert.Equal(0, e.PaintNodes); Assert.Equal(e.Encode().Length, TcData.Decode(e.Encode()).Encode().Length); }

    [Fact] public void DiffCountsWhatChanged()
    {
        var a = Sample(); var b = Sample();
        b.Level[7] = 2.25f; b.ModHeight[9] = true; b.Level[9] = 0.4f; b.Paint[3] = new[] { 0f, 0f, 1f, 1f };
        int h, p; double mx; b.Diff(a, out h, out p, out mx);
        Assert.Equal(2, h); Assert.Equal(1, p); Assert.Equal(1.0, mx, 6);
        a.Diff(a, out h, out p, out mx); Assert.Equal(0, h); Assert.Equal(0, p);
    }

    [Fact] public void BadDataIsRefused()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => TcData.Decode(BitConverter.GetBytes(1).Concat(BitConverter.GetBytes(0)).Concat(new byte[16]).Concat(BitConverter.GetBytes(-5)).ToArray()));
        Assert.ThrowsAny<Exception>(() => TcData.Decode(new byte[3]));
    }

    [Fact] public void ADiscOfStrokes()
    {
        var pts = StrokePlan.Disc(0, 0, 4, 2, 1000);
        Assert.Contains(pts, p => p[0] == 0 && p[1] == 0); Assert.All(pts, p => Assert.True(p[0] * p[0] + p[1] * p[1] <= 16.0001));
        Assert.Equal(13, pts.Count);                                                  // a 5 x 5 grid of 2 m inside radius 4: the corners (distance 5.6) fall outside
        Assert.Equal(3, StrokePlan.Disc(0, 0, 100, 1, 3).Count);                        // the cap
    }

    [Fact] public void ABoxOfStrokesAndItsZones()
    {
        Assert.Equal(9, StrokePlan.Box(0, 0, 4, 4, 2, 100).Count);
        var z = StrokePlan.Zones(-31, -31, 31, 31, 0);
        Assert.Single(z); Assert.Equal(new[] { 0, 0 }, z[0]);
        Assert.Equal(4, StrokePlan.Zones(30, 30, 34, 34, 0).Count);                    // straddles the zone corner at 32
    }
}

public class WecLineTests
{
    static WecLine B(double? radius = null, double? width = null, double? depth = null, double? level = null, double? raise = null, string paint = null, double smooth = 0, double x = -280, double z = -300)
    { return WecLine.Build(x, z, radius, width, depth, level, raise, paint, smooth); }

    [Fact] public void ACircleLevelIsAFixedLine()
    {
        var r = B(radius: 5, level: 44.5, paint: "cultivated"); Assert.Null(r.Error);
        Assert.Equal("terrain from=-280,-300 circle=5 level=44.5 paint=cultivated", r.Line); Assert.True(r.HeightOp);
    }
    [Fact] public void ARectangleSendsHalfExtentsAndAFixedAngleAndSmoothAsAFraction()
    {
        var r = B(width: 8, depth: 12, raise: -1.5, smooth: 2); Assert.Null(r.Error);
        Assert.Equal("terrain from=-280,-300 rect=4,6 angle=0 raise=-1.5 smooth=0.5", r.Line);                      // 2 m of the smaller half extent (4 m)
    }
    [Fact] public void SmoothNeverExceedsTheWholeShapeAndPaintOnlyHasNoSmooth()
    {
        Assert.EndsWith("smooth=1", B(radius: 3, level: 40, smooth: 20).Line);
        Assert.DoesNotContain("smooth", B(radius: 3, paint: "dirt", smooth: 5).Line);
    }
    [Fact] public void OnlyTheListedPaintsAndNothingElseCanReachTheLine()
    {
        foreach (var p in new[] { "lava", "dirt reset", "dirt\nquit", "dirt;reset", "DIRT", "reset" }) Assert.NotNull(B(radius: 3, paint: p).Error);
        foreach (var p in WecLine.Paints) Assert.Null(B(radius: 3, paint: p).Error);
        Assert.DoesNotContain("reset", string.Join(" ", WecLine.Paints));
    }
    [Fact] public void BadNumbersAreRefused()
    {
        Assert.NotNull(B(radius: double.NaN, level: 40).Error); Assert.NotNull(B(radius: 41, level: 40).Error); Assert.NotNull(B(radius: 0, level: 40).Error);
        Assert.NotNull(B(radius: 3, level: double.PositiveInfinity).Error); Assert.NotNull(B(radius: 3, level: 30).Error); Assert.NotNull(B(radius: 3, level: 2001).Error);
        Assert.NotNull(B(radius: 3, raise: 51).Error); Assert.NotNull(B(radius: 3, level: 40, raise: 1).Error); Assert.NotNull(B(radius: 3, level: 40, smooth: 21).Error); Assert.NotNull(B(radius: 3, level: 40, smooth: -1).Error);
        Assert.NotNull(B(width: 81, depth: 4, level: 40).Error); Assert.NotNull(B(width: 4, level: 40).Error); Assert.NotNull(B(radius: 3, width: 4, depth: 4, level: 40).Error);
        Assert.NotNull(B(radius: 3, level: 40, x: 1e9).Error); Assert.NotNull(B(radius: 3).Error);
    }
    [Fact] public void NumbersUseTheInvariantCulture()
    {
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        try { System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); Assert.Equal("terrain from=-280.5,-300 circle=5 level=44.5", B(radius: 5, level: 44.5, x: -280.5).Line); }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
    }
}

public class TerrainEditedNodesTests
{
    [Fact] public void CountsOnlyModifiedNodesInsideTheBox()
    {
        var d = TcData.Empty(4);                                            // 5 x 5 nodes, a zone 4 m wide centred on (100, 200): nodes at x 98..102, z 198..202
        d.ModHeight[0] = true;                                              // (98, 198)
        d.ModHeight[12] = true;                                             // i=2, j=2: (100, 200)
        d.ModHeight[24] = true;                                             // (102, 202)
        Assert.Equal(3, d.ModifiedHeightNodesIn(100, 200, 90, 190, 110, 210));
        Assert.Equal(1, d.ModifiedHeightNodesIn(100, 200, 99.5, 199.5, 100.5, 200.5));
        Assert.Equal(2, d.ModifiedHeightNodesIn(100, 200, 98, 198, 100, 200));      // edges are inclusive
        Assert.Equal(0, d.ModifiedHeightNodesIn(100, 200, 0, 0, 10, 10));
        Assert.Equal(0, TcData.Empty(4).ModifiedHeightNodesIn(100, 200, 90, 190, 110, 210));
    }
}
