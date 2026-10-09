using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;
using Microsoft.Data.Sqlite;
using static ArbetsWatch.Core.Tests.TempStore;

namespace ArbetsWatch.Core.Tests;

public sealed class SearchTests
{
    private const string Malmo = "oYPt_yRA_Smm";

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("Utvecklare", new[] { "utvecklare" })]
    [InlineData("  C#   java ", new[] { "c#", "java" })]
    [InlineData("ÖVERSÄTTARE", new[] { "översättare" })]
    [InlineData("\"hela  dagen\" sjuksköterska", new[] { "hela dagen", "sjuksköterska" })]
    [InlineData("java JAVA Java", new[] { "java" })]
    [InlineData("\"open phrase", new[] { "open phrase" })]
    [InlineData("\"\"", new string[0])]
    public void Input_becomes_folded_terms(string input, string[] expected) =>
        Assert.Equal(expected, TextSearch.Parse(input).Terms);

    [Fact]
    public void Composed_and_decomposed_letters_match()
    {
        var decomposed = "Södertälje"; // "Södertälje" typed with combining marks
        Assert.True(TextSearch.Parse("Södertälje").Matches(TextSearch.Body(decomposed, null)));
    }

    [Fact]
    public void Long_input_is_bounded()
    {
        var search = TextSearch.Parse(string.Join(' ', Enumerable.Range(0, 100).Select(i => $"w{i}")));
        Assert.Equal(TextSearch.MaxTerms, search.Terms.Count);
        Assert.Equal(TextSearch.MaxLength, TextSearch.Parse(new string('a', 1000)).Terms.Single().Length);
    }

    [Fact]
    public void Sql_and_memory_forms_agree()
    {
        string[] bodies =
        [
            TextSearch.Body("Systemutvecklare", "Vi söker en utvecklare med C# och Java."),
            TextSearch.Body("Sjuksköterska", "Arbete hela dagen,\nmåndag–fredag."),
            TextSearch.Body("Lagerarbetare", null),
            TextSearch.Body("Översättare", "100 % distans"),
        ];
        string[] queries = ["", "utvecklare", "c# java", "java python", "\"hela dagen\"", "hela fredag", "ÖVERS", "100 %", "lager"];

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, body TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO t VALUES ($id, $body)";
            insert.Parameters.AddWithValue("$id", i);
            insert.Parameters.AddWithValue("$body", bodies[i]);
            insert.ExecuteNonQuery();
        }

        foreach (var query in queries)
        {
            var search = TextSearch.Parse(query);
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT id FROM t WHERE {search.Where(select, "t.body")} ORDER BY id";
            var sql = new List<long>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    sql.Add(reader.GetInt64(0));
                }
            }

            var memory = Enumerable.Range(0, bodies.Length).Where(i => search.Matches(bodies[i])).Select(i => (long)i);
            Assert.Equal(memory, sql);
        }
    }

    [Fact]
    public void Parser_keeps_the_plain_text_description()
    {
        var ad = Assert.IsType<AdSummary>(AdRecordParser.Parse(Fixtures.Read("active-ad.json"), Fixtures.Fallback));
        Assert.Equal("Synthetic description for tests.", ad.Description);
    }

    [Fact]
    public async Task Title_and_description_are_searched_within_the_filters()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(
            Fixtures.Ad("1", headline: "Systemutvecklare") with { Description = "Backend i C#." },
            Fixtures.Ad("2", headline: "Lärare") with { Description = "Undervisning i programmering och C#." },
            Fixtures.Ad("3", headline: "Utvecklare", municipalityId: Malmo, regionId: "CaRE_1nn_cSU") with { Description = "C# i Malmö." },
            Fixtures.Ad("4", headline: "Kock"));

        Assert.Equal(["1", "3"], await Ids(temp, AdFilter.Default, "utvecklare"));
        Assert.Equal(["1", "2", "3"], await Ids(temp, AdFilter.Default, "c#"));
        Assert.Equal(["2"], await Ids(temp, AdFilter.Default, "\"programmering och\""));

        var goteborg = new AdFilter { MunicipalityIds = new HashSet<string> { "PVZL_BQT_XtL" } };
        Assert.Equal(["1", "2"], await Ids(temp, goteborg, "C#"));
        Assert.Equal(["1"], await Ids(temp, goteborg with { Worktime = WorktimeSet.FullTime }, "UTVECKLARE"));
        Assert.Empty(await Ids(temp, goteborg, "malmö"));
        Assert.Equal(["1", "2", "4"], await Ids(temp, goteborg, "  "));
    }

    [Fact]
    public async Task Updates_replace_the_text_and_gone_ads_take_it_with_them()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1") with { Description = "Gammal text" }, Fixtures.Ad("2"), Fixtures.Ad("3"));
        var end = T0.AddMinutes(5);
        await temp.Store.CommitBatchAsync(
            [
                Fixtures.Ad("1", changed: T0.AddMinutes(1)) with { Description = "Ny text" },
                Fixtures.Removal("2", T0.AddMinutes(1)),
                Fixtures.Ad("3", changed: T0.AddMinutes(1), lastPublication: T0.AddMinutes(2)),
                Fixtures.Ad("4", changed: T0.AddMinutes(1)) with { Description = "Ny text" },
            ],
            AdFilter.Default, end, end);

        Assert.Empty(await Ids(temp, AdFilter.Default, "gammal", end));
        Assert.Equal(["1", "4"], await Ids(temp, AdFilter.Default, "ny text", end));
        Assert.Equal(["1", "4"], TextIds(temp.DatabasePath)); // removal and expiry dropped 2 and 3

        // Search hides ads but never decides what is new.
        var rows = await temp.Store.QueryAsync(AdFilter.Default, end, search: TextSearch.Parse("text"));
        Assert.True(rows.Single(r => r.Ad.Id == "4").Unread);
    }

    [Fact]
    public async Task Snapshot_activation_replaces_text_and_drops_absent_ads()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1") with { Description = "före" }, Fixtures.Ad("2") with { Description = "före" });

        var start = T0.AddDays(1);
        await temp.Store.ResetStagingAsync();
        await temp.Store.StageSnapshotChunkAsync([Fixtures.Ad("1", changed: start.AddMinutes(-1)) with { Description = "efter" }]);
        await temp.Store.ReplayIntoStagingAsync([Fixtures.Ad("5", changed: start.AddMinutes(1)) with { Description = "efter" }]);
        await temp.Store.ActivateSnapshotAsync(AdFilter.Default, start, start.AddMinutes(5), start.AddMinutes(5));

        Assert.Empty(await Ids(temp, AdFilter.Default, "före", start));
        Assert.Equal(["1", "5"], await Ids(temp, AdFilter.Default, "efter", start));
        Assert.Equal(["1", "5"], TextIds(temp.DatabasePath));
    }

    [Fact]
    public async Task Cache_from_before_search_gets_searchable_titles_and_a_new_snapshot()
    {
        using var temp = new TempStore();
        await temp.BaselineAsync(Fixtures.Ad("1", headline: "Systemutvecklare"), Fixtures.Ad("2", headline: "Kock"));
        temp.Store.Dispose();

        // Back to the schema 2 shape, as written by 0.1.2.
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER tr_summary_update_saved;
                DROP TRIGGER tr_summary_insert_saved;
                DROP TABLE saved_ad;
                DROP TRIGGER tr_summary_delete_text;
                DROP TABLE ad_text;
                ALTER TABLE snapshot_staging DROP COLUMN body;
                UPDATE sync_state SET last_snapshot_utc = 1;
                PRAGMA user_version = 2;
                """;
            downgrade.ExecuteNonQuery();
        }

        var store = temp.Reopen();
        Assert.Equal(["1"], await Ids(temp, AdFilter.Default, "UTVECKLARE"));
        Assert.Null((await store.ReadSyncStateAsync()).LastSnapshotUtc);
        Assert.False((await store.QueryAsync(AdFilter.Default, T0)).Single(r => r.Ad.Id == "1").Unread);
    }

    [Fact]
    public async Task Space_left_by_snapshot_staging_is_given_back()
    {
        using var temp = new TempStore();
        var text = new string('x', 4000);
        await temp.BaselineAsync([.. Enumerable.Range(0, 300).Select(i => Fixtures.Ad($"{i}") with { Description = text })]);
        Assert.True(Pragma(temp.DatabasePath, "freelist_count") > 0); // staging was cleared

        await temp.Store.ReclaimSpaceAsync();

        Assert.Equal(2, Pragma(temp.DatabasePath, "auto_vacuum")); // incremental
        Assert.Equal(0, Pragma(temp.DatabasePath, "freelist_count"));
        Assert.Equal(300, (await temp.Store.QueryAsync(AdFilter.Default, T0, search: TextSearch.Parse("xxx"))).Count);
    }

    private static long Pragma(string databasePath, string name)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<List<string>> Ids(TempStore temp, AdFilter filter, string query, DateTimeOffset? now = null) =>
        [.. (await temp.Store.QueryAsync(filter, now ?? T0, search: TextSearch.Parse(query))).Select(r => r.Ad.Id).Order(StringComparer.Ordinal)];

    private static List<string> TextIds(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM ad_text ORDER BY id";
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
