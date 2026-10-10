using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ArbetsWatch.Core.Translation;

/// <summary>Where a secret such as an API key lives between runs. Never the database, preferences or logs.</summary>
public interface ISecretStore
{
    /// <summary>False where no encrypted per-user storage exists; the key can then only come from the environment.</summary>
    bool IsAvailable { get; }

    string? Load();

    void Save(string secret);

    /// <summary>True when no stored key remains.</summary>
    bool Delete();
}

/// <summary>For platforms without an encrypted per-user store: nothing is ever written to disk.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public string? Load() => null;

    public void Save(string secret) => throw new PlatformNotSupportedException("No encrypted storage on this platform.");

    public bool Delete() => true;
}

/// <summary>
/// Windows DPAPI, scoped to the current user: the file holds ciphertext that only this Windows account can decrypt,
/// so copying it (or the data folder) to another account or machine yields nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string path) : ISecretStore
{
    // Binds the ciphertext to this app: another program's DPAPI blobs are not accepted here.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArbetsWatch.DeepL.v1");

    public bool IsAvailable => true;

    public string? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Unreadable (another account, damaged): behave as if no key were saved.
            return null;
        }
    }

    public void Save(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool Delete()
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or read-only: the ciphertext stays, and the caller says so.
            return false;
        }
    }
}
