// Game-free bill of materials: what it takes to craft an item from what is in stock, and what a plan's pieces cost. Recipes come from ObjectDB (Recipes.cs reads them);
// the arithmetic follows Player.HaveRequirements / ConsumeResources: a craft makes Recipe.m_amount items for one set of ingredients, whole crafts only.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal sealed class RecipeRow
    {
        public string Item = ""; public int Amount = 1;
        public string Station = ""; public int MinLevel = 1;
        public bool OnlyOne, UpgradeOnly;                                                // m_requireOnlyOneIngredient: any ONE of the ingredients; m_noCraftOnlyUpgrade: not a way to make the item
        public List<KeyValuePair<string, int>> Ingredients = new List<KeyValuePair<string, int>>();
        public int RequiredStationLevel { get { return Math.Max(1, MinLevel); } }     // Recipe.GetRequiredStationLevel(1)
        public double RawPerItem { get { return Ingredients.Sum(i => i.Value) / (double)Math.Max(1, Amount); } }
    }

    internal sealed class BillStep
    {
        public string Item = ""; public int Crafts, Makes; public string Station = ""; public int Level, Depth;
        public List<KeyValuePair<string, int>> Uses = new List<KeyValuePair<string, int>>();
    }

    internal sealed class BillResult
    {
        public List<BillStep> Steps = new List<BillStep>();                              // in order: ingredients before what they make
        public Dictionary<string, int> Missing = new Dictionary<string, int>();         // what has to be fetched (nothing makes it, or the chain is too deep)
        public Dictionary<string, int> FromStock = new Dictionary<string, int>();
        public Dictionary<string, int> Stations = new Dictionary<string, int>();        // station name -> highest level needed
        public List<string> Notes = new List<string>();
        public bool Complete { get { return Missing.Count == 0; } }
    }

    internal static class Bill
    {
        public const int MaxDepth = 4;

        /// <summary>
        ///     What it takes to have <paramref name="count" /> of <paramref name="item" />. Stock is used first. A craft makes Recipe.m_amount items, whole crafts only, and what a craft makes over
        ///     stays in stock for the rest of the bill. Where several recipes make an item the one with the fewest ingredient units per item is used. An "any one ingredient" recipe takes the
        ///     ingredient that is in stock (the one there is most of), else the first.
        /// </summary>
        public static BillResult Run(string item, int count, IDictionary<string, int> stock, IDictionary<string, List<RecipeRow>> recipes)
        {
            var res = new BillResult();
            var have = new Dictionary<string, int>(stock ?? new Dictionary<string, int>());
            Need(item, Math.Max(1, count), 0, new List<string>(), have, recipes, res);
            return res;
        }

        public static RecipeRow Choose(string item, IDictionary<string, List<RecipeRow>> recipes)
        {
            List<RecipeRow> l; if (recipes == null || !recipes.TryGetValue(item, out l)) return null;
            RecipeRow best = null;
            foreach (var r in l) if (!r.UpgradeOnly && r.Amount > 0 && (best == null || r.RawPerItem < best.RawPerItem)) best = r;
            return best;
        }

        static void Need(string item, int count, int depth, List<string> path, Dictionary<string, int> have, IDictionary<string, List<RecipeRow>> recipes, BillResult res)
        {
            int inStock; have.TryGetValue(item, out inStock);
            var take = Math.Min(inStock, count); if (take > 0) { have[item] = inStock - take; Add(res.FromStock, item, take); }
            var deficit = count - take; if (deficit <= 0) return;

            var rec = Choose(item, recipes);
            if (rec == null) { Add(res.Missing, item, deficit); return; }
            if (depth >= MaxDepth || path.Contains(item)) { Add(res.Missing, item, deficit); res.Notes.Add(item + ": the chain is too deep or loops, stopped here"); return; }

            var crafts = (deficit + rec.Amount - 1) / rec.Amount;
            var surplus = crafts * rec.Amount - deficit; if (surplus > 0) have[item] = (have.ContainsKey(item) ? have[item] : 0) + surplus;
            var step = new BillStep { Item = item, Crafts = crafts, Makes = crafts * rec.Amount, Station = rec.Station, Level = rec.RequiredStationLevel, Depth = depth };
            if (rec.Station.Length > 0) { int lv; res.Stations.TryGetValue(rec.Station, out lv); res.Stations[rec.Station] = Math.Max(lv, rec.RequiredStationLevel); }

            IEnumerable<KeyValuePair<string, int>> use = rec.Ingredients;
            if (rec.OnlyOne && rec.Ingredients.Count > 0)
            {
                var pick = rec.Ingredients.OrderByDescending(i => have.ContainsKey(i.Key) ? Math.Min(have[i.Key], i.Value * crafts) : 0).First();
                use = new[] { pick };
            }
            path.Add(item);
            foreach (var ing in use)
            {
                var n = ing.Value * crafts; step.Uses.Add(new KeyValuePair<string, int>(ing.Key, n));
                Need(ing.Key, n, depth + 1, path, have, recipes, res);
            }
            path.RemoveAt(path.Count - 1);
            res.Steps.Add(step);
        }

        static void Add(Dictionary<string, int> d, string k, int n) { d[k] = (d.ContainsKey(k) ? d[k] : 0) + n; }

        /// <summary>One line per step: "Bronze x6: 3 crafts of 2 at forge level 1 from 6 Copper + 3 Tin".</summary>
        public static string Describe(BillStep s)
        {
            return s.Item + " x" + s.Makes + ": " + s.Crafts + (s.Crafts == 1 ? " craft" : " crafts") + (s.Station.Length > 0 ? " at " + s.Station + " level " + s.Level : "") + " from " + string.Join(" + ", s.Uses.Select(u => u.Value + " " + u.Key).ToArray());
        }
    }

    /// <summary>What a set of pieces costs (Piece.m_resources), with the pieces the world makes free.</summary>
    internal static class Materials
    {
        /// <param name="pieces">prefab -> how many are to be built</param>
        /// <param name="requirements">prefab -> its resources (item, amount)</param>
        /// <param name="free">prefabs that cost nothing in this world (their FreeBuildKey is set)</param>
        public static Dictionary<string, int> Sum(IDictionary<string, int> pieces, IDictionary<string, List<KeyValuePair<string, int>>> requirements, ISet<string> free, out List<string> unknown)
        {
            var total = new Dictionary<string, int>(); unknown = new List<string>();
            foreach (var kv in pieces)
            {
                List<KeyValuePair<string, int>> req;
                if (!requirements.TryGetValue(kv.Key, out req)) { unknown.Add(kv.Key); continue; }
                if (free != null && free.Contains(kv.Key)) continue;
                foreach (var r in req) total[r.Key] = (total.ContainsKey(r.Key) ? total[r.Key] : 0) + r.Value * kv.Value;
            }
            return total;
        }

        /// <summary>need vs stock: for each item what is needed, what is there and what is missing.</summary>
        public static List<KeyValuePair<string, int[]>> Compare(IDictionary<string, int> need, IDictionary<string, int> stock)
        {
            return need.OrderByDescending(n => n.Value).Select(n => { int have; stock.TryGetValue(n.Key, out have); return new KeyValuePair<string, int[]>(n.Key, new[] { n.Value, have, Math.Max(0, n.Value - have) }); }).ToList();
        }
    }
}
