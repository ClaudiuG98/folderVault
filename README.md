# FolderVault

Put a password on a Windows folder. Double-click it, a small box appears where you clicked, type
the password, and the folder opens in normal Explorer — no archive viewer, no special browser.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512bd4) ![Windows](https://img.shields.io/badge/Windows-10%2F11-0078d4) ![licence](https://img.shields.io/badge/licence-MIT-blue) [![ci](https://github.com/ClaudiuG98/folderVault-/actions/workflows/ci.yml/badge.svg)](https://github.com/ClaudiuG98/folderVault-/actions/workflows/ci.yml)

## How it works

A real NTFS folder can't show a password prompt on double-click — the only mechanism that allows
it is a COM shell namespace extension, which is a large C++ component that takes Explorer down
with it when it misbehaves.

So while a folder is locked, its contents move to a hidden store and a **shortcut wearing the
folder icon** takes its place. Windows never displays the `.lnk` extension, so `Photos.lnk` shows
as exactly `Photos`. Double-click it, enter the password, and the real folder is restored.

The decoy's icon is composited at run time from the machine's own `imageres.dll`: the stock folder
with a padlock badge on its bottom-right corner, drawn at every size Explorer asks for. Explorer
draws its own shortcut arrow at the bottom-*left*, so the two never collide.

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
- **Auto-lock** — re-locks on its own; see below for when, and how to change it.

## When a folder re-locks

Set per folder, from **Settings** with a folder selected, or right-click a row and choose
**Auto-lock**. One choice of four:

| | |
|---|---|
| **When I close its Explorer window** | A few seconds after the last window on it closes. |
| **After N minutes without activity** | Default 15. |
| **When I close it, or after N minutes** | Both of the above. The default, and the safest. |
| **Never** | Only when you lock it yourself. |

Plus one independent tickbox, on by default: **also lock when Windows locks or I switch user**.

"Activity" means something inside the folder changing — saving, renaming, deleting. Reading a file
does not count, and neither does having the folder open on screen. That last part is deliberate and
was once a bug: an open Explorer window used to reset the clock every three seconds, so a folder
left open never timed out at all, no matter what the timeout said. A window sitting open is not
someone using the folder; it is exactly the case the timeout exists to catch.

Two things no setting changes. **Signing out or shutting down always locks every open folder**,
because it is the last moment anything can. And all of it requires FolderVault to be running.

That second one is the real limit, and it is worth stating plainly: if the app is not running,
nothing re-locks anything. A power cut, a forced restart, exiting from the tray, or Windows killing
a large encrypted folder mid-lock during shutdown all leave that folder unlocked on disk — and
**FolderVault does not lock anything at startup**, so it stays unlocked until a rule fires or you
lock it yourself.

An encrypted folder re-locking on its own does so quietly — no progress window, just the tray
notification once it is done. You did not ask for it, so it should not land on top of what you
were doing. Pressing **Lock** yourself still shows progress, because then you are waiting for it.

## Two things Explorer makes awkward

**Icon position.** The desktop remembers where an icon sits by file name, and `Photos` and
`Photos.lnk` are two different names — so locking a folder parked in a corner would fling it back
to the first free slot, and unlocking would do it again. FolderVault reads the live desktop view
(`IFolderView::GetItemPosition`) before the swap and puts the replacement back on the same spot
afterwards, so a locked folder stays where you left it.

Two things had to be true for that to work. Explorer refreshes a view when it is *told* to, and
`Directory.Move` tells it nothing — creating the decoy went through `IPersistFile::Save`, which
announces itself, but the folder moving back did not, so the view could not find the item to
position it. Every move and delete now raises `SHChangeNotify`, which also means open Explorer
windows update the moment a folder locks. And unlock removes the decoy *before* restoring the
folder: while both sit on the desktop under one displayed name, Explorer surfaces only one of
them and the other cannot be positioned at all.

Inside an ordinary folder window there is no fix. Those views are always sorted, Windows groups
folders ahead of files, and a decoy is a file — so it moves from the folder group to the file
group and back. The padlock badge at least makes it easy to spot. The only way out would be a
decoy that is genuinely a directory, and a directory cannot show a password prompt when you
double-click it; that is the shell namespace extension this project exists to avoid.

**The shortcut arrow.** Windows draws a small arrow over every shortcut, and it is the last visual
difference between a decoy and a real folder. FolderVault used to offer to hide it. **It no longer
does, and it will not come back** — two implementations were tried and both broke the whole
machine:

1. Deleting `IsShortcut` from `HKLM\Software\Classes\lnkfile`, which is what most of the internet
   suggests. That value is what tells the shell to resolve a `.lnk` to its target instead of
   opening it as a document, and without it every taskbar pin on the machine fails with *"This
   file does not have an app associated with it"* — Explorer's own views still work, which is what
   makes it confusing to diagnose.
2. Pointing the shell's shortcut overlay (`Shell Icons` override `29`) at a blank icon, on the
   theory that the slot would still be drawn with nothing in it. Explorer instead fills it with a
   **solid black block**, so every shortcut on the PC wears a black square where its arrow was.
   The icon is not the problem: a generated blank `.ico` and `shell32.dll,50` both load fully
   transparent at every size and both stay invisible in an ordinary image list. The blackening
   happens inside the shell's own overlay image list and only at the small icon size — the very
   same override renders correctly at 48 and 256 pixels. No icon file can steer that.

The padlock badge sits on the bottom-*right* precisely so it never collides with the arrow on the
bottom-left, so a decoy reads correctly with the arrow left alone. That is why dropping this costs
nothing.

If a PC ran either of those versions it still carries the damage after upgrading, so **Settings**
grows a **Repair Windows shortcuts** button — only when there is something to repair. It restores
`IsShortcut`, removes override `29`, and offers to restart Explorer, which is needed to clear the
black squares already sitting in the icon cache.

## Recovering a folder by hand

FolderVault is not required to get your files back. Everything it does is a move plus, in Fast
mode, an NTFS permission change — both undoable from a command prompt. This matters if the app is
deleted while a folder is locked, or if an unlock fails with *"the access restriction on the stored
folder could not be removed"*.

A locked folder's contents live on the **same drive** the folder came from:

```
<Drive>:\.FolderVault\<vault-id>\plain      Fast mode — your files, exactly as they were
<Drive>:\.FolderVault\<vault-id>\enc        Secure mode — encrypted blobs
```

`.FolderVault` is marked hidden and system, so `dir` will not list it without `dir /a`. Each
`<vault-id>` folder holds a `vault.json` that is deliberately **not** encrypted — open it in any
text editor and `originalPath` tells you which folder that vault holds.

### Fast mode

The payload is your folder, untouched, with a Deny ACE on it. Remove the ACE, restore inheritance,
then move it back wherever you want:

```bat
icacls "C:\.FolderVault\<vault-id>\plain" /remove:d "%USERNAME%"
icacls "C:\.FolderVault\<vault-id>\plain" /inheritance:e
move "C:\.FolderVault\<vault-id>\plain" "%USERPROFILE%\Desktop\Photos"
```

No password is needed — which is the same sentence as "Fast mode is obfuscation, not encryption",
said from the other direction. If `icacls` reports access denied, take ownership first with
`takeown /f "C:\.FolderVault\<vault-id>\plain" /r /d y`.

Delete the leftover `<vault-id>` folder afterwards, and `.FolderVault` itself once no vaults remain
on that drive.

### Secure mode

There is no by-hand route: the blobs are AES-256-GCM and the manifest holding the original
filenames is encrypted alongside them. Reinstall FolderVault, protect the folder's path again, and
unlock with the password or the recovery key. This is what the recovery key is for — without the
password or that key the data is genuinely gone.

## Build

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet test
dotnet publish src/FolderVault/FolderVault.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -o publish
```

Produces a ~383 KB `FolderVault.exe` that needs the [.NET 8 Desktop
Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the machine. Good for working on it.

For a build to give someone else, bundle the runtime instead:

```bash
dotnet publish src/FolderVault/FolderVault.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o dist
```

One 68 MB `FolderVault.exe` that runs on any Windows 10/11 machine with nothing installed. The two
extra flags matter: without `IncludeNativeLibrariesForSelfExtract` five native DLLs sit beside the
exe and have to be copied with it, and without `EnableCompressionInSingleFile` the file is 146 MB.
Compression costs about 9 ms of startup, measured — worth knowing because every double-click on a
locked folder starts one of these processes to hand the request to the running instance.

Either way it runs as `asInvoker` and never needs elevation. You can move or rename the folder it
lives in — locked folders' shortcuts are re-pointed at startup.

`src/FolderVault/app.ico` is the repository's only binary file, and it has to be: Windows reads an
executable's icon out of its resources before any code runs, so unlike the tray, window and decoy
icons it cannot be drawn at startup. It is generated from that same padlock drawing by
`AppIcon.WriteIcoFile`, so the two cannot drift — regenerate it if the artwork changes.

**Windows will warn about it.** A downloaded exe that is not signed with a paid certificate gets
"Windows protected your PC" from SmartScreen; *More info → Run anyway* gets past it. Antivirus
scanners also flag this kind of app now and then, because moving folders, setting deny ACEs and
writing to HKLM is exactly what it does and also what they look for. Neither goes away without
code signing.

## Layout

```
src/FolderVault.Core/    no UI: crypto, vault storage, lock/unlock, recovery
src/FolderVault/         WinForms tray app and dialogs
tests/                   125 tests, including 11 crash-recovery scenarios
```

Data safety rests on two rules: a payload directory only gets its final name once it is complete
and verified (anything mid-write is `*.partial` and is discarded on recovery), and a source is
never deleted until its replacement is in place. So an interrupted lock or unlock always leaves at
least one complete copy, and its name says so.

## Status

Personal project, tested end to end on one machine. Keep backups of anything irreplaceable, as you
would with any tool that moves your files around.

If it ever stops working, or you stop trusting it, your files are still plain files on the same
drive — see [Recovering a folder by hand](#recovering-a-folder-by-hand).

## Licence

MIT — see [LICENSE](LICENSE).
