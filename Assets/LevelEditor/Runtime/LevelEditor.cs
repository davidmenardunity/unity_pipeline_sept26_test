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
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Unity.Pipeline.LevelEditor
{
    // The runtime half of Pipeline Explorer's level editor. The page reads a .unity scene, has Pipeline
    // build a content archive (.ca) per asset, and drives this player with SendMessage("LevelEditor", …):
    // load archives, place instances, move, select, remove. The player draws no UI of its own besides a
    // move widget on the selected instance; it reports what the user does (select, move, delete) back to
    // the page, which owns the scene file and saves it.
    //
    // Archives stay mounted while instances use them. Each mount gets a fresh prefix (a remounted prefix
    // never finishes loading, see the Scene Preview sample's PreviewLoader).
    public class LevelEditor : MonoBehaviour
    {
        [SerializeField] Camera m_Camera;
        [SerializeField] LevelEditorCamera m_CameraRig;
        [SerializeField] Material m_AxisX;
        [SerializeField] Material m_AxisY;
        [SerializeField] Material m_AxisZ;
        [SerializeField] Material m_AxisCenter;
        [SerializeField] Material m_Placeholder;
        [Tooltip("Layer the move widget is on; only the overlay camera renders it, so it draws on top.")]
        [SerializeField] int m_GizmoLayer = 31;

        // ── messages ─────────────────────────────────────────────────────────

        [Serializable]
        class Message
        {
            public string id;
            public string key;
            public string url;
            public string name;
            public string primitive;   // Cube, Plane, Sphere…: a built-in mesh instead of an archive
            public float[] pos;
            public float[] rot;
            public float[] scale;
            public float u;            // viewport position of a drop, 0..1 from the left / top
            public float v;
        }

        [Serializable]
        class Report
        {
            public string type;
            public string id;
            public string key;
            public bool ok;
            public string error;
            public float[] pos;
            public float[] rot;
            public float[] scale;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void LevelEditor_Send(string json);
#else
        static void LevelEditor_Send(string json) => Debug.Log($"[LevelEditor] → {json}");
#endif

        static void Send(Report r) => LevelEditor_Send(JsonUtility.ToJson(r));

        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        static Vector3 ToV(float[] a, Vector3 fallback) => a is { Length: 3 } ? new Vector3(a[0], a[1], a[2]) : fallback;
        static Quaternion ToQ(float[] a) => a is { Length: 4 } ? new Quaternion(a[0], a[1], a[2], a[3]).normalized : Quaternion.identity;

        Report TransformReport(string type, Item item) => new()
        {
            type = type, id = item.Id,
            pos = V(item.Root.transform.position), rot = Q(item.Root.transform.rotation), scale = V(item.Root.transform.localScale),
        };

        // ── archives and instances ───────────────────────────────────────────

        class Archive
        {
            public string Key, Path, Prefix, Error;
            public ArchiveHandle Handle;
            public bool HasHandle;
            public ContentFile File;
            public bool HasFile;
            public Object Primary;
            public bool Loading;
        }

        class Item
        {
            public string Id, Key, Name;
            public GameObject Root;
            public GameObject Content;
            public bool Placeholder;
        }

        readonly Dictionary<string, Archive> m_Archives = new();
        readonly Dictionary<string, Item> m_Items = new();
        Transform m_World;
        Transform m_Prototypes;
        int m_Seq;
        Item m_Selected;

        void Awake()
        {
            if (m_Camera == null)
                m_Camera = Camera.main;
            m_World = new GameObject("Level").transform;
            m_Prototypes = new GameObject("Prototypes").transform;
            m_Prototypes.gameObject.SetActive(false);
            BuildGizmo();
        }

        void Start() => Send(new Report { type = "ready", ok = true });

        /// <summary>Download, mount and load one asset's archive; instances placed with its key show it when ready.</summary>
        public void LoadArchive(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            if (m_Archives.TryGetValue(m.key, out var existing))
            {
                if (!existing.Loading)
                    Send(new Report { type = "archive", key = m.key, ok = existing.Error == null, error = existing.Error });
                return;
            }
            var a = new Archive { Key = m.key, Loading = true };
            m_Archives[m.key] = a;
            StartCoroutine(LoadRoutine(a, m.url));
        }

        IEnumerator LoadRoutine(Archive a, string url)
        {
            m_Seq++;
            a.Path = Path.Combine(Application.persistentDataPath, $"level_{m_Seq}.ca");
            a.Prefix = $"level{m_Seq}:";
            using (var req = UnityWebRequest.Get(url))
            {
                // Pipeline Explorer only answers its own pages; this header is how they identify themselves.
                req.SetRequestHeader("X-Pipeline-Explorer", "1");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    FailArchive(a, $"download failed: {req.responseCode} {req.error}");
                    yield break;
                }
                File.WriteAllBytes(a.Path, req.downloadHandler.data);
            }

            a.Handle = ArchiveFileInterface.MountAsync(ContentNamespace.Default, a.Path, a.Prefix);
            a.HasHandle = true;
            a.Handle.JobHandle.Complete();
            if (a.Handle.Status != ArchiveStatus.Complete)
            {
                FailArchive(a, $"couldn't mount the archive (status {a.Handle.Status})");
                yield break;
            }

            var mountPath = a.Handle.GetMountPath();
            var manifest = ContentArchiveManifest.ReadManifest(mountPath);
            var contentFile = string.IsNullOrEmpty(manifest.contentFileName) ? PreviewLoader.ContentFileName : manifest.contentFileName;
            // Exactly one GlobalTableDependency when the archive uses engine built-ins, none otherwise.
            var deps = new NativeArray<ContentFile>(manifest.requiresDefaultResources ? 1 : 0, Allocator.Temp);
            if (manifest.requiresDefaultResources)
                deps[0] = ContentFile.GlobalTableDependency;
            a.File = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, Path.Combine(mountPath, contentFile), deps);
            a.HasFile = true;
            deps.Dispose();
            while (a.File.LoadingStatus == LoadingStatus.InProgress)
                yield return null;
            if (a.File.LoadingStatus != LoadingStatus.Completed)
            {
                FailArchive(a, $"the content didn't load (status {a.File.LoadingStatus})");
                yield break;
            }

            var objects = a.File.GetObjects();
            a.Primary = objects.OfType<GameObject>().FirstOrDefault() ?? (Object)objects.OfType<Material>().FirstOrDefault();
            if (a.Primary == null)
            {
                FailArchive(a, "the archive has nothing to place (no model, prefab or material)");
                yield break;
            }
            a.Loading = false;
            // Every instance of this asset placed so far (the page places before it builds archives).
            foreach (var item in m_Items.Values)
                if (item.Root != null && item.Placeholder && item.Key == a.Key)
                    Attach(item, a);
            Send(new Report { type = "archive", key = a.Key, ok = true });
        }

        void FailArchive(Archive a, string error)
        {
            a.Loading = false;
            a.Error = error;
            Debug.LogError($"[LevelEditor] {a.Key}: {error}");
            Send(new Report { type = "archive", key = a.Key, ok = false, error = error });
        }

        /// <summary>Place an instance: an archive's content (by key), a built-in primitive, or a placeholder until the archive loads.</summary>
        public void Place(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            if (m_Items.TryGetValue(m.id, out var old))
                Destroy(old.Root);
            var item = new Item { Id = m.id, Key = m.key, Name = m.name };
            item.Root = new GameObject(string.IsNullOrEmpty(m.name) ? m.id : m.name);
            item.Root.transform.SetParent(m_World, false);
            item.Root.transform.SetPositionAndRotation(ToV(m.pos, Vector3.zero), ToQ(m.rot));
            item.Root.transform.localScale = ToV(m.scale, Vector3.one);
            m_Items[m.id] = item;

            if (!string.IsNullOrEmpty(m.primitive) && Enum.TryParse<PrimitiveType>(m.primitive, out var type))
            {
                var p = GameObject.CreatePrimitive(type);
                Destroy(p.GetComponent<Collider>());
                p.GetComponent<Renderer>().sharedMaterial = m_Placeholder;
                p.transform.SetParent(item.Root.transform, false);
                item.Content = p;
                return;
            }
            Archive a = null;
            if (!string.IsNullOrEmpty(m.key))
                m_Archives.TryGetValue(m.key, out a);
            if (a != null && !a.Loading && a.Error == null)
            {
                Attach(item, a);
                return;
            }
            // Until the archive is ready (or if it failed): a small grey box where the instance is.
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(box.GetComponent<Collider>());
            box.GetComponent<Renderer>().sharedMaterial = m_Placeholder;
            box.transform.SetParent(item.Root.transform, false);
            box.transform.localPosition = new Vector3(0, 0.25f, 0);
            box.transform.localScale = Vector3.one * 0.5f;
            item.Content = box;
            item.Placeholder = true;
        }

        void Attach(Item item, Archive a)
        {
            if (item.Content != null)
                Destroy(item.Content);
            GameObject content;
            switch (a.Primary)
            {
                case GameObject go:
                    content = Instantiate(go, item.Root.transform, false);
                    break;
                case Material mat:
                    content = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(content.GetComponent<Collider>());
                    content.GetComponent<Renderer>().sharedMaterial = mat;
                    content.transform.SetParent(item.Root.transform, false);
                    break;
                default:
                    return;
            }
            // The scene says where the instance is: its root sits exactly on the item.
            content.transform.localPosition = Vector3.zero;
            content.transform.localRotation = Quaternion.identity;
            content.transform.localScale = Vector3.one;
            foreach (var c in content.GetComponentsInChildren<Camera>(true)) Destroy(c);
            foreach (var l in content.GetComponentsInChildren<Light>(true)) Destroy(l);
            foreach (var c in content.GetComponentsInChildren<AudioListener>(true)) Destroy(c);
            content.SetActive(true);
            item.Content = content;
            item.Placeholder = false;
        }

        public void SetTransform(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            if (!m_Items.TryGetValue(m.id, out var item))
                return;
            var t = item.Root.transform;
            t.SetPositionAndRotation(ToV(m.pos, t.position), m.rot is { Length: 4 } ? ToQ(m.rot) : t.rotation);
            t.localScale = ToV(m.scale, t.localScale);
        }

        public void Remove(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            if (!m_Items.TryGetValue(m.id, out var item))
                return;
            if (m_Selected == item)
                m_Selected = null;
            Destroy(item.Root);
            m_Items.Remove(m.id);
        }

        public void Select(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            m_Selected = !string.IsNullOrEmpty(m.id) && m_Items.TryGetValue(m.id, out var item) ? item : null;
        }

        /// <summary>Frame one instance (id), or everything (no id).</summary>
        public void Frame(string json)
        {
            var m = string.IsNullOrEmpty(json) ? new Message() : JsonUtility.FromJson<Message>(json);
            var items = !string.IsNullOrEmpty(m.id) && m_Items.TryGetValue(m.id, out var one) ? new[] { one } : m_Items.Values.ToArray();
            var b = BoundsOf(items);
            if (b.HasValue)
                m_CameraRig.Frame(b.Value);
        }

        /// <summary>Remove every instance and release every archive (another scene is being opened).</summary>
        public void Clear(string _)
        {
            m_Selected = null;
            foreach (var item in m_Items.Values)
                if (item.Root != null)
                    DestroyImmediate(item.Root);
            m_Items.Clear();
            StopAllCoroutines();
            foreach (var a in m_Archives.Values)
            {
                if (a.HasFile) { a.File.UnloadAsync().WaitForCompletion(5000); a.HasFile = false; }
                if (a.HasHandle) { a.Handle.Unmount().Complete(); a.HasHandle = false; }
                try { if (a.Path != null) File.Delete(a.Path); } catch { /* unique names never collide */ }
            }
            m_Archives.Clear();
        }

        /// <summary>A prefab was dropped on the viewport: place it where the drop meets the ground.</summary>
        public void DropAt(string json)
        {
            var m = JsonUtility.FromJson<Message>(json);
            var ray = m_Camera.ViewportPointToRay(new Vector3(m.u, 1f - m.v, 0f));
            var ground = new Plane(Vector3.up, Vector3.zero);
            var point = ground.Raycast(ray, out var d) ? ray.GetPoint(d) : ray.GetPoint(10f);
            m.pos = V(point);
            m.rot ??= Q(Quaternion.identity);
            m.scale ??= V(Vector3.one);
            Place(JsonUtility.ToJson(m));
            m_Selected = m_Items[m.id];
            Send(TransformReport("placed", m_Selected));
        }

        // ── picking and the move widget ──────────────────────────────────────

        GameObject m_Gizmo;
        Transform m_HandleX, m_HandleY, m_HandleZ, m_HandleCenter;
        enum Drag { None, X, Y, Z, Plane }
        Drag m_Drag;
        Vector3 m_DragStartPos, m_DragAnchor;
        float m_DragAxisT;
        bool m_Moved;
        float m_LastReport;
        Vector2 m_PressAt;

        void BuildGizmo()
        {
            m_Gizmo = new GameObject("MoveWidget");
            m_HandleX = Arrow("X", Vector3.right, m_AxisX);
            m_HandleY = Arrow("Y", Vector3.up, m_AxisY);
            m_HandleZ = Arrow("Z", Vector3.forward, m_AxisZ);
            var center = GameObject.CreatePrimitive(PrimitiveType.Cube);
            center.name = "Center";
            center.transform.SetParent(m_Gizmo.transform, false);
            center.transform.localScale = Vector3.one * 0.18f;
            center.GetComponent<Renderer>().sharedMaterial = m_AxisCenter;
            m_HandleCenter = center.transform;
            foreach (var t in m_Gizmo.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = m_GizmoLayer;
            m_Gizmo.SetActive(false);
        }

        Transform Arrow(string name, Vector3 dir, Material mat)
        {
            var root = new GameObject(name).transform;
            root.SetParent(m_Gizmo.transform, false);
            root.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.transform.SetParent(root, false);
            shaft.transform.localPosition = new Vector3(0, 0.5f, 0);
            shaft.transform.localScale = new Vector3(0.05f, 0.5f, 0.05f);
            var tip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tip.transform.SetParent(root, false);
            tip.transform.localPosition = new Vector3(0, 1.05f, 0);
            tip.transform.localScale = new Vector3(0.14f, 0.14f, 0.14f);
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                r.sharedMaterial = mat;
            // Easier to grab than the thin shaft.
            var grab = shaft.GetComponent<CapsuleCollider>();
            if (grab != null) grab.radius = 2.5f;
            return root;
        }

        void LateUpdate()
        {
            var show = m_Selected != null && m_Selected.Root != null;
            m_Gizmo.SetActive(show);
            if (!show)
                return;
            var p = m_Selected.Root.transform.position;
            m_Gizmo.transform.position = p;
            // Same size on screen wherever it is.
            var size = Vector3.Distance(m_Camera.transform.position, p) * 0.14f;
            m_Gizmo.transform.localScale = Vector3.one * Mathf.Max(0.05f, size);
        }

        void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (keyboard != null)
                HandleKeys(keyboard);
            if (mouse == null)
                return;
            var pos = mouse.position.ReadValue();
            var ray = m_Camera.ScreenPointToRay(pos);

            if (mouse.leftButton.wasPressedThisFrame)
            {
                m_PressAt = pos;
                m_Moved = false;
                m_Drag = Drag.None;
                if (m_Selected != null && Physics.Raycast(ray, out var hit, 10000f, 1 << m_GizmoLayer))
                {
                    var t = hit.transform;
                    m_Drag = t.IsChildOf(m_HandleX) ? Drag.X : t.IsChildOf(m_HandleY) ? Drag.Y : t.IsChildOf(m_HandleZ) ? Drag.Z : Drag.Plane;
                }
                else
                {
                    var picked = Pick(ray);
                    if (picked != m_Selected)
                    {
                        m_Selected = picked;
                        Send(new Report { type = "selected", id = picked?.Id ?? "" });
                    }
                    // Grabbing the selected object itself drags it over the ground.
                    if (picked != null)
                        m_Drag = Drag.Plane;
                }
                if (m_Drag != Drag.None)
                {
                    m_DragStartPos = m_Selected.Root.transform.position;
                    m_DragAnchor = DragPoint(ray, m_Drag, m_DragStartPos, out m_DragAxisT);
                }
            }
            else if (mouse.leftButton.isPressed && m_Drag != Drag.None && m_Selected != null)
            {
                if (!m_Moved && (pos - m_PressAt).magnitude < 3f)
                    return;
                m_Moved = true;
                var now = DragPoint(ray, m_Drag, m_DragStartPos, out var t);
                Vector3 delta = m_Drag == Drag.Plane ? now - m_DragAnchor : AxisOf(m_Drag) * (t - m_DragAxisT);
                m_Selected.Root.transform.position = m_DragStartPos + delta;
                if (Time.unscaledTime - m_LastReport > 0.1f)
                {
                    m_LastReport = Time.unscaledTime;
                    Send(TransformReport("moving", m_Selected));
                }
            }
            else if (mouse.leftButton.wasReleasedThisFrame && m_Drag != Drag.None)
            {
                if (m_Moved && m_Selected != null)
                    Send(TransformReport("moved", m_Selected));
                m_Drag = Drag.None;
            }
        }

        void HandleKeys(Keyboard k)
        {
            if (m_Selected == null)
                return;
            if (k.rKey.wasPressedThisFrame)
            {
                var step = k.shiftKey.isPressed ? -15f : 15f;
                m_Selected.Root.transform.rotation = Quaternion.Euler(0, step, 0) * m_Selected.Root.transform.rotation;
                Send(TransformReport("moved", m_Selected));
            }
            if (k.deleteKey.wasPressedThisFrame || k.backspaceKey.wasPressedThisFrame)
                Send(new Report { type = "delete", id = m_Selected.Id });
            if (k.fKey.wasPressedThisFrame)
                Frame(JsonUtility.ToJson(new Message { id = m_Selected.Id }));
            if (k.escapeKey.wasPressedThisFrame)
            {
                m_Selected = null;
                Send(new Report { type = "selected", id = "" });
            }
        }

        static Vector3 AxisOf(Drag d) => d == Drag.X ? Vector3.right : d == Drag.Y ? Vector3.up : Vector3.forward;

        // Where the pointer ray meets the drag: the closest point on the axis line, or the ground-level plane
        // through the object. t is the position along the axis.
        static Vector3 DragPoint(Ray ray, Drag drag, Vector3 origin, out float t)
        {
            t = 0;
            if (drag == Drag.Plane)
            {
                var plane = new Plane(Vector3.up, origin);
                return plane.Raycast(ray, out var d) ? ray.GetPoint(d) : origin;
            }
            var axis = AxisOf(drag);
            // Closest points between the axis line (origin + axis*t) and the ray.
            var w0 = origin - ray.origin;
            float b = Vector3.Dot(axis, ray.direction), d0 = Vector3.Dot(axis, w0), e = Vector3.Dot(ray.direction, w0);
            float denom = 1f - b * b;
            t = Mathf.Abs(denom) < 1e-5f ? 0 : (b * e - d0) / denom;
            return origin + axis * t;
        }

        Item Pick(Ray ray)
        {
            Item best = null;
            float bestD = float.MaxValue;
            foreach (var item in m_Items.Values)
            {
                if (item.Root == null) continue;
                foreach (var r in item.Root.GetComponentsInChildren<Renderer>())
                    if (r.bounds.IntersectRay(ray, out var d) && d < bestD)
                    {
                        bestD = d;
                        best = item;
                    }
            }
            return best;
        }

        static Bounds? BoundsOf(IEnumerable<Item> items)
        {
            Bounds? b = null;
            foreach (var item in items)
                if (item?.Root != null)
                    foreach (var r in item.Root.GetComponentsInChildren<Renderer>())
                    {
                        if (b == null) b = r.bounds;
                        else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
                    }
            return b;
        }

        void OnDestroy() => Clear(null);
    }
}
