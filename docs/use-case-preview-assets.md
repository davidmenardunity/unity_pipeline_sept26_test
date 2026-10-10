# Preview assets in a runtime player

Show a project's assets in a real runtime player, such as a WebGL build, without building the game. Project Service runs your own importer on the asset, for the player's platform, and produces a content archive (`.ca`). The player downloads the archive, mounts it and instantiates the asset.

This lets you see a mesh an artist pushed a minute ago, on the target platform, with your scene's lighting and shaders.

## Prerequisites

* A workbench at a validated revision. Refer to [Get started with the Pipeline API](get-started.md).
* An environment for the player's platform, for example a `webgl` build profile for a WebGL player.
* A preview importer and a preview player in your project, as described below.

## How it works

```
Preview player                                    Unity Pipeline
──────────────                                    ──────────────
POST …/imports {T:{guid}+{PreviewContentImporter}} ─▶ runs the importer in a WebGL Editor
                                                    ◀── import manifest: artifact ".ca", contentHash
GET  …/imports/{address}?artifactName=.ca ─────────▶ the content archive
Mount the archive, load its content file, instantiate the asset
```

## Write the importer

The importer is a non-primary `ScriptedImporter`: no asset imports with it by default, and Project Service runs it on demand by its type name. It serializes the asset and everything it references into one content file, adds a small manifest, and packs both into a `.ca` with `ContentBuildInterface.ArchiveAndCompress`.

```csharp
// Registered on a dummy extension: nothing imports with it unless asked to (T:{guid}+{type}).
[ScriptedImporter(8, "___preview_content___")]
public class PreviewContentImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var guid = AssetDatabase.GUIDFromAssetPath(ctx.assetPath);
        ctx.DependsOnArtifact(guid);
        var target = ctx.selectedBuildTarget;   // the environment's platform: WebGL here

        var objects = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, target);
        var dependencies = ContentBuildInterface.GetPlayerDependenciesForObjects(objects, target, null);
        // Write objects + dependencies with ContentBuildInterface.WriteSerializedFile, then pack the
        // content file, its .resS and a manifest with ContentBuildInterface.ArchiveAndCompress.
        byte[] archive = BuildContentArchive(ctx, guid, target, objects, dependencies);

        File.WriteAllBytes(ctx.GetOutputArtifactFilePath("ca"), archive);
    }
}
```

Keep these points in mind:

* **Declare the dependencies.** Call `ctx.DependsOnArtifact` for every asset the archive includes, so it's rebuilt when a material or texture it uses changes, not only the asset itself.
* **Keep engine built-ins out.** Objects in `unity default resources` ship with every player. Map them in the reference map instead of serializing them, and record in the manifest that the loader must pass `ContentFile.GlobalTableDependency`.
* **Strip shader variants for the preview scene.** Compute the build usage from the scene the player shows, so the archive's shaders have the variants that scene needs (its fog mode, its lightmap modes).
* **Bump the importer version** (`[ScriptedImporter(8, …)]`) whenever the archive's layout changes, so cached archives are rebuilt.

> [!IMPORTANT]
> Project Service imports from your repository. Commit and push the importer, then create a workbench from a commit that includes it. A workbench made before the importer existed answers `Type not found for scriptedImporterTypeName`.

## Build an archive

Request the asset's import with the importer's full type name, in the player's environment:

```bash
GUID=d6cda6cdccb84b3439d791e01c71339a
IMPORTER=Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter

papi -X POST "$API/environments/$ENV/revisions/$REV/imports" \
  -d '{"importAddresses": ["T:'"$GUID"'+'"$IMPORTER"'"]}'
```

```json
{
  "results": [
    {
      "manifest": {
        "artifacts": [ ".ca" ],
        "files": [ { "name": ".ca", "contentHash": "ca762f5c31aa12485ec23206367879d2" } ]
      }
    }
  ]
}
```

The first import for an asset can take a few minutes while the environment's Editor starts and imports the asset. The request can time out or answer `409` meanwhile: send it again. Once the archive exists, the same request answers at once.

Then download the archive:

```bash
papi -o Barrel_1F.ca \
  "$API/environments/$ENV/revisions/$REV/imports/T%3A$GUID%2B$IMPORTER?artifactName=.ca"
```

## Load the archive in the player

Mount the archive with a mount prefix you've never used before, load its content file, and instantiate the first GameObject:

```csharp
IEnumerator Show(string archivePath, int seq)
{
    // Never reuse a mount prefix: a remounted one yields content that never finishes loading.
    var archive = ArchiveFileInterface.MountAsync(ContentNamespace.Default, archivePath, $"preview{seq}:");
    archive.JobHandle.Complete();
    var mount = archive.GetMountPath();

    var manifest = ReadManifest(mount);   // the content file's name, and whether it uses built-ins
    var deps = new NativeArray<ContentFile>(manifest.requiresDefaultResources ? 1 : 0, Allocator.Temp);
    if (manifest.requiresDefaultResources) deps[0] = ContentFile.GlobalTableDependency;
    var file = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, mount + manifest.contentFileName, deps);
    deps.Dispose();

    while (file.LoadingStatus == LoadingStatus.InProgress) yield return null;
    var prefab = file.GetObjects().OfType<GameObject>().First();
    Instantiate(prefab);
}
```

To tear a preview down, destroy the instance first, then unload the content file, then unmount the archive.

> [!NOTE]
> On WebGL, materials whose shaders need shader model 4.5 render pink with WebGL 2. Prefer WebGPU, which supports them, and keep WebGL 2 as the fallback.

## Know when to rebuild

The import manifest's `contentHash` for the `.ca` covers everything in the archive, dependencies included. Keep it with the archive: when a new revision gives a different hash, the asset or something it uses changed. A changed FBX that a prefab uses changes the prefab's archive hash, even though the prefab file itself is the same.

## Additional resources

* [Play whole scenes from content archives](use-case-scene-archives.md)
* [Explore a project and inspect assets](use-case-explore-a-project.md)
* [Key concepts](key-concepts.md)
