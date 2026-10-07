using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview.Importer
{
    // The link.xml that decides which types survive into a preview content file: generating it, and
    // reading it back as an allow-list. PreviewContentImporter both filters by it and declares it as
    // an import dependency, so the path lives here rather than being spelled out at each use site.
    public static class PreviewLinkXml
    {
        public const string AssetPath = "Assets/link.xml";

        public static (HashSet<string> types, HashSet<string> assemblies) ParseAllowList(
            string linkXmlPath = AssetPath)
        {
            var allowedTypes = new HashSet<string>();
            var allowedAssemblies = new HashSet<string>();

            if (!File.Exists(linkXmlPath))
            {
                Debug.LogWarning($"link.xml not found at {linkXmlPath}, no type filtering will be applied");
                return (allowedTypes, allowedAssemblies);
            }

            var doc = XDocument.Load(linkXmlPath);
            foreach (var assemblyElement in doc.Descendants("assembly"))
            {
                var assemblyName = assemblyElement.Attribute("fullname")?.Value;
                if (string.IsNullOrEmpty(assemblyName))
                    continue;

                var typeElements = assemblyElement.Elements("type").ToList();
                foreach (var typeElement in typeElements)
                {
                    var fullname = typeElement.Attribute("fullname")?.Value;
                    if (!string.IsNullOrEmpty(fullname))
                        allowedTypes.Add(fullname);
                }

                // An <assembly> with no <type> children means the whole assembly is preserved
                // (preserve="all", or no preserve attribute at all — both mean "preserve everything"
                // per Unity's link.xml format).
                if (typeElements.Count == 0)
                    allowedAssemblies.Add(assemblyName);
            }

            return (allowedTypes, allowedAssemblies);
        }

        // Prefixes of assemblies whose types should be fully preserved. "UnityEngine" covers every
        // built-in engine module, which already includes uGUI's UnityEngine.UI and UI Toolkit's
        // UnityEngine.UIElementsModule (both ship as engine modules, not separate package assemblies).
        // VFX Graph, URP/render-pipelines-core and TextMeshPro ship as their own Unity.* package
        // assemblies instead, so they need their own prefixes.
        static readonly string[] EngineAssemblyPrefixes =
        {
            "UnityEngine",
            "Unity.RenderPipelines",
            "Unity.VisualEffectGraph",
            "Unity.TextMeshPro",
        };

        // Engine modules never preserved: they hold no content types, and forcing them into a player
        // breaks platforms that don't ship them. A WebGL player fails to link with TLSModule
        // ("wasm-ld: undefined symbol: unitytls"); UnityCurlModule is the native HTTP stack WebGL doesn't use.
        static readonly HashSet<string> ExcludedAssemblies = new()
        {
            "UnityEngine.TLSModule",
            "UnityEngine.UnityCurlModule",
        };

        public static void Generate(string outputPath)
        {
            var engineAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Where(name => !name.EndsWith(".Editor")
                    && EngineAssemblyPrefixes.Any(prefix => name.StartsWith(prefix))
                    && !ExcludedAssemblies.Contains(name))
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<linker>");

            foreach (var assemblyName in engineAssemblyNames)
                sb.AppendLine($"  <assembly fullname=\"{assemblyName}\" preserve=\"all\"/>");

            sb.AppendLine("</linker>");

            File.WriteAllText(outputPath, sb.ToString());
            Debug.Log($"Generated link.xml at: {outputPath}");
        }

        [MenuItem("Tools/Scene Preview/Generate Preview link.xml")]
        public static void GenerateForProject()
        {
            // AssetPath is project-relative and the editor's working directory is the project root, so
            // this writes exactly the file PreviewContentImporter reads and depends on.
            Generate(AssetPath);
            AssetDatabase.Refresh();
        }
    }
}
