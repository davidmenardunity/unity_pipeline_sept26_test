using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Drives the pipeline setup the player needs before it can preview anything: wait for a token, pick a
    // workbench (the one named by id, or the newest on a branch, created from a repo if there is none),
    // wait until it has validated, pin its settled revision, find or create an environment for this
    // player's platform, then list the assets to offer from the revision's manifest. Runs once on Start
    // against the ProjectServiceClient on this GameObject and publishes the results (WorkbenchId,
    // EnvironmentId, Revision, Assets) that PreviewClient and PreviewDemoUI read.
    [RequireComponent(typeof(ProjectServiceClient))]
    public class PreviewSession : MonoBehaviour
    {
        public enum State
        {
            Idle,
            WaitingForSignIn,
            CreatingWorkbench,
            Validating,
            ResolvingRevision,
            PreparingEnvironment,
            DiscoveringAssets,
            Ready,
            Failed
        }

        [Header("Workbench")]
        [Tooltip("Workbench to preview from. Empty: the newest workbench on the branch below.")]
        [SerializeField] string m_WorkbenchId = "";
        [Tooltip("Branch whose newest workbench is used when no workbench id is set.")]
        [SerializeField] string m_Branch = "main";
        [Tooltip("Public git repository to create a workbench from when the branch has none. Empty: only " +
                 "reuse an existing workbench. A workbench starts from the branch's latest commit and " +
                 "doesn't follow later pushes.")]
        [SerializeField] string m_Repo = "";
        [Tooltip("Seconds to wait for the workbench to validate (a first validation takes a few minutes).")]
        [SerializeField] int m_SettleTimeoutSeconds = 900;

        [Header("Asset discovery")]
        [Tooltip("Folder subtree to list, e.g. /Assets or /Assets/Art/Characters.")]
        [SerializeField] string m_SearchScope = "/Assets";
        [Tooltip("Optional name filter within the scope (case-insensitive). Empty lists everything in the scope.")]
        [SerializeField] string m_SearchQuery = "";
        [Tooltip("Manifest rows requested per page (the server caps pages at 500).")]
        [SerializeField] int m_PageSize = 500;
        [Tooltip("Stop discovery after this many assets, so a very large scope can't stall setup.")]
        [SerializeField] int m_MaxAssets = 2000;

        [Tooltip("Run the setup automatically on Start.")]
        [SerializeField] bool m_AutoStart = true;
        [Tooltip("Once ready, immediately preview an asset (useful for headless testing).")]
        [SerializeField] bool m_AutoPreviewFirstOnReady;
        [Tooltip("GUID to auto-preview instead of the first previewable discovered asset.")]
        [SerializeField] string m_AutoPreviewGuid = "";

        ProjectServiceClient m_ProjectService;
        readonly List<PreviewDemoUI.PreviewAsset> m_Assets = new();

        public State CurrentState { get; private set; } = State.Idle;
        /// <summary>What the current step is waiting on, for the status line (e.g. readiness and validation).</summary>
        public string StatusDetail { get; private set; }
        public string WorkbenchId { get; private set; }
        public string EnvironmentId { get; private set; }
        public string Revision { get; private set; }
        /// <summary>The pipeline platform of this player (Windows64, Linux64, …).</summary>
        public string Platform { get; private set; }
        public string LastError { get; private set; }
        public bool IsReady => CurrentState == State.Ready;
        public IReadOnlyList<PreviewDemoUI.PreviewAsset> Assets => m_Assets;

        void Awake()
        {
            m_ProjectService = GetComponent<ProjectServiceClient>();
            // Command-line overrides, for running a built player without touching the scene:
            // -previewWorkbench <id> picks the workbench, -previewGuid <guid> previews that asset once ready.
            if (CommandLine.Get("-previewWorkbench") is { Length: > 0 } workbench)
                m_WorkbenchId = workbench;
            if (CommandLine.Get("-previewGuid") is { Length: > 0 } guid)
            {
                m_AutoPreviewGuid = guid;
                m_AutoPreviewFirstOnReady = true;
            }
        }

        void Start()
        {
            // Embedded in a host page, the host owns the session (workbench, sign-in, discovery) and sends
            // archives by URL; setting up here would only fail on WebGL and report it.
            if (m_AutoStart && !Host.Embedded)
                StartCoroutine(Run());
        }

        string Org => m_ProjectService.OrgId;
        string Project => m_ProjectService.ProjectGuid;
        string Base => m_ProjectService.BaseUrl;

        public IEnumerator Run()
        {
            Platform = PreviewPlatform.Current;
            if (Platform == null)
            {
                Fail($"platform {Application.platform} has no pipeline environment platform");
                yield break;
            }

            if (string.IsNullOrEmpty(Org) || string.IsNullOrEmpty(Project))
            {
                Fail("no organization/project: set Org Id and Project Guid on ProjectServiceClient");
                yield break;
            }

            if (m_ProjectService.RequiresSignIn)
            {
                CurrentState = State.WaitingForSignIn;
                yield return WaitForSignIn();
                if (CurrentState == State.Failed) yield break;
            }

            CurrentState = State.CreatingWorkbench;
            yield return EnsureWorkbench();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.Validating;
            yield return WaitUntilSettled();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.PreparingEnvironment;
            yield return EnsureEnvironment();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.DiscoveringAssets;
            yield return Discover();
            if (CurrentState == State.Failed) yield break;

            StatusDetail = null;
            CurrentState = State.Ready;
            Debug.Log($"[PreviewSession] ready: platform={Platform} workbench={WorkbenchId} env={EnvironmentId} " +
                      $"revision={Revision} assets={m_Assets.Count}");

            if (m_AutoPreviewFirstOnReady)
                AutoPreview();
        }

        void AutoPreview()
        {
            var guid = m_AutoPreviewGuid;
            if (string.IsNullOrEmpty(guid))
                foreach (var asset in m_Assets)
                    if (PreviewLoader.CanPresent(asset.Name))
                    {
                        guid = asset.Guid;
                        break;
                    }

            var client = GetComponent<PreviewClient>();
            if (string.IsNullOrEmpty(guid) || client == null)
            {
                Debug.LogWarning("[PreviewSession] auto-preview found no previewable asset.");
                return;
            }
            client.RequestPreview(guid);
        }

        // Requests need a token. An interactive provider waits for the user to press Sign in; a
        // non-interactive one (an injected token) must already have a token.
        IEnumerator WaitForSignIn()
        {
            var provider = m_ProjectService.TokenProvider;
            if (provider == null)
            {
                Fail("no token provider on the rig: add InjectedTokenProvider (or CompositeAuthTokenProvider)");
                yield break;
            }

            var signIn = provider as IPreviewSignIn;
            if (signIn == null)
            {
                if (!provider.HasToken)
                    Fail("the token provider has no token (set UNITY_JWT in the token file, or the environment variable)");
                yield break;
            }

            while (!signIn.IsSignedIn)
                yield return null;
        }

        IEnumerator EnsureWorkbench()
        {
            if (!string.IsNullOrEmpty(m_WorkbenchId))
            {
                WorkbenchId = m_WorkbenchId.Trim();
                StatusDetail = $"workbench {Short(WorkbenchId)}";
                yield break;
            }

            ProjectServiceClient.JsonResult list = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Workbenches(Base, Org, Project), null, r => list = r);
            if (!list.Ok)
            {
                Fail($"list workbenches failed: {list.Error}");
                yield break;
            }

            // The newest workbench on the branch (and repo, when one is set) is the freshest commit.
            WorkbenchItem newest = null;
            var items = JsonUtility.FromJson<WorkbenchListResult>(list.Text)?.items ?? Array.Empty<WorkbenchItem>();
            foreach (var wb in items)
            {
                if ((string.IsNullOrEmpty(wb.branchName) ? "main" : wb.branchName) != m_Branch)
                    continue;
                if (!string.IsNullOrEmpty(m_Repo) && !SameRepo(wb.upstreamRepository, m_Repo))
                    continue;
                if (newest == null || string.CompareOrdinal(wb.createdAt, newest.createdAt) > 0)
                    newest = wb;
            }
            if (newest != null)
            {
                WorkbenchId = newest.workbenchId;
                StatusDetail = $"workbench {Short(WorkbenchId)} @ {Short(newest.upstreamRevision, 7)}";
                yield break;
            }

            if (string.IsNullOrEmpty(m_Repo))
            {
                Fail($"no workbench on branch '{m_Branch}', and no repo set to create one");
                yield break;
            }

            StatusDetail = $"creating a workbench on {m_Branch}";
            var body = JsonUtility.ToJson(new CreateWorkbenchRequest { type = "git", branch = m_Branch, repo = m_Repo });
            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("POST", AssetPipelineRoute.Workbenches(Base, Org, Project), body, r => res = r);
            if (!res.Ok)
            {
                Fail($"create workbench failed: {res.Error}");
                yield break;
            }

            WorkbenchId = JsonUtility.FromJson<WorkbenchItem>(res.Text)?.workbenchId;
            if (string.IsNullOrEmpty(WorkbenchId))
                Fail("workbench create returned no id");
        }

        // Reads are pinned to a validated revision. A failed validation never settles, so check the
        // workbench's validation status too and fail with its reason instead of waiting out the timeout.
        IEnumerator WaitUntilSettled()
        {
            var deadline = Time.realtimeSinceStartup + m_SettleTimeoutSeconds;
            while (true)
            {
                ProjectServiceClient.JsonResult res = null;
                yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Readiness(Base, Org, Project, WorkbenchId), null, r => res = r);
                if (!res.Ok)
                {
                    Fail($"read readiness failed: {res.Error}" +
                         (res.Text != null && res.Text.Contains("status_authority_unavailable")
                             ? " (is the project service running? It idles out after some hours.)" : ""));
                    yield break;
                }

                var readiness = JsonUtility.FromJson<ReadinessResponse>(res.Text) ?? new ReadinessResponse();
                if (readiness.readiness == "settled" && !string.IsNullOrEmpty(readiness.settledRevision))
                {
                    Revision = readiness.settledRevision;
                    yield break;
                }
                if (readiness.readiness == "gone")
                {
                    Fail("the project service no longer has this workbench (readiness: gone)");
                    yield break;
                }

                ProjectServiceClient.JsonResult wbRes = null;
                yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Workbench(Base, Org, Project, WorkbenchId), null, r => wbRes = r);
                var validation = wbRes.Ok ? JsonUtility.FromJson<WorkbenchDetails>(wbRes.Text)?.validation : null;
                if (validation?.status == "failed")
                {
                    Fail($"workbench validation failed ({validation.error?.category}: {validation.error?.message})");
                    yield break;
                }

                StatusDetail = $"readiness {readiness.readiness}, head {readiness.head}, validation {validation?.status ?? "?"}";
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail($"the workbench didn't validate within {m_SettleTimeoutSeconds} s ({StatusDetail})");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(5f);
            }
        }

        IEnumerator EnsureEnvironment()
        {
            ProjectServiceClient.JsonResult list = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Environments(Base, Org, Project, WorkbenchId), null, r => list = r);
            if (list.Ok)
            {
                var items = JsonUtility.FromJson<EnvironmentListResult>(list.Text)?.items ?? Array.Empty<EnvironmentItem>();
                foreach (var env in items)
                    if (env.workbenchId == WorkbenchId && string.Equals(env.platform, Platform, StringComparison.OrdinalIgnoreCase))
                    {
                        EnvironmentId = env.environmentId;
                        StatusDetail = $"{Platform} environment {Short(EnvironmentId)}";
                        yield break;
                    }
            }

            StatusDetail = $"creating a {Platform} environment";
            var body = JsonUtility.ToJson(new CreateEnvironmentRequest { platform = Platform });
            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("POST", AssetPipelineRoute.Environments(Base, Org, Project, WorkbenchId), body, r => res = r);
            if (!res.Ok)
            {
                Fail($"create {Platform} environment failed: {res.Error}");
                yield break;
            }

            EnvironmentId = JsonUtility.FromJson<EnvironmentItem>(res.Text)?.environmentId;
            if (string.IsNullOrEmpty(EnvironmentId))
                Fail("environment create returned no id");
        }

        // The manifest lists what the revision's Editor imported (with GUIDs), so every entry is
        // something the importer can run on. Filtered here by scope and name.
        IEnumerator Discover()
        {
            m_Assets.Clear();
            var scope = "/" + (m_SearchScope ?? "").Trim().Trim('/');
            var prefix = scope == "/" ? "/" : scope + "/";
            string cursor = null;
            var rows = 0;
            do
            {
                StatusDetail = $"{rows} manifest rows read, {m_Assets.Count} assets in {scope}";
                var url = AssetPipelineRoute.Manifest(Base, Org, Project, WorkbenchId, Revision, m_PageSize, cursor);
                ProjectServiceClient.JsonResult res = null;
                yield return m_ProjectService.SendJson("GET", url, null, r => res = r);
                if (!res.Ok)
                {
                    Fail($"asset discovery failed: {res.Error}");
                    yield break;
                }

                var page = JsonUtility.FromJson<ManifestPage>(res.Text);
                foreach (var item in page?.items ?? Array.Empty<ManifestItem>())
                {
                    rows++;
                    var path = "/" + (item.path ?? "").TrimStart('/');
                    if (item.isFolder || string.IsNullOrEmpty(item.assetGuid) || !path.StartsWith(prefix, StringComparison.Ordinal)
                        || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = Basename(path);
                    if (!string.IsNullOrEmpty(m_SearchQuery) && name.IndexOf(m_SearchQuery, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    // Name from the path basename so it always carries the extension (.mat/.fbx/.prefab…);
                    // the full path lets the picker present the folder hierarchy.
                    m_Assets.Add(new PreviewDemoUI.PreviewAsset { Name = name, Guid = item.assetGuid, Path = path });
                }

                cursor = page?.nextCursor;
                if (!string.IsNullOrEmpty(cursor) && m_Assets.Count >= m_MaxAssets)
                {
                    Debug.LogWarning($"[PreviewSession] stopped discovery at {m_Assets.Count} assets (Max Assets); narrow the Search Scope to see the rest.");
                    break;
                }
            } while (!string.IsNullOrEmpty(cursor));

            if (m_Assets.Count == 0)
                Debug.LogWarning($"[PreviewSession] discovery found no assets in '{scope}'{(string.IsNullOrEmpty(m_SearchQuery) ? "" : $" matching '{m_SearchQuery}'")}.");
        }

        static bool SameRepo(string a, string b)
            => string.Equals(TrimRepo(a), TrimRepo(b), StringComparison.OrdinalIgnoreCase);

        static string TrimRepo(string url)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url.Substring(0, url.Length - 4) : url;
        }

        static string Short(string id, int length = 8)
            => string.IsNullOrEmpty(id) ? "?" : id.Length <= length ? id : id.Substring(0, length);

        static string Basename(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
        }

        void Fail(string message)
        {
            CurrentState = State.Failed;
            LastError = message;
            Debug.LogError($"[PreviewSession] {message}");
        }
    }

    /// <summary>
    /// Whether a host drives the player: Pipeline Explorer embeds the WebGL build as player.html?embedded=1;
    /// a desktop player can be started with -previewEmbedded. The host then draws all UI and requests
    /// previews (PreviewClient.PreviewArchiveFromUrl), so the player draws none and runs no setup.
    /// </summary>
    static class Host
    {
        static bool? s_Embedded;

        public static bool Embedded => s_Embedded ??=
            Array.IndexOf(Environment.GetCommandLineArgs(), "-previewEmbedded") >= 0 ||
            (Application.absoluteURL ?? "").Contains("embedded=1");
    }

    static class CommandLine
    {
        /// <summary>The value after <paramref name="name"/> on the command line, or null.</summary>
        public static string Get(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            return null;
        }
    }
}
