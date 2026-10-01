using System;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Request/response payloads for the pipeline broker, shaped for UnityEngine's JsonUtility: fields are
    // named to match the JSON keys verbatim (JsonUtility matches by field name, case-sensitive). Unknown
    // response fields are ignored, so partial DTOs are fine. JsonUtility can't express a missing object:
    // an absent nested object comes back with empty fields, so check its strings, not the reference.

    // ── workbenches ──────────────────────────────────────────────────────────

    [Serializable]
    public class WorkbenchItem
    {
        public string workbenchId;
        public string branchName;
        public string upstreamRepository;
        public string upstreamRevision;   // the commit the workbench was created from
        public string createdAt;          // ISO 8601
    }

    [Serializable]
    public class WorkbenchListResult
    {
        public WorkbenchItem[] items;
    }

    [Serializable]
    public class CreateWorkbenchRequest
    {
        public string type;   // "git"
        public string branch;
        public string repo;   // public https git URL
    }

    [Serializable]
    public class ValidationError
    {
        public string category;
        public string message;
    }

    [Serializable]
    public class WorkbenchValidation
    {
        public string status;   // e.g. in_progress | validated | failed
        public ValidationError error;
    }

    [Serializable]
    public class WorkbenchDetails
    {
        public string workbenchId;
        public string branchName;
        public string upstreamRepository;
        public string upstreamRevision;
        public WorkbenchValidation validation;
    }

    [Serializable]
    public class ReadinessResponse
    {
        public string readiness;         // settled | settling | gone | unknown
        public string branch;
        public string settledRevision;   // empty until the first validation passes
        public string head;
    }

    // ── environments ─────────────────────────────────────────────────────────

    [Serializable]
    public class EnvironmentItem
    {
        public string environmentId;
        public string platform;      // Windows64 | Linux64 | MacOS64 | iOS | Android
        public string workbenchId;   // the list can include other workbenches' environments
        public string name;
    }

    [Serializable]
    public class EnvironmentListResult
    {
        public EnvironmentItem[] items;
    }

    [Serializable]
    public class CreateEnvironmentRequest
    {
        public string platform;
    }

    // ── discovery ────────────────────────────────────────────────────────────

    [Serializable]
    public class ManifestItem
    {
        public string path;        // "/Assets/…" (or a package path)
        public string assetGuid;
        public bool isFolder;
    }

    [Serializable]
    public class ManifestPage
    {
        public ManifestItem[] items;
        public string nextCursor;
    }

    // ── imports ──────────────────────────────────────────────────────────────

    [Serializable]
    public class ImportRequest
    {
        public string[] addresses;
    }

    [Serializable]
    public class ImportFile
    {
        public string name;          // empty for the importer's main output
        public string contentHash;
    }

    [Serializable]
    public class ImportManifest
    {
        public string importResultId;
        public string[] artifacts;   // names of the importer's extra output files, e.g. "….ca"
        public ImportFile[] files;
    }

    [Serializable]
    public class SlotError
    {
        public string code;      // e.g. import_not_found: the project has no importer of that type
        public string message;
    }

    [Serializable]
    public class ImportSlot
    {
        public ImportManifest manifest;
        public SlotError error;
    }

    [Serializable]
    public class ImportResults
    {
        public ImportSlot[] results;
    }

    // ── async protocol and errors ────────────────────────────────────────────

    // The 202 envelope: a jobId to poll, then re-issue the original request.
    [Serializable]
    public class AcceptedRetryResponse
    {
        public string jobId;
        public string status;
        public string disposition;   // "retry" | "repin" | "terminal" (Project Service; the broker sends none)
    }

    // GET /jobs/{jobId}.
    [Serializable]
    public class JobSnapshot
    {
        public string jobId;
        public string kind;
        public string state;
        public bool isTerminal;
    }

    [Serializable]
    public class ProblemError
    {
        public string code;
        public string message;
    }

    // A failed call's problem+json body: the server's code, message and request id.
    [Serializable]
    public class ProblemResponse
    {
        public ProblemError error;
        public string title;
        public string detail;
        public string requestId;
    }
}
