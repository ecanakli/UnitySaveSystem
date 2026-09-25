# Troubleshooting

[Back to index](index.md)

Symptoms, causes and fixes. Each entry starts with what you actually see.

---

## The DI types do not exist

**Symptom:** `CS0246: The type or namespace name 'SaveServiceInstaller' could not be found`, or
`Ecanakli.SaveSystem.DependencyInjection` does not resolve, even though the package is installed.

**Cause:** the integration assembly is gated by the scripting define
`ECANAKLI_SAVESYSTEM_DI_ZENJECT`. Without it, the assembly is not compiled and its types do not
exist.

**Fix:**

- Zenject copied under `Assets/`: enable `Tools/Save System/Zenject Integration`. It sets the define
  on **every** build target. Commit `ProjectSettings` afterwards.
- Extenject installed as a UPM package: the define is set automatically through `versionDefines` at
  9.0.0 or newer. If it still does not resolve, check the installed Extenject version.
- Either way, reference `Ecanakli.SaveSystem.DependencyInjection.Zenject` from your assembly
  definition. The define makes the assembly exist; the reference makes it visible to your code.

## The define was left behind after removing Zenject

**Symptom:** `CS0246: The type or namespace name 'Zenject' could not be found`, coming from inside
the package.

**Cause:** the reverse of the above. `ECANAKLI_SAVESYSTEM_DI_ZENJECT` is still in the Player
Settings, so the integration assembly tries to compile, but Zenject is gone.

**Fix:** turn off `Tools/Save System/Zenject Integration`, which removes the symbol from every build
target. If the menu is not available because the Editor cannot compile at all, remove
`ECANAKLI_SAVESYSTEM_DI_ZENJECT` by hand from **Project Settings → Player → Scripting Define
Symbols**, for each platform tab that has it.

The same applies per build target: a define set only for Android will leave an iOS build without the
integration, and IL2CPP will not tell you — it just silently lacks the installer.

## UniTask is missing

**Symptom:** `CS0246: The type or namespace name 'Cysharp' could not be found`, or `UniTask` does
not resolve, right after installing the package.

**Cause:** UniTask is a required dependency, but a package installed from a git URL cannot declare
another git package as a dependency, so UPM cannot pull it in for you.

**Fix:** install [UniTask](https://github.com/Cysharp/UniTask) first, from its git URL or from
OpenUPM, then re-import this package. The assembly definitions reference it by the assembly name
`UniTask`.

## Saves are empty in an IL2CPP build

**Symptom:** everything works in the Editor and in a Mono build. In an IL2CPP build (typically
Android or iOS release), every slot loads as defaults, or some fields are always default while
others are fine.

**Cause:** managed code stripping removed your data types or their members. A JSON deserializer
constructs those types by reflection, and the stripper cannot see that.

**Fix:** mark your data classes **and** your slot types:

```csharp
using UnityEngine.Scripting;

[Preserve]
public sealed class PlayerData { ... }

[Preserve]
public sealed class PlayerSlot : SaveSlot<PlayerData> { ... }
```

Or preserve the whole assembly with a `link.xml`:

```xml
<linker>
  <assembly fullname="MyGame.Save" preserve="all" />
</linker>
```

To confirm it is stripping and not something else, set **Player → Managed Stripping Level** to
`Minimal` and rebuild. If the saves come back, it was stripping.

Note that `[Preserve]` on the class does not always keep every member. Property setters are the
usual casualty; preserving the assembly is the blunt but reliable answer.

## `SchemaTooNew` in the logs, and a slot never loads

**Symptom:** a slot is `Failed`, its restore outcome is `SkippedSchemaTooNew`, uploads for it are
skipped, and the `UpdateRequired` event fires.

**Cause:** that payload was written by a build with a higher `SchemaVersion` (or envelope `fmt`)
than the one running. The usual sources: the player has two devices and updated only one; a QA build
was installed over a release build; you rolled the app version back.

**Fix:** there is nothing the package can do about it, on purpose. It refuses rather than guessing,
and it never overwrites the newer data.

- **In production:** handle `UpdateRequired` and show an update prompt. `FoundSchema` and
  `SupportedSchema` tell you how far behind the build is.
- **In development:** either run the newer build, or delete the local data with
  `Tools/Save System/Delete All Saves...`. For a cloud value, delete the key from the backend
  dashboard.

Do not "fix" it by lowering the file's `schema` field by hand. The payload really is shaped the new
way, and the current build cannot read it.

See [Schema changes](Schema-Changes.md#a-save-from-a-newer-build).

## The schema guard reports a breaking change

**Symptom:** a console error after a script reload, along the lines of:

> Save System schema guard: slot 'MyGame.Saves.PlayerSlot' (key 'player') has a breaking data shape change:
> removed 'LastSkin : string'; added 'OwnedSkins : List&lt;string&gt;'. This may be a rename; it is
> reported as a removal plus an addition because intent cannot be known. Bump SchemaVersion to 3
> and handle the upgrade in UpgradePayload.

It repeats on every reload until it is resolved.

**Cause:** you changed a data class in a way that existing save files cannot survive: you removed a
member, renamed one (which looks like a removal plus an addition), or changed its type. The guard
compares the live shapes against `ProjectSettings/SaveSystemSchemas.json`.

**This is not a build error. It is the guard doing its job** — it is telling you that shipping this
change as it stands will silently reset that data for every existing player.

**Fix:**

1. Decide what should happen to existing data for each named member.
2. Raise the slot's `SchemaVersion` by one.
3. Add the conversion to `UpgradePayload` for the version you just left.
4. Recompile. The slot is reclassified as Accepted and the snapshot updates itself.
5. Commit the code and `ProjectSettings/SaveSystemSchemas.json` together.

Running `Tools/Save System/Update Save Schema Snapshot` also makes the error go away, but it accepts
the change without an upgrade path. Only do that when you are certain no shipped build ever wrote
the old shape.

Remember what the guard **cannot** see: semantic changes (seconds to milliseconds, re-used enum
values), `[JsonProperty]` wire renames, and anything in an assembly that is not compiling. See
[the limits](Schema-Changes.md#what-the-guard-cannot-see).

## List items duplicated after load

**Symptom:** a list grows every time the game starts. Default items appear twice, then three times.
Or a dictionary keeps entries that should have been replaced.

**Cause:** something changed the Newtonsoft settings. The package pins
`ObjectCreationHandling.Replace`. With Newtonsoft's default (`Auto`), a field like

```csharp
public List<string> Items { get; set; } = new List<string> { "starter-sword" };
```

has the saved items **appended** to the initializer's items on every load, forever.

**Fix:** do not override `ObjectCreationHandling` in a custom `JsonConverter` added through
`SaveServiceOptions.JsonConverters`, and do not hand the slot a serializer of your own. If you need
custom conversion for one type, write a converter for that type only and leave the container
handling alone.

If the duplication is already in players' files, it is data now, not a settings problem: fix the
settings, then bump `SchemaVersion` and de-duplicate the list in `UpgradePayload`.

## "Old progress came back after reinstall"

**Symptom:** a player reinstalls the game and sees progress from days or weeks ago, not what they
had when they uninstalled.

**Cause:** the operating system restored it. Android Auto Backup (on by default, API 23+) and iOS
device backups both include `persistentDataPath`. Whatever the backup captured comes back, however
old it is.

**What the package does about it:** for `CloudSync` slots this resolves itself. Reconciliation uses
write ids, not timestamps, so a restored stale file is recognised as "a copy whose last upload is
not the current cloud value" and goes through the conflict path instead of silently winning. A
restored stale tombstone will not delete a cloud value that was rewritten since.

For `LocalOnly` slots and guest profiles there is no other copy, so the restored state **is** the
state. That is a feature as often as it is a bug: it is the only reason guest progress survives a
reinstall at all.

**If you need to opt out** (per-platform manifest settings and the iOS no-backup flag), see
[OS backups and reinstall](Sync-And-Recovery.md#os-backups-and-reinstall). The trade-off is that
you then lose guest and `LocalOnly` data on every reinstall.

## The WebGL warning in the console

**Symptom:** on the first frame of a WebGL build:

> [SaveSystem] WebGL is not supported in this version (no threads, browser-backed file system);
> saves may be lost.

**Cause:** exactly what it says. WebGL is not supported in 0.1.0. The browser has no real file
system behind `Application.persistentDataPath`, and the package offloads serialization and IO to the
thread pool, which WebGL does not have.

**Fix:** there is no fix in this version. The warning is logged once and the package keeps running,
so it does not break a build that happens to target WebGL — but do not ship player progress through
it. The assembly definitions deliberately do **not** exclude WebGL, because that would turn a
platform switch into a wall of `CS0246` errors in your own code.

If you need WebGL, `ISaveStorage` is the seam: a browser-backed implementation would plug in there
without the rest of the core knowing.

## Steam Cloud and a cloud provider are both enabled

**Symptom:** saves behave inconsistently across machines. Steam shows its own file-conflict dialog.
Progress that the game reported as synced is not there on the other PC.

**Cause:** two sync layers are managing the same bytes. Steam Auto-Cloud copies files by pattern and
knows nothing about write ids; this package's reconcile knows nothing about Steam having swapped a
file underneath it. Neither can win reliably.

**Fix:** pick one **per slot**.

- Slots synced by Steam Auto-Cloud must be `LocalOnly`.
- Slots synced by a cloud provider must not be inside Steam's configured paths.
- Configure Steam Auto-Cloud with root `saves/profiles/` and pattern `*.json`. Never include
  `saves/device/` (it holds the device id and the last-active pointer), and never include `.tmp` or
  `.bak` files, which are mid-write artefacts.

See [Steam Auto-Cloud](Sync-And-Recovery.md#steam-auto-cloud).

## Listener order warning

**Symptom:**

> [SaveSystem] restore listener HudRefresher has Order 3000, equal to InventoryViewRefresher.
> Ties run in registration order.

**Cause:** you registered two listeners with the same `Order`. They still run — the tie falls back to
registration order — but that order is not stable. With a DI container it comes from `ResolveAll`,
which changes when bindings move between installers.

**Fix:** if one of them genuinely depends on the other having run, give it a higher `Order`. If they
are independent, give one a neighbouring value anyway so the warning stops and the next reader does
not have to wonder.

See [the recommended ranges](Events-And-Listeners.md#recommended-ranges), and leave steps of 10
between values.

## Duplicate sample folder after upgrading the package

**Symptom:** after updating to a new package version and importing a sample, the project has two
copies of it and the compiler reports duplicate type definitions.

**Cause:** Package Manager copies samples into `Assets/Samples/Save System/<version>/`. The
**version number is part of the path**, so importing a sample from a newer package version creates a
second folder next to the old one. Both compile.

**Fix:** delete the old `Assets/Samples/Save System/<old-version>/` folder. Do that **before**
importing the new one, so you never have two.

Samples are example code; they are meant to be copied and edited in your project. If you edited the
imported copy, move your changes out of `Assets/Samples/` entirely before upgrading.

## A slot is never saved

**Symptom:** `Mutate` appears to work, values are correct in play mode, and nothing is on disk.

Work through these:

1. **Was the slot registered?** It must be in the collection passed to the `SaveService`
   constructor. With Zenject, use `Container.BindSaveSlot<TSlot>()` — binding only as `TSlot`
   injects fine but the service never sees it.
2. **Did `Mutate` return `false`?** It returns false and runs nothing when the slot is not `Ready`,
   is `CloudReadOnly`, a profile switch is in progress, the service is disposed, or the call came
   from inside a slot hook.
3. **Are you changing the data outside `Mutate`?** Keeping the `TData` reference from `Read` and
   changing it later is invisible to the system. In the Editor and development builds this is
   reported as an error by the mutation detector. See
   [never keep the reference](Slots-And-Profiles.md#never-keep-the-reference).
4. **Is the disk failing?** Subscribe to `LocalWriteHealthChanged`. Disk full and permission errors
   put the slot into a write backoff, and the data stays in memory.

## `FlushAsync` never reports `IsComplete`

Check `FlushResult.Cloud` — each entry names the slot and the reason:

| Reason | Meaning |
|---|---|
| `ProfileNotCloudBacked` | The active profile is Guest or Local. These never upload; a flush on them can never be complete. |
| `NotSignedIn` / `AccountMismatch` | The provider is signed out, or signed in as a different account than the active profile. |
| `NotReconciled` | No restore has decided this slot yet this epoch. Call `RestoreAsync` after activating the profile. |
| `SchemaTooNew` | See [above](#schematoonew-in-the-logs-and-a-slot-never-loads). |
| `EmptyOverContent` | The upload guard refused to push empty data over a slot that had content. Usually a load bug; if the slot really should be empty, use `DeleteSlotAsync`. |
| `LocalWriteFailed` | The local write is failing. Disk before cloud, always. |
| `SuspendedUntilReconcile` | A permanent upload failure suspended this slot until its next successful reconcile. |
| `Aborted` | A profile switch or `Dispose` superseded the flush mid-run. |
| `CloudError` | A provider error; `Error` carries the kind and message. |

---

## Related pages

- [Slots and profiles](Slots-And-Profiles.md)
- [Sync and recovery](Sync-And-Recovery.md)
- [Events and listeners](Events-And-Listeners.md)
- [Schema changes](Schema-Changes.md)
- [Dependency injection](Dependency-Injection.md)
- [Unity Cloud Save](Unity-Cloud-Save.md)
