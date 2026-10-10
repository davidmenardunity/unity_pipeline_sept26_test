# Build a level editor

Open a scene from your project in a lightweight runtime, add prefabs to it, move objects, and save the scene back to the workbench as a new revision, without the Unity Editor. This use case combines reading a scene's YAML, building content archives for its prefabs, and writing the changed scene in a transaction.

## Prerequisites

* A workbench at a validated revision, and an environment for the runtime's platform. Refer to [Get started with the Pipeline API](get-started.md).
* A preview importer that builds a content archive for one asset. Refer to [Preview assets in a runtime player](use-case-preview-assets.md).
* A runtime player (for example, a WebGL build) with an empty scene that can load content archives and place their objects. It needs no content of its own.

## How it works

```
1. Read the scene file          GET …/revisions/{rev}/files/{scene path}
2. Read each prefab it uses     GET …/revisions/{rev}/files/{prefab path}
3. Build their archives         POST …/environments/{env}/revisions/{rev}/imports {T:{guid}+{importer}}
4. Place them in the runtime    download each .ca, load it, instantiate at the scene's transforms
5. Save the edited scene        PUT …/blobs, POST …/transactions, PATCH …/transactions/{tx}
```

## Read the scene

Download the scene at the revision and parse its YAML documents:

```bash
papi -o SampleScene.unity "$API/workbenches/$WB/revisions/$REV/files/Assets%2FScenes%2FSampleScene.unity"
```

The documents a level editor needs:

| Class ID | Document | What to read |
| --- | --- | --- |
| `4` | Transform | `m_LocalPosition`, `m_LocalRotation`, `m_LocalScale` and `m_Father` (the parent transform). |
| `1001` | PrefabInstance | `m_SourcePrefab` (the prefab's GUID), `m_Modification.m_TransformParent`, and the `m_Modifications` overrides. |
| `33` | MeshFilter | Objects with a mesh that aren't prefab instances. |
| `1660057539` | SceneRoots | The scene's root objects, in order (Unity 6 scenes). |

A prefab instance doesn't store its position in a Transform document. It stores overrides on the prefab's root transform:

```yaml
--- !u!1001 &1543885624
PrefabInstance:
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 4274984841250312, guid: d6cda6cdccb84b3439d791e01c71339a, type: 3}
      propertyPath: m_LocalPosition.x
      value: -2.83
      objectReference: {fileID: 0}
  m_SourcePrefab: {fileID: 100100000, guid: d6cda6cdccb84b3439d791e01c71339a, type: 3}
```

To place an instance, start from the prefab's own root transform and apply its overrides. Then compose with its parents' transforms, up to the scene root.

## Read the prefabs

For each prefab GUID, find its path (`GET …/revisions/{rev}/assets/{guid}`) and read the prefab file. You need:

* The root GameObject and its root Transform's file IDs (the Transform whose `m_Father` is `{fileID: 0}`). Overrides target those IDs.
* The root Transform's own position, rotation and scale: the values an instance has when it doesn't override them.

Prefab variants have no plain root transform of their own. Skip them, or resolve their base prefab first.

## Build and load the archives

Request each prefab's archive with your preview importer in the runtime's environment, a few at a time:

```bash
papi -X POST "$API/environments/$ENV/revisions/$REV/imports" \
  -d '{"importAddresses": ["T:'"$PREFAB_GUID"'+Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter"]}'
```

Show placeholders while the archives build, then download each `.ca` and replace the placeholders that use it with instances. Mount each archive under a new mount prefix; refer to [Load the archive in the player](use-case-preview-assets.md#load-the-archive-in-the-player).

## Add and move objects

Do all edits in the runtime, and keep a list of what changed:

* **Moved objects:** their new world transform.
* **Added prefabs:** their prefab GUID and world transform.

Convert world transforms back to local ones under each object's parent before you write them.

## Write the changes into the scene

Edit the scene's text in place, as in [Edit a project](use-case-edit-a-project.md#change-values):

* **A moved prefab instance:** update its `m_LocalPosition.*`, `m_LocalRotation.*` and `m_LocalScale.*` overrides, and add the ones it doesn't have yet.
* **A moved plain object:** update its Transform document's `m_LocalPosition`, `m_LocalRotation` and `m_LocalScale`.
* **An added prefab:** add a PrefabInstance document with a new, unused file ID, before the SceneRoots document. Then add its file ID to SceneRoots' `m_Roots`.

```yaml
--- !u!1001 &4040807847533014664
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 1664475624090918, guid: d6cda6cdccb84b3439d791e01c71339a, type: 3}
      propertyPath: m_Name
      value: Barrel_1F
      objectReference: {fileID: 0}
    - target: {fileID: 4274984841250312, guid: d6cda6cdccb84b3439d791e01c71339a, type: 3}
      propertyPath: m_LocalPosition.x
      value: -16.68
      objectReference: {fileID: 0}
    # … m_LocalPosition.y/z and m_LocalRotation.x/y/z/w the same way
    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: d6cda6cdccb84b3439d791e01c71339a, type: 3}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 330585546}
  - {fileID: 4040807847533014664}
```

## Save the scene

Upload the edited scene, stage it, and commit, as in [Save the change as a new revision](use-case-edit-a-project.md#save-the-change-as-a-new-revision):

```bash
CONTENT_GUID=$(md5sum SampleScene.unity | cut -d' ' -f1)
HANDLE=$(curl -sS -X PUT -H "Authorization: Bearer $UNITY_JWT" -H "Content-Type: application/octet-stream" \
  --data-binary @SampleScene.unity "$API/workbenches/$WB/blobs/$CONTENT_GUID" | jq -r .uploadHandle)
TX=$(papi -X POST "$API/workbenches/$WB/transactions" -d '{"branchName": "main"}' | jq -r .transactionId)
papi -X POST "$API/workbenches/$WB/transactions/$TX/assets" \
  -d '[{"path": "Assets/Scenes/SampleScene.unity", "uploadHandle": "'"$HANDLE"'"}]'
papi -X PATCH "$API/workbenches/$WB/transactions/$TX" \
  -d '{"status": "committed", "message": "SampleScene: add a barrel by the gas pump"}'
```

Wait for the new revision to validate, then read the scene again from it. The new revision's file is the one to edit next, so later saves build on it.

> [!TIP]
> Keep each runtime object's identity (the prefab instance's file ID) across saves, so the runtime keeps showing the same objects after you re-read the saved scene.

## Limits of this approach

* Only prefab instances and plain objects with meshes are placed. Lights, cameras and other components come from the scene's own archive; refer to [Play whole scenes from content archives](use-case-scene-archives.md).
* Objects nested inside a prefab instance are placed with the instance's root.
* Removing objects that the scene already had needs their documents and references removed too, which this approach doesn't cover.

## Additional resources

* [Edit a project](use-case-edit-a-project.md)
* [Preview assets in a runtime player](use-case-preview-assets.md)
* [Publish workbench changes to git](publish-to-git.md)
