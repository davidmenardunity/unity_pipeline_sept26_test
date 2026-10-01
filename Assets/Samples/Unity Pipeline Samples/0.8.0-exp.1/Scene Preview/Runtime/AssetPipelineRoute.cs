using System;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Builds Unity Pipeline URLs, as the pipeline broker serves them
    // ({gateway}/api/pipeline/v1/organizations/{org}/projects/{project}/…). A path inside a segment, and
    // an import address ("T:{guid}+{importer}"), must be percent-encoded as ONE segment: the gateway
    // matches a path parameter as a single segment and answers a 404 for a literal '/', ':' or '+'.
    public static class AssetPipelineRoute
    {
        public const string StagingGateway = "https://staging.services.api.unity.com";
        public const string ProductionGateway = "https://services.api.unity.com";

        static string E(string segment) => Uri.EscapeDataString(segment ?? string.Empty);

        static string ProjectBase(string baseUrl, string orgId, string projectId)
            => $"{baseUrl.TrimEnd('/')}/api/pipeline/v1/organizations/{E(orgId)}/projects/{E(projectId)}";

        /// <summary>The caller's workbenches (GET lists them, POST creates one).</summary>
        public static string Workbenches(string baseUrl, string orgId, string projectId)
            => $"{ProjectBase(baseUrl, orgId, projectId)}/workbenches";

        /// <summary>One workbench: origin (branch, commit) and validation status.</summary>
        public static string Workbench(string baseUrl, string orgId, string projectId, string workbenchId)
            => $"{Workbenches(baseUrl, orgId, projectId)}/{E(workbenchId)}";

        /// <summary>settled | settling | gone | unknown, with the settled revision reads are pinned to.</summary>
        public static string Readiness(string baseUrl, string orgId, string projectId, string workbenchId)
            => $"{Workbench(baseUrl, orgId, projectId, workbenchId)}/readiness";

        /// <summary>A workbench's environments (GET lists them, POST {platform} creates one).</summary>
        public static string Environments(string baseUrl, string orgId, string projectId, string workbenchId)
            => $"{Workbench(baseUrl, orgId, projectId, workbenchId)}/environments";

        /// <summary>A background job: state and isTerminal.</summary>
        public static string Job(string baseUrl, string orgId, string projectId, string jobId)
            => $"{ProjectBase(baseUrl, orgId, projectId)}/jobs/{E(jobId)}";

        /// <summary>
        /// One page of what the revision's Editor imported (Assets/ and packages). <paramref name="revision"/>
        /// must be a concrete settled revision; <paramref name="cursor"/> is the previous page's nextCursor.
        /// </summary>
        public static string Manifest(string baseUrl, string orgId, string projectId, string workbenchId,
            string revision, int limit, string cursor = null)
            => $"{Workbench(baseUrl, orgId, projectId, workbenchId)}/revisions/{E(revision)}/manifest?limit={limit}" +
               (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + E(cursor));

        /// <summary>The import address of a non-primary importer's result for one asset.</summary>
        public static string ImportAddress(string assetGuid, string importerType) => $"T:{assetGuid}+{importerType}";

        /// <summary>POST {addresses:[…]}: import in the environment and answer one slot per address.</summary>
        public static string Imports(string baseUrl, string orgId, string projectId, string workbenchId,
            string environmentId, string revision)
            => $"{Environments(baseUrl, orgId, projectId, workbenchId)}/{E(environmentId)}/revisions/{E(revision)}/imports";

        /// <summary>One output file of an import, by the name its import manifest lists.</summary>
        public static string ImportArtifact(string baseUrl, string orgId, string projectId, string workbenchId,
            string environmentId, string revision, string importAddress, string artifactName)
            => $"{Imports(baseUrl, orgId, projectId, workbenchId, environmentId, revision)}/{E(importAddress)}/{E(artifactName)}";

        /// <summary>
        /// The same output file, named in the query string (the form Project Service itself serves). Used
        /// only as a fallback when the import manifest doesn't list the artifact by name.
        /// </summary>
        public static string ImportArtifactByQuery(string baseUrl, string orgId, string projectId, string workbenchId,
            string environmentId, string revision, string importAddress, string artifactName)
            => $"{Imports(baseUrl, orgId, projectId, workbenchId, environmentId, revision)}/{E(importAddress)}" +
               $"?artifactName={E(artifactName)}";
    }
}
