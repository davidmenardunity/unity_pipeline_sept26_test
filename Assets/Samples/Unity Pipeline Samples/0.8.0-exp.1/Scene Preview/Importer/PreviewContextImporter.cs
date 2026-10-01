using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Build.Content;
using UnityEditor.Build.Profile;
using UnityEditor.Experimental;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // On-demand importer for the preview BuildProfile — produces the GlobalUsage ("gus") artifact, the
    // shader-feature usage (fog, lightmap modes, …) of the profile's scenes, that PreviewContentImporter
    // strips preview content against. Not the profile's primary importer; triggered explicitly via
    // ProduceContextArtifact() / ArtifactKey(guid, typeof(PreviewContextImporter)).
    //
    // Deliberately NOT applied via SetImporterOverride: overriding the primary importer of the
    // preview BuildProfile breaks every out-of-process AssetImportWorker.
    // EditorUserBuildSettings::InitBuildProfilesInternal (native) resolves the active profile's path
    // via the persistent manager before scripting is even initialized; once that profile's primary
    // artifact is ours, the resolved path becomes a VirtualArtifacts cache path the native bootstrap
    // can't parse as a BuildProfile — every worker exits immediately with "Failed to read the
    // specified build profile", and any ProduceArtifact call hangs forever waiting on workers that
    // keep dying.
    [InitializeOnLoad]
    [ScriptedImporter(2, "___scenepreview_context___")]
    public class PreviewContextImporter : ScriptedImporter
    {
        // The preview build profile: the one the preview player is built from. Independent of whatever
        // profile is active in Build Settings; overridable per project via BuildProfilePath below.
        public const string DefaultBuildProfilePath = "Assets/Settings/Build Profiles/ScenePreview.asset";

        // Plain project file, NOT EditorUserSettings: importers run in out-of-process
        // AssetImportWorkers, which do not share the editor's EditorUserSettings state — reading it
        // from inside an import silently returned DefaultBuildProfilePath there, so
        // PreviewContentImporter looked up the context artifact of the WRONG BuildProfile whenever the
        // editor had overridden the path. A file under ProjectSettings/ is read identically by every
        // process sharing the project.
        const string BuildProfilePathSettingFile = "ProjectSettings/ScenePreviewBuildProfile.txt";

        const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";

        // Custom dependency for BuildProfilePath's current value. Anything that reads BuildProfilePath
        // during OnImportAsset (e.g. PreviewContentImporter, to find which profile's context artifact
        // to depend on) must call ctx.DependsOnCustomDependency(BuildProfilePathDependencyKey) so
        // switching the target profile — with no asset actually changing — still invalidates their
        // cached artifacts.
        public const string BuildProfilePathDependencyKey = "Unity.Pipeline.Samples.ScenePreview/PreviewContextBuildProfilePath";

        /// <summary>
        /// Path of the BuildProfile the preview pipeline targets. Persisted per-project via a
        /// ProjectSettings file so import worker processes read the same value as the main editor
        /// (see BuildProfilePathSettingFile).
        /// </summary>
        public static string BuildProfilePath
        {
            get
            {
                if (File.Exists(BuildProfilePathSettingFile))
                {
                    var path = File.ReadAllText(BuildProfilePathSettingFile).Trim();
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                return DefaultBuildProfilePath;
            }
            set
            {
                File.WriteAllText(BuildProfilePathSettingFile, value ?? string.Empty);
                RegisterBuildProfilePathDependency();
            }
        }

        static PreviewContextImporter()
        {
            // Register on domain reload too, so the hash matches BuildProfilePath's current value even
            // if nothing called the setter this session.
            RegisterBuildProfilePathDependency();
        }

        static void RegisterBuildProfilePathDependency()
        {
            AssetDatabase.RegisterCustomDependency(BuildProfilePathDependencyKey, Hash128.Compute(BuildProfilePath));
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(PreviewImporterDependency.DependencyKey);
            ctx.DependsOnSourceAsset(GraphicsSettingsPath);

            // Load the BuildProfile from the source file directly (can't use AssetDatabase during import).
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(ctx.assetPath);
            var profile = objects.OfType<BuildProfile>().FirstOrDefault();
            if (profile == null)
            {
                ctx.LogImportError($"BuildProfile not found in: {ctx.assetPath}");
                return;
            }

            ctx.DependsOnArtifact(ctx.assetPath);

            var scenes = profile.GetScenesForBuild();

            // Reset to a fresh, empty scene before reading the baseline GlobalUsage.
            // GetGlobalUsageFromGraphicsSettings() reads Auto-mode settings (e.g. fog) from whatever
            // scene is CURRENTLY loaded — in an out-of-process import worker, that can be leftover
            // state from an unrelated, earlier import job the same worker process handled. Since
            // globalUsage is only ever OR'd into (monotonic — see the loop below), a leaked "true"
            // from a prior job would never clear on its own.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Make the in-process GraphicsSettings match the file this importer depends on before
            // reading any GlobalUsage from it — see SyncGraphicsSettingsFromFile.
            SyncGraphicsSettingsFromFile(ctx);

            var globalUsage = ContentBuildInterface.GetGlobalUsageFromGraphicsSettings();

            foreach (var sceneEntry in scenes)
            {
                if (!sceneEntry.enabled)
                    continue;

                var scenePath = sceneEntry.path;
                if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
                {
                    Debug.LogWarning($"PreviewContextImporter: Scene not found: {scenePath}");
                    continue;
                }

                // A .unity scene has no ScriptedImporter/artifact of its own (it's loaded directly by
                // SceneManager, not imported into a Library artifact), so DependsOnArtifact would be a
                // no-op here; DependsOnSourceAsset depends on the file's own content hash, which is
                // what actually reflects edits.
                if (!sceneEntry.guid.Empty())
                    ctx.DependsOnSourceAsset(sceneEntry.guid);

                var scene = EditorSceneManager.OpenScene(scenePath);
                globalUsage |= ContentBuildInterface.GetGlobalUsageFromGraphicsSettings();
                EditorSceneManager.CloseScene(scene, true);
            }

            File.WriteAllBytes(ctx.GetOutputArtifactFilePath("gus"), SerializeGlobalUsage(globalUsage));
        }

        // ContentBuildInterface.GetGlobalUsageFromGraphicsSettings() reads the editor's IN-PROCESS
        // GraphicsSettings singleton, and an AssetImportWorker loads that singleton exactly once, at
        // its own process startup: workers reload serialized singletons only while handling an
        // Initialize or Prepare message, and Prepare is re-sent only when its blob hash changes — that
        // blob carries import parameters, assemblies and worker mode flags, nothing derived from
        // GraphicsSettings. So a Shader Stripping change never reaches an already-running worker; the
        // importer reruns (DependsOnSourceAsset fires) but recomputes a byte-identical "gus" from
        // stale settings, and PreviewContentImporter legitimately reuses its cached content artifact —
        // preview built with the wrong shader variant stripping.
        //
        // Fixing the input rather than the computation: copy the file onto the singleton and let the
        // engine compute GlobalUsage exactly as before. CopySerialized is just "make the singleton
        // match the file", the same intent as the engine's own ReloadFromDisk on these singletons.
        static void SyncGraphicsSettingsFromFile(AssetImportContext ctx)
        {
            var liveSettings = Unsupported.GetSerializedAssetInterfaceSingleton("GraphicsSettings");
            if (liveSettings == null)
            {
                ctx.LogImportError("Could not get the GraphicsSettings singleton");
                return;
            }

            // Workers only. In the main editor the live singleton IS the freshest state, and copying
            // the file over it would silently revert the user's unsaved Project Settings edits. Unsaved
            // settings still matter though: they make a main-process import and a worker import compute
            // different GlobalUsage for the same artifact key, so say so rather than hiding it.
            if (!AssetDatabase.IsAssetImportWorkerProcess())
            {
                if (EditorUtility.IsDirty(liveSettings))
                    ctx.LogImportWarning(
                        $"{GraphicsSettingsPath} has unsaved changes. GlobalUsage is being computed from " +
                        "the in-memory settings, which import workers cannot see — save Project Settings " +
                        "for a reproducible preview build.");
                return;
            }

            var fileObjects = InternalEditorUtility.LoadSerializedFileAndForget(GraphicsSettingsPath);
            var fileSettings = fileObjects?.FirstOrDefault(o => o != null);
            if (fileSettings == null)
            {
                ctx.LogImportError($"Could not load GraphicsSettings from {GraphicsSettingsPath}");
                return;
            }

            try
            {
                EditorUtility.CopySerialized(fileSettings, liveSettings);
            }
            finally
            {
                // LoadSerializedFileAndForget hands back objects nobody owns; a worker process handles
                // many imports before it exits, so dropping them would leak one GraphicsSettings per
                // import.
                UnityEngine.Object.DestroyImmediate(fileSettings, true);
            }
        }

        static unsafe byte[] SerializeGlobalUsage(BuildUsageTagGlobal globalUsage)
        {
            int size = sizeof(BuildUsageTagGlobal);
            byte[] bytes = new byte[size];
            fixed (byte* ptr = bytes)
            {
                *(BuildUsageTagGlobal*)ptr = globalUsage;
            }
            return bytes;
        }

        /// <summary>
        /// Explicitly produce the context artifact (GlobalUsage) for a BuildProfile.
        /// Returns the ArtifactID if successful.
        /// </summary>
        public static ArtifactID ProduceContextArtifact(string buildProfilePath = null)
        {
            if (string.IsNullOrEmpty(buildProfilePath))
                buildProfilePath = BuildProfilePath;

            var guid = AssetDatabase.GUIDFromAssetPath(buildProfilePath);
            if (guid.Empty())
            {
                Debug.LogError($"PreviewContextImporter: BuildProfile not found: {buildProfilePath}");
                return default;
            }

            var artifactId = AssetDatabaseExperimental.ProduceArtifact(new ArtifactKey(guid, typeof(PreviewContextImporter)));
            if (!artifactId.isValid)
            {
                Debug.LogError($"PreviewContextImporter: Failed to produce artifact for {buildProfilePath}");
                return default;
            }

            return artifactId;
        }

        /// <summary>
        /// Get the artifact paths for a context artifact previously produced via ProduceContextArtifact.
        /// Returns null if the id is invalid.
        /// </summary>
        public static string[] GetContextArtifactPaths(ArtifactID artifactId)
        {
            if (!artifactId.isValid)
                return null;

            if (!AssetDatabaseExperimental.GetArtifactPaths(artifactId, out var paths))
                return null;

            return paths;
        }
    }
}
