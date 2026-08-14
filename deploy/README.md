# deploy/ — install by copying files

A Revit add-in is a `.addin` manifest plus a folder of assemblies. That is the whole
mechanism. Revit hosts its own CLR, so there is no runtime to install, and
`System.Data.SQLite`'s native interop needs no registration — it only has to sit beside
the managed assembly, which copying already achieves.

**No admin rights are needed** for a per-user install, which is why this exists: the MSI
takes an admin-only upgrade path that fails on a locked-down machine.

## Install

```powershell
# 1. build the release you want
dotnet build src\MetamorphosisCore\MetamorphosisCore.csproj -c Release -p:RevitVersion=2024

# 2. close Revit, then copy it into place
.\deploy\install.ps1 -RevitVersion 2024 -Source .\bin\Release2024
```

Add `-AllUsers` to install for everyone instead of just you; that one *does* need
elevation.

**Revit must be closed.** It loads add-ins with `Assembly.LoadFrom`, which holds a file
lock for the life of the process, so `Metamorphosis.dll` cannot be replaced while Revit
is running. The script checks and refuses rather than half-installing.

Any existing install is renamed to `Metamorphosis.bak-<timestamp>` before the new files
land, so a failed upgrade never costs you a working one.

## Where things go

| | |
|---|---|
| Per-user (default) | `%APPDATA%\Autodesk\Revit\Addins\<year>\` |
| All users, ≤ 2026 | `%ProgramData%\Autodesk\Revit\Addins\<year>\` |
| All users, 2027+ | `%ProgramFiles%\Autodesk\Revit\Addins\<year>\` |

Revit 2027 moved the all-user location out of `ProgramData` into `Program Files` for
security reasons. **The per-user path is unchanged and still supported**, which is the
one that matters here.

The manifest sits *beside* the payload folder, not inside it, and refers to
`.\Metamorphosis\Metamorphosis.dll` relative to itself.

## What gets installed

About 1.3 MB:

```
Metamorphosis.addin
Metamorphosis\
    Metamorphosis.dll          the add-in
    Metamorphosis.dll.config
    Settings.xml               colours and tolerances; optional, defaults apply without it
    Newtonsoft.Json.dll
    System.Data.SQLite.dll
    x64\SQLite.Interop.dll     native, architecture-specific
    x86\SQLite.Interop.dll
```

The `x64`/`x86` folders are easy to miss when copying by hand: without them
`System.Data.SQLite` loads happily and then fails at the first query.

## Why not the MSI

`src/MetaInstaller/` still builds one, and it is fine on a machine where you have admin.
It is no longer the primary path because its upgrade sequence requires elevation, which
is exactly what a managed corporate machine withholds. Copying has none of that
dependency and is trivially scriptable.
