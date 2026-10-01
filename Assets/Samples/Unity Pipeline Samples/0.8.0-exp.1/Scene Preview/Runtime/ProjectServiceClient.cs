using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // The one place that talks HTTP to Project Service. Owns the endpoint, the org/project the routes
    // address, and authentication, and implements the asynchronous protocol every artifact-producing
    // endpoint uses: a call answers 200 (result) or 202 (not yet — a Retry-After plus, when the work is
    // watchable, a jobId). The result is always fetched by re-issuing the identical request; the job only
    // says when. All helpers are coroutines so UnityWebRequest stays on Unity's main thread.
    public class ProjectServiceClient : MonoBehaviour
    {
        public enum ServiceMode
        {
            // The public Unity Services gateway. Requests carry the signed-in user's token, and the
            // routes address the project's Unity Cloud organization and project.
            UnityCloud,
            // A Project Service your organization hosts (on-prem, or localhost for development). No auth
            // header is sent — the service uses its own configured credential.
            SelfHosted
        }

        [Tooltip("Unity Cloud: the public gateway, signed in as a Unity user. Self Hosted: a Project Service " +
                 "your organization runs, reached directly with no auth header.")]
        [SerializeField] ServiceMode m_Mode = ServiceMode.UnityCloud;

        [Tooltip("Service base URL. Unity Cloud: " + AssetPipelineRoute.UnityCloudGateway +
                 ". Self Hosted: your service's address, e.g. http://localhost:8811.")]
        [SerializeField] string m_BaseUrl = AssetPipelineRoute.UnityCloudGateway;

        [Tooltip("Unity Cloud organization id. Filled in from Project Settings > Services when empty.")]
        [SerializeField] string m_OrgId = "";

        [Tooltip("Unity Cloud project id. Filled in from Project Settings > Services when empty. Self Hosted " +
                 "may leave it empty and let PreviewSession resolve its Project Name instead.")]
        [SerializeField] string m_ProjectGuid = "";

        [Header("Async")]
        [Tooltip("Seconds asked of the server (Prefer: wait=N) before it answers 202.")]
        [SerializeField] int m_PreferWaitSeconds = 20;
        [Tooltip("Max re-issues of a 202'd request before giving up.")]
        [SerializeField] int m_MaxAttempts = 60;
        [Tooltip("Max seconds to wait on a single backing job before giving up.")]
        [SerializeField] int m_MaxJobWaitSeconds = 300;

        IPreviewTokenProvider m_TokenProvider;

        public ServiceMode Mode => m_Mode;
        public bool RequiresSignIn => m_Mode == ServiceMode.UnityCloud;
        public IPreviewTokenProvider TokenProvider => m_TokenProvider;
        public string BaseUrl => m_BaseUrl;
        public string OrgId => m_OrgId;
        public string ProjectGuid => m_ProjectGuid;
        public int PreferWaitSeconds => m_PreferWaitSeconds;

        /// <summary>Set the project GUID once resolved from a project name at runtime.</summary>
        public void SetProjectGuid(string guid) => m_ProjectGuid = guid;

        void Awake()
        {
            m_TokenProvider = GetComponent<IPreviewTokenProvider>();
            if (m_Mode == ServiceMode.UnityCloud && string.IsNullOrEmpty(m_ProjectGuid))
                m_ProjectGuid = Application.cloudProjectId;
        }

#if UNITY_EDITOR
        // The player has no runtime API for the organization id, so capture the project's Unity Cloud link
        // into the serialized fields while editing; the built player then carries them.
        void OnValidate()
        {
            if (m_Mode != ServiceMode.UnityCloud)
                return;
            if (string.IsNullOrEmpty(m_OrgId))
                m_OrgId = UnityEditor.CloudProjectSettings.organizationKey;
            if (string.IsNullOrEmpty(m_ProjectGuid))
                m_ProjectGuid = UnityEditor.CloudProjectSettings.projectId;
        }
#endif

        public class JsonResult
        {
            public long Code;
            public bool Ok;          // a final 2xx that is not 202
            public string Text;      // response body (JSON or problem+json)
            public string Error;     // transport error, or a terminal/exhausted-retry description
        }

        public class DownloadResult
        {
            public bool Ok;          // 200 and the bytes are at DestPath
            public long Code;
            public string Error;
        }

        /// <summary>GET/POST/PATCH a JSON endpoint, transparently re-issuing while it answers 202.</summary>
        public IEnumerator SendJson(string method, string url, string body, Action<JsonResult> onDone)
        {
            var result = new JsonResult();
            for (int attempt = 0; attempt < m_MaxAttempts; attempt++)
            {
                using var req = BuildJson(method, url, body);
                yield return ApplyAuth(req);

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.ConnectionError)
                {
                    result.Error = $"{method} {url}: {req.error}";
                    break;
                }

                result.Code = req.responseCode;
                result.Text = req.downloadHandler != null ? req.downloadHandler.text : null;

                if (req.responseCode == 202)
                {
                    if (IsTerminal(result.Text))
                    {
                        result.Error = $"{method} {url}: server reported a terminal disposition";
                        break;
                    }
                    yield return WaitAfter(req, result.Text);
                    continue;
                }

                result.Ok = req.responseCode >= 200 && req.responseCode < 300;
                if (!result.Ok)
                    result.Error = $"{method} {url}: HTTP {req.responseCode} {req.error}";
                break;
            }

            if (!result.Ok && result.Error == null)
                result.Error = $"{method} {url}: gave up after {m_MaxAttempts} attempts";
            onDone?.Invoke(result);
        }

        /// <summary>
        /// GET an artifact to <paramref name="destPath"/>, handling the 202/job protocol: on 202 it polls
        /// the backing job (or waits Retry-After) and re-issues until the bytes arrive.
        /// </summary>
        public IEnumerator Download(string url, string destPath, Action<DownloadResult> onDone)
        {
            var result = new DownloadResult();
            for (int attempt = 0; attempt < m_MaxAttempts; attempt++)
            {
                using var req = UnityWebRequest.Get(url);
#if UNITY_WEBGL && !UNITY_EDITOR
                // WebGL has no file-backed download handler; buffer the bytes and write them below.
                req.downloadHandler = new DownloadHandlerBuffer();
#else
                req.downloadHandler = new DownloadHandlerFile(destPath) { removeFileOnAbort = true };
#endif
                if (m_PreferWaitSeconds > 0)
                    req.SetRequestHeader("Prefer", $"wait={m_PreferWaitSeconds}");
                yield return ApplyAuth(req);

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.ConnectionError)
                {
                    result.Error = $"GET {url}: {req.error}";
                    break;
                }

                result.Code = req.responseCode;

                if (req.responseCode == 200)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    try { File.WriteAllBytes(destPath, req.downloadHandler.data); }
                    catch (Exception e) { result.Error = $"write '{destPath}': {e.Message}"; break; }
#endif
                    result.Ok = true;
                    break;
                }

                if (req.responseCode == 202)
                {
                    // The retry envelope is the response body. With DownloadHandlerFile it was written to
                    // destPath (a small JSON, overwritten by the next attempt's handler); on WebGL it is
                    // in the buffer.
                    string envelope = ReadRetryEnvelope(req, destPath);
                    if (IsTerminal(envelope))
                    {
                        result.Error = $"GET {url}: server reported a terminal disposition";
                        break;
                    }

                    var jobId = ParseJobId(envelope);
                    if (!string.IsNullOrEmpty(jobId))
                        yield return WaitForJob(jobId, RetryAfter(req));
                    else
                        yield return new WaitForSeconds(RetryAfter(req));
                    continue;
                }

                result.Error = $"GET {url}: HTTP {req.responseCode} {req.error}";
                break;
            }

            if (!result.Ok && result.Error == null)
                result.Error = $"GET {url}: gave up after {m_MaxAttempts} attempts";
            onDone?.Invoke(result);
        }

        // Poll a backing job until it reaches a terminal status (or the job-wait budget runs out), so the
        // caller can re-issue the original request once the work is actually done.
        IEnumerator WaitForJob(string jobId, float pollSeconds)
        {
            var url = AssetPipelineRoute.Job(m_BaseUrl, m_OrgId, m_ProjectGuid, jobId);
            float waited = 0f;
            float interval = Mathf.Max(1f, pollSeconds);
            while (waited < m_MaxJobWaitSeconds)
            {
                using var req = UnityWebRequest.Get(url);
                req.SetRequestHeader("Accept", "application/json");
                yield return ApplyAuth(req);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.ConnectionError && req.responseCode == 200)
                {
                    var snap = SafeFromJson<JobSnapshot>(req.downloadHandler.text);
                    var status = snap?.status;
                    if (status == "completed" || status == "failed" || status == "cancelled")
                        yield break;
                }

                yield return new WaitForSeconds(interval);
                waited += interval;
            }
        }

        IEnumerator ApplyAuth(UnityWebRequest req)
        {
            if (!RequiresSignIn || m_TokenProvider == null)
                yield break;
            var task = m_TokenProvider.GetTokenAsync();
            while (!task.IsCompleted)
                yield return null;
            var token = task.IsFaulted ? null : task.Result;
            if (!string.IsNullOrEmpty(token))
                req.SetRequestHeader("Authorization", "Bearer " + token);
        }

        UnityWebRequest BuildJson(string method, string url, string body)
        {
            var req = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer() };
            if (!string.IsNullOrEmpty(body))
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)) { contentType = "application/json" };
            req.SetRequestHeader("Accept", "application/json");
            if (m_PreferWaitSeconds > 0)
                req.SetRequestHeader("Prefer", $"wait={m_PreferWaitSeconds}");
            return req;
        }

        IEnumerator WaitAfter(UnityWebRequest req, string envelope)
        {
            var jobId = ParseJobId(envelope);
            if (!string.IsNullOrEmpty(jobId))
                yield return WaitForJob(jobId, RetryAfter(req));
            else
                yield return new WaitForSeconds(RetryAfter(req));
        }

        static float RetryAfter(UnityWebRequest req)
        {
            var header = req.GetResponseHeader("Retry-After");
            return int.TryParse(header, out var seconds) && seconds > 0 ? seconds : 1f;
        }

        static string ReadRetryEnvelope(UnityWebRequest req, string destPath)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return req.downloadHandler != null ? req.downloadHandler.text : null;
#else
            try { return File.Exists(destPath) ? File.ReadAllText(destPath) : null; }
            catch { return null; }
#endif
        }

        static string ParseJobId(string envelope)
            => SafeFromJson<AcceptedRetryResponse>(envelope)?.jobId;

        static bool IsTerminal(string envelope)
            => SafeFromJson<AcceptedRetryResponse>(envelope)?.disposition == "terminal";

        static T SafeFromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try { return JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }
    }
}
