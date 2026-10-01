using System;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Small JSON payload packed into a content archive describing what the archive needs to load —
    // currently the content-file name and whether it references the engine default-resources global
    // table. Written by the Importer's ContentArchiveManifestBuilder; read here at runtime from the
    // mounted archive's VFS so the loader can size the LoadContentFileAsync dependency array per asset.
    [Serializable]
    public class ManifestData
    {
        // Declared before requiresDefaultResources so that JsonUtility (which writes fields in
        // declaration order) puts this string ahead of the trailing bool — the archive VFS read drops
        // the final byte, so only the brace after the last field is ever lost.
        public string contentFileName;
        public bool requiresDefaultResources;
    }

    public static class ContentArchiveManifest
    {
        public const string ManifestFileName = "archive_manifest.json";

        // mountPath is ArchiveHandle.GetMountPath() (ends with a trailing separator). Returns default
        // ManifestData if the archive contains no manifest.
        public static ManifestData ReadManifest(string mountPath)
        {
            var manifestPath = mountPath + ManifestFileName;
            var bytes = ReadAllBytesFromVfs(manifestPath);
            if (bytes == null)
            {
                Debug.LogWarning($"[Preview] No manifest at '{manifestPath}'; assuming defaults.");
                return new ManifestData();
            }

            // The archive VFS read comes back a byte short for this small resource file (the closing
            // brace is dropped), so rather than depend on byte-perfect JSON, match the one flag
            // directly, whitespace-insensitive — robust to that truncation and to formatting.
            var manifest = new ManifestData();
            var text = Encoding.UTF8.GetString(bytes)
                .Replace(" ", string.Empty).Replace("\n", string.Empty)
                .Replace("\r", string.Empty).Replace("\t", string.Empty);
            manifest.requiresDefaultResources = text.Contains("\"requiresDefaultResources\":true");

            const string nameKey = "\"contentFileName\":\"";
            var idx = text.IndexOf(nameKey, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var start = idx + nameKey.Length;
                var end = text.IndexOf('"', start);
                manifest.contentFileName = end > start ? text.Substring(start, end - start) : text.Substring(start);
            }
            return manifest;
        }

        // Files inside a mounted archive live on Unity's virtual file system, so read them through
        // AsyncReadManager rather than System.IO.File.
        static unsafe byte[] ReadAllBytesFromVfs(string vfsPath)
        {
            FileInfoResult info;
            var infoHandle = AsyncReadManager.GetFileInfo(vfsPath, &info);
            infoHandle.JobHandle.Complete();
            infoHandle.Dispose();

            if (info.FileState != FileState.Exists || info.FileSize <= 0)
                return null;

            var size = (int)info.FileSize;
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            try
            {
                var cmd = new ReadCommand
                {
                    Buffer = NativeArrayUnsafeUtility.GetUnsafePtr(buffer),
                    Offset = 0,
                    Size = size
                };

                var readHandle = AsyncReadManager.Read(vfsPath, &cmd, 1);
                readHandle.JobHandle.Complete();
                var status = readHandle.Status;
                readHandle.Dispose();

                if (status != ReadStatus.Complete)
                {
                    Debug.LogError($"[Preview] Failed to read manifest at '{vfsPath}': {status}");
                    return null;
                }

                return buffer.ToArray();
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}
