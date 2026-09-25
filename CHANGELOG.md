# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-09-24

First release.

### Added

- **Save slots.** `SaveSlot<TData>` with `Read`, `Mutate` and `SaveNowAsync`, plus the
  `CreateDefault`, `Normalize`, `UpgradePayload`, `IsEmpty` and `ResolveConflict` hooks. `SyncMode`
  (`LocalOnly`, `CloudSync`, `CloudReadOnly`) and `SlotScope` (`Profile`, `Device`).
- **Crash-safe local writes.** `AtomicFileStorage` writes to a temp file, fsyncs it, rotates the
  previous file to `.bak` and renames. Loads recover from `.tmp` or `.bak`, verify a SHA-256
  checksum over the payload bytes, and quarantine a file that cannot be recovered.
- **Profiles.** `ProfileId.Guest`, `ProfileId.Account(id)` and `ProfileId.Local(name)`, each with
  its own directory. `ActivateProfileAsync` is the single switch entry point, including the
  first-sign-in guest claim.
- **Cloud sync.** Provider-agnostic reconciliation by write-id provenance, debounced uploads,
  conditional writes when the provider supports them, conflict resolution through the slot hook,
  and a `{key}.conflict.json` record of both sides.
- **Strict flush.** `FlushAsync` reports per-slot upload results and an `IsComplete` flag that is
  true only when local writes completed and every cloud-backed slot is uploaded or confirmed.
- **Deletes.** `DeleteSlotAsync`, `DeleteProfileAsync` (with `RequireSynced` / `DiscardUnsynced`)
  and `DeleteAccountDataAsync` for account data removal requests.
- **Schema versioning.** Per-slot `SchemaVersion` and a one-step-at-a-time `UpgradePayload` chain.
  A payload written by a newer build is refused, left untouched, and reported through the
  `UpdateRequired` event.
- **Events.** `ProfileActivated`, `RestoreCompleted`, `SlotLoadIssueDetected`, `UploadFailed`,
  `LocalWriteHealthChanged` and `UpdateRequired`.
- **Listeners.** `IRestoreListener` and `IProfileDeactivatingListener`, dispatched in `Order` with
  a shared deactivation timeout budget and recorded failures.
- **Lifecycle.** `SaveLifecycleDriver` forwards pause, focus loss and quit; pause flushes locally
  and then attempts a best-effort cloud flush.
- **Readiness.** `IsReady` and `WhenReadyAsync`, completed after slots load and before listeners
  run, so a listener can await it without deadlocking.
- **Development aids.** Warning on slot access before load, and detection of changes made outside
  `Mutate`; both default to on in the Editor and development builds.
- **Unity Cloud Save provider.** `Ecanakli.SaveSystem.UnityCloudSave` activates itself when the UGS
  Cloud Save package (3.0.0+) is installed. Groups keys by access class, maps SDK exceptions onto
  the package's error kinds, and declares its capability limits.
- **Zenject integration.** `Ecanakli.SaveSystem.DependencyInjection.Zenject` with
  `SaveServiceInstaller`, the optional `SaveSignalsInstaller` and its seven signals,
  `BindSaveSlot<TSlot>()`, listener registration and lifecycle hosting. Activated by
  `versionDefines` for a UPM Extenject install, or by the `Tools/Save System/Zenject Integration`
  menu toggle for a Zenject copy under `Assets/`.
- **Editor tools.** `Tools/Save System/Open Save Folder`, `Delete All Saves...`,
  `Validate Save Schemas`, `Update Save Schema Snapshot` and the `Schema Check On Reload` toggle.
  The schema guard compares live slot shapes against `ProjectSettings/SaveSystemSchemas.json` and
  reports a removed or retyped member as a breaking change unless `SchemaVersion` was raised.
- **Samples.** A plain C# setup and a Zenject setup.
- **Documentation.** `Documentation~/` covers slots and profiles, sync and recovery, events and
  listeners, schema changes, dependency injection, the Unity Cloud Save provider and
  troubleshooting.

### Known limitations

- WebGL is not supported; the service logs one warning and saves may be lost.
- Linux is supported by design but was not verified on hardware for this release.
- `CloudCapabilities.PreservesValueText` is `false` for the Unity Cloud Save provider, so cloud
  payload checksums are not verified until a live round trip proves values come back byte-identical.
- Metadata-only cloud reads are not implemented; a restore reads full values.

[0.1.0]: https://github.com/ecanakli/UnitySaveSystem/releases/tag/v0.1.0
