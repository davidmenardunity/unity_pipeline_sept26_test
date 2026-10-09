using UnityEngine;

namespace Unity.Pipeline.Gameplay
{
    // A squirrel: twitchy, never still for long. It idles, snaps its head around, sits up on its haunches,
    // darts off in short zig-zag bursts and hops. When the player comes near it freezes for a beat, then
    // bolts away, zig-zagging, and sits up to watch from a safe distance.
    //
    // It walks on whatever ground is under it (terrain, roads, pavement), turns back at walls and steep
    // drops, and stays within a home range around where it started. Animations are the model's legacy clips:
    // Idle, Look, Alert, Run, Jump.
    public class Squirrel : MonoBehaviour
    {
        [SerializeField] float m_DartSpeed = 3.5f;
        [SerializeField] float m_FleeSpeed = 7f;
        [SerializeField] float m_FearDistance = 7f;
        [SerializeField] float m_SafeDistance = 14f;
        [SerializeField] float m_HomeRange = 30f;
        [SerializeField] float m_MaxStep = 0.5f;

        enum State { Idle, Look, Alert, Dart, Hop, Freeze, Flee }

        Animation m_Animation;
        State m_State;
        float m_Until, m_NextTurn, m_Speed, m_HopStart;
        Vector3 m_Heading = Vector3.forward, m_Home;
        static Transform s_Player;
        static int s_PlayerFrame = -1;

        void Awake()
        {
            m_Animation = GetComponentInChildren<Animation>();   // on the model, under this object
            foreach (AnimationState s in m_Animation) s.wrapMode = s.name == "Jump" ? WrapMode.ClampForever : WrapMode.Loop;
        }

        void Start()
        {
            if (m_Animation == null) { enabled = false; return; }
            m_Home = transform.position;
            m_Heading = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            transform.rotation = Quaternion.LookRotation(m_Heading);
            Enter(RandomCalm());
            // Not all in step: start each clip at a random point.
            foreach (AnimationState s in m_Animation) s.normalizedTime = Random.value;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            var player = Player();
            var toPlayer = player != null ? Flat(transform.position - player.position) : Vector3.positiveInfinity;
            var distance = player != null ? toPlayer.magnitude : float.PositiveInfinity;

            // Danger: freeze first (a beat, sometimes none), then bolt.
            if (distance < m_FearDistance && m_State != State.Flee && m_State != State.Freeze)
                Enter(Random.value < 0.6f ? State.Freeze : State.Flee);

            switch (m_State)
            {
                case State.Dart:
                    if (Time.time > m_NextTurn) Turn(Random.Range(-70f, 70f), Random.Range(0.2f, 0.6f));
                    Move(m_Speed, dt);
                    break;
                case State.Flee:
                    if (Time.time > m_NextTurn)
                    {
                        // Away from the player, zig-zagging.
                        var away = toPlayer.sqrMagnitude > 0.01f ? toPlayer.normalized : m_Heading;
                        m_Heading = Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f) * away;
                        m_NextTurn = Time.time + Random.Range(0.15f, 0.4f);
                    }
                    Move(m_Speed, dt);
                    if (distance > m_SafeDistance && Time.time > m_Until) Enter(State.Alert);
                    break;
                case State.Hop:
                    var t = (Time.time - m_HopStart) / 0.7f;
                    if (t > 0.15f && t < 0.75f) Move(2.5f, dt);
                    break;
                case State.Freeze:
                    if (Time.time > m_Until) Enter(State.Flee);
                    break;
            }

            if (Time.time > m_Until && m_State != State.Flee && m_State != State.Freeze) Enter(Next());
        }

        // What to do next: mostly small things, now and then a dash.
        State Next()
        {
            var r = Random.value;
            if (m_State == State.Dart) return r < 0.5f ? State.Look : r < 0.75f ? State.Alert : r < 0.9f ? State.Idle : State.Hop;
            return r < 0.45f ? State.Dart : r < 0.6f ? State.Hop : RandomCalm();
        }

        static State RandomCalm()
        {
            var r = Random.value;
            return r < 0.4f ? State.Look : r < 0.7f ? State.Idle : State.Alert;
        }

        void Enter(State state)
        {
            m_State = state;
            switch (state)
            {
                case State.Idle: Play("Idle", 0.15f); m_Until = Time.time + Random.Range(0.4f, 1.6f); break;
                case State.Look: Play("Look", 0.1f); m_Until = Time.time + Random.Range(0.8f, 2.2f); break;
                case State.Alert: Play("Alert", 0.15f); m_Until = Time.time + Random.Range(0.8f, 2.5f); break;
                case State.Freeze: Play("Alert", 0.08f); m_Until = Time.time + Random.Range(0.15f, 0.6f); break;
                case State.Hop:
                    Turn(Random.Range(-60f, 60f), 10f);
                    m_HopStart = Time.time;
                    m_Animation.Stop("Jump");
                    Play("Jump", 0.05f);
                    m_Until = Time.time + 0.75f;
                    break;
                case State.Dart:
                    // Home calls when it's wandered off.
                    if (Flat(transform.position - m_Home).magnitude > m_HomeRange)
                        m_Heading = Flat(m_Home - transform.position).normalized;
                    else Turn(Random.Range(-150f, 150f), 0f);
                    m_Speed = m_DartSpeed * Random.Range(0.7f, 1.3f);
                    m_NextTurn = Time.time + Random.Range(0.2f, 0.6f);
                    m_Until = Time.time + Random.Range(0.3f, 1.4f);
                    Play("Run", 0.05f, m_Speed / m_DartSpeed * 1.6f);
                    break;
                case State.Flee:
                    m_Speed = m_FleeSpeed * Random.Range(0.85f, 1.15f);
                    m_NextTurn = 0f;
                    m_Until = Time.time + Random.Range(1.5f, 3f);
                    Play("Run", 0.05f, m_Speed / m_DartSpeed * 1.6f);
                    break;
            }
        }

        void Turn(float degrees, float holdFor)
        {
            m_Heading = Quaternion.Euler(0f, degrees, 0f) * m_Heading;
            m_NextTurn = Time.time + holdFor;
        }

        // A step along the heading, kept on the ground; walls, cliffs and drops send it the other way.
        void Move(float speed, float dt)
        {
            var from = transform.position;
            var to = from + m_Heading * (speed * dt);
            if (!Ground(to, from.y, out var y) || Mathf.Abs(y - from.y) > m_MaxStep)
            {
                m_Heading = Quaternion.Euler(0f, Random.Range(120f, 240f), 0f) * m_Heading;
                return;
            }
            transform.position = new Vector3(to.x, y, to.z);
            var look = Quaternion.LookRotation(m_Heading);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, 900f * dt);
        }

        bool Ground(Vector3 at, float near, out float y)
        {
            if (Physics.Raycast(new Vector3(at.x, near + 1.5f, at.z), Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)
                && hit.normal.y > 0.55f)
            {
                y = hit.point.y;
                return true;
            }
            y = near;
            return false;
        }

        void Play(string clip, float fade, float speed = 1f)
        {
            if (m_Animation[clip] == null) return;
            m_Animation[clip].speed = speed;
            m_Animation.CrossFade(clip, fade);
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        // The player: the first-person controller if the scene has one, else the main camera. Looked up once a frame for all squirrels.
        static Transform Player()
        {
            if (s_PlayerFrame == Time.frameCount) return s_Player;
            s_PlayerFrame = Time.frameCount;
            if (s_Player == null)
            {
                var fps = FindAnyObjectByType<FirstPersonController>();
                s_Player = fps != null ? fps.transform : Camera.main != null ? Camera.main.transform : null;
            }
            return s_Player;
        }
    }
}
