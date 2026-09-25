# Events and listeners

[Back to index](index.md)

There are two ways to react to the save system. **Events** are C# events on `ISaveService`: they are
facts, they are fire-and-forget, and nothing waits for a handler. **Listeners** are interfaces the
service awaits at specific points, in a defined order, so that caches and views are rebuilt before
the game continues.

---

## Events

All six are `Action<T>` events on `ISaveService`. They are raised on the main thread, each handler in
its own try/catch (a throwing handler is logged and does not stop the others), and nothing is raised
after `Dispose`.

| Event | Payload | Raised when | What to do with it |
|---|---|---|---|
| `ProfileActivated` | `ProfileActivationResult` | After slots load and restore listeners finish, including the initial activation inside `InitializeAsync`. | Update "signed in as" UI. Check `ClaimedGuestData` to show a one-time "your progress was linked" message. |
| `RestoreCompleted` | `RestoreReport` | End of every `RestoreAsync` **and** every single-slot conflict reconcile. Also raised when a restore was refused, with the failure in `Status` / `Error`. | Show a sync indicator. Check `RequiresAppUpdate`. |
| `SlotLoadIssueDetected` | `SlotLoadIssue` | A local or cloud payload problem was found during a load. | Send to analytics. `Kind`, `Cause` and `BackupFileName` say exactly what happened and which file was kept. |
| `UploadFailed` | `UploadFailure` | A **permanent** upload failure or refusal. Never fired for a transient retry. | Log it, and surface it if `Reason` indicates the player should act (`EmptyOverContent` is a bug report; `SchemaTooNew` is an update prompt). |
| `LocalWriteHealthChanged` | `LocalWriteHealth` | Local write health **transitions**: healthy to failing, failing to healthy, or a change of error kind. | Show or hide a "cannot save, storage full" banner. `Kind` is the most severe of `DiskFull` > `AccessDenied` > `IoError`. |
| `UpdateRequired` | `UpdateRequiredInfo` | A local or cloud payload was written by a newer build. Once per slot, per source, per epoch. | Prompt the player to update the app. `FoundSchema` / `SupportedSchema` say how far behind this build is. |

`LocalWriteHealthChanged` is a transition event on purpose. A disk that is full fails every write;
an event per failed attempt would be a log flood, and the banner would flicker.

```csharp
_save.LocalWriteHealthChanged += health =>
{
    _storageBanner.SetActive(!health.IsHealthy);
    if (!health.IsHealthy)
    {
        _storageBanner.SetMessage(health.Kind == LocalWriteErrorKind.DiskFull
            ? "Device storage is full. Progress cannot be saved."
            : "Progress cannot be saved right now.");
    }
};
```

Unsubscribe where you subscribed. `Dispose` clears the service's own handler lists, but a
subscription that outlives its owner still leaks that owner until then.

---

## `IRestoreListener`

```csharp
public sealed class InventoryViewRefresher : IRestoreListener
{
    public int Order => 3000;

    public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
    {
        _view.Rebuild(_inventorySlot.Read(static d => d.Items));
        return UniTask.CompletedTask;
    }
}

_save.AddRestoreListener(new InventoryViewRefresher());
```

Runs after slot data has changed and before the game is told the operation finished. The
`RestoreReport` tells you why:

- `report.Trigger == RestoreTrigger.ProfileActivated` — slots were loaded from local storage
  (initialization or a profile switch).
- `report.Trigger == RestoreTrigger.CloudRestore` — a cloud restore or a single-slot reconcile ran.
- `report.GetResult(slot)` returns that slot's `SlotRestoreResult`, or `null` when the slot was not
  part of this report.

A common use of `GetResult`: a derived `LocalOnly` cache that is only valid for one particular
version of a synced slot.

```csharp
public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
{
    if (report.Trigger != RestoreTrigger.CloudRestore)
    {
        return UniTask.CompletedTask;
    }

    SlotRestoreResult source = report.GetResult(_inventorySlot);
    if (source != null && (source.Outcome == SlotRestoreOutcome.Restored
                           || source.Outcome == SlotRestoreOutcome.Merged))
    {
        // The source changed underneath us; throw the derived cache away.
        return _save.DeleteSlotAsync(_derivedIndexSlot, DeleteTarget.LocalOnly, ct)
            .AsUniTask();
    }

    return UniTask.CompletedTask;
}
```

---

## `IProfileDeactivatingListener`

```csharp
public sealed class SessionWriter : IProfileDeactivatingListener
{
    public int Order => 2000;

    public async UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
    {
        _statsSlot.Mutate(_session.Elapsed, static (d, t) => d.TotalPlayTime += t);
        await _statsSlot.SaveNowAsync(ct);
    }
}

_save.AddDeactivationListener(new SessionWriter());
```

Runs **before** a profile switch, while the outgoing profile is still fully writable. `Mutate`,
`SaveNowAsync` and `FlushAsync` all work here, and whatever you write lands in the outgoing
profile's directory. `context.Outgoing` and `context.Incoming` say who is leaving and who is
arriving.

The budget is shared: `SaveServiceOptions.DeactivationTimeout` (5 seconds by default) covers **all**
deactivation listeners of one switch, not each one. When it runs out, the remaining listeners are
skipped, recorded in `ProfileActivationResult.DeactivationFailures`, `DeactivationTimedOut` is set,
and **the switch proceeds anyway**. A misbehaving service must not be able to block a sign-in
forever.

---

## Dispatch table

| Operation | Deactivation listeners | Restore listeners | Events raised |
|---|---|---|---|
| `InitializeAsync` | Not dispatched | Yes, `Trigger = ProfileActivated` | `SlotLoadIssueDetected` per issue, then `ProfileActivated` |
| `ActivateProfileAsync`, real switch | Yes, before the gate is taken and before mutations are refused | Yes, `Trigger = ProfileActivated` | `SlotLoadIssueDetected` per issue, then `ProfileActivated` |
| `ActivateProfileAsync`, already active | No | No | None |
| `ActivateProfileAsync`, refused or cancelled during the hooks | Started; the rest are skipped | No | None |
| `RestoreAsync`, success or partial | No | Yes, `Trigger = CloudRestore` | `SlotLoadIssueDetected`, `UpdateRequired`, then `RestoreCompleted` |
| `RestoreAsync`, precondition failure or an account change mid-flight | No | **No** | `RestoreCompleted` only, carrying the failure |
| Single-slot reconcile after a write conflict | No | Yes, `Trigger = CloudRestore`, with that one slot in the report | `RestoreCompleted` |
| `FlushAsync`, `FlushLocalNow`, `SaveNowAsync` | No | No | `UploadFailed`, `LocalWriteHealthChanged` as they occur |
| `DeleteSlotAsync`, `DeleteProfileAsync`, `DeleteAccountDataAsync` | No | No | None |
| Quit and pause | No | No | `LocalWriteHealthChanged` if the flush changed health |

A listener **added after `InitializeAsync`** gets no catch-up call. If a system starts late, read the
slots directly once at startup and rely on the listener only for subsequent changes.

Both `Add` methods ignore a duplicate registration of the same instance, and both `Add` and `Remove`
are no-ops after `Dispose`. A listener removed while a dispatch is running is not called.

---

## Order

Listeners are dispatched in ascending `Order`. Ties fall back to registration order.

That tie-break is fragile, and deliberately so: with a DI container, registration order comes from
`ResolveAll`, which changes when bindings move between installers. Do not rely on it.

### Recommended ranges

| Range | Use |
|---|---|
| `< 0` | Reserved for package integrations. None in 0.1.0. |
| `0` - `999` | Source caches that read slots directly. |
| `1000` - `1999` | Derived caches that read other caches. |
| `2000` - `2999` | Gameplay systems and controllers. |
| `3000` - `3999` | Presenters and UI. |
| `4000` and up | Analytics and diagnostics. |

Leave steps of 10 between your values so a new listener can be inserted later without renumbering
everything.

These ranges are a convention. Nothing enforces them.

### The equal-order warning

Adding a listener whose `Order` equals one already registered logs a warning naming **both** types.
It is logged once per add, in all builds, from both `AddRestoreListener` and
`AddDeactivationListener`:

> [SaveSystem] restore listener InventoryViewRefresher has Order 3000, equal to HudRefresher.
> Ties run in registration order.

If you see it and the two listeners genuinely do not depend on each other, give one of them a
neighbouring value anyway. It costs nothing and removes the question.

The final dispatch order is also logged at **verbose** level on every dispatch, as `Order Type`
pairs plus the trigger. Turn verbose logging on in your `ISaveLogger` when you are debugging an
ordering problem; dispatches are rare, so the log is short.

---

## Failures

Listener exceptions are **recorded, never rethrown**. The dispatch continues with the next listener.

- Restore listener failures: `RestoreReport.ListenerFailures`.
- Deactivation listener failures: `ProfileActivationResult.DeactivationFailures`.

Each entry is a `ListenerFailure` with `ListenerType`, `Order`, `Kind`
(`Threw`, `TimedOut`, `Skipped`) and, for `Threw`, the `Exception`.

Calling `InitializeAsync`, `ActivateProfileAsync` or `RestoreAsync` from inside a listener is
refused with `SaveErrorCode.ReentrantCall`. Those operations are what dispatched you; re-entering
them would deadlock or recurse.

---

## The rule that matters most

**Listeners refresh state. They never replay earned effects.**

A listener runs when data *appears*, which is not the same as when it was *earned*. If a listener
fires a reward popup, a level-up animation, an achievement toast or an analytics `currency_earned`
event, then:

- signing in replays them for progress earned days ago on another device;
- a cloud restore replays them for progress this session already showed;
- a reinstall replays the player's entire history in one burst.

```csharp
// WRONG, in a listener.
if (_playerSlot.Read(static d => d.Level) > _shownLevel)
{
    _levelUpVfx.Play();
    _analytics.Track("level_up");
}

// RIGHT, in a listener.
_hud.SetLevel(_playerSlot.Read(static d => d.Level));
```

Earned effects belong at the moment of earning: in the gameplay command that granted the thing, next
to the `Mutate` call that recorded it.

If you do want to tell the player that a sync changed something, that is a separate, explicit
notice — "Progress from another device was loaded" — driven off `RestoreReport.Trigger ==
RestoreTrigger.CloudRestore`, not off the data itself.

---

## Related pages

- [Slots and profiles](Slots-And-Profiles.md) — what a listener is reading.
- [Sync and recovery](Sync-And-Recovery.md) — what produces a `RestoreReport`.
- [Dependency injection](Dependency-Injection.md) — registering listeners through a container, and
  the signal equivalents of these events.
