# Play whole scenes from content archives

Build a content archive for a whole scene (`.unity`) in the cloud, and load and play it in a generic runtime player. Unlike rebuilding a scene from its prefabs (refer to [Build a level editor](use-case-level-editor.md)), the archive holds the real scene: its lighting, terrain, objects that aren't prefabs, and the scene's own settings.

## Prerequisites

* A workbench at a validated revision, created from a commit that includes your scene importer. Refer to [Get started with the Pipeline API](get-started.md).
* An environment for the player's platform.
* A player built from its own build profile, with an empty scene that loads scene archives.

## Scenes and assets use different APIs

Unity's content build and load APIs have parallel paths for assets and for scenes. A scene can't go through the asset path, and the reverse is also true:

| | Assets | Scenes |
| --- | --- | --- |
| Find what to include | `ContentBuildInterface.GetPlayerObjectIdentifiersInAsset`, `GetPlayerDependenciesForObjects` | `ContentBuildInterface.CalculatePlayerDependenciesForScene` |
| Write | `ContentBuildInterface.WriteSerializedFile` | `ContentBuildInterface.WriteSceneSerializedFile` |
| Load | `ContentLoadInterface.LoadContentFileAsync` | `ContentLoadInterface.LoadSceneAsync` |

A scene archive therefore holds two content files: the objects the scene references, written with the asset path, and the scene itself, written with the scene path and referring to the first file.

## Write the scene importer

Like the [asset preview importer](use-case-preview-assets.md#write-the-importer), the scene importer is a non-primary `ScriptedImporter` that Project Service runs by type name.

```csharp
[ScriptedImporter(1, "___sceneviewer_scene___")]
public class PreviewSceneImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var scenePath = ctx.assetPath;
        ctx.DependsOnSourceAsset(AssetDatabase.GUIDFromAssetPath(scenePath));
        var target = ctx.selectedBuildTarget;
        var settings = new BuildSettings { target = target, group = BuildPipeline.GetBuildTargetGroup(target) };

        // The scene's references, and its own shader-feature usage (fog mode, lightmaps).
        var usage = new BuildUsageTagSet();
        var info = ContentBuildInterface.CalculatePlayerDependenciesForScene(scenePath, settings, usage);
        var globalUsage = info.globalUsage;

        // 1. Everything the scene references, as one content file ("{name}_deps").
        var deps = ExpandDependencies(info.referencedObjects, target);   // + what those reference
        var depsCommand = WriteCommandFor(depsName, deps);
        ContentBuildInterface.CalculateBuildUsageTags(deps, deps, globalUsage, usage);
        var depsResult = ContentBuildInterface.WriteSerializedFile(folder, new WriteParameters
        {
            writeCommand = depsCommand, settings = settings, globalUsage = globalUsage,
            usageSet = usage, referenceMap = MapOf(depsName, depsCommand),
        });

        // 2. The scene itself ("{name}_scene"): no objects of its own to list; it refers to the first file.
        var sceneCommand = new WriteCommand { internalName = sceneName, fileName = sceneName,
                                              serializeObjects = new List<SerializationInfo>() };
        var sceneResult = ContentBuildInterface.WriteSceneSerializedFile(folder, new WriteSceneParameters
        {
            scenePath = info.scene, writeCommand = sceneCommand, settings = settings,
            globalUsage = globalUsage, usageSet = usage, referenceMap = MapOf(depsName, depsCommand),
        });

        // 3. A manifest, then pack everything into one .ca.
        //    sceneResult.externalFileReferences gives the order LoadSceneAsync needs its dependencies in.
        WriteManifest(folder, sceneName, depsName, sceneResult.externalFileReferences, depsResult.externalFileReferences);
        File.WriteAllBytes(ctx.GetOutputArtifactFilePath("ca"), ArchiveAndCompress(folder));
    }
}
```

Keep these points in mind:

* **Record the dependency order.** `LoadSceneAsync` takes the scene's dependencies in the order the build reported them in `WriteResult.externalFileReferences`. Write that order into the manifest: the dependency content file, or `ContentFile.GlobalTableDependency` for `unity default resources`.
* **Use the scene's shader usage.** `SceneDependencyInfo.globalUsage` describes the scene's fog and lightmap modes. Use it for both files, so the archive's shaders have the variants the scene needs.
* **Write the manifest so a dropped byte is harmless.** The archive's file system can return small files one byte short. Use a line-based format that ends with a blank line, rather than JSON whose last brace matters.
* **Expect a slow first import.** A scene's first import compiles every shader variant its materials need. In Project Service that can take several minutes; later imports reuse the shader cache.

Test the importer locally before you push it. In the Editor, produce its artifact with `AssetDatabaseExperimental.ProduceArtifact(new ArtifactKey(sceneGuid, typeof(PreviewSceneImporter)))`. That's the same import path Project Service uses.

## Build the archive

Request the scene's import with the scene importer:

```bash
SCENE_GUID=40ef6e60490d68541971945758777f58   # Assets/Toon Gas Station/Scenes/Demo_Scene_1.unity

papi -X POST "$API/environments/$ENV/revisions/$REV/imports" \
  -d '{"importAddresses": ["T:'"$SCENE_GUID"'+Unity.Pipeline.SceneViewer.Editor.PreviewSceneImporter"]}'

papi -o Demo_Scene_1.ca \
  "$API/environments/$ENV/revisions/$REV/imports/T%3A$SCENE_GUID%2BUnity.Pipeline.SceneViewer.Editor.PreviewSceneImporter?artifactName=.ca"
```

## Load and play the scene

Mount the archive, load the dependency file, then load the scene additively and make it the active scene, so its lighting, fog and skybox apply:

```csharp
IEnumerator Play(string archivePath, int seq)
{
    var archive = ArchiveFileInterface.MountAsync(ContentNamespace.Default, archivePath, $"scene{seq}:");
    archive.JobHandle.Complete();
    var mount = archive.GetMountPath();
    var manifest = ReadManifest(mount + "scene_manifest.txt");

    // The objects the scene uses.
    var depsDeps = Dependencies(manifest.DepsDependencies, default);
    var deps = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, mount + manifest.DepsFile, depsDeps);
    depsDeps.Dispose();
    while (deps.LoadingStatus == LoadingStatus.InProgress) yield return null;

    // The scene, its dependencies in the recorded order.
    var sceneDeps = Dependencies(manifest.SceneDependencies, deps);
    var parameters = new ContentSceneParameters { loadSceneMode = LoadSceneMode.Additive, autoIntegrate = true };
    var scene = ContentLoadInterface.LoadSceneAsync(ContentNamespace.Default, mount + manifest.SceneFile,
                                                    manifest.SceneName, parameters, sceneDeps);
    sceneDeps.Dispose();
    while (scene.Status != SceneLoadingStatus.Complete && scene.Status != SceneLoadingStatus.Failed) yield return null;

    SceneManager.SetActiveScene(scene.Scene);
}

static NativeArray<ContentFile> Dependencies(string[] slots, ContentFile deps)
{
    var array = new NativeArray<ContentFile>(slots.Length, Allocator.Temp);
    for (var i = 0; i < slots.Length; i++)
        array[i] = slots[i] == "global" ? ContentFile.GlobalTableDependency : deps;
    return array;
}
```

To unload, call `scene.UnloadAtEndOfFrame()` and wait a frame, then unload the dependency file, then unmount the archive.

To play the scene, use the scene's own camera and controllers if it has them. Otherwise, add your own first-person or fly camera to the loaded scene.

## Keep the player generic

A scene archive carries art, not code. On platforms that use IL2CPP, such as WebGL, scripts are compiled into the player. So:

* **Scripts must be in the player.** A scene's components only run if the player was built with their code. Keep gameplay code in assemblies every player includes, and preserve them with a `link.xml`: nothing in the player's own scene references them, so code stripping would remove them.
* **Keep shader variants for any scene.** Shaders that a render pipeline looks up at runtime, such as URP's terrain grass shaders, come from the player, not the archive. Unity strips their variants for the player's own scenes, and an empty player scene has no fog. In **Project Settings > Graphics**, set fog modes to **Custom** and keep Linear, Exponential and Exponential Squared. Otherwise grass in a scene with fog renders as flat fog color.
* **Give the player its own build profile.** Then changing it doesn't rebuild your other players. Turn off WebGL compression (**Player Settings > Publishing Settings > Compression Format: Disabled**) when the player is served locally: Brotli on a large `.wasm` adds minutes to every build.

## Known limitations

* Lighting that depends on lighting data Unity computes when it loads a scene the usual way, such as a skybox-based ambient probe, can render incorrectly in a scene loaded from a content archive. Scenes with trilight or flat ambient light render as expected.
* A render pipeline's runtime lookups (terrain detail shaders and similar) resolve from the player's graphics settings, which an archive can't supply.

## Additional resources

* [Preview assets in a runtime player](use-case-preview-assets.md)
* [Build a level editor](use-case-level-editor.md)
* [Troubleshooting](troubleshooting.md)
