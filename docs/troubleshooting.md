# Troubleshooting

Problems you might run into with the Pipeline API, and how to solve them.

## Workbenches and revisions

### Workbench calls fail after a quiet period

**Cause:** Project Service idled out.

**Resolution:** Start it with `POST …/projectservice/start` on the internal host, and poll `…/projectservice/status` until it's ready. Refer to [Start Project Service](get-started.md#start-project-service).

### A workbench doesn't have the latest commits

**Cause:** A workbench starts from its branch's latest commit and stays there. `PATCH …/workbenches/{wb} {"type": "sync"}` answers `validated` but doesn't move the workbench to newer commits.

**Resolution:** Create a new workbench on the branch. Publish the old one first if it has changes to keep.

### You can't tell which git branch a workbench came from

**Cause:** A workbench's `branchName` is its own branch, always `main`. Only the response to the create request names the git branch.

**Resolution:** Store the branch when you create the workbench. If the request is cut off, Project Service still creates the workbench: record the branch under the workbench's name before sending, and match it by name afterwards. As a fallback, compare the workbench's `upstreamRevision` with `git ls-remote`. This only works while branches point to different commits.

### Reads fail with `409 revision_not_validated`

**Cause:** The revision isn't validated yet, which is common right after a workbench starts.

**Resolution:** Read only revisions that `…/head` reports as `validatedRevision`, and retry for a few minutes after a cold start.

## Imports and archives

### `import_failed: Type not found for scriptedImporterTypeName`

**Cause:** The importer named in the `T:` address isn't in the code Project Service compiled. The workbench was created from a commit without it, or the importer's assembly didn't compile there.

**Resolution:** Push the importer, then create a new workbench from a commit that includes it. Check that the type name is the full name, namespace included.

### `import_not_found`

**Cause:** The project has no importer of that type for the asset.

**Resolution:** Check the type name in the address. For `G:` addresses, the asset may not be imported at all, for example because the path is ignored.

### The first import takes minutes, or times out

**Cause:** The environment's Editor starts and imports the asset, and a scene compiles all its shader variants on first import.

**Resolution:** Re-send the request until it answers with a manifest. Later imports at the same revision are cached.

### Materials render pink in a WebGL player

**Cause:** Their shaders need shader model 4.5, which WebGL 2 doesn't support.

**Resolution:** Use WebGPU in the player, with WebGL 2 as the fallback.

### A scene from an archive renders black and white, or with no ambient light

**Cause:** Lighting data that Unity computes when it loads a scene the usual way, such as a skybox-based ambient probe, isn't computed for scenes loaded from content archives.

**Resolution:** Use trilight or flat ambient light in scenes you load from archives.

### Terrain grass renders as one flat color in a scene loaded from an archive

**Cause:** Terrain grass uses the player's own URP shaders. Unity stripped their fog variants because the player's own scene has no fog.

**Resolution:** In **Project Settings > Graphics**, set fog modes to **Custom** and keep Linear, Exponential and Exponential Squared. Then rebuild the player.

### Components are missing in a scene loaded from an archive

**Cause:** The player wasn't built with the scripts the scene uses. Archives can't carry code on IL2CPP platforms.

**Resolution:** Put gameplay code in assemblies the player includes, preserve them with a `link.xml`, and rebuild the player.

## Saving and publishing

### `Publish` fails with `could not read Username for 'https://github.com'`

**Cause:** The workbench was created without a VCS connection, so Project Service has no credentials to push.

**Resolution:** Create a new workbench with `vcsConnectionId`, move your changes to it, and publish from there. Refer to [Publish workbench changes to git](publish-to-git.md).

### Creating a VCS connection fails with `Repository URL is not reachable: Git ls-remote failure`

**Cause:** Build Automation couldn't read the repository over HTTPS. In testing this happened for GitHub even with valid credentials, and for public repositories.

**Resolution:** Connect GitHub repositories with OAuth in the Unity Cloud Dashboard (**Build Automation** > **Source control**).

### A model pushed from a DCC tool imports with a blank material

**Cause:** The model's material name changed, for example `Tree_6A_D` became `Tree_6A_D.004` (Blender adds suffixes when a file already has a material of that name). The model's import settings map materials by name.

**Resolution:** Export materials with their original names. Or, in the model's import settings, set **Materials > Location** to **Use Embedded Materials** and remap the new name to the existing material (the `externalObjects` entries in the `.meta`). The legacy **Use External Materials** location finds materials by name and creates the ones it can't find, so it ignores remapping.

### A workbench says it can't open a transaction

**Cause:** A workbench holds one open transaction at a time, and a previous one wasn't committed or aborted.

**Resolution:** Abort transactions you don't commit (`DELETE …/transactions/{tx}`). An abandoned transaction aborts on its own after an idle timeout.

## API changes observed in October 2026

The Pipeline API changed during October 2026. If you call it from code written before, check for these changes:

| Before | After |
| --- | --- |
| `…/projects/{p}/branches/{branch}/projectservice/…` | `…/projects/{p}/projectservice/…` |
| `…/workbenches/{wb}/readiness` | `…/workbenches/{wb}/head` (`validatedRevision`, `validating`) |
| `…/workbenches/{wb}/environments` | Project-level `…/environments` and `…/profiles` |
| `POST …/asset-guid {"keys": […]}` | `POST …/asset-guid {"paths": […]}` |
| `POST …/imports {"addresses": […]}` | `POST …/imports {"importAddresses": […]}` |
| `POST …/workbenches/{wb}/batch` | Transactions (`…/transactions`) |
| `POST …/previews` then a job | `GET …/environments/{env}/revisions/{rev}/previews/{guid}`, `202` until ready |

## Additional resources

* [Get started with the Pipeline API](get-started.md)
* [Key concepts](key-concepts.md)
