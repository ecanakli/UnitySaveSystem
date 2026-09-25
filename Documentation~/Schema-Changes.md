# Schema changes

[Back to index](index.md)

Save data outlives the code that wrote it. A player installs 1.0, plays for a month, and then gets
1.3 pushed onto the same device. Their file is still shaped the way 1.0 wrote it.

This page is about the two things that make that survivable: a **schema version** per slot, and an
**upgrade hook** that converts an old payload into a new one, one step at a time. There is also an
Editor guard that shouts at you when you change a data class without doing either.

---

## `SchemaVersion`

Every slot declares the payload version this build writes and understands:

```csharp
public sealed class PlayerSlot : SaveSlot<PlayerData>
{
    public override string Key => "player";
    protected override int SchemaVersion => 3;
}
```

It defaults to `1` and must be at least `1`; the service constructor throws otherwise. The number is
written into every envelope as `"schema"`, alongside `"fmt"` (the envelope format, `1` in this
release).

When a payload is loaded, three things can be true:

| | What happens |
|---|---|
| `schema == SchemaVersion` | Deserialize directly. |
| `schema < SchemaVersion` | Run `UpgradePayload` once per step, then deserialize. |
| `schema > SchemaVersion` | **Refuse.** Nothing is written, nothing is deleted. See [below](#a-save-from-a-newer-build). |

## `UpgradePayload`

```csharp
protected override JObject UpgradePayload(JObject payload, int fromSchemaVersion)
```

The contract is narrow on purpose:

- It receives the payload at `fromSchemaVersion` and must return it at `fromSchemaVersion + 1`.
  **One step per call.** Never jump straight to the current version.
- The core calls it repeatedly: `(payload, 1)`, then `(payload, 2)`, up to `SchemaVersion - 1`.
- It may modify and return the same `JObject`.
- Returning `null` or throwing fails the load. The slot ends up `Failed(NormalizeFailed)`, the file
  is left untouched, and a `SlotLoadIssueDetected` event names the step that failed.
- It works on the **JSON**, not on your C# class. That is the whole point: it must be able to read
  shapes your current classes no longer have.
- It is a hook, so the [hook rules](Slots-And-Profiles.md#hook-rules) apply: synchronous, pure, no
  service calls, no Unity APIs.

The one-step rule is what keeps the chain testable. With it, a v1 file and a v2 file exercise the
same v2→v3 code, so shipping version 7 does not mean writing six new conversions.

---

## A worked example

Version 1 of the player data:

```csharp
public sealed class PlayerData
{
    public int Level { get; set; } = 1;
    public int Coins { get; set; }                        // int
    public string LastSkin { get; set; }                  // single value
}
```

On disk, that looks like:

```json
{"fmt":1,"schema":1,"rev":12, ... ,"data":{"Level":7,"Coins":1200,"LastSkin":"red"}}
```

Two changes ship over the next two releases:

- **v1 → v2:** `Coins` becomes a `long`, because players hit the `int` ceiling.
- **v2 → v3:** `LastSkin` (one value) becomes `OwnedSkins` (a list), and `LastSkin` is removed.

The current class:

```csharp
public sealed class PlayerData
{
    public int Level { get; set; } = 1;
    public long Coins { get; set; }
    public List<string> OwnedSkins { get; set; } = new List<string>();
}
```

The slot:

```csharp
using System.Collections.Generic;
using Ecanakli.SaveSystem;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[Preserve]
public sealed class PlayerSlot : SaveSlot<PlayerData>
{
    public override string Key => "player";
    public override SyncMode SyncMode => SyncMode.CloudSync;

    protected override int SchemaVersion => 3;

    protected override JObject UpgradePayload(JObject payload, int fromSchemaVersion)
    {
        switch (fromSchemaVersion)
        {
            case 1:
                return UpgradeV1ToV2(payload);
            case 2:
                return UpgradeV2ToV3(payload);
            default:
                return payload;
        }
    }

    // v1 -> v2: Coins widened from int to long. JSON numbers do not care, but be explicit.
    private static JObject UpgradeV1ToV2(JObject payload)
    {
        JToken coins = payload["Coins"];
        payload["Coins"] = coins?.Type == JTokenType.Integer ? (long)coins : 0L;
        return payload;
    }

    // v2 -> v3: LastSkin (string) became OwnedSkins (list). The old key is removed.
    private static JObject UpgradeV2ToV3(JObject payload)
    {
        var owned = new JArray();
        string lastSkin = (string)payload["LastSkin"];
        if (!string.IsNullOrEmpty(lastSkin))
        {
            owned.Add(lastSkin);
        }

        payload["OwnedSkins"] = owned;
        payload.Remove("LastSkin");
        return payload;
    }

    protected override void Normalize(PlayerData data)
    {
        data.OwnedSkins ??= new List<string>();
    }
}
```

What actually happens when a v1 file is opened by this build:

```
read file            schema = 1, target = 3
UpgradePayload(payload, 1)   -> payload is now v2
UpgradePayload(payload, 2)   -> payload is now v3
deserialize into PlayerData
Normalize(data)
slot is Ready
```

You do not write the loop, do not set the version field inside the payload, and do not compare
versions yourself. The envelope carries the version and the core drives the chain.

### When you need an upgrade step

| Change to `TData` | Needs a version bump and an upgrade step? |
|---|---|
| Add a field with a sensible default | No. A missing property is simply left at its default. |
| Remove a field | Yes. The old value is dropped; do it deliberately, and write down that the data is gone. |
| Rename a field | Yes. To the JSON this is "one property vanished, another appeared", and nothing carries the value across. |
| Change a field's type | Yes. Even when it seems compatible, be explicit about the conversion. |
| Change the *meaning* of a field (seconds to milliseconds, a re-used enum value) | Yes, and this is the dangerous one: nothing in the shape changes, so no tool can see it. |
| Add a new nested class | No. |
| Restructure a nested class | Yes. |

Additive changes are the reason `MissingMemberHandling.Ignore` and `NullValueHandling.Ignore` are
pinned: adding a field is genuinely free, and removing one does not make an old file unreadable.

---

## A save from a newer build

A player on two devices updates one of them. Or they install an old APK over new data. Either way,
the build can meet a payload whose `fmt` or `schema` is higher than it supports.

The rule: **refuse, never guess.**

- Nothing is written to that file or cloud key. It is not deleted, downgraded or quarantined.
- The slot is `Failed(SchemaTooNew)` locally, or the restore outcome is `SkippedSchemaTooNew` for a
  cloud value.
- Uploads for that slot are skipped with `Skipped(SchemaTooNew)`, so the newer data cannot be
  overwritten by this build.
- `UpdateRequired` is raised once per slot, per source (local or cloud), per epoch, and
  `RestoreReport.RequiresAppUpdate` is set.

```csharp
_save.UpdateRequired += info =>
{
    Debug.LogWarning($"'{info.SlotKey}' from {info.Source} needs a newer build: " +
                     $"fmt {info.FoundFormat}/{info.SupportedFormat}, " +
                     $"schema {info.FoundSchema}/{info.SupportedSchema}");
    _updatePrompt.Show();
};
```

This is the one refusal the package cannot resolve on its own. Every other refusal clears within the
next restore-and-upload cycle; this one needs a new app build. That is why it has a dedicated event
instead of just a log line. Show an update prompt.

Meanwhile the game keeps running on whatever that slot last had locally, and the player can keep
playing; their changes stay on disk and upload once the build catches up.

---

## The Editor guard

The failure this guards against: you rename a field, ship it, and every existing save silently loads
that field as its default. Nothing crashes. Nobody notices until the reviews arrive.

The guard takes a snapshot of every slot's data shape and compares it against the last accepted one.

### Menu items

| Menu item | What it does |
|---|---|
| `Tools/Save System/Validate Save Schemas` | Calls `SaveSchemaGuard.Validate()`, logs the result and shows a summary dialog. **Never writes the snapshot.** |
| `Tools/Save System/Update Save Schema Snapshot` | Force-accepts the current shapes as the new baseline. Use it after you have bumped `SchemaVersion` and written the upgrade step. |
| `Tools/Save System/Schema Check On Reload` | A checkable toggle, on by default. When on, the comparison runs after every script reload, logs only (no dialog), and persists the snapshot for slots whose change was not breaking. |

The snapshot lives in `ProjectSettings/SaveSystemSchemas.json`. **Commit it.** It is the shared
memory of what your save data looked like at the last accepted state, and its diff in a pull request
is the review artefact. A reload that changes nothing does not rewrite the file, so an unrelated
compile does not create disk churn or a spurious diff.

Only production assemblies are compared: the guard asks `CompilationPipeline` which assemblies ship
in a player build, so a real Test Assembly is excluded regardless of its name (`MyGame.Test`,
`MyGame.Testing`, `MyGame.TESTS`), and a production assembly that merely has "Tests" in its name
(`MyGame.ABTests`) is not. A name-based fallback only applies to an assembly `CompilationPipeline`
does not track at all, such as a precompiled DLL.

### Failing CI on a breaking change

`Ecanakli.SaveSystem.EditorTools.SaveSchemaGuard.Validate()` is the public, dialog-free entry point.
It runs the same comparison as `Validate Save Schemas`, logs to the Console, and returns a
`SaveSchemaValidationResult` with `Errors`, `Warnings`, `ErrorCount`, `WarningCount` and `HasErrors`.
It never writes the snapshot. Call it from an EditMode test in your own project:

```csharp
[Test]
public void SaveSchemas_HaveNoUnresolvedBreakingChanges()
{
    SaveSchemaValidationResult result = SaveSchemaGuard.Validate();
    Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
}
```

### What it compares

For each slot type it records `Key`, `SchemaVersion`, whether `UpgradePayload` is overridden, and
the shape of `TData` as a sorted list of `path : typeName` entries.

- A member is a public instance property with both a getter and a setter, or a public instance
  field. `[JsonIgnore]` members are excluded.
- Entries are sorted by path, so reordering declarations is never a change.
- Primitives, `string`, enums, `DateTime`, `Guid`, `Uri`, `TimeSpan`, `object` and `JToken` types
  are leaves. Everything else recurses (`Stats.Strength`).
- Collections and dictionaries record their own line (`List<string>`, `Dictionary<string,int>`) and
  recurse into non-leaf element types under `path[]`.
- Cycles are cut by a recursion-path set, and depth is capped at 8.

### Breaking versus additive

| Case | Result |
|---|---|
| A member was **added** only | Additive. The snapshot is updated, no message. |
| A member was **removed or retyped** | **Breaking.** An error naming the slot and the members, telling you to bump `SchemaVersion` and handle it in `UpgradePayload`. |
| A breaking change, with `SchemaVersion` already raised | Accepted. The snapshot is updated. |
| `SchemaVersion` raised without an `UpgradePayload` override | Warning. |
| `Key` changed | Its own error: the old file will be orphaned. **Never auto-resolved** — unlike a member diff, a `SchemaVersion` bump does not accept it, so it repeats on every reload until you revert the `Key` or run `Update Save Schema Snapshot`. |
| A slot type the snapshot has never seen | Recorded silently. |

A rename shows up as a removal plus an addition, and the message says so: the guard cannot know
whether you meant to move the value or drop it.

### The workflow when it reports an error

1. Read the error. It names the slot, the members, and whether they were removed or retyped.
2. Decide what should happen to existing player data for each one. "Nothing, it is gone" is a valid
   answer — just make it a decision rather than an accident.
3. Raise `SchemaVersion` by one.
4. Add the step to `UpgradePayload` for the version you just left behind.
5. Compile. The reload check now classifies the slot as **Accepted** and updates the snapshot
   entry, and the error stops repeating.
6. Test it against a real old save (next section).
7. Commit the code **and** `ProjectSettings/SaveSystemSchemas.json` together.

Until step 3 is done, the reload hook deliberately keeps the old snapshot entry for that slot, so the
error repeats on every reload instead of going quiet.

### What the guard cannot see

Know these before you trust it:

- It only sees code compiled locally. A slot in a package or assembly that is not currently
  compiling is invisible.
- A rename looks like a removal plus an addition.
- Renaming a nested CLR type without changing any of its members is flagged as breaking even though
  the JSON is identical.
- `[JsonProperty("name")]` wire renames are not tracked. The guard compares CLR member names.
- **Semantic-only changes are invisible by design.** Seconds to milliseconds, a re-used enum value,
  an inverted boolean: the shape is the same, so nothing is reported. These are the changes most
  likely to corrupt a save, and only you can catch them.
- A slot type with no parameterless constructor is skipped, with a warning.
- There is no "slot type removed" message, and the automatic reload check cannot tell a genuine
  deletion from a slot assembly that simply failed to compile this pass: either way, the entry is
  carried forward into the baseline unchanged. Only the explicit `Update Save Schema Snapshot`
  command, which always rebuilds the baseline from the types it can currently see, drops a slot that
  was truly removed from the code. A duplicate entry for the same slot type in a hand-edited or
  merge-conflicted snapshot is collapsed to one (the last one) rather than carried forward forever.
- The reload check does not write the snapshot at all while `EditorUtility.scriptCompilationFailed`
  is true, so a broken compile never erases slots from a different assembly than the one that failed.
- A corrupt or merge-conflicted `SaveSystemSchemas.json`, or one with a `formatVersion` newer than
  the installed package understands, is never silently reset: the guard logs one warning naming the
  file and the recovery step, treats it as empty for that pass only, and does **not** overwrite the
  file. Resolve the conflict (or update the package) and run `Update Save Schema Snapshot` to write a
  clean baseline.
- Regenerating the snapshot blindly makes the check pass silently. That is why the snapshot file is
  committed: review its diff.

---

## Keep an old save and load it in a test

The guard tells you that you changed something. Only a test tells you that the upgrade produced the
right values.

Make this a habit: whenever you bump `SchemaVersion`, copy a **real** save file from the previous
version into the repository and write a test that loads it.

```
Tests/SaveData/player.v1.json
Tests/SaveData/player.v2.json
```

```csharp
[Test]
public void PlayerSlot_V1Payload_UpgradesToCurrentSchema()
{
    // Arrange: a real v1 file, kept byte for byte.
    string json = File.ReadAllText(TestPaths.SaveData("player.v1.json"));
    ...

    // Assert the values, not just that it loaded.
    Assert.AreEqual(1200L, coins);
    Assert.AreEqual(new[] { "red" }, ownedSkins);
    Assert.AreEqual(7, level);
}
```

What makes these tests worth the trouble:

- Keep one fixture **per shipped schema version**, forever. A v1 fixture keeps exercising the v1→v2
  step long after v1 stopped being current.
- Use files a real build produced, not files you hand-wrote to match your expectations. The bugs
  live in the difference between the two.
- Assert the **values**. "It did not throw" passes just as happily when every field came back as
  its default, which is the failure you are trying to catch.
- Add one fixture that is deliberately truncated or corrupt, and assert that loading it does not
  throw out of the service.

---

## Related pages

- [Slots and profiles](Slots-And-Profiles.md) — the other hooks, and migrations that need external
  content.
- [Sync and recovery](Sync-And-Recovery.md) — how a too-new cloud value is handled.
- [Troubleshooting](Troubleshooting.md) — `SchemaTooNew`, and what a failing schema guard means.
