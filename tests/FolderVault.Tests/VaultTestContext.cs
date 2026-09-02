using System.Security.Cryptography;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;

namespace FolderVault.Tests;

/// <summary>
/// A disposable sandbox: a throwaway folder tree, a private registry index, and cleanup that
/// also removes the per-volume store the vault created at the drive root.
/// </summary>
public sealed class VaultTestContext : IDisposable
{
    public string Root { get; }
    public string FolderPath { get; }
    public VaultRegistry Registry { get; }
    public VaultService Service { get; }

    private readonly List<Guid> _vaultIds = [];

    public VaultTestContext()
    {
        Root = Path.Combine(Path.GetTempPath(), "fv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        FolderPath = Path.Combine(Root, "Secrets");
        Registry = new VaultRegistry(Path.Combine(Root, "vaults.json"));
        Service = new VaultService(Registry);
    }

    public void Track(Guid vaultId) => _vaultIds.Add(vaultId);

    /// <summary>
    /// Builds a tree covering the cases that break naive implementations: nested directories,
    /// a non-ASCII name, an empty file, an empty directory, and a file spanning several chunks.
    /// </summary>
    public static void BuildSampleTree(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "nested", "deeper"));
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));

        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello vault");
        File.WriteAllText(Path.Combine(root, "café-résumé-日本語.txt"), "unicode name");
        File.WriteAllBytes(Path.Combine(root, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(root, "nested", "small.bin"), RandomNumberGenerator.GetBytes(1024));
        File.WriteAllBytes(Path.Combine(root, "nested", "deeper", "multi-chunk.bin"),
            RandomNumberGenerator.GetBytes(3 * (1 << 20) + 7)); // spans several 1 MiB chunks
    }

    /// <summary>Relative path to SHA-256 for every file, plus the set of directories.</summary>
    public static (Dictionary<string, string> Files, HashSet<string> Directories) Snapshot(string root)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

        var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d))
            .ToHashSet();

        return (files, directories);
    }

    public void Dispose()
    {
        foreach (var id in _vaultIds)
        {
            var store = VolumeStore.GetVaultStore(Root, id);
            // Fast mode leaves a Deny ACE on the payload; drop it or the delete fails.
            var plain = VaultLayout.Plain(store);
            if (Directory.Exists(plain))
            {
                try { Acl.RemoveDeny(plain); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            try { AtomicFile.DeleteDirectory(store); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        try { AtomicFile.DeleteDirectory(Root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
