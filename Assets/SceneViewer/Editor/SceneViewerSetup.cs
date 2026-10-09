using System.IO;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unity.Pipeline.SceneViewer.Editor
{
    // Creates the Scene Viewer's own player: an empty scene (a fallback camera and the SceneViewer that
    // loads scene archives) and the SceneViewer_WebGL build profile that builds only it, to
    // Builds/SceneViewer_WebGL. Its own profile, so changing it never rebuilds the other viewers.
    public static class SceneViewerSetup
    {
        const string Root = "Assets/SceneViewer";
        const string ScenePath = Root + "/SceneViewer.unity";
        const string ProfileFolder = "Assets/Settings/Build Profiles";
        const string ProfilePath = ProfileFolder + "/SceneViewer_WebGL.asset";
        const string TemplateProfile = ProfileFolder + "/LevelEditor_WebGL.asset";

        [MenuItem("Tools/Scene Viewer/Create Scene and Build Profile")]
        public static void CreateAll()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Until a scene loads (and makes itself the active scene): a plain dark view.
            RenderSettings.fog = false;
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.4f, 0.42f, 0.45f);

            var cam = new GameObject("Fallback Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.114f, 0.129f, 0.161f);
            cam.farClipPlane = 3000f;
            cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            cam.gameObject.AddComponent<AudioListener>();

            var viewer = new GameObject("SceneViewer").AddComponent<SceneViewer>();
            var so = new SerializedObject(viewer);
            so.FindProperty("m_FallbackCamera").objectReferenceValue = cam;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            CreateProfile();
            AssetDatabase.SaveAssets();
            Debug.Log($"[SceneViewerSetup] Created {ScenePath} and {ProfilePath}");
        }

        public static void CreateAllFromCommandLine() => CreateAll();

        static void CreateProfile()
        {
            if (AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath) == null && !AssetDatabase.CopyAsset(TemplateProfile, ProfilePath))
            {
                Debug.LogError($"[SceneViewerSetup] Couldn't copy {TemplateProfile} to {ProfilePath}");
                return;
            }
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            profile.overrideGlobalScenes = true;
            profile.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            UncompressedWebGL();
        }

        // The app serves the players locally: Brotli-compressing a build (minutes on a big .wasm) buys nothing
        // there. A project-wide player setting, so it applies to every WebGL profile.
        [MenuItem("Tools/Scene Viewer/Turn Off WebGL Compression")]
        public static void UncompressedWebGL()
        {
            if (PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Disabled) return;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneViewerSetup] WebGL builds are no longer compressed");
        }

        [MenuItem("Tools/Scene Viewer/Build Archive for Selected Scene")]
        static void ArchiveSelected()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var archive = PreviewSceneImporter.Produce(path);
            Debug.Log(archive != null ? $"[SceneViewerSetup] {path}: {new FileInfo(archive).Length:N0} bytes at {archive}" : $"[SceneViewerSetup] No archive for {path}");
        }

        [MenuItem("Tools/Scene Viewer/Build Archive for Selected Scene", true)]
        static bool CanArchiveSelected() => AssetDatabase.GetAssetPath(Selection.activeObject).EndsWith(".unity");
    }
}
