using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

// Imports .pipetxt files as a TextAsset and writes one extra output file per import,
// to try the Unity Pipeline's import "content files" (artifacts).
// Pipeline address: T:{guid}+PipelineTextImporter
[ScriptedImporter(1, "pipetxt")]
public class PipelineTextImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var text = File.ReadAllText(ctx.assetPath);
        var asset = new TextAsset(text);
        ctx.AddObjectToAsset("main", asset);
        ctx.SetMainObject(asset);

        // An extra output file of this import: what should show up as a content file.
        File.WriteAllText(ctx.GetOutputArtifactFilePath("txt"), text.ToUpperInvariant());
    }
}
