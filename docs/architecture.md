# Architecture

This page shows the components involved when a tool or a player works with a Unity project through the Pipeline API, and how requests flow between them. It's a logical view built from the API's observable behavior: Project Service's internal implementation can differ.

## Components

```mermaid
flowchart LR
    subgraph Client["Your side"]
        direction TB
        Tool["Tool or service<br/>(C# HttpClient, curl)"]
        Player["Runtime player<br/>(WebGL, desktop)"]
        Git[("Git repository<br/>(GitHub)")]
    end

    subgraph Cloud["Unity Cloud"]
        direction TB
        API["Pipeline API<br/>services.api.unity.com/api/pipeline/v1"]
        Ctl["Project Service control<br/>…/projectservice/start, /status"]

        subgraph PS["Project Service (per Unity Cloud project)"]
            direction TB
            WB["Workbench<br/>git checkout + revisions"]
            ADB["Asset database<br/>paths, GUIDs, .meta"]
            Blobs[("Blob store<br/>uploaded file contents")]
            Env["Environment<br/>Unity Editor for a build profile"]
            Imp["Importers<br/>G: primary, T: your ScriptedImporters"]
            Art[("Artifact cache<br/>by revision + import address")]
        end

        BA["Build Automation<br/>VCS connections"]
    end

    Tool -- "REST + bearer token" --> API
    Tool -- "start / status" --> Ctl
    Ctl --> PS
    API --> WB
    API --> ADB
    API --> Blobs
    API --> Env
    WB --- ADB
    Env --> Imp --> Art
    API -- "download artifacts (.ca)" --> Art
    Player -- "loads .ca (via your tool or directly)" --> API
    WB -- "clone at create" --> Git
    WB -- "publish: push pr-{commit}" --> Git
    BA -. "credentials (vcsConnectionId)" .-> WB
```

| Component | Role |
| --- | --- |
| **Pipeline API** | The REST surface: `…/organizations/{org}/projects/{project}/…`. Every call takes a Unity bearer token. |
| **Project Service control** | Starts and reports on the project's Project Service. Project Service idles out when unused, and workbench calls fail until it's started again. |
| **Workbench** | A working copy of one git branch at one commit. It holds the project's revisions: revision 1 is the checkout, and every commit adds one. Refer to [Key concepts](key-concepts.md#workbench). |
| **Asset database** | The imported project's index: file tree, GUIDs, `.meta` files and dependencies, per revision. |
| **Blob store** | Holds uploaded file contents until a transaction binds them to paths. |
| **Environment** | A Unity Editor running the project for one build profile (platform). Imports run here, so their output matches what a player on that platform loads. |
| **Importers** | `G:{guid}` runs an asset's primary importer. `T:{guid}+{type}` runs a specific importer, such as your own non-primary `ScriptedImporter`. |
| **Artifact cache** | Import results, keyed by revision, environment and import address. A repeated import at the same revision is served from here. |
| **Build Automation** | Owns VCS connections: the git credentials a workbench uses to publish. |
| **Runtime player** | Your player build. It contains code and shaders; scenes and assets arrive as content archives. |

## Lifecycle of a workbench

```mermaid
sequenceDiagram
    autonumber
    participant T as Your tool
    participant C as Project Service control
    participant API as Pipeline API
    participant PS as Project Service
    participant G as Git

    T->>C: POST …/projectservice/start
    loop until ready (cold start: minutes)
        T->>C: GET …/projectservice/status
    end
    T->>API: POST …/workbenches {repo, branch, name, vcsConnectionId}
    API->>PS: create workbench
    PS->>G: clone branch at its latest commit
    API-->>T: workbench id, git branch
    PS->>PS: import project → revision 1
    loop until validatedRevision ≥ 1
        T->>API: GET …/workbenches/{wb}/head
    end
    Note over T,PS: Reads, imports and writes now use validated revisions
```

A workbench stays on the commit it was created from. To work from newer commits, create a new workbench. Refer to [Troubleshooting](troubleshooting.md#a-workbench-doesnt-have-the-latest-commits).

## Reading and importing

```mermaid
sequenceDiagram
    autonumber
    participant T as Your tool
    participant API as Pipeline API
    participant E as Environment (Editor for a platform)
    participant A as Artifact cache
    participant P as Runtime player

    T->>API: POST …/revisions/{rev}/asset-guid {paths}
    API-->>T: GUIDs
    T->>API: POST …/environments/{env}/revisions/{rev}/imports {importAddresses: [T:{guid}+{importer}]}
    alt first import at this revision
        API->>E: run importer (may compile shaders)
        E->>A: store artifacts + manifest
    else already imported
        A-->>API: cached manifest
    end
    API-->>T: import manifest (artifact names, hashes)
    T->>API: GET …/imports/{address}?artifactName=.ca
    API-->>T: content archive
    T->>P: hand over the archive
    P->>P: mount, load content file or scene, instantiate
```

The first import of an asset at a revision can take minutes, because the environment's Editor imports it, and a scene compiles every shader variant its materials need. Re-send the request until it answers with a manifest. Refer to [Preview assets in a runtime player](use-case-preview-assets.md) and [Play whole scenes from content archives](use-case-scene-archives.md).

## Writing and publishing

```mermaid
sequenceDiagram
    autonumber
    participant T as Your tool
    participant API as Pipeline API
    participant PS as Project Service
    participant G as Git

    T->>API: PUT …/blobs/{md5} (file bytes)
    API-->>T: uploadHandle
    T->>API: POST …/transactions {branchName: "main"}
    API-->>T: transactionId
    T->>API: POST …/transactions/{tx}/assets [{path, uploadHandle}]
    T->>API: PATCH …/transactions/{tx} {status: "committed", message}
    API->>PS: new revision n+1
    PS->>PS: validate (re-import changed assets)
    loop until validatedRevision = n+1
        T->>API: GET …/workbenches/{wb}/head
    end
    T->>API: POST …/workbenches/{wb}/publishes {message}
    PS->>G: push "Publish from Pipeline workbench {id}" to pr-{commit}
    Note over T,G: Merge pr-{commit} into your branch like any other branch
```

Refer to [Edit a project](use-case-edit-a-project.md) and [Publish workbench changes to git](publish-to-git.md).

## What lives where

| Lives in your player build | Arrives in content archives |
| --- | --- |
| Scripts (IL2CPP can't load code at runtime) | Meshes, textures, materials, prefabs |
| Shaders the render pipeline looks up at runtime, such as terrain detail shaders | Shaders the archived materials use, with the variants their scene needs |
| Graphics settings, including which fog and lightmap variants are kept | Whole scenes, with their lighting settings and objects |

This split is why a player that loads archives stays generic: it ships the code and runtime shaders once, and every scene or asset comes from Project Service. Refer to [Keep the player generic](use-case-scene-archives.md#keep-the-player-generic).

## Additional resources

* [Key concepts](key-concepts.md)
* [Get started with the Pipeline API](get-started.md)
* [Troubleshooting](troubleshooting.md)
