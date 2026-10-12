# Architecture

This page shows the parts involved when a tool or a player works with a Unity project through the Pipeline API, then the three request flows you use most. It's a view built from the API's observable behavior. Project Service's internal implementation can differ.

## Overview

![Your tool and runtime player call the Pipeline API, which fronts Project Service. Project Service holds workbenches, environments and an artifact cache, and clones from and publishes to your git repository with credentials from Build Automation.](images/architecture-overview.svg)

| Part | Role |
| --- | --- |
| **Your tool** | Any code that calls the REST API with a Unity bearer token: a C# service, a script, a DCC add-on. |
| **Runtime player** | Your player build. It contains code and runtime shaders; scenes and assets arrive as content archives (`.ca`). |
| **Pipeline API** | The single entry point: `…/organizations/{org}/projects/{project}/…`. Starting Project Service is the one call that goes to a separate host. Refer to [Get started](get-started.md#start-project-service). |
| **Workbench** | A working copy of one git branch at one commit. Revision 1 is the checkout, and every commit adds a revision. Refer to [Key concepts](key-concepts.md#workbench). |
| **Environment** | A Unity Editor running the project for one build profile (platform). Imports run here, so their output matches what a player on that platform loads. |
| **Artifact cache** | Import results, keyed by revision, environment and import address. Repeating an import at the same revision is fast. |
| **Git repository** | Where a workbench is cloned from, and where publishing pushes a `pr-{commit}` branch. |
| **Build Automation** | Holds the VCS connection, the git credentials a workbench needs to publish. Pass its ID as `vcsConnectionId` when you create the workbench. |

## Flow 1: Start working on a branch (2 calls, then wait)

```mermaid
sequenceDiagram
    participant T as Your tool
    participant API as Pipeline API
    participant PS as Project Service
    participant G as Git

    T->>API: POST …/projectservice/start
    Note over T,PS: Wait for …/projectservice/status to report ready (cold start: minutes)
    T->>API: POST …/workbenches {repo, branch, name, vcsConnectionId}
    PS->>G: clone the branch's latest commit
    API-->>T: workbench id
    Note over T,PS: Wait for GET …/workbenches/{wb}/head to report validatedRevision 1 (minutes)
```

A workbench stays on the commit it was created from. To work from newer commits, create a new workbench. Refer to [Troubleshooting](troubleshooting.md#a-workbench-doesnt-have-the-latest-commits).

## Flow 2: Get an asset into a player (3 calls)

```mermaid
sequenceDiagram
    participant T as Your tool
    participant API as Pipeline API
    participant PS as Project Service
    participant P as Runtime player

    T->>API: POST …/revisions/{rev}/asset-guid {paths}
    API-->>T: GUIDs
    T->>API: POST …/environments/{env}/revisions/{rev}/imports {importAddresses}
    PS->>PS: run the importer in the environment's Editor, or reuse the cached result
    API-->>T: import manifest
    T->>API: GET …/imports/{address}?artifactName=.ca
    API-->>T: content archive
    T->>P: hand over the archive to mount and load
```

The first import of an asset at a revision can take minutes, and a scene compiles every shader variant its materials need. Re-send the request until it answers with a manifest. Refer to [Preview assets in a runtime player](use-case-preview-assets.md) and [Play whole scenes from content archives](use-case-scene-archives.md).

## Flow 3: Save a change and publish it (5 calls, then wait)

```mermaid
sequenceDiagram
    participant T as Your tool
    participant API as Pipeline API
    participant PS as Project Service
    participant G as Git

    T->>API: PUT …/blobs/{md5}
    T->>API: POST …/transactions
    T->>API: POST …/transactions/{tx}/assets
    T->>API: PATCH …/transactions/{tx} {status: committed}
    PS->>PS: validate the new revision
    Note over T,PS: Wait for GET …/workbenches/{wb}/head to report the new revision (seconds)
    T->>API: POST …/workbenches/{wb}/publishes
    PS->>G: push pr-{commit}
```

Merge the `pr-{commit}` branch into your branch like any other. Refer to [Edit a project](use-case-edit-a-project.md) and [Publish workbench changes to git](publish-to-git.md).

## What lives where

| In your player build | In content archives |
| --- | --- |
| Scripts (IL2CPP can't load code at runtime) | Meshes, textures, materials, prefabs |
| Shaders the render pipeline looks up at runtime, such as terrain detail shaders | Shaders the archived materials use, with the variants their scene needs |
| Graphics settings, including which fog and lightmap variants are kept | Whole scenes, with their lighting settings and objects |

This split is why a player that loads archives stays generic: it ships the code and runtime shaders once, and every scene or asset comes from Project Service. Refer to [Keep the player generic](use-case-scene-archives.md#keep-the-player-generic).

## Additional resources

* [Key concepts](key-concepts.md)
* [Get started with the Pipeline API](get-started.md)
* [Troubleshooting](troubleshooting.md)
