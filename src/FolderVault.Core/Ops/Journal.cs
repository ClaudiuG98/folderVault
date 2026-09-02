using System.Text.Json;
using FolderVault.Core.Model;
using FolderVault.Core.Store;

namespace FolderVault.Core.Ops;

public enum JournalOperation
{
    Lock = 0,
    Unlock = 1,
}

/// <summary>
/// A record of an operation that was in flight. Written into the vault store <b>before</b> the
/// first destructive step and deleted once the operation completes.
///
/// The journal explains <i>what was being attempted</i> so the UI can describe an interrupted
/// operation. It is deliberately not the thing that keeps data safe: correctness comes from the
/// naming rule enforced by <see cref="VaultLayout"/>, where a payload directory only takes its
/// final name once it is complete and verified. Recovery therefore inspects the filesystem and
/// treats the journal as a hint, which means a journal that is missing, stale or corrupt can
/// never cause data loss.
/// </summary>
public sealed class JournalEntry
{
    public Guid VaultId { get; set; }
    public JournalOperation Operation { get; set; }
    public VaultMode Mode { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Human-readable step, for the recovery dialog only.</summary>
    public string Step { get; set; } = string.Empty;
}

public static class Journal
{
    public const string FileName = "journal.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathFor(string vaultStore) => Path.Combine(vaultStore, FileName);

    public static void Write(string vaultStore, JournalEntry entry)
    {
        Directory.CreateDirectory(vaultStore);
        AtomicFile.WriteAllBytes(PathFor(vaultStore), JsonSerializer.SerializeToUtf8Bytes(entry, Options));
    }

    /// <summary>Updates the human-readable step of an in-flight entry. Best effort.</summary>
    public static void Step(string vaultStore, JournalEntry entry, string step)
    {
        entry.Step = step;
        try
        {
            Write(vaultStore, entry);
        }
        catch (IOException)
        {
            // Progress reporting must never break the operation it describes.
        }
    }

    public static JournalEntry? TryRead(string vaultStore)
    {
        var bytes = AtomicFile.ReadAllBytesOrNull(PathFor(vaultStore));
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<JournalEntry>(bytes);
        }
        catch (JsonException)
        {
            // A corrupt journal is survivable: recovery reads the filesystem, not this file.
            return null;
        }
    }

    public static void Clear(string vaultStore)
    {
        try
        {
            var path = PathFor(vaultStore);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* a stale journal is harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }
}
