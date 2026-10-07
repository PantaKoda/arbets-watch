using System.Globalization;
using System.Text.Json;
using ArbetsWatch.Core.Time;

namespace ArbetsWatch.Core.Ads;

/// <summary>
/// Projects one JSON-lines record from JobStream into a compact <see cref="SourceRecord"/>.
/// Other large fields are never materialized beyond the per-line document; the plain-text description is kept
/// for search.
/// </summary>
public static class AdRecordParser
{
    /// <summary>Parses one line. Throws <see cref="AdParseException"/> for malformed records.</summary>
    /// <param name="line">One JSON object.</param>
    /// <param name="unorderedFallbackUtc">
    /// The change instant for a record whose own one is missing or unreadable: the end of the requested interval
    /// (or the snapshot start). The record then counts as the newest state and is applied rather than rejected
    /// on invented ordering data (docs/api-contracts.md, "Ordering of states").
    /// </param>
    public static SourceRecord Parse(string line, DateTimeOffset unorderedFallbackUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return Parse(document.RootElement, unorderedFallbackUtc);
        }
        catch (JsonException ex)
        {
            throw new AdParseException("A record is not valid JSON.", ex);
        }
    }

    public static SourceRecord Parse(JsonElement ad, DateTimeOffset unorderedFallbackUtc)
    {
        if (ad.ValueKind != JsonValueKind.Object)
        {
            throw new AdParseException($"Expected a JSON object, found {ad.ValueKind}.");
        }

        var id = Id(ad);
        if (ad.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.True)
        {
            // The raw text is kept even when it can't be read, for diagnostics.
            var raw = Str(ad, "removed_date");
            return new AdRemoval(
                id,
                SwedishTime.ParseLocal(raw, laterInRepeatedHour: true) ?? unorderedFallbackUtc,
                raw,
                Str(ad, "country"),
                Str(ad, "region"),
                Str(ad, "municipality"));
        }

        var address = Obj(ad, "workplace_address");
        var worktime = Obj(ad, "working_hours_type");
        var published = Str(ad, "publication_date");
        var lastPublication = Str(ad, "last_publication_date");

        return new AdSummary(
            id,
            Headline: Str(ad, "headline") ?? string.Empty,
            Employer: Str(Obj(ad, "employer"), "name"),
            Url: Str(ad, "webpage_url"),
            CountryId: Str(address, "country_concept_id"),
            RegionId: Str(address, "region_concept_id"),
            RegionLabel: Str(address, "region"),
            MunicipalityId: Str(address, "municipality_concept_id"),
            MunicipalityLabel: Str(address, "municipality"),
            WorktimeId: Str(worktime, "concept_id"),
            WorktimeLabel: Str(worktime, "label"),
            PublishedUtc: SwedishTime.ParseLocal(published),
            PublishedRaw: published,
            LastPublicationUtc: SwedishTime.ParseLocal(lastPublication),
            LastPublicationRaw: lastPublication,
            // timestamp is epoch milliseconds (UTC). Without it the order is unknown, so the state is applied.
            ChangedUtc: Timestamp(ad) ?? unorderedFallbackUtc)
        {
            Description = Str(Obj(ad, "description"), "text"),
        };
    }

    private static string Id(JsonElement ad)
    {
        if (!ad.TryGetProperty("id", out var id))
        {
            throw new AdParseException("A record has no id.");
        }

        var text = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text)
            ? throw new AdParseException("A record has an empty or invalid id.")
            : text.Trim();
    }

    private static DateTimeOffset? Timestamp(JsonElement ad) =>
        ad.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class AdParseException : Exception
{
    public AdParseException(string message)
        : base(message)
    {
    }

    public AdParseException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public AdParseException()
    {
    }

    public static string Describe(long lineNumber, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"Line {lineNumber}: {message}");
}
