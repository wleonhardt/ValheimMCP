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
