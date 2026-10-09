using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Pipeline.Gameplay
{
    // A first-person player: walk (WASD), run (Shift), jump (Space), look with the mouse. A click captures
    // the mouse; Esc gives it back (the browser does that on WebGL, the editor and desktop players here).
    // Needs a CharacterController on the same object and a camera as a child at eye height.
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [SerializeField] Transform m_Camera;
        [SerializeField] float m_WalkSpeed = 4.5f;
        [SerializeField] float m_RunSpeed = 8.5f;
        [SerializeField] float m_JumpHeight = 1.2f;
        [SerializeField] float m_Gravity = 20f;
        [SerializeField] float m_LookSensitivity = 0.12f;
        [Tooltip("Speeds change this fast (m/s²): a little inertia so starts and stops aren't instant.")]
        [SerializeField] float m_Acceleration = 40f;

        CharacterController m_Body;
        Vector3 m_Velocity;
        float m_Pitch;
        Vector3 m_Spawn;

        public bool Captured => Cursor.lockState == CursorLockMode.Locked;

        void Awake()
        {
            m_Body = GetComponent<CharacterController>();
            if (m_Camera == null && GetComponentInChildren<Camera>() is { } cam) m_Camera = cam.transform;
            if (m_Camera != null)
            {
                var x = m_Camera.localEulerAngles.x;
                m_Pitch = x > 180f ? x - 360f : x;
            }
            m_Spawn = transform.position;
        }

        void Update()
        {
            var mouse = Mouse.current;
            var keys = Keyboard.current;
            if (mouse == null || keys == null) return;

            if (!Captured)
            {
                if (mouse.leftButton.wasPressedThisFrame) Cursor.lockState = CursorLockMode.Locked;
            }
            else
            {
                if (keys.escapeKey.wasPressedThisFrame) Cursor.lockState = CursorLockMode.None;
                var look = mouse.delta.ReadValue() * m_LookSensitivity;
                transform.Rotate(0f, look.x, 0f, Space.World);
                m_Pitch = Mathf.Clamp(m_Pitch - look.y, -85f, 85f);
                if (m_Camera != null) m_Camera.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);
            }

            // Moving only while captured: the keys belong to the page otherwise.
            var input = Captured
                ? new Vector2((keys.dKey.isPressed ? 1 : 0) - (keys.aKey.isPressed ? 1 : 0), (keys.wKey.isPressed ? 1 : 0) - (keys.sKey.isPressed ? 1 : 0))
                : Vector2.zero;
            var run = keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed;
            var target = transform.TransformDirection(new Vector3(input.x, 0f, input.y)).normalized * (run ? m_RunSpeed : m_WalkSpeed);
            var horizontal = Vector3.MoveTowards(new Vector3(m_Velocity.x, 0f, m_Velocity.z), target, m_Acceleration * Time.deltaTime);
            m_Velocity.x = horizontal.x;
            m_Velocity.z = horizontal.z;

            if (m_Body.isGrounded)
            {
                m_Velocity.y = -2f;   // keeps it snapped to slopes and steps
                if (Captured && keys.spaceKey.wasPressedThisFrame) m_Velocity.y = Mathf.Sqrt(2f * m_JumpHeight * m_Gravity);
            }
            else m_Velocity.y -= m_Gravity * Time.deltaTime;

            m_Body.Move(m_Velocity * Time.deltaTime);

            if (transform.position.y < m_Spawn.y - 100f)   // fell off the world
            {
                m_Body.enabled = false;
                transform.position = m_Spawn;
                m_Body.enabled = true;
                m_Velocity = Vector3.zero;
            }
        }

        void OnDisable()
        {
            if (Captured) Cursor.lockState = CursorLockMode.None;
        }
    }
}
