using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Content;
using Unity.IO.Archive;
using Unity.Loading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Runtime consumer of com.unity.pipeline content archives (.ca). Double-buffered: two slots hold
    // the shown preview and the one being prepared, so the next preview is downloaded, mounted, loaded
    // and instantiated (disabled) in the free slot while the current one stays on screen — the swap
    // is a SetActive toggle once the incoming instance is ready. A request that arrives before the
    // swap interrupts the in-flight prep and reuses the same free slot, leaving the shown preview
    // untouched, so a slow or failed load never blanks the pedestal.
    //
    // Each load gets a fresh download file and archive mount prefix (preview_{seq}.ca / preview{seq}:):
    // remounting a mount prefix that was previously mounted and unmounted in a ContentNamespace yields
    // an archive whose LoadContentFileAsync never completes, so prefixes must never be reused.
    public class PreviewLoader : MonoBehaviour
    {
        // Fallback content-file name. The producer names the content file per-asset and records that
        // name in the archive manifest (ManifestData.contentFileName), which the loader reads below;
        // this const only applies to an older archive whose manifest omits the name.
        public const string ContentFileName = "previewcontent";

        // Surface a loaded Material is shown on; falls back to a primitive sphere when unassigned.
        [SerializeField] GameObject m_MaterialDisplayMesh;

        // World Y the previewed object rests on (e.g. the pedestal top), so previews sit on the stand.
        [SerializeField] float m_GroundY;

        class Slot
        {
            public string Path;
            public string MountPrefix;
            public ArchiveHandle Archive;
            public bool HasArchive;
            public ContentFile ContentFile;
            public bool HasContentFile;
            public GameObject Instance;
        }

        Transform m_Root;
        Slot[] m_Slots;
        int m_Active = -1;
        int m_Pending = -1;
        int m_Seq;
        Coroutine m_Routine;
        bool m_Preparing;

        void Awake()
        {
            m_Root = new GameObject("PreviewRoot").transform;
            m_Root.SetParent(transform, false);
            m_Slots = new[] { new Slot(), new Slot() };
        }

        /// <summary>True once a preview instance is shown under the preview root.</summary>
        public bool HasPreview => m_Active >= 0 && m_Slots[m_Active].Instance != null;

        /// <summary>True while a preview is being downloaded/prepared, before it swaps into view.</summary>
        public bool IsPreparing => m_Preparing;

        public enum Outcome { None, Shown, Failed }

        /// <summary>How the most recent request ended; None while it is still in flight.</summary>
        public Outcome LastOutcome { get; private set; }

        /// <summary>Why the most recent request failed, when LastOutcome is Failed.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Whether an asset is worth requesting: the loader presents GameObjects (models, prefabs) and
        /// Materials, so other asset types would produce an archive with nothing to show.
        /// </summary>
        public static bool CanPresent(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return false;
            var p = nameOrPath.ToLowerInvariant();
            return p.EndsWith(".mat") || p.EndsWith(".prefab") || p.EndsWith(".fbx") || p.EndsWith(".obj");
        }

        /// <summary>
        /// Reserve the free slot for a new preview and return the file path to download into. Cancels
        /// any in-flight prep and reuses the same free slot, leaving the shown preview untouched.
        /// </summary>
        public string AcquireSlot()
        {
            if (m_Routine != null)
            {
                StopCoroutine(m_Routine);
                m_Routine = null;
            }

            int free = 1 - Mathf.Max(m_Active, 0);
            TeardownSlot(free);
            m_Seq++;
            var slot = m_Slots[free];
            slot.Path = Path.Combine(Application.persistentDataPath, $"preview_{m_Seq}.ca");
            slot.MountPrefix = $"preview{m_Seq}:";
            m_Pending = free;
            m_Preparing = true;
            LastOutcome = Outcome.None;
            LastError = null;
            return slot.Path;
        }

        /// <summary>Mount, load and swap in the archive just downloaded to the acquired slot.</summary>
        public void PrepareAndSwap() => m_Routine = StartCoroutine(PrepareRoutine());

        /// <summary>Release the acquired slot without swapping (e.g. the download failed).</summary>
        public void AbandonSlot(string reason)
        {
            if (m_Pending >= 0)
                TeardownSlot(m_Pending);
            m_Pending = -1;
            m_Preparing = false;
            LastOutcome = Outcome.Failed;
            LastError = reason;
        }

        System.Collections.IEnumerator PrepareRoutine()
        {
            int p = m_Pending;
            var slot = m_Slots[p];

            if (string.IsNullOrEmpty(slot.Path) || !File.Exists(slot.Path))
            {
                FailPending(p, $"archive not found: '{slot.Path}'");
                yield break;
            }

            slot.Archive = ArchiveFileInterface.MountAsync(ContentNamespace.Default, slot.Path, slot.MountPrefix);
            slot.HasArchive = true;
            slot.Archive.JobHandle.Complete();
            if (slot.Archive.Status != ArchiveStatus.Complete)
            {
                FailPending(p, $"failed to mount archive '{slot.Path}' (status {slot.Archive.Status})");
                yield break;
            }

            var mountPath = slot.Archive.GetMountPath();
            // The archive's manifest records the content-file name (the producer names it per-asset)
            // and whether this asset references engine built-ins; supply the GlobalTableDependency slot
            // iff so — the count is exact (too many faults as hard as too few).
            var manifest = ContentArchiveManifest.ReadManifest(mountPath);
            var contentFileName = string.IsNullOrEmpty(manifest.contentFileName) ? ContentFileName : manifest.contentFileName;
            var vfsPath = Path.Combine(mountPath, contentFileName);
            var requiresDefaults = manifest.requiresDefaultResources;
            var deps = new NativeArray<ContentFile>(requiresDefaults ? 1 : 0, Allocator.Temp);
            if (requiresDefaults)
                deps[0] = ContentFile.GlobalTableDependency;
            slot.ContentFile = ContentLoadInterface.LoadContentFileAsync(ContentNamespace.Default, vfsPath, deps);
            slot.HasContentFile = true;
            deps.Dispose();

            while (slot.ContentFile.LoadingStatus == LoadingStatus.InProgress)
                yield return null;

            if (slot.ContentFile.LoadingStatus != LoadingStatus.Completed)
            {
                FailPending(p, $"content file failed to load from '{slot.Path}' (status {slot.ContentFile.LoadingStatus})");
                yield break;
            }

            var objects = slot.ContentFile.GetObjects();
            Object primary = objects.OfType<GameObject>().FirstOrDefault()
                ?? (Object)objects.OfType<Material>().FirstOrDefault()
                ?? objects.FirstOrDefault();
            if (primary == null)
            {
                FailPending(p, "the archive has no object to show");
                yield break;
            }

            GameObject incoming = BuildInstance(primary);
            if (incoming == null)
            {
                FailPending(p, $"'{primary.name}' is a {primary.GetType().Name}; only models and materials can be shown");
                yield break;
            }

            slot.Instance = incoming;
            Swap(p, incoming);
        }

        // Instantiate the primary object under the preview root, strip stowaway cameras/lights, and
        // leave it disabled so the swap can reveal it in a single frame. Returns null for unsupported
        // object types.
        GameObject BuildInstance(Object primary)
        {
            GameObject instance;
            switch (primary)
            {
                case GameObject go:
                    instance = Instantiate(go, m_Root, false);
                    break;
                case Material mat:
                    instance = m_MaterialDisplayMesh != null
                        ? Instantiate(m_MaterialDisplayMesh, m_Root, false)
                        : GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    instance.transform.SetParent(m_Root, false);
                    foreach (var r in instance.GetComponentsInChildren<Renderer>())
                        r.sharedMaterial = mat;
                    break;
                default:
                    return null;
            }

            foreach (var cam in instance.GetComponentsInChildren<Camera>())
                if (cam) Destroy(cam);
            foreach (var light in instance.GetComponentsInChildren<Light>())
                if (light) Destroy(light);

            instance.SetActive(false);
            return instance;
        }

        void Swap(int p, GameObject incoming)
        {
            int old = m_Active;
            incoming.SetActive(true);
            PlaceAndFrame(incoming);
            if (old >= 0 && old != p && m_Slots[old].Instance != null)
                m_Slots[old].Instance.SetActive(false);
            m_Active = p;
            m_Pending = -1;
            m_Preparing = false;
            m_Routine = null;
            LastOutcome = Outcome.Shown;
            Debug.Log($"[Preview] swapped in slot {p}: '{incoming.name}'");
        }

        void FailPending(int p, string reason)
        {
            Debug.LogError($"[Preview] {reason}");
            LastOutcome = Outcome.Failed;
            LastError = reason;
            TeardownSlot(p);
            if (m_Pending == p)
            {
                m_Pending = -1;
                m_Preparing = false;
            }
            m_Routine = null;
        }

        void PlaceAndFrame(GameObject instance)
        {
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            var bounds = GetCompoundBounds(instance);
            if (bounds.size.magnitude > 0)
            {
                // Center on XZ, rest the bottom on the ground plane (the pedestal top).
                instance.transform.position += new Vector3(-bounds.center.x, m_GroundY - bounds.min.y, -bounds.center.z);
                bounds = GetCompoundBounds(instance);
                FrameCamera(bounds);
            }
        }

        static void FrameCamera(Bounds bounds)
        {
            var cam = Camera.main;
            if (cam == null)
                return;

            // The orbit camera is the sole writer of the camera transform, so hand it the new framing
            // instead of moving the camera here (which would fight its per-frame update).
            var orbit = cam.GetComponent<PreviewOrbitCamera>();
            if (orbit != null)
            {
                orbit.Frame(bounds);
                return;
            }

            float radius = bounds.extents.magnitude;
            float fov = cam.fieldOfView * Mathf.Deg2Rad;
            float distance = radius / Mathf.Max(0.01f, Mathf.Sin(fov * 0.5f));
            var dir = new Vector3(0f, 0.4f, -1f).normalized;
            cam.transform.position = bounds.center + dir * distance;
            cam.transform.LookAt(bounds.center);
        }

        static Bounds GetCompoundBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.zero);

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        void TeardownSlot(int i)
        {
            var slot = m_Slots[i];
            // DestroyImmediate, not Destroy: release the instance's references to the content file's
            // objects synchronously so the UnloadAsync below can complete within this call — a deferred
            // Destroy would not take effect until end-of-frame, after the wait.
            if (slot.Instance != null)
            {
                DestroyImmediate(slot.Instance);
                slot.Instance = null;
            }

            // Unload the content file BEFORE unmounting the archive that backs it.
            if (slot.HasContentFile)
            {
                slot.ContentFile.UnloadAsync().WaitForCompletion(5000);
                slot.HasContentFile = false;
            }

            if (slot.HasArchive)
            {
                slot.Archive.Unmount().Complete();
                slot.HasArchive = false;
            }

            if (!string.IsNullOrEmpty(slot.Path))
            {
                try { File.Delete(slot.Path); } catch { /* best-effort; unique names never collide */ }
                slot.Path = null;
            }

            if (m_Active == i)
                m_Active = -1;
        }

        void OnDestroy()
        {
            if (m_Slots == null)
                return;
            TeardownSlot(0);
            TeardownSlot(1);
        }
    }
}
