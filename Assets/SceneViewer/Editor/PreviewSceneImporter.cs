using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Build.Content;
using UnityEditor.Experimental;
using UnityEngine;

namespace Unity.Pipeline.SceneViewer.Editor
{
    // Builds a content archive (.ca) for a whole scene (.unity), for the Scene Viewer player
    // (SceneViewer_WebGL): two content files and a manifest.
    //   - "{name}_deps": every object the scene references (prefabs, meshes, materials, textures, terrain
    //     data, the skybox…), written like PreviewContentImporter writes one asset.
    //   - "{name}_scene": the scene itself (WriteSceneSerializedFile), referring to the first file.
    //   - scene_manifest.txt: the file names, and the order the scene's dependencies must be passed to
    //     ContentLoadInterface.LoadSceneAsync (the order the build reported them).
    // Shader variants are kept for the scene's own feature usage (its fog mode, lightmaps…), not the
    // Scene Preview's, so the scene renders as it does in the editor.
    //
    // Like PreviewContentImporter, a non-primary importer: Project Service runs it by type name
    // (T:{guid}+Unity.Pipeline.SceneViewer.Editor.PreviewSceneImporter). Bump the version when the
    // archive layout changes, so cached archives are rebuilt.
    [ScriptedImporter(1, "___sceneviewer_scene___")]
    public class PreviewSceneImporter : ScriptedImporter
    {
        public const string ArchiveArtifactName = "ca";
        public const string ManifestFileName = "scene_manifest.txt";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var scenePath = ctx.assetPath;
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                ctx.LogImportError($"Not a scene: {scenePath}");
                return;
            }
            var sceneGuid = AssetDatabase.GUIDFromAssetPath(scenePath);
            if (sceneGuid.Empty())
            {
                ctx.LogImportError($"Scene not found: {scenePath}");
                return;
            }
            // A scene has no importer artifact of its own to depend on: its source file is the dependency.
            ctx.DependsOnSourceAsset(sceneGuid);
            var target = ctx.selectedBuildTarget;   // reading it keys the artifact per platform

            var bytes = BuildSceneArchive(scenePath, target, message => ctx.LogImportError(message), guid => ctx.DependsOnArtifact(guid));
            if (bytes == null) return;
            File.WriteAllBytes(ctx.GetOutputArtifactFilePath(ArchiveArtifactName), bytes);
            Debug.Log($"PreviewSceneImporter: wrote {target} scene archive ({bytes.Length} bytes) for {scenePath}");
        }

        /// <summary>The archive's bytes, or null after reporting why to <paramref name="error"/>.</summary>
        public static byte[] BuildSceneArchive(string scenePath, BuildTarget target, Action<string> error, Action<GUID> dependsOn = null)
        {
            // Shader variant stripping runs inside the write calls only with the build callbacks registered
            // (272 = ShaderProcessors | ComputeShader), as in PreviewContentImporter.
            var callbacks = Type.GetType("UnityEditor.Build.BuildPipelineInterfaces, UnityEditor");
            var init = callbacks?.GetMethod("InitializeBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
            var cleanup = callbacks?.GetMethod("CleanupBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
            try { init?.Invoke(null, new object[] { 272 }); }
            catch (TargetInvocationException e)
            {
                Debug.LogWarning($"PreviewSceneImporter: shader variant stripping is off for {scenePath} ({e.GetBaseException().Message})");
            }

            var folder = Path.Combine("Temp", "PreviewScene_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            try
            {
                var settings = new BuildSettings { target = target, group = BuildPipeline.GetBuildTargetGroup(target), typeDB = null };
                var usageSet = new BuildUsageTagSet();
                var info = ContentBuildInterface.CalculatePlayerDependenciesForScene(scenePath, settings, usageSet);
                var globalUsage = info.globalUsage;   // the scene's own fog, lightmap and probe usage

                var referenced = info.referencedObjects.ToArray();
                bool IsDefault(ObjectIdentifier o) => o.filePath.Contains("unity default resources");
                var defaults = referenced.Where(IsDefault).ToArray();
                // The scene's direct references, then everything they pull in (a prefab's meshes, a
                // material's textures…).
                var direct = referenced.Where(o => !IsDefault(o)).ToArray();
                var closure = ContentBuildInterface.GetPlayerDependenciesForObjects(direct, target, null);
                defaults = defaults.Concat(closure.Where(IsDefault)).Distinct().ToArray();
                var depsObjects = direct.Concat(closure.Where(o => !IsDefault(o))).Distinct().ToArray();
                foreach (var guid in depsObjects.Select(o => o.guid).Distinct())
                    if (!guid.Empty()) dependsOn?.Invoke(guid);

                var name = Sanitize(Path.GetFileNameWithoutExtension(scenePath));
                var depsName = name + "_deps";
                var sceneName = name + "_scene";
                var resources = new List<ResourceFile>();

                // 1. The scene's dependencies, as one content file.
                var depsCommand = new WriteCommand { internalName = depsName, fileName = depsName, serializeObjects = new List<SerializationInfo>() };
                for (var i = 0; i < depsObjects.Length; i++)
                    depsCommand.serializeObjects.Add(new SerializationInfo { serializationObject = depsObjects[i], serializationIndex = i + 1 });
                ContentBuildInterface.CalculateBuildUsageTags(depsObjects, depsObjects, globalUsage, usageSet);
                var depsMap = new BuildReferenceMap();
                depsMap.AddMappings(depsName, depsCommand.serializeObjects.ToArray());
                foreach (var d in defaults) depsMap.AddMapping(d.filePath, d.localIdentifierInFile, d);
                var depsResult = ContentBuildInterface.WriteSerializedFile(folder, new WriteParameters
                {
                    writeCommand = depsCommand, settings = settings, globalUsage = globalUsage,
                    usageSet = usageSet, referenceMap = depsMap,
                });
                resources.AddRange(depsResult.resourceFiles);

                // 2. The scene, referring to the dependencies' file by its serialization indices.
                var sceneCommand = new WriteCommand { internalName = sceneName, fileName = sceneName, serializeObjects = new List<SerializationInfo>() };
                var sceneMap = new BuildReferenceMap();
                sceneMap.AddMappings(depsName, depsCommand.serializeObjects.ToArray());
                foreach (var d in defaults) sceneMap.AddMapping(d.filePath, d.localIdentifierInFile, d);
                var sceneResult = ContentBuildInterface.WriteSceneSerializedFile(folder, new WriteSceneParameters
                {
                    scenePath = info.scene, writeCommand = sceneCommand, settings = settings,
                    globalUsage = globalUsage, usageSet = usageSet, referenceMap = sceneMap,
                });
                resources.AddRange(sceneResult.resourceFiles);

                // 3. The manifest. The loader passes the scene's dependencies in this exact order.
                string Slot(string filePath) => filePath == depsName ? "deps" : filePath.Contains("unity default resources") ? "global" : "?" + filePath;
                var sceneDeps = sceneResult.externalFileReferences.Select(r => Slot(r.filePath)).ToArray();
                var depsDeps = depsResult.externalFileReferences.Select(r => Slot(r.filePath)).ToArray();
                if (sceneDeps.Concat(depsDeps).FirstOrDefault(s => s.StartsWith("?")) is { } unknown)
                {
                    error($"The scene references a file the archive doesn't hold: {unknown.Substring(1)}");
                    return null;
                }
                var manifest = new StringBuilder()
                    .Append("sceneFile=").Append(sceneName).Append('\n')
                    .Append("sceneName=").Append(Path.GetFileNameWithoutExtension(scenePath)).Append('\n')
                    .Append("depsFile=").Append(depsName).Append('\n')
                    .Append("sceneDependencies=").Append(string.Join(",", sceneDeps)).Append('\n')
                    .Append("depsDependencies=").Append(string.Join(",", depsDeps)).Append('\n')
                    .Append("objects=").Append(depsObjects.Length).Append('\n')
                    .Append("end\n\n");   // the archive's VFS read drops the last byte: only padding is lost
                var manifestPath = Path.Combine(folder, ManifestFileName);
                File.WriteAllText(manifestPath, manifest.ToString());
                resources.Add(new ResourceFile { fileName = manifestPath, fileAlias = ManifestFileName });

                var missing = resources.Where(r => !File.Exists(r.fileName)).ToList();
                if (missing.Count > 0)
                {
                    error("Cannot archive; files not on disk: " + string.Join(", ", missing.Select(m => Path.GetFileName(m.fileName))));
                    return null;
                }
                var archive = Path.Combine(folder, name + ".ca");
                if (ContentBuildInterface.ArchiveAndCompress(resources.ToArray(), archive, UnityEngine.BuildCompression.LZ4) == 0)
                {
                    error("Failed to archive the scene");
                    return null;
                }
                return File.ReadAllBytes(archive);
            }
            catch (Exception e)
            {
                error($"Couldn't build the scene archive for {scenePath}: {e}");
                return null;
            }
            finally
            {
                try { cleanup?.Invoke(null, null); } catch { /* not registered */ }
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        static string Sanitize(string name) =>
            string.IsNullOrEmpty(name) ? "scene"
                : new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

        /// <summary>Produce the scene's archive in this editor (for testing without Project Service). Returns its path, or null.</summary>
        public static string Produce(string scenePath)
        {
            var guid = AssetDatabase.GUIDFromAssetPath(scenePath);
            if (guid.Empty()) return null;
            var id = AssetDatabaseExperimental.ProduceArtifact(new ArtifactKey(guid, typeof(PreviewSceneImporter)));
            if (!id.isValid || !AssetDatabaseExperimental.GetArtifactPaths(id, out var paths)) return null;
            return paths.FirstOrDefault(p => p.EndsWith("." + ArchiveArtifactName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Batch mode: -executeMethod Unity.Pipeline.SceneViewer.Editor.PreviewSceneImporter.ProduceFromCommandLine
        /// -scene Assets/….unity -out path/to/file.ca
        /// </summary>
        public static void ProduceFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var scene = Arg("-scene");
            var output = Arg("-out");
            var produced = scene != null ? Produce(scene) : null;
            if (produced == null)
            {
                Debug.LogError($"PreviewSceneImporter: no archive for {scene}");
                EditorApplication.Exit(1);
                return;
            }
            if (output != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                FileUtil.ReplaceFile(produced, output);
            }
            Debug.Log($"PreviewSceneImporter: {scene} -> {output ?? produced}");
        }
    }
}
