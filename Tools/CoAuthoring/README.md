# CoAuthoring

Several people and agents edit the same Unity scene at the same time. Each change they make is a Unity CLI command,
from the `com.unity.pipeline` package. A local service queues every command and carries it out with
[Unity.DataModel](../Unity.DataModel), against a shared draft of the workbench's files. **Save** turns the draft into
one commit on the workbench.

This service runs locally, not in Unity Pipeline yet. Its routes are scoped to a workbench so it can move into
Pipeline unchanged. The Pipeline Explorer hosts it ([CoAuthoringEndpoints.cs](../PipelineExplorer/CoAuthoringEndpoints.cs))
and shows it in its **Co-authoring** tab.

## How it works

- **One queue per workbench** ([Session](Engine/Session.cs)). Every request waits its turn and is resolved alone,
  in arrival order.
  - A command that succeeds is written to the draft and gets the next sequence number.
  - A command that fails changes nothing. Its result says why, and the caller can send it again.
  - With `transaction: true`, a request's commands apply all together or not at all.
- **Draft on disk.** The draft lives under `Library/CoAuthoring/{workbench}/`:
  - `files/` holds the changed files.
  - `state.json` holds the base revision, sequence number and change log.
  - `oplog.jsonl` records every change.

  A restart picks the draft up again.
- **Conflicts.** By default the last writer wins, field by field, so edits to different fields of the same object
  always merge. If a request sends `baseSeq`, a command fails with `conflict` when a field it changes was changed by
  someone else after that sequence number. The error lists those fields.
- **Save.** The draft's changed files go into one commit, through the workbench's own transaction, on its branch,
  with a message that names everyone who contributed. If someone else committed to one of those files in the
  meantime, the save is refused rather than overwriting their work.
- **Events.** `GET …/events` streams server-sent events: each change (with what it created, deleted and updated),
  saves, discards, presence, and Claude's progress.
- **Presence.** Each participant reports their scene and camera a few times a second. Someone who stays silent for
  10 seconds leaves. The Co-authoring tab's player draws everyone else as a small camera with its view frustum and
  a name tag, in their colour (`Assets/LevelEditor/Runtime/LevelEditorPresence.cs`).

## Commands

The commands are the pipeline package's own. Their names, descriptions, parameters and schemas come from the
Editor's `GET /api/commands` and are stored in [catalog.json](catalog.json). Lines are read the way `unity command`
reads them:

```
create_gameobject --name Crate --primitive Cube
set_transform --target /Crate --position [0,0.5,3] --rotation [0,45,0]
instantiate_prefab --prefab "Assets/Toon Gas Station/Prefabs/Barrel_1F.prefab"
batch --operations [{"id":"a","command":"create_gameobject","params":{"name":"A"}},{"command":"set_parent","params":{"target":"$a.globalId","parent":"/Crate"}}]
```

The package's commands run inside the Unity Editor. Here, [Builtins.cs](Commands/Builtins.cs) carries each one out
on the data model instead, with the same name, the same parameters, and results shaped like the package's
(`globalId`, `hierarchyPath`, `type`; this service adds `fileId`):

| Group | Commands |
| --- | --- |
| Scenes | `open_scene`, `list_open_scenes`, `get_scene_hierarchy`, `find_gameobjects` |
| Objects | `create_gameobject`, `create_gameobjects`, `instantiate_prefab`, `delete_gameobject` |
| Changing objects | `set_transform`, `set_parent`, `set_active`, `set_tag`, `set_layer`, `rename_gameobject` |
| Components | `add_component` (built-ins, or a script by class name), `remove_component`, `get_component_properties`, `set_component_properties`, `get_serialized_fields`, `set_serialized_field` |
| Batches | `batch` |

**Handles** are the package's ObjectRef forms: a `globalId`, an asset path, a `guid` (with or without a `fileId`), or
a hierarchy path such as `/Root/Child`. This service also accepts `{"fileId": …}`. An `instanceId` only means
something in a running Editor, so it is refused.

**Prefab instances** can be placed, moved, renamed, re-parented, shown and hidden, and deleted. Adding components to
an instance, or setting its component properties, is refused with `unsupported`.

## Custom commands

Custom commands are declared the way the pipeline package declares commands: a static method marked
`[CliCommand(name, description)]`, with `[CliArg(name, description)]` on its parameters. The service finds them by
the attribute's name, so either the package's attribute or Unity.DataModel's `Unity.DataModel.Commands.CliCommand`
works.

A co-authoring command runs on the data model rather than in the Editor, so it also takes an `ICommandContext`
parameter. That parameter gives the command the scene, other files, and asset GUIDs and prefabs.

In the Unity project, [Assets/CoAuthoringCommands](../../Assets/CoAuthoringCommands) holds the sample:

- **Assembly:** an assembly definition with no engine references, using `Plugins/Unity.DataModel.dll`.
- **Sample command:** `scatter_prefab` places copies of a prefab in a ring.

The Editor compiles that assembly into `Library/ScriptAssemblies`. The Explorer loads the commands from there when
it starts, and lists them with `custom: true`.

When Unity.DataModel changes, copy `bin/Release/netstandard2.1/Unity.DataModel.dll` into
`Assets/CoAuthoringCommands/Plugins/` again.

## Agents

[mcp/coauthor-mcp.mjs](mcp/coauthor-mcp.mjs) is an MCP server that lets Claude Code, or any MCP client, join a
session as one more participant:

```
claude mcp add coauthor -e COAUTHOR_WORKBENCH=<workbench id> -e COAUTHOR_SCENE=Assets/Scenes/SampleScene.unity -- node Tools/CoAuthoring/mcp/coauthor-mcp.mjs
```

The Co-authoring tab's **Ask Claude** panel does the same from the page ([ClaudeAssistant.cs](../PipelineExplorer/ClaudeAssistant.cs)):

- It runs Claude Code headless (`claude -p`, stream-json) with this MCP server as its only tools; its file, shell
  and web tools are off.
- It uses the Claude Code sign-in the machine already has, so no API key is needed. It finds Claude Code on `PATH`,
  in the Claude desktop app's bundled copy, or at `CLAUDE_CODE_PATH`.
- Claude edits as the participant "Claude", and a follow-up in the same scene continues the conversation.

The **Demo** button starts five pretend people ([DemoCrowd.cs](Engine/DemoCrowd.cs)). Each works in their own
area around the scene, about 16 m from the centre. They circle it (presence) and, every few seconds, place a random
prefab of the project (primitives when there are none) or move, turn, resize or remove what they placed. Their
commands go through the same queue as everyone else's.

## REST

Every route is under `/api/coauthor/v1/workbenches/{wb}`. Send `X-Actor` (a name) and `X-Actor-Kind` (`human` or
`agent`). The Explorer also requires `X-Pipeline-Explorer: 1`.

| Route | Body / query | Returns |
| --- | --- | --- |
| `POST /commands` | `{ commands: [line \| {command, params} \| {argv}], transaction?, baseSeq?, scene? }`, optional `Idempotency-Key` header | `{ seq, results: [{ status: ok\|error\|skipped\|reverted, command, result \| code+message, seq?, changes? }] }` |
| `GET /commands` | `detail=compact\|full` | the catalog, plus `notImplemented` and `loadErrors` |
| `GET /events` | `since=seq` | server-sent events: `hello`, `write`, `save`, `discard`, `presence`, `assistant`, `resync` |
| `PUT /presence` / `DELETE /presence` | `{ scene, camera: {p, q, fov, aspect}, selection }` | the participants |
| `GET /draft` | | the base revision, the dirty files, the changes since the last save, the participants |
| `GET /scene` | `path` | what the scene places in the draft, with world poses |
| `GET /hierarchy` | `path` | the scene's GameObjects and prefab instances, as a tree |
| `POST /save` | `{ message? }` | `{ revision, message, files, changes, by }`, or 409 on a conflict |
| `POST /discard` | | `{ revision, dropped }` |
| `POST /demo`, `POST /demo/stop` | `{ scene, actors?, seconds?, center? }` | |
| `POST /ask` | `{ prompt, scene }` | `{ text, commands }` |

## Tests

```
dotnet test Tools/CoAuthoring.Tests
```

The tests run against a small fake project, served as a workbench from a folder. They cover:

- commands, references between commands, transactions and `batch`
- failed commands, and resending them
- conflicts, and retried requests with the same idempotency key
- 16 actors sending commands at once
- save and conflicting saves, and restarting from the draft on disk
- events and presence
- custom commands
- the demo crowd
