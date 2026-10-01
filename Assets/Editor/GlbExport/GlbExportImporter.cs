using System;
using System.IO;
using GLTFast.Export;
using GLTFast.Logging;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

// Imports a .glbexport recipe: exports the model or prefab it names to glTF-Binary with
// glTFast, and writes the GLB as an extra output file of the import, which the Unity
// Pipeline serves as an import "content file".
//
// Recipe (JSON):    { "source": "Assets/Models/Tree.fbx" }
// Pipeline address: T:{recipe guid}+GlbExportImporter
[ScriptedImporter(1, "glbexport")]
public class GlbExportImporter : ScriptedImporter
{
    [Serializable]
    class Recipe
    {
        public string source;
    }

    public override void OnImportAsset(AssetImportContext ctx)
    {
        var summary = Export(ctx);
        var main = new TextAsset(summary);
        ctx.AddObjectToAsset("main", main);
        ctx.SetMainObject(main);
    }

    // Returns a one-line summary for the main object; problems go to the import log.
    static string Export(AssetImportContext ctx)
    {
        var source = JsonUtility.FromJson<Recipe>(File.ReadAllText(ctx.assetPath))?.source?.Trim();
        if (string.IsNullOrEmpty(source))
        {
            ctx.LogImportError($"{ctx.assetPath}: the recipe has no \"source\" path");
            return "error: no source";
        }

        // Re-import when the source's import result changes; also what allows loading it here.
        ctx.DependsOnArtifact(source);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(source);
        if (prefab == null)
        {
            ctx.LogImportError($"{ctx.assetPath}: {source} isn't a model or prefab (or doesn't exist)");
            return $"error: {source} not found";
        }

        var instance = UnityEngine.Object.Instantiate(prefab);
        instance.name = prefab.name;
        instance.hideFlags = HideFlags.HideAndDontSave;
        var logger = new CollectingLogger();
        bool ok;
        byte[] glb;
        try
        {
            var export = new GameObjectExport(
                new ExportSettings
                {
                    Format = GltfFormat.Binary,
                    ImageDestination = ImageDestination.MainBuffer,   // one self-contained .glb
                    Deterministic = true,                             // same input, same bytes
                },
                logger: logger);
            export.AddScene(new[] { instance }, prefab.name);
            using var stream = new MemoryStream();
            // An importer can't await: forceSync runs the whole export on this thread.
            ok = export.SaveToStreamAndDispose(stream, forceSync: true).Result;
            glb = stream.ToArray();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }

        if (logger.Items != null)
        {
            foreach (var item in logger.Items)
            {
                var message = $"glTFast: {LogMessages.GetFullMessage(item.Code, item.Messages)}";
                if (item.Type is LogType.Error or LogType.Exception) ctx.LogImportError(message);
                else if (item.Type == LogType.Warning) ctx.LogImportWarning(message);
            }
        }
        if (!ok || glb.Length == 0)
        {
            ctx.LogImportError($"{ctx.assetPath}: glTFast couldn't export {source}");
            return $"error: export of {source} failed";
        }

        File.WriteAllBytes(ctx.GetOutputArtifactFilePath("glb"), glb);
        return $"{source} -> {glb.Length} bytes of GLB";
    }
}
