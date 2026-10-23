# Building from source

PerfeBT compiles against the game's and other mods' assemblies. They can't be published, so the repository has no copies of them: you need a working install to build.

## Requirements

- **Windows** with the **.NET SDK** 6 or newer (built with 8.0) for `dotnet build`.
- **.NET Framework 4.7.2 Developer Pack** (reference assemblies for `net472`). Without it the build fails with `MSB3644: The reference assemblies for .NETFramework,Version=v4.7.2 were not found`.
- **BATTLETECH** (Steam or GOG) with:
  - **ModTek 4.x** in `Mods/ModTek/` (PerfeBT uses its `lib/0Harmony.dll` (HarmonyX) and `lib/MonoMod.Core.dll`);
  - **CustomComponents** in `Mods/Core/CustomComponents/`, as installed by RogueTech or BTA 3062.

The project references these files from the install (see `PerfeBT.csproj`):

| Assembly | Path under the game folder |
| --- | --- |
| `Assembly-CSharp`, `UnityEngine*`, `Newtonsoft.Json`, `Google.Protobuf.Unity` | `BattleTech_Data/Managed/` |
| `0Harmony`, `MonoMod.Core` | `Mods/ModTek/lib/` |
| `CustomComponents` | `Mods/Core/CustomComponents/` |

All other mods (CustomAmmoCategories, CustomUnits, LowVisibility, …) are looked up by name at runtime. They are not needed to build.

## Build

```
git clone https://github.com/cyboman32/perfebt
cd perfebt
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\BATTLETECH"
```

`GameDir` defaults to the author's install path in `PerfeBT.csproj`. Pass your own with `-p:GameDir=…`, or edit the default locally. The output is `bin/Release/PerfeBT.dll`.

## Install the build

With the game closed:

1. Copy `bin/Release/PerfeBT.dll` and `mod.json` into `BATTLETECH/Mods/PerfeBT/`.
2. If you already have a `mod.json` there with your own settings, keep it and only add the new keys from the repository's `mod.json`.

A missing key uses its default from `src/Settings.cs`.

## Release package

`scripts/release.ps1 -Tag vX.Y.Z -DryRun` builds with that version and packs `dist/PerfeBT_vX.Y.Z.zip`, ready to extract into `Mods/`. Pass `-GameDir` if the game is not at the default path. Without `-DryRun` the script also tags and publishes a GitHub release (see the README's "Releasing" note).

## Source layout and checks

- The source layout is described in [HOW-IT-WORKS.md](HOW-IT-WORKS.md).
- There is no test project in the repository. Fixes check themselves in game: a fix that finds a difference from the original code switches itself off and logs it to `PerfeBT.log`.
