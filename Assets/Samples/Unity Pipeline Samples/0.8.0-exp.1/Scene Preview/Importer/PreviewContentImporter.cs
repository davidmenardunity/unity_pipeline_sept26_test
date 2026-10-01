using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Build.Content;
using UnityEditor.Experimental;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // Builds a single self-contained content archive (.ca) for one asset and emits it as the "ca"
    // artifact. A non-primary ScriptedImporter: nothing imports with it by default; Project Service runs
    // it on demand by type name (T:{guid}+{typeName}). The archive is serialized for the build target
    // the import runs under — Project Service starts its editor on the requesting environment's
    // platform — and its shader variants are stripped against the preview scene named by the preview
    // build profile (PreviewContextImporter.BuildProfilePath).
    //
    // This is a starting point: copy it and change BuildContentArchive to produce whatever your preview
    // player needs (other object filters, extra payloads in the manifest, a different archive layout).
    [ScriptedImporter(8, "___scenepreview_content___")]
    public class PreviewContentImporter : ScriptedImporter
    {
        public const string ArchiveArtifactName = "ca";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(PreviewImporterDependency.DependencyKey);
            ctx.DependsOnSourceAsset(PreviewLinkXml.AssetPath);
            // BuildProfilePath is read below to decide which profile's context artifact this content
            // depends on — if the configured path changes, this artifact must be recomputed even
            // though no asset touched by the old/new path necessarily changed.
            ctx.DependsOnCustomDependency(PreviewContextImporter.BuildProfilePathDependencyKey);

            var assetPath = ctx.assetPath;
            var assetGuid = AssetDatabase.GUIDFromAssetPath(assetPath);
            if (assetGuid.Empty())
            {
                ctx.LogImportError($"Asset not found: {assetPath}");
                return;
            }
            ctx.DependsOnArtifact(assetGuid);

            // Reading selectedBuildTarget also makes the artifact depend on it, so archives for
            // different platforms never share a cache entry.
            var buildTarget = ctx.selectedBuildTarget;

            var profilePath = PreviewContextImporter.BuildProfilePath;
            var profileGuid = AssetDatabase.GUIDFromAssetPath(profilePath);
            if (profileGuid.Empty())
            {
                ctx.LogImportError($"Preview build profile not found: {profilePath}");
                return;
            }

            var globalUsage = LoadGlobalUsageFromArtifact(ctx, profileGuid);

            byte[] archiveBytes = BuildContentArchive(ctx, assetPath, assetGuid, buildTarget, globalUsage);
            if (archiveBytes == null)
                return;

            File.WriteAllBytes(ctx.GetOutputArtifactFilePath(ArchiveArtifactName), archiveBytes);
            Debug.Log($"PreviewContentImporter: wrote {buildTarget} content archive ({archiveBytes.Length} bytes) for {assetPath}");
        }

        unsafe BuildUsageTagGlobal LoadGlobalUsageFromArtifact(AssetImportContext ctx, GUID profileGuid)
        {
            // PreviewContextImporter is non-primary, so target it by explicit importer type — NOT the
            // profile's primary artifact (new ArtifactKey(profileGuid) would resolve the BuildProfile's
            // own importer, not our gus). ctx.GetArtifactFilePath is a passive lookup that both
            // registers the dependency (so this content re-runs when the gus changes, e.g. graphics-
            // settings shader stripping) and returns "" if the artifact isn't available yet; the
            // context artifact is produced upfront in the main process (see ProduceContentArtifact),
            // and a forcing ProduceArtifact call from inside a worker import cannot trigger imports.
            var artifactKey = new ArtifactKey(profileGuid, typeof(PreviewContextImporter));
            ctx.DependsOnArtifact(artifactKey);

            var gusPath = ctx.GetArtifactFilePath(artifactKey, "gus");
            if (string.IsNullOrEmpty(gusPath))
                return default;

            var bytes = ReadVfsFile(gusPath);
            if (bytes == null || bytes.Length != sizeof(BuildUsageTagGlobal))
                return default;

            fixed (byte* ptr = bytes)
            {
                return *(BuildUsageTagGlobal*)ptr;
            }
        }

        static byte[] ReadVfsFile(string vfsPath)
        {
            var tempPath = Path.Combine("Temp", Path.GetRandomFileName());
            try
            {
                FileUtil.CopyFileOrDirectory(vfsPath, tempPath);
                return File.ReadAllBytes(tempPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to read VFS file '{vfsPath}': {e.Message}");
                return null;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        // Serializes the asset into a single self-contained content file (+ optional .resS) and packs
        // it with a small manifest into a .ca via ArchiveAndCompress. Returns the .ca bytes.
        byte[] BuildContentArchive(AssetImportContext ctx, string assetPath, GUID assetGuid,
            BuildTarget target, BuildUsageTagGlobal globalUsage)
        {
            // Register the engine's shader/compute-shader build callbacks so shader variant stripping
            // runs during WriteSerializedFile (272 = ShaderProcessors | ComputeShader). Pairs with
            // PreviewContextImporter's GraphicsSettings sync — without it, stripping is a no-op.
            var buildPipelineType = Type.GetType("UnityEditor.Build.BuildPipelineInterfaces, UnityEditor");
            var initMethod = buildPipelineType.GetMethod("InitializeBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
            var cleanMethod = buildPipelineType.GetMethod("CleanupBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                initMethod.Invoke(null, new object[] { 272 });
            }
            catch (TargetInvocationException e) when (e.GetBaseException() is UnityException)
            {
                // URP 17.5+ (Unity 6.5) saves its URP assets while creating its shader-processor callback
                // (UpdateShaderPrefilteringDataBeforeBuild calls AssetDatabase.SaveAssetIfDirty), which
                // Unity refuses during an import. Build without URP's variant stripping instead of failing.
                // A console warning, not ctx.LogImportWarning: import workers report any import message as
                // "had errors", which a caller could read as a failed import.
                Debug.LogWarning($"PreviewContentImporter: shader variant stripping is off for {ctx.assetPath}: a build " +
                    $"callback can't run during import ({e.GetBaseException().Message})");
            }

            try
            {
                var includedObjects = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(assetGuid, target);
                if (includedObjects.Length == 0)
                {
                    ctx.LogImportError($"No objects found in asset: {assetPath}");
                    return null;
                }

                var dependencies = ContentBuildInterface.GetPlayerDependenciesForObjects(includedObjects, target, null);

                // "unity default resources" ship with the player and resolve at runtime via
                // ContentFile.GlobalTableDependency: keep them out of the serialized set but map them
                // so references resolve, and record in the manifest that the loader must supply the slot.
                var filteredDeps = dependencies
                    .Where(obj => !obj.filePath.Contains("unity default resources"))
                    .ToArray();
                var defaultDeps = dependencies
                    .Where(obj => obj.filePath.Contains("unity default resources"))
                    .ToArray();

                // Depend on every distinct asset actually referenced (materials, textures, meshes,
                // etc.) — ctx.DependsOnArtifact(assetGuid) alone does NOT transitively cover this: a
                // referenced asset's own content changing doesn't reimport the asset that references
                // it, since native GUID references resolve at load time rather than being baked into
                // the referencing asset's own serialized bytes.
                foreach (var dependencyGuid in filteredDeps.Select(obj => obj.guid).Distinct())
                {
                    if (!dependencyGuid.Empty() && dependencyGuid != assetGuid)
                        ctx.DependsOnArtifact(dependencyGuid);
                }

                var allObjects = new List<ObjectIdentifier>();
                allObjects.AddRange(includedObjects);
                allObjects.AddRange(filteredDeps);

                // Filter objects by the types/assemblies allowed in link.xml, so the content file never
                // references a type stripped out of the preview player.
                var (allowedTypes, allowedAssemblies) = PreviewLinkXml.ParseAllowList();
                if (allowedTypes.Count > 0 || allowedAssemblies.Count > 0)
                {
                    allObjects = allObjects
                        .Where(obj =>
                        {
                            var type = ContentBuildInterface.GetTypeForObject(obj);
                            if (type == null)
                                return false;
                            return allowedTypes.Contains(type.FullName)
                                || allowedAssemblies.Contains(type.Assembly.GetName().Name);
                        })
                        .ToList();
                }

                // Per-asset content-file name, carried in the manifest so the runtime loader can mount
                // and load it without knowing the asset up front (it downloads a .ca by GUID).
                // internalName and fileName MUST match: WriteSerializedFile names the .resS from
                // fileName but lists it in writeResult.resourceFiles under internalName, so a mismatch
                // makes the archived .resS alias differ from the name the content file references.
                var internalName = SanitizeName(Path.GetFileNameWithoutExtension(assetPath));
                var command = new WriteCommand
                {
                    internalName = internalName,
                    fileName = internalName,
                    serializeObjects = new List<SerializationInfo>()
                };

                // PathID 0 is reserved for null references; serialization indices start at 1.
                for (int i = 0; i < allObjects.Count; i++)
                {
                    command.serializeObjects.Add(new SerializationInfo
                    {
                        serializationObject = allObjects[i],
                        serializationIndex = i + 1
                    });
                }

                var settings = new BuildSettings
                {
                    target = target,
                    group = BuildPipeline.GetBuildTargetGroup(target),
                    typeDB = null
                };

                var objectIds = allObjects.ToArray();
                var usageSet = new BuildUsageTagSet();
                ContentBuildInterface.CalculateBuildUsageTags(objectIds, objectIds, globalUsage, usageSet);

                var referenceMap = new BuildReferenceMap();
                referenceMap.AddMappings(internalName, command.serializeObjects.ToArray());
                foreach (var dependency in defaultDeps)
                    referenceMap.AddMapping(dependency.filePath, dependency.localIdentifierInFile, dependency);

                var tempOutputFolder = Path.Combine("Temp", "PreviewContent_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempOutputFolder);

                try
                {
                    var parameters = new WriteParameters
                    {
                        writeCommand = command,
                        settings = settings,
                        globalUsage = globalUsage,
                        usageSet = usageSet,
                        referenceMap = referenceMap,
                        bundleInfo = null
                    };

                    var writeResult = ContentBuildInterface.WriteSerializedFile(tempOutputFolder, parameters);

                    var manifestPath = ContentArchiveManifestBuilder.WriteManifest(tempOutputFolder, defaultDeps.Length > 0, internalName);
                    var resourceFiles = writeResult.resourceFiles.ToList();
                    resourceFiles.Add(ContentArchiveManifestBuilder.CreateManifestResourceFile(manifestPath));

                    // Every listed resource file must exist on disk or ArchiveAndCompress fails with a
                    // cryptic "could not be opened"; usually a symptom of internalName != fileName above.
                    var missing = resourceFiles.Where(rf => !File.Exists(rf.fileName)).ToList();
                    if (missing.Count > 0)
                    {
                        ctx.LogImportError("Cannot archive; resource files not on disk: " +
                            string.Join(", ", missing.Select(m => Path.GetFileName(m.fileName))));
                        return null;
                    }

                    var archivePath = Path.Combine(tempOutputFolder, internalName + ".ca");
                    var crc = ContentBuildInterface.ArchiveAndCompress(resourceFiles.ToArray(), archivePath, UnityEngine.BuildCompression.LZ4);
                    if (crc == 0)
                    {
                        ctx.LogImportError("Failed to archive content file");
                        return null;
                    }

                    return File.ReadAllBytes(archivePath);
                }
                finally
                {
                    if (Directory.Exists(tempOutputFolder))
                        Directory.Delete(tempOutputFolder, true);
                }
            }
            finally
            {
                cleanMethod.Invoke(null, null);
            }
        }

        /// <summary>
        /// Explicitly produce the content-archive artifact for an asset. Returns the ArtifactID.
        /// </summary>
        public static ArtifactID ProduceContentArtifact(string assetPath)
        {
            var guid = AssetDatabase.GUIDFromAssetPath(assetPath);
            if (guid.Empty())
            {
                Debug.LogError($"PreviewContentImporter: Asset not found: {assetPath}");
                return default;
            }

            // Produce the context artifact upfront, in the main process: the content import reads the
            // context "gus" via a passive lookup and a forcing ProduceArtifact call from inside a
            // worker import cannot trigger imports, so it must already exist by the time this runs.
            var contextArtifactId = PreviewContextImporter.ProduceContextArtifact();
            if (!contextArtifactId.isValid)
            {
                Debug.LogError($"PreviewContentImporter: Failed to produce context artifact for the preview build profile {PreviewContextImporter.BuildProfilePath}");
                return default;
            }

            var artifactId = AssetDatabaseExperimental.ProduceArtifact(new ArtifactKey(guid, typeof(PreviewContentImporter)));
            if (!artifactId.isValid)
            {
                Debug.LogError($"PreviewContentImporter: Failed to produce artifact for {assetPath}");
                return default;
            }

            return artifactId;
        }

        /// <summary>
        /// Path to the produced .ca artifact for an asset, or null if it has not been produced.
        /// </summary>
        public static string GetContentArchivePath(string assetPath)
        {
            var guid = AssetDatabase.GUIDFromAssetPath(assetPath);
            if (guid.Empty())
                return null;

            var artifactId = AssetDatabaseExperimental.LookupArtifact(new ArtifactKey(guid, typeof(PreviewContentImporter)));
            if (!artifactId.isValid)
                return null;

            if (!AssetDatabaseExperimental.GetArtifactPaths(artifactId, out var paths))
                return null;

            // Only return the archive artifact. If it is absent (the import failed to write it),
            // return null so the caller fails cleanly rather than shipping the importer's default
            // serialized artifact as if it were a content archive.
            return paths.FirstOrDefault(p => p.EndsWith("." + ArchiveArtifactName, System.StringComparison.OrdinalIgnoreCase));
        }

        // Content-file names live on the archive VFS and are referenced by alias, so keep them to a
        // conservative, lower-case, path-safe set derived deterministically from the asset name.
        static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "previewcontent";
            var chars = name.ToLowerInvariant()
                .Select(c => (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_')
                .ToArray();
            return new string(chars);
        }
    }
}
