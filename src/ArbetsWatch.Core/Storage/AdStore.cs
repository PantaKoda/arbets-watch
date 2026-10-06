using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Time;
using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Storage;

/// <summary>
/// The local SQLite cache. One writer at a time (all mutations take <see cref="_writer"/>); reads use their own
/// pooled connections and see committed data only (WAL). Every public operation runs off the caller's thread.
/// </summary>
public sealed class AdStore : IDisposable
{
    private const string SummaryColumnList =
        "id, headline, employer, url, country_id, region_id, region_label, municipality_id, municipality_label, " +
        "worktime_id, worktime_label, published_utc, published_raw, last_publication_utc, last_publication_raw, changed_utc";

    private readonly string _connectionString;
    private readonly StorePolicy _policy;
    private readonly SemaphoreSlim _writer = new(1, 1);

    private AdStore(string connectionString, StorePolicy policy)
    {
        _connectionString = connectionString;
        _policy = policy;
    }

    public static AdStore Open(string databasePath, StorePolicy? policy = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();

        var store = new AdStore(connectionString, policy ?? new StorePolicy());
        using var connection = store.Connect();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        Schema.Migrate(connection);
        return store;
    }

    // ---- Sync state -------------------------------------------------------------------------------------

    public Task<SyncState> ReadSyncStateAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = Connect();
            return ReadSyncState(connection, null);
        }, cancellationToken);

    // ---- Incremental updates ----------------------------------------------------------------------------

    /// <summary>
    /// Applies one complete stream interval and advances the checkpoint to <paramref name="checkpointEnd"/> in
    /// the same transaction. On any failure nothing is committed and the checkpoint is unchanged.
    /// </summary>
    public Task<ApplyResult> CommitBatchAsync(
        IReadOnlyList<SourceRecord> records,
        AdFilter filter,
        DateTimeOffset checkpointEnd,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            var sync = ReadSyncState(connection, transaction);
            var counters = new Counters();
            ApplyRecords(connection, transaction, records, filter, sync.BaselineEstablished, Ms(now), counters, cancellationToken);
            counters.Expired = Expire(connection, transaction, Ms(now));
            counters.Pruned = Prune(connection, transaction, Ms(now));
            Execute(connection, transaction,
                "UPDATE sync_state SET committed_through_utc = $end, last_success_utc = $now, time_adapter_version = $tv WHERE id = 1",
                ("$end", Ms(checkpointEnd)), ("$now", Ms(now)), ("$tv", SwedishTime.AdapterVersion));
            return counters.ToResult();
        }, cancellationToken);

    // ---- Snapshot staging -------------------------------------------------------------------------------

    public Task ResetStagingAsync(CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction, "DELETE FROM snapshot_staging");
            return 0;
        }, cancellationToken);

    /// <summary>Adds snapshot ads to staging in one bounded transaction. The active dataset is untouched.</summary>
    public Task StageSnapshotChunkAsync(IReadOnlyList<AdSummary> ads, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            using var command = StagingUpsert(connection, transaction);
            foreach (var ad in ads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindSummary(command, ad);
                command.Parameters["$removed"].Value = 0;
                command.ExecuteNonQuery();
            }

            return 0;
        }, cancellationToken);

    /// <summary>
    /// Replays stream records that overlap the snapshot download into staging. Older states never overwrite
    /// newer staged ones; removals become staged tombstones.
    /// </summary>
    public Task ReplayIntoStagingAsync(IReadOnlyList<SourceRecord> records, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            using var command = StagingUpsert(connection, transaction);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (record)
                {
                    case AdSummary ad:
                        BindSummary(command, ad);
                        command.Parameters["$removed"].Value = 0;
                        break;
                    case AdRemoval removal:
                        BindSummary(command, Tombstone(removal));
                        command.Parameters["$removed"].Value = 1;
                        break;
                    default:
                        continue;
                }

                command.ExecuteNonQuery();
            }

            return 0;
        }, cancellationToken);

    /// <summary>
    /// Makes the staged snapshot the active dataset and sets the checkpoint, in one transaction. Read state is
    /// kept for known IDs. On the first-ever activation no ad becomes unread (baseline); afterwards, IDs never
    /// seen as an ad before become unread when they match <paramref name="filter"/>. Throws
    /// <see cref="SnapshotRejectedException"/> (nothing changes) when staging is implausibly small.
    /// </summary>
    /// <param name="snapshotStart">Captured before the snapshot request; live rows newer than it are kept.</param>
    public Task<ApplyResult> ActivateSnapshotAsync(
        AdFilter filter,
        DateTimeOffset snapshotStart,
        DateTimeOffset checkpointEnd,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            var sync = ReadSyncState(connection, transaction);
            var nowMs = Ms(now);
            var counters = new Counters();

            // An empty or much smaller snapshot than the cache is far more likely a truncated download than half
            // of Platsbanken vanishing; keep the previous cache (AGENTS.md section 7).
            var staged = Count(connection, transaction, "SELECT COUNT(*) FROM snapshot_staging WHERE removed = 0");
            var current = Count(connection, transaction,
                "SELECT COUNT(*) FROM ad_summary WHERE last_publication_utc IS NULL OR last_publication_utc >= $now", ("$now", nowMs));
            if (current > 0 && staged < current * _policy.MinSnapshotFraction)
            {
                throw new SnapshotRejectedException(staged, current);
            }

            // The snapshot is authoritative for membership (docs/api-contracts.md, "Ordering of states"): staged
            // rows apply whatever the stored state says. Current ads missing from it are gone, unless the cache
            // holds a state newer than the snapshot could reflect.
            counters.Absent = Execute(connection, transaction, """
                UPDATE ad_state SET inactive_since_utc = $now, inactive_reason = 'absent'
                WHERE inactive_since_utc IS NULL
                  AND id IN (SELECT a.id FROM ad_summary a
                             WHERE NOT EXISTS (SELECT 1 FROM snapshot_staging s WHERE s.id = a.id)
                               AND a.changed_utc < $start)
                """, ("$now", nowMs), ("$start", Ms(snapshotStart)));

            // Staged removals (replayed over the download period).
            counters.Removed = Execute(connection, transaction, """
                UPDATE ad_state
                SET changed_utc = s.changed_utc, changed_kind = 'removal',
                    inactive_since_utc = COALESCE(ad_state.inactive_since_utc, $now),
                    inactive_reason = COALESCE(ad_state.inactive_reason, 'removed')
                FROM snapshot_staging s
                WHERE s.id = ad_state.id AND s.removed = 1
                """, ("$now", nowMs));
            Execute(connection, transaction, """
                INSERT INTO ad_state (id, first_seen_utc, unread, changed_utc, changed_kind, inactive_since_utc, inactive_reason)
                SELECT s.id, NULL, 0, s.changed_utc, 'removal', $now, 'removed'
                FROM snapshot_staging s
                WHERE s.removed = 1 AND NOT EXISTS (SELECT 1 FROM ad_state st WHERE st.id = s.id)
                """, ("$now", nowMs));

            // Staged ads: current, read state kept.
            Execute(connection, transaction, """
                UPDATE ad_state
                SET changed_utc = s.changed_utc, changed_kind = 'ad', inactive_since_utc = NULL, inactive_reason = NULL
                FROM snapshot_staging s
                WHERE s.id = ad_state.id AND s.removed = 0
                """);

            // First sighting as an ad: IDs never seen, or seen only as removals. Unread after the baseline when they
            // match and are not already expired (the same rule as ApplyRecords).
            using (var firstAd = connection.CreateCommand())
            {
                firstAd.Transaction = transaction;
                firstAd.CommandText = $"""
                    UPDATE ad_state
                    SET first_seen_utc = $now, unread = {UnreadCase(filter, firstAd)}
                    FROM snapshot_staging s
                    WHERE s.id = ad_state.id AND s.removed = 0 AND ad_state.first_seen_utc IS NULL
                    """;
                firstAd.Parameters.AddWithValue("$now", nowMs);
                firstAd.Parameters.AddWithValue("$unreadAllowed", sync.BaselineEstablished ? 1 : 0);
                firstAd.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"""
                    INSERT INTO ad_state (id, first_seen_utc, unread, changed_utc, changed_kind)
                    SELECT s.id, $now, {UnreadCase(filter, insert)}, s.changed_utc, 'ad'
                    FROM snapshot_staging s
                    WHERE s.removed = 0 AND NOT EXISTS (SELECT 1 FROM ad_state st WHERE st.id = s.id)
                    """;
                insert.Parameters.AddWithValue("$now", nowMs);
                insert.Parameters.AddWithValue("$unreadAllowed", sync.BaselineEstablished ? 1 : 0);
                insert.ExecuteNonQuery();
            }

            counters.Applied = Execute(connection, transaction, $"""
                INSERT INTO ad_summary ({SummaryColumnList})
                SELECT {Prefixed("s")}
                FROM snapshot_staging s
                WHERE s.removed = 0
                ON CONFLICT (id) DO UPDATE SET {UpdateAssignments()}
                """);

            Execute(connection, transaction, """
                DELETE FROM ad_summary
                WHERE EXISTS (SELECT 1 FROM ad_state st WHERE st.id = ad_summary.id AND st.inactive_since_utc IS NOT NULL)
                """);

            counters.Expired = Expire(connection, transaction, nowMs);
            counters.Pruned = Prune(connection, transaction, nowMs);
            counters.NewUnread = Convert.ToInt32(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM ad_state WHERE first_seen_utc = $now AND unread = 1", ("$now", nowMs)),
                System.Globalization.CultureInfo.InvariantCulture);

            Execute(connection, transaction, """
                UPDATE sync_state
                SET committed_through_utc = $end, last_success_utc = $now, last_snapshot_utc = $start,
                    baseline_established = 1, time_adapter_version = $tv
                WHERE id = 1
                """, ("$end", Ms(checkpointEnd)), ("$now", nowMs), ("$start", Ms(snapshotStart)), ("$tv", SwedishTime.AdapterVersion));
            Execute(connection, transaction, "DELETE FROM snapshot_staging");
            return counters.ToResult();
        }, cancellationToken);

    // ---- Queries ----------------------------------------------------------------------------------------

    /// <summary>Current ads matching the filter, newest publication first, ID as a stable tie-breaker.</summary>
    public Task<IReadOnlyList<AdRow>> QueryAsync(AdFilter filter, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AdRow>>(() =>
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            var where = AdFilterSql.Where(filter, command, "s");
            command.CommandText = $"""
                SELECT {Prefixed("s")}, st.unread, st.first_seen_utc
                FROM ad_summary s JOIN ad_state st ON st.id = s.id
                WHERE ({where}) AND (s.last_publication_utc IS NULL OR s.last_publication_utc >= $now)
                ORDER BY s.published_utc DESC, s.id DESC
                """;
            command.Parameters.AddWithValue("$now", Ms(now));

            var rows = new List<AdRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new AdRow(ReadSummary(reader), reader.GetInt64(16) == 1, reader.IsDBNull(17) ? now : FromMs(reader.GetInt64(17))));
            }

            return rows;
        }, cancellationToken);

    public Task<int> CountCurrentAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = Connect();
            return Convert.ToInt32(Scalar(connection, null,
                "SELECT COUNT(*) FROM ad_summary WHERE last_publication_utc IS NULL OR last_publication_utc >= $now",
                ("$now", Ms(now))), System.Globalization.CultureInfo.InvariantCulture);
        }, cancellationToken);

    // ---- Read state -------------------------------------------------------------------------------------

    public Task MarkReadAsync(string id, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
            Execute(connection, transaction, "UPDATE ad_state SET unread = 0 WHERE id = $id", ("$id", id)),
            cancellationToken);

    /// <summary>Clears unread for the ads currently matching <paramref name="filter"/> only.</summary>
    public Task<int> MarkMatchingReadAsync(AdFilter filter, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var where = AdFilterSql.Where(filter, command, "s");
            command.CommandText = $"""
                UPDATE ad_state SET unread = 0
                WHERE unread = 1 AND id IN (
                    SELECT s.id FROM ad_summary s
                    WHERE ({where}) AND (s.last_publication_utc IS NULL OR s.last_publication_utc >= $now))
                """;
            command.Parameters.AddWithValue("$now", Ms(now));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    // ---- Preferences ------------------------------------------------------------------------------------

    public Task<string?> GetPreferenceAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = Connect();
            return Scalar(connection, null, "SELECT value FROM preferences WHERE key = $key", ("$key", key)) as string;
        }, cancellationToken);

    public Task SetPreferenceAsync(string key, string value, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction) =>
            Execute(connection, transaction,
                "INSERT INTO preferences (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                ("$key", key), ("$value", value)),
            cancellationToken);

    public void Dispose()
    {
        _writer.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    // ---- Internals --------------------------------------------------------------------------------------

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Per connection (unlike journal_mode, which is stored in the file): safe with WAL, fewer fsyncs.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>The unread decision for a staged row aliased <c>s</c>, as SQL (needs <c>$now</c> and <c>$unreadAllowed</c>).</summary>
    private static string UnreadCase(AdFilter filter, SqliteCommand command) =>
        $"CASE WHEN $unreadAllowed = 1 AND {AdFilterSql.Where(filter, command, "s")} " +
        "AND (s.last_publication_utc IS NULL OR s.last_publication_utc >= $now) THEN 1 ELSE 0 END";

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(Scalar(connection, transaction, sql, parameters), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Connect();
                using var transaction = connection.BeginTransaction();
                var result = work(connection, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    private void ApplyRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SourceRecord> records,
        AdFilter filter,
        bool unreadAllowed,
        long nowMs,
        Counters counters,
        CancellationToken cancellationToken)
    {
        using var readState = Command(connection, transaction,
            "SELECT changed_utc, changed_kind, first_seen_utc FROM ad_state WHERE id = $id", "$id");
        using var upsertSummary = Command(connection, transaction, $"""
            INSERT INTO ad_summary ({SummaryColumnList}) VALUES ({ParameterList()})
            ON CONFLICT (id) DO UPDATE SET {UpdateAssignments()}
            """, SummaryParameters());
        using var deleteSummary = Command(connection, transaction, "DELETE FROM ad_summary WHERE id = $id", "$id");
        using var insertState = Command(connection, transaction, """
            INSERT INTO ad_state (id, first_seen_utc, unread, changed_utc, changed_kind, inactive_since_utc, inactive_reason)
            VALUES ($id, $now, $unread, $changed, $kind, $inactive, $reason)
            """, "$id", "$now", "$unread", "$changed", "$kind", "$inactive", "$reason");
        using var firstAd = Command(connection, transaction,
            "UPDATE ad_state SET first_seen_utc = $now, unread = $unread WHERE id = $id", "$id", "$now", "$unread");
        using var markActive = Command(connection, transaction, """
            UPDATE ad_state SET changed_utc = $changed, changed_kind = 'ad', inactive_since_utc = NULL, inactive_reason = NULL
            WHERE id = $id
            """, "$id", "$changed");
        using var markRemoved = Command(connection, transaction, """
            UPDATE ad_state
            SET changed_utc = $changed, changed_kind = 'removal',
                inactive_since_utc = COALESCE(inactive_since_utc, $now), inactive_reason = COALESCE(inactive_reason, 'removed')
            WHERE id = $id
            """, "$id", "$changed", "$now");

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var incomingKind = record is AdRemoval ? SourceOrder.RemovalKind : SourceOrder.AdKind;
            var incomingMs = Ms(record.SourceChangedUtc);

            readState.Parameters["$id"].Value = record.Id;
            long? storedMs = null;
            string? storedKind = null;
            var seenAsAd = false;
            using (var reader = readState.ExecuteReader())
            {
                if (reader.Read())
                {
                    storedMs = reader.GetInt64(0);
                    storedKind = reader.GetString(1);
                    seenAsAd = !reader.IsDBNull(2);
                }
            }

            if (storedMs is { } stored && SourceOrder.IsOlder(incomingMs, incomingKind, stored, storedKind!))
            {
                counters.Stale++;
                continue;
            }

            switch (record)
            {
                case AdSummary ad:
                    BindSummary(upsertSummary, ad);
                    upsertSummary.ExecuteNonQuery();

                    // "New" is decided on the first sighting as an ad; an earlier removal-only row doesn't count.
                    var unread = !seenAsAd && unreadAllowed && AdMatcher.Matches(filter, ad) && !ad.IsExpiredAt(FromMs(nowMs));
                    if (storedMs is null)
                    {
                        Bind(insertState, ("$id", ad.Id), ("$now", nowMs), ("$unread", unread ? 1 : 0),
                            ("$changed", incomingMs), ("$kind", SourceOrder.AdKind), ("$inactive", null), ("$reason", null));
                        insertState.ExecuteNonQuery();
                    }
                    else
                    {
                        Bind(markActive, ("$id", ad.Id), ("$changed", incomingMs));
                        markActive.ExecuteNonQuery();
                        if (!seenAsAd)
                        {
                            Bind(firstAd, ("$id", ad.Id), ("$now", nowMs), ("$unread", unread ? 1 : 0));
                            firstAd.ExecuteNonQuery();
                        }
                    }

                    if (unread)
                    {
                        counters.NewUnread++;
                    }

                    counters.Applied++;
                    break;

                case AdRemoval removal:
                    Bind(deleteSummary, ("$id", removal.Id));
                    var deleted = deleteSummary.ExecuteNonQuery();
                    if (storedMs is null)
                    {
                        // Known only as a removal: no first sighting as an ad yet (first_seen_utc stays null).
                        Bind(insertState, ("$id", removal.Id), ("$now", null), ("$unread", 0), ("$changed", incomingMs),
                            ("$kind", SourceOrder.RemovalKind), ("$inactive", nowMs), ("$reason", "removed"));
                        insertState.ExecuteNonQuery();
                    }
                    else
                    {
                        Bind(markRemoved, ("$id", removal.Id), ("$changed", incomingMs), ("$now", nowMs));
                        markRemoved.ExecuteNonQuery();
                    }

                    counters.Removed += deleted;
                    break;
            }
        }
    }

    /// <summary>Removes ads whose last publication instant has passed; their state is kept as inactive.</summary>
    private static int Expire(SqliteConnection connection, SqliteTransaction transaction, long nowMs)
    {
        Execute(connection, transaction, """
            UPDATE ad_state SET inactive_since_utc = $now, inactive_reason = 'expired'
            WHERE inactive_since_utc IS NULL
              AND id IN (SELECT id FROM ad_summary WHERE last_publication_utc < $now)
            """, ("$now", nowMs));
        return Execute(connection, transaction, "DELETE FROM ad_summary WHERE last_publication_utc < $now", ("$now", nowMs));
    }

    private int Prune(SqliteConnection connection, SqliteTransaction transaction, long nowMs) =>
        Execute(connection, transaction,
            "DELETE FROM ad_state WHERE inactive_since_utc IS NOT NULL AND inactive_since_utc < $cutoff",
            ("$cutoff", nowMs - (long)_policy.InactiveRetention.TotalMilliseconds));

    private static SyncState ReadSyncState(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT committed_through_utc, last_success_utc, last_snapshot_utc, baseline_established, time_adapter_version
            FROM sync_state WHERE id = 1
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new SyncState(
            OptionalMs(reader, 0),
            OptionalMs(reader, 1),
            OptionalMs(reader, 2),
            reader.GetInt64(3) == 1,
            reader.GetInt32(4));
    }

    private static SqliteCommand StagingUpsert(SqliteConnection connection, SqliteTransaction transaction) =>
        Command(connection, transaction, $"""
            INSERT INTO snapshot_staging ({SummaryColumnList}, removed) VALUES ({ParameterList()}, $removed)
            ON CONFLICT (id) DO UPDATE SET {UpdateAssignments()}, removed = excluded.removed
            WHERE NOT ((excluded.removed = 0 AND snapshot_staging.removed = 0 AND excluded.changed_utc < snapshot_staging.changed_utc)
                    OR ((excluded.removed = 1 OR snapshot_staging.removed = 1) AND excluded.changed_utc / 1000 < snapshot_staging.changed_utc / 1000))
            """, [.. SummaryParameters(), "$removed"]);

    private static AdSummary Tombstone(AdRemoval removal) =>
        new(removal.Id, string.Empty, null, null, removal.CountryId, removal.RegionId, null, removal.MunicipalityId, null,
            null, null, null, null, null, null, removal.RemovedUtc);

    private static string[] SummaryParameters() =>
        ["$id", "$headline", "$employer", "$url", "$country", "$region", "$regionLabel", "$municipality", "$municipalityLabel",
         "$worktime", "$worktimeLabel", "$published", "$publishedRaw", "$lastPublication", "$lastPublicationRaw", "$changed"];

    private static string ParameterList() => string.Join(", ", SummaryParameters());

    private static string Prefixed(string alias) =>
        string.Join(", ", SummaryColumnList.Split(", ").Select(c => $"{alias}.{c}"));

    private static string UpdateAssignments() =>
        string.Join(", ", SummaryColumnList.Split(", ").Skip(1).Select(c => $"{c} = excluded.{c}"));

    private static void BindSummary(SqliteCommand command, AdSummary ad) =>
        Bind(command,
            ("$id", ad.Id), ("$headline", ad.Headline), ("$employer", ad.Employer), ("$url", ad.Url),
            ("$country", ad.CountryId), ("$region", ad.RegionId), ("$regionLabel", ad.RegionLabel),
            ("$municipality", ad.MunicipalityId), ("$municipalityLabel", ad.MunicipalityLabel),
            ("$worktime", ad.WorktimeId), ("$worktimeLabel", ad.WorktimeLabel),
            ("$published", ad.PublishedUtc is { } p ? Ms(p) : null), ("$publishedRaw", ad.PublishedRaw),
            ("$lastPublication", ad.LastPublicationUtc is { } l ? Ms(l) : null), ("$lastPublicationRaw", ad.LastPublicationRaw),
            ("$changed", Ms(ad.ChangedUtc)));

    private static AdSummary ReadSummary(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8), Text(r, 9), Text(r, 10),
            OptionalMs(r, 11), Text(r, 12), OptionalMs(r, 13), Text(r, 14),
            FromMs(r.GetInt64(15)));

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptionalMs(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : FromMs(r.GetInt64(i));

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameters)
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        command.Prepare();
        return command;
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            command.Parameters[name].Value = value ?? DBNull.Value;
        }
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    private static long Ms(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private sealed class Counters
    {
        public int Applied { get; set; }

        public int Stale { get; set; }

        public int NewUnread { get; set; }

        public int Removed { get; set; }

        public int Expired { get; set; }

        public int Absent { get; set; }

        public int Pruned { get; set; }

        public ApplyResult ToResult() => new(Applied, Stale, NewUnread, Removed, Expired, Absent, Pruned);
    }
}
