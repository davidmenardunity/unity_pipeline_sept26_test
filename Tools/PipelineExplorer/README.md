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
press **Reload config**.

Stop the app before rebuilding: while it runs it locks
`bin/Debug/net10.0/Pipeline.Client.dll`, and the build fails with MSB3027.

## What maps to what

| In the page | API |
| --- | --- |
| **Org ID** / **Project** | `GET staging.services.unity.com/api/unity/legacy/v1/organizations/{org}` (name) and `…/{org}/projects?limit&offset` (internal host; archived projects hidden). The pick is remembered in this browser; `.env` isn't changed. **Enter project ID…** takes any UUID. |
| Progress panel | Narrates long waits: project service start-up steps, workbench creation, validation (readiness, head, validation status), with a timer and a log of state changes. |
| Drop files on a folder | Upload as blobs, then `POST …/batch` into existing folders (a transaction when the folder is new): one new revision, which the panel follows until it validates. Then the tree shows it and selects the file. `.meta` files and folders can't be dropped. |
| Project service pill / **Start service** | `GET`/`POST …/branches/{branch}/projectservice/status|start` (internal host). One service per branch. |
| **Branch** | Branches from `git ls-remote` on `GIT_REPO_URL`, plus the branches of your workbenches. **Other…** takes any name. |
| **Workbench** | `GET …/workbenches`, filtered to the branch. **Start workbench on branch**: starts the service if needed, then `POST …/workbenches {type:git, branch, repo}`. **Delete**: `DELETE …/workbenches/{wb}`. |
| Readiness text | `GET …/readiness` + `GET …/workbenches/{wb}` (validation). Reads are pinned to the settled revision. |
| **Environment** / **Add** | `GET`/`POST …/workbenches/{wb}/environments`. Previews and imports need one. |
| File tree | `GET …/revisions/{rev}/file-tree/{path}` (path as one `%2F` segment). |
| Selecting a file | `POST …/revisions/{rev}/asset-guid`, `GET …/assets/{guid}` |
| **.meta file** | `GET …/revisions/{rev}/assets/{guid}/meta` |
| **Source** | `GET …/revisions/{rev}/files/{path}` |
| **Preview** | `POST …/environments/{env}/revisions/{rev}/previews` → job → `GET …/previews/{guid}?allowAsync=false` |
| **Content files** | `POST …/environments/{env}/revisions/{rev}/imports {addresses:[G:{guid}]}` (or `T:{guid}+{importer type}`), then **Fetch**: `GET …/imports/{address}/{artifact}` |
| API calls panel | Every call the server made. Select one for the full exchange (token redacted) or **Copy as curl**. |

Known staging limits (see the pipeline playground's README): the first import
requests after a cold start can answer `409 revision_not_validated` for
minutes (press **Retry**); `T:` addresses only resolve for importers the
project has (`import_not_found` otherwise).
