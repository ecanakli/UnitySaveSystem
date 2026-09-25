# Zenject Setup

Requires the package's Zenject integration to be active
(`Ecanakli.SaveSystem.DependencyInjection.Zenject`, gated behind `ECANAKLI_SAVESYSTEM_DI_ZENJECT`).

## Setup

1. If your Zenject/Extenject install lives under `Assets` (not UPM/OpenUPM), run
   `Tools/Save System/Zenject Integration` once and commit the resulting `ProjectSettings` change.
   Installs via UPM or OpenUPM define the symbol automatically through `versionDefines` and need no menu step.
2. Reference `Ecanakli.SaveSystem`, `Ecanakli.SaveSystem.DependencyInjection.Zenject` and `Zenject` from your game asmdef.
3. Install `SaveSampleInstaller` from your `ProjectContext` (or copy its bindings into your own installer).
   Never install it again in a `SceneContext`; the DI documentation forbids it (`ResolveAll` also searches
   parent containers, so a second `SaveListenerRegistrar` in a scene container double-registers). No scene
   asset ships with this sample, since it would store Zenject script GUIDs that differ between an Assets
   install and a UPM install.

## What to look at

- `SaveSampleInstaller.cs` — `SignalBusInstaller.Install`, `SaveServiceInstaller.Install`,
  `SaveSignalsInstaller.Install` (optional), `BindSaveSlot<SettingsSlot>()`, and binding the listener/boot
  classes with `BindInterfacesTo`.
- `SettingsSlot.cs` — a `Device`-scoped, `LocalOnly` slot; `Normalize` clamps the volume fields; `[Preserve]`
  on the constructor for IL2CPP, since Zenject constructs it through reflection.
- `SettingsRestoreLogger.cs` — an `IRestoreListener` that branches on `RestoreTrigger`, and also subscribes
  to `SaveUploadFailedSignal` in `Initialize` / unsubscribes in `Dispose`.
- `SaveSampleBoot.cs` — an `IInitializable`/`IDisposable` that awaits `InitializeAsync` after the package's
  own registrar and lifecycle host have run (they use an early execution order; this class uses the default).
