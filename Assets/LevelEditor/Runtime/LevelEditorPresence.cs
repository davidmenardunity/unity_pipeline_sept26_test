using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Unity.Pipeline.LevelEditor
{
    // Co-authoring presence in the level editor: reports this viewer's camera to the page a few times a second
    // ({type: "camera"}), and draws everyone else where they are looking from: a small camera, its view frustum,
    // and a name tag in their colour (SetPresence, from the page). LevelEditor adds it next to itself, so the
    // page's SendMessage("LevelEditor", …) reaches it too.
    public class LevelEditorPresence : MonoBehaviour
    {
        const float ReportEvery = 0.25f;
        const float FrustumLength = 2.5f;

        [Serializable]
        class Participant
        {
            public string actor;
            public string color;
            public float[] pos;
            public float[] rot;
            public float fov = 60f;
            public float aspect = 1.6f;
        }

        [Serializable]
        class PresenceMessage
        {
            public Participant[] participants;
        }

        [Serializable]
        class CameraReport
        {
            public string type = "camera";
            public float[] pos;
            public float[] rot;
            public float fov;
            public float aspect;
            public float near;
            public float far;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void LevelEditor_Send(string json);
#else
        static void LevelEditor_Send(string json) { }
#endif

        class Shown
        {
            public GameObject Root;
            public LineRenderer Frustum;
            public Transform Tag;
            public Material Material;
        }

        readonly Dictionary<string, Shown> m_Shown = new();
        Camera m_Camera;
        Material m_Base;
        Font m_Font;
        float m_NextReport;
        Vector3 m_LastPos;
        Quaternion m_LastRot;

        public void Init(Camera camera, Material baseMaterial)
        {
            m_Camera = camera;
            m_Base = baseMaterial;
        }

        void Update()
        {
            if (m_Camera == null || Time.unscaledTime < m_NextReport)
                return;
            m_NextReport = Time.unscaledTime + ReportEvery;
            var t = m_Camera.transform;
            // Only when it moved (or once a second), so an idle viewer stays quiet.
            if ((t.position - m_LastPos).sqrMagnitude < 1e-6f && Quaternion.Angle(t.rotation, m_LastRot) < 0.05f && Time.frameCount % 60 != 0)
                return;
            m_LastPos = t.position;
            m_LastRot = t.rotation;
            LevelEditor_Send(JsonUtility.ToJson(new CameraReport
            {
                pos = new[] { t.position.x, t.position.y, t.position.z },
                rot = new[] { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w },
                fov = m_Camera.fieldOfView, aspect = m_Camera.aspect, near = m_Camera.nearClipPlane, far = m_Camera.farClipPlane,
            }));
        }

        void LateUpdate()
        {
            if (m_Camera == null)
                return;
            // Name tags face this viewer and keep a readable size.
            foreach (var s in m_Shown.Values)
            {
                if (s.Tag == null) continue;
                s.Tag.rotation = m_Camera.transform.rotation;
                var d = Vector3.Distance(m_Camera.transform.position, s.Tag.position);
                s.Tag.localScale = Vector3.one * Mathf.Clamp(d * 0.012f, 0.02f, 2f);
            }
        }

        /// <summary>Everyone else in the scene: {participants: [{actor, color, pos, rot, fov, aspect}]}.</summary>
        public void SetPresence(string json)
        {
            var m = JsonUtility.FromJson<PresenceMessage>(json);
            var seen = new HashSet<string>();
            foreach (var p in m.participants ?? Array.Empty<Participant>())
            {
                if (string.IsNullOrEmpty(p.actor) || p.pos is not { Length: 3 } || p.rot is not { Length: 4 })
                    continue;
                seen.Add(p.actor);
                if (!m_Shown.TryGetValue(p.actor, out var s))
                    m_Shown[p.actor] = s = Make(p.actor);
                ColorUtility.TryParseHtmlString(p.color ?? "#ffffff", out var color);
                s.Material.SetColor("_BaseColor", color);
                s.Material.color = color;
                s.Root.transform.SetPositionAndRotation(new Vector3(p.pos[0], p.pos[1], p.pos[2]),
                    new Quaternion(p.rot[0], p.rot[1], p.rot[2], p.rot[3]).normalized);
                DrawFrustum(s.Frustum, p.fov, p.aspect);
                s.Tag.GetComponent<TextMesh>().color = color;
            }
            foreach (var gone in new List<string>(m_Shown.Keys))
            {
                if (seen.Contains(gone)) continue;
                Destroy(m_Shown[gone].Root);
                m_Shown.Remove(gone);
            }
        }

        Shown Make(string actor)
        {
            var root = new GameObject($"Presence {actor}");
            root.transform.SetParent(transform, false);
            var material = m_Base != null ? new Material(m_Base) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));

            // The camera body: a box, and a smaller box for the lens.
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.35f, 0.25f, 0.45f);
            body.transform.localPosition = new Vector3(0f, 0f, -0.25f);
            body.GetComponent<MeshRenderer>().sharedMaterial = material;
            var lens = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(lens.GetComponent<Collider>());
            lens.transform.SetParent(root.transform, false);
            lens.transform.localScale = new Vector3(0.15f, 0.15f, 0.12f);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.03f);
            lens.GetComponent<MeshRenderer>().sharedMaterial = material;

            var line = new GameObject("Frustum").AddComponent<LineRenderer>();
            line.transform.SetParent(root.transform, false);
            line.useWorldSpace = false;
            line.widthMultiplier = 0.03f;
            line.sharedMaterial = material;
            line.positionCount = 0;

            m_Font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var tag = new GameObject("Name");
            tag.transform.SetParent(root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            var text = tag.AddComponent<TextMesh>();
            text.text = actor;
            text.font = m_Font;
            text.fontSize = 64;
            text.characterSize = 1f;
            text.anchor = TextAnchor.LowerCenter;
            tag.GetComponent<MeshRenderer>().sharedMaterial = m_Font.material;
            return new Shown { Root = root, Frustum = line, Tag = tag.transform, Material = material };
        }

        // The view's pyramid, cut at FrustumLength: apex to each corner, then the far rectangle.
        static void DrawFrustum(LineRenderer line, float fov, float aspect)
        {
            var h = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * FrustumLength;
            var w = h * Mathf.Max(0.2f, aspect);
            var a = Vector3.zero;
            var tl = new Vector3(-w, h, FrustumLength);
            var tr = new Vector3(w, h, FrustumLength);
            var br = new Vector3(w, -h, FrustumLength);
            var bl = new Vector3(-w, -h, FrustumLength);
            var points = new[] { a, tl, tr, a, br, bl, a, tl, bl, br, tr };
            line.positionCount = points.Length;
            line.SetPositions(points);
        }
    }
}
