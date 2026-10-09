using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Storage;

/// <summary>
/// Explicit, ordered schema migrations tracked with <c>PRAGMA user_version</c>. Instants are stored as
/// Unix milliseconds (UTC); original source text is kept alongside where it matters for diagnostics.
/// </summary>
internal static class Schema
{
    private const string SummaryColumns = """
        id TEXT NOT NULL PRIMARY KEY,
        headline TEXT NOT NULL,
        employer TEXT,
        url TEXT,
        country_id TEXT,
        region_id TEXT,
        region_label TEXT,
        municipality_id TEXT,
        municipality_label TEXT,
        worktime_id TEXT,
        worktime_label TEXT,
        published_utc INTEGER,
        published_raw TEXT,
        last_publication_utc INTEGER,
        last_publication_raw TEXT,
        changed_utc INTEGER NOT NULL
        """;

    private static readonly string[] Migrations =
    [
        // 1: initial schema
        $"""
        CREATE TABLE ad_summary (
            {SummaryColumns}
        );
        CREATE INDEX ix_summary_order ON ad_summary (published_utc DESC, id DESC);
        CREATE INDEX ix_summary_country ON ad_summary (country_id);
        CREATE INDEX ix_summary_region ON ad_summary (region_id);
        CREATE INDEX ix_summary_municipality ON ad_summary (municipality_id);
        CREATE INDEX ix_summary_last_publication ON ad_summary (last_publication_utc);

        -- Read state and the last applied source state per ID. Kept after an ad becomes inactive so that
        -- older replays are rejected and read state survives a return; pruned after the retention period.
        CREATE TABLE ad_state (
            id TEXT NOT NULL PRIMARY KEY,
            first_seen_utc INTEGER,              -- first sighting as an ad; null while known only from a removal
            unread INTEGER NOT NULL DEFAULT 0,
            changed_utc INTEGER NOT NULL,
            changed_kind TEXT NOT NULL,          -- 'ad' (millisecond timestamp) or 'removal' (whole seconds)
            inactive_since_utc INTEGER,          -- null while the ad is current
            inactive_reason TEXT                 -- 'removed' | 'expired' | 'absent'
        );
        CREATE INDEX ix_state_unread ON ad_state (unread) WHERE unread = 1;
        CREATE INDEX ix_state_inactive ON ad_state (inactive_since_utc) WHERE inactive_since_utc IS NOT NULL;

        CREATE TABLE sync_state (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            committed_through_utc INTEGER,
            last_success_utc INTEGER,
            last_snapshot_utc INTEGER,
            baseline_established INTEGER NOT NULL DEFAULT 0,
            time_adapter_version INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO sync_state (id) VALUES (1);

        CREATE TABLE preferences (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- Snapshot staging, separate from the active dataset. removed = 1 marks a replayed removal.
        CREATE TABLE snapshot_staging (
            {SummaryColumns},
            removed INTEGER NOT NULL DEFAULT 0
        );
        """,

        // 2: ad_state.first_seen_utc becomes nullable in every database. Pre-release builds created v1 with NOT NULL,
        //    which made recording a removal for an unknown ID fail every refresh. SQLite can't drop a column
        //    constraint, so the table is rebuilt (cheap: one row per ad).
        """
        CREATE TABLE ad_state_v2 (
            id TEXT NOT NULL PRIMARY KEY,
            first_seen_utc INTEGER,
            unread INTEGER NOT NULL DEFAULT 0,
            changed_utc INTEGER NOT NULL,
            changed_kind TEXT NOT NULL,
            inactive_since_utc INTEGER,
            inactive_reason TEXT
        );
        INSERT INTO ad_state_v2 (id, first_seen_utc, unread, changed_utc, changed_kind, inactive_since_utc, inactive_reason)
        SELECT id, first_seen_utc, unread, changed_utc, changed_kind, inactive_since_utc, inactive_reason FROM ad_state;
        DROP TABLE ad_state;
        ALTER TABLE ad_state_v2 RENAME TO ad_state;
        CREATE INDEX ix_state_unread ON ad_state (unread) WHERE unread = 1;
        CREATE INDEX ix_state_inactive ON ad_state (inactive_since_utc) WHERE inactive_since_utc IS NOT NULL;
        """,

        // 3: text search. ad_text holds the folded title + description of each current ad (TextSearch.Body); it
        //    lives and dies with its ad_summary row. Existing caches have no descriptions yet, so the next refresh
        //    downloads a snapshot (last_snapshot_utc NULL); titles are searchable at once (AdStore.Initialize).
        """
        CREATE TABLE ad_text (
            id TEXT NOT NULL PRIMARY KEY,
            body TEXT NOT NULL
        );
        CREATE TRIGGER tr_summary_delete_text AFTER DELETE ON ad_summary
        BEGIN
            DELETE FROM ad_text WHERE id = old.id;
        END;
        DELETE FROM snapshot_staging;
        ALTER TABLE snapshot_staging ADD COLUMN body TEXT;
        UPDATE sync_state SET last_snapshot_utc = NULL;
        """,

        // 4: saved ads. A saved ad keeps its own copy of the summary, so it stays listed (as no longer published)
        //    after Platsbanken removes it, until the user removes it; retention never prunes it. Triggers keep the
        //    copy in step while the ad is current.
        """
        CREATE TABLE saved_ad (
            id TEXT NOT NULL PRIMARY KEY,
            headline TEXT NOT NULL,
            employer TEXT,
            url TEXT,
            country_id TEXT,
            region_id TEXT,
            region_label TEXT,
            municipality_id TEXT,
            municipality_label TEXT,
            worktime_id TEXT,
            worktime_label TEXT,
            published_utc INTEGER,
            published_raw TEXT,
            last_publication_utc INTEGER,
            last_publication_raw TEXT,
            changed_utc INTEGER NOT NULL,
            saved_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_saved_order ON saved_ad (saved_utc DESC, id DESC);
        CREATE TRIGGER tr_summary_update_saved AFTER UPDATE ON ad_summary
        BEGIN
            UPDATE saved_ad SET headline = new.headline, employer = new.employer, url = new.url, country_id = new.country_id,
                region_id = new.region_id, region_label = new.region_label, municipality_id = new.municipality_id,
                municipality_label = new.municipality_label, worktime_id = new.worktime_id, worktime_label = new.worktime_label,
                published_utc = new.published_utc, published_raw = new.published_raw,
                last_publication_utc = new.last_publication_utc, last_publication_raw = new.last_publication_raw,
                changed_utc = new.changed_utc
            WHERE id = new.id;
        END;
        CREATE TRIGGER tr_summary_insert_saved AFTER INSERT ON ad_summary
        BEGIN
            UPDATE saved_ad SET headline = new.headline, employer = new.employer, url = new.url, country_id = new.country_id,
                region_id = new.region_id, region_label = new.region_label, municipality_id = new.municipality_id,
                municipality_label = new.municipality_label, worktime_id = new.worktime_id, worktime_label = new.worktime_label,
                published_utc = new.published_utc, published_raw = new.published_raw,
                last_publication_utc = new.last_publication_utc, last_publication_raw = new.last_publication_raw,
                changed_utc = new.changed_utc
            WHERE id = new.id;
        END;
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public static void Migrate(SqliteConnection connection)
    {
        var current = UserVersion(connection);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"The database was created by a newer ArbetsWatch (schema {current}; this version supports {LatestVersion}).");
        }

        for (var version = current + 1; version <= LatestVersion; version++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[version - 1] + $"\nPRAGMA user_version = {version};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public static int UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
