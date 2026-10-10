# Edit a project

Change values in a project's prefabs, scenes, ScriptableObjects and materials, and save the changes as a new workbench revision. For example, change a light's intensity in a scene, or a material's color, without a local checkout and without the Unity Editor.

These assets are YAML text files. You read the file at a revision, change the values in the text, and write the whole file back in a transaction.

## Prerequisites

* A workbench at a validated revision. Refer to [Get started with the Pipeline API](get-started.md).
* The asset's path. To browse for it, refer to [Explore a project and inspect assets](use-case-explore-a-project.md).

## Read the asset

Download the file at the revision you're editing from:

```bash
papi -o Fur.mat "$API/workbenches/$WB/revisions/$REV/files/Assets%2FGameplay%2FSquirrel%2FMaterials%2FSquirrel_Fur.mat"
```

A Unity YAML file is a series of documents, one per object. Each starts with a header that gives the object's class ID and file ID:

```yaml
--- !u!21 &2100000
Material:
  m_Name: Squirrel_Fur
  m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}
  m_SavedProperties:
    m_Colors:
    - _BaseColor: {r: 0.55, g: 0.26, b: 0.09, a: 1}
```

In a prefab or a scene, GameObjects (`!u!1`) list their components, and each component (`!u!4` Transform, `!u!23` MeshRenderer, `!u!114` MonoBehaviour…) holds its serialized fields. Prefab instances in scenes (`!u!1001`) don't copy the prefab: they store overrides, as `m_Modifications` entries with a `target`, a `propertyPath` and a `value`.

## Change values

Change only the characters of the values you edit, and keep everything else byte for byte. Unity reads the file either way, but untouched lines keep diffs small and merges clean.

* **Scalars:** replace the value after `key: `. Strings that need it are quoted; keep the quoting style.
* **Flow mappings:** colors and vectors such as `{r: 1, g: 0.92, b: 0.78, a: 1}` are edited per field.
* **Wrapped values:** Unity wraps long strings over several lines. Rewrite the whole value, or leave it unchanged.
* **Prefab overrides:** to change a prefab instance's value in a scene, change its `m_Modifications` entry, or add one with the right `target` (the prefab's object, by file ID and prefab GUID) and `propertyPath`.

```diff
-    - _BaseColor: {r: 0.55, g: 0.26, b: 0.09, a: 1}
+    - _BaseColor: {r: 0.62, g: 0.30, b: 0.11, a: 1}
```

## Save the change as a new revision

Saving takes three requests: upload the new bytes, stage them in a transaction, and commit with a message.

### 1. Upload the file

Upload the bytes as a blob. The blob's ID is the content's MD5 hash, so uploading identical bytes twice costs nothing:

```bash
CONTENT_GUID=$(md5sum Fur.mat | cut -d' ' -f1)
curl -sS -X PUT -H "Authorization: Bearer $UNITY_JWT" -H "Content-Type: application/octet-stream" \
  --data-binary @Fur.mat "$API/workbenches/$WB/blobs/$CONTENT_GUID"
```

```json
{ "uploadHandle": "c1b9…", "contentGuid": "5e0d…", "byteCount": 2304 }
```

### 2. Stage it in a transaction

Open a transaction on the workbench's own branch, which is always `main`, whatever git branch the workbench came from. Then bind the path to the upload handle:

```bash
TX=$(papi -X POST "$API/workbenches/$WB/transactions" -d '{"branchName": "main"}' | jq -r .transactionId)

papi -X POST "$API/workbenches/$WB/transactions/$TX/assets" -d '[
  { "path": "Assets/Gameplay/Squirrel/Materials/Squirrel_Fur.mat", "uploadHandle": "c1b9…" }
]'
```

Stage several files in one transaction to save them as one revision: for example, a prefab and its material.

* **New folders:** create them before you stage files into them: `POST …/transactions/{tx}/assets {"type": "folders", "paths": ["Assets/New", "Assets/New/Sub"]}`, outermost first.
* **Renames and moves:** use `PATCH …/transactions/{tx}/assets {"type": "move", "pathsToMove": [["Assets/A.mat", "Assets/B.mat"]]}`. A move keeps the asset's GUID, so references to it stay intact; deleting and uploading again would give it a new GUID.

### 3. Commit

Commit with a message describing the change:

```bash
papi -X PATCH "$API/workbenches/$WB/transactions/$TX" \
  -d '{"status": "committed", "message": "Squirrel fur: warmer brown"}'
```

```json
{ "revisionId": "2", "noNewRevision": false }
```

`noNewRevision` is `true` when the staged files were identical to the revision's. If anything fails before the commit, abort the transaction with `DELETE …/transactions/{tx}`: a workbench holds one open transaction at a time, and an abandoned one blocks the next until it times out.

## Wait for the new revision

The new revision is validated before it can be read. Poll the head until `validatedRevision` is the committed revision:

```bash
papi "$API/workbenches/$WB/head"
# {"branch":"main","head":"2","validatedRevision":"1","validating":true}
# {"branch":"main","head":"2","validatedRevision":"2","validating":false}
```

Then read, preview or import the asset at the new revision. Imports and previews of the changed asset, and of anything that uses it, are rebuilt for the new revision.

> [!NOTE]
> Saving changes only the workbench. To push the changes to your git repository, refer to [Publish workbench changes to git](publish-to-git.md).

## Additional resources

* [Build a level editor](use-case-level-editor.md)
* [Publish workbench changes to git](publish-to-git.md)
* [Key concepts](key-concepts.md)
