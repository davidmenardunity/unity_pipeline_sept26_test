using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.Pipeline.Gameplay.Editor
{
    // Turns the squirrel exported from Blender (Squirrel.fbx: rigid parts on a rig, five actions) into a prefab
    // and puts a crowd of them in Demo_Scene_1:
    //   - the FBX imports with legacy animation, clips renamed Idle / Look / Alert / Run / Jump;
    //   - its Blender materials map to URP Lit materials in Squirrel/Materials;
    //   - Squirrel.prefab: the model (turned to face +Z if needed) under a root with the Squirrel behaviour;
    //   - a "Squirrels" SquirrelSpawner in the scene, around the first-person player.
    public static class SquirrelSetup
    {
        const string Folder = "Assets/Gameplay/Squirrel";
        const string ModelPath = Folder + "/Squirrel.fbx";
        const string PrefabPath = Folder + "/Squirrel.prefab";
        const string DemoScene = "Assets/Toon Gas Station/Scenes/Demo_Scene_1.unity";
        static readonly string[] Clips = { "Idle", "Look", "Alert", "Run", "Jump" };

        static readonly (string Name, Color Color, float Smoothness)[] Materials =
        {
            ("Squirrel_Fur", new Color(0.55f, 0.26f, 0.09f), 0.15f),
            ("Squirrel_FurDark", new Color(0.36f, 0.16f, 0.05f), 0.1f),
            ("Squirrel_Belly", new Color(0.93f, 0.80f, 0.62f), 0.15f),
            ("Squirrel_Eye", new Color(0.02f, 0.02f, 0.02f), 0.8f),
        };

        [MenuItem("Tools/Gameplay/Set Up Squirrels in Demo_Scene_1")]
        public static void SetUp()
        {
            ConfigureModel();
            var prefab = CreatePrefab();
            AddToScene(prefab);
        }

        public static void ConfigureModel()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            importer.animationType = ModelImporterAnimationType.Legacy;
            importer.importAnimation = true;
            importer.globalScale = 1f;
            importer.SaveAndReimport();   // so the default clips (the FBX takes) are known

            // Takes come in as "Squirrel|Squirrel_Run": keep the five, named for the behaviour.
            importer.clipAnimations = importer.defaultClipAnimations
                .Select(c =>
                {
                    var name = Clips.FirstOrDefault(n => c.takeName.EndsWith("_" + n) || c.takeName.EndsWith(n));
                    if (name == null) return null;
                    c.name = name;
                    c.wrapMode = name == "Jump" ? WrapMode.ClampForever : WrapMode.Loop;
                    c.loopTime = name != "Jump";
                    return c;
                })
                .Where(c => c != null).ToArray();

            Directory.CreateDirectory(Folder + "/Materials");
            foreach (var (name, color, smoothness) in Materials)
            {
                var path = $"{Folder}/Materials/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Smoothness", smoothness);
                EditorUtility.SetDirty(mat);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), mat);
            }
            importer.SaveAndReimport();
            AssetDatabase.SaveAssets();
        }

        public static GameObject CreatePrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var root = new GameObject("Squirrel");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            instance.name = "Model";
            // Face +Z, the way Squirrel moves: the head must be in front of the root.
            var head = instance.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Head");
            if (head != null && head.position.z < 0f) instance.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            foreach (var r in instance.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            var animation = instance.GetComponent<Animation>() ?? instance.AddComponent<Animation>();
            animation.playAutomatically = false;
            animation.cullingType = AnimationCullingType.BasedOnRenderers;   // a crowd: don't animate the ones off screen
            root.AddComponent<Squirrel>();
            var turned = head != null && head.position.z < 0f;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            var clips = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).Select(c => c.name);
            Debug.Log($"[Gameplay] {PrefabPath}: model {(turned ? "turned 180°" : "as exported")}, clips {string.Join(", ", clips)}");
            return prefab;
        }

        static void AddToScene(GameObject prefab)
        {
            var previous = EditorSceneManager.GetActiveScene().path;
            var scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            var spawner = scene.GetRootGameObjects().Select(g => g.GetComponent<SquirrelSpawner>()).FirstOrDefault(s => s != null);
            if (spawner == null) spawner = new GameObject("Squirrels").AddComponent<SquirrelSpawner>();
            var player = scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<FirstPersonController>()).FirstOrDefault(p => p != null);
            spawner.transform.position = player != null ? player.transform.position : Vector3.zero;
            var so = new SerializedObject(spawner);
            so.FindProperty("m_Prefab").objectReferenceValue = prefab;
            so.FindProperty("m_Count").intValue = 120;
            so.FindProperty("m_Radius").floatValue = 45f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            if (!string.IsNullOrEmpty(previous) && previous != DemoScene) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
            Debug.Log($"[Gameplay] {DemoScene}: 120 squirrels around {spawner.transform.position}");
        }
    }
}
