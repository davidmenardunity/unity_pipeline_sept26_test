using System;
using System.IO;
using UnityEditor;
using UnityEditor.Experimental;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // Produces a content archive (.ca) for one asset locally, without Project Service: ensures the
    // context artifact exists, runs the default content importer, and copies the .ca out of the
    // artifact cache. Point PreviewClient's Local Archive Path at the result to test the player's
    // mount/load path offline.
    public static class PreviewProducer
    {
        /// <summary>
        /// Produce the .ca for <paramref name="assetPath"/> and return the produced archive's path
        /// in the artifact VFS, or null on failure.
        /// </summary>
        public static string ProduceArchive(string assetPath)
        {
            // The content importer reads the context importer's GlobalUsage artifact, so ensure it is
            // produced first.
            PreviewContextImporter.ProduceContextArtifact();

            var artifactId = PreviewContentImporter.ProduceContentArtifact(assetPath);
            if (!artifactId.isValid)
                return null;

            return PreviewContentImporter.GetContentArchivePath(assetPath);
        }

        /// <summary>Produce the .ca for an asset and copy it to <paramref name="outputPath"/>.</summary>
        public static bool ProduceArchiveToFile(string assetPath, string outputPath)
        {
            var archiveVfsPath = ProduceArchive(assetPath);
            if (string.IsNullOrEmpty(archiveVfsPath))
            {
                Debug.LogError($"[PreviewProducer] Failed to produce content archive for '{assetPath}'.");
                return false;
            }

            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                // Artifact paths are in the VFS; copy out to a real file.
                FileUtil.ReplaceFile(archiveVfsPath, outputPath);
                var size = new FileInfo(outputPath).Length;
                Debug.Log($"[PreviewProducer] Wrote content archive ({size} bytes) for '{assetPath}' → '{outputPath}'.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PreviewProducer] Failed to copy content archive to '{outputPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Command-line entry (batchmode): -previewAsset &lt;assetPath&gt; -previewOut &lt;outFile&gt;.
        /// Exits 0 on success, 1 on failure.
        /// </summary>
        public static void ProduceFromCommandLine()
        {
            string assetPath = GetArg("-previewAsset");
            string outPath = GetArg("-previewOut");
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(outPath))
            {
                Debug.LogError("[PreviewProducer] Usage: -executeMethod Unity.Pipeline.Samples.ScenePreview.Importer.PreviewProducer.ProduceFromCommandLine -previewAsset <path> -previewOut <file>");
                EditorApplication.Exit(1);
                return;
            }

            bool ok = ProduceArchiveToFile(assetPath, outPath);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        internal static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            return null;
        }
    }
}
