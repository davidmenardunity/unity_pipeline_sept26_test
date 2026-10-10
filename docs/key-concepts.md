# Key concepts

This page describes the building blocks of the Pipeline API and how they relate to each other.

## Project Service

Project Service is the cloud runtime that hosts your project for the Pipeline API. It runs per Unity Cloud project, starts on request, and idles out after a period without use (typically overnight). While it's stopped, workbench calls fail; start it with `POST …/projectservice/start` and poll `GET …/projectservice/status` until it reports ready. Refer to [Get started](get-started.md#start-project-service).

## Workbench

A workbench is a working copy of your project in the cloud, created from a git repository and branch. Project Service checks out the branch's latest commit and imports the project. Every read and write goes through a workbench.

Keep the following in mind:

* **A workbench starts from the branch's latest commit and stays there.** It doesn't follow later pushes to git. To work on newer commits, create a new workbench.
* **A workbench's own branch is always `main`.** The `branchName` the API reports is the workbench's internal branch, not the git branch it came from. Only the response to the create request says which git branch a workbench was made from; record it if you need it later.
* **A workbench can only push to git if it was created with a VCS connection.** One created from a public repository URL alone can read the repository but not publish to it. A connection can't be added later. Refer to [Publish workbench changes to git](publish-to-git.md).
* **Workbenches can have a name.** Without one, Project Service names it `wb-{uuid}`.

## Revision

A revision is an immutable state of a workbench. The workbench starts at revision 1, and every change creates a new one. A new revision is validated (imported and checked) before it can be read. `GET …/workbenches/{wb}/head` reports the latest validated revision; read from that revision, never from an unvalidated one.

All read routes take a revision: `…/workbenches/{wb}/revisions/{rev}/…`. There's no "latest" alias.

## Asset, GUID and path

Assets are addressed by path for files (`Assets/Scenes/SampleScene.unity`) and by GUID for asset metadata, imports and previews. `POST …/revisions/{rev}/asset-guid` turns paths into GUIDs. Paths in URLs are one path segment: encode `/` as `%2F` (`Assets%2FScenes%2FSampleScene.unity`).

## Build profile and environment

Imports run for a platform. A build profile names a build target (`win64`, `webgl`, `android`…), and an environment binds a build profile to a workbench. Project Service runs an environment's imports in a Unity Editor started for that platform, so their results match what a player on that platform can load.

Environments and profiles are project-level resources: `…/environments` and `…/profiles`.

## Importer, import address and artifact

An import runs an importer on one asset in an environment. The asset and the importer form an import address:

| Address | Meaning |
| --- | --- |
| `G:{guid}` | The asset's primary import: what the Editor imports it as. |
| `T:{guid}+{importer type}` | One specific importer, named by its full type name, run on the asset. |

`T:` addresses let you run your own importers on any asset. A non-primary `ScriptedImporter` in your project (one that nothing imports with by default) can be run on demand this way. That's how the [preview](use-case-preview-assets.md) and [scene](use-case-scene-archives.md) use cases build content archives.

An import produces artifacts: files the importer wrote, named in the import manifest. The manifest also gives each artifact's content hash, which covers everything that went into it, including the asset's dependencies.

## Blob, transaction and commit

A change is made in three steps:

1. **Upload** each new file's bytes as a blob: `PUT …/workbenches/{wb}/blobs/{contentGuid}`. The answer is an upload handle.
2. **Stage** the files in a transaction: `POST …/transactions` opens one, then `POST …/transactions/{tx}/assets` binds paths to upload handles.
3. **Commit** the transaction with a message: `PATCH …/transactions/{tx} {status: "committed", message}`. The answer names the new revision.

A workbench has at most one open transaction, which aborts on its own after an idle timeout.

## Publish

Publishing pushes a workbench's revisions to its git repository as a commit, `Publish from Pipeline workbench {id}`, on a new `pr-{commit}` branch. You merge that branch into your branch like any other. Refer to [Publish workbench changes to git](publish-to-git.md).

## Additional resources

* [Get started with the Pipeline API](get-started.md)
* [Unity Pipeline and Project Service](index.md)
