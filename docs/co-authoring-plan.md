# Co-authoring tab: plan

Status: built (2026-10-30). See Tools/CoAuthoring/README.md; this was the plan.

## What we're building

A **Co-authoring** tab in the Pipeline Explorer. It is backed by a new **local** endpoint that isn't part of
Unity Pipeline yet; the goal is to move it there later. On one workbench:

- **Commands.** Humans and agents send **Unity CLI commands**, the ones `com.unity.pipeline` defines. That
  includes custom commands declared the pipeline-package way.
- **Execution.** Each command is evaluated with the **Unity data model** (`Tools/Unity.DataModel`) against a
  shared draft of the workbench's files, then written to disk.
- **Concurrency.** Many callers can send at once. Commands go into **one queue per workbench** and are
  resolved one at a time. A failed command answers with a failure, and the caller may resend it.
- **Presence.** Everyone in the scene sees each other: a small camera with its view frustum and a name tag,
  where each person (or agent) is looking.
- **Save.** **Save** collapses everything since the last save into **one commit** on the workbench, on the
  branch (and version control) the workbench is tied to.

## What already exists

`C:\work_repos\runtime-authoring` is our earlier prototype of the same idea, aimed at a runtime player.
Several parts are already built and stress-tested there:

| Piece | Where | Reuse? |
| --- | --- | --- |
| One endpoint that takes CLI commands (line, argv or `{command, params}`), `$0.id` result references, `job batch` | `service/src/Authoring.Api/Commands/*` (`CommandLine`, `CommandCatalog`, `CommandRunner`) | Port: the parser and binding rules match `unity command`. |
| Per-workbench single writer queue, `seq`, failed write reverted, `baseSeq` conflicts (409), idempotency keys, undo per actor | `Authoring.Core/Engine`, `docs/concurrency.md` | Port the design, and the code where it fits. |
| Op log (JSONL, fsync) so drafts survive a crash; rebase when the workbench head moves | `Authoring.Core` | Port. |
| Presence (actor, state, colour, 10 s timeout), event stream `GET events?since=` | `Authoring.Api/PresenceService.cs`, `CommandEndpoints.cs` | Port. |
| Lossless YAML | `Authoring.UnityYaml` | **Converge** with `Unity.DataModel`; see decision 1. |

The pipeline package (`Library/PackageCache/com.unity.pipeline@…`, `0.7.0-exp.1` in this project):
- **Declaration.** Commands are static methods marked `[CliCommand(name, description)]`, with
  `[CliArg(name, description)]` on their parameters or on a DTO's fields (`IStructuredCommandInput`).
- **Discovery.** `CommandRegistry` scans assemblies for the attribute (`TypeCache` in the Editor) and
  serves the list, with JSON schemas, at `GET /api/commands`.
- **Editor-bound.** Every scene, prefab, GameObject and component command (`create_gameobject`,
  `set_transform`, `set_parent`, `add_component`, `set_component_properties`, `instantiate_prefab`,
  `set_serialized_field`, `batch`, …) lives in the Editor-only `Unity.Pipeline.Editor` assembly. They call
  `UnityEditor` APIs and change live Editor state, not files.

So the service can't run the package's command *code*. It takes the package's command **contract** (names,
parameters, results) and implements each command with the data model. That gives the same language, a
different engine.

## Decisions to make first

1. **One data model, not two.** `Unity.DataModel` (written today, used by the Level editor and the Inspector tab)
   and `Authoring.UnityYaml` overlap. Recommendation:
   - Make `Unity.DataModel` the shared library.
   - Bring over what `Authoring.UnityYaml` has that it lacks: `.meta` files, creating and removing
     documents, re-parenting, and a whole-document emitter for new objects.
   - Keep `Unity.DataModel`'s rule that only touched characters and lines change.
   - Its golden and whole-project tests guard against regressions.
2. **Where the endpoint lives.** Recommendation: inside the Pipeline Explorer server for the demo, behind
   `/coauthor/v1/workbenches/{wb}/…`. Keep it as its own project (`Tools/CoAuthoring`) with no Explorer
   dependencies, so it can move into Pipeline as a service later.
3. **Custom commands.** The pipeline-package way is `[CliCommand]` on a static method, and those methods
   normally use `UnityEditor`. The service can only run code that needs no engine. Recommendation:
   - Custom commands meant for co-authoring go in an asmdef with `noEngineReferences: true` that references
     `Unity.DataModel.dll`.
   - They keep the same `[CliCommand]`/`[CliArg]` attributes. The service matches the attributes **by
     name**, so it doesn't load the package's Editor assemblies.
   - Each command takes a data-model context as its first parameter.
   - The service loads that DLL from the workbench's built scripts, or from `Library/ScriptAssemblies` in
     the demo. The same method still shows up in the Editor's CLI.
   - Commands it can't run (Editor-only ones) are refused with `unsupported` and the reason.
   - The alternative, forwarding to a running Editor's `/api/exec`, needs an Editor per workbench, so it's a
     later option.
4. **Identity.** Recommendation: each caller sends `X-Actor` (display name) and `X-Actor-Kind`
   (`human` or `agent`). This is good enough for a local demo. Later, use the Unity user from the token.

## Design

### Endpoint

Same shape as the runtime-authoring API, so its clients (`authoring.mjs`, the MCP server) work with
little change:

| Route | What |
| --- | --- |
| `POST /coauthor/v1/workbenches/{wb}/commands` | `{ commands: [line \| argv \| {command, params}], transaction?, baseSeq? }` with `X-Actor`. Returns `{ seq, results: [{ status: ok\|error\|skipped\|reverted, command, result \| code+message }] }`. |
| `GET  /coauthor/v1/workbenches/{wb}/commands` | The catalog: every command with parameters and JSON schema, the shape of the package's `GET /api/commands` (the built-ins, plus custom commands found). |
| `GET  /coauthor/v1/workbenches/{wb}/events?since=seq` | SSE: each applied command with what it changed (objects, fields), presence, saves, rebases. |
| `PUT  /coauthor/v1/workbenches/{wb}/presence` | `{ scene, camera: {p, q, fov, aspect, near, far}, selection, color? }` about every 250 ms; dropped after 10 s of silence. |
| `GET  /coauthor/v1/workbenches/{wb}/draft` | Base revision, `seq`, dirty files, who changed what since the last save. |
| `POST /coauthor/v1/workbenches/{wb}/save` | `{ message }`: one commit with every dirty file; returns the revision. |
| `POST /coauthor/v1/workbenches/{wb}/discard` | Throw the draft away (back to the base revision). |

### The queue

- **One writer per workbench.** A `Channel<Work>` and one consumer task. Every request (one command, or a
  batch as a transaction) is one work item.
- **Ordering.** Items are resolved strictly in arrival order. Each gets the next `seq`.
- **Failure.** A failed command is reverted: the data model's `Revert` for that item's files, or a snapshot
  of the touched files taken before it ran. The caller gets `status: error` with a code (`bad_request`,
  `not_found`, `conflict`, `gone`, `invalid_operation`, `unsupported`). Resending is the caller's choice.
  Retries carry the same `Idempotency-Key` so a timeout doesn't apply a command twice.
- **Conflicts.** With no condition, the last writer wins field by field. With `baseSeq`, a command fails
  with `conflict` (and the current values) if a field it touches changed after that `seq`. That's the
  Google Docs feel: edits to different fields of the same object always merge.
- **Draft state.** The draft is the workbench's files at the base revision, plus applied changes, held as
  `UnityFile`s in memory and written to a draft folder on disk after each item. That is the requested
  "saved to disk", and with the op log it survives a restart.
- **Moving head.** If someone else commits to the workbench meanwhile, the draft rebases: it reloads the
  head and replays the op log. Commands that no longer apply are dropped and announced.

### Commands, first set

Implemented with `Unity.DataModel`, with names and parameters as in `com.unity.pipeline` (snapshot its
`GET /api/commands` from a running Editor into `catalog.json`; a test checks that ours match):

| Command | Data model work needed |
| --- | --- |
| `create_gameobject` (name, primitive, parent), `create_gameobjects` | New GameObject + Transform documents, MeshFilter/MeshRenderer for primitives (built-in mesh refs), parent `m_Children`, `SceneRoots`. |
| `instantiate_prefab` | Have it: `AddPrefabInstance`; add parent support. |
| `set_transform` | Have it: `Transform.SetLocalPose` / `PrefabInstance.SetRootPose` (Euler in, quaternion out). |
| `set_parent`, `set_active`, `set_tag`, `set_layer`, `rename_gameobject` | Field sets; re-parenting edits two `m_Children` lists and `m_Father`. |
| `delete_gameobject` | Remove documents (the object, its components, its children) and references to them. |
| `add_component`, `remove_component` | Built-in types from a small class-ID/default-fields table. MonoBehaviours from the script's GUID (asset index) and its serialized fields' defaults. |
| `get_component_properties`, `set_component_properties`, `get_serialized_fields`, `set_serialized_field` | Field reads and sets by property path, through the model. |
| `get_scene_hierarchy`, `find_gameobjects` | `Hierarchy`. |
| `batch` | The runner. |

**Handles.** `ObjectRef` is one of `globalId`, `path` + `fileId`, `guid` + `fileId`, or `hierarchyPath`,
resolved against the draft. `instanceId` has no meaning outside the Editor, so it's refused.

### Presence in the scene

- **Reporting.** The Level editor's WebGL runtime already has a camera. It reports its pose about 4× a
  second through the page, using a new `LevelEditor` → page message (`camera`).
- **Drawing.** The runtime draws every other participant as a small camera gizmo with its frustum (from fov,
  aspect, near/far), in their colour, with a billboarded name tag. It does this in `Assets/LevelEditor`:
  line meshes plus a world-space label.
- **Agents** have no camera. They show up in the tab's list of participants and as a pulse on the objects
  their last command touched. An agent can optionally send a "look at" pose.
- **Rebuild player.** It's the `LevelEditor_WebGL` profile, built with `run_script` and
  `BuildPlayerWithProfileOptions` (not `--profileName`; see the CLI gotchas).

### The tab

- **Left:** the scene picker, plus the participants (colour, name, human/agent, what they're doing, idle).
- **Centre:** the Level editor runtime showing the draft. Edits from anyone appear live, from the event
  stream.
- **Right:** a command box (type a CLI line, with history and autocomplete from the catalog), and the
  activity feed with each command's actor, `seq`, status and **Resend** on failures.
- **Bar:** draft state ("12 changes by 3 people since revision 41"), **Save** (message, one commit),
  **Discard**.
- **Level editor actions go through the commands too:** moving an object sends `set_transform`; dropping
  a prefab sends `instantiate_prefab`. Humans and agents use one path.

### Save

**Save** runs on the queue, so nothing interleaves:
1. It takes every dirty file's text from the data model.
2. It commits them with `SaveFilesAsync` (one transaction, the message, the workbench's own branch) at
   the expected head.
3. It waits until the revision validates.
4. It moves the draft's base to the new revision and clears the op log.

The workbench is already tied to its git branch. **Publish** (existing) pushes it to the version control
the workbench came from, so the version control only ever sees one commit per save.

## Steps

1. **Data model: structure.**
   - Add creating and removing documents, re-parenting, components with defaults, and `.meta` reads.
     Merge in from `Authoring.UnityYaml`.
   - Tests: goldens produced by the Unity Editor. Run the same commands in the Editor through
     `unity command`, save, and compare files. Also the whole-project round trip.
2. **Command layer** (`Tools/CoAuthoring`).
   - Port the parser and catalog from runtime-authoring.
   - `catalog.json` from the package's `/api/commands`.
   - Handlers for the first set.
   - Tests: each command against fixtures, plus parity with the catalog.
3. **Queue, draft, op log, events, presence.**
   - Port from runtime-authoring.
   - Tests: the stress test (many actors, random commands; every acknowledged write visible, replay equals
     state), failure → revert, idempotent retry, `baseSeq` conflict.
4. **Custom commands.**
   - Discover `[CliCommand]` by attribute name in `noEngineReferences` assemblies.
   - Write one sample custom command in this project, for example `level.scatter_prefab` that places N
     copies of a prefab on a grid. It must run both in the Editor CLI and on the endpoint.
5. **Explorer tab and endpoint wiring:** the participants list, command box, feed, save bar, and the
   Level editor sending commands.
6. **Runtime presence:** camera reports, frustum and name gizmos, live application of others' edits; then
   rebuild `LevelEditor_WebGL`.
7. **Demo script:**
   - two browsers (two humans) and one agent (Claude Code through the MCP server, which points at the
     local endpoint) on one scene;
   - a forced conflict and a resend;
   - **Save** → one commit → **Publish** → one commit on the branch.

## Risks

- **Faithful new objects.** New GameObjects and components need the right `serializedVersion` and
  default fields for 6000.7. Mitigation: defaults taken from Editor-written goldens, not hand-written, and
  a check that the Editor re-imports the result without changes.
- **MonoBehaviour defaults.** Without the Editor these need the script's serialized layout. For the
  demo, use the fields the Editor writes for each script (captured once). A real version would get them
  from Pipeline's script analysis.
- **Pipeline status.** The endpoint is local. Moving it to Pipeline means the queue and draft live
  server-side per workbench, the same contract, which is why the routes are kept workbench-scoped from day
  one.
