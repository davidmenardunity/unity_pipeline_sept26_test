# Scene Preview

Preview your project's assets in a real runtime build, inside a scene you control.

A scene preview is a scene built only for previewing assets. It has your lighting,
post-processing, camera and quality settings, and runs on your target device. Build a player that
contains only that scene, and it can pull any asset from your project through Project Service and
show it. That includes a mesh an artist pushed a minute ago, or a material the art director wants to
compare, without a full game build.

This sample is a working scene preview, meant to be read and copied. It shows how to:

1. **Build a preview scene and a build profile** that produces a player containing only that scene.
2. **Talk to Project Service at runtime:** sign in with Unity, set up a workbench and environment,
   browse the project's assets, and request an importer artifact for one of them.
3. **Use the import result.** Here it is a content archive (`.ca`) that the player mounts, loads and
   places on a pedestal.

The importer that produces the archive is in the sample too. Treat it as a starting point for your
own importer, which can produce whatever your preview player needs for whichever asset types you preview.

## How it works

Everything lives in your game project. The preview scene is just another scene and build profile in it.

```
Preview player (built from your project)               Project Service (serving your project's repo)
────────────────────────────────────────               ─────────────────────────────────────────────
Sign in with Unity ─────────────────────────────────▶  (Unity Services gateway, bearer token)
PreviewSession ── reuse or create a workbench ──────▶  GET/POST …/workbenches
               ── sync it, pin its settled revision ▶  PATCH …/workbenches/{id}, GET …/head
               ── profile + environment for this   ─▶  GET/POST …/profiles, POST …/environments
                  player's platform
               ── list a folder ────────────────────▶  GET …/revisions/{rev}/asset-search?scope=…
PreviewClient  ── request one asset's archive ──────▶  GET …/imports/T:{guid}+{importer}?artifactName=ca
                                                        Project Service runs the importer in an editor
                                                        started for the environment's platform
PreviewLoader  ── mount the .ca, load it, put it on the pedestal
```

The platform is derived automatically. The player works out its own platform
(`PreviewPlatform.cs`) and creates an environment for it. Project Service runs imports for that
environment in an editor started on that platform, so the archive always matches the player.
Nothing platform-specific needs configuring.

## What's in this sample

| Path | Purpose |
|------|---------|
| `VillageCorner_preview.unity` | The preview scene: lighting, a volume, an orbit camera, a pedestal, and the **PreviewRig** GameObject holding the runtime components. |
| `Runtime/` | Player code (`Unity.Pipeline.Samples.ScenePreview`). `ProjectServiceClient` does all HTTP, including auth and the 202/job retry protocol. `AssetPipelineRoute` builds the URLs. `PreviewPlatform` holds the platform strings sent to Project Service. `PreviewSession` handles setup, `PreviewClient` requests the archive, `PreviewLoader` mounts, loads and swaps it, `PreviewDemoUI` is the IMGUI browser and Sign in button, and `PreviewOrbitCamera` is the camera. |
| `Runtime/Auth/` | `CompositeAuthTokenProvider`: Unity sign-in through `com.unity.cloud.identity`. |
| `Importer/` | Editor code (`Unity.Pipeline.Samples.ScenePreview.Importer`). `PreviewContentImporter` turns one asset into a `.ca`. `PreviewContextImporter` records the preview scene's shader-feature usage. It also includes the preview build profile and `link.xml` helpers, and `PreviewProducer` for building archives offline. |
| `PreviewSubjects/` | Example assets to preview: a church, a tower and a large tree. |
| `AGENTS.md` | Rules for changing the code safely: contracts between the runtime and the importer, and platform, request and loading constraints. Useful to read before adapting the sample, whether by hand or with an agent. |
| `Art/`, `Terrain/`, `Shaders/`, `VillageCorner/`, `URP/`, `*.asset` | The village backdrop and its URP setup (derived from Unity's Fantasy Kingdom sample). |

## Requirements

- Unity 6000.0 or later, and a URP project. The samples package brings in URP and
  `com.unity.cloud.identity`.
- The project linked to a Unity Cloud organization and project in **Project Settings > Services**.
- The project's repository reachable by Project Service (git or Unity Version Control).
- Build support for each platform you preview on.

## Set up a scene preview

1. **Import the sample** into your game project and open `VillageCorner_preview.unity`. If the
   project has no URP asset assigned, assign `URP/SampleURP.asset` in **Project Settings >
   Graphics**. Later you'll likely replace the village with your own preview scene: your lighting, your
   volume, your camera. Keep the **PreviewRig**.
2. **Create the preview build profile.** Set the platform you preview on as active, keep the
   preview scene open, and run **Tools > Scene Preview > Create Preview Build Profile**. This creates
   `Assets/Settings/Build Profiles/ScenePreview.asset`, a profile whose only scene is the preview
   scene. You build the preview player from it. The importer also reads its scene list to strip
   preview content for that scene's lighting and fog modes.
3. **Generate `link.xml`** with **Tools > Scene Preview > Generate Preview link.xml**. It lists the
   engine and package assemblies a preview may contain. The player keeps them, and the importer drops
   anything else, so an archive never references a type the player stripped.
4. **Commit and push**, including the profile, `link.xml`, the sample and all `.meta` files.
   Project Service imports from the repository, so the importer and the preview build profile must be in it.
5. **Configure the PreviewRig.** Org and project fill in automatically from the Unity Cloud link.
   Choose how the player finds its workbench on `PreviewSession`:
   - **Preview what artists push:** set **Workbench Name** to the workbench your artists push to.
     The player reuses it and shows its latest settled revision.
   - **Browse without a workbench:** set **Repo** and **Branch**. If no workbench named **Workbench
     Name** exists, the player creates one from that repository.

   Set **Search Scope** to the folder to browse (for example `/Assets/Art/Characters`, or
   `/Assets/Samples/…/Scene Preview/PreviewSubjects` to try the included subjects).
6. **Build the preview player** from the preview build profile and run it. Press **Sign in**, complete the
   Unity login in the browser, then **Browse assets** and pick a prefab, model or material.

The first request for an asset can take a while, because Project Service has to sync the workbench and
start an editor. The player shows each step in its status line. Later requests are quick.

## PreviewRig reference

| Component | Fields |
|-----------|--------|
| `ProjectServiceClient` | **Mode**: *Unity Cloud* (default: the public gateway, signed in) or *Self Hosted* (a Project Service your organization runs; no auth header). **Base Url**: `https://services.unity.com`, or your service's address. **Org Id** and **Project Guid**: the Unity Cloud organization and project, filled in from Project Settings > Services. Plus the retry/wait budgets. |
| `CompositeAuthTokenProvider` | Unity sign-in. No fields. It supplies the bearer token for every request. To authenticate differently (CI, an injected token), replace it with `InjectedTokenProvider` or your own `IPreviewTokenProvider`. |
| `PreviewSession` | **Workbench Name**, **VCS Type**, **Repo**, **Branch** (see step 5), **Search Scope**, **Search Query**, **Page Size** and **Max Assets** (discovery follows pages up to that cap). **Project Name** is used only in Self Hosted mode when no project id is set. The platform, profile and environment are derived; they are not fields. |
| `PreviewClient` | **Importer Type**: the full type name Project Service runs, `Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter` by default. **Artifact Name**: `ca`. **Local Archive Path**: see below. |
| `PreviewLoader` | **Material Display Mesh**: what a previewed material is shown on (a sphere if empty). **Ground Y**: the pedestal top. |
| `PreviewDemoUI` | A fallback asset list, used only when discovery returns nothing. |

## Make it your own

- **Your own preview scene:** build any scene you like. Put a PreviewRig in it, point
  `PreviewLoader`'s **Ground Y** at your stand, and add the scene to the preview build profile.
- **Your own importer:** copy `Importer/PreviewContentImporter.cs`, rename the class and its dummy
  extension, and change `BuildContentArchive` to produce what your preview player needs. Examples: different
  object filters, extra data in the archive manifest, or a different output altogether. Point
  `PreviewClient`'s **Importer Type** at the new class. `ContentArchiveManifest` (runtime) and
  `ContentArchiveManifestBuilder` (importer) are the contract between the two sides; change them
  together.
- **Your own presentation:** `PreviewLoader` shows the archive's first `GameObject`, or else its
  first `Material`. Replace `BuildInstance` to handle other types, or to present them differently,
  and extend `PreviewLoader.CanPresent` so the browser and auto-preview offer them.
- **Your own UI:** read `PreviewSession.Assets` once `PreviewSession.IsReady` is true, then call
  `PreviewClient.RequestPreview(assetGuid)`. `PreviewLoader.IsPreparing` and `HasPreview` report
  progress. `PreviewDemoUI` is just one consumer.
- **Another platform:** build the preview player for it, adding it to `PreviewPlatform` if it isn't listed.
  The environment, the import and the archive all follow automatically.

## Self Hosted Project Service

Organizations that run their own Project Service (on-prem, or `localhost` in development) set
**Mode** to *Self Hosted* and **Base Url** to that service. No auth header is sent; the service uses
its own configured credential. Set **Org Id**, and either **Project Guid** or **Project Name**, to
the service's own identifiers. For a plain `http://` address, also enable **Allow downloads over
HTTP** in **Player Settings > Other Settings**. Unity blocks insecure requests by default.

## Testing the preview player without Project Service

`PreviewProducer` builds an archive locally:

```
Unity -batchmode -projectPath <your project> -buildTarget <platform> \
  -executeMethod Unity.Pipeline.Samples.ScenePreview.Importer.PreviewProducer.ProduceFromCommandLine \
  -previewAsset Assets/…/Church.prefab -previewOut /tmp/church.ca
```

Set `PreviewClient`'s **Local Archive Path** to the output. The browser then opens immediately with
the scene's fallback asset list, and every preview request loads that file. This exercises mount,
load and swap with no network and no sign-in.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `no organization/project` | Link the project in **Project Settings > Services**, then reselect the PreviewRig so the fields fill in, or set them by hand. |
| Stuck on "Sign in to Unity…" | Press **Sign in** and finish the browser login. |
| `Setup failed: list workbenches failed: … HTTP 401` (or 403) | The signed-in user has no access to that organization or project in Project Service. |
| `no workbench named '…' and no repo set to create one` | Set **Repo** and **Branch**, or use the name of an existing workbench. |
| `workbench validation did not pass` | Project Service could not sync the repository. The message includes its error category. |
| Discovery returns no assets | **Search Scope** doesn't match a folder in the repository. Paths start with `/Assets/`. |
| The import request fails | The importer or the preview build profile isn't committed to the repository, or the build-support module for the platform isn't installed where Project Service runs. |
| The archive downloads but won't load | The player stripped types the archive references. Regenerate `link.xml` and rebuild the preview player. |
| Nothing happens on a desktop build while unfocused | Enable **Run In Background** in Player Settings, or keep the player window focused. |
