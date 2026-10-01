using System;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Request/response payloads for the Project Service asset-pipeline API, shaped for UnityEngine's
    // JsonUtility: fields are named to match the JSON keys verbatim (JsonUtility matches by field name,
    // case-sensitive), so they are camelCase — and snake_case on the job snapshot, which the /jobs
    // routes serialise that way. Only the request fields the sample always sends are declared; optional
    // fields we never set (e.g. a UVCS connection id) are omitted so JsonUtility never emits them empty.
    // Unknown response fields are ignored by JsonUtility, so partial DTOs are fine.

    [Serializable]
    public class CreateWorkbenchRequest
    {
        public string type;   // "git" or "uvcs"
        public string repo;
        public string branch;
        public string name;
    }

    [Serializable]
    public class WorkbenchCreatedResponse
    {
        public string workbenchId;
        public string name;
        public string revision;
    }

    // GET /projects/{projectId} resolves a project by GUID or name to its repo GUID.
    [Serializable]
    public class ProjectResolveResponse
    {
        public string name;
        public string guid;
    }

    [Serializable]
    public class WorkbenchSummary
    {
        public string workbenchId;
        public string name;
        public string type;
        public string upstreamRepository;
        public string upstreamRevision;
    }

    [Serializable]
    public class WorkbenchListResult
    {
        public WorkbenchSummary[] workbenches;
    }

    // The 409 name_conflict body hands back the existing workbench's id so the caller can adopt it.
    [Serializable]
    public class NameConflictResponse
    {
        public string code;
        public string workbenchId;
    }

    // GET /workbenches/{id} — only the fields needed to verify an adopted workbench points at our repo.
    [Serializable]
    public class WorkbenchDetailsResponse
    {
        public string upstreamRepository;
        public string upstreamRevision;
    }

    [Serializable]
    public class PatchWorkbenchRequest
    {
        public string type;   // "sync"
    }

    [Serializable]
    public class SyncError
    {
        public string category;
        public string message;
    }

    [Serializable]
    public class WorkbenchSyncResponse
    {
        public string status;   // "validated" or "failed"
        public SyncError error;
    }

    [Serializable]
    public class WorkbenchHeadResponse
    {
        public string branch;
        public string head;
        public string settledRevision;   // omitted until first validation; may lag head
        public bool settling;
    }

    [Serializable]
    public class CreateProfileRequest
    {
        public string name;
        public string buildTarget;
    }

    [Serializable]
    public class ProfileResponse
    {
        public string profileId;
        public string name;
        public string buildTarget;
    }

    [Serializable]
    public class ProfileListResponse
    {
        public ProfileResponse[] profiles;
    }

    [Serializable]
    public class CreateEnvironmentRequest
    {
        public string profileGuid;
        public string workbenchGuid;
        public string name;
    }

    [Serializable]
    public class EnvironmentResponse
    {
        public string environmentId;
        public string name;
        public string profileId;
        public string workbenchId;
    }

    [Serializable]
    public class AssetSearchItem
    {
        public string path;
        public string guid;
        public string name;
        public string type;   // "file" or "folder"
    }

    [Serializable]
    public class AssetSearchResponse
    {
        public string workbenchVersion;
        public AssetSearchItem[] results;
        public string nextCursor;
    }

    // The 202 retry envelope. jobId/links are present only on the job-backed shape; the minimal shape
    // carries just disposition. See ProjectServiceClient for how these drive the re-issue loop.
    [Serializable]
    public class AcceptedRetryResponse
    {
        public string jobId;
        public string status;
        public string disposition;   // "retry" | "repin" | "terminal"
    }

    // GET /jobs/{jobId} — the job routes serialise snake_case, unlike the camelCase workbench routes.
    [Serializable]
    public class JobSnapshot
    {
        public string job_id;
        public string status;   // queued | processing | cancelling | cancelled | completed | failed
        public int progress_percentage;
        public string log_message;
        public int error_code;
        public string error_message;
    }
}
