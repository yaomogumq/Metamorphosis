# dev/ — runtime-compile loop for the diff engine

Iterate on Metamorphosis's comparison logic **without rebuilding the add-in in Visual
Studio and without restarting Revit**. Edit the C#, run one script through the Revit
MCP, read the JSON. Seconds per iteration.

Dev-only. Nothing here ships in the add-in.

## Why this exists

Revit loads add-ins with `Assembly.LoadFrom(path)`, which holds a file lock and caches
the assembly for the life of the AppDomain. That is what forces a Revit restart after
every build. Two changes remove it:

- **Compile only the diff engine, at runtime** — 8 files, no UI dependencies, well
  under a second.
- **Load the result from a byte array** — `Assembly.Load(File.ReadAllBytes(...))` holds
  no file handle, so the DLL can be recompiled immediately, and every call yields a
  genuinely new assembly.

### Why Roslyn and not CodeDom

`CSharpCodeProvider` is frozen at **C#5**. This project declares `LangVersion 7.3` and
its source uses C#6+ throughout — string interpolation (`ComparisonMaker.cs:193`),
auto-property initialisers (`Objects/Change.cs`) — so CodeDom cannot compile it at all,
independently of anything we add. CodeDom also does not exist on .NET 8, so it is a
dead end for Revit 2025+. Roslyn compiles the real source unmodified on both runtimes.

### Why `csc.exe` and not in-process Roslyn

Same compiler, far fewer failure modes. Hosting `Microsoft.CodeAnalysis` inside Revit
on .NET Framework 4.8 means deploying ~8 DLLs and fighting binding redirects for
`System.Collections.Immutable` / `System.Reflection.Metadata` against whatever Revit
already loaded. Shelling out avoids all of it, and the exact command can be pasted into
a terminal when a compile misbehaves. If no Roslyn `csc.exe` is on the machine,
in-process hosting is the fallback — `probe_env.py` reports which applies.

## Use

1. **Probe the machine first.** Run `probe_env.py` through `execute_revit_code`. It
   reports the Revit/CLR version, hunts for a Roslyn `csc.exe`, checks whether any
   Roslyn assemblies are already loaded in Revit's AppDomain, and locates `RevitAPI.dll`
   and the add-in folder. Which compiler exists decides the design, so do not guess.
2. **Edit the paths at the top of `roslyn_reload.py`** — `SRC_ROOT`, `DEV_DIR`,
   `PREVIOUS_SNAPSHOT`, `OUT_DIR`.
3. **Run `roslyn_reload.py`** through `execute_revit_code`. It compiles, loads from
   bytes, runs `ComparisonMaker.Compare()` against your snapshot, writes JSON, and
   prints a histogram of how many change types each element carries.
4. **Edit C#, re-run step 3.** No rebuild, no restart.

Leave `PREVIOUS_SNAPSHOT` empty for a compile-only smoke test.

## What the summary tells you

The run ends with a count of **compound changes** — elements carrying more than one
change type. Before the item-1 fix that number was always zero by construction, because
`compareElements()` returned as soon as `compareParameters()` found anything. A non-zero
count is the fix working; the listed elements are the ones a take-off would previously
have under-reported.

## Three traps this already handles

- **`Settings` cannot be compiled in.** `Utilities/Settingcs.cs` finds `Settings.xml`
  via `Assembly.GetExecutingAssembly().Location`, which is **empty** for an assembly
  loaded from bytes — `XmlDocument.Load` then throws straight out of the
  `ComparisonMaker` constructor. `SettingsStub.cs` replaces it; the engine's only use of
  `Settings` is one boolean at `ComparisonMaker.cs:64`. Tolerances are set on the
  instance directly instead.
- **Conditional compilation symbols must match the host.** `ComparisonMaker` branches on
  `REVIT20xx` and `LONGELEMENTIDS`; the latter applies from 2024 on, matching the csproj.
  `roslyn_reload.py` derives both from the running Revit's version.
- **`DataUtility` reads embedded resources.** A runtime build has none unless they are
  embedded with matching resource IDs, so `databaseFormat.txt` and the two
  `DBScript\UpgradeToV1*.txt` files are passed via `/resource:`.

## Limits

- .NET Framework cannot unload assemblies, so each run leaves the previous copy in
  memory. Harmless for a dev loop — restart Revit every few hours.
- Compiled types have different identity from the ribbon-loaded ones despite identical
  names; never cast between them. This harness never needs to, because the engine
  returns results as a JSON file rather than as objects.
- Written for **Revit 2024 / .NET Framework 4.8**. On Revit 2025+ (.NET 8) the framework
  references must point at the .NET ref-pack and `csc` is invoked as
  `dotnet <sdk>\Roslyn\bincore\csc.dll`. Marked in the script at `REFERENCES`.

## Verification status

Written on macOS with no Revit available, so **nothing here has been executed against a
running Revit**. What has been checked: the 8-file compile unit is closed (only
`Settings` reaches outside it, and the stub covers that — no UI, WinForms or Drawing
references), and all eight files parse cleanly under Roslyn with `REVIT2024;LONGELEMENTIDS`
as well as under 2026, 2023 and 2019 symbol sets. Neither substitutes for a real run;
expect first-run friction in path configuration.
