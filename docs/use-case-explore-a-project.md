# Explore a project and inspect assets

Browse a project's folders, look up an asset's GUID and facts, read its `.meta` file and source, render a preview image in the cloud, and run its imports. Everything on this page is a read: nothing changes the workbench.

## Prerequisites

* A workbench at a validated revision, and an environment for previews and imports. Refer to [Get started with the Pipeline API](get-started.md).
* The shell variables `API`, `WB`, `REV` and `ENV` from that page.

## List a folder

`GET …/revisions/{rev}/file-tree/{folder}` lists one level of a folder. Pass the folder as one path segment: encode `/` as `%2F`.

```bash
papi "$API/workbenches/$WB/revisions/$REV/file-tree/Assets%2FToon%20Gas%20Station"
```

```json
{
  "revision": "1",
  "entries": [
    { "path": "/Assets/Toon Gas Station/Models", "type": "folder" },
    { "path": "/Assets/Toon Gas Station/Prefabs", "type": "folder" },
    { "path": "/Assets/Toon Gas Station/_ReadFirst.txt", "type": "file" }
  ]
}
```

Each entry is a folder when its `type` is `folder`. Paths come back with a leading `/`: remove it before passing them to other routes. A folder's `.meta` files are listed as files too.

To list a whole tree, list each folder when it's expanded rather than walking the project up front.

> [!TIP]
> To find assets by name instead, use `GET …/revisions/{rev}/asset-search?q={text}`. An empty query lists every asset, packages included. To export the whole asset database, page through `GET …/revisions/{rev}/manifest?limit=1000&cursor={nextCursor}`.

## Resolve an asset's GUID

Imports, previews and `.meta` reads take the asset's GUID. Resolve paths in a batch:

```bash
papi -X POST "$API/workbenches/$WB/revisions/$REV/asset-guid" \
  -d '{"paths": ["Assets/Toon Gas Station/Prefabs/Barrel_1F.prefab"]}'
```

```json
{
  "revision": "1",
  "results": [
    { "assetGuid": "d6cda6cdccb84b3439d791e01c71339a" }
  ]
}
```

A path that isn't an asset (a folder, a file the Editor ignores) comes back with an `error` instead of an `assetGuid`.

## Read an asset's facts, metadata and source

With the GUID, read the asset's facts and its `.meta` file. With the path, read the file itself:

```bash
# Path, name, file hash, .meta hash, size.
papi "$API/workbenches/$WB/revisions/$REV/assets/d6cda6cdccb84b3439d791e01c71339a"

# The .meta file (YAML).
papi "$API/workbenches/$WB/revisions/$REV/assets/d6cda6cdccb84b3439d791e01c71339a/meta"

# The file's bytes, by path (one segment).
papi "$API/workbenches/$WB/revisions/$REV/files/Assets%2FToon%20Gas%20Station%2FPrefabs%2FBarrel_1F.prefab" -o Barrel_1F.prefab
```

The `fileHash` and `metafileHash` change whenever the file or its `.meta` changes, so they're a cheap way to tell whether an asset differs between two revisions or two branches. Refer to [Compare an asset across branches](use-case-compare-branches.md).

## Render a preview image

Project Service can render an asset's preview image in the environment's Editor. Request it, and repeat the request until it answers with the image:

```bash
papi -o Barrel_1F.png -w "%{http_code}\n" \
  -H "Accept: image/*, application/json" \
  "$API/environments/$ENV/revisions/$REV/previews/d6cda6cdccb84b3439d791e01c71339a"
# 202 while it renders; 200 with a PNG when it's ready
```

A cold render takes about two minutes. Assets that have no preview image, such as scenes and scripts, keep answering `202`; stop after a reasonable time.

## Run an asset's imports

An import runs an importer on the asset, in the environment's Editor, and produces artifacts:

```bash
papi -X POST "$API/environments/$ENV/revisions/$REV/imports" \
  -d '{"importAddresses": ["G:d6cda6cdccb84b3439d791e01c71339a"]}'
```

The answer has one slot per address: either the import manifest or an error.

```json
{
  "results": [
    {
      "manifest": {
        "artifacts": [ "" ],
        "files": [ { "name": "", "contentHash": "ca762f5c31aa12485ec23206367879d2" } ]
      }
    }
  ]
}
```

* `G:{guid}` is the asset's primary import: what the Editor imports it as. Its main artifact has an empty name.
* `T:{guid}+{importer type}` runs one specific importer, named by its full type name (namespace included). The slot's error is `import_not_found` when the project has no such importer. Refer to [Preview assets in a runtime player](use-case-preview-assets.md).

Download an artifact by the name its manifest lists. The import address is one path segment, so encode `:` and `+`:

```bash
ADDRESS='T%3Ad6cda6cdccb84b3439d791e01c71339a%2BUnity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter'

# A named artifact.
papi -o Barrel_1F.ca "$API/environments/$ENV/revisions/$REV/imports/$ADDRESS?artifactName=.ca"

# The main import result (an empty name): ask for "." instead.
papi -o Barrel_1F.bin "$API/environments/$ENV/revisions/$REV/imports/G%3Ad6cda6cdccb84b3439d791e01c71339a?artifactName=."
```

> [!NOTE]
> Right after a workbench starts, import requests can answer `409 revision_not_validated` for a few minutes, and the first import of an asset can take minutes while the environment's Editor starts. Re-send the request until it answers with a manifest. Imports are cached per revision, environment and address, so later requests return at once.

## Additional resources

* [Preview assets in a runtime player](use-case-preview-assets.md)
* [Compare an asset across branches](use-case-compare-branches.md)
* [Key concepts](key-concepts.md)
