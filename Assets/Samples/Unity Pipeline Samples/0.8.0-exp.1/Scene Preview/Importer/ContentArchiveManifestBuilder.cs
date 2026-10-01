using System.IO;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // Writes the small JSON manifest (ContentArchiveManifest) to disk and wraps it as a ResourceFile
    // so the content-archive importer can pack it into the .ca alongside the content file.
    public static class ContentArchiveManifestBuilder
    {
        public static string WriteManifest(string outputFolder, bool requiresDefaultResources, string contentFileName)
        {
            var manifest = new ManifestData
            {
                contentFileName = contentFileName,
                requiresDefaultResources = requiresDefaultResources
            };
            var json = JsonUtility.ToJson(manifest, prettyPrint: true);
            var manifestPath = Path.Combine(outputFolder, ContentArchiveManifest.ManifestFileName);
            File.WriteAllText(manifestPath, json);
            return manifestPath;
        }

        public static ResourceFile CreateManifestResourceFile(string manifestPath)
        {
            return new ResourceFile
            {
                fileName = manifestPath,
                fileAlias = ContentArchiveManifest.ManifestFileName
            };
        }
    }
}
