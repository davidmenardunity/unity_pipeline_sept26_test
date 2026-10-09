using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Content;
using Unity.IO.Archive;
using Unity.IO.LowLevel.Unsafe;
using Unity.Loading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Unity.Pipeline.SceneViewer
{
    // Loads a whole scene from a content archive built by PreviewSceneImporter, and lets you play it:
    // download the .ca, mount it, load the scene's dependencies, load the scene additively and make it
    // active (its lighting, fog and skybox apply), then put a first-person ScenePlayer at its camera.
    //
    // The page that embeds the player sends SendMessage("SceneViewer", "LoadArchiveFromUrl", url) and
    // "Unload"; reports go back with SceneViewer_Send ({ state, … }).
    public class SceneViewer : MonoBehaviour
    {
        [Tooltip("Used when the loaded scene has no camera of its own.")]
        [SerializeField] Camera m_FallbackCamera;

        class Loaded
        {
            public string Path;
            public ArchiveHandle Archive;
            public bool HasArchive;
            public ContentFile Deps;
            public bool HasDeps;
            public ContentSceneFile Scene;
            public bool HasScene;
            public ScenePlayer Player;
            public Camera DisabledFallback;
            public bool OwnPlayer;
        }

        bool m_Captured;

        // The scene's own player captures the mouse itself: tell the page when it does.
        void Update()
        {
            if (m_Current == null || !m_Current.OwnPlayer) return;
            var captured = Cursor.lockState == CursorLockMode.Locked;
            if (captured == m_Captured) return;
            m_Captured = captured;
            Send(new Report { state = captured ? "captured" : "released" });
        }

        Loaded m_Current;
        int m_Seq;
        Coroutine m_Routine;

        void Start() => Send(new Report { state = "ready" });

        public void LoadArchiveFromUrl(string url)
        {
            if (m_Routine != null) StopCoroutine(m_Routine);
            m_Routine = StartCoroutine(Load(url));
        }

        public void Unload()
        {
            if (m_Routine != null) StopCoroutine(m_Routine);
            m_Routine = StartCoroutine(UnloadCurrent());
        }

        IEnumerator Load(string url)
        {
            yield return UnloadCurrent();
            var seq = ++m_Seq;
            var item = new Loaded { Path = System.IO.Path.Combine(Application.persistentDataPath, $"scene_{seq}.ca") };
            m_Current = item;

            // 1. Download.
            Send(new Report { state = "downloading" });
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerFile(item.Path) { removeFileOnAbort = true };
                // Pipeline Explorer's /api answers only requests carrying its header (the page usually
                // downloads the archive itself and passes a blob URL; this covers a direct URL).
                if (url.Contains("/api/")) request.SetRequestHeader("X-Pipeline-Explorer", "1");
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    Send(new Report { state = "downloading", progress = request.downloadProgress });
                    yield return null;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail($"couldn't download the scene archive: {request.error}");
                    yield break;
                }
            }

            // 2. Mount, with a prefix never used before (a reused one never finishes loading).
            Send(new Report { state = "loading", detail = "mounting the archive" });
            item.Archive = ArchiveFileInterface.MountAsync(ContentNamespace.Default, item.Path, $"scene{seq}:");
            item.HasArchive = true;
            item.Archive.JobHandle.Complete();
            if (item.Archive.Status != ArchiveStatus.Complete)
            {
                Fail($"couldn't mount the archive (status {item.Archive.Status})");
                yield break;
            }
            var mount = item.Archive.GetMountPath();
            var manifest = ReadManifest(mount + "scene_manifest.txt");
            if (manifest == null || !manifest.ContainsKey("sceneFile"))
            {
                Fail("the archive has no scene manifest: is it a scene archive (PreviewSceneImporter)?");
                yield break;
            }

            // 3. The objects the scene uses.
            Send(new Report { state = "loading", detail = $"loading {manifest.GetValueOrDefault("objects", "the")} objects the scene uses" });
            using (var deps = Dependencies(manifest.GetValueOrDefault("depsDependencies", ""), default))
                item.Deps = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, mount + manifest["depsFile"], deps);
            item.HasDeps = true;
            while (item.Deps.LoadingStatus == LoadingStatus.InProgress) yield return null;
            if (item.Deps.LoadingStatus != LoadingStatus.Completed)
            {
                Fail("the scene's objects failed to load");
                yield break;
            }

            // 4. The scene, additively, then made active so its lighting, fog and skybox apply.
            Send(new Report { state = "loading", detail = "loading the scene" });
            var parameters = new ContentSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.None, autoIntegrate = true };
            using (var deps = Dependencies(manifest.GetValueOrDefault("sceneDependencies", ""), item.Deps))
                item.Scene = ContentLoadInterface.LoadSceneAsync(ContentNamespace.Default, mount + manifest["sceneFile"],
                    manifest.GetValueOrDefault("sceneName", "Scene"), parameters, deps);
            item.HasScene = true;
            while (item.Scene.Status != SceneLoadingStatus.Complete && item.Scene.Status != SceneLoadingStatus.Failed) yield return null;
            if (item.Scene.Status == SceneLoadingStatus.Failed)
            {
                Fail("the scene failed to load");
                yield break;
            }
            var scene = item.Scene.Scene;
            SceneManager.SetActiveScene(scene);
            DynamicGI.UpdateEnvironment();

            // 5. A camera to play from: the scene's own, else ours framing the scene.
            var cameras = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Camera>()).Where(c => c.enabled).ToList();
            var camera = cameras.OrderByDescending(c => c.CompareTag("MainCamera")).ThenByDescending(c => c.depth).FirstOrDefault();
            if (camera != null && m_FallbackCamera != null)
            {
                m_FallbackCamera.gameObject.SetActive(false);
                item.DisabledFallback = m_FallbackCamera;
            }
            else if (camera == null)
            {
                camera = m_FallbackCamera;
                Frame(camera, scene);
            }
            foreach (var other in cameras.Where(c => c != camera)) other.enabled = false;
            // A scene with its own player (a CharacterController: a first-person controller…) plays as it is;
            // others get ours.
            item.OwnPlayer = scene.GetRootGameObjects().Any(g => g.GetComponentInChildren<CharacterController>() != null);
            if (!item.OwnPlayer) item.Player = ScenePlayer.Attach(camera, Report2Page);

            Send(new Report
            {
                state = "loaded",
                scene = scene.name,
                objects = scene.GetRootGameObjects().Sum(g => g.GetComponentsInChildren<Transform>(true).Length),
                camera = cameras.Count > 0 ? camera.name : null,
                player = item.OwnPlayer ? "scene" : "viewer",
            });
            m_Routine = null;
        }

        static NativeArray<ContentFile> Dependencies(string list, ContentFile deps)
        {
            var slots = list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var array = new NativeArray<ContentFile>(slots.Length, Allocator.Temp);
            for (var i = 0; i < slots.Length; i++)
                array[i] = slots[i] == "global" ? ContentFile.GlobalTableDependency : deps;
            return array;
        }

        // Put the fallback camera where it sees the whole scene.
        static void Frame(Camera camera, Scene scene)
        {
            var renderers = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>()).ToList();
            if (renderers.Count == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            var dir = new Vector3(0.6f, 0.45f, -0.65f).normalized;
            camera.transform.position = bounds.center + dir * Mathf.Max(5f, bounds.extents.magnitude);
            camera.transform.LookAt(bounds.center);
        }

        IEnumerator UnloadCurrent()
        {
            var item = m_Current;
            m_Current = null;
            if (item == null) yield break;
            if (item.Player != null) item.Player.Detach();
            // The fallback camera played in the loaded scene: bring it home before that scene goes.
            if (m_FallbackCamera != null && m_FallbackCamera.gameObject.scene != gameObject.scene)
                SceneManager.MoveGameObjectToScene(m_FallbackCamera.gameObject, gameObject.scene);
            if (item.DisabledFallback != null) item.DisabledFallback.gameObject.SetActive(true);
            // The scene, then the content file it uses, then the archive behind both.
            if (item.HasScene && item.Scene.IsValid)
            {
                if (item.Scene.Status == SceneLoadingStatus.Complete && item.Scene.UnloadAtEndOfFrame())
                    yield return new WaitForEndOfFrame();
                yield return null;
            }
            if (item.HasDeps) item.Deps.UnloadAsync().WaitForCompletion(10000);
            if (item.HasArchive) item.Archive.Unmount().Complete();
            try { File.Delete(item.Path); } catch { /* unique names */ }
            Send(new Report { state = "unloaded" });
        }

        void Fail(string message)
        {
            Debug.LogError($"[SceneViewer] {message}");
            Send(new Report { state = "error", detail = message });
            m_Routine = null;
        }

        void Report2Page(string state) => Send(new Report { state = state });

        // key=value lines (PreviewSceneImporter), read through the archive's virtual file system.
        static unsafe Dictionary<string, string> ReadManifest(string vfsPath)
        {
            FileInfoResult info;
            var infoHandle = AsyncReadManager.GetFileInfo(vfsPath, &info);
            infoHandle.JobHandle.Complete();
            infoHandle.Dispose();
            if (info.FileState != FileState.Exists || info.FileSize <= 0) return null;
            var buffer = new NativeArray<byte>((int)info.FileSize, Allocator.Temp);
            try
            {
                var cmd = new ReadCommand { Buffer = NativeArrayUnsafeUtility.GetUnsafePtr(buffer), Offset = 0, Size = info.FileSize };
                var read = AsyncReadManager.Read(vfsPath, &cmd, 1);
                read.JobHandle.Complete();
                var status = read.Status;
                read.Dispose();
                if (status != ReadStatus.Complete) return null;
                var result = new Dictionary<string, string>();
                foreach (var line in System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Split('\n'))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0) result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                return result;
            }
            finally { buffer.Dispose(); }
        }

        [Serializable]
        class Report
        {
            public string state;
            public string detail;
            public float progress = -1f;
            public string scene;
            public int objects;
            public string camera;
            public string player;   // "scene": the scene's own player; "viewer": ours (F flies)
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void SceneViewer_Send(string json);
#else
        static void SceneViewer_Send(string json) => Debug.Log($"[SceneViewer] → page: {json}");
#endif

        static void Send(Report report) => SceneViewer_Send(JsonUtility.ToJson(report));
    }
}
