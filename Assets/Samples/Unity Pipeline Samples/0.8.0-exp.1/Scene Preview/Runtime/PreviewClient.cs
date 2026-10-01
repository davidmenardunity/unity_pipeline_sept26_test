using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Requests a preview content archive (.ca) for an asset and hands it to the loader. The environment
    // and settled revision come from PreviewSession (resolved at runtime against Project Service); the
    // request itself — including the cold-start 202/job retry — runs through ProjectServiceClient, so this
    // component only builds the import address, drives the loader's slots, and cancels a superseded
    // request. Set a local archive path instead to exercise the mount/load/preview path with no network.
    [RequireComponent(typeof(PreviewLoader))]
    [RequireComponent(typeof(ProjectServiceClient))]
    public class PreviewClient : MonoBehaviour
    {
        [Header("Import address")]
        [Tooltip("Full type name of the importer Project Service runs on the asset to produce its preview.")]
        [SerializeField] string m_ImporterType = "Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter";
        [Tooltip("Which produced artifact to fetch (the importer's content-archive output artifact).")]
        [SerializeField] string m_ArtifactName = "ca";

        [Header("Manual overrides (optional)")]
        [Tooltip("Environment id. Leave empty to use the one PreviewSession creates at runtime.")]
        [SerializeField] string m_EnvironmentId = "";
        [Tooltip("Settled revision. Leave empty to use the one PreviewSession resolves at runtime.")]
        [SerializeField] string m_Revision = "";

        [Header("Local test (bypasses network)")]
        [Tooltip("If set, copy this local .ca into the slot instead of downloading — for testing the load path.")]
        [SerializeField] string m_LocalArchivePath = "";

        PreviewLoader m_Loader;
        ProjectServiceClient m_ProjectService;
        PreviewSession m_Session;
        Coroutine m_Current;

        void Awake()
        {
            m_Loader = GetComponent<PreviewLoader>();
            m_ProjectService = GetComponent<ProjectServiceClient>();
            m_Session = GetComponent<PreviewSession>();
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

            var environmentId = ResolveEnvironment();
            var revision = ResolveRevision();
            if (string.IsNullOrEmpty(environmentId) || string.IsNullOrEmpty(revision))
            {
                Debug.LogError("[PreviewClient] no environment/revision available — is PreviewSession ready?");
                m_Loader.AbandonSlot("Project Service setup hasn't finished");
                yield break;
            }

            var url = AssetPipelineRoute.ImportArtifact(
                m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid, environmentId, revision, assetGuid, m_ImporterType, m_ArtifactName);
            Debug.Log($"[PreviewClient] GET {url}");

            ProjectServiceClient.DownloadResult res = null;
            yield return m_ProjectService.Download(url, dest, r => res = r);

            if (res == null || !res.Ok)
            {
                var error = res?.Error ?? "no result";
                Debug.LogError($"[PreviewClient] import request failed: {error}");
                m_Loader.AbandonSlot($"import request failed: {error}");
                yield break;
            }

            m_Loader.PrepareAndSwap();
            m_Current = null;
        }

        /// <summary>True when previews come from Local Archive Path instead of Project Service.</summary>
        public bool UsesLocalArchive => !string.IsNullOrEmpty(m_LocalArchivePath);

        string ResolveEnvironment()
            => !string.IsNullOrEmpty(m_EnvironmentId) ? m_EnvironmentId
                : m_Session != null && m_Session.IsReady ? m_Session.EnvironmentId : null;

        string ResolveRevision()
            => !string.IsNullOrEmpty(m_Revision) ? m_Revision
                : m_Session != null && m_Session.IsReady ? m_Session.Revision : null;

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
