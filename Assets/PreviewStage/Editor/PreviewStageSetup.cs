using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Pipeline.PreviewStage.Editor
{
    // Builds the set around the Scene Preview's pedestal: a procedural sky, a warm sun, a round plaza,
    // and a ring of trees, street props and buildings from the project's own packs, kept clear of the
    // camera's side. Running it again replaces the stage it made before.
    //
    // The preview's content archives are compiled for this scene's shader features (PreviewContextImporter
    // reads them from the scene), so this keeps them as they were: linear fog, realtime lighting, no
    // lightmaps, no extra lights.
    public static class PreviewStageSetup
    {
        const string ScenePath = "Assets/Samples/Unity Pipeline Samples/0.8.0-exp.1/Scene Preview/VillageCorner_preview.unity";
        const string SkyPath = "Assets/PreviewStage/PreviewSky.mat";
        const string StageName = "Stage";
        const string Toon = "Assets/Toon Gas Station/Prefabs/";

        // The orbit camera stays 9–24 m from the centre and spins all the way round, so anything tall sits
        // outside that (26 m and more) and only low props (barrels, rocks, a sign, a car) stand near the plaza.
        // Angle in degrees from +Z, clockwise seen from above; distance from the centre to the prop's near edge.
        static readonly (string Prefab, float Angle, float Near, Facing Facing, float Scale)[] Layout =
        {
            // Near the plaza: low enough to look over
            (Toon + "Barrel_1F.prefab", 28f, 4.8f, Facing.Random, 1f),
            (Toon + "Barrel_1G.prefab", 35f, 5.1f, Facing.Random, 1f),
            (Toon + "Barrel_3B.prefab", -24f, 4.9f, Facing.Random, 1f),
            (Toon + "Rock_Cluster_1A.prefab", 100f, 5.4f, Facing.Random, 1f),
            (Toon + "Rock_Cluster_1C.prefab", -95f, 5.6f, Facing.Random, 1f),
            (Toon + "Streetsign_3D.prefab", 60f, 5.8f, Facing.Centre, 1f),
            (Toon + "Car_14I.prefab", -140f, 6.5f, Facing.Tangent, 1f),
            (Toon + "Rock_Cluster_1B.prefab", 165f, 6f, Facing.Random, 0.8f),
            // The street: outside the camera's orbit
            (Toon + "Streetlight_1C.prefab", 140f, 26f, Facing.Centre, 1f),
            (Toon + "Streetlight_1C.prefab", -150f, 26.5f, Facing.Centre, 1f),
            (Toon + "Gas_Tank_1A.prefab", -170f, 29f, Facing.Centre, 1f),
            (Toon + "Bilboard_1E.prefab", 75f, 30f, Facing.Centre, 1f),
            // Trees all round
            (Toon + "Tree_7A.prefab", -70f, 26f, Facing.Random, 1.2f),
            (Toon + "Tree_7C.prefab", -105f, 28f, Facing.Random, 1.3f),
            (Toon + "Tree_7E.prefab", 105f, 27f, Facing.Random, 1.2f),
            (Toon + "Tree_7G.prefab", 125f, 31f, Facing.Random, 1.35f),
            (Toon + "Tree_7B.prefab", -125f, 30f, Facing.Random, 1.15f),
            (Toon + "Tree_7D.prefab", 18f, 27f, Facing.Random, 1.2f),
            (Toon + "Tree_7F.prefab", -18f, 28f, Facing.Random, 1.25f),
            (Toon + "Tree_7A.prefab", 165f, 27f, Facing.Random, 1.1f),
            (Toon + "Tree_7E.prefab", -165f, 33f, Facing.Random, 1.3f),
            // Backdrop
            (Toon + "Building_7B.prefab", 0f, 36f, Facing.Centre, 1f),
            (Toon + "Building_17E.prefab", -40f, 38f, Facing.Centre, 1f),
            (Toon + "Building_7C.prefab", 42f, 37f, Facing.Centre, 1f),
            (Toon + "Building_7B.prefab", 180f, 40f, Facing.Centre, 1f),
        };

        enum Facing { Centre, Tangent, Random }

        [MenuItem("Tools/Scene Preview/Build Preview Stage")]
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == StageName))
                Object.DestroyImmediate(old);

            var rnd = new System.Random(7);
            var stage = new GameObject(StageName);
            stage.transform.SetSiblingIndex(0);

            Sky();
            Sun(scene);
            Ground(scene);

            // The plaza the preview stands on: four quarter pieces (each a square with one rounded corner),
            // square corners meeting at the centre, top at y = 0 (where PreviewLoader rests previews).
            var plaza = new GameObject("Plaza").transform;
            plaza.SetParent(stage.transform, false);
            for (var q = 0; q < 4; q++)
            {
                var piece = Place(Toon + "Pavement_Rounded_2E_8X8.prefab", plaza);
                if (piece == null) break;
                piece.transform.localScale *= 1.4f;
                piece.transform.rotation = Quaternion.Euler(0f, q * 90f, 0f);
                var b = Bounds(piece);
                var square = SquareCorner(piece, b);
                piece.transform.position += new Vector3(-square.x, -b.max.y + 0.002f, -square.z);
            }

            var props = new List<PreviewStage.Prop>();
            var group = new GameObject("Props").transform;
            group.SetParent(stage.transform, false);
            foreach (var (path, angle, near, facing, scale) in Layout)
            {
                var go = Place(path, group);
                if (go == null) continue;
                var dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                go.transform.localScale *= scale;
                go.transform.rotation = facing switch
                {
                    Facing.Centre => Quaternion.LookRotation(-dir, Vector3.up),
                    Facing.Tangent => Quaternion.LookRotation(Vector3.Cross(Vector3.up, dir), Vector3.up),
                    _ => Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f),
                };
                // Rest it on the ground with its near edge `near` from the centre.
                var b = Bounds(go);
                var reach = Mathf.Max(b.extents.x, b.extents.z);
                var target = dir * (near + reach);
                go.transform.position += new Vector3(target.x - b.center.x, -b.min.y, target.z - b.center.z);
                props.Add(new PreviewStage.Prop { Transform = go.transform, Home = go.transform.position });
            }

            var component = stage.AddComponent<PreviewStage>();
            component.SetProps(props, 4.4f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[PreviewStage] Built the stage: {props.Count} props, sky {SkyPath}.");
        }

        public static void BuildFromCommandLine()
        {
            Build();
            AssetDatabase.SaveAssets();
        }

        static GameObject Place(string path, Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[PreviewStage] Missing prefab: {path}");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.localPosition = Vector3.zero;
            // Props are scenery: no colliders for the annotation pins to catch, no shadows lost to the
            // far ones (the camera never gets close).
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        // The corner of the piece's footprint its mesh reaches (the other three... one of them is rounded off):
        // of the four, the one opposite the corner farthest from every vertex.
        static Vector3 SquareCorner(GameObject piece, Bounds b)
        {
            var corners = new[] { new Vector3(b.min.x, 0, b.min.z), new Vector3(b.max.x, 0, b.min.z), new Vector3(b.max.x, 0, b.max.z), new Vector3(b.min.x, 0, b.max.z) };
            var gap = new float[4];
            for (var i = 0; i < 4; i++) gap[i] = float.MaxValue;
            foreach (var f in piece.GetComponentsInChildren<MeshFilter>())
            {
                if (f.sharedMesh == null) continue;
                foreach (var v in f.sharedMesh.vertices)
                {
                    var w = f.transform.TransformPoint(v);
                    w.y = 0;
                    for (var i = 0; i < 4; i++) gap[i] = Mathf.Min(gap[i], (w - corners[i]).sqrMagnitude);
                }
            }
            var rounded = System.Array.IndexOf(gap, gap.Max());
            return corners[(rounded + 2) % 4];
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }

        // A procedural sky with a soft sun disc; fog and ambient follow its colours so the horizon blends.
        static void Sky()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(mat, SkyPath);
            }
            mat.SetFloat("_SunDisk", 2f);   // high quality
            mat.SetFloat("_SunSize", 0.035f);
            mat.SetFloat("_SunSizeConvergence", 6f);
            mat.SetFloat("_AtmosphereThickness", 0.85f);
            mat.SetColor("_SkyTint", new Color(0.46f, 0.55f, 0.66f));
            mat.SetColor("_GroundColor", new Color(0.42f, 0.44f, 0.42f));
            mat.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(mat);

            RenderSettings.skybox = mat;
            // Trilight, not Skybox: a skybox ambient needs a lighting bake to store its probe.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.66f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.52f, 0.55f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.27f, 0.25f, 0.22f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;   // as before: the archives' shader variants expect it
            RenderSettings.fogColor = new Color(0.74f, 0.82f, 0.9f);
            RenderSettings.fogStartDistance = 40f;
            RenderSettings.fogEndDistance = 190f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        }

        static void Sun(UnityEngine.SceneManagement.Scene scene)
        {
            var sun = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Light>(true))
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null) return;
            // Late afternoon from behind the left shoulder: long shadows toward the camera's right.
            sun.transform.rotation = Quaternion.Euler(38f, -40f, 0f);
            sun.color = new Color(1f, 0.9f, 0.76f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            RenderSettings.sun = sun;
        }

        // The island grows to hold the ring; the water stays below it.
        static void Ground(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "Island") root.transform.localScale = new Vector3(13f, 1f, 13f);
                if (root.name == "Water")
                {
                    root.transform.position = new Vector3(0f, -0.25f, 0f);
                    root.transform.localScale = new Vector3(40f, 1f, 40f);
                }
            }
        }
    }
}
