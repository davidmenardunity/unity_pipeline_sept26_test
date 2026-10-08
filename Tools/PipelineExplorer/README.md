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
`ScenePreview_WebGL` profile there as a release build, then restart the app).

## Layout

- **Top configuration**: org, project, token, project service, branch, workbench, environment.
  Every example works on these.
- **Example tabs**: one way to use Pipeline each. **+ New example** adds a placeholder tab
  (saved in this browser) describing how to build one.
  - **Explorer** (`explorer.js`): browse a workbench. The asset panel on the right (drag its edge
    to resize; double-click it for half the window) has **Details** (facts, `.meta`, source, cloud
    preview image, import results), **Viewer** (the player, with **Preview on select**) and
    **Calls** (the calls made for that asset). Picking an asset updates the whole panel; reads for
    the previous one stop, and a preview being built finishes in the background.
  - **Edit a project** (`edit.js`, `unityyaml.js`): open a `.prefab`, `.unity`, `.asset` or `.mat`,
    pick an object, and edit its values. Saving writes the file back as one new workbench revision
    (`POST /api/…/save`, a transaction with your message) and follows it until it validates. Only
    the characters of changed values are replaced; values Unity wraps over several lines are
    shown but not editable.
  - **Compare branches** (`compare.js`): pick an asset, then a branch to compare with. That branch
    is read through its own workbench (create one from the page if it has none). Two viewers show
    the asset side by side, with its facts and a text diff.
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
| **Workbench** | `GET …/workbenches`, filtered to the branch. **New workbench**: starts the service if needed, then `POST …/workbenches {type:git, branch, repo}`. **Delete**: `DELETE …/workbenches/{wb}`. |
| Readiness pill | `GET …/workbenches/{wb}/head` + `GET …/workbenches/{wb}` (validation). Reads are pinned to the settled revision. |
| **Environment** / **Add** | `GET`/`POST …/workbenches/{wb}/environments`. Previews and imports need one. |
| File tree | `GET …/revisions/{rev}/file-tree/{path}` (path as one `%2F` segment). |
| Selecting a file | `POST …/revisions/{rev}/asset-guid`, `GET …/assets/{guid}` |
| **.meta file** | `GET …/revisions/{rev}/assets/{guid}/meta` |
| **Source** | `GET …/revisions/{rev}/files/{path}` |
| **Preview** | `POST …/environments/{env}/revisions/{rev}/previews` → job → `GET …/previews/{guid}?allowAsync=false` |
| **Import results** | `POST …/environments/{env}/revisions/{rev}/imports {addresses:[G:{guid}]}` (or `T:{guid}+{importer type}`), then **Fetch**: `GET …/imports/{address}/{artifact}` |
| Exchange (in **Activity** and the **Calls** tab) | Every call the server made, grouped by operation. Select one for the full exchange (token redacted) or **Copy as curl**. |

Known staging limits (see the pipeline playground's README): the first import
requests after a cold start can answer `409 revision_not_validated` for
minutes (press **Retry**); `T:` addresses only resolve for importers the
project has (`import_not_found` otherwise).
