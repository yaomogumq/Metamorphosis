# encoding: utf-8
"""
Runtime-compile the Metamorphosis diff engine inside a running Revit and run it,
without rebuilding the add-in in Visual Studio and without restarting Revit.

Run through the Revit MCP (execute_revit_code). Edit the C# on disk, run this, read
the result. VERIFIED on HT2524 / Revit 2024.2 / .NET Framework 4.8:
compile 0.49s, compare 1.5s for one category. Compare that with a VS rebuild plus a
Revit restart on a 55,000-element model.

WHY ROSLYN AND NOT CodeDom
    CSharpCodeProvider is frozen at C#5. This project declares LangVersion 7.3 and its
    source uses C#6+ (string interpolation, auto-property initialisers), so CodeDom
    cannot compile it at all - and CodeDom does not exist on .NET 8 (Revit 2025+).

WHY csc.exe RATHER THAN HOSTING ROSLYN IN-PROCESS
    Same compiler, far fewer failure modes. This machine proves the point: pyRevit has
    already loaded Microsoft.CodeAnalysis 4.10, and System.Collections.Immutable is
    loaded TWICE at different versions (1.2.5 from Revit, 8.0.0 from pyRevit). Hosting
    Roslyn in-process would have to be reconciled against that. Shelling out does not.

WHY THE DLL IS LOADED FROM BYTES
    Revit loads add-ins with Assembly.LoadFrom, which holds a file lock and caches the
    assembly for the AppDomain's life - that is what forces the restart. Assembly.Load
    (byte[]) holds no handle; the loaded assembly's Location comes back empty.

LIMITS
    - .NET Framework cannot unload assemblies, so each run leaves the previous copy in
      memory. Harmless for a dev loop; restart Revit every few hours.
    - Compiled types have different identity from the ribbon-loaded ones despite
      identical names. Never cast between them.
    - Verified for Revit 2024 / .NET Framework 4.8. On Revit 2025+ (.NET 8) point the
      framework references at the .NET ref-pack and invoke csc as
      "dotnet <sdk>\\Roslyn\\bincore\\csc.dll". See REFERENCES below.
"""

import System
from System.IO import Path, File, Directory
from System.Diagnostics import Process, ProcessStartInfo
from System.Reflection import Assembly
from System.Collections.Generic import List
from Autodesk.Revit.DB import Category, BuiltInCategory

# ----------------------------------------------------------------------------------
# CONFIGURE
# ----------------------------------------------------------------------------------
REPO = r"C:\Users\HT2524\source\Metamorphosis"
OUT_DIR = r"C:\Temp\metamorphosis-dev"
PREVIOUS_SNAPSHOT = Path.Combine(OUT_DIR, "baseline.sdb")   # "" = compile-only
CSC = r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
ADDIN = r"C:\Users\HT2524\AppData\Roaming\Autodesk\Revit\Addins\2024\Metamorphosis"
REVIT_DIR = r"C:\Program Files\Autodesk\Revit 2024"

# Narrow the compare to keep the loop fast. None = every category.
ONLY_CATEGORY = BuiltInCategory.OST_StructuralFraming

MOVE_TOLERANCE = 0.0006      # decimal feet
ROTATE_TOLERANCE = 0.0349    # radians, ~2 degrees

SRC = Path.Combine(REPO, r"src\MetamorphosisCore")
DEV = Path.Combine(REPO, "dev")
FX = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

# The diff engine's dependency closure. No WinForms, no Drawing - but RevitAPIUI IS
# needed: RevitUtils.GetExtents takes an Autodesk.Revit.UI.UIApplication, written
# fully-qualified inline rather than through a using, so grepping the usings misses it.
ENGINE_SOURCES = [
    "ComparisonMaker.cs",
    "ElementIdExtensions.cs",
    r"Objects\Change.cs",
    r"Objects\RevitElement.cs",
    r"Objects\ChangeSummary.cs",
    r"Utilities\RevitUtils.cs",
    r"Utilities\DataUtility.cs",
]

# DataUtility reads its schema-upgrade SQL from embedded resources. A runtime build has
# none unless we embed them, and the ids must match exactly what it asks for.
RESOURCES = [
    ("databaseFormat.txt", "Metamorphosis.databaseFormat.txt"),
    (r"DBScript\UpgradeToV1.txt", "Metamorphosis.DBScript.UpgradeToV1.txt"),
    (r"DBScript\UpgradeToV1.1.txt", "Metamorphosis.DBScript.UpgradeToV1.1.txt"),
]


def asc(x):
    """pyRevit's routes handler serialises the response with an ASCII JSON encoder. One
    non-ASCII byte anywhere in the output - a degree sign in a parameter value, a CJK
    character in a family name - throws AFTER the code has already run, so the work
    happens but the result is lost. Everything printed goes through here."""
    if x is None:
        return "None"
    return "".join([c if 32 <= ord(c) < 127 else "?" for c in str(x)])


def compile_engine(sources, out_dll, defines):
    refs = [Path.Combine(FX, n) for n in
            ["mscorlib.dll", "System.dll", "System.Core.dll", "System.Data.dll", "System.Xml.dll"]]
    # REFERENCES: on Revit 2025+ swap the five above for the .NET ref-pack equivalents.
    refs += [Path.Combine(REVIT_DIR, "RevitAPI.dll"),
             Path.Combine(REVIT_DIR, "RevitAPIUI.dll"),
             Path.Combine(ADDIN, "System.Data.SQLite.dll"),
             Path.Combine(ADDIN, "Newtonsoft.Json.dll")]

    missing = [r for r in refs if not File.Exists(r)] + [s for s in sources if not File.Exists(s)]
    if missing:
        print("MISSING:")
        for m in missing:
            print("   " + asc(m))
        return None

    args = ["/target:library", "/langversion:7.3", "/nostdlib+", "/optimize-",
            "/preferreduilang:en-US",     # else diagnostics arrive in the OS language
            '/out:"%s"' % out_dll, "/define:%s" % ";".join(defines)]
    for r in refs:
        args.append('/reference:"%s"' % r)
    for rel, rid in RESOURCES:
        f = Path.Combine(SRC, rel)
        if File.Exists(f):
            args.append('/resource:"%s",%s' % (f, rid))
    for s in sources:
        args.append('"%s"' % s)

    psi = ProcessStartInfo(CSC, " ".join(args))
    psi.UseShellExecute = False
    psi.RedirectStandardOutput = True
    psi.RedirectStandardError = True
    psi.CreateNoWindow = True

    t0 = System.DateTime.Now
    p = Process.Start(psi)
    out = p.StandardOutput.ReadToEnd()
    err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    took = System.DateTime.Now - t0

    if p.ExitCode != 0:
        print("COMPILE FAILED (exit %d)" % p.ExitCode)
        print(asc(out)[:4000])
        print(asc(err)[:1500])
        return None

    print("compiled in %s -> %s (%d bytes)" % (took, asc(out_dll), System.IO.FileInfo(out_dll).Length))
    return Assembly.Load(File.ReadAllBytes(out_dll))   # no file lock, no restart


def main():
    year = int(doc.Application.VersionNumber)
    defines = ["REVIT%d" % year]
    if year >= 2024:
        defines.append("LONGELEMENTIDS")     # matches the csproj; 2024 onward
    print("Revit %d   defines: %s" % (year, ";".join(defines)))

    if not Directory.Exists(OUT_DIR):
        Directory.CreateDirectory(OUT_DIR)

    sources = [Path.Combine(SRC, s) for s in ENGINE_SOURCES]
    sources.append(Path.Combine(DEV, "SettingsStub.cs"))

    asm = compile_engine(sources, Path.Combine(OUT_DIR, "MetamorphosisEngine.dll"), defines)
    if asm is None:
        return
    print("loaded from bytes; Location='%s' (empty means no file lock)" % asm.Location)

    if not PREVIOUS_SNAPSHOT or not File.Exists(PREVIOUS_SNAPSHOT):
        print("")
        print("No snapshot set - compile-only run.")
        print("Take one with the installed add-in: Metamorphosis.Snapshot.Export(doc, path)")
        return

    cmt = asm.GetType("Metamorphosis.ComparisonMaker")
    cm = System.Activator.CreateInstance(cmt, System.Array[object]([doc, PREVIOUS_SNAPSHOT]))
    cmt.GetProperty("MoveTolerance").SetValue(cm, MOVE_TOLERANCE, None)
    cmt.GetProperty("RotateTolerance").SetValue(cm, System.Single(ROTATE_TOLERANCE), None)
    if ONLY_CATEGORY is not None:
        cmt.GetProperty("AllCategories").SetValue(cm, False, None)
        cats = List[Category]()
        cats.Add(doc.Settings.Categories.get_Item(ONLY_CATEGORY))
        cmt.GetProperty("RequestedCategories").SetValue(cm, cats, None)

    t0 = System.DateTime.Now
    changes = cmt.GetMethod("Compare").Invoke(cm, None)
    print("compared in %s: %d change(s)" % (System.DateTime.Now - t0, changes.Count))

    cht = asm.GetType("Metamorphosis.Objects.Change")
    p_types = cht.GetProperty("ChangeTypes")
    if p_types is None:
        print("This build predates the item-1 fix (no ChangeTypes property).")
        return
    p_id = cht.GetProperty("ElementId")
    p_desc = cht.GetProperty("ChangeTypeDescription")
    p_cd = cht.GetProperty("ChangeDescription")

    hist = {}
    compound = 0
    for i in range(changes.Count):
        c = changes[i]
        n = p_types.GetValue(c, None).Count
        hist[n] = hist.get(n, 0) + 1
        if n > 1:
            if compound < 10:
                print("   compound: id=%s  %s  |  %s" % (
                    asc(p_id.GetValue(c, None)), asc(p_desc.GetValue(c, None)),
                    asc(p_cd.GetValue(c, None))[:110]))
            compound += 1

    print("")
    print("elements by number of change types:")
    for k in sorted(hist.keys()):
        print("   %d type(s): %d element(s)" % (k, hist[k]))
    print("")
    print("compound changes: %d   (structurally always 0 before the item-1 fix)" % compound)


main()
