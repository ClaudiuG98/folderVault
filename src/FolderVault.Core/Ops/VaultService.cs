using System.Security.Cryptography;
using FolderVault.Core.Crypto;
using FolderVault.Core.Model;
using FolderVault.Core.Shell;
using FolderVault.Core.Store;

namespace FolderVault.Core.Ops;

/// <summary>Result of inspecting a vault whose last operation did not finish.</summary>
public sealed record RecoveryResult(VaultState State, string Summary, bool NeedsUserDecision = false);

/// <summary>What, if anything, a locked vault's decoy shortcut needed on startup.</summary>
public enum DecoyRepair
{
    /// <summary>It was already correct.</summary>
    None,

    /// <summary>It pointed at an executable that had moved, so double-clicking it did nothing.</summary>
    Retargeted,

    /// <summary>It was written by an older build and wore that build's icon.</summary>
    Reiconed,
}

/// <summary>
/// Orchestrates everything a vault can do: create, lock, unlock, change password, recover.
///
/// Every destructive sequence follows the same shape - write the journal, transform into a
/// <c>*.partial</c> directory, verify, promote by atomic rename, and only then delete the source.
/// <see cref="Recover"/> relies on that shape rather than on the journal being up to date.
/// </summary>
public sealed class VaultService(VaultRegistry registry)
{
    private readonly VaultRegistry _registry = registry;

    /// <summary>
    /// The exe a decoy shortcut points at.
    ///
    /// Deliberately only Environment.ProcessPath: Assembly.Location returns an empty string in a
    /// single-file build, and an empty target here would produce shortcuts that open nothing,
    /// leaving locked folders unreachable.
    /// </summary>
    public static string LauncherPath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the FolderVault executable path.");

    // ---- Keys ----

    /// <summary>
    /// Turns a password into the vault's data key. Throws <see cref="CryptographicException"/>
    /// if the password is wrong - the authenticated unwrap is the password check.
    /// </summary>
    public static byte[] DeriveDek(Vault vault, string password)
    {
        var kek = KeyDerivation.DeriveKek(password, vault.Salt, vault.Iterations);
        try
        {
            return KeyDerivation.UnwrapDek(kek, vault.WrappedDek);
        }
        finally
        {
            KeyDerivation.Wipe(kek);
        }
    }

    public static byte[] DeriveDekFromRecoveryKey(Vault vault, string recoveryKey)
    {
        if (vault.RecoverySalt is null || vault.RecoveryWrappedDek is null)
            throw new VaultOperationException("This vault has no recovery key.");

        var kek = KeyDerivation.DeriveKek(
            KeyDerivation.NormalizeRecoveryKey(recoveryKey), vault.RecoverySalt, vault.Iterations);
        try
        {
            return KeyDerivation.UnwrapDek(kek, vault.RecoveryWrappedDek);
        }
        finally
        {
            KeyDerivation.Wipe(kek);
        }
    }

    // ---- Create ----

    /// <summary>
    /// Protects a folder for the first time and locks it. Returns the recovery key when one was
    /// requested; it is shown to the user once and never stored in recoverable form.
    /// </summary>
    public (Vault Vault, string? RecoveryKey) Create(string folderPath, VaultMode mode, string password,
        bool issueRecoveryKey = true, IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (VolumeStore.ValidateProtectable(folderPath) is { } problem)
            throw new VaultOperationException(problem);

        var full = Path.GetFullPath(folderPath);
        if (File.Exists(full + ".lnk"))
            throw new VaultOperationException(
                $"A shortcut named '{Path.GetFileName(full)}' already sits beside this folder. " +
                "Rename or remove it first.");

        var vault = new Vault
        {
            OriginalPath = full,
            Mode = mode,
            State = VaultState.Unlocked,
            Salt = KeyDerivation.NewSalt(),
            Iterations = KeyDerivation.DefaultIterations,
        };

        var dek = KeyDerivation.NewDek();
        string? recoveryKey = null;
        try
        {
            var kek = KeyDerivation.DeriveKek(password, vault.Salt, vault.Iterations);
            vault.WrappedDek = KeyDerivation.WrapDek(kek, dek);
            KeyDerivation.Wipe(kek);

            if (issueRecoveryKey)
            {
                recoveryKey = KeyDerivation.NewRecoveryKey();
                vault.RecoverySalt = KeyDerivation.NewSalt();
                var recoveryKek = KeyDerivation.DeriveKek(
                    KeyDerivation.NormalizeRecoveryKey(recoveryKey), vault.RecoverySalt, vault.Iterations);
                vault.RecoveryWrappedDek = KeyDerivation.WrapDek(recoveryKek, dek);
                KeyDerivation.Wipe(recoveryKek);
            }

            var store = VolumeStore.EnsureVaultStore(full, vault.Id);
            VaultRegistry.WriteStoreCopy(store, vault);
            _registry.Upsert(vault);

            Lock(vault, dek, progress, ct);
            return (vault, recoveryKey);
        }
        finally
        {
            KeyDerivation.Wipe(dek);
        }
    }

    // ---- Lock ----

    /// <summary>
    /// Locks a vault. <paramref name="dek"/> is required for Secure mode and ignored for Fast,
    /// which is why an idle auto-lock can close a Fast vault without prompting for anything.
    /// </summary>
    public void Lock(Vault vault, byte[]? dek = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        // Every precondition is checked before EnsureVaultStore, because that call creates the
        // store directory as a side effect: validating afterwards would leave a resurrected
        // store behind on the very paths this is meant to refuse to touch.
        if (vault.Mode == VaultMode.Secure && dek is null)
            throw new VaultOperationException("Locking an encrypted vault needs its key.");

        if (!Directory.Exists(vault.OriginalPath))
            throw new VaultOperationException($"There is no folder at '{vault.OriginalPath}' to lock.");

        // Refuse to re-protect a folder whose protection was removed. Without this, anything
        // still holding a stale reference - an auto-lock timer that was never cancelled, say -
        // could silently recreate the vault minutes after the user deliberately removed it.
        if (_registry.Load().All(known => known.Id != vault.Id))
            throw new VaultOperationException(
                $"'{vault.DisplayName}' is no longer protected by FolderVault, so it cannot be locked.");

        var store = VolumeStore.EnsureVaultStore(vault.OriginalPath, vault.Id);

        var journal = new JournalEntry
        {
            VaultId = vault.Id,
            Operation = JournalOperation.Lock,
            Mode = vault.Mode,
            OriginalPath = vault.OriginalPath,
            Step = "starting",
        };
        Journal.Write(store, journal);
        SetState(vault, VaultState.Locking);

        // Read before the folder goes anywhere: once it has moved, the desktop has already
        // forgotten where it was.
        var desktopPosition = DesktopIcons.TryGetPosition(vault.OriginalPath);

        try
        {
            VaultLayout.DiscardPartials(store);

            Journal.Step(store, journal, "moving folder into the store");
            FastLocker.Stage(vault.OriginalPath, store, applyAcl: vault.Mode == VaultMode.Fast, progress);

            if (vault.Mode == VaultMode.Secure)
            {
                Journal.Step(store, journal, "encrypting");
                SecureLocker.Encrypt(store, dek!, progress, ct);

                // Only now, with a verified encrypted copy promoted into place, is it safe to
                // remove the plaintext.
                Journal.Step(store, journal, "removing plaintext");
                progress?.Report(new OperationProgress("Removing the unencrypted copy"));
                AtomicFile.DeleteDirectory(VaultLayout.Plain(store));
            }

            Journal.Step(store, journal, "creating shortcut");
            CreateDecoy(vault);

            if (desktopPosition is { } position) DesktopIcons.TryPlaceAt(vault.ShortcutPath, position);

            SetState(vault, VaultState.Locked);
            Journal.Clear(store);
        }
        catch
        {
            // Leave the journal in place: the vault stays marked as interrupted so the next
            // launch runs recovery rather than assuming a clean state.
            SetState(vault, VaultState.Locking);
            throw;
        }
    }

    // ---- Unlock ----

    public void Unlock(Vault vault, byte[] dek,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var store = VolumeStore.EnsureVaultStore(vault.OriginalPath, vault.Id);

        var journal = new JournalEntry
        {
            VaultId = vault.Id,
            Operation = JournalOperation.Unlock,
            Mode = vault.Mode,
            OriginalPath = vault.OriginalPath,
            Step = "starting",
        };
        Journal.Write(store, journal);
        SetState(vault, VaultState.Unlocking);

        // The decoy is about to be deleted; capture where the user had it before it goes.
        var desktopPosition = DesktopIcons.TryGetPosition(vault.ShortcutPath);

        try
        {
            VaultLayout.DiscardPartials(store);

            if (vault.Mode == VaultMode.Secure)
            {
                Journal.Step(store, journal, "decrypting");
                SecureLocker.Decrypt(store, dek, progress, ct);
            }

            // The decoy goes before the folder comes back, not after. While a folder and a
            // .lnk of the same displayed name both sit on the desktop, Explorer surfaces only one
            // of them, and the other cannot be found or positioned - so the returning folder would
            // keep whatever slot Explorer picked for it. Removing the decoy first leaves the view
            // a clean remove-then-add.
            //
            // It costs nothing in safety. The decoy is not a record of anything; the payload's
            // location is. A crash between here and the move below leaves the payload in the
            // store, which is exactly what recovery reads as "locked" - and it rebuilds the decoy.
            Journal.Step(store, journal, "removing shortcut");
            ShortcutFactory.Delete(vault.ShortcutPath);

            Journal.Step(store, journal, "moving folder back");
            FastLocker.Restore(vault.OriginalPath, store, removeAcl: vault.Mode == VaultMode.Fast, progress);

            if (desktopPosition is { } position) DesktopIcons.TryPlaceAt(vault.OriginalPath, position);

            if (vault.Mode == VaultMode.Secure)
            {
                // The plaintext is back in place, so the ciphertext is now redundant. Re-locking
                // encrypts afresh rather than trusting a copy that may have gone stale.
                Journal.Step(store, journal, "removing encrypted copy");
                AtomicFile.DeleteDirectory(VaultLayout.Encrypted(store));
            }

            vault.UnlockedAtUtc = DateTimeOffset.UtcNow;
            SetState(vault, VaultState.Unlocked);
            Journal.Clear(store);
        }
        catch
        {
            SetState(vault, VaultState.Unlocking);
            throw;
        }
    }

    // ---- Recovery ----

    /// <summary>
    /// Works out where an interrupted vault actually stands and puts it back into a consistent
    /// state. Driven by what is on disk, because the naming rule guarantees that any directory
    /// with a final name is complete; anything <c>*.partial</c> is discarded.
    /// </summary>
    public RecoveryResult Recover(Vault vault)
    {
        var store = VolumeStore.EnsureVaultStore(vault.OriginalPath, vault.Id);
        VaultLayout.DiscardPartials(store);

        var location = VaultLayout.Locate(vault.OriginalPath, store);

        switch (location)
        {
            case PayloadLocation.AtOriginal:
                // The folder is in place and complete. Anything left in the store is stale.
                AtomicFile.DeleteDirectory(VaultLayout.Encrypted(store));
                ShortcutFactory.Delete(vault.ShortcutPath);
                SetState(vault, VaultState.Unlocked);
                Journal.Clear(store);
                return new RecoveryResult(VaultState.Unlocked,
                    "The folder was already back in place. It is unlocked.");

            case PayloadLocation.PlainInStore when vault.Mode == VaultMode.Fast:
                CreateDecoy(vault);
                SetState(vault, VaultState.Locked);
                Journal.Clear(store);
                return new RecoveryResult(VaultState.Locked, "The folder was safely locked.");

            case PayloadLocation.PlainInStore:
                // Secure mode: an unencrypted copy in the store means encryption had not finished
                // being verified. The safe move is to put it back and let the user re-lock;
                // nothing is lost because this copy was never deleted.
                AtomicFile.DeleteDirectory(VaultLayout.Encrypted(store));
                // Decoy first, then the folder - the same ordering Unlock uses, and for the same
                // reason: a folder and its same-named decoy must not be on the desktop at once.
                ShortcutFactory.Delete(vault.ShortcutPath);
                FastLocker.Restore(vault.OriginalPath, store, removeAcl: false);
                SetState(vault, VaultState.Unlocked);
                Journal.Clear(store);
                return new RecoveryResult(VaultState.Unlocked,
                    "Encryption had not finished, so the folder was restored intact. It is unlocked - " +
                    "lock it again when you are ready.");

            case PayloadLocation.EncryptedInStore:
                CreateDecoy(vault);
                SetState(vault, VaultState.Locked);
                Journal.Clear(store);
                return new RecoveryResult(VaultState.Locked, "The folder was safely locked and encrypted.");

            case PayloadLocation.Ambiguous:
                return new RecoveryResult(vault.State,
                    $"There is a folder at '{vault.OriginalPath}' and also a copy in the vault store. " +
                    "FolderVault will not guess which one you want. Compare them and remove the one " +
                    "you do not need, then run recovery again.", NeedsUserDecision: true);

            default:
                return new RecoveryResult(vault.State,
                    $"No copy of '{vault.DisplayName}' could be found, at its original path or in the " +
                    "vault store. It was moved or deleted outside FolderVault.", NeedsUserDecision: true);
        }
    }

    /// <summary>Re-derives the state of every known vault from disk. Run at startup.</summary>
    public List<(Vault Vault, RecoveryResult Result)> RecoverAll()
    {
        var results = new List<(Vault, RecoveryResult)>();
        foreach (var vault in _registry.Load().Where(v => v.NeedsRecovery))
        {
            try
            {
                results.Add((vault, Recover(vault)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultOperationException)
            {
                results.Add((vault, new RecoveryResult(vault.State, ex.Message, NeedsUserDecision: true)));
            }
        }
        return results;
    }

    /// <summary>
    /// Makes sure a locked vault's decoy shortcut points at the executable that is running now
    /// and wears the current icon, rewriting it if not. Returns true if it had to be repaired.
    ///
    /// A .lnk stores an absolute path, so moving, renaming or reinstalling FolderVault leaves
    /// every locked folder standing in front of an executable that is no longer there: the
    /// decoy still looks like a folder but double-clicking it does nothing. Re-pointing them at
    /// startup keeps the app relocatable.
    ///
    /// The icon is checked for the same reason in reverse: a folder locked by an older build
    /// wears whatever that build drew, and would keep it until the next lock. Comparing here
    /// means an upgrade brings existing decoys up to date on first launch.
    /// </summary>
    public DecoyRepair RepairDecoy(Vault vault)
    {
        if (vault.State != VaultState.Locked) return DecoyRepair.None;

        var target = ShortcutFactory.TryReadTarget(vault.ShortcutPath);
        if (target is null ||
            !string.Equals(target, LauncherPath, StringComparison.OrdinalIgnoreCase))
        {
            CreateDecoy(vault);
            return DecoyRepair.Retargeted;
        }

        var (expectedIcon, expectedIndex) = DecoyIcon.ForShortcut();
        var icon = ShortcutFactory.TryReadIconLocation(vault.ShortcutPath);
        if (icon is not null && icon.Value.Index == expectedIndex &&
            string.Equals(icon.Value.Location, expectedIcon, StringComparison.OrdinalIgnoreCase))
            return DecoyRepair.None;

        CreateDecoy(vault);
        return DecoyRepair.Reiconed;
    }

    /// <summary>
    /// Repairs every locked vault's decoy. Returns only the ones that had to be re-pointed at a
    /// moved executable - the case worth telling the user about. A refreshed icon is invisible
    /// housekeeping and is done silently.
    /// </summary>
    public List<Vault> RepairAllDecoys()
    {
        var repaired = new List<Vault>();

        foreach (var vault in _registry.Load().Where(v => v.State == VaultState.Locked))
        {
            try
            {
                if (RepairDecoy(vault) == DecoyRepair.Retargeted) repaired.Add(vault);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           System.Runtime.InteropServices.COMException)
            {
                // A drive that is not mounted, or a folder we cannot write to. The vault itself
                // is untouched; the user can still open it from the manager.
            }
        }

        return repaired;
    }

    // ---- Maintenance ----

    public void ChangePassword(Vault vault, string currentPassword, string newPassword)
    {
        var dek = DeriveDek(vault, currentPassword); // throws if the current password is wrong
        try
        {
            // Re-wrapping the same DEK is why this is instant even for a large encrypted vault:
            // the files themselves are not touched.
            vault.Salt = KeyDerivation.NewSalt();
            var kek = KeyDerivation.DeriveKek(newPassword, vault.Salt, vault.Iterations);
            vault.WrappedDek = KeyDerivation.WrapDek(kek, dek);
            KeyDerivation.Wipe(kek);

            Persist(vault);
        }
        finally
        {
            KeyDerivation.Wipe(dek);
        }
    }

    /// <summary>
    /// Forgets the vault, leaving the folder as an ordinary folder. The vault must already be
    /// unlocked: requiring that here keeps the password prompt in the UI layer, where it belongs,
    /// instead of needing a key handed in for what is otherwise a bookkeeping operation.
    /// </summary>
    public void RemoveProtection(Vault vault)
    {
        if (vault.State != VaultState.Unlocked)
            throw new VaultOperationException(
                "Unlock the folder before removing its protection.");

        var store = VolumeStore.GetVaultStore(vault.OriginalPath, vault.Id);
        AtomicFile.DeleteDirectory(store);
        _registry.Remove(vault.Id);
    }

    // ---- Internals ----

    private static void CreateDecoy(Vault vault)
    {
        ShortcutFactory.Create(
            vault.ShortcutPath,
            LauncherPath,
            $"--unlock {vault.Id:N}",
            $"{vault.DisplayName} (locked by FolderVault)");
    }

    private void SetState(Vault vault, VaultState state)
    {
        vault.State = state;
        Persist(vault);
    }

    private void Persist(Vault vault)
    {
        _registry.Upsert(vault);
        try
        {
            VaultRegistry.WriteStoreCopy(VolumeStore.GetVaultStore(vault.OriginalPath, vault.Id), vault);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is still authoritative for day-to-day use; the store copy is a fallback
            // for disaster recovery and is refreshed on the next successful operation.
        }
    }
}
