using System;
using System.Text;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Builds Project Service asset-pipeline URLs. The Unity Services gateway (services.unity.com) exposes
    // the same paths Project Service serves directly, so only the host differs — and segments containing
    // ':' or '+' must be percent-encoded, because the gateway does not accept them raw.
    public static class AssetPipelineRoute
    {
        public const string UnityCloudGateway = "https://services.unity.com";

        // {baseUrl}/api/assetpipeline/v1/organizations/{org}/projects/{proj}
        static string ProjectBase(string baseUrl, string orgId, string projectGuid)
            => $"{baseUrl.TrimEnd('/')}/api/assetpipeline/v1/organizations/{Uri.EscapeDataString(orgId)}" +
               $"/projects/{Uri.EscapeDataString(projectGuid)}";

        /// <summary>A project resolved by GUID or name (GET returns its repo GUID).</summary>
        public static string Project(string baseUrl, string orgId, string projectId)
            => ProjectBase(baseUrl, orgId, projectId);

        /// <summary>Collection of workbenches (POST to create, GET to list).</summary>
        public static string Workbenches(string baseUrl, string orgId, string projectGuid)
            => $"{ProjectBase(baseUrl, orgId, projectGuid)}/workbenches";

        /// <summary>A single workbench (GET details, PATCH to sync/validate).</summary>
        public static string Workbench(string baseUrl, string orgId, string projectGuid, string workbenchId)
            => $"{ProjectBase(baseUrl, orgId, projectGuid)}/workbenches/{Uri.EscapeDataString(workbenchId)}";

        /// <summary>The workbench head: branch head + last settled (validated) revision.</summary>
        public static string WorkbenchHead(string baseUrl, string orgId, string projectGuid, string workbenchId)
            => $"{Workbench(baseUrl, orgId, projectGuid, workbenchId)}/head";

        /// <summary>Build profiles (GET to list, POST to create).</summary>
        public static string Profiles(string baseUrl, string orgId, string projectGuid)
            => $"{ProjectBase(baseUrl, orgId, projectGuid)}/profiles";

        /// <summary>Environments (POST to bind a profile to a workbench).</summary>
        public static string Environments(string baseUrl, string orgId, string projectGuid)
            => $"{ProjectBase(baseUrl, orgId, projectGuid)}/environments";

        /// <summary>A background job's status snapshot.</summary>
        public static string Job(string baseUrl, string orgId, string projectGuid, string jobId)
            => $"{ProjectBase(baseUrl, orgId, projectGuid)}/jobs/{Uri.EscapeDataString(jobId)}";

        /// <summary>
        /// Search a workbench revision's assets. <paramref name="revision"/> must be a concrete settled
        /// changeset id (there is no "head" alias on read routes). An empty <paramref name="query"/> with
        /// a <paramref name="scope"/> lists that folder subtree; <paramref name="typeFilter"/> "file" or
        /// "folder" narrows the kind. <paramref name="cursor"/> is a previous page's <c>nextCursor</c>.
        /// </summary>
        public static string AssetSearch(
            string baseUrl, string orgId, string projectGuid, string workbenchId, string revision,
            string query, int limit, string scope = null, string typeFilter = null, string cursor = null)
        {
            var sb = new StringBuilder(ProjectBase(baseUrl, orgId, projectGuid));
            sb.Append("/workbenches/").Append(Uri.EscapeDataString(workbenchId));
            sb.Append("/revisions/").Append(Uri.EscapeDataString(revision));
            sb.Append("/asset-search?q=").Append(Uri.EscapeDataString(query ?? string.Empty));
            if (!string.IsNullOrEmpty(scope))
                sb.Append("&scope=").Append(Uri.EscapeDataString(scope));
            if (!string.IsNullOrEmpty(typeFilter))
                sb.Append("&type=").Append(Uri.EscapeDataString(typeFilter));
            if (limit > 0)
                sb.Append("&limit=").Append(limit);
            if (!string.IsNullOrEmpty(cursor))
                sb.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
            return sb.ToString();
        }

        /// <summary>
        /// GET URL for an asset's on-demand import artifact (e.g. the preview content archive).
        /// <paramref name="importerType"/> is the non-primary scripted importer to run, e.g.
        /// "Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter"; <paramref name="artifactName"/> is its output, e.g. "ca".
        /// </summary>
        public static string ImportArtifact(
            string baseUrl, string orgId, string projectGuid, string environmentId,
            string revision, string assetGuid, string importerType, string artifactName)
        {
            // Encode "T:{guid}+{importer}" as a single path segment: ':' -> %3A, '+' -> %2B.
            var importAddress = Uri.EscapeDataString($"T:{assetGuid}+{importerType}");
            // artifactName is a query value, not a path segment: the primary artifact's name is empty
            // (or "."), which path normalisation would collapse — a query value carries any name faithfully.
            return $"{ProjectBase(baseUrl, orgId, projectGuid)}" +
                   $"/environments/{Uri.EscapeDataString(environmentId)}" +
                   $"/revisions/{Uri.EscapeDataString(revision)}" +
                   $"/imports/{importAddress}?artifactName={Uri.EscapeDataString(artifactName)}";
        }
    }
}
