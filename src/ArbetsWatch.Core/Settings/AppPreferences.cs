using System.Text.Json;
using System.Text.Json.Serialization;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;

namespace ArbetsWatch.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record WindowBounds(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>User preferences, stored as one JSON document in the <c>preferences</c> table.</summary>
public sealed record AppPreferences
{
    public const int MinPollMinutes = 1;
    public const int MaxPollMinutes = 60;
    public const int DefaultPollMinutes = 5;

    public AdFilter Filter { get; init; } = AdFilter.Default;

    public int PollMinutes { get; init; } = DefaultPollMinutes;

    public WindowBounds? Window { get; init; }

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public bool Transparent { get; init; }

    public bool AlwaysOnTop { get; init; }

    public bool MonitoringPaused { get; init; }

    public AppPreferences Normalized() =>
        this with { PollMinutes = Math.Clamp(PollMinutes, MinPollMinutes, MaxPollMinutes) };
}

public static class PreferencesStore
{
    /// <summary>A missing or empty worktime selection would show nothing: it falls back to all categories.</summary>
    private static WorktimeSet Worktime(WorktimeSet? stored)
    {
        var value = (stored ?? WorktimeSet.All) & WorktimeSet.All;
        return value == WorktimeSet.None ? WorktimeSet.All : value;
    }

    private const string Key = "app";

    public static async Task<AppPreferences> LoadAsync(AdStore store, CancellationToken cancellationToken = default) =>
        Deserialize(await store.GetPreferenceAsync(Key, cancellationToken).ConfigureAwait(false));

    public static Task SaveAsync(AdStore store, AppPreferences preferences, CancellationToken cancellationToken = default) =>
        store.SetPreferenceAsync(Key, Serialize(preferences), cancellationToken);

    public static string Serialize(AppPreferences preferences)
    {
        var p = preferences.Normalized();
        var dto = new PreferencesDto(
            new FilterDto(p.Filter.AllSweden, [.. p.Filter.RegionIds.Order(StringComparer.Ordinal)],
                [.. p.Filter.MunicipalityIds.Order(StringComparer.Ordinal)], p.Filter.Worktime),
            p.PollMinutes, p.Window, p.Theme, p.Transparent, p.AlwaysOnTop, p.MonitoringPaused);
        return JsonSerializer.Serialize(dto, PreferencesJsonContext.Default.PreferencesDto);
    }

    /// <summary>Unreadable or missing preferences fall back to defaults instead of blocking startup.</summary>
    public static AppPreferences Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppPreferences();
        }

        try
        {
            var dto = JsonSerializer.Deserialize(json, PreferencesJsonContext.Default.PreferencesDto);
            if (dto is null)
            {
                return new AppPreferences();
            }

            var filter = dto.Filter is { } f
                ? new AdFilter
                {
                    AllSweden = f.AllSweden ?? false,
                    RegionIds = new HashSet<string>(f.RegionIds ?? [], StringComparer.Ordinal),
                    MunicipalityIds = new HashSet<string>(f.MunicipalityIds ?? [], StringComparer.Ordinal),
                    Worktime = Worktime(f.Worktime),
                }
                : AdFilter.Default;

            return new AppPreferences
            {
                Filter = filter,
                PollMinutes = dto.PollMinutes ?? AppPreferences.DefaultPollMinutes,
                Window = dto.Window is { Width: > 0, Height: > 0 } window ? window : null,
                Theme = dto.Theme ?? ThemePreference.System,
                Transparent = dto.Transparent ?? false,
                AlwaysOnTop = dto.AlwaysOnTop ?? false,
                MonitoringPaused = dto.MonitoringPaused ?? false,
            }.Normalized();
        }
        catch (JsonException)
        {
            return new AppPreferences();
        }
    }
}

internal sealed record FilterDto(bool? AllSweden, string[]? RegionIds, string[]? MunicipalityIds, WorktimeSet? Worktime);

internal sealed record PreferencesDto(
    FilterDto? Filter,
    int? PollMinutes,
    WindowBounds? Window,
    ThemePreference? Theme,
    bool? Transparent,
    bool? AlwaysOnTop,
    bool? MonitoringPaused);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PreferencesDto))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;
