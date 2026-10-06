using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Settings;

namespace ArbetsWatch.Core.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public async Task Preferences_round_trip_through_the_store()
    {
        using var temp = new TempStore();
        var preferences = new AppPreferences
        {
            Filter = new AdFilter
            {
                RegionIds = new HashSet<string> { "wjee_qH2_yb6" },
                MunicipalityIds = new HashSet<string> { "PVZL_BQT_XtL", "mc45_ki9_Bv3" },
                Worktime = WorktimeSet.PartTime | WorktimeSet.NotSpecified,
            },
            PollMinutes = 15,
            Window = new WindowBounds(100, 200, 640, 780, false),
            Theme = ThemePreference.Dark,
            Transparent = true,
            AlwaysOnTop = true,
            MonitoringPaused = true,
        };

        await PreferencesStore.SaveAsync(temp.Store, preferences);
        temp.Reopen();

        Assert.Equal(preferences, await PreferencesStore.LoadAsync(temp.Store));
    }

    [Fact]
    public async Task Missing_preferences_are_defaults()
    {
        using var temp = new TempStore();
        var loaded = await PreferencesStore.LoadAsync(temp.Store);
        Assert.Equal(new AppPreferences(), loaded);
        Assert.True(loaded.Filter.AllSweden);
        Assert.Equal(WorktimeSet.All, loaded.Filter.Worktime);
        Assert.Equal(5, loaded.PollMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(61, 60)]
    public void Poll_interval_is_clamped(int stored, int expected) =>
        Assert.Equal(expected, PreferencesStore.Deserialize($$"""{"pollMinutes":{{stored}}}""").PollMinutes);

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"filter":{"allSweden":"yes"}}""")]
    [InlineData("""{"theme":"Neon"}""")]
    public void Unreadable_preferences_fall_back_to_defaults(string json) =>
        Assert.Equal(new AppPreferences(), PreferencesStore.Deserialize(json));
}
