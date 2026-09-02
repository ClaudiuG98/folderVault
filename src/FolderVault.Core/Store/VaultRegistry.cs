using System.Security.Cryptography;
using System.Text.Json;
using FolderVault.Core.Model;

namespace FolderVault.Core.Store;

/// <summary>
/// Persistence for vault metadata, kept in two places on purpose.
///
/// <para><b>The index</b> (<c>%LOCALAPPDATA%\FolderVault\vaults.json</c>, DPAPI-wrapped to the
/// current user) lists which folders are protected, plus per-vault policy such as the auto-lock
/// timeout. DPAPI here hides <i>which</i> folders are protected from anyone reading the disk.</para>
///
/// <para><b>The store copy</b> (<c>&lt;store&gt;/vault.json</c>, plain) holds the crypto
/// parameters. It is deliberately not DPAPI-wrapped: DPAPI keys die with a Windows profile, and
/// if the salt and wrapped key were only ever inside a DPAPI blob then a reinstall would make a
/// Secure vault permanently unrecoverable <i>even with the correct password</i>. Leaving it plain
/// costs nothing - a salt and a wrapped key are useless without the password - and makes the
/// store self-describing, so the drive can be moved to another machine and still be unlocked.</para>
/// </summary>
public sealed class VaultRegistry
{
    public const string IndexFileName = "vaults.json";
    public const string StoreCopyFileName = "vault.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _indexPath;

    public VaultRegistry(string? indexPath = null) =>
        _indexPath = indexPath ?? Path.Combine(DefaultDirectory, IndexFileName);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderVault");

    public string IndexPath => _indexPath;

    // ---- Index ----

    public List<Vault> Load()
    {
        var raw = AtomicFile.ReadAllBytesOrNull(_indexPath);
        if (raw is null || raw.Length == 0) return [];

        byte[] json;
        try
        {
            json = ProtectedData.Unprotect(raw, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Written by a different Windows user, or the profile's DPAPI keys are gone. The
            // vaults themselves are still recoverable from their per-store vault.json copies.
            throw new VaultRegistryUnreadableException(
                "The vault index could not be decrypted. It belongs to a different Windows user account, " +
                "or this profile has been recreated. Vaults can be re-imported from their drives.");
        }

        try
        {
            return JsonSerializer.Deserialize<List<Vault>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new VaultRegistryUnreadableException("The vault index is corrupt.", ex);
        }
    }

    public void Save(IEnumerable<Vault> vaults)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(vaults.ToList(), Options);
        var wrapped = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(_indexPath, wrapped);
    }

    /// <summary>Loads the index, applies <paramref name="mutate"/>, and saves it back.</summary>
    public void Update(Action<List<Vault>> mutate)
    {
        var vaults = Load();
        mutate(vaults);
        Save(vaults);
    }

    public void Upsert(Vault vault) => Update(vaults =>
    {
        vaults.RemoveAll(v => v.Id == vault.Id);
        vaults.Add(vault);
    });

    public void Remove(Guid vaultId) => Update(vaults => vaults.RemoveAll(v => v.Id == vaultId));

    // ---- Store copy ----

    /// <summary>
    /// Mirrors the vault's metadata beside its payload so the store can be unlocked without the
    /// index. Call after any change to the crypto parameters.
    /// </summary>
    public static void WriteStoreCopy(string vaultStore, Vault vault)
    {
        Directory.CreateDirectory(vaultStore);
        AtomicFile.WriteAllBytes(
            Path.Combine(vaultStore, StoreCopyFileName),
            JsonSerializer.SerializeToUtf8Bytes(vault, Options));
    }

    public static Vault? TryReadStoreCopy(string vaultStore)
    {
        var bytes = AtomicFile.ReadAllBytesOrNull(Path.Combine(vaultStore, StoreCopyFileName));
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<Vault>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds vault stores on a drive that the index does not know about - after a profile reset,
    /// or when a drive is moved between machines.
    /// </summary>
    public static IEnumerable<Vault> DiscoverOnVolume(string anyPathOnVolume)
    {
        var storeRoot = VolumeStore.GetStoreRoot(anyPathOnVolume);
        if (!Directory.Exists(storeRoot)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(storeRoot))
        {
            var vault = TryReadStoreCopy(dir);
            if (vault is not null) yield return vault;
        }
    }
}

public sealed class VaultRegistryUnreadableException : Exception
{
    public VaultRegistryUnreadableException(string message, Exception? inner = null)
        : base(message, inner) { }
}
