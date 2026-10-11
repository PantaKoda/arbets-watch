using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Core.Settings;

namespace ArbetsWatch.Core.Tests;

public sealed class ReadingBookmarkTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // Sorted like the ad list: newest publication first, larger ID first on ties, missing times last.
    private static readonly (string Id, DateTimeOffset? Published)[] List =
    [
        ("90", T.AddHours(3)),
        ("80", T.AddHours(2)),
        ("71", T.AddHours(1)),
        ("70", T.AddHours(1)),
        ("60", T),
        ("50", null),
        ("40", null),
    ];

    private static BookmarkPosition? Locate(ReadingBookmark bookmark, params (string Id, DateTimeOffset? Published)[] rows) =>
        bookmark.Locate(rows, r => r.Id, r => r.Published);

    [Theory]
    [InlineData("90", 0)]
    [InlineData("70", 3)]
    [InlineData("40", 6)]
    public void Bookmarked_ad_is_found_where_it_is(string id, int index)
    {
        var published = List.Single(r => r.Id == id).Published;
        Assert.Equal(new BookmarkPosition(index, Exact: true), Locate(new ReadingBookmark(id, published), List));
    }

    [Fact]
    public void Missing_ad_points_to_the_next_ad_after_its_place()
    {
        // Between 80 and 71/70 in time.
        Assert.Equal(new BookmarkPosition(2, Exact: false), Locate(new ReadingBookmark("75", T.AddHours(1.5)), List));

        // Same time as 71 and 70: the ID decides. 705 sorts between them as text ("71" > "705" > "70").
        Assert.Equal(new BookmarkPosition(3, Exact: false), Locate(new ReadingBookmark("705", T.AddHours(1)), List));

        // Newer than everything: the top.
        Assert.Equal(new BookmarkPosition(0, Exact: false), Locate(new ReadingBookmark("99", T.AddDays(1)), List));
    }

    [Fact]
    public void Missing_ad_without_a_publication_time_lands_among_those_without_one()
    {
        Assert.Equal(new BookmarkPosition(6, Exact: false), Locate(new ReadingBookmark("45", null), List));
    }

    [Fact]
    public void Bookmark_older_than_everything_shown_points_to_the_end()
    {
        var newer = List.Take(5).ToArray();
        Assert.Equal(new BookmarkPosition(4, Exact: false), Locate(new ReadingBookmark("10", T.AddDays(-30)), newer));
    }

    [Fact]
    public void Empty_list_has_no_position()
    {
        Assert.Null(Locate(new ReadingBookmark("1", T)));
    }

    [Fact]
    public void An_ad_whose_time_changed_is_still_found_by_its_id()
    {
        // The bookmark remembers the old time; the ad was republished and moved up.
        Assert.Equal(new BookmarkPosition(1, Exact: true), Locate(new ReadingBookmark("80", T.AddHours(-5)), List));
    }

    [Fact]
    public void Bookmark_is_saved_with_the_preferences()
    {
        Assert.Null(PreferencesStore.Deserialize(null).Bookmark);
        Assert.Null(PreferencesStore.Deserialize("{}").Bookmark);
        Assert.Null(PreferencesStore.Deserialize("""{"bookmark":{"adId":"","publishedUtc":null}}""").Bookmark);

        var bookmark = new ReadingBookmark("31578159", T);
        var json = PreferencesStore.Serialize(new AppPreferences { Bookmark = bookmark });
        Assert.Equal(bookmark, PreferencesStore.Deserialize(json).Bookmark);

        var untimed = new ReadingBookmark("5", null);
        Assert.Equal(untimed, PreferencesStore.Deserialize(PreferencesStore.Serialize(new AppPreferences { Bookmark = untimed })).Bookmark);
    }
}
