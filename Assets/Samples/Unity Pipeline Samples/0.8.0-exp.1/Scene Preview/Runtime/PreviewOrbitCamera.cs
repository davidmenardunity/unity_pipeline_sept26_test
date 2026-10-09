using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Orbits its camera around a pivot with a clamped zoom range, so the visible area stays bounded.
    // One-finger drag orbits, two-finger pinch zooms, and after a short idle it slowly auto-spins so
    // the scene reads as live. Mouse drag + scroll wheel mirror the touch controls in the editor. Uses
    // legacy UnityEngine.Input; it is the sole writer of the camera transform, so PreviewLoader hands it
    // new framing via Frame() rather than moving the camera itself.
    [RequireComponent(typeof(Camera))]
    public class PreviewOrbitCamera : MonoBehaviour
    {
        [SerializeField] Vector3 m_Pivot = new(0f, 1.5f, 0f);
        [SerializeField] float m_Distance = 20f;
        [SerializeField] float m_Yaw = 25f;
        [SerializeField] float m_Pitch = 27f;

        [Header("Limits")]
        [SerializeField] float m_MinPitch = 18f;
        [SerializeField] float m_MaxPitch = 70f;
        [SerializeField] float m_MinDistance = 9f;
        [SerializeField] float m_MaxDistance = 24f;

        [Header("Speeds")]
        [SerializeField] float m_OrbitSpeed = 0.2f;
        [SerializeField] float m_PinchZoomSpeed = 0.01f;
        [SerializeField] float m_ScrollZoomSpeed = 2f;
        [SerializeField] float m_AutoSpinSpeed = 6f;
        [SerializeField] float m_IdleBeforeAutoSpin = 4f;

        Camera m_Camera;
        float m_IdleTimer;
        float m_LastPinchDistance;
        Vector3 m_LastMousePosition;

        void Awake() => m_Camera = GetComponent<Camera>();

        /// <summary>
        /// While set, the left button and one-finger drags belong to someone else (annotation pins and
        /// drawings): only the wheel and pinch still zoom, and the camera doesn't auto-spin.
        /// </summary>
        public bool Hold { get; set; }

        bool m_Parked;   // a viewpoint was set from outside: no auto-spin until the user moves the camera

        void LateUpdate()
        {
            bool interacted = HandleTouch() || HandleMouse();
            if (interacted) m_Parked = false;

            m_IdleTimer = interacted || Hold || m_Parked ? 0f : m_IdleTimer + Time.deltaTime;
            if (m_IdleTimer >= m_IdleBeforeAutoSpin)
                m_Yaw += m_AutoSpinSpeed * Time.deltaTime;

            Apply();
        }

        /// <summary>The point the camera orbits.</summary>
        public Vector3 Pivot => m_Pivot;

        /// <summary>
        /// Look at <paramref name="pivot"/> from <paramref name="position"/> (e.g. a saved viewpoint), and
        /// stay there until the user moves the camera.
        /// </summary>
        public void LookFrom(Vector3 position, Vector3 pivot)
        {
            var offset = position - pivot;
            if (offset.sqrMagnitude < 1e-6f) return;
            m_Pivot = pivot;
            m_Distance = Mathf.Clamp(offset.magnitude, m_MinDistance * 0.25f, m_MaxDistance * 2f);
            var dir = -offset.normalized;   // from the camera toward the pivot
            m_Pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
            m_Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            m_Parked = true;
            Apply();
        }

#if ENABLE_INPUT_SYSTEM
        // Input System (the project's active input handling). The legacy UnityEngine.Input branch below
        // throws an InvalidOperationException every frame when only the Input System is enabled.
        bool HandleTouch()
        {
            var screen = UnityEngine.InputSystem.Touchscreen.current;
            if (screen == null)
                return false;

            UnityEngine.InputSystem.Controls.TouchControl first = null, second = null;
            var active = 0;
            foreach (var touch in screen.touches)
            {
                if (!touch.isInProgress)
                    continue;
                if (active == 0) first = touch;
                else if (active == 1) second = touch;
                active++;
            }

            if (active == 1 && !Hold)
            {
                var delta = first.delta.ReadValue();
                m_Yaw += delta.x * m_OrbitSpeed;
                m_Pitch -= delta.y * m_OrbitSpeed;
                m_Pitch = Mathf.Clamp(m_Pitch, m_MinPitch, m_MaxPitch);
                return true;
            }

            if (active == 2)
            {
                float pinch = (first.position.ReadValue() - second.position.ReadValue()).magnitude;
                if (first.press.wasPressedThisFrame || second.press.wasPressedThisFrame)
                    m_LastPinchDistance = pinch;
                Zoom((m_LastPinchDistance - pinch) * m_PinchZoomSpeed);
                m_LastPinchDistance = pinch;
                return true;
            }

            return false;
        }

        bool HandleMouse()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
                return false;
            bool interacted = false;

            Vector3 position = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
                m_LastMousePosition = position;
            if (mouse.leftButton.isPressed && !Hold)
            {
                var delta = position - m_LastMousePosition;
                m_LastMousePosition = position;
                m_Yaw += delta.x * m_OrbitSpeed;
                m_Pitch -= delta.y * m_OrbitSpeed;
                m_Pitch = Mathf.Clamp(m_Pitch, m_MinPitch, m_MaxPitch);
                interacted = true;
            }

            // The Input System reports scroll in the platform's units (120 per wheel notch on Windows);
            // the legacy Input.mouseScrollDelta was one per notch.
            float scroll = mouse.scroll.ReadValue().y / 120f;
            if (!Mathf.Approximately(scroll, 0f))
            {
                Zoom(-scroll * m_ScrollZoomSpeed);
                interacted = true;
            }

            return interacted;
        }
#else
        bool HandleTouch()
        {
            if (Input.touchCount == 1)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Moved)
                {
                    m_Yaw += touch.deltaPosition.x * m_OrbitSpeed;
                    m_Pitch -= touch.deltaPosition.y * m_OrbitSpeed;
                    m_Pitch = Mathf.Clamp(m_Pitch, m_MinPitch, m_MaxPitch);
                }
                return true;
            }

            if (Input.touchCount == 2)
            {
                float pinch = (Input.GetTouch(0).position - Input.GetTouch(1).position).magnitude;
                if (Input.GetTouch(1).phase == TouchPhase.Began || Input.GetTouch(0).phase == TouchPhase.Began)
                    m_LastPinchDistance = pinch;
                Zoom((m_LastPinchDistance - pinch) * m_PinchZoomSpeed);
                m_LastPinchDistance = pinch;
                return true;
            }

            return false;
        }

        bool HandleMouse()
        {
            bool interacted = false;

            if (Input.GetMouseButtonDown(0))
                m_LastMousePosition = Input.mousePosition;
            if (Input.GetMouseButton(0))
            {
                var delta = Input.mousePosition - m_LastMousePosition;
                m_LastMousePosition = Input.mousePosition;
                m_Yaw += delta.x * m_OrbitSpeed;
                m_Pitch -= delta.y * m_OrbitSpeed;
                m_Pitch = Mathf.Clamp(m_Pitch, m_MinPitch, m_MaxPitch);
                interacted = true;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (!Mathf.Approximately(scroll, 0f))
            {
                Zoom(-scroll * m_ScrollZoomSpeed);
                interacted = true;
            }

            return interacted;
        }
#endif

        void Zoom(float amount)
            => m_Distance = Mathf.Clamp(m_Distance + amount, Mathf.Min(m_MinDistance, m_Distance), Mathf.Max(m_MaxDistance, m_Distance));

        void Apply()
        {
            var rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            transform.position = m_Pivot + rotation * (Vector3.back * m_Distance);
            transform.rotation = Quaternion.LookRotation(m_Pivot - transform.position, Vector3.up);
        }

        /// <summary>Position the camera for a still (Play mode drives it live via LateUpdate).</summary>
        public void ApplyNow() => Apply();

        /// <summary>Recenter the orbit on the given world-space bounds and back off far enough to frame them.</summary>
        public void Frame(Bounds worldBounds)
        {
            m_Pivot = worldBounds.center;
            if (m_Camera == null)
                m_Camera = GetComponent<Camera>();
            float radius = Mathf.Max(0.01f, worldBounds.extents.magnitude);
            float fov = (m_Camera != null ? m_Camera.fieldOfView : 45f) * Mathf.Deg2Rad;
            float framed = radius / Mathf.Max(0.01f, Mathf.Sin(fov * 0.5f));
            m_Distance = Mathf.Clamp(framed, m_MinDistance, m_MaxDistance);
        }
    }
}
