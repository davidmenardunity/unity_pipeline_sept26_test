using System.Linq;
using Unity.Pipeline.PreviewStage.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.Pipeline.PreviewAnnotations.Editor
{
    // Adds the "Annotations" object (PreviewAnnotations) to the Scene Preview scene, with the materials its
    // pins and strokes use, after building the stage around the pedestal.
    public static class PreviewAnnotationsSetup
    {
        const string Folder = "Assets/PreviewAnnotations";

        [MenuItem("Tools/Scene Preview/Build Preview Stage and Annotations")]
        public static void Build()
        {
            PreviewStageSetup.Build();   // opens and saves the preview scene
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == "Annotations"))
                Object.DestroyImmediate(old);

            var go = new GameObject("Annotations");
            var component = go.AddComponent<PreviewAnnotations>();
            var so = new SerializedObject(component);
            so.FindProperty("m_Camera").objectReferenceValue = Camera.main;
            so.FindProperty("m_PinMaterial").objectReferenceValue =
                Material($"{Folder}/AnnotationPin.mat", "Universal Render Pipeline/Unlit", m => m.SetColor("_BaseColor", new Color(1f, 0.78f, 0.2f)));
            // Particles/Unlit takes the line's vertex colours, so one material serves every stroke colour.
            so.FindProperty("m_LineMaterial").objectReferenceValue =
                Material($"{Folder}/AnnotationStroke.mat", "Universal Render Pipeline/Particles/Unlit", m => m.SetColor("_BaseColor", Color.white));
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[PreviewAnnotations] Added the Annotations object to the preview scene.");
        }

        public static void BuildFromCommandLine() => Build();

        static Material Material(string path, string shader, System.Action<Material> setup)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(m, path);
            }
            setup(m);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
