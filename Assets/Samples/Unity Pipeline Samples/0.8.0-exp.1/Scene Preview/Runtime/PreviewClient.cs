using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Requests a preview content archive (.ca) for an asset from the pipeline and hands it to the loader.
    // The workbench, environment and settled revision come from PreviewSession. The pipeline runs the
    // importer on demand: POST …/imports with the address T:{guid}+{importer type} answers the import's
    // manifest, which names its output files; the archive is then downloaded by name. Requests run
    // through ProjectServiceClient (auth, 202/job retry). Set a local archive path instead to exercise
    // the mount/load/preview path with no network.
    [RequireComponent(typeof(PreviewLoader))]
    [RequireComponent(typeof(ProjectServiceClient))]
    public class PreviewClient : MonoBehaviour
    {
        [Header("Import address")]
        [Tooltip("Full type name of the importer the pipeline runs on the asset to produce its preview.")]
        [SerializeField] string m_ImporterType = "Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter";
        [Tooltip("The importer's output to fetch: matches an artifact named exactly this, or ending in .{this}.")]
        [SerializeField] string m_ArtifactName = "ca";

        [Header("Retries")]
        [Tooltip("Times to re-issue an import the pipeline is still producing (409 revision_not_validated, " +
                 "usual for minutes after a cold start).")]
        [SerializeField] int m_NotReadyRetries = 40;
        [Tooltip("Seconds between those re-issues.")]
        [SerializeField] float m_NotReadyRetrySeconds = 15f;

        [Header("Manual overrides (optional)")]
        [Tooltip("Workbench id. Leave empty to use the one PreviewSession picks.")]
        [SerializeField] string m_WorkbenchId = "";
        [Tooltip("Environment id. Leave empty to use the one PreviewSession finds or creates.")]
        [SerializeField] string m_EnvironmentId = "";
        [Tooltip("Settled revision. Leave empty to use the one PreviewSession pins.")]
        [SerializeField] string m_Revision = "";

        [Header("Local test (bypasses network)")]
        [Tooltip("If set, copy this local .ca into the slot instead of downloading — for testing the load path.")]
        [SerializeField] string m_LocalArchivePath = "";

        PreviewLoader m_Loader;
        ProjectServiceClient m_ProjectService;
        PreviewSession m_Session;
        Coroutine m_Current;

        /// <summary>What the current request is doing (importing, retrying, downloading), for the status line.</summary>
        public string Status { get; private set; }

        void Awake()
        {
            m_Loader = GetComponent<PreviewLoader>();
            m_ProjectService = GetComponent<ProjectServiceClient>();
            m_Session = GetComponent<PreviewSession>();
            // -previewLocalArchive <path>: load that .ca for every request, with no network.
            if (CommandLine.Get("-previewLocalArchive") is { Length: > 0 } local)
            {
                m_LocalArchivePath = local;
                m_LoadLocalOnStart = true;
            }
        }

        bool m_LoadLocalOnStart;

        // A local archive given on the command line is shown straight away: the archive names its own
        // content, so no asset needs picking (and no network or sign-in is involved).
        void Start()
        {
            if (m_LoadLocalOnStart)
                RequestPreview("local");
        }

        /// <summary>
        /// Preview a content archive (.ca) on disk, e.g. one dropped on the window (PreviewFileDrop). From
        /// then on, requests load local archives instead of asking the pipeline.
        /// </summary>
        public void PreviewArchive(string path)
        {
            m_LocalArchivePath = path;
            RequestPreview("local");
        }

        /// <summary>
        /// Download a content archive (.ca) from a URL and preview it. For hosts that hand the player an
        /// archive, e.g. a web page embedding a WebGL player: call it with SendMessage("PreviewRig",
        /// "PreviewArchiveFromUrl", url). Blob URLs from a dropped file work too.
        /// </summary>
        public void PreviewArchiveFromUrl(string url)
        {
            if (m_Current != null)
                StopCoroutine(m_Current);
            m_Current = StartCoroutine(DownloadRoutine(url));
        }

        IEnumerator DownloadRoutine(string url)
        {
            var dest = m_Loader.AcquireSlot();
            Status = "downloading…";
            using var req = UnityEngine.Networking.UnityWebRequest.Get(url);
            // Pipeline Explorer only answers its own pages; this header is how they identify themselves.
            if (!url.StartsWith("blob:", StringComparison.Ordinal))
                req.SetRequestHeader("X-Pipeline-Explorer", "1");
            yield return req.SendWebRequest();
            if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Finish($"download failed: {req.responseCode} {req.error}");
                yield break;
            }
            try
            {
                File.WriteAllBytes(dest, req.downloadHandler.data);
            }
            catch (Exception e)
            {
                Finish($"couldn't store the archive: {e.Message}");
                yield break;
            }
            Status = null;
            Debug.Log($"[PreviewClient] loading {req.downloadHandler.data.Length} bytes from {url}");
            m_Loader.PrepareAndSwap();
            m_Current = null;
        }

        /// <summary>Request, download, and preview the content archive for the given asset GUID.</summary>
        public void RequestPreview(string assetGuid)
        {
            // Supersede any in-flight request. The loader hands each request a uniquely named slot file, so
            // a superseded download never writes to the path the new one uses; the shown preview is left
            // untouched until the new archive is ready.
            if (m_Current != null)
                StopCoroutine(m_Current);
            m_Current = StartCoroutine(RequestRoutine(assetGuid));
        }

        IEnumerator RequestRoutine(string assetGuid)
        {
            var dest = m_Loader.AcquireSlot();

            if (!string.IsNullOrEmpty(m_LocalArchivePath))
            {
                if (!CopyLocal(m_LocalArchivePath, dest))
                {
                    m_Loader.AbandonSlot($"could not copy the local archive '{m_LocalArchivePath}'");
                    yield break;
                }
                m_Loader.PrepareAndSwap();
                yield break;
            }

            var ready = m_Session != null && m_Session.IsReady;
            var workbenchId = Pick(m_WorkbenchId, ready ? m_Session.WorkbenchId : null);
            var environmentId = Pick(m_EnvironmentId, ready ? m_Session.EnvironmentId : null);
            var revision = Pick(m_Revision, ready ? m_Session.Revision : null);
            if (string.IsNullOrEmpty(workbenchId) || string.IsNullOrEmpty(environmentId) || string.IsNullOrEmpty(revision))
            {
                Debug.LogError("[PreviewClient] no workbench/environment/revision available — is PreviewSession ready?");
                m_Loader.AbandonSlot("pipeline setup hasn't finished");
                yield break;
            }

            var ps = m_ProjectService;
            var address = AssetPipelineRoute.ImportAddress(assetGuid, m_ImporterType);
            var importUrl = AssetPipelineRoute.Imports(ps.BaseUrl, ps.OrgId, ps.ProjectGuid, workbenchId, environmentId, revision);
            var body = JsonUtility.ToJson(new ImportRequest { addresses = new[] { address } });

            // 1. Run the importer: one slot back, with the import's manifest or why it didn't resolve.
            ProjectServiceClient.JsonResult res = null;
            for (var attempt = 0; ; attempt++)
            {
                Status = attempt == 0 ? "importing…" : $"the pipeline is still producing it, retry {attempt}/{m_NotReadyRetries}…";
                yield return ps.SendJson("POST", importUrl, body, r => res = r);
                var notReady = res.Code == 409 && res.Text != null && res.Text.Contains("revision_not_validated");
                if (res.Ok || !notReady || attempt >= m_NotReadyRetries)
                    break;
                yield return new WaitForSecondsRealtime(m_NotReadyRetrySeconds);
            }
            if (!res.Ok)
            {
                Finish($"import request failed: {res.Error}");
                yield break;
            }

            var slots = JsonUtility.FromJson<ImportResults>(res.Text)?.results;
            var slot = slots != null && slots.Length > 0 ? slots[0] : null;
            if (slot == null)
            {
                Finish("the import answered no result slot");
                yield break;
            }
            if (!string.IsNullOrEmpty(slot.error?.code))
            {
                Finish($"{slot.error.code}: {slot.error.message}" + (slot.error.code == "import_not_found"
                    ? $" (is {m_ImporterType} in the workbench's commit?)" : ""));
                yield break;
            }

            // 2. Download the archive the manifest names; fall back to naming it in the query string.
            var artifact = FindArtifact(slot.manifest?.artifacts);
            var url = artifact != null
                ? AssetPipelineRoute.ImportArtifact(ps.BaseUrl, ps.OrgId, ps.ProjectGuid, workbenchId, environmentId, revision, address, artifact)
                : AssetPipelineRoute.ImportArtifactByQuery(ps.BaseUrl, ps.OrgId, ps.ProjectGuid, workbenchId, environmentId, revision, address, m_ArtifactName);
            if (artifact == null)
                Debug.LogWarning($"[PreviewClient] the import manifest lists no '{m_ArtifactName}' artifact " +
                                 $"({Describe(slot.manifest)}); trying ?artifactName={m_ArtifactName}");

            Status = "downloading…";
            Debug.Log($"[PreviewClient] GET {url}");
            ProjectServiceClient.DownloadResult download = null;
            yield return ps.Download(url, dest, r => download = r);
            if (download == null || !download.Ok)
            {
                Finish($"download failed: {download?.Error ?? "no result"}");
                yield break;
            }

            Status = null;
            m_Loader.PrepareAndSwap();
            m_Current = null;
        }

        void Finish(string error)
        {
            Status = null;
            Debug.LogError($"[PreviewClient] {error}");
            m_Loader.AbandonSlot(error);
            m_Current = null;
        }

        // The archive among the import's outputs: named exactly m_ArtifactName, or ending in ".{m_ArtifactName}".
        string FindArtifact(string[] artifacts)
        {
            if (artifacts == null)
                return null;
            foreach (var name in artifacts)
                if (string.Equals(name, m_ArtifactName, StringComparison.OrdinalIgnoreCase)
                    || (name != null && name.EndsWith("." + m_ArtifactName, StringComparison.OrdinalIgnoreCase)))
                    return name;
            return null;
        }

        static string Describe(ImportManifest manifest)
        {
            if (manifest == null)
                return "no manifest";
            var artifacts = manifest.artifacts != null && manifest.artifacts.Length > 0 ? string.Join(", ", manifest.artifacts) : "none";
            return $"artifacts: {artifacts}; files: {manifest.files?.Length ?? 0}";
        }

        /// <summary>True when previews come from Local Archive Path instead of the pipeline.</summary>
        public bool UsesLocalArchive => !string.IsNullOrEmpty(m_LocalArchivePath);

        static string Pick(string manual, string fromSession) => !string.IsNullOrEmpty(manual) ? manual : fromSession;

        static bool CopyLocal(string source, string dest)
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PreviewClient] failed to copy local archive '{source}': {e.Message}");
                return false;
            }
        }
    }
}
