using ArbetsWatch.Core.Ads;
using ArbetsWatch.Core.Filtering;
using ArbetsWatch.Core.Storage;

namespace ArbetsWatch.Core.Tests;

/// <summary>A store in a temporary directory, deleted on dispose.</summary>
internal sealed class TempStore : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arbetswatch-tests", Guid.NewGuid().ToString("N"));

    public TempStore(StorePolicy? policy = null)
    {
        Policy = policy;
        Store = AdStore.Open(DatabasePath, policy);
    }

    public string DatabasePath => Path.Combine(_directory, "arbetswatch.db");

    public StorePolicy? Policy { get; }

    public AdStore Store { get; private set; }

    public AdStore Reopen()
    {
        Store.Dispose();
        Store = AdStore.Open(DatabasePath, Policy);
        return Store;
    }

    /// <summary>Establishes the first baseline from the given ads, as a first snapshot would.</summary>
    public async Task BaselineAsync(params AdSummary[] ads)
    {
        await Store.ResetStagingAsync();
        await Store.StageSnapshotChunkAsync(ads);
        await Store.ActivateSnapshotAsync(AdFilter.Default, T0, T0, T0);
    }

    public async Task<Dictionary<string, AdRow>> RowsAsync(AdFilter? filter = null, DateTimeOffset? now = null) =>
        (await Store.QueryAsync(filter ?? AdFilter.Default, now ?? T0)).ToDictionary(r => r.Ad.Id);

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleanup removes leftovers.
        }
    }
}
