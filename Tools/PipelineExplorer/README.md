# Pipeline Explorer

A small local web app for trying the Unity Pipeline APIs on staging: browse a
project, switch branches, start workbenches, and request a file's artifacts
(`.meta`, source, preview, and import content files).

It's an ASP.NET Core server (`Program.cs`) that holds the bearer token and
calls the pipeline through the typed client in
`../Pipeline.Client` (a copy of the asset browser's client from the pipeline
playground repo, with import, project-list and `.env` support added), plus one page
(`wwwroot/`). The server listens on `127.0.0.1:5280` only, and `/api` calls
must carry an `X-Pipeline-Explorer: 1` header, so other websites can't use
your token through it.

## Run

The app reads `Tools/.env`, the same file `Tools/pipeline.http` uses (copy
`Tools/.env.example`; `.env` is git-ignored). If that's missing it falls back to
a `pipeline-main/pipeline-onboard.config` in a folder above, and `PIPELINE_ONBOARD_CONFIG`
overrides both. Set up the project service, workbench and environment with
`pipeline.http` (sections 1–4) or in the app. You need the VPN and the .NET 10 SDK.

```bash
dotnet run --project Tools/PipelineExplorer
```

Then open <http://127.0.0.1:5280>. After you put a fresh token in `.env`,
press **Reload config**. A pasted token and the last org/project you picked are kept for your Windows
account (`%LOCALAPPDATA%\PipelineExplorer`), so other clients, like the
[Blender add-on](../BlenderPipeline), work as soon as the app starts.

Stop the app before rebuilding: while it runs it locks
`bin/Debug/net10.0/Pipeline.Client.dll`, and the build fails with MSB3027.

The viewers embed the Scene Preview WebGL player from `Builds/ScenePreview_WebGL` (build the
`ScenePreview_WebGL` profile there as a release build, then restart the app). The level editor embeds
`Builds/LevelEditor_WebGL` (the `LevelEditor_WebGL` profile; **Tools > Level Editor > Create Scene and
Build Profile** recreates its scene and profile).

## Layout

- **Top configuration**: org, project, token, project service, branch, workbench, environment.
  Every example works on these.
- **Example tabs**: one way to use Pipeline each. **+ New example** adds a placeholder tab
  (saved in this browser) describing how to build one.
  - **Explorer** (`explorer.js`): browse a workbench. The asset panel on the right (drag its edge
    to resize; double-click it for half the window) has **Details** (facts, `.meta`, source, cloud
    preview image, import results), **Viewer** (the player, with **Preview on select**),
    **Comments** and **Calls** (the calls made for that asset). Picking an asset updates the whole
    panel; reads for the previous one stop, and a preview being built finishes in the background.
    The player stands the asset on a small stage (sky, sun, a plaza, trees and street props in a ring
    that makes room for big assets; `Assets/PreviewStage`).
  - **Scenes** (`sceneplayer.html`, Unity side in `Assets/SceneViewer`): selecting a `.unity` file in the
    Explorer has Pipeline build an archive of the whole scene (`PreviewSceneImporter`: the objects it uses as
    one content file, the scene as another, with the scene's own shader features) and plays it in its own
    player (the `SceneViewer_WebGL` build profile, built to `Builds/SceneViewer_WebGL`). Click the view to
    capture the mouse, Esc to release it; mouse looks, WASD moves, Shift runs, Space jumps, F switches
    walking and flying. Scripts the player wasn't built with load as missing components. Set up with
    **Tools > Scene Viewer > Create Scene and Build Profile**.
  - **Comments** (`comments.js`, Unity side in `Assets/PreviewAnnotations`): Unity Cloud
    Collaboration annotations on the asset. In the **Viewer**, **Comment** pins one on the model (or
    the ground) and **Draw** draws strokes that stay in place in the scene; either opens a box for the
    text. The player's `PreviewAnnotations` saves it with the Collaboration SDK (a `spatial-3d` or
    `sketch` attachment, plus the camera), and shows every comment's pin and drawing; numbered bubbles
    over the pins open the thread (replies, **Resolve**). The **Comments** tab lists the asset's threads
    (or every asset's), with replies, notes without a pin, **Resolve**/**Reopen**, **Delete** and
    **Show** (back to the viewpoint it was made from). Typing `@` lists the org's members; mentions
    are saved as `:user[Name]{#id}`.
  - **Edit a project** (`edit.js`, `unityyaml.js`): open a `.prefab`, `.unity`, `.asset` or `.mat`,
    pick an object, and edit its values. Saving writes the file back as one new workbench revision
    (`POST /api/…/save`, a transaction with your message) and follows it until it validates. Only
    the characters of changed values are replaced; values Unity wraps over several lines are
    shown but not editable.
  - **Compare branches** (`compare.js`): pick an asset, then a branch to compare with. That branch
    is read through its own workbench (create one from the page if it has none). Two viewers show
    the asset side by side, with its facts and a text diff.
  - **Level editor** (`leveleditor.js`, `levelscene.js`, Unity side in `Assets/LevelEditor`): open a
    `.unity` scene and it's rebuilt in its own WebGL runtime (the `LevelEditor_WebGL` build profile,
    built to `Builds/LevelEditor_WebGL`): each prefab instance and mesh object is placed where the scene
    says (through its parents), from the asset's Pipeline-built content archive; grey boxes show until
    an archive is ready. Drag prefabs from the tree into the view to add them. Left-click selects;
    drag the widget's arrows (or the object) to move; R rotates 15° (Shift+R back), F frames, Delete
    removes an added object; right-drag orbits, middle-drag pans, the wheel zooms. The panel shows the
    objects and the selected one's world position, rotation and scale (editable). Saving writes only
    the changed values into the scene (existing prefab overrides updated, missing ones added, new
    prefab instances appended with their `SceneRoots` entry) as one new workbench revision.
    Limits: objects inside a prefab instance are placed at the instance's root; removing saved
    objects, rotating with the widget and prefab variants aren't supported yet.
- **Status bar**: what is running (step, progress, elapsed) or the last result; **Activity** opens
  every operation with the calls it made (the page sends `X-Op`, the server tags each call).

## What maps to what

| In the page | API |
| --- | --- |
| **Org ID** / **Project** | `GET staging.services.unity.com/api/unity/legacy/v1/organizations/{org}` (name) and `…/{org}/projects?limit&offset` (internal host; archived projects hidden). The pick is remembered in this browser; `.env` isn't changed. **Enter project ID…** takes any UUID. |
| Status bar / **Activity** | Every action is an operation with steps: project service start-up, workbench creation, validation (head, validation status), uploads, saves, previews. Activity lists them with the calls each made. |
| Drop files on a folder | Upload as blobs, then `POST …/batch` into existing folders (a transaction when the folder is new): one new revision, which the panel follows until it validates. Then the tree shows it and selects the file. `.meta` files and folders can't be dropped. |
| Project service pill / **Start service** | `GET …/projectservice/status`, `POST …/projectservice/start` (internal host). If the service is down when the workbenches are listed, the page follows its start and lists them again. |
| **Branch** / ↻ | Branches from `git ls-remote` on `GIT_REPO_URL`, plus the branches of your workbenches. ↻ (or opening the list, when it's over 15 s old) refreshes branches and workbenches. **Other…** takes any name. |
| Workbench's git branch | Pipeline only says which git branch a workbench came from when it's created (its own branch, `branchName`, is always `main`). The server remembers it (`%LOCALAPPDATA%\PipelineExplorer\workbench-branches.json`); others are matched by commit, else listed under "Branch unknown". |
| Progress strip | Creating a workbench, a workbench getting ready, a publish or an update: shown under the example tabs on every tab, until it's done or dismissed. |
| **Update from git…** | Shown when the workbench is behind its branch. `PATCH …/workbenches/{wb} {type: "sync"}`, then checks the workbench's commit against the branch; if it didn't move, offers a fresh workbench. |
| **Publish to git…** | `POST …/workbenches/{wb}/publishes`, pushing with Pipeline's git token. Needs a workbench with a VCS connection that can push: one made from a public URL fails with "could not read Username". `publishedRevision: ""` means nothing to publish. |
| **Workbench** | `GET …/workbenches`, filtered to the branch. **New workbench**: starts the service if needed, then `POST …/workbenches {type:git, branch, repo, name, vcsConnectionId}` (name and VCS connection optional; only a workbench made with a VCS connection can publish). The dialog lists the project's connections from Build Automation v3 (`GET build-automation[.staging].services.api.unity.com/v3/orgs/{org}/projects/{project}/connections`) and prefills the one whose URL matches the repo. When there's none, it offers to create one first (`POST staging.services.unity.com/api/build-automation/v2/orgs/{org}/projects/{project}/connections {type:git, url, name, user, pass}`, Build Automation's internal API) with your GitHub user name and a personal access token that can push; the token is passed on, never stored or logged. **Delete**: `DELETE …/workbenches/{wb}`. |
| Readiness pill | `GET …/workbenches/{wb}/head` + `GET …/workbenches/{wb}` (validation). Reads are pinned to the settled revision. |
| **Environment** / **Add** | `GET`/`POST …/workbenches/{wb}/environments`. Previews and imports need one. |
| File tree | `GET …/revisions/{rev}/file-tree/{path}` (path as one `%2F` segment). |
| Selecting a file | `POST …/revisions/{rev}/asset-guid`, `GET …/assets/{guid}` |
| **.meta file** | `GET …/revisions/{rev}/assets/{guid}/meta` |
| **Source** | `GET …/revisions/{rev}/files/{path}` |
| **Preview** | `POST …/environments/{env}/revisions/{rev}/previews` → job → `GET …/previews/{guid}?allowAsync=false` |
| **Import results** | `POST …/environments/{env}/revisions/{rev}/imports {addresses:[G:{guid}]}` (or `T:{guid}+{importer type}`), then **Fetch**: `GET …/imports/{address}/{artifact}` |
| **Comments** | `/api/collab/…` passes through to `/collaboration/v1/…` (same host as the pipeline): `POST projects/{p}/annotations-search {target: unity/project/{p}/assets/{guid}}` (the target the Collaboration dashboard shows), `POST …/annotations/export` (every asset), `GET …/annotations/{id}/replies`, `POST …/annotations` (comments and replies), `PATCH …/resolve` / `…/unresolve`, `DELETE …/annotations/{id}`. The player's Collaboration SDK calls the same route. Collaboration refuses the pipeline's user JWT (issuer `unity-ads`) and takes a Genesis access token: **Use a token for comments…** takes one (kept like the first, in `collab-token.dat`). `@` lists come from `GET …/api/access/legacy/v1/organizations/{org}/members`. |
| Exchange (in **Activity** and the **Calls** tab) | Every call the server made, grouped by operation. Select one for the full exchange (token redacted) or **Copy as curl**. |

Known staging limits (see the pipeline playground's README): the first import
requests after a cold start can answer `409 revision_not_validated` for
minutes (press **Retry**); `T:` addresses only resolve for importers the
project has (`import_not_found` otherwise).
