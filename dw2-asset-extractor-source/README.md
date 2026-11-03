# Distant Worlds 2 Asset Extractor (dw2extract)

Extracts every asset from DW2's `.bundle` files and converts it to a standard
format: textures → `.dds`/`.png`, sounds → `.wav`, meshes → `.fbx`, shaders →
`.sdsl` source, everything else copied as-is. This tool only extracts/converts
to your local disk — it does not repackage bundles or insert anything back into
the game.

Two projects:
- **`DistantWorlds2.Core`** — class library holding the actual conversion logic
  (texture/sound/mesh conversion, bundle reading).
- **`DistantWorlds2.AssetExtractor`** (`dw2extract`) — the CLI/GUI-hybrid tool
  itself. Point it at your DW2 install and an output folder, and it walks every
  bundle you select, converting and recreating the full folder structure
  automatically.

## One-time setup

Prerequisites: Windows 10/11, a legitimately owned Distant Worlds 2 install
(Steam or GOG), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
and [FFmpeg](https://ffmpeg.org/download.html) (`ffmpeg.exe`/`ffprobe.exe`, for
sound and PNG conversion — step 3 below). See the root `README.md`'s
"Prerequisites" section for why each of these is needed.

1. **Point the build at your DW2 install.** The real Xenko/Stride engine DLLs
   this tool links against ship with the game itself — they can't be bundled
   with this repo (they're Slitherine/Matrix Games' property, not a
   redistributable SDK).

   Copy `DW2BT.local.props.example` to `DW2BT.local.props` (same folder, next to
   this README) and edit the path:
   ```xml
   <DW2GameDir>C:\Steam\steamapps\common\Distant Worlds 2</DW2GameDir>
   ```
   `DW2BT.local.props` is gitignored — it's machine-specific.

2. **Build:**
   ```
   dotnet build DistantWorlds2.AssetExtractor.sln
   ```
   This produces `DistantWorlds2.AssetExtractor\bin\Debug\net8.0-windows\dw2extract.exe`,
   with all the engine DLLs and native libraries (`x64\*.dll`) copied alongside
   automatically.

3. **Sound/PNG conversion needs FFmpeg.** Put `ffmpeg.exe`/`ffprobe.exe` on your
   `PATH`, or copy them into the same output folder as `dw2extract.exe`.

## Running it

`dw2extract.exe` needs no setup beyond the build steps above — run it and it
asks for your DW2 install folder, then which bundles to extract, then which
asset types to extract (both are checklists, all checked by default — use
Select All/Select None to speed up picking just a few), then where to save
extracted assets. It mounts whatever install folder you pick at runtime, so it
doesn't need any junction or copy-into-place step.

The install folder and output folder are remembered between runs (saved to
`%LocalAppData%\dw2extract\settings.json`). On a repeat run, instead of the
folder-browse dialog you'll get a Yes/No/Cancel prompt showing the
previously-used path — **Yes** reuses it, **No** opens the folder browser to
pick a different one, **Cancel** aborts the run. The very first run (or if the
remembered install folder no longer contains `DistantWorlds2.exe`, e.g. after
an uninstall/move) just goes straight to the folder browser as before.
Whichever folders you end up using — reused or freshly picked — get saved for
next time. This doesn't apply to the scripted command-line form (see below),
which never touches the settings file.

It then walks every selected bundle and recreates the game's own folder
structure under your chosen output folder, one subfolder per bundle
(`CoreContent\`, `Human\`, `Abandoned\`, ...), converting as it goes. The asset
type checklist controls which of these happen:
- Textures → `.dds` and/or `.png`, independently selectable — DDS only, PNG
  only (converts from DDS then deletes the DDS, unless conversion fails, in
  which case the DDS is kept as a fallback), or both side by side. The PNG
  conversion is via FFmpeg (needs `ffmpeg.exe` on `PATH` or next to the exe,
  same as sound conversion), not Xenko's own PNG encoder, which turned out to
  silently mis-decode at least some block-compressed textures and to crash
  outright (unmanaged, uncatchable) on floating-point/HDR textures like BRDF
  LUTs.
- Sounds → `.wav`
- Meshes → `.fbx` — exports geometry (positions/normals/UVs) and the full node
  hierarchy (skeleton bones, or hardpoint markers like
  `#weaponVerticalLauncherLarge9` for ships) with each mesh parented under its
  correct node. Does **not** export bone-weight/skinning data (fine for ship
  hardpoint structure; a skinned character exports its shape correctly but
  won't be posable).

  Materials and textures come along too — selecting FBX automatically pulls
  in PNG export as well (there's no point linking a texture that never gets
  extracted), so the exported model opens in Blender already textured: base
  color, emissive, and normal maps are wired directly into the material.
  Glossiness/Metalness/AmbientOcclusion maps are *not* wired into a texture
  slot — Stride typically packs those three into channels of one shared
  texture (e.g. R=Metalness/G=Glossiness/B=AO for a given material — verify
  per-material, this isn't universal), and classic FBX materials have no slot
  that means "the red channel of this texture." Forcing them into a
  wrong-meaning slot (e.g. "Specular") would be actively misleading. Instead
  they show up as plain custom properties on the material (visible in
  Blender's Material Properties → Custom Properties panel) naming the exact
  packed texture file — enough for a technical artist to wire up manually
  (one Separate Color node) or hand straight to Marmoset Toolbag/Substance for
  channel-packed editing, without this tool having invented a new unpacked
  texture format that the game itself doesn't understand. Only textures the
  bundle actually declares get linked — built-in Stride engine resources
  (e.g. PBR environment lighting LUTs) and cross-bundle texture references are
  silently skipped rather than pointing at a file that won't exist.
- Everything else / unsupported (shader source, XML/data files, ...) — copied
  as-is, unconverted, under its own "misc/unsupported" checklist entry so you
  can still see what's in a bundle even for types this tool doesn't convert.
  Shader source (`.sdsl` entries under `shaders/`) needs no conversion step —
  it's raw text in the bundle, not compiled bytecode.

Each bundle's subfolder contains exactly that bundle's *own* declared assets — no
cross-bundle deduplication, and no attempt to resolve which version "wins" at
runtime. DW2 routinely has later bundles re-declare (override) an asset path
that an earlier one also declares (e.g. both `CoreContent` and `Human` can
legitimately have their own version of the same path) — this tool deliberately
doesn't try to figure out which one the game actually uses at a given point;
it just gets everything out where you can see and compare both versions
yourself. Same path appearing in multiple bundle subfolders is expected, not
a bug.

For scripting/testing, it also accepts the two paths as command-line arguments
to skip the dialogs (and both checklists — this form extracts every bundle and
every asset type): `dw2extract.exe "C:\...\Distant Worlds 2" "D:\output"`. A
third argument limits the run to bundles whose name contains it
(case-insensitive), e.g. `dw2extract.exe "C:\...\Distant Worlds 2" "D:\output" Human`.
`-types <list>` limits the asset types (comma-separated: `dds`, `png`,
`textures` for both, `wav`, `fbx`, `misc`), e.g. `... Human -types textures`
for textures only. If the output folder isn't empty, also pass `-overwrite`,
`-skip-existing` or `-recreate`. Run `dw2extract.exe --help` for every option.

Heads up: a full-catalog extraction (all bundles, all asset types selected)
pulls around 30-40 GB from the game's bundles, and full conversion (especially
mesh export) will take a while and use significant disk space. Use the bundle
and asset-type checklists to extract just what you need instead.

## Not yet covered

- Nothing in this tool packs assets back into the game's format or rebuilds
  bundles — extraction/conversion only, by design.
- No tooling yet to distinguish DW2's own custom shaders from Stride's bundled
  engine shader library within a bulk extraction — `grep`/search the extracted
  `.sdsl` files' `namespace` line (DW2's own use `namespace DistantWorlds2`) or
  their companion `<name>.sdsl/path` file (Stride's point into a NuGet cache path,
  DW2's point into the original dev's project tree).
