using System;
using HubnerExt;
using Xunit;

public class ProducerRulesTests
{
    [Theory]
    [InlineData(false, 0.0, "empty")] [InlineData(true, 100.0, "fermenting")] [InlineData(true, 2400.0, "fermenting")] [InlineData(true, 2400.5, "ready")] [InlineData(true, -1.0, "fermenting")]
    public void FermenterStatusFollowsTheTimer(bool content, double since, string want) { Assert.Equal(want, ProducerRules.FermenterStatus(content, since, 2400)); }

    [Fact] public void SecondsLeftNeverGoesNegative()
    {
        Assert.Equal(2300, ProducerRules.FermenterSecondsLeft(100, 2400), 6);
        Assert.Equal(0, ProducerRules.FermenterSecondsLeft(5000, 2400), 6);
        Assert.Equal(2400, ProducerRules.FermenterSecondsLeft(-1, 2400), 6);
    }

    [Fact] public void ALevelFillsOneUnitPerInterval()
    {
        Assert.Equal(15, ProducerRules.SecondsToLevel(2, 4, 5, 10), 6);          // two more units at 10 s each, 5 s already banked
        Assert.Equal(0, ProducerRules.SecondsToLevel(4, 4, 0, 10), 6);
        Assert.Equal(0, ProducerRules.SecondsToLevel(3, 4, 99, 10), 6);
    }

    [Fact] public void ASapCollectorCannotTakeMoreThanTheRootHas()
    {
        Assert.Equal(4, ProducerRules.SapUnitsAvailable(0, 4, 100));
        Assert.Equal(2, ProducerRules.SapUnitsAvailable(0, 4, 2.9));
        Assert.Equal(0, ProducerRules.SapUnitsAvailable(0, 4, 0.5));
        Assert.Equal(1, ProducerRules.SapUnitsAvailable(3, 4, double.NaN));
        Assert.Equal(0, ProducerRules.SapUnitsAvailable(4, 4, 50));
    }
}

public class DropAndPickRulesTests
{
    [Fact] public void AYoungItemIsKeptAndAnOldOneGoesUnlessSomethingKeepsIt()
    {
        Assert.Null(DropRules.KeptBy(true, 100, false, 10, false, false, false));            // not an hour old yet: nothing to be kept from
        Assert.Null(DropRules.KeptBy(true, 4000, false, 10, false, false, false));           // old and alone: it goes
        Assert.Contains("base", DropRules.KeptBy(true, 4000, true, 40, false, false, false));
        Assert.Null(DropRules.KeptBy(true, 4000, true, 10, false, false, false));            // a base below y 28 does not keep it
        Assert.Contains("player", DropRules.KeptBy(true, 4000, false, 10, true, false, false));
        Assert.Contains("tar", DropRules.KeptBy(true, 4000, false, 10, false, true, false));
        Assert.Contains("piece", DropRules.KeptBy(true, 4000, false, 10, false, false, true));
        Assert.Contains("m_autoDestroy", DropRules.KeptBy(false, 4000, false, 10, false, false, false));
        Assert.Equal(3000, DropRules.SecondsLeft(600), 6); Assert.Equal(0, DropRules.SecondsLeft(9000), 6);
    }

    [Fact] public void APickedPlantComesBackAfterItsTime()
    {
        var now = TimeSpan.TicksPerMinute * 1000L;
        Assert.Equal(0, PickRules.MinutesToRespawn(false, true, 240, 0, now));
        Assert.True(double.IsNaN(PickRules.MinutesToRespawn(true, true, 0, 5, now)));         // never respawns
        Assert.Equal(240, PickRules.MinutesToRespawn(true, true, 240, 0, now), 6);           // not stamped yet
        Assert.Equal(190, PickRules.MinutesToRespawn(true, true, 240, now - 50 * TimeSpan.TicksPerMinute, now), 6);
        Assert.Equal(0, PickRules.MinutesToRespawn(true, true, 240, now - 300 * TimeSpan.TicksPerMinute, now), 6);
    }
}

public class ActiveAreaTests
{
    [Fact] public void ZonesAre64MetresCentredOnTheMultiples()
    {
        Assert.Equal(0, ActiveArea.Zone(31.9)); Assert.Equal(1, ActiveArea.Zone(32.0)); Assert.Equal(-1, ActiveArea.Zone(-32.1)); Assert.Equal(64, ActiveArea.ZoneCentre(1), 6);
    }

    [Fact] public void TheDefaultReachIsOneAndAHalfZonesFromTheCentreAndRoundedByTheEuclideanLimit()
    {
        // near 2, not classic: Chebyshev <= 96 and Euclid < 112
        Assert.True(ActiveArea.Inside(0, 0, 96, 0, 2, false));
        Assert.False(ActiveArea.Inside(0, 0, 97, 0, 2, false));
        Assert.False(ActiveArea.Inside(0, 0, 96, 96, 2, false));                    // the corner of the square is 135 m away
        Assert.True(ActiveArea.Inside(0, 0, 96, 96, 2, true));                      // classic keeps the square
        Assert.True(ActiveArea.Inside(0, 0, 96, 96, 3, false));
    }

    [Fact] public void NearOneIsOneZone()
    {
        Assert.True(ActiveArea.Inside(0, 0, 64, 0, 1, true)); Assert.False(ActiveArea.Inside(0, 0, 65, 0, 1, true));
    }

    [Fact] public void ThePeerStandingAnywhereInAZoneCoversFromThatZonesCentre()
    {
        Assert.True(ActiveArea.Inside(30, 30, 96, 0, 2, false));                     // the peer is at the edge of zone 0; the reach is measured from the zone centre
        Assert.False(ActiveArea.Inside(30, 30, 100, 0, 2, false));
    }
}

public class MineAndSpawnerRulesTests
{
    static string Pack(params float[] areas) { var b = new byte[4 + 4 * areas.Length]; BitConverter.GetBytes(areas.Length).CopyTo(b, 0); for (var i = 0; i < areas.Length; i++) BitConverter.GetBytes(areas[i]).CopyTo(b, 4 + 4 * i); return Convert.ToBase64String(b); }

    [Fact] public void MineRockAreasDecode()
    {
        var a = MineRules.DecodeAreas(Pack(2f, 0f, 1.5f, -3f));
        Assert.Equal(4, a.Length); Assert.Equal(2, MineRules.Remaining(a));
        Assert.Null(MineRules.DecodeAreas("")); Assert.Null(MineRules.DecodeAreas("not base64!")); Assert.Null(MineRules.DecodeAreas(Convert.ToBase64String(new byte[] { 1, 0, 0, 0 })));      // says one area, holds none
        Assert.Null(MineRules.DecodeAreas(Convert.ToBase64String(BitConverter.GetBytes(-1))));
        Assert.Equal(0, MineRules.Remaining(null));
    }

    [Fact] public void ToolTiers() { Assert.True(MineRules.ToolWorks(2, 2)); Assert.False(MineRules.ToolWorks(1, 2)); }

    [Fact] public void ASpawnerWaitsItsRespawnTimeAfterTheCreatureIsGone()
    {
        var now = TimeSpan.TicksPerMinute * 1000L;
        Assert.True(double.IsNaN(SpawnerRules.MinutesToRespawn(true, true, 20, 0, now)));                       // the creature lives
        Assert.Equal(15, SpawnerRules.MinutesToRespawn(false, true, 20, now - 5 * TimeSpan.TicksPerMinute, now), 6);
        Assert.Equal(0, SpawnerRules.MinutesToRespawn(false, true, 20, now - 60 * TimeSpan.TicksPerMinute, now), 6);
        Assert.True(double.IsNaN(SpawnerRules.MinutesToRespawn(false, true, 0, 0, now)));                       // spawns once
        Assert.Equal(0, SpawnerRules.MinutesToRespawn(false, false, 0, 0, now), 6);
    }
}

public class WearAndAbsenceRulesTests
{
    [Fact] public void RainNeverGoesBelowHalfHealth()
    {
        Assert.Equal(0, WearRules.SecondsOfRainToHalf(0.5, 100), 6);
        Assert.Equal(60, WearRules.SecondsOfRainToHalf(0.55, 100), 6);
        Assert.Equal(600, WearRules.SecondsOfRainToHalf(1.0, 100), 6);          // ten steps of 5 percent
    }

    [Fact] public void AshNeedsADamageSettingAndNoShelter()
    {
        Assert.False(WearRules.AshApplies(true, 0, false, false, false, false, 1));          // the setting is 0 in vanilla: no ash damage
        Assert.True(WearRules.AshApplies(true, 5, false, false, false, false, 1));
        Assert.False(WearRules.AshApplies(true, 5, true, false, false, false, 1));           // shield
        Assert.False(WearRules.AshApplies(true, 5, false, true, false, false, 1));           // immune
        Assert.False(WearRules.AshApplies(true, 5, false, false, true, false, 1));           // roof
        Assert.False(WearRules.AshApplies(true, 5, false, false, false, true, 0.05));        // a resistant piece stops at 10 percent
        Assert.False(WearRules.AshApplies(false, 5, false, false, false, false, 1));
    }

    [Fact] public void LavaNeedsPaintAndAHeightUnderOneAndAHalfMetres()
    {
        Assert.Equal(0, WearRules.LavaPercentPerTick(0.1, 0.5, false, false, false), 6);
        Assert.Equal(0, WearRules.LavaPercentPerTick(0.5, 2, false, false, false), 6);
        Assert.Equal(35, WearRules.LavaPercentPerTick(0.5, 1, false, false, false), 6);
        Assert.Equal(15, WearRules.LavaPercentPerTick(0.5, 1, true, false, false), 6);
        Assert.Equal(35 * 0.33, WearRules.LavaPercentPerTick(0.5, 1, false, false, true), 6);
        Assert.Equal(0, WearRules.LavaPercentPerTick(0.5, 1, false, true, false), 6);
    }

    [Fact] public void RequiredBiome() { Assert.False(WearRules.OutsideRequiredBiome(0, 4)); Assert.True(WearRules.OutsideRequiredBiome(2, 4)); Assert.False(WearRules.OutsideRequiredBiome(6, 4)); }

    [Fact] public void FireBurnsFuelTimesSecondsPerFuel()
    {
        Assert.Equal(7200, AbsenceRules.FireBurnsFor(2, 3600), 6);
        Assert.True(AbsenceRules.FireOutWithin(2, 3600, 8 * 3600));
        Assert.False(AbsenceRules.FireOutWithin(10, 3600, 8 * 3600));
    }
}

public class ShieldRulesTests
{
    [Fact] public void TheDomeGrowsWithTheFuel()
    {
        Assert.Equal(10, ShieldRules.Radius(0, 100, 10, 30, false), 6);                 // empty but not switched off: the minimum
        Assert.Equal(0, ShieldRules.Radius(0, 100, 10, 30, true), 6);                   // switched off with no fuel
        Assert.Equal(20, ShieldRules.Radius(50, 100, 10, 30, true), 6);
        Assert.Equal(30, ShieldRules.Radius(500, 100, 10, 30, true), 6);                // over full counts as full
        Assert.Equal(10, ShieldRules.Radius(5, 0, 10, 30, true), 6);
    }

    [Fact] public void InsideIsTheDistanceUnderTheRadius()
    {
        var s = new[] { new ShieldFact { X = 0, Y = 0, Z = 0, Radius = 20 } };
        Assert.True(ShieldRules.Inside(s, 19, 0, 0)); Assert.False(ShieldRules.Inside(s, 20, 0, 0)); Assert.False(ShieldRules.Inside(s, 15, 15, 0));
        Assert.False(ShieldRules.Inside(new[] { new ShieldFact { Radius = 0 } }, 0, 0, 0)); Assert.False(ShieldRules.Inside(null, 0, 0, 0));
    }
}

public class SituationBandTests
{
    [Fact] public void BandsNameANumber()
    {
        Assert.Equal("healthy", SituationBands.Health(1)); Assert.Equal("hurt", SituationBands.Health(0.5)); Assert.Equal("low", SituationBands.Health(0.2));
        Assert.Equal("fresh", SituationBands.Stamina(0.9)); Assert.Equal("tired", SituationBands.Stamina(0.3)); Assert.Equal("spent", SituationBands.Stamina(0.1));
        Assert.Equal("light", SituationBands.Pack(100, 300, false)); Assert.Equal("loaded", SituationBands.Pack(200, 300, false)); Assert.Equal("heavy", SituationBands.Pack(270, 300, false)); Assert.Equal("encumbered", SituationBands.Pack(400, 300, true));
        Assert.Equal("hungry", SituationBands.Food(0, 0)); Assert.Equal("fading", SituationBands.Food(2, 60)); Assert.Equal("fed", SituationBands.Food(2, 900));
        Assert.Equal("here", SituationBands.Distance(5)); Assert.Equal("near", SituationBands.Distance(40)); Assert.Equal("away", SituationBands.Distance(200)); Assert.Equal("far", SituationBands.Distance(900)); Assert.Equal("unknown", SituationBands.Distance(double.NaN));
        Assert.Equal("calm", SituationBands.Danger(0, 0)); Assert.Equal("watch", SituationBands.Danger(2, 0)); Assert.Equal("danger", SituationBands.Danger(2, 1));
    }
}
