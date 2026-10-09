using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Cloud.Collaboration;
using Unity.Cloud.Common;
using Unity.Pipeline.Samples.ScenePreview;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Pipeline.PreviewAnnotations
{
    // Comments on the previewed asset, in the scene: pins on its surface and drawings around it, saved as
    // Unity Cloud Collaboration annotations (a spatial-3d attachment for a pin, a sketch attachment for a
    // drawing, and the camera that saw them). Threads, replies and @mentions are plain annotation text and
    // replies, so they read the same anywhere annotations are shown.
    //
    // The host page draws all the UI (toolbar, composer, thread bubbles) and talks to this component through
    // SendMessage("Annotations", "Call", json) and the reports sent back with PreviewAnnotations_Send.
    // Calls: { id, method, arg }; replies: { event: "result", id, ok, value | error }.
    public class PreviewAnnotations : MonoBehaviour
    {
        [SerializeField] Camera m_Camera;
        [SerializeField] Material m_PinMaterial;
        [SerializeField] Material m_LineMaterial;
        [SerializeField] float m_PinSize = 0.12f;
        [SerializeField] float m_StrokeWidth = 0.035f;

        enum Mode { None, Pin, Draw }

        sealed class Shown
        {
            public string Id;
            public int Number;
            public bool Resolved;
            public Vector3? Pin;
            public GameObject Root;
            public ICameraDetails Camera;
        }

        sealed class Draft
        {
            public Vector3? Pin;
            public readonly List<(Color Color, List<Vector3> Points, LineRenderer Line)> Strokes = new();
            public GameObject Root;
            public ICameraDetails Camera;
        }

        AnnotationManagement m_Api;
        string m_Origin;   // the page's origin: uploads go through its /api/collab-upload
        PreviewOrbitCamera m_Orbit;
        string m_ProjectId, m_AssetId;
        Dictionary<string, string> m_TargetContext = new();
        readonly List<Shown> m_Shown = new();
        Draft m_Draft;
        Mode m_Mode;
        Color m_Color = new(1f, 0.35f, 0.35f);
        bool m_Visible = true;
        string m_Focused;
        int m_LoadSeq;
        GameObject m_Preview;
        readonly List<Collider> m_PickColliders = new();
        (Color Color, List<Vector3> Points, LineRenderer Line)? m_Stroke;
        Vector2 m_PressAt;
        float m_LastReport;
        string m_LastPins;
        MaterialPropertyBlock m_Block;

        void Awake()
        {
            if (m_Camera == null) m_Camera = Camera.main;
            m_Orbit = m_Camera != null ? m_Camera.GetComponent<PreviewOrbitCamera>() : null;
            m_Block = new MaterialPropertyBlock();
            try
            {
                m_Api = LocalCollaborationService.Create(out var host);
                m_Origin = host[..host.LastIndexOf("/api/collab", StringComparison.Ordinal)];
                Debug.Log($"[Annotations] Collaboration through {host}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Annotations] Couldn't set up the Collaboration SDK: {e}");
            }
        }

        void OnEnable() => PreviewLoader.Shown += OnPreviewShown;
        void OnDisable() => PreviewLoader.Shown -= OnPreviewShown;

        // ── messages from the page ────────────────────────────────────────────

        /// <summary>Entry point for the host page: { id, method, arg }.</summary>
        public async void Call(string json)
        {
            string id = null;
            try
            {
                var msg = JObject.Parse(json);
                id = (string)msg["id"];
                var arg = msg["arg"] as JObject ?? new JObject();
                object value = (string)msg["method"] switch
                {
                    "Context" => await SetContext(arg),
                    "Reload" => await Load(),
                    "Mode" => SetMode(arg),
                    "Show" => Show((bool?)arg["visible"] ?? true),
                    "Create" => await Create(arg),
                    "Discard" => DiscardDraft(),
                    "Focus" => Focus((string)arg["annotationId"]),
                    "Delete" => await Delete((string)arg["annotationId"]),
                    var other => throw new ArgumentException($"unknown method '{other}'"),
                };
                Send(new { @event = "result", id, ok = true, value });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Annotations] {e}");
                Send(new { @event = "result", id, ok = false, error = Describe(e) });
            }
        }

        async Task<object> SetContext(JObject arg)
        {
            m_ProjectId = (string)arg["projectId"];
            m_AssetId = (string)arg["assetId"];
            m_TargetContext = new Dictionary<string, string>();
            foreach (var key in new[] { "path", "workbench", "revision", "branch" })
                if (arg[key] is { Type: JTokenType.String } v && !string.IsNullOrEmpty((string)v))
                    m_TargetContext[key] = (string)v;
            DiscardDraft();
            m_Focused = null;
            return await Load();
        }

        object SetMode(JObject arg)
        {
            m_Mode = (string)arg["mode"] switch { "pin" => Mode.Pin, "draw" => Mode.Draw, _ => Mode.None };
            if (arg["color"] is { Type: JTokenType.String } c && ColorUtility.TryParseHtmlString((string)c, out var color))
                m_Color = color;
            if (m_Orbit != null) m_Orbit.Hold = m_Mode != Mode.None;
            return m_Mode.ToString().ToLowerInvariant();
        }

        object Show(bool visible)
        {
            m_Visible = visible;
            foreach (var s in m_Shown) if (s.Root != null) s.Root.SetActive(visible);
            m_LastPins = null;
            return visible;
        }

        // ── reading ───────────────────────────────────────────────────────────

        AssetReference Asset => new(new ProjectId(m_ProjectId), new AssetId(m_AssetId));

        async Task<object> Load()
        {
            var seq = ++m_LoadSeq;
            if (m_Api == null) throw new InvalidOperationException("the Collaboration SDK isn't set up");
            if (string.IsNullOrEmpty(m_ProjectId) || string.IsNullOrEmpty(m_AssetId)) { Clear(); return new { count = 0 }; }

            var all = new List<IAnnotation>();
            string next = null;
            do
            {
                var page = await m_Api.ReadAnnotationsAsync(Asset, new FilteringOptions(next, 100, SortOrder.Ascending));
                if (seq != m_LoadSeq) return new { count = 0, superseded = true };
                all.AddRange(page.Annotations ?? Array.Empty<IAnnotation>());
                next = page.Next;
            } while (!string.IsNullOrEmpty(next) && all.Count < 500);

            Clear();
            var roots = all.Where(a => a.RootAnnotationId == null).OrderBy(a => a.Created).ToList();
            for (var i = 0; i < roots.Count; i++) m_Shown.Add(Build(roots[i], i + 1));
            m_LastPins = null;
            Send(new { @event = "loaded", count = roots.Count });
            return new { count = roots.Count, ids = roots.Select(r => r.AnnotationId.ToString()).ToArray() };
        }

        Shown Build(IAnnotation a, int number)
        {
            var s = new Shown
            {
                Id = a.AnnotationId.ToString(),
                Number = number,
                Resolved = a.Resolved != null,
                Camera = a.Camera,
                Root = new GameObject($"Annotation {number}"),
            };
            s.Root.transform.SetParent(transform, false);
            s.Root.SetActive(m_Visible);
            foreach (var att in a.Attachments ?? Array.Empty<IAttachment>())
            {
                switch (att)
                {
                    case ISpatial3DAttachment pin:
                        s.Pin = ToVector(pin.Position);
                        MakePin(s.Root.transform, s.Pin.Value, s.Resolved ? new Color(0.55f, 0.6f, 0.65f) : new Color(1f, 0.78f, 0.2f));
                        s.Camera ??= pin.Camera;
                        break;
                    case ISketchAttachment sketch:
                        foreach (var (color, points) in ReadSketch(sketch.SketchData))
                            MakeLine(s.Root.transform, color, points);
                        s.Camera ??= sketch.Camera;
                        // Without a pin, the bubble sits at the drawing's last point.
                        s.Pin ??= ReadSketch(sketch.SketchData).LastOrDefault().Points?.LastOrDefault();
                        break;
                }
            }
            return s;
        }

        void Clear()
        {
            foreach (var s in m_Shown) if (s.Root != null) Destroy(s.Root);
            m_Shown.Clear();
            m_LastPins = null;
        }

        // ── writing ───────────────────────────────────────────────────────────

        async Task<object> Create(JObject arg)
        {
            if (m_Api == null) throw new InvalidOperationException("the Collaboration SDK isn't set up");
            if (string.IsNullOrEmpty(m_AssetId)) throw new InvalidOperationException("no asset is shown");
            var text = ((string)arg["text"])?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("the comment is empty");

            // The view as it is now: the drawing's strokes and the pin are in the scene, so the screenshot
            // composes them with the model. It becomes the comment's thumbnail.
            byte[] shot = null;
            string shotError = null;
            try { shot = await CaptureAsync(); }
            catch (Exception e) { shotError = Describe(e); }

            var camera = m_Draft?.Camera ?? CurrentCamera();
            var attachments = new List<ICreateAttachmentRequest>();
            if (m_Draft?.Pin is { } pin)
                attachments.Add(new CreateSpatial3DAttachmentRequest("pin", ToSpatial(pin), camera));
            if (m_Draft is { Strokes: { Count: > 0 } strokes })
                attachments.Add(new CreateSketchAttachmentRequest(WriteSketch(strokes), camera));

            var metadata = new Dictionary<string, MetadataValue> { ["source"] = MetadataValue.FromString("pipeline-explorer") };
            var id = await m_Api.CreateAnnotationAsync(Asset, new CreateAnnotationData(
                targetContext: m_TargetContext, text: text, metadata: metadata,
                attachments: attachments.Count > 0 ? attachments : null, camera: camera));

            if (shot != null)
            {
                try { await UploadThumbnail(id, shot); }
                catch (Exception e) { shotError = Describe(e); }
            }

            DiscardDraft();
            await Load();
            m_Focused = id.ToString();
            return new { annotationId = id.ToString(), thumbnail = shot != null && shotError == null, thumbnailError = shotError };
        }

        // ── screenshots ───────────────────────────────────────────────────────

        Task<byte[]> CaptureAsync()
        {
            var done = new TaskCompletionSource<byte[]>();
            StartCoroutine(Capture(done));
            return done.Task;
        }

        System.Collections.IEnumerator Capture(TaskCompletionSource<byte[]> done)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var jpg = tex.EncodeToJPG(88);
                Destroy(tex);
                done.SetResult(jpg);
            }
            catch (Exception e) { done.SetException(e); }
        }

        // Collaboration's three steps: an upload URL, the upload (relayed by the page's server: the storage
        // doesn't accept this origin), then finalize.
        async Task UploadThumbnail(AnnotationId id, byte[] image)
        {
            var reference = new AnnotationReference(new ProjectId(m_ProjectId), id);
            var target = await m_Api.ReadThumbnailUploadUrlAsync(reference);
            using (var put = UnityEngine.Networking.UnityWebRequest.Put(
                       $"{m_Origin}/api/collab-upload?url={Uri.EscapeDataString(target.Url)}", image))
            {
                put.SetRequestHeader("Content-Type", "image/jpeg");
                put.SetRequestHeader("X-Pipeline-Explorer", "1");
                var sent = new TaskCompletionSource<bool>();
                put.SendWebRequest().completed += _ => sent.SetResult(true);
                await sent.Task;
                if (put.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    throw new InvalidOperationException($"the screenshot upload failed: {put.error} {put.downloadHandler?.text}");
            }
            await m_Api.FinalizeThumbnailUploadAsync(reference);
        }

        async Task<object> Delete(string annotationId)
        {
            await m_Api.DeleteAnnotationAsync(new AnnotationReference(new ProjectId(m_ProjectId), new AnnotationId(annotationId)));
            await Load();
            return annotationId;
        }

        object DiscardDraft()
        {
            if (m_Draft?.Root != null) Destroy(m_Draft.Root);
            m_Draft = null;
            m_Stroke = null;
            m_LastPins = null;
            return true;
        }

        Draft EnsureDraft()
        {
            if (m_Draft != null) return m_Draft;
            m_Draft = new Draft { Root = new GameObject("Draft annotation"), Camera = CurrentCamera() };
            m_Draft.Root.transform.SetParent(transform, false);
            return m_Draft;
        }

        // ── focusing a comment ────────────────────────────────────────────────

        object Focus(string annotationId)
        {
            m_Focused = annotationId;
            var s = m_Shown.FirstOrDefault(x => x.Id == annotationId);
            if (s == null) return false;
            if (s.Camera != null && m_Orbit != null)
            {
                var pos = ToVector(s.Camera.Position);
                var target = s.Camera.Target is { } t ? ToVector(t) : s.Pin ?? m_Orbit.Pivot;
                m_Orbit.LookFrom(pos, target);
            }
            m_LastPins = null;
            return true;
        }

        // ── input: pins and strokes ───────────────────────────────────────────

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null || m_Camera == null || m_Mode == Mode.None) return;
            var pos = mouse.position.ReadValue();
            var inside = pos.x >= 0 && pos.y >= 0 && pos.x <= Screen.width && pos.y <= Screen.height;

            if (m_Mode == Mode.Pin)
            {
                if (mouse.leftButton.wasPressedThisFrame && inside) m_PressAt = pos;
                if (mouse.leftButton.wasReleasedThisFrame && inside && (pos - m_PressAt).sqrMagnitude < 36f && Pick(pos, out var hit))
                {
                    var d = EnsureDraft();
                    d.Pin = hit;
                    d.Camera = CurrentCamera();
                    foreach (Transform child in d.Root.transform) if (child.name == "Pin") Destroy(child.gameObject);
                    MakePin(d.Root.transform, hit, m_Color);
                    var vp = m_Camera.WorldToViewportPoint(hit);
                    Send(new { @event = "draft", kind = "pin", u = vp.x, v = 1f - vp.y });
                }
            }
            else if (m_Mode == Mode.Draw)
            {
                if (mouse.leftButton.wasPressedThisFrame && inside)
                {
                    var d = EnsureDraft();
                    d.Camera = CurrentCamera();
                    var line = MakeLine(d.Root.transform, m_Color, new List<Vector3>());
                    m_Stroke = (m_Color, new List<Vector3>(), line);
                    d.Strokes.Add(m_Stroke.Value);
                }
                // Every frame the button is down, and the frames it goes down and up (a quick drag can
                // press and release within a frame or two).
                if (m_Stroke is { } stroke && (mouse.leftButton.isPressed || mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame))
                {
                    var p = DrawPoint(pos);
                    if (stroke.Points.Count == 0 || (stroke.Points[^1] - p).sqrMagnitude > 0.0004f)
                    {
                        stroke.Points.Add(p);
                        stroke.Line.positionCount = stroke.Points.Count;
                        stroke.Line.SetPositions(stroke.Points.ToArray());
                    }
                }
                if (m_Stroke is { } done && mouse.leftButton.wasReleasedThisFrame)
                {
                    m_Stroke = null;
                    if (done.Points.Count < 2)
                    {
                        m_Draft?.Strokes.Remove(done);
                        Destroy(done.Line.gameObject);
                        return;
                    }
                    var vp = m_Camera.WorldToViewportPoint(done.Points[^1]);
                    Send(new { @event = "draft", kind = "sketch", strokes = m_Draft.Strokes.Count, u = vp.x, v = 1f - vp.y });
                }
            }
        }

        // The surface under the cursor: the preview (approximate boxes when its meshes aren't readable),
        // else the ground.
        bool Pick(Vector2 screen, out Vector3 point)
        {
            var ray = m_Camera.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 500f) && m_PickColliders.Contains(hit.collider))
            {
                point = hit.point;
                return true;
            }
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (ground.Raycast(ray, out var enter) && enter < 200f)
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = default;
            return false;
        }

        // Strokes lie on the preview where they cross it, else on whichever comes first of the ground and
        // the plane through the orbit's pivot facing the camera: a drawing stays in place in the scene as
        // the camera orbits, and strokes around the preview's foot lie on the ground instead of under it.
        Vector3 DrawPoint(Vector2 screen)
        {
            var ray = m_Camera.ScreenPointToRay(screen);
            var toward = -ray.direction * 0.02f;   // just off the surface, so the line isn't buried in it
            if (Physics.Raycast(ray, out var hit, 500f) && m_PickColliders.Contains(hit.collider)) return hit.point + toward;
            var pivot = m_Orbit != null ? m_Orbit.Pivot : Vector3.zero;
            var best = float.MaxValue;
            if (new Plane(-m_Camera.transform.forward, pivot).Raycast(ray, out var onPlane)) best = onPlane;
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var onGround) && onGround < best)
                return ray.GetPoint(onGround) + Vector3.up * 0.02f;
            return best < float.MaxValue ? ray.GetPoint(best) : ray.GetPoint(10f);
        }

        void OnPreviewShown(GameObject preview, Bounds bounds)
        {
            foreach (var c in m_PickColliders) if (c != null) Destroy(c);
            m_PickColliders.Clear();
            m_Preview = preview;
            foreach (var filter in preview.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                if (mesh.isReadable)
                {
                    var mc = filter.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    m_PickColliders.Add(mc);
                }
                else
                {
                    var box = filter.gameObject.AddComponent<BoxCollider>();
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                    m_PickColliders.Add(box);
                }
            }
            foreach (var skinned in preview.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var box = skinned.gameObject.AddComponent<BoxCollider>();
                box.center = skinned.localBounds.center;
                box.size = skinned.localBounds.size;
                m_PickColliders.Add(box);
            }
        }

        // ── reporting where things are on screen ──────────────────────────────

        void LateUpdate()
        {
            if (m_Camera == null || Time.unscaledTime - m_LastReport < 1f / 30f) return;
            m_LastReport = Time.unscaledTime;
            var items = new List<object>();
            foreach (var s in m_Shown)
                if (s.Pin is { } p && m_Visible) items.Add(OnScreen(s.Id, s.Number, p, s.Resolved));
            if (m_Draft != null)
            {
                var at = m_Draft.Pin ?? m_Draft.Strokes.LastOrDefault().Points?.LastOrDefault();
                if (at is { } d) items.Add(OnScreen("draft", 0, d, false));
            }
            var json = JsonConvert.SerializeObject(items);
            if (json == m_LastPins) return;
            m_LastPins = json;
            SendRaw($"{{\"event\":\"pins\",\"focused\":{JsonConvert.SerializeObject(m_Focused)},\"items\":{json}}}");
        }

        object OnScreen(string id, int number, Vector3 world, bool resolved)
        {
            var vp = m_Camera.WorldToViewportPoint(world);
            var behind = vp.z <= 0f;
            // Hidden behind the preview (by its pick shapes) as seen from here.
            var occluded = !behind && Physics.Linecast(m_Camera.transform.position, world + (m_Camera.transform.position - world).normalized * 0.05f,
                out var hit) && m_PickColliders.Contains(hit.collider);
            return new { id, n = number, u = Round(vp.x), v = Round(1f - vp.y), behind, occluded, resolved };
        }

        static float Round(float x) => (float)Math.Round(x, 4);

        // ── visuals ───────────────────────────────────────────────────────────

        void MakePin(Transform parent, Vector3 at, Color color)
        {
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Pin";
            Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(parent, false);
            head.transform.position = at;
            head.transform.localScale = Vector3.one * m_PinSize;
            var r = head.GetComponent<MeshRenderer>();
            if (m_PinMaterial != null) r.sharedMaterial = m_PinMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.GetPropertyBlock(m_Block);
            m_Block.SetColor("_BaseColor", color);
            r.SetPropertyBlock(m_Block);
        }

        LineRenderer MakeLine(Transform parent, Color color, List<Vector3> points)
        {
            var go = new GameObject("Stroke");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            if (m_LineMaterial != null) line.sharedMaterial = m_LineMaterial;
            line.widthMultiplier = m_StrokeWidth;
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.startColor = line.endColor = color;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
            return line;
        }

        // ── conversions ───────────────────────────────────────────────────────

        CameraDetails CurrentCamera()
        {
            if (m_Camera == null) return null;
            var t = m_Camera.transform;
            var e = t.rotation.eulerAngles;
            var pivot = m_Orbit != null ? m_Orbit.Pivot : t.position + t.forward * 10f;
            return new CameraDetails(ToSpatial(t.position), new SpatialRotation(e.x, e.y, e.z), m_Camera.fieldOfView, ToSpatial(pivot));
        }

        static SpatialPosition ToSpatial(Vector3 v) => new(v.x, v.y, v.z);
        static Vector3 ToVector(SpatialPosition p) => new(p.X, p.Y, p.Z);

        // Sketch data: { "v": 1, "space": "world", "strokes": [ { "color": "#RRGGBB", "points": [x, y, z, …] } ] }.
        static string WriteSketch(IEnumerable<(Color Color, List<Vector3> Points, LineRenderer Line)> strokes) =>
            JsonConvert.SerializeObject(new
            {
                v = 1,
                space = "world",
                strokes = strokes.Where(s => s.Points.Count > 1).Select(s => new
                {
                    color = "#" + ColorUtility.ToHtmlStringRGB(s.Color),
                    points = s.Points.SelectMany(p => new[] { Round(p.x), Round(p.y), Round(p.z) }).ToArray(),
                }).ToArray(),
            });

        static List<(Color Color, List<Vector3> Points)> ReadSketch(string json)
        {
            var list = new List<(Color, List<Vector3>)>();
            try
            {
                foreach (var s in (JObject.Parse(json)["strokes"] as JArray) ?? new JArray())
                {
                    ColorUtility.TryParseHtmlString((string)s["color"] ?? "#ff5a5a", out var color);
                    var f = (s["points"] as JArray)?.Select(x => (float)x).ToArray() ?? Array.Empty<float>();
                    var pts = new List<Vector3>();
                    for (var i = 0; i + 2 < f.Length; i += 3) pts.Add(new Vector3(f[i], f[i + 1], f[i + 2]));
                    if (pts.Count > 1) list.Add((color, pts));
                }
            }
            catch (JsonException) { /* someone else's sketch format: nothing to draw */ }
            return list;
        }

        static string Describe(Exception e)
        {
            var msg = e.Message;
            if (e is AggregateException { InnerException: { } inner }) msg = inner.Message;
            return string.IsNullOrEmpty(msg) ? e.GetType().Name : msg;
        }

        // ── to the page ───────────────────────────────────────────────────────

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void PreviewAnnotations_Send(string json);
#else
        static void PreviewAnnotations_Send(string json) => Debug.Log($"[Annotations] → page: {json}");
#endif

        static void Send(object message) => SendRaw(JsonConvert.SerializeObject(message));
        static void SendRaw(string json) => PreviewAnnotations_Send(json);
    }
}
