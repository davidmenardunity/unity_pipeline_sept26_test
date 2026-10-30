# Unity.DataModel

A C# library for reading and changing Unity's text asset files: scenes (`.unity`), prefabs, materials
and ScriptableObjects (`.asset`). Everything that edits these files in this demo goes through it: the
Pipeline Explorer's **Inspector** tab and **Level editor** (via `Tools/PipelineExplorer/DataModelEndpoints.cs`).

It targets `netstandard2.1`, so the same DLL works in a .NET service and in the Unity Editor.

## How changes are written

Unity's own reader is forgiving, but diffs and merges aren't. The model never re-serializes a file. It
keeps the text it read and remembers where every value sits (line and characters). A change replaces
just those characters, and added objects or overrides are inserted as new lines. Every other byte,
including line endings, comes back unchanged. A file with no changes is written back identical.

Values Unity wraps over several lines (long flow maps, multi-line strings) and flow lists (`[]`) can be
read but not set in place; setting one throws `InvalidOperationException`.

## The model

| Type | What it is |
| --- | --- |
| `UnityFile` | One file: `Parse(text)`, `Objects`, `Find(fileId)`, `Hierarchy`, `SceneRoots`, `Changes`, `ToText()`, `ApplyChanges()`, `Revert()`, `AddPrefabInstance(...)`. |
| `UnityObject` | One object (`--- !u!{classId} &{fileId}`). Its fields are read and set by property path (`m_LocalPosition.x`, `m_Materials.0`, `m_SavedProperties.m_Colors.0._Color.r`): `GetString/GetNumber/GetBool/GetReference/GetVector3/GetQuaternion`, `SetString/SetNumber/SetBool/SetRaw/SetVector3/SetQuaternion`. |
| `GameObject` | `Name`, `IsActive`, `Components`, `Transform`. |
| `Transform` | `LocalPosition/LocalRotation/LocalScale/LocalPose`, `Father`, `ChildIds`, `SetLocalPose`. Also RectTransform. |
| `PrefabInstance` | `SourcePrefab`, `TransformParentId`, `Modifications`, `Name`, `FindModification`, `SetModification` (changes an override or adds one), `RootPose/SetRootPose`. |
| `SceneRoots` | The scene's root list (Unity 6.7). |
| `PrefabRoot` | A prefab file's root: the ids an instance's overrides target, its name and default pose. |
| `Hierarchy` | GameObjects under their parents (m_Children order), prefab instances under their parent transform, other objects. |
| `Levels.LevelScene` | A scene as a level: `Placeables()` (prefab instances and mesh objects with world poses), `Move(item, worldPose)`, `Add(NewInstance)`. |
| `Pose`, `Vector3`, `Quaternion` | Transform math (Unity's Euler order, compose, world → local). |
| `Yaml.*` | The text layer: `UnityYamlParser`, `YamlNode` (kind, raw text, property path, position), `YamlText` (quoting and numbers as Unity writes them). |

Strings are quoted the way Unity writes them: plain when unambiguous, otherwise single-quoted, and a
value that was quoted stays quoted. Numbers set from a `double` are written with 8 significant digits.
Numbers set from text (`SetNumber(path, "1.50")`) are written exactly as given.

```csharp
using Unity.DataModel;
using Unity.DataModel.Levels;

var scene = UnityFile.Parse(File.ReadAllText("Assets/Scenes/Level.unity"));

// A value, by object and property path.
var crate = scene.Objects.OfType<GameObject>().First(g => g.Name == "Crate");
crate.SetString("m_Name", "Crate (big)");
crate.Transform!.SetVector3("m_LocalScale", new Vector3(2, 2, 2));

// The level view: move an instance in world space, add a prefab.
var prefabs = LevelScene.PrefabGuids(scene).ToDictionary(g => g, g => PrefabRoot.Read(UnityFile.Parse(ReadPrefab(g))));
var level = new LevelScene(scene, prefabs);
var tree = level.Placeables().Items.First(i => i.Name == "Tree");
level.Move(tree, new Pose(new Vector3(4, 0, 2), Quaternion.Euler(0, 90, 0), Vector3.One));
long added = level.Add(new NewInstance(treeGuid, "Tree 2", new Pose(new Vector3(6, 0, 2), Quaternion.Identity, Vector3.One)));

File.WriteAllText("Assets/Scenes/Level.unity", scene.ToText());
```

## REST (Pipeline Explorer)

Under `/api/workbenches/{wb}/revisions/{rev}`. Each endpoint reads the file at that revision; saving commits a new revision.

| Endpoint | Body / query | Returns |
| --- | --- | --- |
| `GET /model` | `path` | `objects` (each document's fields: key, path, kind, raw, value, readOnly, ref, items, children), `hierarchy`, `text` |
| `POST /model/edit` | `{ path, edits: [{ fileId, path, value, as: "string"\|"number"\|"bool"\|"raw" }], message?, branch?, preview? }` | `{ revision }`, or with `preview` the new `text` |
| `GET /level` | `path` | counts, and the GUIDs of the prefabs it uses |
| `GET /level/prefab` | `guid` or `path` | `{ guid, path, name, ok, reason }` |
| `GET /level/placeables` | `path` | `items` (id, kind, name, assetGuid, primitive, world `{p, q, s}`), `skipped` |
| `POST /level/save` | `{ path, moved: [{ id, world }], added: [{ key, assetGuid, name, world }], message?, branch?, preview? }` | `{ revision, added: { key: fileId }, placed }`, or with `preview` the new `text` |

## Tests

```
dotnet test Tools/Unity.DataModel.Tests
```

Golden tests compare the model's output with what the browser-side editor it replaced wrote for the same
changes. Those fixtures are in `Fixtures/Expected`. Another test reads every YAML file in the project's
`Assets/` and checks it is written back byte for byte.
