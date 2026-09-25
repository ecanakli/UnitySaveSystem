# Dependency injection

[Back to index](index.md)

The core of this package knows nothing about any DI container. `Ecanakli.SaveSystem` references
UniTask and Newtonsoft.Json and nothing else. You can construct `SaveService` with `new` and be
done.

The Zenject integration is a **separate assembly** that only compiles when Zenject or Extenject is
actually present. If you never install one, that assembly is skipped by the compiler and costs you
nothing.

---

## Without a container

```csharp
var options = new SaveServiceOptions();
ISaveService save = new SaveService(
    options,
    new AtomicFileStorage(options.RootDirectory),
    NullCloudSaveProvider.Instance,
    new SaveSlot[] { playerSlot, settingsSlot });

IDisposable lifecycle = SaveLifecycleDriver.Attach(save);
await save.InitializeAsync(ct);

// ... and on shutdown
lifecycle.Dispose();
save.Dispose();
```

That is the whole integration surface. Everything below is convenience for Zenject users.

---

## The Zenject integration

Assembly: `Ecanakli.SaveSystem.DependencyInjection.Zenject`.
Namespace: `Ecanakli.SaveSystem.DependencyInjection` — note that the namespace does not name the
framework, so a game's `using` lines would not change if it ever moved to a different container.

It references `Ecanakli.SaveSystem`, `UniTask` and `Zenject`, and lists `Newtonsoft.Json.dll` and
`Zenject-usage.dll` as precompiled references.

### Turning it on

The assembly is gated by the scripting define `ECANAKLI_SAVESYSTEM_DI_ZENJECT`. There are two ways
it gets set, and which one applies depends on how Zenject is installed in your project.

**Extenject installed as a UPM package** — nothing to do. The assembly definition carries a
`versionDefines` entry:

```json
"versionDefines": [
  { "name": "com.svermeulen.extenject", "expression": "9.0.0", "define": "ECANAKLI_SAVESYSTEM_DI_ZENJECT" }
]
```

Unity sets the define for that assembly automatically when the package is present at 9.0.0 or newer.
No Player Settings change, nothing to commit.

**Zenject copied under `Assets/`** — version defines cannot see a plain folder, so use the menu:

`Tools/Save System/Zenject Integration`

It is a checkable toggle. Turning it on adds `ECANAKLI_SAVESYSTEM_DI_ZENJECT` to the scripting
define symbols of **every** build target, not just the active one, because IL2CPP builds for a
target whose defines were never set would silently drop the assembly. Commit `ProjectSettings` after
toggling.

If no assembly named `Zenject` exists in the project, the menu item refuses with a dialog instead of
setting a define that would break the compile.

### `SaveServiceInstaller`

```csharp
using Ecanakli.SaveSystem;
using Ecanakli.SaveSystem.DependencyInjection;
using Zenject;

public sealed class GameProjectInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        var options = new SaveServiceOptions
        {
            CloudDebounce = TimeSpan.FromSeconds(30),
            CloudMaxWait = TimeSpan.FromMinutes(5),
        };

        SaveServiceInstaller.Install(Container, options);

        Container.BindSaveSlot<PlayerSlot>();
        Container.BindSaveSlot<SettingsSlot>();

        // Optional: signals. SignalBusInstaller must run first.
        SignalBusInstaller.Install(Container);
        SaveSignalsInstaller.Install(Container);
    }
}
```

`SaveServiceInstaller` binds:

| Binding | Notes |
|---|---|
| `SaveServiceOptions` | The instance you passed, `AsSingle`. `Install` throws if the container already binds one. |
| `ISaveStorage` | An `AtomicFileStorage` rooted at `options.RootDirectory`, bound `IfNotBound()`. If you bind your own `ISaveStorage` before calling `Install`, the installer keeps it and never builds its own. |
| `ISaveService` + `IDisposable` | One `SaveService`, `AsCached`, built from the options, the storage, an `ICloudSaveProvider` if one is bound (otherwise `NullCloudSaveProvider.Instance`) and `ResolveAll<SaveSlot>()`. All of them come from the container `Install` ran in, even when a child container resolves the service first. |
| `SaveListenerRegistrar` as `IInitializable` + `IDisposable` | `AsCached`, `NonLazy`. Adds every resolved `IRestoreListener` and `IProfileDeactivatingListener` on `Initialize`, removes them on `Dispose`. |
| `SaveLifecycleHost` as `IInitializable` + `IDisposable` | `AsCached`, `NonLazy`. Calls `SaveLifecycleDriver.Attach` on `Initialize` and disposes the handle on `Dispose`. |

`AsCached` here is about what these bindings can coexist with, not about instance identity. Zenject
6+ refuses two `AsSingle` creation bindings for the same contract type, and `IDisposable` is exactly
that contract: the service, the registrar and the lifecycle host (and, with signals, the bridge) all
bind it. `AsCached` is what lets those bindings, plus a game's own
`BindInterfacesAndSelfTo<T>().AsSingle()` services, coexist without Zenject throwing at bind time.

`ISaveStorage`, like `ICloudSaveProvider`, is overridable: bind your own before `Install` and the
installer keeps it. `SaveServiceOptions` is not. The instance you pass to `Install` is the only one
the service and the storage are built from, whichever container resolves them first. `Install`
throws `InvalidOperationException` when `SaveServiceOptions` is already bound in the same container,
which also catches a second `Install` into the same container.
Do not bind it afterwards either: inject `SaveServiceOptions` where your own code needs it, since the
installer already binds it.

The installer takes no dependency on `SignalBus`. Signals are entirely optional.

To use the bundled cloud provider, bind it before or after the installer; the service picks it up
through `TryResolve`:

```csharp
Container.Bind<ICloudSaveProvider>().To<UnityCloudSaveProvider>().AsSingle();
```

### `BindSaveSlot<TSlot>()`

```csharp
public static ConcreteIdArgConditionCopyNonLazyBinder BindSaveSlot<TSlot>(
    this DiContainer container, params Type[] additionalContracts) where TSlot : SaveSlot
{
    var contractTypes = new List<Type> { typeof(SaveSlot), typeof(TSlot) };
    if (additionalContracts != null)
    {
        contractTypes.AddRange(additionalContracts.Where(t => t != null));
    }

    return container.Bind(contractTypes.Distinct().ToList()).To<TSlot>().AsCached();
}
```

One instance, reachable as `SaveSlot` (so the installer's `ResolveAll<SaveSlot>()` finds it), as
`TSlot` (so your own systems can inject the concrete type and call its typed methods), and as any
`additionalContracts` you pass. A `null` entry in `additionalContracts` is ignored, and a repeated
contract (including `SaveSlot` or `TSlot` themselves) registers the provider once, not once per
occurrence.

Binding a slot only as `TSlot` is the classic mistake — it injects fine, but the service never sees
it and it is never loaded or saved.

It binds with `AsCached`, not `AsSingle`, and returns the binder instead of `void`. Zenject 6+
refuses two `AsSingle` creation bindings for the same concrete type, and `TSlot` is exactly the type
you are likely to bind again — for example a slot that is also an `IRestoreListener`. Do not bind
that interface separately with `Container.BindInterfacesTo<TSlot>()`; it is a second, independent
creation binding, so it resolves a second instance that the save service never sees. Pass the
interface as an additional contract instead, so it resolves to the same instance:

```csharp
Container.BindSaveSlot<PlayerSlot>(typeof(IRestoreListener));
```

The returned binder is an ordinary Zenject binder, so it chains normally afterwards
(`.WithConcreteId(...)`, `.When(...)`, `.NonLazy()`, ...).

### `SaveSignalsInstaller` and the seven signals

Install it **after** `SaveServiceInstaller`, and only after `SignalBusInstaller.Install(Container)`.
It throws `InvalidOperationException` with that instruction if `SignalBus` is not bound.

It declares seven signals, all with `OptionalSubscriber()` so an unsubscribed signal is not an
error, and binds `SaveSignalBridge` (`IInitializable` + `IDisposable`, `AsCached`, `NonLazy`).

| Signal | Kind | Payload |
|---|---|---|
| `SaveProfileActivatedSignal` | Fact | `ProfileActivationResult Result` — also fires for the initial activation inside `InitializeAsync` |
| `SaveRestoreCompletedSignal` | Fact | `RestoreReport Report` |
| `SaveSlotLoadIssueDetectedSignal` | Fact | `SlotLoadIssue Issue` |
| `SaveUploadFailedSignal` | Fact | `UploadFailure Failure` |
| `SaveLocalWriteHealthChangedSignal` | Fact | `LocalWriteHealth Health` |
| `SaveUpdateRequiredSignal` | Fact | `UpdateRequiredInfo Info` |
| `FlushSavesRequest` | Request | none |

The six fact signals mirror the six `ISaveService` events one to one. `FlushSavesRequest` goes the
other way: firing it makes the bridge call `FlushAsync`.

```csharp
_signalBus.Fire<FlushSavesRequest>();
```

That call is fire-and-forget by design — a request signal has no way to return a result. The bridge
catches everything inside its own handler, so nothing escapes into the signal bus. **When you need
the result, call `await saveService.FlushAsync(ct)` directly.** Use the request signal only for
"something happened, please flush eventually" from code that has no reference to the service.

The bridge subscribes in `Initialize` and unsubscribes in `Dispose`, and owns its own
`CancellationTokenSource` for the flushes it starts, cancelled and disposed in `Dispose`. It never
uses `BindSignal().ToMethod()`.

### Execution order

The registrar, the lifecycle host and the signal bridge are all bound with
`BindExecutionOrder<T>(-10000)`.

That puts them ahead of any game `IInitializable`, which matters: with `OffloadIo = false` (tests, or
a deliberately synchronous bootstrap), `InitializeAsync` can complete synchronously, and a game
initializer that calls it must not run before the listeners are registered.

They also dispose last. Every `Add` / `Remove` / `Attach` path involved is a no-op after the service
is disposed, so the ordering is safe either way.

### Listeners through the container

```csharp
Container.BindInterfacesTo<InventoryViewRefresher>().AsSingle();
```

`SaveListenerRegistrar` resolves `IRestoreListener` and `IProfileDeactivatingListener` with
`ResolveAll` and registers all of them. See
[Events and listeners](Events-And-Listeners.md#order) for the `Order` conventions — do not rely on
resolve order, which changes when bindings move between installers.

### The scene container rule

**Install `SaveServiceInstaller` in exactly one place: the project container (or wherever the save
service itself lives). Never install it again in a scene container.**

`ResolveAll` also searches parent containers. A second `SaveListenerRegistrar` in a scene container
would resolve the project container's listeners too, and then **remove them** when that scene
unloads — silently killing restore handling for the rest of the session.

Scene-scoped listeners should register themselves:

```csharp
public sealed class LevelHudRefresher : IInitializable, IDisposable, IRestoreListener
{
    private readonly ISaveService _save;
    public int Order => 3010;

    public void Initialize() => _save.AddRestoreListener(this);
    public void Dispose() => _save.RemoveRestoreListener(this);

    public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct) { ... }
}
```

### Removing the integration

Nothing in the core depends on it, so removal is mechanical:

1. Turn off `Tools/Save System/Zenject Integration` (or remove the Extenject package).
2. Delete the `SaveServiceInstaller.Install` and `SaveSignalsInstaller.Install` calls and the
   `BindSaveSlot` calls.
3. Drop the `Ecanakli.SaveSystem.DependencyInjection.Zenject` reference from your assembly
   definition.
4. Construct the service as shown in [without a container](#without-a-container).

The core assembly and the Editor assembly both still compile: the Editor assembly references only
the core, and the core references no DI framework.

---

## Adding another framework

The pattern is deliberately repeatable. A VContainer or Reflex integration would be a new folder,
a new assembly definition and a handful of files:

1. **A new gated assembly**, for example `Ecanakli.SaveSystem.DependencyInjection.VContainer`, under
   `DependencyInjection/VContainer/`, with `rootNamespace` `Ecanakli.SaveSystem.DependencyInjection`
   — the same namespace every integration shares.
2. **A define** following the same scheme, `ECANAKLI_SAVESYSTEM_DI_VCONTAINER`, in both
   `defineConstraints` and a `versionDefines` entry for that framework's UPM package name. Add a
   matching menu toggle for non-UPM installs.
3. **The three pieces** the Zenject integration has, translated to the new container's vocabulary:
   - registration of options, storage, provider, slots and the service, with lifetimes that resolve
     the service and its `IDisposable` to one instance;
   - a listener registrar that adds on start and removes on teardown;
   - a lifecycle host that calls `SaveLifecycleDriver.Attach` and disposes the handle.
4. **Optionally an event bridge**, if the framework has a message bus worth mirroring the six events
   onto.

The core needs no change. That is the point of keeping it DI-neutral: no `#if` blocks inside
`Ecanakli.SaveSystem`, and no reflection-based framework detection at runtime.

---

## Related pages

- [Events and listeners](Events-And-Listeners.md) — what the signals mirror.
- [Unity Cloud Save](Unity-Cloud-Save.md) — binding a cloud provider.
- [Troubleshooting](Troubleshooting.md#the-di-types-do-not-exist) — when the integration types do
  not resolve.
