# Unity Pipeline for Blender

A Blender add-on (extension) for working on a Unity project's models through Pipeline: browse the
project, open an asset in Blender, change it, and push it back to the workbench it came from. The
push overwrites the file as a new revision of that workbench; git isn't changed. The asset's `.meta`
(and GUID) stay, so references to it in Unity keep working.

It talks to [Pipeline Explorer](../PipelineExplorer), which runs on your machine, holds the bearer
token and calls Pipeline (and follows its API changes). Keep Pipeline Explorer running while you
use the add-on.

## Install

Needs Blender 4.2 or later (tested with 5.2).

1. Build the package (or zip the `unity_pipeline` folder yourself):

   ```bash
   blender --command extension build --source-dir Tools/BlenderPipeline/unity_pipeline --output-dir Tools/BlenderPipeline/dist
   ```

   Or, to work on the add-on, link the folder so Blender loads it from the repo (restart Blender after
   a change, and don't use Blender's Uninstall on it: that deletes the folder it points at):

   ```powershell
   New-Item -ItemType Junction -Path "$env:APPDATA\Blender Foundation\Blender\5.2\extensions\user_default\unity_pipeline" -Target "$PWD\Tools\BlenderPipeline\unity_pipeline"
   ```

2. In Blender: **Edit > Preferences > Get Extensions**, the **⌄** menu at the top right, **Install from Disk…**,
   and pick `Tools/BlenderPipeline/dist/unity_pipeline-0.2.0.zip`.
3. Start Pipeline Explorer (`dotnet run --project Tools/PipelineExplorer`), open <http://127.0.0.1:5280>
   once to paste a token and pick the org and project. It remembers both.

The add-on's preferences set Pipeline Explorer's address (default `http://127.0.0.1:5280`), where
downloaded assets are kept (default: a folder in the system temp folder), and how many rows the tree
shows.

## Use

Open the sidebar in a 3D Viewport (**N**) and pick the **Pipeline** tab.

1. **Connect.** Pick the **Branch** and **Workbench** (the ones made from the branch's latest commit
   come first; older ones say "behind"). The add-on remembers your pick per branch. To create a
   workbench, use Pipeline Explorer.
2. **Project**: open folders and pick a file. The cube button hides files Blender can't open; the
   search field filters by name.
3. **Selected asset > Open in Blender** (or click the selected file again) downloads it at the
   workbench's revision and opens it in a new scene named after it.
4. Change it, then **This scene > Push to workbench {id} ({branch})**. Add a message; the add-on
   exports the scene in the asset's format, uploads it to that workbench as a new revision, and waits
   until the revision validates (about a minute). If the asset changed on the workbench after you opened it, the push stops and says so:
   **Reopen** it, or push again with **Overwrite newer changes**.

**Publish to git…** (under the workbench) pushes the workbench's changes to its git branch, with
Pipeline's own git token. It needs a workbench with a VCS connection that can push; see Pipeline
Explorer's README.

The status box at the top always says what is running (and for how long), or the last result. The
sidebar also shows the add-on's version, to check which code Blender has loaded.
Every call also shows in Pipeline Explorer's **Activity**.

## Formats

| Format | Opened with | Pushed with |
| --- | --- | --- |
| `.fbx` | `import_scene.fbx` | `export_scene.fbx` (same axes, no leaf bones) |
| `.obj` | `wm.obj_import` | `wm.obj_export` (no `.mtl`, which would become a new asset) |
| `.glb` | `import_scene.gltf` | `export_scene.gltf` (GLB) |
| `.stl`, `.ply` | `wm.stl_import`, `wm.ply_import` | `wm.stl_export`, `wm.ply_export` |
| `.usd`, `.usda`, `.usdc`, `.usdz` | `wm.usd_import` | `wm.usd_export` |
| `.blend` | its first scene | the scene and what it uses |

Everything in the asset's scene is exported. A `.gltf` isn't offered: its `.bin` and textures are
separate files. An FBX opened and pushed back unchanged comes back with the same objects, vertex
counts, positions, sizes and materials.

## Files

| File | What it does |
| --- | --- |
| `unity_pipeline/client.py` | HTTP calls to Pipeline Explorer (no `bpy`; works from plain Python) |
| `unity_pipeline/state.py` | What the add-on knows, and background work: network calls run on threads, results come back on Blender's main thread |
| `unity_pipeline/formats.py` | Opening an asset in its own scene, and writing it back in its format |
| `unity_pipeline/ops.py` | Connect, pick branch and workbench, browse, open, reopen, push |
| `unity_pipeline/ui.py` | The sidebar panels, menus and preferences |
