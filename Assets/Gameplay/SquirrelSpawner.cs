using UnityEngine;

namespace Unity.Pipeline.Gameplay
{
    // Scatters squirrels over the ground around this object when the scene starts: random spots within the
    // radius where there's walkable ground (terrain, roads, pavement; not roofs or walls).
    public class SquirrelSpawner : MonoBehaviour
    {
        [SerializeField] GameObject m_Prefab;
        [SerializeField] int m_Count = 120;
        [SerializeField] float m_Radius = 45f;
        [SerializeField] float m_MinScale = 0.9f;
        [SerializeField] float m_MaxScale = 1.25f;

        void Start()
        {
            if (m_Prefab == null) return;
            var placed = 0;
            for (var attempt = 0; attempt < m_Count * 10 && placed < m_Count; attempt++)
            {
                var offset = Random.insideUnitCircle * m_Radius;
                var top = transform.position + new Vector3(offset.x, 60f, offset.y);
                if (!Physics.Raycast(top, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) continue;
                // Ground only: level enough, and not the top of something tall (a roof, a car).
                if (hit.normal.y < 0.85f || hit.point.y > transform.position.y + 3f) continue;
                var squirrel = Instantiate(m_Prefab, hit.point, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
                squirrel.transform.localScale *= Random.Range(m_MinScale, m_MaxScale);
                squirrel.name = $"Squirrel {placed + 1}";
                placed++;
            }
        }
    }
}
