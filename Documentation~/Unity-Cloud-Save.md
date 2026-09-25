# Unity Cloud Save

[Back to index](index.md)

The package ships an `ICloudSaveProvider` implementation for Unity Gaming Services Cloud Save. It
lives in its own assembly, `Ecanakli.SaveSystem.UnityCloudSave`, and turns itself on when the UGS
Cloud Save package is installed.

You do not have to use it. The cloud layer is an interface; this is one implementation of it.

---

## Enabling it

1. Install **UGS Cloud Save 3.0.0 or newer** through Package Manager. It pulls in Unity Services
   Core and Authentication.
2. That is it. The assembly definition carries a `versionDefines` entry:

```json
"versionDefines": [
  { "name": "com.unity.services.cloudsave", "expression": "3.0.0", "define": "ECANAKLI_SAVESYSTEM_UGS" }
]
```

Unity sets `ECANAKLI_SAVESYSTEM_UGS` for that assembly when the package is present, and the
assembly's `defineConstraints` require it. No menu toggle, no Player Settings change. Without the
UGS package the assembly simply does not compile into your project.

3. Reference `Ecanakli.SaveSystem.UnityCloudSave` from your assembly definition and pass the
   provider to the service:

```csharp
using Ecanakli.SaveSystem;
using Ecanakli.SaveSystem.UnityCloudSave;

var options = new SaveServiceOptions();
ISaveService save = new SaveService(
    options,
    new AtomicFileStorage(options.RootDirectory),
    new UnityCloudSaveProvider(),
    slots);
```

With Zenject:

```csharp
Container.Bind<ICloudSaveProvider>().To<UnityCloudSaveProvider>().AsSingle();
```

### Before any cloud call works

- `UnityServices.InitializeAsync()` must have completed.
- The player must be signed in through `AuthenticationService`.
- Activate the matching profile: `await save.ActivateProfileAsync(ProfileId.Account(AuthenticationService.Instance.PlayerId), ct)`.

The provider reads `AuthenticationService.Instance.IsSignedIn` and `.PlayerId` for
`SignedInAccountId`, and returns `null` (behaving like signed out) if Unity Services has not been
initialized yet, instead of throwing. So the order is forgiving: it will report "not signed in"
rather than blow up in your bootstrap.

---

## Capabilities

The provider declares its limits through `CloudCapabilities`. The core checks them before it
attempts a write, so a payload that is too large fails locally with a clear reason instead of
producing a server error.

| Capability | Value | Where the number came from |
|---|---|---|
| `MaxValueBytes` | 5,000,000 | The UGS item model documentation says "maximum size of 5 MB". 5,000,000 is the decimal reading, which is the smaller and therefore safer of the two possible interpretations. **Not confirmed against the live service.** |
| `MaxKeysPerWrite` | 20 | Verified in the SDK source: `PlayerDataService.SaveWithErrorHandlingAsync` auto-chunks at 20 items per HTTP call. |
| `MaxKeysPerRead` | 20 | **Not discoverable.** The SDK exposes no per-call key limit for loads, so this mirrors the write ceiling as a conservative default until a live check. |
| `MaxTotalBytes` | 0 | 0 means "unknown or unlimited". No per-account byte quota is documented in the installed package; the SDK's `KeyLimitExceeded` is a key-**count** limit, not a byte limit. |
| `SupportsConditionalWrite` | `true` | UGS write locks are a compare-and-swap on the expected existing lock, and are always available. This is what lets the package do conditional repair writes. |
| `PreservesValueText` | `false` | A stored value goes through parse and re-serialize on the round trip, so it is not guaranteed to come back byte-identical. While this is `false`, cloud payload checksums are **not** verified. |

All six are overridable through `UnityCloudSaveOptions`, so you can tighten or correct them without
waiting for a package update:

```csharp
var provider = new UnityCloudSaveProvider(new UnityCloudSaveOptions(
    maxValueBytes: 5_242_880,          // if you verify the binary-MB reading
    maxKeysPerRead: 50,                // if you verify a higher read ceiling
    preservesValueText: true,          // only after the round-trip check below
    publicKeys: new[] { "leaderboard-profile" }));
```

Do not raise `PreservesValueText` on a guess. Setting it to `true` enables checksum verification of
cloud payloads, and if values are not actually byte-identical, every cloud value will be reported as
corrupt.

---

## Access classes

UGS Cloud Save player data has access classes. The client SDK is allowed to:

| Access class | Client can read | Client can write |
|---|---|---|
| `Default` | Yes | Yes |
| `Public` | Yes | Yes |
| `Protected` | Yes | **No** |

The provider maps the package's `CloudAccess` onto them:

| Package | Read as | Write as |
|---|---|---|
| `CloudAccess.ClientOwned` (a `CloudSync` slot) | `Default` | `Default` |
| `CloudAccess.ServerOwned` (a `CloudReadOnly` slot) | `Protected` | **Refused** |
| Any key listed in `UnityCloudSaveOptions.publicKeys` | `Public` | `Public` |

The per-key `publicKeys` override always wins, for both reads and writes. Use it for the rare value
another player's client is allowed to see.

A write to a `ServerOwned` key that is not in `publicKeys` is refused inside the provider, with a
permanent error naming the key. The core never routes `CloudReadOnly` slots to a write in the first
place, so this is a second line of defence rather than a code path you should hit.

One reconcile batch can mix `ClientOwned` and `ServerOwned` keys, because the restore builds a
single read list across `CloudSync` and `CloudReadOnly` slots. Each UGS call takes exactly one
access class, so the provider **groups the requests by resolved access class, issues one SDK call
per group, and reassembles the results in the original request order.** That reassembly is
load-bearing: a naive single call would send `CloudReadOnly` keys to the wrong access class and read
nothing.

---

## Error mapping

The provider never throws (apart from cancellation). SDK exceptions become `CloudError` values, and
the core decides what to retry from the `CloudErrorKind`. Only `Transient` and `RateLimited` are
retried.

| SDK exception or reason | `CloudErrorKind` | Notes |
|---|---|---|
| `CloudSaveRateLimitedException` | `RateLimited` | Carries `RetryAfter` through to the retry policy. |
| `CloudSaveConflictException` | `Conflict` | Another writer got there first. Triggers a single-slot reconcile. |
| `CloudSaveValidationException` | `PayloadTooLarge` | UGS has no separate "too large" reason. |
| `ServicesInitializationException` | `NotSignedIn` | Unity Services not initialized yet; treated as signed out, not as a hard failure. |
| `NoInternetConnection`, `ServiceUnavailable` | `Transient` | Retried. |
| `ProjectIdMissing`, `PlayerIdMissing`, `AccessTokenMissing` | `NotSignedIn` | |
| `Unauthorized` | `Unauthorized` | |
| `KeyLimitExceeded` | `QuotaExceeded` | Key count, not bytes. |
| `TooManyRequests` | `RateLimited` | Only reachable if a 429 arrives as a plain `CloudSaveException`. |
| `Conflict` (as a plain reason) | `Conflict` | Same, for a 409/412. |
| `InvalidArgument` | `PayloadTooLarge` | |
| `NotFound`, `Unknown`, anything else | `Permanent` | |

### Batch atomicity

One conflicting key fails the **whole** UGS write call with a `CloudSaveConflictException`. So when
a batch fails because of a sibling key, the other keys in that batch are marked `Transient` with an
explicit message, and the gateway retries each of them alone. Without that, one slot with a stale
write lock would block every other slot's upload.

### Deletes

The UGS delete API gives no "not found" signal. The package treats a delete that does not report an
error as done, and a missing key counts as already absent.

---

## Before you ship: the live-service checklist

Everything above was verified offline, against the installed SDK source and its documentation. The
following items **cannot** be verified without a real project and a real signed-in player. Work
through them once against your own UGS project, and correct `UnityCloudSaveOptions` if the answers
differ.

1. **The real `MaxKeysPerRead` ceiling.** Load more keys in one call than the current default of 20
   and see whether it succeeds.
2. **The exact per-value byte limit:** 5,000,000 or 5,242,880? Write a payload just under each.
3. **Whether a per-account byte quota exists at all,** and what happens when it is hit.
4. **Whether a written value comes back byte-identical.** Write a value, read it back, compare the
   exact text. Only enable `preservesValueText: true` if it matches.
5. **A conditional delete of an already-deleted key with a stale expected version.** Confirm it does
   not resurrect or error in a way the mapping does not cover.
6. **The dashboard shows the envelope as a JSON object,** not as one long quoted string. If it shows
   a quoted string, values are being double-encoded.
7. **The awkward cases:** a write-lock conflict produced by two Editor instances writing the same
   key; actual rate-limit behaviour under a burst; reading a `Protected` key written by a server;
   and that account linking keeps the same `PlayerId`, so the profile directory does not change
   under the player.

---

## Writing your own provider

`ICloudSaveProvider` is the whole contract:

```csharp
public interface ICloudSaveProvider
{
    CloudCapabilities Capabilities { get; }
    string SignedInAccountId { get; }
    UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct);
    UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct);
    UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct);
}
```

Implementation rules, learned from the UGS one:

- **Never throw** except for cancellation. Return a `CloudWriteResult.Failed` / `CloudReadResult`
  with a classified `CloudError` instead. The retry policy reads the kind.
- **Return results in request order**, one per request, even for the requests you refused. The core
  matches results to slots positionally.
- **Declare honest capabilities.** `MaxValueBytes` too high means server errors the core could have
  prevented; `SupportsConditionalWrite` claimed but not implemented means the safety invariants stop
  holding.
- **`SignedInAccountId` must never throw.** Return `null` when the backend is not ready.
- Leave `PreservesValueText` at `false` unless your backend stores the exact bytes. Backends that
  store JSON documents (jsonb-style) reorder keys, which would make every value look corrupt.

---

## Related pages

- [Sync and recovery](Sync-And-Recovery.md) — when the provider is actually called.
- [Dependency injection](Dependency-Injection.md) — binding the provider.
- [Troubleshooting](Troubleshooting.md) — cloud symptoms and fixes.
