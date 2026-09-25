# ADR-002: DI integrations ship as gated assemblies inside one package

- **Status:** Accepted, implemented in 0.1.0. Supersedes an earlier "one package per framework" plan.
- **Related:** [Architecture section 9](../Architecture.md#9-assemblies),
  [Dependency injection](../../Dependency-Injection.md)

## Context

The core is DI-agnostic by design ([ADR-001](ADR-001-Core-Save-Model.md)), but a game using a container
still wants an installer, a binding helper for slots, automatic listener collection, and a bridge from
the package's events to the container's messaging. That integration code must reference the container's
assembly, which most consuming projects do not have.

The container itself can arrive in two very different ways, and both must work:

- **Copied into `Assets/`** (the common Zenject setup). There is no package manifest entry to detect,
  so nothing can turn the integration on automatically.
- **Installed as a UPM package** (Extenject from a scoped registry). Here the package manager knows the
  dependency and its version.

The original plan was a second package, `com.ecanakli.savesystem.zenject`, depending on the core. That
plan was reversed before implementation.

## Decision

**One package. Each DI framework integration is a separate assembly inside it, compiled only when its
define symbol is present.**

- Folder `DependencyInjection/Zenject/`, assembly
  `Ecanakli.SaveSystem.DependencyInjection.Zenject`, namespace
  `Ecanakli.SaveSystem.DependencyInjection`, symbol `ECANAKLI_SAVESYSTEM_DI_ZENJECT`.
- The asmdef carries both `defineConstraints` on that symbol **and** a `versionDefines` entry on the
  UPM Extenject package, so a UPM install activates the integration with no manual step.
- For an `Assets/` install, `Tools/Save System/Zenject Integration` is a checkable menu item that writes
  the symbol to every non-obsolete build target and removes it again. It refuses with a dialog when no
  assembly named `Zenject` exists, so the symbol is never written into a project that would fail to
  compile.
- Assembly names may contain the framework name; **namespaces never do**. Every future integration
  shares `Ecanakli.SaveSystem.DependencyInjection`, so a game's `using` lines survive a container
  change. A guard test asserts that no namespace segment is `Zenject`, `VContainer` or `Editor`, and
  that no public type name is duplicated across package assemblies — two integrations must be able to
  compile side by side.
- The same shape covers the non-DI optional assembly: `Ecanakli.SaveSystem.UnityCloudSave` is gated by
  `versionDefines` on the Unity Cloud Save package.

Adding a framework later requires no restructuring: a new gated folder, one `versionDefines` entry, one
menu method pair if `Assets/` installs are supported, a test folder, a sample and a docs section. The
core and the existing integration are untouched.

## Alternatives considered

### A separate package per framework

Rejected, and this was the original plan.

**Concrete failure mode.** UPM cannot resolve a git dependency declared inside a package's
`package.json`. So the integration package's dependency on the core does not resolve from a git URL,
and the consumer must add **both** URLs by hand. Forget the core URL and package resolution fails for
the *entire project*, not just this package: a manifest-level error that blocks everything, with an
error message that does not obviously point at the missing line.

**Second failure mode.** Two packages mean two tags that must move together. A consumer who bumps one
and not the other compiles the integration against a different core API version. That failure is a
compile error at best and a behavioural mismatch at worst, and neither repository can detect it.

It also doubles the maintenance surface: two `package.json` files, two changelogs, two tags, two
release checklists.

### The integration shipped as a sample

Rejected.

**Concrete failure modes.** `Samples~` content is copied into `Assets/Samples/<package>/<version>/` on
import, so it never updates with the package — a fixed bug stays fixed only in the package, not in the
copy the game compiles. Re-importing after an upgrade creates a *second* versioned folder containing an
asmdef with the same name, which is a duplicate-assembly compile error until the old folder is deleted
by hand. And because folders ending in `~` are not compiled, the integration would never be built or
tested in the development host at all: sample rot with no signal.

### `#if` blocks inside the core assembly

Rejected.

**Concrete failure mode.** For the code inside the `#if` to compile, the *core* asmdef must reference
`Zenject`. An asmdef reference to a missing assembly is a hard error, so the core would fail to compile
for every consumer who does not have Zenject — which is the majority, and includes the package's own
minimal test project. The preprocessor gate does not help, because assembly references are resolved
before compilation.

### Detecting the framework by reflection at runtime

Rejected. It requires a string-typed API with no compile-time safety, is exactly the pattern IL2CPP
managed stripping breaks, and gives no editor-time error when a binding name is wrong. It would also
have to reflect over the container's generic binding API, which is the least stable part of any
container.

### Automatically adding the define from an `[InitializeOnLoad]` detector

Rejected, and this is the most tempting option, so the reason matters.

**Concrete failure mode.** It cannot handle *removal*, which is the case that actually hurts. Once
Zenject is deleted from a project while the symbol is still set, the integration assembly fails to
compile with `CS0246`. Compile errors block the domain reload, so the `[InitializeOnLoad]` detector
never runs again — the very code that was supposed to clean up the symbol is dead exactly when it is
needed. The project owner is left editing `ProjectSettings.asset` or Player Settings by hand, in Safe
Mode.

It also silently edits the consumer's `ProjectSettings`, which is a file people review in code review
and expect to change only when they change it.

The menu toggle is the deliberate opposite: explicit, reversible **before** the framework is removed,
and harmless if it is never clicked.

### A `csc.rsp` define

Rejected: `defineConstraints` only evaluate Player Settings symbols and the assembly's own
`versionDefines`. A symbol from `csc.rsp` would not gate the assembly at all.

## Consequences

**Positive**

- One install URL, one tag, one changelog. Every part of the package always comes from the same commit.
- A UPM container install needs no manual step: `versionDefines` turn the integration on and off with
  the dependency.
- A project with no container and no cloud package compiles only the core; nothing else is even built.
- The integration is compiled and tested in the development host on every run, so it cannot rot.

**Negative / accepted costs**

- A project with the container copied into `Assets/` must set one define, once, and commit
  `ProjectSettings`. If they forget, game code referencing the integration fails with `CS0234`. This is
  called out in the troubleshooting page as the first entry.
- The define must exist for **every** build target, which is why the menu writes all of them. A symbol
  set only for the active target works in the Editor and fails on a platform switch or a device build.
- Removing the framework while the symbol is still set produces `CS0246` in the integration assembly
  until the symbol is removed. The documented order is: toggle off first, then delete the framework.
- Unity 6 build profiles that override Player Settings may need the symbol in the profile as well; this
  is documented as a first-device-build check.

**Enforced by tests**

- `ScriptingDefineSymbolsTests` — adding a symbol is idempotent, removing one preserves the other
  symbols and their order, and empty input is handled.
- `AssemblyNamingGuardTests` — no forbidden namespace segment, no framework name in a public type name,
  no public type name duplicated across package assemblies.
- The Zenject integration test assembly compiles and runs only with the define on, and the release
  checklist includes a build with the define off to prove the core still compiles alone.
