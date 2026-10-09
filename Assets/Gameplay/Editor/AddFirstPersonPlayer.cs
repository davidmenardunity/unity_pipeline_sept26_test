using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.Pipeline.Gameplay.Editor
{
    // Adds a "Player" (CharacterController + FirstPersonController) to a scene, standing on the ground under
    // the scene's main camera, and hands that camera to it at eye height. Running it again moves the
    // existing player instead of adding another.
    public static class AddFirstPersonPlayer
    {
        const float EyeHeight = 1.65f;
        const string DemoScene = "Assets/Toon Gas Station/Scenes/Demo_Scene_1.unity";

        [MenuItem("Tools/Gameplay/Add First-Person Player to Demo_Scene_1")]
        public static void AddToDemoScene()
        {
            var previous = EditorSceneManager.GetActiveScene().path;
            var scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            AddToActiveScene();
            EditorSceneManager.SaveScene(scene);
            if (!string.IsNullOrEmpty(previous) && previous != DemoScene) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Gameplay/Add First-Person Player to Open Scene")]
        public static void AddToActiveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var cameras = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Camera>(true)).ToList();
            var camera = cameras.FirstOrDefault(c => c.CompareTag("MainCamera")) ?? cameras.FirstOrDefault();
            if (camera == null)
            {
                camera = new GameObject("Main Camera").AddComponent<Camera>();
                camera.tag = "MainCamera";
            }

            var player = scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<FirstPersonController>(true)).FirstOrDefault(c => c != null)?.gameObject;
            if (player == null)
            {
                player = new GameObject("Player");
                var body = player.AddComponent<CharacterController>();
                body.height = 1.8f;
                body.radius = 0.35f;
                body.center = new Vector3(0f, 0.9f, 0f);
                body.stepOffset = 0.45f;
                body.slopeLimit = 50f;
                player.AddComponent<FirstPersonController>();
            }

            // Feet on whatever is under the camera (terrain, roads…); the camera's heading becomes the player's.
            var start = camera.transform.position;
            var ground = Physics.Raycast(start + Vector3.up, Vector3.down, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point : new Vector3(start.x, start.y - EyeHeight, start.z);
            player.transform.SetPositionAndRotation(ground + Vector3.up * 0.05f, Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f));

            camera.transform.SetParent(player.transform, false);
            camera.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
            camera.transform.localRotation = Quaternion.identity;
            camera.nearClipPlane = Mathf.Min(camera.nearClipPlane, 0.05f);

            var so = new SerializedObject(player.GetComponent<FirstPersonController>());
            so.FindProperty("m_Camera").objectReferenceValue = camera.transform;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Gameplay] Player at {player.transform.position} in {scene.path}, with camera '{camera.name}'");
        }
    }
}
