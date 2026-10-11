// Game-free plant rules (Plant.UpdateHealth, HaveRoof, HaveGrowSpace, decompiled). Order of the verdict: wrong biome, not cultivated, too hot, too cold, no sun, no space.
// No sun: any collider of the layers Default, static_solid or piece in the 100 m straight above the plant (terrain and non-solid pieces do not count, and a wall beside it does not).
// No space: any collider of Default, static_solid, Default_small, piece or piece_nonsolid within the grow radius that is not a plant; another plant blocks only while it is healthy.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal sealed class PlantFacts
    {
        public string Prefab = ""; public double GrowRadius = 1, GrowTime = 10, GrowTimeMax = 2000;
        public int BiomeMask; public bool NeedCultivated, TolerateHeat, TolerateCold;
    }

    internal sealed class PlantVerdict
    {
        public string Status = "Healthy"; public string Why = ""; public bool Unknown;          // Unknown: a condition could not be judged here (the zone is not loaded)
    }

    internal static class PlantRules
    {
        public static int All() { return Meadows | Swamp | Mountain | BlackForest | Plains | AshLands | DeepNorth | Ocean | Mistlands; }
        public const int Meadows = 1, Swamp = 2, Mountain = 4, BlackForest = 8, Plains = 16, AshLands = 32, DeepNorth = 64, Ocean = 256, Mistlands = 512;

        static bool InRoofMask(string layer) { return layer == "Default" || layer == "static_solid" || layer == "piece"; }
        static bool InSpaceMask(string layer) { return layer == "Default" || layer == "static_solid" || layer == "Default_small" || layer == "piece" || layer == "piece_nonsolid"; }

        /// <param name="biome">biome flag at the plant (Heightmap.Biome value)</param>
        /// <param name="cultivated">null = unknown (zone not loaded)</param>
        /// <param name="others">everything else standing round it (pieces, rocks, trees) and the other plants as <see cref="PlantFacts" />-less pieces</param>
        /// <param name="isPlant">tells whether a placed piece is itself a plant (plants only block while healthy; the caller says which are)</param>
        public static PlantVerdict Judge(PlantFacts f, V3 pos, int biome, bool? cultivated, IList<PlacedPiece> others, Func<PlacedPiece, bool> isPlant, Func<PlacedPiece, bool> plantHealthy)
        {
            var v = new PlantVerdict();
            if ((biome & f.BiomeMask) == 0) { v.Status = "WrongBiome"; v.Why = "biome " + biome + " is not in the plant's biomes (" + f.BiomeMask + ")"; return v; }
            if (f.NeedCultivated)
            {
                if (!cultivated.HasValue) { v.Unknown = true; v.Why = "cultivated ground could not be read (zone not loaded)"; }
                else if (!cultivated.Value) { v.Status = "NotCultivated"; v.Why = "the ground is not cultivated (paint green under 0.5)"; return v; }
            }
            if (!f.TolerateHeat && biome == AshLands) { v.Status = "TooHot"; v.Why = "Ashlands, no heat tolerance"; return v; }
            if (!f.TolerateCold && (biome == DeepNorth || biome == Mountain)) { v.Status = "TooCold"; v.Why = "cold biome, no cold tolerance"; return v; }

            var up = new V3(0, 1, 0);
            foreach (var o in others)
                foreach (var b in o.Boxes(InRoofMask))
                    if (b.RayHits(pos, up, 100)) { v.Status = "NoSun"; v.Why = o.Geo.Prefab + " (" + o.Id + ") is above it"; return v; }
            foreach (var o in others)
            {
                var plant = isPlant != null && isPlant(o);
                if (plant && (plantHealthy == null || !plantHealthy(o))) continue;
                foreach (var b in o.Boxes(InSpaceMask))
                {
                    var cp = b.ClosestPoint(pos);
                    if (V3.Dist(cp, pos) <= f.GrowRadius) { v.Status = "NoSpace"; v.Why = o.Geo.Prefab + " (" + o.Id + ") is within " + f.GrowRadius + " m" + (plant ? " and healthy (two plants this close: the game updates them one after the other, so one of the two survives and the other is NoSpace)" : ""); return v; }
                }
            }
            return v;
        }

        /// <summary>Seconds a plant needs: Random.Range seeded by its own seed picks a point between the two times, so only the range is known without the seed.</summary>
        public static double[] GrowRange(PlantFacts f) { return new[] { f.GrowTime, f.GrowTimeMax }; }

        /// <summary>How many plants of this radius fit on a patch (a crop grid at one metre per plant is the usual best).</summary>
        public static int FitsOn(double width, double depth, double growRadius)
        {
            var step = Math.Max(0.1, growRadius);
            return (int)(Math.Floor(width / step) * Math.Floor(depth / step));
        }
    }
}
