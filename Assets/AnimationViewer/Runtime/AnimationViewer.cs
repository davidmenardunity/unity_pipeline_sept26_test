using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Content;
using Unity.IO.Archive;
using Unity.Loading;
using Unity.Pipeline.Samples.ScenePreview;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Networking;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Unity.Pipeline.AnimationViewer
{
    // The runtime half of Pipeline Explorer's Animations tab. The page has Pipeline build a content archive
    // (.ca) for a clip's asset (an .anim, or the .fbx that imports it) and calls Show: this player downloads and
    // mounts it, finds the clip by name and plays it.
    //  - mode "model": on a model from the project (its archive, modelUrl) that the page found compatible: its
    //    Avatar retargets a Humanoid clip; a generic one binds by path, its top transform renamed when the page
    //    says so (modelRoot → clipRoot: another animal's rig with the same bones).
    //  - Humanoid clips (or mode "character") play on the scene's humanoid character: its Avatar retargets them.
    //  - mode "own": the clip plays on the model its .fbx imports (the archive has both).
    //  - mode "rig": a generic clip plays on a rig built from the transform paths it animates (same names and
    //    hierarchy), with a box on each, or a copy of a generated model when the page passes one (modelUrl).
    // Legacy clips play with an Animation component, the others with a PlayableGraph on an Animator.
    public class AnimationViewer : MonoBehaviour
    {
        [SerializeField] Animator m_Character;
        [SerializeField] AnimationViewerCamera m_CameraRig;
        [SerializeField] Material m_Box;

        [Serializable]
        class Message
        {
            public string key;
            public string url;
            public string clip;
            public string mode;
            public string[] paths;
            public string modelKey;
            public string modelUrl;
            public string modelRoot;   // the model's top transform, renamed to clipRoot so the clip binds
            public string clipRoot;
            public float speed = 1f;
        }

        [Serializable]
        class Report
        {
            public string type;
            public string clip;
            public string target;
            public string error;
            public float length;
            public bool humanoid;
            public bool legacy;
            public bool loop;
            public int version;
        }

        // What this player understands; the page offers the model picker from version 2 (modes "model", "character").
        const int Version = 2;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void AnimationViewer_Send(string json);
#else
        static void AnimationViewer_Send(string json) => Debug.Log($"[AnimationViewer] → {json}");
#endif

        static void Send(Report r) => AnimationViewer_Send(JsonUtility.ToJson(r));

        // ── archives ─────────────────────────────────────────────────────────

        class Archive
        {
            public string Error;
            public bool Loading = true;
            public Object[] Objects = Array.Empty<Object>();
        }

        readonly Dictionary<string, Archive> m_Archives = new();
        int m_Seq;

        // Download, mount and load one archive (once per key); each mount gets a fresh prefix.
        IEnumerator Load(string key, string url)
        {
            if (m_Archives.TryGetValue(key, out var a))
            {
                while (a.Loading) yield return null;
                yield break;
            }
            a = m_Archives[key] = new Archive();
            m_Seq++;
            var path = Path.Combine(Application.persistentDataPath, $"anim_{m_Seq}.ca");
            using (var req = UnityWebRequest.Get(url))
            {
                // Pipeline Explorer only answers its own pages; this header is how they identify themselves.
                req.SetRequestHeader("X-Pipeline-Explorer", "1");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail(a, $"download failed: {req.responseCode} {req.error}");
                    yield break;
                }
                File.WriteAllBytes(path, req.downloadHandler.data);
            }

            var handle = ArchiveFileInterface.MountAsync(ContentNamespace.Default, path, $"anim{m_Seq}:");
            handle.JobHandle.Complete();
            if (handle.Status != ArchiveStatus.Complete)
            {
                Fail(a, $"couldn't mount the archive (status {handle.Status})");
                yield break;
            }
            var mountPath = handle.GetMountPath();
            var manifest = ContentArchiveManifest.ReadManifest(mountPath);
            var contentFile = string.IsNullOrEmpty(manifest.contentFileName) ? PreviewLoader.ContentFileName : manifest.contentFileName;
            var deps = new NativeArray<ContentFile>(manifest.requiresDefaultResources ? 1 : 0, Allocator.Temp);
            if (manifest.requiresDefaultResources)
                deps[0] = ContentFile.GlobalTableDependency;
            var file = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, Path.Combine(mountPath, contentFile), deps);
            deps.Dispose();
            while (file.LoadingStatus == LoadingStatus.InProgress)
                yield return null;
            if (file.LoadingStatus != LoadingStatus.Completed)
            {
                Fail(a, $"the content didn't load (status {file.LoadingStatus})");
                yield break;
            }
            a.Objects = file.GetObjects();
            a.Loading = false;
        }

        static void Fail(Archive a, string error)
        {
            a.Loading = false;
            a.Error = error;
        }

        // ── showing a clip ───────────────────────────────────────────────────

        Transform m_Stage;
        GameObject m_Subject;          // what the clip plays on, when it isn't the character
        PlayableGraph m_Graph;
        AnimationClipPlayable m_Playable;
        AnimationState m_Legacy;
        AnimationClip m_Clip;
        float m_Speed = 1f;
        int m_Show;

        void Awake()
        {
            m_Stage = new GameObject("Stage").transform;
            if (m_Character != null)
                m_Character.applyRootMotion = false;
        }

        void Start() => Send(new Report { type = "ready", version = Version });

        /// <summary>Load a clip's archive (and a generated model's, if any) and play the clip.</summary>
        public void Show(string json) => StartCoroutine(ShowRoutine(JsonUtility.FromJson<Message>(json), ++m_Show));

        IEnumerator ShowRoutine(Message m, int show)
        {
            yield return Load(m.key, m.url);
            if (show != m_Show) yield break;
            var a = m_Archives[m.key];
            if (a.Error != null) { Error(m.clip, a.Error); yield break; }
            var clips = a.Objects.OfType<AnimationClip>().ToList();
            var clip = clips.FirstOrDefault(c => c.name == m.clip) ?? clips.FirstOrDefault();
            if (clip == null) { Error(m.clip, "the archive has no animation clip"); yield break; }

            Archive model = null;
            if ((m.mode == "model" || !clip.humanMotion && m.mode == "rig") && !string.IsNullOrEmpty(m.modelUrl))
            {
                yield return Load(m.modelKey, m.modelUrl);
                if (show != m_Show) yield break;
                model = m_Archives[m.modelKey];
                if (model.Error != null) { Error(m.clip, $"the model: {model.Error}"); yield break; }
            }

            Clear();
            string target;
            GameObject on;
            if (m.mode == "model" && model != null && Root(model) is { } picked)
            {
                // A model from the project: Humanoid clips retarget through its Avatar, others bind by path.
                on = m_Subject = Instantiate(picked, m_Stage, false);
                on.transform.localPosition = Vector3.zero;
                if (!string.IsNullOrEmpty(m.modelRoot) && !string.IsNullOrEmpty(m.clipRoot) && on.transform.Find(m.modelRoot) is { } top)
                    top.name = m.clipRoot;
                target = picked.name;
            }
            else if (clip.humanMotion || m.mode == "character")
            {
                if (m_Character == null) { Error(m.clip, "this player has no humanoid character"); yield break; }
                on = m_Character.gameObject;
                target = "the humanoid character";
            }
            else if (m.mode == "own" && Root(a) is { } root)
            {
                on = m_Subject = Instantiate(root, m_Stage, false);
                on.transform.localPosition = Vector3.zero;
                target = $"its model ({root.name})";
            }
            else
            {
                on = m_Subject = BuildRig(m.paths ?? Array.Empty<string>(), model != null ? Root(model) : null);
                target = model != null ? "a rig from its paths, with the generated model" : "a rig from its paths (boxes)";
            }
            if (m_Character != null)
                m_Character.gameObject.SetActive(on == m_Character.gameObject);
            foreach (var c in on.GetComponentsInChildren<Camera>(true)) Destroy(c);
            foreach (var l in on.GetComponentsInChildren<Light>(true)) Destroy(l);

            Play(on, clip);
            FrameSubject();
            Send(new Report { type = "playing", clip = m.clip, target = target, length = clip.length, humanoid = clip.humanMotion, legacy = clip.legacy, loop = clip.isLooping });
        }

        static GameObject Root(Archive a) =>
            a.Objects.OfType<GameObject>().FirstOrDefault(g => g.transform.parent == null) ?? a.Objects.OfType<GameObject>().FirstOrDefault();

        void Error(string clip, string error)
        {
            Debug.LogError($"[AnimationViewer] {clip}: {error}");
            Send(new Report { type = "error", clip = clip, error = error });
        }

        void Play(GameObject on, AnimationClip clip)
        {
            m_Clip = clip;
            if (clip.legacy)
            {
                if (!on.TryGetComponent<Animation>(out var anim))
                    anim = on.AddComponent<Animation>();
                if (anim.GetClip(clip.name) == null)
                    anim.AddClip(clip, clip.name);
                m_Legacy = anim[clip.name];
                m_Legacy.wrapMode = WrapMode.Loop;
                anim.Play(clip.name);
            }
            else
            {
                if (!on.TryGetComponent<Animator>(out var animator))
                    animator = on.AddComponent<Animator>();
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = null;   // the graph drives it, not the model's controller
                animator.Rebind();
                m_Graph = PlayableGraph.Create("AnimationViewer");
                m_Playable = AnimationClipPlayable.Create(m_Graph, clip);
                AnimationPlayableOutput.Create(m_Graph, "clip", animator).SetSourcePlayable(m_Playable);
                m_Graph.Play();
            }
            ApplySpeed();
        }

        void Clear()
        {
            if (m_Graph.IsValid())
                m_Graph.Destroy();
            m_Legacy = null;
            m_Clip = null;
            if (m_Subject != null)
                Destroy(m_Subject);
            m_Subject = null;
        }

        void Update()
        {
            // Every clip loops here, whatever its own loop setting, so it keeps playing in the preview.
            if (m_Graph.IsValid() && m_Clip != null && m_Clip.length > 0f && m_Playable.GetTime() > m_Clip.length)
                m_Playable.SetTime(m_Playable.GetTime() % m_Clip.length);
        }

        void OnDestroy() => Clear();

        /// <summary>Playback speed; 0 pauses.</summary>
        public void SetSpeed(string json)
        {
            m_Speed = JsonUtility.FromJson<Message>(json).speed;
            ApplySpeed();
        }

        void ApplySpeed()
        {
            if (m_Graph.IsValid())
                m_Playable.SetSpeed(m_Speed);
            if (m_Legacy != null)
                m_Legacy.speed = m_Speed;
        }

        /// <summary>Look at whatever plays the clip.</summary>
        public void Frame(string _) => FrameSubject();

        void FrameSubject()
        {
            var on = m_Subject != null ? m_Subject : m_Character != null ? m_Character.gameObject : null;
            if (on == null || m_CameraRig == null)
                return;
            var renderers = on.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;
            var b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            b.Expand(Vector3.Max(Vector3.zero, Vector3.one * 1.5f - b.size));   // small things: not too close
            m_CameraRig.Frame(b);
        }

        // ── a rig for a generic clip ─────────────────────────────────────────

        // The transforms the clip animates, by path, under one root with an Animator (generic: it binds by path).
        // Each animated transform gets a box, or a copy of the generated model; top-level ones are spread out
        // so they don't overlap (a clip that animates their position moves them anyway).
        GameObject BuildRig(string[] paths, GameObject model)
        {
            var root = new GameObject("Rig");
            root.transform.SetParent(m_Stage, false);
            var made = new Dictionary<string, Transform>();
            var tops = 0;
            Transform Node(string path)
            {
                if (made.TryGetValue(path, out var t))
                    return t;
                var slash = path.LastIndexOf('/');
                var parent = slash < 0 ? root.transform : Node(path[..slash]);
                t = new GameObject(slash < 0 ? path : path[(slash + 1)..]).transform;
                t.SetParent(parent, false);
                if (slash < 0)
                    t.localPosition = new Vector3(tops++ * 1.5f, 0f, 0f);
                else
                    t.localPosition = new Vector3(0f, 0.6f, 0f);
                made[path] = t;
                return t;
            }
            var copies = 0;
            foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
            {
                var t = Node(path);
                GameObject look;
                if (model != null && copies++ < 8)
                    look = Instantiate(model, t, false);
                else
                {
                    look = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(look.GetComponent<Collider>());
                    if (m_Box != null)
                        look.GetComponent<Renderer>().sharedMaterial = m_Box;
                    look.transform.SetParent(t, false);
                    look.transform.localPosition = new Vector3(0f, 0.15f, 0f);
                    look.transform.localScale = new Vector3(0.5f, 0.3f, 0.2f);
                }
                look.name = "(preview)";
            }
            if (paths.Length == 0)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(box.GetComponent<Collider>());
                box.transform.SetParent(root.transform, false);
            }
            root.AddComponent<Animator>();
            return root;
        }
    }
}
