using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Samples.ScenePreview.Importer;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unity.Pipeline.AnimationViewer.Editor
{
    // The animation viewer's runtime: a scene (camera, light, ground, the humanoid character, the AnimationViewer)
    // and a WebGL build profile with only that scene. Pipeline Explorer's Animations tab embeds the build from
    // Builds/AnimationViewer_WebGL. Also packs models generated for a clip into WebGL content archives (PackModel),
    // which Pipeline Explorer calls through `unity command run_script`.
    public static class AnimationViewerSetup
    {
        const string Root = "Assets/AnimationViewer";
        const string ScenePath = Root + "/AnimationViewer.unity";
        const string ProfilePath = "Assets/Settings/Build Profiles/AnimationViewer_WebGL.asset";
        const string TemplateProfile = "Assets/Settings/Build Profiles/ScenePreview_WebGL.asset";
        const string BuildPath = "Builds/AnimationViewer_WebGL";
        // The character the scene uses: a Humanoid model (Avatar) — the generated one when there is one.
        public const string MannequinPath = Root + "/Character/Mannequin.prefab";
        public const string GeneratedCharacterPath = Root + "/Character/Character.prefab";

        [MenuItem("Tools/Animation Viewer/Create Scene and Build Profile")]
        public static void CreateAll()
        {
            Directory.CreateDirectory(Root + "/Materials");
            AssetDatabase.Refresh();
            var ground = Lit("Ground", new Color(0.55f, 0.58f, 0.6f));
            var box = Lit("Box", new Color(0.95f, 0.62f, 0.25f));
            var character = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedCharacterPath);
            if (character == null)
                character = AssetDatabase.LoadAssetAtPath<GameObject>(MannequinPath);
            if (character == null)
                character = CreateMannequin();

            // Made additively and closed once saved: the scenes the user has open stay as they are.
            var previous = EditorSceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene);

            // Archives are built with the Scene Preview scene's shader variants (linear fog among them).
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
            sun.transform.rotation = Quaternion.Euler(50f, 150f, 0f);

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.farClipPlane = 2000f;
            cam.nearClipPlane = 0.05f;
            cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var rig = cam.gameObject.AddComponent<AnimationViewerCamera>();

            var groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            groundGo.name = "Ground";
            Object.DestroyImmediate(groundGo.GetComponent<Collider>());
            groundGo.transform.localScale = new Vector3(10f, 1f, 10f);
            groundGo.transform.position = new Vector3(0f, -0.01f, 0f);
            groundGo.GetComponent<Renderer>().sharedMaterial = ground;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(character, scene);
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var viewer = new GameObject("AnimationViewer").AddComponent<AnimationViewer>();
            var so = new SerializedObject(viewer);
            so.FindProperty("m_Character").objectReferenceValue = instance.GetComponentInChildren<Animator>();
            so.FindProperty("m_CameraRig").objectReferenceValue = rig;
            so.FindProperty("m_Box").objectReferenceValue = box;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            if (previous.IsValid() && previous != scene)
            {
                EditorSceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
            CreateProfile();
            AssetDatabase.SaveAssets();
            Debug.Log($"[AnimationViewerSetup] Created {ScenePath} (character {AssetDatabase.GetAssetPath(character)}) and {ProfilePath}");
        }

        /// <summary>Build the AnimationViewer_WebGL profile to Builds/AnimationViewer_WebGL.</summary>
        public static string Build()
        {
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
            {
                buildProfile = profile, locationPathName = BuildPath, options = BuildOptions.None,
            });
            return $"{report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize / 1048576f:0.0} MB, {report.summary.totalTime}";
        }

        static void CreateProfile()
        {
            if (AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath) == null && !AssetDatabase.CopyAsset(TemplateProfile, ProfilePath))
            {
                Debug.LogError($"[AnimationViewerSetup] Couldn't copy {TemplateProfile} to {ProfilePath}");
                return;
            }
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            profile.overrideGlobalScenes = true;
            profile.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorUtility.SetDirty(profile);
        }

        // ── the mannequin: a humanoid made of capsules, with an Avatar ───────

        // Bone, parent, local position (T-pose, facing +Z; the character's left is -X).
        static readonly (string bone, string parent, Vector3 at)[] Bones =
        {
            ("Hips", null, new Vector3(0f, 0.95f, 0f)),
            ("Spine", "Hips", new Vector3(0f, 0.1f, 0f)),
            ("Chest", "Spine", new Vector3(0f, 0.15f, 0f)),
            ("Neck", "Chest", new Vector3(0f, 0.25f, 0f)),
            ("Head", "Neck", new Vector3(0f, 0.08f, 0f)),
            ("LeftShoulder", "Chest", new Vector3(-0.05f, 0.2f, 0f)),
            ("LeftUpperArm", "LeftShoulder", new Vector3(-0.13f, 0f, 0f)),
            ("LeftLowerArm", "LeftUpperArm", new Vector3(-0.27f, 0f, 0f)),
            ("LeftHand", "LeftLowerArm", new Vector3(-0.25f, 0f, 0f)),
            ("RightShoulder", "Chest", new Vector3(0.05f, 0.2f, 0f)),
            ("RightUpperArm", "RightShoulder", new Vector3(0.13f, 0f, 0f)),
            ("RightLowerArm", "RightUpperArm", new Vector3(0.27f, 0f, 0f)),
            ("RightHand", "RightLowerArm", new Vector3(0.25f, 0f, 0f)),
            ("LeftUpperLeg", "Hips", new Vector3(-0.1f, -0.05f, 0f)),
            ("LeftLowerLeg", "LeftUpperLeg", new Vector3(0f, -0.42f, 0f)),
            ("LeftFoot", "LeftLowerLeg", new Vector3(0f, -0.42f, 0f)),
            ("LeftToes", "LeftFoot", new Vector3(0f, -0.05f, 0.12f)),
            ("RightUpperLeg", "Hips", new Vector3(0.1f, -0.05f, 0f)),
            ("RightLowerLeg", "RightUpperLeg", new Vector3(0f, -0.42f, 0f)),
            ("RightFoot", "RightLowerLeg", new Vector3(0f, -0.42f, 0f)),
            ("RightToes", "RightFoot", new Vector3(0f, -0.05f, 0.12f)),
        };

        /// <summary>A plain humanoid (capsule segments on a skeleton) with a Humanoid Avatar, saved as a prefab.</summary>
        [MenuItem("Tools/Animation Viewer/Create Mannequin")]
        public static GameObject CreateMannequin()
        {
            Directory.CreateDirectory(Root + "/Character");
            AssetDatabase.Refresh();
            var body = Lit("Mannequin", new Color(0.82f, 0.84f, 0.88f));
            var joint = Lit("MannequinJoint", new Color(0.25f, 0.6f, 0.95f));

            var root = new GameObject("Mannequin");
            var bones = new Dictionary<string, Transform>();
            foreach (var (bone, parent, at) in Bones)
            {
                var t = new GameObject(bone).transform;
                t.SetParent(parent == null ? root.transform : bones[parent], false);
                t.localPosition = at;
                bones[bone] = t;
            }
            // A capsule from each bone's parent to it (the limbs and the spine), joints, a head, hands and feet.
            foreach (var (bone, parent, at) in Bones)
            {
                if (parent == null || bone.EndsWith("Shoulder") || bone.EndsWith("UpperLeg"))
                    continue;
                var radius = bone.Contains("Leg") || bone.EndsWith("Foot") ? 0.065f : bone is "Spine" or "Chest" or "Neck" ? 0.11f : 0.045f;
                Segment(bones[parent], at, radius, body);
            }
            foreach (var side in new[] { "Left", "Right" })
            {
                Shape(PrimitiveType.Cube, bones[side + "Foot"], new Vector3(0f, -0.04f, 0.05f), new Vector3(0.09f, 0.07f, 0.24f), body);
                Shape(PrimitiveType.Cube, bones[side + "Hand"], new Vector3(side == "Left" ? -0.08f : 0.08f, 0f, 0f), new Vector3(0.16f, 0.04f, 0.09f), body);
                foreach (var j in new[] { "UpperArm", "LowerArm", "UpperLeg", "LowerLeg" })
                    Shape(PrimitiveType.Sphere, bones[side + j], Vector3.zero, Vector3.one * 0.1f, joint);
            }
            Shape(PrimitiveType.Capsule, bones["Hips"], new Vector3(0f, 0.02f, 0f), new Vector3(0.3f, 0.1f, 0.18f), body);
            Shape(PrimitiveType.Sphere, bones["Head"], new Vector3(0f, 0.12f, 0.01f), new Vector3(0.22f, 0.26f, 0.24f), body);
            Shape(PrimitiveType.Cube, bones["Head"], new Vector3(0f, 0.14f, 0.11f), new Vector3(0.14f, 0.04f, 0.04f), joint);   // which way it faces

            var description = new HumanDescription
            {
                human = Bones.Select(b => new HumanBone { boneName = b.bone, humanName = b.bone, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone
                    { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            if (!avatar.isValid || !avatar.isHuman)
            {
                Object.DestroyImmediate(root);
                throw new System.InvalidOperationException("[AnimationViewerSetup] The mannequin's Avatar isn't a valid humanoid");
            }
            avatar.name = "MannequinAvatar";
            var avatarPath = Root + "/Character/MannequinAvatar.asset";
            AssetDatabase.DeleteAsset(avatarPath);
            AssetDatabase.CreateAsset(avatar, avatarPath);
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, MannequinPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static Material Lit(string name, Color color)
        {
            var path = $"{Root}/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(Root + "/Materials");
                AssetDatabase.Refresh();
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.3f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void Segment(Transform from, Vector3 to, float radius, Material mat)
        {
            var length = to.magnitude;
            var c = Shape(PrimitiveType.Capsule, from, to * 0.5f, new Vector3(radius * 2f, length * 0.5f + radius, radius * 2f), mat);
            c.transform.localRotation = Quaternion.FromToRotation(Vector3.up, to.normalized);
        }

        static GameObject Shape(PrimitiveType type, Transform parent, Vector3 at, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = $"{parent.name}_{type}";
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        // ── a character generated with Unity AI ──────────────────────────────

        /// <summary>Import a rigged character model as Humanoid (Avatar from the model), save it as the scene's
        /// character and recreate the scene. Returns what happened.</summary>
        public static string UseCharacter(string modelPath)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            importer.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman)
                return $"{modelPath} didn't map to a humanoid Avatar: keeping the current character";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(model);
            if (!root.TryGetComponent<Animator>(out var animator))
                animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            PrefabUtility.SaveAsPrefabAsset(root, GeneratedCharacterPath);
            Object.DestroyImmediate(root);
            CreateAll();
            return $"{GeneratedCharacterPath} is the character now";
        }

        // ── models generated for a clip ──────────────────────────────────────

        /// <summary>
        /// Import a generated model, save a prefab of it about 1 m big, standing on its origin (the player puts a
        /// copy on each transform the clip animates), and build that prefab's WebGL content archive to archivePath.
        /// </summary>
        public static string PackModel(string modelPath, string archivePath)
        {
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null)
                return $"error: {modelPath} didn't import as a model";
            var root = new GameObject(Path.GetFileNameWithoutExtension(modelPath));
            var inner = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inner.transform.SetParent(root.transform, false);
            var renderers = inner.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                var scale = 1f / Mathf.Max(0.001f, Mathf.Max(b.size.x, b.size.y, b.size.z));
                inner.transform.localScale = Vector3.one * scale;
                inner.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z) * scale;
            }
            var prefabPath = Path.ChangeExtension(modelPath, ".prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            return PreviewProducer.ProduceArchiveToFile(prefabPath, archivePath)
                ? $"packed {prefabPath} → {archivePath} ({new FileInfo(archivePath).Length} bytes)"
                : $"error: couldn't build the archive for {prefabPath}";
        }
    }
}
