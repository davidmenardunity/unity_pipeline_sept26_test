using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Pipeline.SceneViewer
{
    // First-person play in a loaded scene. Click the view to capture the mouse (the browser's pointer lock);
    // Esc releases it. While captured: the mouse looks, WASD moves, Shift runs, Space jumps, F switches
    // between walking (gravity and collisions, a CharacterController) and flying (Space/Q up and down).
    // Walking starts on the ground under the scene's camera; with no ground there, it flies.
    public class ScenePlayer : MonoBehaviour
    {
        public float WalkSpeed = 4.5f;
        public float RunSpeed = 9f;
        public float FlySpeed = 12f;
        public float LookSpeed = 0.12f;
        public float JumpSpeed = 5f;
        public float Gravity = 18f;
        public float EyeHeight = 1.65f;

        Camera m_Camera;
        Transform m_OriginalParent;
        Vector3 m_OriginalPosition;
        Quaternion m_OriginalRotation;
        CharacterController m_Body;
        Vector3 m_Spawn;
        float m_Yaw, m_Pitch, m_VerticalSpeed;
        bool m_Fly, m_Locked;
        Action<string> m_Report;

        /// <summary>Put a player rig where <paramref name="camera"/> stands and hand the camera to it.</summary>
        public static ScenePlayer Attach(Camera camera, Action<string> report)
        {
            var rig = new GameObject("ScenePlayer");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rig, camera.gameObject.scene);
            var player = rig.AddComponent<ScenePlayer>();
            player.m_Report = report;
            player.Take(camera);
            return player;
        }

        void Take(Camera camera)
        {
            m_Camera = camera;
            var t = camera.transform;
            m_OriginalParent = t.parent;
            m_OriginalPosition = t.position;
            m_OriginalRotation = t.rotation;
            var euler = t.rotation.eulerAngles;
            m_Yaw = euler.y;
            m_Pitch = euler.x > 180f ? euler.x - 360f : euler.x;

            // Stand on the ground under the camera if there is some; otherwise fly from where it is.
            var start = t.position;
            m_Fly = !Physics.Raycast(start + Vector3.up * 0.5f, Vector3.down, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore);
            transform.position = m_Fly ? start : hit.point + Vector3.up * 0.05f;
            m_Spawn = transform.position;

            m_Body = gameObject.AddComponent<CharacterController>();
            m_Body.height = EyeHeight + 0.15f;
            m_Body.radius = 0.35f;
            m_Body.center = new Vector3(0f, m_Body.height / 2f, 0f);
            m_Body.stepOffset = 0.45f;
            m_Body.enabled = !m_Fly;

            t.SetParent(transform, true);
            t.localPosition = m_Fly ? Vector3.zero : new Vector3(0f, EyeHeight, 0f);
            Apply();
        }

        /// <summary>Give the camera back as it was, and remove the rig.</summary>
        public void Detach()
        {
            Cursor.lockState = CursorLockMode.None;
            if (m_Camera != null)
            {
                var t = m_Camera.transform;
                t.SetParent(m_OriginalParent, true);
                t.SetPositionAndRotation(m_OriginalPosition, m_OriginalRotation);
            }
            Destroy(gameObject);
        }

        void Update()
        {
            var mouse = Mouse.current;
            var keys = Keyboard.current;
            if (mouse == null || keys == null) return;

            // Capture on click; the browser releases on Esc (Unity sees lockState go back to None).
            if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame)
                Cursor.lockState = CursorLockMode.Locked;
            var locked = Cursor.lockState == CursorLockMode.Locked;
            if (locked != m_Locked)
            {
                m_Locked = locked;
                m_Report?.Invoke(locked ? "captured" : "released");
            }
            if (!locked) return;

            var look = mouse.delta.ReadValue() * LookSpeed;
            m_Yaw += look.x;
            m_Pitch = Mathf.Clamp(m_Pitch - look.y, -85f, 85f);

            if (keys.fKey.wasPressedThisFrame) SetFly(!m_Fly);

            var input = new Vector3((keys.dKey.isPressed ? 1 : 0) - (keys.aKey.isPressed ? 1 : 0), 0f,
                (keys.wKey.isPressed ? 1 : 0) - (keys.sKey.isPressed ? 1 : 0));
            input = Vector3.ClampMagnitude(input, 1f);
            var run = keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed;

            if (m_Fly)
            {
                var look3 = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
                var up = (keys.spaceKey.isPressed || keys.eKey.isPressed ? 1 : 0) - (keys.qKey.isPressed || keys.cKey.isPressed ? 1 : 0);
                transform.position += (look3 * input + Vector3.up * up) * (FlySpeed * (run ? 3f : 1f) * Time.deltaTime);
            }
            else
            {
                var heading = Quaternion.Euler(0f, m_Yaw, 0f);
                var move = heading * input * (run ? RunSpeed : WalkSpeed);
                if (m_Body.isGrounded)
                {
                    m_VerticalSpeed = -1f;
                    if (keys.spaceKey.wasPressedThisFrame) m_VerticalSpeed = JumpSpeed;
                }
                else m_VerticalSpeed -= Gravity * Time.deltaTime;
                move.y = m_VerticalSpeed;
                m_Body.Move(move * Time.deltaTime);
                if (transform.position.y < m_Spawn.y - 100f)   // fell off the world
                {
                    m_Body.enabled = false;
                    transform.position = m_Spawn;
                    m_Body.enabled = true;
                    m_VerticalSpeed = 0f;
                }
            }
            Apply();
        }

        void SetFly(bool fly)
        {
            m_Fly = fly;
            m_Body.enabled = !fly;
            m_VerticalSpeed = 0f;
            m_Camera.transform.localPosition = fly ? Vector3.zero : new Vector3(0f, EyeHeight, 0f);
            if (fly) transform.position += Vector3.up * EyeHeight;
            else transform.position -= Vector3.up * EyeHeight;
            m_Report?.Invoke(fly ? "flying" : "walking");
        }

        void Apply() => m_Camera.transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);

        void OnDestroy()
        {
            if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = CursorLockMode.None;
        }
    }
}
