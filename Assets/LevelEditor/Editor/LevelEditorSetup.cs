using System.IO;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unity.Pipeline.LevelEditor.Editor
{
    // Creates the level editor's runtime: an empty scene (camera, light, ground grid, the LevelEditor rig)
    // and a WebGL build profile with only that scene. Pipeline Explorer embeds the build from
    // Builds/LevelEditor_WebGL. Run from the menu, or in batch mode:
    //   Unity -batchmode -projectPath . -executeMethod Unity.Pipeline.LevelEditor.Editor.LevelEditorSetup.CreateAll -quit
    public static class LevelEditorSetup
    {
        const string Root = "Assets/LevelEditor";
        const string ScenePath = Root + "/LevelEditor.unity";
        const string ProfilePath = "Assets/Settings/Build Profiles/LevelEditor_WebGL.asset";
        // The Scene Preview WebGL profile: a release build with the same player settings.
        const string TemplateProfile = "Assets/Settings/Build Profiles/ScenePreview_WebGL.asset";
        const int GizmoLayer = 31;

        [MenuItem("Tools/Level Editor/Create Scene and Build Profile")]
        public static void CreateAll()
        {
            Directory.CreateDirectory(Root + "/Materials");
            AssetDatabase.Refresh();

            var grid = GridTexture();
            var ground = Lit("Ground", new Color(0.62f, 0.66f, 0.64f), grid, 200f);
            var placeholder = Lit("Placeholder", new Color(0.55f, 0.58f, 0.6f), null, 1f);
            var axisX = Unlit("AxisX", new Color(0.93f, 0.26f, 0.21f));
            var axisY = Unlit("AxisY", new Color(0.35f, 0.8f, 0.3f));
            var axisZ = Unlit("AxisZ", new Color(0.24f, 0.5f, 0.95f));
            var center = Unlit("AxisCenter", new Color(0.98f, 0.82f, 0.25f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Archives are built with the shader variants the Scene Preview scene needs (linear fog among
            // them), so this scene uses linear fog too, pushed far away.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 400f;
            RenderSettings.fogEndDistance = 2000f;
            RenderSettings.fogColor = new Color(0.75f, 0.82f, 0.88f);
            RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            RenderSettings.ambientMode = AmbientMode.Skybox;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.cullingMask = ~(1 << GizmoLayer);
            cam.farClipPlane = 5000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var camData = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camData.renderType = CameraRenderType.Base;
            var rig = cam.gameObject.AddComponent<LevelEditorCamera>();

            // The move widget draws on top: an overlay camera that only sees its layer.
            var overlay = new GameObject("Widget Camera").AddComponent<Camera>();
            overlay.transform.SetParent(cam.transform, false);
            overlay.cullingMask = 1 << GizmoLayer;
            overlay.farClipPlane = 5000f;
            var overlayData = overlay.gameObject.AddComponent<UniversalAdditionalCameraData>();
            overlayData.renderType = CameraRenderType.Overlay;
            overlayData.renderShadows = false;
            camData.cameraStack.Add(overlay);

            var groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            groundGo.name = "Ground";
            Object.DestroyImmediate(groundGo.GetComponent<Collider>());
            groundGo.transform.localScale = new Vector3(100f, 1f, 100f);   // 1 km, the grid at 1 m
            groundGo.transform.position = new Vector3(0f, -0.01f, 0f);       // just under y = 0, no z-fighting
            groundGo.GetComponent<Renderer>().sharedMaterial = ground;

            var editor = new GameObject("LevelEditor").AddComponent<LevelEditor>();
            var so = new SerializedObject(editor);
            so.FindProperty("m_Camera").objectReferenceValue = cam;
            so.FindProperty("m_CameraRig").objectReferenceValue = rig;
            so.FindProperty("m_AxisX").objectReferenceValue = axisX;
            so.FindProperty("m_AxisY").objectReferenceValue = axisY;
            so.FindProperty("m_AxisZ").objectReferenceValue = axisZ;
            so.FindProperty("m_AxisCenter").objectReferenceValue = center;
            so.FindProperty("m_Placeholder").objectReferenceValue = placeholder;
            so.FindProperty("m_GizmoLayer").intValue = GizmoLayer;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            CreateProfile();
            AssetDatabase.SaveAssets();
            Debug.Log($"[LevelEditorSetup] Created {ScenePath} and {ProfilePath}");
        }

        static void CreateProfile()
        {
            if (AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath) == null && !AssetDatabase.CopyAsset(TemplateProfile, ProfilePath))
            {
                Debug.LogError($"[LevelEditorSetup] Couldn't copy {TemplateProfile} to {ProfilePath}");
                return;
            }
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            profile.overrideGlobalScenes = true;
            profile.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorUtility.SetDirty(profile);
        }

        static Texture2D GridTexture()
        {
            const string path = Root + "/Materials/Grid.png";
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var line = x < 3 || y < 3;
                var c = line ? new Color(0.45f, 0.49f, 0.5f) : new Color(0.8f, 0.83f, 0.82f);
                tex.SetPixel(x, y, c);
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material Lit(string name, Color color, Texture2D map, float tiling)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", color);
            if (map != null)
            {
                mat.SetTexture("_BaseMap", map);
                mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            }
            mat.SetFloat("_Smoothness", 0.1f);
            return Save(mat, name);
        }

        static Material Unlit(string name, Color color)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetColor("_BaseColor", color);
            return Save(mat, name);
        }

        static Material Save(Material mat, string name)
        {
            var path = $"{Root}/Materials/{name}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) is { } existing)
            {
                existing.CopyPropertiesFromMaterial(mat);
                existing.shader = mat.shader;
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
