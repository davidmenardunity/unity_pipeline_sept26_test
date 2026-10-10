# Get started with the Pipeline API

This page sets up what every use case needs: credentials, the API's hosts, a running Project Service, a workbench at a validated revision, and an environment for imports.

## Prerequisites

* A Unity Cloud organization and project with access to Unity Pipeline.
* A git repository with your Unity project. A public HTTPS URL is enough to read; to publish changes back, you also need a VCS connection (refer to [Publish workbench changes to git](publish-to-git.md)).
* A bearer token: a Unity user token (JWT), or a service account key.

## Hosts and base routes

The Pipeline API has a public host and an internal host:

| Purpose | Staging | Production |
| --- | --- | --- |
| Workbenches, revisions, imports | `https://staging.services.api.unity.com` | `https://services.api.unity.com` |
| Project Service start and status | `https://staging.services.unity.com` | `https://services.unity.com` |

Routes are scoped to an organization and a project:

```
/api/pipeline/v1/organizations/{organizationId}/projects/{projectId}
```

The examples on these pages use the following shell variables:

```bash
export UNITY_JWT='<your bearer token>'
export ORG='<organization id>'
export PROJECT='<project id>'
export API="https://staging.services.api.unity.com/api/pipeline/v1/organizations/$ORG/projects/$PROJECT"
export INTERNAL="https://staging.services.unity.com/api/pipeline/v1/organizations/$ORG/projects/$PROJECT"
alias papi='curl -sS -H "Authorization: Bearer $UNITY_JWT" -H "Content-Type: application/json"'
```

> [!NOTE]
> Never put a token in a URL or commit it to source control. Read it from an environment variable or a secrets store.

The same calls from C#, with `HttpClient`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

var org = "<organization id>";
var project = "<project id>";
var http = new HttpClient
{
    BaseAddress = new Uri($"https://staging.services.api.unity.com/api/pipeline/v1/organizations/{org}/projects/{project}/"),
};
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("UNITY_JWT"));

// GET: list the project's workbenches.
var workbenches = await http.GetFromJsonAsync<WorkbenchList>("workbenches");

// POST with a JSON body: resolve an asset's GUID at a revision.
var response = await http.PostAsJsonAsync($"workbenches/{workbenchId}/revisions/{revision}/asset-guid",
    new { paths = new[] { "Assets/Scenes/SampleScene.unity" } });
response.EnsureSuccessStatusCode();
var guids = await response.Content.ReadFromJsonAsync<GuidResults>();

record WorkbenchList(List<Workbench> Workbenches);
record Workbench(string WorkbenchId, string Name, string BranchName, string UpstreamRevision);
record GuidResults(List<GuidResult> Results, string Revision);
record GuidResult(string AssetGuid);
```

`System.Net.Http.Json` reads the API's camelCase JSON into these records. The rest of this documentation shows REST calls with `curl`; each one maps to `HttpClient` the same way.

## Start Project Service

Project Service idles out when it's not used. Check it, and start it if needed:

```bash
papi "$INTERNAL/projectservice/status"
# {"status":"stopped"}

papi -X POST "$INTERNAL/projectservice/start" -d '{"region":"America"}'
# 202 while it starts; 409 if it's already starting or running
```

Poll the status every few seconds until it reports ready. A cold start takes up to a few minutes.

## Create a workbench

Create a workbench on a git branch. The body takes:

| Field | Required | Description |
| --- | --- | --- |
| `branch` | Yes | The git branch to start from. |
| `repo` | Yes | The repository URL, for example `https://github.com/acme/space-shooter.git`. |
| `type` | No | `git` (default) or `uvcs`. |
| `name` | No | A name. Without one, Project Service generates `wb-{uuid}`. |
| `vcsConnectionId` | No | The VCS connection Project Service uses to reach the repository. Required to publish. |

```bash
papi -X POST "$API/workbenches" -d '{
  "type": "git",
  "branch": "main",
  "repo": "https://github.com/acme/space-shooter.git",
  "name": "space-shooter-main",
  "vcsConnectionId": "9f15569b-52d2-45f4-8a3a-9c872d28c1d0"
}'
```

The answer describes the workbench, including the git branch it was made from. Store that branch: later reads of the workbench only report its internal branch, `main`.

```json
{
  "workbenchId": "2cb776ef-d306-4c95-ae61-baccfeff997e",
  "name": "space-shooter-main",
  "branchName": "main",
  "upstreamRepository": "https://github.com/acme/space-shooter.git",
  "upstreamRevision": "844f8b2…"
}
```

> [!IMPORTANT]
> Finish the create call even if the caller goes away. If you abandon the request, the workbench is created anyway, but you lose the only answer that names its git branch. Record the branch under the workbench's name before you send the request, so you can match it up later.

List and delete workbenches with `GET $API/workbenches` and `DELETE $API/workbenches/{workbenchId}`.

## Wait for a validated revision

A new workbench imports the project before it can be read. Poll its head:

```bash
papi "$API/workbenches/$WB/head"
# {"branch":"main","head":"1","validatedRevision":"","validating":true}
# … later:
# {"branch":"main","head":"1","validatedRevision":"1","validating":false}
```

Read from `validatedRevision` once `validating` is `false`. If validation fails, the workbench's `validation.status` (from `GET $API/workbenches/$WB`) is `failed` and gives the error; a failed validation never settles, so stop polling.

```bash
export REV=1
```

## Create an environment for imports

Imports and previews run in an environment, for one platform. Find or create a build profile for the build target, then an environment that binds it to your workbench:

```bash
papi "$API/profiles"
papi -X POST "$API/profiles" -d '{"name":"webgl","buildTarget":"webgl"}'
# {"profileId":"…","buildTarget":"webgl"}

papi -X POST "$API/environments" -d '{
  "workbenchId": "'"$WB"'",
  "profileId": "<profileId>",
  "platform": "WebGL",
  "name": "webgl-'"${WB:0:8}"'"
}'
# {"environmentId":"342b9903-…","workbenchId":"2cb776ef-…","profileId":"…"}

export ENV=342b9903-ec6d-8f9d-242b-ec83a7858237
```

`GET $API/environments` lists every environment in the project; filter by `workbenchId`.

## Next steps

* [Explore a project and inspect assets](use-case-explore-a-project.md)
* [Edit a project](use-case-edit-a-project.md)

## Additional resources

* [Key concepts](key-concepts.md)
* [Troubleshooting](troubleshooting.md)
