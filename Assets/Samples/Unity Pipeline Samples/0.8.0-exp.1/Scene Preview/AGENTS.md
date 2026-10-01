# Working on the Scene Preview sample

`README.md` explains what the sample does and how to set it up. This file lists the rules for
changing the code without breaking it silently. `Runtime/` is the preview player; `Importer/` is
editor-only code that Project Service runs to produce previews.

## Keep these pairs in sync

- **Archive manifest.** `Runtime/ContentArchiveManifest.cs` (reader, `ManifestData`) and
  `Importer/ContentArchiveManifestBuilder.cs` (writer) are one contract. Change both together.
  Keep `requiresDefaultResources` as the last field: the archive's virtual file system read drops
  the final byte.
- **Importer type name.** `PreviewClient`'s **Importer Type** must be the importer's full type name,
  namespace included. Renaming, moving or copying `PreviewContentImporter` means updating that
  field in the scene and the default in `PreviewClient.cs`.
- **Importer registration.** Every `[ScriptedImporter]` needs its own dummy extension. Bump its
  version when the output format changes, so cached artifacts are rebuilt.
- **What can be previewed.** `PreviewLoader.CanPresent` (by file extension) and `BuildInstance`
  (by object type) must agree. The browser and auto-preview only offer what `CanPresent` accepts.
- **Serialized fields and the scene.** Renaming a `[SerializeField]` on a PreviewRig component means
  renaming its key in `VillageCorner_preview.unity` too (or add `[FormerlySerializedAs]`).
  Otherwise the value silently resets.

## Platform

- Never hardcode a platform. The player derives Project Service's build-target short name in
  `Runtime/PreviewPlatform.cs`, the only place these strings live. The importer builds for
  `ctx.selectedBuildTarget`, which Project Service sets by starting its editor on the environment's
  platform.
- To support a new platform, add it to `PreviewPlatform.BuildTargetFor`. The valid short names are
  `android`, `ios`, `linux64`, `osxuniversal`, `webgl`, `win`, `win64`.

## Project Service requests

- All HTTP goes through `ProjectServiceClient` (auth, the 202/job retry protocol) and all URLs
  through `AssetPipelineRoute`. Don't call `UnityWebRequest` elsewhere.
- Read routes take a concrete settled revision from the workbench head; there is no "head" alias.
- The import address `T:{guid}+{importerType}` is one path segment and must be percent-encoded.
  Keep `artifactName` in the query string.
- Unity Cloud mode requires sign-in before any request. Self Hosted mode sends no auth header.

## Loading archives (`PreviewLoader`)

- Never reuse a mount prefix. Remounting one yields an archive whose content load never completes.
  Each load gets a fresh `preview{n}:` prefix and file.
- Tear down in this order: destroy the instance (`DestroyImmediate`), unload the content file,
  then unmount the archive.
- Pass exactly one `ContentFile.GlobalTableDependency` when the manifest says
  `requiresDefaultResources`, and none otherwise. Too many fails as surely as too few.

## When you change the importer

- Changes reach Project Service only through the repository. Commit and push the importer, the
  preview build profile (`Assets/Settings/Build Profiles/ScenePreview.asset`) and `Assets/link.xml`.
- Regenerate `link.xml` (**Tools > Scene Preview > Generate Preview link.xml**) whenever the set of
  assemblies a preview may contain changes. Rebuild the preview player after regenerating it.
- Importers run in out-of-process import workers. Read settings from project files, not from
  `EditorUserSettings` or other in-memory editor state (see `PreviewContextImporter`).

## Verifying

- Offline: build an archive with `PreviewProducer.ProduceFromCommandLine` (see README) and set
  `PreviewClient`'s **Local Archive Path**. This tests mount, load and swap with no network.
- Online: a Self Hosted Project Service on `localhost` needs **Allow downloads over HTTP** in the
  player, and **Run In Background** for an unfocused desktop player.
