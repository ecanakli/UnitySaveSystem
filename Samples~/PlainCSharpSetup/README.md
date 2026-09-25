# Plain C# Setup

No DI framework. Composes `SaveService` by hand from a `MonoBehaviour`.

## Setup

1. Reference `Ecanakli.SaveSystem` (and `UniTask`) from your game asmdef.
2. Add the `SaveBootstrap` component to one persistent GameObject that lives for the app's lifetime
   (or make its GameObject `DontDestroyOnLoad` from your own bootstrap script).
3. Wire `SaveBootstrap.OnMusicVolumeChanged` to a UI slider's `OnValueChanged`, or call it from code,
   to see `Read`, `Mutate` and `SaveNowAsync` run together.

## What to look at

- `SettingsSlot.cs` — a `Device`-scoped, `LocalOnly` slot; `Normalize` clamps the volume fields.
- `SettingsRestoreLogger.cs` — an `IRestoreListener` that branches on `RestoreTrigger`.
- `SaveBootstrap.cs` — builds `SaveServiceOptions`, `AtomicFileStorage` and `SaveService`; attaches
  `SaveLifecycleDriver`; awaits `InitializeAsync`; disposes everything in `OnDestroy`.
