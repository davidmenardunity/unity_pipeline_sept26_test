# Unity Pipeline and Project Service

Unity Pipeline gives tools, services and players access to a Unity project without opening the Unity Editor on your machine. Project Service runs the project in the cloud: it checks out your repository, imports it with the Unity Editor, and serves its files, its asset database and its import results through the Pipeline API.

Use the Pipeline API to:

* Browse a project's files and assets at an exact revision, and read their GUIDs, metadata and contents.
* Run importers in the cloud, for the platform you choose, and download what they produce. For example, a content archive (`.ca`) that a player can load at runtime.
* Change files and save the changes as new revisions, without a local checkout.
* Read the same asset on several branches and compare it.
* Publish the changes back to your git repository.

## How it works

```
Your tool or player                              Unity Pipeline (Project Service)
───────────────────                              ────────────────────────────────
Create a workbench on a git branch ────────────▶ checks out the branch, imports the project
Wait until a revision is validated ────────────▶ GET …/workbenches/{wb}/head
Read files and assets at that revision ────────▶ GET …/revisions/{rev}/file-tree, /assets/{guid}, /files/{path}
Request an import for a platform ──────────────▶ POST …/environments/{env}/revisions/{rev}/imports
                                                  runs the importer in an Editor for that platform
Download the artifact (.ca, .png…) ────────────▶ GET …/imports/{address}?artifactName=…
Save changed files as a new revision ──────────▶ PUT …/blobs, POST …/transactions, PATCH …/transactions/{tx}
Publish the workbench to git ──────────────────▶ POST …/workbenches/{wb}/publishes
```

Every read is pinned to a revision, so what you read never changes under you. Every write creates a new revision, which Project Service validates before it can be read.

## Use cases

The use cases in this documentation are working examples, each built against the Pipeline API:

| Use case | What it shows |
| --- | --- |
| [Explore a project and inspect assets](use-case-explore-a-project.md) | Browse the file tree, resolve GUIDs, read `.meta` files and sources, render preview images and run imports. |
| [Preview assets in a runtime player](use-case-preview-assets.md) | Build a content archive for one asset with your own importer, and load it in a WebGL player. |
| [Edit a project](use-case-edit-a-project.md) | Change values in prefabs, scenes, ScriptableObjects and materials, and save them as a new revision. |
| [Compare an asset across branches](use-case-compare-branches.md) | Read one asset from two branches, compare its files and its import results. |
| [Build a level editor](use-case-level-editor.md) | Rebuild a scene from its prefabs' content archives, add and move objects, and save the scene back. |
| [Play whole scenes from content archives](use-case-scene-archives.md) | Build a content archive for a whole scene and load it in a generic player. |

To set up access and a workbench first, refer to [Get started with the Pipeline API](get-started.md). To understand the terms used throughout, refer to [Key concepts](key-concepts.md).

> [!NOTE]
> The Pipeline API is in preview. The routes and request bodies in this documentation are those served by the staging environment in October 2026. Some of them changed during that month; [Troubleshooting](troubleshooting.md) lists the changes that were observed.

## Additional resources

* [Get started with the Pipeline API](get-started.md)
* [Publish workbench changes to git](publish-to-git.md)
* [Troubleshooting](troubleshooting.md)
