namespace ArbetsWatch.Core.Presentation;

/// <summary>
/// Where the user stopped reading the list: one ad, remembered with its place in the sort order (publication
/// time, then ID). The place survives the ad being removed or filtered out, so the bookmark can always be found
/// again in whatever list is shown.
/// </summary>
public sealed record ReadingBookmark(string AdId, DateTimeOffset? PublishedUtc)
{
    /// <summary>
    /// Finds the bookmark in a list sorted like the ad list (newest publication first, missing publication times
    /// last, larger ID first on ties). Returns the bookmarked ad's index, or, when that ad is not in the list, the
    /// index of the first ad that sorts after it (the nearest one further down). Null for an empty list.
    /// </summary>
    public BookmarkPosition? Locate<T>(IReadOnlyList<T> rows, Func<T, string> id, Func<T, DateTimeOffset?> published)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var rowId = id(rows[i]);
            if (string.Equals(rowId, AdId, StringComparison.Ordinal))
            {
                return new BookmarkPosition(i, Exact: true);
            }

            if (SortsAfter(rowId, published(rows[i])))
            {
                return new BookmarkPosition(i, Exact: false);
            }
        }

        // Everything shown is newer than the bookmark: the end of the list is as close as it gets.
        return new BookmarkPosition(rows.Count - 1, Exact: false);
    }

    /// <summary>Whether an ad comes after the bookmark in the list order.</summary>
    private bool SortsAfter(string rowId, DateTimeOffset? rowPublished)
    {
        if (rowPublished != PublishedUtc)
        {
            // Missing publication times sort last.
            return rowPublished is null || (PublishedUtc is { } mine && rowPublished < mine);
        }

        return string.CompareOrdinal(rowId, AdId) < 0;
    }
}

/// <summary>A bookmark's place in a list. <see cref="Exact"/> is false when the bookmarked ad itself is not shown.</summary>
public sealed record BookmarkPosition(int Index, bool Exact);
