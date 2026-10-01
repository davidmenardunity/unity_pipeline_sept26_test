using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Drives the whole Project Service setup the player needs before it can preview anything: wait for
    // sign-in, ensure a workbench (reuse one by name — e.g. the one an artist pushes to — or create it
    // from a repo), validate it, pin its settled revision, ensure a build profile and an environment for
    // this player's platform, then discover the assets to offer. Runs once on Start against the
    // ProjectServiceClient on this GameObject and publishes the results (EnvironmentId, Revision, Assets)
    // that PreviewClient and PreviewDemoUI read.
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

        [Header("Project")]
        [Tooltip("Self Hosted only: project name to resolve when the ProjectServiceClient has no project id.")]
        [SerializeField] string m_ProjectName = "";

        [Header("Workbench")]
        [Tooltip("Workbench to preview from. Reused if one with this name exists (e.g. the one artists push " +
                 "to); otherwise created from the repository below.")]
        [SerializeField] string m_WorkbenchName = "scene-preview";
        [Tooltip("Version control type used only when creating the workbench: 'git' or 'uvcs'.")]
        [SerializeField] string m_VcsType = "git";
        [Tooltip("Repository to create the workbench from. Leave empty to only reuse an existing workbench.")]
        [SerializeField] string m_Repo = "";
        [SerializeField] string m_Branch = "main";

        [Header("Asset discovery")]
        [Tooltip("Folder subtree to list. Empty query + scope lists the folder.")]
        [SerializeField] string m_SearchScope = "/Assets";
        [Tooltip("Optional name filter within the scope. Empty lists everything in the scope.")]
        [SerializeField] string m_SearchQuery = "";
        [Tooltip("Assets requested per discovery page. Discovery follows pages until the scope is listed.")]
        [SerializeField] int m_PageSize = 200;
        [Tooltip("Stop discovery after this many assets, so a very large scope can't stall setup.")]
        [SerializeField] int m_MaxAssets = 2000;

        [Tooltip("Run the setup automatically on Start. Off leaves the scene on its baked asset list.")]
        [SerializeField] bool m_AutoStart = true;
        [Tooltip("Once ready, immediately preview an asset (useful for headless testing).")]
        [SerializeField] bool m_AutoPreviewFirstOnReady;
        [Tooltip("GUID to auto-preview instead of the first previewable discovered asset.")]
        [SerializeField] string m_AutoPreviewGuid = "";

        ProjectServiceClient m_ProjectService;
        string m_WorkbenchId;
        readonly List<PreviewDemoUI.PreviewAsset> m_Assets = new();

        public State CurrentState { get; private set; } = State.Idle;
        public string EnvironmentId { get; private set; }
        public string Revision { get; private set; }
        public string LastError { get; private set; }
        public bool IsReady => CurrentState == State.Ready;
        public IReadOnlyList<PreviewDemoUI.PreviewAsset> Assets => m_Assets;

        void Awake() => m_ProjectService = GetComponent<ProjectServiceClient>();

        void Start()
        {
            if (m_AutoStart)
                StartCoroutine(Run());
        }

        public string BuildTarget { get; private set; }

        public IEnumerator Run()
        {
            BuildTarget = PreviewPlatform.CurrentBuildTarget;
            if (BuildTarget == null)
            {
                Fail($"platform {Application.platform} has no Project Service build target");
                yield break;
            }

            if (string.IsNullOrEmpty(m_ProjectService.OrgId) ||
                (string.IsNullOrEmpty(m_ProjectService.ProjectGuid) && string.IsNullOrEmpty(m_ProjectName)))
            {
                Fail("no organization/project: link the project in Project Settings > Services, or set them on ProjectServiceClient");
                yield break;
            }

            if (m_ProjectService.RequiresSignIn)
            {
                CurrentState = State.WaitingForSignIn;
                yield return WaitForSignIn();
                if (CurrentState == State.Failed) yield break;
            }

            yield return ResolveProject();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.CreatingWorkbench;
            yield return EnsureWorkbench();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.Validating;
            yield return Validate();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.ResolvingRevision;
            yield return ResolveRevision();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.PreparingEnvironment;
            yield return EnsureProfileAndEnvironment();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.DiscoveringAssets;
            yield return Discover();
            if (CurrentState == State.Failed) yield break;

            CurrentState = State.Ready;
            Debug.Log($"[PreviewSession] ready: target={BuildTarget} env={EnvironmentId} revision={Revision} assets={m_Assets.Count}");

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

        // Unity Cloud needs a signed-in user before any request. An interactive provider waits for the
        // user to press Sign in; a non-interactive one (an injected token) must already have a token.
        IEnumerator WaitForSignIn()
        {
            var provider = m_ProjectService.TokenProvider;
            if (provider == null)
            {
                Fail("Unity Cloud mode needs a token provider on the rig (CompositeAuthTokenProvider)");
                yield break;
            }

            var signIn = provider as IPreviewSignIn;
            if (signIn == null)
            {
                if (!provider.HasToken)
                    Fail("the token provider has no token");
                yield break;
            }

            while (!signIn.IsSignedIn)
                yield return null;
        }

        // Resolve the project name to its repo GUID when the client wasn't given one directly (the GUID
        // is minted by the backing VCS and changes whenever the local server is re-seeded).
        IEnumerator ResolveProject()
        {
            if (!string.IsNullOrEmpty(m_ProjectService.ProjectGuid))
                yield break;

            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Project(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectName),
                null, r => res = r);
            if (!res.Ok)
            {
                Fail($"resolve project '{m_ProjectName}' failed: {res.Error}");
                yield break;
            }

            var guid = JsonUtility.FromJson<ProjectResolveResponse>(res.Text)?.guid;
            if (string.IsNullOrEmpty(guid))
            {
                Fail($"project '{m_ProjectName}' has no guid");
                yield break;
            }
            m_ProjectService.SetProjectGuid(guid);
        }

        IEnumerator EnsureWorkbench()
        {
            // Reuse first: an existing workbench with this name (e.g. a seeded one) is what we want, and
            // creating locally would need a VCS source the client can't reach.
            ProjectServiceClient.JsonResult list = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Workbenches(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid),
                null, r => list = r);
            if (!list.Ok)
            {
                Fail($"list workbenches failed: {list.Error}");
                yield break;
            }

            var workbenches = JsonUtility.FromJson<WorkbenchListResult>(list.Text)?.workbenches;
            if (workbenches != null)
                foreach (var wb in workbenches)
                    if (wb.name == m_WorkbenchName)
                    {
                        m_WorkbenchId = wb.workbenchId;
                        yield break;
                    }

            if (string.IsNullOrEmpty(m_Repo))
            {
                Fail($"no workbench named '{m_WorkbenchName}' and no repo set to create one");
                yield break;
            }

            var body = JsonUtility.ToJson(new CreateWorkbenchRequest
                { type = m_VcsType, repo = m_Repo, branch = m_Branch, name = m_WorkbenchName });

            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("POST", AssetPipelineRoute.Workbenches(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid),
                body, r => res = r);

            if (res.Ok)
            {
                m_WorkbenchId = JsonUtility.FromJson<WorkbenchCreatedResponse>(res.Text)?.workbenchId;
                if (string.IsNullOrEmpty(m_WorkbenchId))
                    Fail("workbench create returned no id");
                yield break;
            }

            if (res.Code == 409)
            {
                var existing = JsonUtility.FromJson<NameConflictResponse>(res.Text)?.workbenchId;
                if (string.IsNullOrEmpty(existing))
                {
                    Fail($"workbench name '{m_WorkbenchName}' conflict without an id to adopt");
                    yield break;
                }
                yield return AdoptWorkbench(existing);
                yield break;
            }

            Fail($"create workbench failed: {res.Error}");
        }

        // A name conflict hands back a workbench id, but the conflict is decided on name alone — verify it
        // tracks the repo we asked for before adopting it.
        IEnumerator AdoptWorkbench(string workbenchId)
        {
            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Workbench(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid, workbenchId),
                null, r => res = r);

            if (!res.Ok)
            {
                Fail($"verify existing workbench failed: {res.Error}");
                yield break;
            }

            var details = JsonUtility.FromJson<WorkbenchDetailsResponse>(res.Text);
            if (details != null && !string.IsNullOrEmpty(details.upstreamRepository) &&
                !string.Equals(details.upstreamRepository, m_Repo, StringComparison.OrdinalIgnoreCase))
            {
                Fail($"workbench '{m_WorkbenchName}' tracks '{details.upstreamRepository}', not '{m_Repo}' — pick another name");
                yield break;
            }
            m_WorkbenchId = workbenchId;
        }

        IEnumerator Validate()
        {
            var body = JsonUtility.ToJson(new PatchWorkbenchRequest { type = "sync" });
            ProjectServiceClient.JsonResult res = null;
            yield return m_ProjectService.SendJson("PATCH", AssetPipelineRoute.Workbench(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid, m_WorkbenchId),
                body, r => res = r);

            if (!res.Ok)
            {
                Fail($"validate workbench failed: {res.Error}");
                yield break;
            }

            var sync = JsonUtility.FromJson<WorkbenchSyncResponse>(res.Text);
            if (sync == null || sync.status != "validated")
            {
                var detail = sync?.error != null ? $"{sync.error.category}: {sync.error.message}" : res.Text;
                Fail($"workbench validation did not pass ({detail})");
            }
        }

        IEnumerator ResolveRevision()
        {
            // A just-validated workbench normally reports its settled revision immediately, but allow a
            // couple of polls for the validation record to surface.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                ProjectServiceClient.JsonResult res = null;
                yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.WorkbenchHead(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid, m_WorkbenchId),
                    null, r => res = r);

                if (!res.Ok)
                {
                    Fail($"read workbench head failed: {res.Error}");
                    yield break;
                }

                var head = JsonUtility.FromJson<WorkbenchHeadResponse>(res.Text);
                if (head != null && !head.settling && !string.IsNullOrEmpty(head.settledRevision))
                {
                    Revision = head.settledRevision;
                    yield break;
                }
                yield return new WaitForSeconds(2f);
            }
            Fail("workbench has no settled revision to pin");
        }

        IEnumerator EnsureProfileAndEnvironment()
        {
            string profileId = null;

            ProjectServiceClient.JsonResult list = null;
            yield return m_ProjectService.SendJson("GET", AssetPipelineRoute.Profiles(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid),
                null, r => list = r);
            if (list.Ok)
            {
                var profiles = JsonUtility.FromJson<ProfileListResponse>(list.Text)?.profiles;
                if (profiles != null)
                    foreach (var p in profiles)
                        if (p.buildTarget == BuildTarget)
                        {
                            profileId = p.profileId;
                            break;
                        }
            }

            if (string.IsNullOrEmpty(profileId))
            {
                var body = JsonUtility.ToJson(new CreateProfileRequest
                    { name = PreviewPlatform.DefaultProfileName(BuildTarget), buildTarget = BuildTarget });
                ProjectServiceClient.JsonResult res = null;
                yield return m_ProjectService.SendJson("POST", AssetPipelineRoute.Profiles(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid),
                    body, r => res = r);
                if (!res.Ok)
                {
                    Fail($"create profile failed: {res.Error}");
                    yield break;
                }
                profileId = JsonUtility.FromJson<ProfileResponse>(res.Text)?.profileId;
            }

            if (string.IsNullOrEmpty(profileId))
            {
                Fail("could not resolve a profile id");
                yield break;
            }

            var envBody = JsonUtility.ToJson(new CreateEnvironmentRequest
                { profileGuid = profileId, workbenchGuid = m_WorkbenchId, name = PreviewPlatform.DefaultEnvironmentName(BuildTarget) });
            ProjectServiceClient.JsonResult envRes = null;
            yield return m_ProjectService.SendJson("POST", AssetPipelineRoute.Environments(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid),
                envBody, r => envRes = r);
            if (!envRes.Ok)
            {
                Fail($"create environment failed: {envRes.Error}");
                yield break;
            }

            EnvironmentId = JsonUtility.FromJson<EnvironmentResponse>(envRes.Text)?.environmentId;
            if (string.IsNullOrEmpty(EnvironmentId))
                Fail("environment create returned no id");
        }

        IEnumerator Discover()
        {
            m_Assets.Clear();
            string cursor = null;
            do
            {
                var url = AssetPipelineRoute.AssetSearch(m_ProjectService.BaseUrl, m_ProjectService.OrgId, m_ProjectService.ProjectGuid,
                    m_WorkbenchId, Revision, m_SearchQuery, m_PageSize, m_SearchScope, "file", cursor);
                ProjectServiceClient.JsonResult res = null;
                yield return m_ProjectService.SendJson("GET", url, null, r => res = r);

                if (!res.Ok)
                {
                    Fail($"asset discovery failed: {res.Error}");
                    yield break;
                }

                var page = JsonUtility.FromJson<AssetSearchResponse>(res.Text);
                if (page?.results != null)
                    foreach (var item in page.results)
                    {
                        if (item.type == "folder" || string.IsNullOrEmpty(item.guid))
                            continue;
                        m_Assets.Add(new PreviewDemoUI.PreviewAsset
                        {
                            // Name from the path basename so it always carries the extension (.mat/.fbx/.prefab…);
                            // the full path lets the picker present the folder hierarchy.
                            Name = !string.IsNullOrEmpty(item.path) ? Basename(item.path) : item.name,
                            Guid = item.guid,
                            Path = item.path
                        });
                    }

                cursor = page?.nextCursor;
                if (!string.IsNullOrEmpty(cursor) && m_Assets.Count >= m_MaxAssets)
                {
                    Debug.LogWarning($"[PreviewSession] stopped discovery at {m_Assets.Count} assets (Max Assets); narrow the Search Scope to see the rest.");
                    break;
                }
            } while (!string.IsNullOrEmpty(cursor));

            if (m_Assets.Count == 0)
                Debug.LogWarning($"[PreviewSession] discovery returned no assets for query '{m_SearchQuery}' in scope '{m_SearchScope}'.");
        }

        static string Basename(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
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
}
