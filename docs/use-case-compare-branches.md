# Compare an asset across branches

Read one asset from two git branches and compare it: its file, its `.meta` file and what it imports as. For example, check what a branch changed in a prefab before merging it, or confirm that a model re-exported from a DCC tool really changed.

The Pipeline API reads through workbenches, and a workbench belongs to one branch, so you compare through two workbenches.

## Prerequisites

* A workbench on each branch, both at a validated revision. Refer to [Get started with the Pipeline API](get-started.md).
* An environment for each workbench, to compare import results.

## Find a workbench for each branch

List the project's workbenches:

```bash
papi "$API/workbenches"
```

A workbench's `branchName` is its own branch, always `main`, not the git branch it came from. To tell which git branch a workbench belongs to, use the branch you recorded when you created it (refer to [Create a workbench](get-started.md#create-a-workbench)). Failing that, match the workbench's `upstreamRevision` against the branch heads from `git ls-remote`. This works only while the branches point to different commits, so record the branch when you can.

```bash
git ls-remote https://github.com/acme/space-shooter.git
# 797aba99…  refs/heads/demo-123
# 844f8b2…   refs/heads/main
```

If a branch has no workbench, create one. Remember that a workbench stays at the commit it was created from: compare against a workbench made after the changes you care about.

## Read the asset on both sides

Resolve the asset on each workbench at its validated revision. A GUID is the same on every branch, unless one of them deleted and re-added the asset.

```bash
for side in "$WB_A $REV_A" "$WB_B $REV_B"; do
  set -- $side
  papi -X POST "$API/workbenches/$1/revisions/$2/asset-guid" \
    -d '{"paths": ["Assets/Toon Gas Station/Models/Tree_6D.fbx"]}'
done
```

If the asset doesn't exist on one side, its slot has an error instead of a GUID.

## Compare the files

Compare the asset's facts first: they're cheap.

```bash
papi "$API/workbenches/$WB_A/revisions/$REV_A/assets/$GUID"
papi "$API/workbenches/$WB_B/revisions/$REV_B/assets/$GUID"
```

* Different `fileHash`: the file itself changed. For text assets (prefabs, scenes, materials, ScriptableObjects), download both files (`…/files/{path}`) and diff them line by line.
* Different `metafileHash`: the import settings changed. Download both `.meta` files (`…/assets/{guid}/meta`) and diff them.

## Compare what the asset imports as

Identical files can still import differently, because an asset's import depends on other assets. A prefab looks the same while the FBX it uses was re-exported. Request the same import on both sides, and compare the content hashes in the import manifests:

```bash
ADDRESS='T:'"$GUID"'+Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter'

papi -X POST "$API/environments/$ENV_A/revisions/$REV_A/imports" -d '{"importAddresses": ["'"$ADDRESS"'"]}'
papi -X POST "$API/environments/$ENV_B/revisions/$REV_B/imports" -d '{"importAddresses": ["'"$ADDRESS"'"]}'
```

Compare `files[].contentHash` for the artifact (`.ca` here) between the two manifests. Equal hashes mean the two branches produce the same content. Different hashes with identical files mean a dependency changed.

| File | .meta | Import content hash | Meaning |
| --- | --- | --- | --- |
| Same | Same | Same | Identical. |
| Same | Same | Different | A dependency changed (a model, a material, a texture). |
| Different | Any | Different | The asset changed. |
| Same | Different | Different | The import settings changed. |

To compare visually, download both artifacts and show them side by side in two preview players. Refer to [Preview assets in a runtime player](use-case-preview-assets.md).

> [!NOTE]
> Both sides must import for the same platform. Use an environment with the same build target on each workbench.

## Additional resources

* [Explore a project and inspect assets](use-case-explore-a-project.md)
* [Preview assets in a runtime player](use-case-preview-assets.md)
