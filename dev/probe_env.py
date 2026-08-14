# encoding: utf-8
"""
Probe the Revit machine for what a runtime-compile dev loop can actually use.

Run this through the Revit MCP (execute_revit_code) before anything else. Which
compiler is available decides the whole design, and it is not safe to guess:

  - Revit 2024 runs on .NET Framework 4.8, whose in-box compiler
    (Framework64\\v4.0.30319\\csc.exe) is the OLD C#5 compiler. It cannot build
    this project - the source uses C#6+ and the csproj declares LangVersion 7.3.
  - A real Roslyn csc.exe ships with Visual Studio / Build Tools / the .NET SDK.
    If one is present, shelling out to it is the most robust option: no in-process
    hosting, no assembly binding redirects.
  - If no Roslyn csc.exe exists, Roslyn must be hosted in-process, which means
    deploying the Microsoft.CodeAnalysis DLLs and probably binding redirects for
    System.Collections.Immutable / System.Reflection.Metadata on .NET 4.8.

Prints a plain report. Nothing is modified.
"""

import System
import os
from System import Environment
from System.IO import Path, File, Directory


def exists(p):
    try:
        return File.Exists(p)
    except Exception:
        return False


print("=" * 68)
print("REVIT / RUNTIME")
print("=" * 68)
app = doc.Application
print("Revit version      : %s (build %s)" % (app.VersionNumber, app.VersionBuild))
print("CLR version        : %s" % Environment.Version)
print("64-bit process     : %s" % Environment.Is64BitProcess)
print("Document           : %s" % doc.Title)

# Revit 2025+ is .NET 8; 2024 and earlier are .NET Framework 4.8.
major = Environment.Version.Major
print("Runtime family     : %s" % ("`.NET (Core) 5+`" if major >= 5 else "`.NET Framework`"))

print("")
print("=" * 68)
print("ROSLYN csc.exe CANDIDATES  (preferred: no in-process hosting needed)")
print("=" * 68)

candidates = []

# Visual Studio 2022/2019, all editions, plus standalone Build Tools.
for pf in [r"C:\Program Files", r"C:\Program Files (x86)"]:
    for ver in ["2022", "2019"]:
        for ed in ["Enterprise", "Professional", "Community", "BuildTools", "Preview"]:
            candidates.append(
                r"%s\Microsoft Visual Studio\%s\%s\MSBuild\Current\Bin\Roslyn\csc.exe" % (pf, ver, ed))

# The .NET SDK carries Roslyn too, under its own versioned folder.
sdk_root = r"C:\Program Files\dotnet\sdk"
if Directory.Exists(sdk_root):
    try:
        for d in Directory.GetDirectories(sdk_root):
            candidates.append(Path.Combine(d, r"Roslyn\bincore\csc.dll"))
    except Exception:
        pass

found_csc = []
for c in candidates:
    if exists(c):
        found_csc.append(c)
        print("FOUND   %s" % c)

if not found_csc:
    print("none found")

print("")
print("--- the in-box .NET Framework compiler (C#5 only - NOT usable here) ---")
legacy = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
print("%s  %s" % ("present" if exists(legacy) else "absent ", legacy))

print("")
print("=" * 68)
print("IN-PROCESS ROSLYN  (fallback: is Microsoft.CodeAnalysis already loaded?)")
print("=" * 68)
loaded = []
for a in System.AppDomain.CurrentDomain.GetAssemblies():
    try:
        n = a.GetName().Name
    except Exception:
        continue
    if n and ("CodeAnalysis" in n or n in ("System.Collections.Immutable", "System.Reflection.Metadata")):
        try:
            loc = a.Location
        except Exception:
            loc = "(dynamic)"
        loaded.append("%s  %s  %s" % (n, a.GetName().Version, loc))

if loaded:
    for l in sorted(set(loaded)):
        print("LOADED  %s" % l)
    print("")
    print("NOTE: versions already loaded in Revit's AppDomain constrain what can be")
    print("      hosted in-process - a mismatch is what forces binding redirects.")
else:
    print("no Roslyn / Immutable / Metadata assemblies loaded in Revit's AppDomain")
    print("(clean slate - good for in-process hosting, but the DLLs must be deployed)")

print("")
print("=" * 68)
print("METAMORPHOSIS: currently loaded assembly + where its source might be")
print("=" * 68)
for a in System.AppDomain.CurrentDomain.GetAssemblies():
    try:
        n = a.GetName().Name
    except Exception:
        continue
    if n in ("Metamorphosis", "System.Data.SQLite", "Newtonsoft.Json"):
        try:
            loc = a.Location
        except Exception:
            loc = "(no location)"
        print("%-20s %-12s %s" % (n, a.GetName().Version, loc))

print("")
print("RevitAPI.dll location (needed as a compile reference):")
try:
    print("  " + System.Type.GetType("Autodesk.Revit.DB.XYZ, RevitAPI").Assembly.Location)
except Exception:
    for a in System.AppDomain.CurrentDomain.GetAssemblies():
        try:
            if a.GetName().Name == "RevitAPI":
                print("  " + a.Location)
        except Exception:
            pass

print("")
print("=" * 68)
print("VERDICT")
print("=" * 68)
if found_csc:
    print("Use the csc.exe path. Most robust: compile out-of-process, load the")
    print("resulting bytes with Assembly.Load(byte[]) - no binding redirects, and")
    print("the exact same command can be reproduced in a terminal for debugging.")
else:
    print("No Roslyn csc.exe on this box. Either install .NET SDK / VS Build Tools")
    print("(simplest), or deploy the Microsoft.CodeAnalysis DLLs and host Roslyn")
    print("in-process, expecting binding-redirect work on .NET Framework.")
