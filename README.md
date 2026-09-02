# FolderVault

Put a password on a Windows folder. Double-click it, a small box appears where you clicked, type
the password, and the folder opens in normal Explorer — no archive viewer, no special browser.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512bd4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078d4) ![tests](https://img.shields.io/badge/tests-64%20passing-brightgreen)

## How it works

A real NTFS folder can't show a password prompt on double-click — the only mechanism that allows
it is a COM shell namespace extension, which is a large C++ component that takes Explorer down
with it when it misbehaves.

So while a folder is locked, its contents move to a hidden store and a **shortcut wearing the
folder icon** takes its place. Windows never displays the `.lnk` extension, so `Photos.lnk` shows
as exactly `Photos`. Double-click it, enter the password, and the real folder is restored.

Locking is a same-volume move, which NTFS does as a metadata rename: a 300 MB folder locks in
**248 ms** and stays that fast at 100 GB, because no bytes are copied.

## Two modes, per folder

|  | **Fast** | **Secure** |
|---|---|---|
| Method | Moves the folder to a hidden store, denies access via NTFS permissions | AES-256-GCM on every file |
| Speed | Instant at any size | Scales with folder size |
| Stops a housemate or colleague | Yes | Yes |
| Stops an admin or an offline disk | **No** | Yes |
| Losing the password | Recoverable | **Data is gone** without the recovery key |

**Fast mode is obfuscation, not encryption.** NTFS gives a file's owner implicit `WRITE_DAC`, so
the owner — or any administrator — can always strip the deny rule and read the files. That is
inherent to Windows, and it's why Secure mode exists. In both modes the files are plaintext on
disk while unlocked.

## Using it

- **Protect a folder** — pick a folder, a mode, and a password. Save the recovery key.
- **Open** — double-click the folder, or use the manager.
- **Remove protection** — select it and choose Remove protection. It becomes an ordinary folder
  again; nothing is deleted. Locked folders ask for the password first.
- **Auto-lock** — re-locks on an idle timer, when the Explorer window closes, and when Windows
  locks.

## Build

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet test
dotnet publish src/FolderVault/FolderVault.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -o publish
```

Produces a single ~280 KB `FolderVault.exe`. It runs as `asInvoker` and never needs elevation.
You can move or rename the folder it lives in — locked folders' shortcuts are re-pointed at
startup.

## Layout

```
src/FolderVault.Core/    no UI: crypto, vault storage, lock/unlock, recovery
src/FolderVault/         WinForms tray app and dialogs
tests/                   64 tests, including 8 crash-recovery scenarios
```

Data safety rests on two rules: a payload directory only gets its final name once it is complete
and verified (anything mid-write is `*.partial` and is discarded on recovery), and a source is
never deleted until its replacement is in place. So an interrupted lock or unlock always leaves at
least one complete copy, and its name says so.

## Status

Personal project, tested end to end on one machine. Keep backups of anything irreplaceable, as you
would with any tool that moves your files around.
