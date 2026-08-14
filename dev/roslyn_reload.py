# encoding: utf-8
"""
Runtime-compile the Metamorphosis diff engine inside a running Revit and run it,
without rebuilding the add-in in Visual Studio and without restarting Revit.

Run through the Revit MCP (execute_revit_code). Edit the C# on disk, run this, read
the JSON. Seconds per iteration instead of a rebuild-and-restart cycle.

WHY ROSLYN AND NOT CodeDom
    CSharpCodeProvider is frozen at C#5. This project declares LangVersion 7.3 and its
    source uses C#6+ (string interpolation, auto-property initialisers), so CodeDom
    cannot compile it at all - and CodeDom does not exist on .NET 8 (Revit 2025+).
    Roslyn compiles the real source unmodified and covers both runtimes.

WHY csc.exe RATHER THAN HOSTING ROSLYN IN-PROCESS
    Same compiler, far fewer failure modes. Hosting Microsoft.CodeAnalysis inside
    Revit on .NET Framework 4.8 usually means deploying ~8 DLLs and fighting binding
    redirects for System.Collections.Immutable / System.Reflection.Metadata against
    whatever Revit already loaded. Shelling out has none of that, and the exact
    command below can be pasted into a terminal when something goes wrong.

WHY THE DLL IS LOADED FROM BYTES
    Revit loads add-ins with Assembly.LoadFrom, which holds a file lock and caches the
    assembly for the AppDomain's life - that is what forces the restart. Assembly.Load
    (byte[]) holds no handle, so the file can be recompiled immediately, and each call
    produces a genuinely new assembly.

KNOWN LIMITS
    - .NET Framework cannot unload assemblies. Every run leaves the previous copy in
      memory. Harmless for a dev loop; restart Revit every few hours.
    - The compiled types have different identity from the ribbon-loaded ones despite
      identical names. Never cast between them. This harness never needs to: the
      engine hands results back as a JSON file.
    - Written and verified for Revit 2024 / .NET Framework 4.8. On Revit 2025+ (.NET 8)
      the framework references below must point at the .NET ref-pack instead, and csc
      is invoked as "dotnet <sdk>/Roslyn/bincore/csc.dll". See notes at REFERENCES.
"""

import System
import clr
import os
from System import Environment, Array, String
from System.IO import Path, File, Directory
from System.Diagnostics import Process, ProcessStartInfo
from System.Reflection import Assembly, BindingFlags

# ----------------------------------------------------------------------------------
# CONFIGURE: point these at your checkout and the snapshot you want to compare against
# ----------------------------------------------------------------------------------
SRC_ROOT = r"C:\Users\jacky.luk\source\Metamorphosis\src\MetamorphosisCore"
DEV_DIR = r"C:\Users\jacky.luk\source\Metamorphosis\dev"
PREVIOUS_SNAPSHOT = r""      # a .db/.sdb snapshot; leave "" to only compile
OUT_DIR = r"C:\Temp\metamorphosis-dev"

MOVE_TOLERANCE = 0.0006      # decimal feet
ROTATE_TOLERANCE = 0.0349    # radians, ~2 degrees

# The diff engine's dependency closure. Deliberately excludes every UI file - none of
# these need WinForms - and excludes Utilities/Settingcs.cs, replaced by SettingsStub.
ENGINE_SOURCES = [
    "ComparisonMaker.cs",
    "ElementIdExtensions.cs",
    r"Objects\Change.cs",
    r"Objects\RevitElement.cs",
    r"Objects\ChangeSummary.cs",
    r"Utilities\RevitUtils.cs",
    r"Utilities\DataUtility.cs",
]

# DataUtility reads its schema-upgrade SQL out of embedded resources. A runtime build
# has none unless we embed them, and the resource IDs must match what it asks for.
RESOURCES = [
    ("databaseFormat.txt", "Metamorphosis.databaseFormat.txt"),
    (r"DBScript\UpgradeToV1.txt", "Metamorphosis.DBScript.UpgradeToV1.txt"),
    (r"DBScript\UpgradeToV1.1.txt", "Metamorphosis.DBScript.UpgradeToV1.1.txt"),
]


def find_csc():
    """Locate a Roslyn csc.exe. NOT the in-box Framework64 one - that is C#5."""
    cands = []
    for pf in [r"C:\Program Files", r"C:\Program Files (x86)"]:
        for ver in ["2022", "2019"]:
            for ed in ["Enterprise", "Professional", "Community", "BuildTools", "Preview"]:
                cands.append(r"%s\Microsoft Visual Studio\%s\%s\MSBuild\Current\Bin\Roslyn\csc.exe"
                             % (pf, ver, ed))
    for c in cands:
        if File.Exists(c):
            return c
    return None


def loaded_assembly_path(simple_name):
    for a in System.AppDomain.CurrentDomain.GetAssemblies():
        try:
            if a.GetName().Name == simple_name:
                loc = a.Location
                if loc:
                    return loc
        except Exception:
            pass
    return None


def main():
    app = doc.Application
    revit_year = int(app.VersionNumber)
    print("Revit %s, CLR %s" % (revit_year, Environment.Version))

    csc = find_csc()
    if not csc:
        print("")
        print("ERROR: no Roslyn csc.exe found. Run dev/probe_env.py for the full report.")
        print("Install the .NET SDK or VS Build Tools, or host Roslyn in-process instead.")
        return
    print("csc: %s" % csc)

    # ---- REFERENCES -------------------------------------------------------------
    # RevitAPI comes from the running process, so the compile always matches the host.
    revit_api = loaded_assembly_path("RevitAPI")
    addin_dir = Path.GetDirectoryName(loaded_assembly_path("Metamorphosis") or revit_api)
    fx = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319"   # Revit 2025+: use the .NET ref pack

    refs = [
        Path.Combine(fx, "mscorlib.dll"),
        Path.Combine(fx, "System.dll"),
        Path.Combine(fx, "System.Core.dll"),
        Path.Combine(fx, "System.Data.dll"),
        Path.Combine(fx, "System.Xml.dll"),
        revit_api,
        Path.Combine(addin_dir, "System.Data.SQLite.dll"),
        Path.Combine(addin_dir, "Newtonsoft.Json.dll"),
    ]
    missing = [r for r in refs if not File.Exists(r)]
    if missing:
        print("")
        print("ERROR: missing compile references:")
        for m in missing:
            print("   " + m)
        return

    # ---- SOURCES ----------------------------------------------------------------
    sources = [Path.Combine(SRC_ROOT, s) for s in ENGINE_SOURCES]
    sources.append(Path.Combine(DEV_DIR, "SettingsStub.cs"))
    missing = [s for s in sources if not File.Exists(s)]
    if missing:
        print("")
        print("ERROR: missing source files (check SRC_ROOT):")
        for m in missing:
            print("   " + m)
        return

    # ---- DEFINES ----------------------------------------------------------------
    # ComparisonMaker branches on these. LONGELEMENTIDS is set from 2024 on, matching
    # the csproj - get it wrong and ElementId construction fails to compile.
    defines = ["REVIT%d" % revit_year]
    if revit_year >= 2024:
        defines.append("LONGELEMENTIDS")

    if not Directory.Exists(OUT_DIR):
        Directory.CreateDirectory(OUT_DIR)
    out_dll = Path.Combine(OUT_DIR, "MetamorphosisEngine.dll")

    args = ["/target:library", "/langversion:7.3", "/nostdlib+", "/optimize-", "/debug:portable",
            "/out:\"%s\"" % out_dll, "/define:%s" % ";".join(defines)]
    for r in refs:
        args.append("/reference:\"%s\"" % r)
    for rel, resid in RESOURCES:
        full = Path.Combine(SRC_ROOT, rel)
        if File.Exists(full):
            args.append("/resource:\"%s\",%s" % (full, resid))
    for s in sources:
        args.append("\"%s\"" % s)

    # ---- COMPILE ----------------------------------------------------------------
    psi = ProcessStartInfo(csc, " ".join(args))
    psi.UseShellExecute = False
    psi.RedirectStandardOutput = True
    psi.RedirectStandardError = True
    psi.CreateNoWindow = True
    p = Process.Start(psi)
    stdout = p.StandardOutput.ReadToEnd()
    stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()

    if p.ExitCode != 0:
        print("")
        print("COMPILE FAILED (exit %d)" % p.ExitCode)
        print(stdout)
        print(stderr)
        return
    print("compiled OK -> %s" % out_dll)

    # ---- LOAD FROM BYTES (no file lock, no restart) ------------------------------
    asm = Assembly.Load(File.ReadAllBytes(out_dll))
    print("loaded assembly: %s" % asm.FullName)

    if not PREVIOUS_SNAPSHOT:
        print("")
        print("PREVIOUS_SNAPSHOT is empty - compile-only run. Set it to compare.")
        return
    if not File.Exists(PREVIOUS_SNAPSHOT):
        print("")
        print("ERROR: snapshot not found: %s" % PREVIOUS_SNAPSHOT)
        return

    # ---- RUN --------------------------------------------------------------------
    cm_type = asm.GetType("Metamorphosis.ComparisonMaker")
    cm = System.Activator.CreateInstance(cm_type, Array[object]([doc, PREVIOUS_SNAPSHOT]))
    cm_type.GetProperty("MoveTolerance").SetValue(cm, MOVE_TOLERANCE, None)
    cm_type.GetProperty("RotateTolerance").SetValue(cm, System.Single(ROTATE_TOLERANCE), None)
    cm_type.GetProperty("AllCategories").SetValue(cm, True, None)

    changes = cm_type.GetMethod("Compare").Invoke(cm, None)
    count = changes.Count
    print("")
    print("changes found: %d" % count)

    json_path = Path.Combine(OUT_DIR, "changes-dev.json")
    ser = cm_type.GetMethod("Serialize", Array[System.Type]([System.String, changes.GetType()]))
    if ser is None:
        for m in cm_type.GetMethods():
            if m.Name == "Serialize" and len(m.GetParameters()) == 2:
                ser = m
                break
    ser.Invoke(cm, Array[object]([json_path, changes]))
    print("json written: %s" % json_path)

    # ---- SUMMARISE the thing this whole exercise exists to verify ----------------
    # Item 1 of the revamp: an element that changed in several ways must now report
    # every one of them, not just whichever was detected first.
    change_type = asm.GetType("Metamorphosis.Objects.Change")
    types_prop = change_type.GetProperty("ChangeTypes")
    desc_prop = change_type.GetProperty("ChangeTypeDescription")

    if types_prop is None:
        print("")
        print("NOTE: this build has no ChangeTypes property - it predates the item 1 fix.")
        return

    compound = 0
    histogram = {}
    for i in range(count):
        c = changes[i]
        tl = types_prop.GetValue(c, None)
        n = tl.Count
        histogram[n] = histogram.get(n, 0) + 1
        if n > 1:
            if compound < 10:
                print("   compound: id=%s  %s" % (
                    change_type.GetProperty("ElementId").GetValue(c, None),
                    desc_prop.GetValue(c, None)))
            compound += 1

    print("")
    print("elements by number of change types:")
    for k in sorted(histogram.keys()):
        print("   %d type(s): %d element(s)" % (k, histogram[k]))
    print("")
    print("compound changes: %d  (before the fix this was always 0)" % compound)


main()
