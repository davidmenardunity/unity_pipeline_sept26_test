using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // Creates the preview build profile: a Build Profile whose only scene is the preview scene, for the
    // active platform. Build the preview player from it; PreviewContextImporter also reads its scene
    // list to strip preview content for that scene. Open the preview scene and use the Tools menu, or
    // run in batchmode with -executeMethod and -previewScene <path>.
    // BuildProfile.CreateInstance(BuildTarget, subtarget) is internal, hence the reflection.
    public static class PreviewBuildProfileSetup
    {
        [MenuItem("Tools/Scene Preview/Create Preview Build Profile")]
        public static void CreateBuildProfile()
        {
            var profilePath = PreviewContextImporter.BuildProfilePath;
            if (AssetDatabase.LoadAssetAtPath<BuildProfile>(profilePath) != null)
            {
                Debug.Log($"[PreviewBuildProfileSetup] Preview build profile already exists: {profilePath}");
                Finish();
                return;
            }

            var scenePath = PreviewProducer.GetArg("-previewScene") ?? SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                Debug.LogError("[PreviewBuildProfileSetup] Open (and save) the preview scene first, or pass -previewScene <path>.");
                Finish(1);
                return;
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            var subtarget = BuildPipeline.GetBuildTargetGroup(target) == BuildTargetGroup.Standalone
                ? StandaloneBuildSubtarget.Player
                : StandaloneBuildSubtarget.Default;

            var create = typeof(BuildProfile).GetMethod(
                "CreateInstance",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) },
                null);
            if (create == null)
            {
                Debug.LogError("[PreviewBuildProfileSetup] BuildProfile.CreateInstance(BuildTarget, StandaloneBuildSubtarget) not found.");
                Finish(1);
                return;
            }

            BuildProfile profile;
            try
            {
                profile = (BuildProfile)create.Invoke(null, new object[] { target, subtarget });
            }
            catch (Exception e)
            {
                Debug.LogError($"[PreviewBuildProfileSetup] Failed to create a {target} build profile (module installed?): {e.InnerException?.Message ?? e.Message}");
                Finish(1);
                return;
            }

            profile.overrideGlobalScenes = true;
            profile.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };

            Directory.CreateDirectory(Path.GetDirectoryName(profilePath));
            AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(profile, profilePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PreviewBuildProfileSetup] Created {target} preview build profile for '{scenePath}': {profilePath}");
            Finish();
        }

        static void Finish(int code = 0)
        {
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
        }
    }
}
