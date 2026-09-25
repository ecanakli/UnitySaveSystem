# Save System

Crash-safe, account-scoped save data for Unity, with optional cloud sync.

Player progress is written through a temp file, an fsync and a rename, with a `.bak` copy kept, so a
crash or a battery death in the middle of a write cannot leave a half-written save. Each account,
guest session and named local profile gets its own folder, so signing in and out never mixes two
players' data. Data classes carry a schema version and an upgrade hook, so shipping a new build over
an old save is a normal, tested operation instead of a silent wipe.

Cloud sync is optional and provider-agnostic: a `CloudSync` slot is reconciled by write-id
provenance, never by "last write wins", and the package will refuse an unsafe overwrite rather than
destroy a copy of the data it cannot prove it owns.

## Requirements

| | |
|---|---|
| Unity | 6000.0 or newer. The tests run on 6000.3; the 6000.0 floor is declared from the APIs used, not exercised on that version for 0.1.0. |
| [UniTask](https://github.com/Cysharp/UniTask) | Required. Install it before this package; a git-URL package cannot declare it as a dependency. |
| Newtonsoft Json (`com.unity.nuget.newtonsoft-json` 3.2.1+) | Required; resolved automatically. |
| Zenject / Extenject | Optional, for the DI integration. |
| Unity Gaming Services Cloud Save 3.0.0+ | Optional, for the bundled cloud provider. |

Supported platforms: Android, iOS, Windows, macOS, Linux, and the Editor on desktop.
**WebGL is not supported in 0.1.0.** Consoles are untested.

## Install

Package Manager → **Add package from git URL**:

```
https://github.com/ecanakli/UnitySaveSystem.git#v0.1.0
```

Or add the line to `Packages/manifest.json`:

```json
"com.ecanakli.savesystem": "https://github.com/ecanakli/UnitySaveSystem.git#v0.1.0"
```

Always install a tag. Without `#v0.1.0` the install tracks the default branch.

### Then, depending on your setup

**No DI container**

1. Reference `Ecanakli.SaveSystem` from your game's assembly definition.
2. Construct `SaveService` yourself. The quick start below is the whole thing.

**Zenject/Extenject installed under `Assets/`**

1. Reference `Ecanakli.SaveSystem` and `Ecanakli.SaveSystem.DependencyInjection.Zenject`.
2. Enable `Tools/Save System/Zenject Integration` (it sets a scripting define on every build
   target) and commit `ProjectSettings`.
3. Install `SaveServiceInstaller` in your project installer. See
   [Dependency injection](Documentation~/Dependency-Injection.md).

**Extenject installed as a UPM package**

Same as above, but skip step 2: the integration assembly turns itself on through `versionDefines`.

## Quick start

```csharp
public sealed class PlayerData
{
    public int Level { get; set; } = 1;
    public long Coins { get; set; }
}

public sealed class PlayerSlot : SaveSlot<PlayerData>
{
    public override string Key => "player";
}

// Bootstrap, once.
var options = new SaveServiceOptions();
var slot = new PlayerSlot();
ISaveService save = new SaveService(
    options,
    new AtomicFileStorage(options.RootDirectory),
    NullCloudSaveProvider.Instance,
    new SaveSlot[] { slot });

IDisposable lifecycle = SaveLifecycleDriver.Attach(save);
await save.InitializeAsync(ct);

// Anywhere in gameplay.
long coins = slot.Read(static data => data.Coins);
slot.Mutate(100, static (data, amount) => data.Coins += amount);

// Purchases and rewards wait for the bytes to land.
await slot.SaveNowAsync(ct);
```

## Documentation

- [Start here](Documentation~/index.md) — what it does, how a game saves, the API map, platforms and limits
- [Slots and profiles](Documentation~/Slots-And-Profiles.md) — declaring slots, hooks, profiles, account switching
- [Sync and recovery](Documentation~/Sync-And-Recovery.md) — uploads, conflicts, corruption, OS backups, Steam
- [Events and listeners](Documentation~/Events-And-Listeners.md) — the six events and the two listener interfaces
- [Schema changes](Documentation~/Schema-Changes.md) — versioning save data and the Editor guard
- [Dependency injection](Documentation~/Dependency-Injection.md) — the Zenject integration
- [Unity Cloud Save](Documentation~/Unity-Cloud-Save.md) — enabling the UGS provider
- [Troubleshooting](Documentation~/Troubleshooting.md) — symptoms and fixes

## License

MIT. See [LICENSE.md](LICENSE.md).
