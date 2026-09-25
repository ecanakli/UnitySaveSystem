# ADR-004: Editor-time schema change guard

- **Status:** Accepted, implemented in 0.1.0
- **Related:** [Schema changes](../../Schema-Changes.md),
  [Architecture section 10](../Architecture.md#10-editor-tooling)

## Context

In a previous production save system, a save data class changed shape between two releases. There was
no schema version on the payload and no upgrade step, so the new build deserialized old saves into a
type that no longer matched: some fields silently became defaults, others vanished. Players lost
progress. Nothing failed loudly — the load succeeded, which is exactly what made it expensive to find.

This package already has the runtime half of the answer: every payload carries a `schema` version in
its envelope, `SaveSlot<TData>.SchemaVersion` declares what the current build writes, and
`UpgradePayload(JObject payload, int fromSchemaVersion)` is the hook that migrates an older payload
forward. A payload from a *newer* build is refused outright rather than parsed optimistically.

That machinery is sound and unused if nobody turns it on. The runtime cannot detect the mistake that
caused the incident, because at runtime a renamed or removed field is indistinguishable from a field
that was simply absent in an old save. The information needed — "the class used to look like this" —
only exists at development time, in version control.

So: catch the breaking change on the developer's machine, before it reaches a player. Keep additive
changes friction-free, because most changes are additive and a check that cries wolf gets disabled.

## Decision

An **Editor-only** guard that snapshots the serializable shape of every slot's data type, compares the
current shape against the snapshot on script reload, and reports a breaking change as a console error
when `SchemaVersion` was not bumped.

Nothing is added to `Runtime/`. This is a development-time check and must cost nothing in a build.

### What is snapshotted

For every `SaveSlot<TData>` subclass in the loaded assemblies: the slot type's full name, its `Key`,
its `SchemaVersion`, whether it overrides `UpgradePayload`, and the serializable member list of
`TData`.

A member is a public instance property with both a getter and a setter, or a public instance field;
`[JsonIgnore]` members are excluded. Entries are recorded as `path : typeName`, sorted by path, so
declaration order never matters. Primitives, `string`, enums, `DateTime`, `Guid`, `Uri`, `TimeSpan`,
`object` and token types are leaves; everything else is walked recursively. Collections and
dictionaries record their own line and recurse into non-leaf element types. Cycles terminate through a
recursion-path set and depth is capped.

The snapshot is a JSON file in the **consuming project** at `ProjectSettings/SaveSystemSchemas.json`.
That location is deliberate: it is committed, it is next to the other project-level settings a reviewer
already reads, and `ProjectSettings` produces no `.meta` noise. It is project data, not package data,
so it never ships inside the package.

The package's own test slots are excluded by an assembly-name filter, so package tests do not pollute a
game's snapshot.

### Classification

| Difference | Verdict |
|---|---|
| Member added | **Additive.** Snapshot updated silently, no version bump needed |
| Member removed, renamed or retyped | **Breaking.** Error naming the slot, the members, and the required action |
| Breaking difference, `SchemaVersion` already raised | **Accepted.** Snapshot updated |
| `SchemaVersion` raised, but the slot does not override `UpgradePayload` | **Warning** |
| `Key` changed on an existing slot type | **Breaking**, with its own message: the old file is orphaned. Unlike a member diff, a `SchemaVersion` bump does not accept it; the previous entry (old `Key`) is kept so the error repeats every reload until the change is reverted or force-accepted |
| Slot type new to the snapshot | Recorded, no message |

The additive/breaking split follows what the serializer actually does. A new field is absent from old
JSON and receives its default or initializer, which is safe. A removed or renamed field means old data
lands nowhere, and a retyped field means old data lands wrongly or not at all — both need an explicit
upgrade step.

A rename is reported as a removal plus an addition, and the message says so: the tool cannot know
intent, and guessing would be worse than stating the ambiguity.

When a breaking change is unresolved — a removed/retyped member without a version bump, or a `Key`
change (which no version bump ever resolves) — the reload hook **keeps the old snapshot entry**, so the
error repeats on every reload until it is fixed. A one-shot message would be missed in a busy console.

The same principle extends to a pass that cannot see every slot at all: a previous entry with no match
in the current pass (a slot assembly that failed to compile, or a type reflection could not load) is
carried forward into the new snapshot unchanged, rather than dropped. Dropping it would silently erase
those slots from the baseline; when the assembly compiles again, they would come back as "new" and be
recorded without ever having been reviewed as breaking. Only `Update Save Schema Snapshot`, which always
rebuilds the baseline from exactly the types it can currently see, intentionally drops a slot that was
truly deleted from the code. The reload hook additionally skips the write entirely when
`EditorUtility.scriptCompilationFailed` is true, so a broken compile in one assembly cannot touch the
snapshot entries that came from a different, successfully-compiled one.

A snapshot file that cannot be trusted — corrupt or merge-conflicted JSON, or a `formatVersion` newer
than the installed package understands — is handled the same way: one warning naming the file and the
recovery step, an empty snapshot for that pass only, and no write. Treating an unreadable file as "no
baseline" and then overwriting it would turn a recoverable merge conflict into a silent baseline reset.

### Surfaces

- `Ecanakli.SaveSystem.EditorTools.SaveSchemaGuard.Validate()` — the public, dialog-free entry point.
  Runs the check, logs to the console, returns a `SaveSchemaValidationResult` (`Errors`, `Warnings`,
  `ErrorCount`, `WarningCount`, `HasErrors`), and **never writes the snapshot**. This is the form a
  consuming project calls from its own EditMode test to fail CI; every other type in the guard stays
  internal.
- `Tools/Save System/Validate Save Schemas` — calls `SaveSchemaGuard.Validate()` and shows a summary
  dialog built from its result.
- `Tools/Save System/Update Save Schema Snapshot` — accepts the current shapes. Used after bumping the
  version and writing the upgrade code.
- `Tools/Save System/Schema Check On Reload` — a per-developer `EditorPrefs` toggle, on by default,
  controlling the automatic check after every script reload. The automatic path logs errors only.

The comparison itself is a pure function over two snapshot models, which is what makes it unit testable
inside the package and callable from a consuming project's test.

## Alternatives considered

### A runtime shape hash in the envelope

Rejected. It would mean hashing the type's serializable shape at load and storing it alongside `schema`,
then refusing or warning when the hash differs.

**Failure modes.** It detects the problem on the *player's* device, after the build shipped — the one
place where nothing can be done about it. It also cannot distinguish additive from breaking (any added
field changes the hash), so it would either refuse safe changes or warn constantly. Computing it costs
reflection at startup on the platform least able to afford it. And a shape hash in the envelope becomes
part of the on-disk format, so every future change to how the hash is computed is itself a format
change.

The runtime already has the correct refusal: a payload whose `schema` is newer than the build is
rejected and raises `UpdateRequired`. That covers the direction where the client cannot possibly cope.
The other direction is a development-time mistake and belongs in the Editor.

### Golden-file tests only

Rejected as the *only* mechanism, recommended as a complement.

Keeping a real v1 save file in the test project and asserting that it still loads correctly is genuinely
valuable, and [Schema changes](../../Schema-Changes.md) recommends it. But it only covers the versions
somebody remembered to capture, it fails *after* the breaking change is written rather than at the
moment it is written, and the failure message points at a mismatched value rather than at the field
that moved. It also does not exist for a team that never adopted the habit — which is precisely the
team that will ship the incident.

The guard and golden files cover different halves: the guard says "you changed the shape and did not
bump"; a golden file says "your upgrade code is wrong".

### Nothing

Rejected, but honestly the default position for most packages. `SchemaVersion` plus `UpgradePayload`
plus documentation is a complete mechanism, and discipline is free.

It was rejected because discipline is exactly what failed in the incident that motivated this package.
The change that broke saves was small, obviously safe-looking, and made by someone who was not thinking
about serialization at that moment. A check that runs automatically on script reload costs one reflection
pass per compile and catches the case where nobody was thinking about it.

## Documented limits

These are stated here and in [Schema changes](../../Schema-Changes.md), because a guard whose limits are
not understood is worse than none.

- **It only sees code compiled locally.** A change made on another machine and never compiled here is
  invisible; the snapshot diff in code review is what covers that.
- **A rename looks like a removal plus an addition.** The message states the ambiguity rather than
  guessing.
- **Renaming a nested CLR type** without changing any member is flagged as breaking, even though the
  serialized JSON is identical. A false positive, resolved by accepting the snapshot.
- **`[JsonProperty("name")]` wire renames are not tracked.** The guard compares CLR member names, so a
  wire-level rename is invisible to it and a CLR-level rename behind a stable wire name is a false
  positive.
- **Semantic changes are invisible by design.** Seconds becoming milliseconds, or an enum member
  changing meaning, keep the shape identical. No shape-based tool can see them.
- **A slot type without a parameterless constructor is skipped**, with a warning.
- **There is no "slot type removed" message**, and the automatic reload check cannot distinguish a
  genuine deletion from a slot assembly that failed to compile this pass — either way the entry is
  carried forward unchanged. Only `Update Save Schema Snapshot` intentionally drops a slot that was
  truly removed, because it always rebuilds the baseline from scratch.
- **Blindly regenerating the snapshot makes the check pass silently.** This is the real failure mode:
  the tool is only as good as the review of its diff. `ProjectSettings/SaveSystemSchemas.json` must be
  committed and its changes read in code review, in the same way a migration file is.

## Consequences

**Positive**

- The specific incident that motivated the package — a shape change with no version bump — now fails on
  the developer's machine, on the compile that introduces it.
- Additive changes stay silent, so the check has a very low false-alarm rate and is unlikely to be
  switched off.
- The comparison is a pure function, so a consuming project can fail CI on it without running the
  Editor menu.
- The snapshot file doubles as documentation: the serialized shape of every slot, in one reviewable
  file.

**Negative / accepted costs**

- One more committed file in the consuming project, and one more thing a merge can conflict on. A
  conflict in it is resolved by regenerating — which is also the way to defeat the guard, so the review
  habit matters.
- A reflection pass over the loaded assemblies on every script reload. It is small and Editor-only, but
  it is not free.
- Two categories of false positive (nested type rename, `[JsonProperty]` rename), each resolved by
  accepting the snapshot, which slightly erodes the "always read the diff" discipline the guard depends
  on.
