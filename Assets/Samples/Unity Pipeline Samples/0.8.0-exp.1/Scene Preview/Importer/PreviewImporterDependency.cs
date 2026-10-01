using System;
using UnityEditor;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // A custom dependency both preview importers declare, so the whole pipeline can be invalidated in
    // one call (Invalidate) without touching any asset — used to force a rebuild when importer logic
    // changes during development. The hash is persisted in SessionState so it survives domain reloads
    // within an editor session.
    [InitializeOnLoad]
    public static class PreviewImporterDependency
    {
        public const string DependencyKey = "Unity.Pipeline.Samples.ScenePreview/PreviewImporterVersion";
        const string SessionStateKey = "PreviewImporter_DependencyHash";

        static PreviewImporterDependency()
        {
            RegisterCurrentHash();
        }

        public static void Invalidate()
        {
            var newHash = Guid.NewGuid().ToString();
            SessionState.SetString(SessionStateKey, newHash);
            RegisterCurrentHash();
        }

        static void RegisterCurrentHash()
        {
            var hash = SessionState.GetString(SessionStateKey, "v1");
            AssetDatabase.RegisterCustomDependency(DependencyKey, Hash128.Compute(hash));
        }
    }
}
