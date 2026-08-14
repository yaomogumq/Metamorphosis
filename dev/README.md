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
2. **Check the paths at the top of `roslyn_reload.py`.** They are set for HT2524; only
   `REPO`, `ADDIN` and `CSC` change between machines.
3. **Take a baseline snapshot** with the installed add-in — no rebuild needed:
   `Metamorphosis.Snapshot.Export(doc, r"C:\Temp\metamorphosis-dev\baseline.sdb")`.
4. **Optionally plant a known compound change** with `doctor_snapshot.py`, which edits
   the *snapshot* rather than the model, so nothing in Revit is touched.
5. **Run `roslyn_reload.py`.** It compiles, loads from bytes, runs
   `ComparisonMaker.Compare()`, and prints a histogram of how many change types each
   element carries.
6. **Edit C#, re-run step 5.** No rebuild, no restart. About two seconds a lap.

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

## Verification status — VERIFIED end to end

Run on **HT2524 / Revit 2024.2 / .NET Framework 4.8** against a real 55,110-element
structural model, over the Revit MCP.

| Step | Result |
|---|---|
| Runtime compile (8 files) | **0.49 s** |
| Load from bytes | `Assembly.Location` empty — no file lock, no restart |
| Embedded resources | all three present with the right ids |
| Compare (Structural Framing) | **1.5 s** |
| Baseline snapshot export | 27 MB / 29 s — see note on item 3 below |

### The A/B that proves item 1

`doctor_snapshot.py` planted one compound change — element **950146** moved 1 ft *and*
had its `Comments` parameter altered. The same comparison was then run twice, against
engines compiled from the pre-fix `master` and from the fixed branch:

```
OLD (master)   ChangeType  : ParameterChange
               Description : Comments From: DOCTORED-BASELINE to
               MoveDescription: None          <-- the 1 ft move is simply gone

NEW (branch)   ChangeTypes : ParameterChange + Move
               Description : Comments From: DOCTORED-BASELINE to ; Location Offset 30.48cm
```

That missing move is the take-off blind spot, reproduced and then closed.

### Three things the live run corrected

Found only by compiling for real — worth knowing before trusting a static check:

- **`RevitAPIUI.dll` is required.** `RevitUtils.GetExtents` takes an
  `Autodesk.Revit.UI.UIApplication`, written fully-qualified inline rather than through
  a `using`, so grepping the using-lines said "no UI dependency" and the first compile
  failed on it.
- **`/preferreduilang:en-US` matters.** Compiler diagnostics came back in the machine's
  OS language (Chinese), which makes automated error handling unreliable.
- **All MCP output must be ASCII.** pyRevit's routes handler serialises responses with
  an ASCII JSON encoder, so a single degree sign in a parameter value throws *after*
  the code has already run — the work happens and the result is lost. Every print goes
  through an `asc()` filter.

Incidental datapoint for revamp item 3: a snapshot export of 55,110 elements /
407,831 EAV rows took **29 seconds**, all of it single-threaded one-INSERT-per-row.
