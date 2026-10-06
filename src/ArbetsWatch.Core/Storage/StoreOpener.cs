using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Storage;

/// <summary>The opened store, and where an unreadable database was moved (null when none was).</summary>
public sealed record StoreOpenResult(AdStore Store, string? QuarantinedTo);

/// <summary>The database exists but can't be used right now (locked, no permission, disk full). Nothing was changed.</summary>
public sealed class StoreUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Opens the cache. Only a database SQLite reports as corrupt or not a database is moved aside (together with its
/// WAL and shared-memory files, which must never be paired with a new file) so the app can start fresh. Any
/// other error leaves the files untouched: the previous usable cache is preserved (AGENTS.md section 7).
/// </summary>
public static class StoreOpener
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    public static StoreOpenResult Open(string databasePath, StorePolicy? policy = null, TimeProvider? time = null)
    {
        try
        {
            return new StoreOpenResult(AdStore.Open(databasePath, policy), null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            SqliteConnection.ClearAllPools();
            var stamp = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var aside = $"{databasePath}.unreadable-{stamp}";
            try
            {
                File.Move(databasePath, aside);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    if (File.Exists(databasePath + suffix))
                    {
                        File.Move(databasePath + suffix, aside + suffix);
                    }
                }
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                throw new StoreUnavailableException($"The database is unreadable and couldn't be moved aside ({move.Message}).", move);
            }

            try
            {
                return new StoreOpenResult(AdStore.Open(databasePath, policy), aside);
            }
            catch (SqliteException again)
            {
                throw new StoreUnavailableException($"A new database couldn't be created ({again.Message}).", again);
            }
        }
        catch (SqliteException ex)
        {
            throw new StoreUnavailableException($"The database can't be opened right now ({ex.Message}).", ex);
        }
    }
}
