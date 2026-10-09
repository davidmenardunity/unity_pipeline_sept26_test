using System;
using System.Collections.Generic;
using Unity.Pipeline.Samples.ScenePreview;
using UnityEngine;

namespace Unity.Pipeline.PreviewStage
{
    // The set around the previewed object: a plaza, trees, street props and buildings in a ring.
    // Previews range from a barrel to a church, so when one swaps in, every prop slides straight out
    // from the centre until the ring clears the object's footprint (and back in for a small one).
    // Built by Tools > Scene Preview > Build Preview Stage (PreviewStageSetup).
    public class PreviewStage : MonoBehaviour
    {
        [Serializable]
        public class Prop
        {
            public Transform Transform;
            [Tooltip("Where the setup placed it (world space, around the origin).")]
            public Vector3 Home;
        }

        [Tooltip("The footprint radius the props were laid out for: the ring stays put for previews this size or smaller.")]
        [SerializeField] float m_ClearRadius = 3f;

        [Tooltip("Room kept between the preview's footprint and the nearest prop.")]
        [SerializeField] float m_Margin = 1f;

        [Tooltip("Seconds the props take to slide to their new places.")]
        [SerializeField] float m_Ease = 0.6f;

        [SerializeField] List<Prop> m_Props = new();

        float m_Push, m_From, m_To, m_Started = -1f;

        public IReadOnlyList<Prop> Props => m_Props;

        public void SetProps(IEnumerable<Prop> props, float clearRadius)
        {
            m_Props = new List<Prop>(props);
            m_ClearRadius = clearRadius;
        }

        void OnEnable() => PreviewLoader.Shown += OnShown;
        void OnDisable() => PreviewLoader.Shown -= OnShown;

        void OnShown(GameObject instance, Bounds bounds)
        {
            // The footprint: the farthest corner of the bounds from the vertical axis through the origin.
            var x = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x));
            var z = Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
            var footprint = Mathf.Sqrt(x * x + z * z);
            m_From = m_Push;
            m_To = Mathf.Max(0f, footprint + m_Margin - m_ClearRadius);
            m_Started = Time.time;
        }

        void Update()
        {
            if (m_Started < 0f) return;
            var t = m_Ease <= 0f ? 1f : Mathf.Clamp01((Time.time - m_Started) / m_Ease);
            m_Push = Mathf.Lerp(m_From, m_To, t * t * (3f - 2f * t));
            foreach (var p in m_Props)
            {
                if (p.Transform == null) continue;
                var flat = new Vector3(p.Home.x, 0f, p.Home.z);
                var dir = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.zero;
                p.Transform.position = p.Home + dir * m_Push;
            }
            if (t >= 1f) m_Started = -1f;
        }
    }
}
