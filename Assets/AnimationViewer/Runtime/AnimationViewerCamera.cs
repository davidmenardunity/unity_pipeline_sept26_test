using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Pipeline.AnimationViewer
{
    // Orbit camera for the animation viewer: left- or right-drag orbits, middle-drag (or Shift + drag) pans,
    // the wheel zooms.
    public class AnimationViewerCamera : MonoBehaviour
    {
        [SerializeField] Vector3 m_Pivot = new(0f, 1f, 0f);
        [SerializeField] float m_Distance = 4f;
        [SerializeField] float m_Yaw = 200f;
        [SerializeField] float m_Pitch = 12f;
        [SerializeField] float m_OrbitSpeed = 0.25f;

        void LateUpdate()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                var shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
                var drag = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
                if (drag && !shift)
                {
                    m_Yaw += delta.x * m_OrbitSpeed;
                    m_Pitch = Mathf.Clamp(m_Pitch - delta.y * m_OrbitSpeed, -20f, 89f);
                }
                else if (mouse.middleButton.isPressed || (drag && shift))
                {
                    var scale = m_Distance * 0.0015f;
                    m_Pivot -= (transform.right * delta.x + transform.up * delta.y) * scale;
                }
                var scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    m_Distance = Mathf.Clamp(m_Distance * Mathf.Pow(0.9f, Mathf.Sign(scroll)), 0.3f, 500f);
            }
            transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            transform.position = m_Pivot - transform.forward * m_Distance;
        }

        /// <summary>Look at these bounds from far enough to see all of them.</summary>
        public void Frame(Bounds bounds)
        {
            m_Pivot = bounds.center;
            var cam = GetComponent<Camera>();
            var fov = (cam != null ? cam.fieldOfView : 60f) * Mathf.Deg2Rad;
            m_Distance = Mathf.Clamp(bounds.extents.magnitude / Mathf.Sin(fov * 0.5f) * 1.1f, 1f, 500f);
        }
    }
}
