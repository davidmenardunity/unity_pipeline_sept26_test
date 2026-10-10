# Publish workbench changes to git

Push the revisions saved on a workbench back to your git repository. Project Service commits the workbench's changes and pushes them to a new branch, which you merge like any other branch.

## Prerequisites

* A workbench created **with a VCS connection** (`vcsConnectionId`). A workbench created from a public repository URL alone can read the repository but can't push to it, and a connection can't be added to a workbench later.
* Changes saved on the workbench. Refer to [Edit a project](use-case-edit-a-project.md).

## Set up a VCS connection

A VCS connection holds the credentials Project Service uses to reach your repository. Connections belong to a Unity Cloud project and are managed by Unity Build Automation.

List the project's connections:

```bash
curl -sS -H "Authorization: Bearer $UNITY_JWT" \
  "https://build-automation.staging.services.api.unity.com/v3/orgs/$ORG/projects/$PROJECT/connections"
```

```json
[
  {
    "id": "9f15569b-52d2-45f4-8a3a-9c872d28c1d0",
    "name": "main",
    "type": "oauth",
    "gitProvider": "github",
    "url": "https://github.com/acme/space-shooter.git"
  }
]
```

Check that a connection works with `POST …/connections/{id}/validate`, which answers `204` when Build Automation can reach the repository.

To create a connection for a GitHub repository, use the Unity Cloud Dashboard: **Build Automation** > project settings > **Source control**, and connect GitHub with OAuth. Build Automation's internal API also has `POST …/v2/orgs/{orgid}/projects/{projectid}/connections` (`type`, `url`, `name`, and `user`/`pass` for HTTPS git). For GitHub repositories, though, it refused HTTPS credentials in testing with `Repository URL is not reachable: Git ls-remote failure`, even for public repositories. Use OAuth for GitHub.

> [!NOTE]
> The `…/connections/{connectionid}/hooks/commit` endpoint doesn't create connections. It's the webhook your git provider calls on each push, for a connection that already exists.

Then create the workbench with the connection's ID:

```bash
papi -X POST "$API/workbenches" -d '{
  "branch": "main",
  "repo": "https://github.com/acme/space-shooter.git",
  "name": "main-oct-10",
  "vcsConnectionId": "9f15569b-52d2-45f4-8a3a-9c872d28c1d0"
}'
```

## Publish

Publish the workbench, with an optional commit message:

```bash
papi -X POST "$API/workbenches/$WB/publishes" -d '{"message": "Squirrel fur: warmer brown"}'
```

Publishing can take minutes. The request either completes with the result, or answers `202` with a job to wait for (`GET $API/jobs/{jobId}`).

* `409 publish_drafted`: the changes landed on a draft branch instead. That's an outcome, not a failure.
* An empty `publishedRevision`: there was nothing new to publish.

## Merge the published branch

Project Service pushes a commit named `Publish from Pipeline workbench {workbenchId}` to a new branch named `pr-{commit}`. The branch starts from the commit the workbench was created from, not from your branch's current head.

```bash
git fetch origin
git log --oneline origin/main..origin/pr-32faab9bff313d6a2782df1f3728e25e02027195
# 32faab9 Publish from Pipeline workbench f53dbc03
# 5a05166 Publish from Pipeline workbench f53dbc03

git checkout main
git merge origin/pr-32faab9bff313d6a2782df1f3728e25e02027195
git push origin main
git push origin --delete pr-32faab9bff313d6a2782df1f3728e25e02027195
```

Each publish from the same workbench creates a new `pr-` branch that includes the earlier publishes, so merge the newest one.

Because the branch starts from an older commit, changes made on your branch since then can conflict, especially in scenes. A scene is a list of separate YAML documents, so you can resolve most scene conflicts object by object:
* keep the documents your branch changed or removed;
* add the documents the publish created;
* take the publish's edits to documents your branch didn't touch;
* merge the `SceneRoots` list the same way.

To take the published scene as it is instead, use `git checkout --theirs -- <scene>`.

## Bring new git commits into a workbench

A workbench doesn't follow its branch. `PATCH $API/workbenches/{wb} {"type": "sync"}` answers `{"status": "validated"}`, but in testing it didn't move the workbench to newer commits. To work from the latest commit, create a new workbench on the branch. Publish the old one first if it has changes to keep.

## Additional resources

* [Edit a project](use-case-edit-a-project.md)
* [Troubleshooting](troubleshooting.md)
