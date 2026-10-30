using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.DataModel.Yaml;

namespace Unity.DataModel
{
    // Changes to a file's structure: creating, deleting and re-parenting objects, adding and removing components.
    // Each is applied right away: pending value changes are written, the lines are edited, and the file is read
    // again, so objects found before a structural change must be found again (UnityObject.Generation).
    public sealed partial class UnityFile
    {
        static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

        // Write pending changes, edit the lines, read the result.
        void Restructure(Action<List<string>> edit)
        {
            if (values.Count > 0 || addedModifications.Count > 0 || addedDocuments.Count > 0) Load(ToText());
            var lines = yaml.Lines.ToList();
            edit(lines);
            Load(string.Join(yaml.Eol, lines));
        }

        // New documents go before SceneRoots (Unity keeps it last), else at the end of the file.
        int DocumentInsertIndex(List<string> lines)
        {
            var index = SceneRoots?.Document.Line ?? lines.Count;
            while (index > 0 && lines[index - 1].Trim().Length == 0) index--;
            return index;
        }

        // Lines [start, end) of a document.
        (int Start, int End) DocumentLines(YamlDocument doc)
        {
            var next = yaml.Documents.Select(d => d.Line).Where(l => l > doc.Line).DefaultIfEmpty(yaml.Lines.Count).Min();
            return (doc.Line, next);
        }

        // An item appended to a list (m_Children, m_Component, m_Roots); a flow list ("[]") becomes a block list.
        static void AppendItem(List<string> lines, YamlNode list, string item)
        {
            if (list.Kind == YamlNodeKind.FlowSequence)
            {
                lines[list.Line] = lines[list.Line].Substring(0, list.Start).TrimEnd();
                lines.Insert(list.Line + 1, new string(' ', list.Indent) + "- " + item);
                return;
            }
            if (list.Kind != YamlNodeKind.Sequence) throw new InvalidOperationException($"{list.Path} isn't a list.");
            lines.Insert(list.LastLine + 1, new string(' ', list.ListIndent ?? list.Indent) + "- " + item);
        }

        // Remove a list's items that match; an emptied list is written as "[]", as Unity does.
        static int RemoveItems(List<string> lines, YamlNode? list, Func<YamlNode, bool> match)
        {
            if (list?.Kind != YamlNodeKind.Sequence) return 0;
            var gone = list.Children.Where(match).ToList();
            if (gone.Count == 0) return 0;
            foreach (var item in gone.OrderByDescending(i => i.Line))
                lines.RemoveRange(item.Line, item.LastLine - item.Line + 1);
            if (gone.Count == list.Children.Count) lines[list.Line] = lines[list.Line].TrimEnd() + " []";
            return gone.Count;
        }

        static bool Refers(YamlNode item, long id) => ObjectReference.From(item.Child("component") ?? item)?.FileId == id;

        // ── creating ────────────────────────────────────────────────────────

        /// <summary>
        /// Create a GameObject (empty, or a built-in primitive: "Cube", "Sphere"…) named <paramref name="name"/>,
        /// under the transform <paramref name="parentTransformId"/> (0: the scene's root). Returns its file ID.
        /// </summary>
        public long CreateGameObject(string name, string? primitive = null, long parentTransformId = 0)
        {
            if (!string.IsNullOrEmpty(primitive) && Templates.PrimitiveName(primitive) == null)
                throw new ArgumentException($"{primitive} isn't a primitive: {string.Join(", ", Templates.Primitives)}.", nameof(primitive));
            CheckParent(parentTransformId);
            var (docLines, goId, tId) = Templates.GameObject(primitive, NewFileId);
            Restructure(lines => lines.InsertRange(DocumentInsertIndex(lines), docLines));
            Find<GameObject>(goId)!.SetString("m_Name", name);
            Attach(tId, parentTransformId);
            return goId;
        }

        /// <summary>Add a component by type name ("Rigidbody", "BoxCollider", "Light"…). Returns its file ID.</summary>
        public long AddComponent(long gameObjectId, string type)
        {
            var go = Find<GameObject>(gameObjectId) ?? throw new KeyNotFoundException($"&{gameObjectId} isn't a GameObject in this file.");
            if (go.IsStripped) throw new InvalidOperationException($"&{gameObjectId} is inside a prefab instance; add components to the prefab.");
            var (docLines, id) = Templates.Component(type, gameObjectId, NewFileId);
            return AddComponentDocument(go, docLines, id);
        }

        /// <summary>Add a script (a MonoBehaviour by its script asset's GUID). Its fields take their defaults when Unity loads it.</summary>
        public long AddScript(long gameObjectId, string scriptGuid, string? classIdentifier = null)
        {
            var go = Find<GameObject>(gameObjectId) ?? throw new KeyNotFoundException($"&{gameObjectId} isn't a GameObject in this file.");
            var id = NewFileId();
            var docLines = new List<string>
            {
                $"--- !u!114 &{Id(id)}", "MonoBehaviour:", "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}", "  m_PrefabAsset: {fileID: 0}", $"  m_GameObject: {{fileID: {Id(gameObjectId)}}}",
                "  m_Enabled: 1", "  m_EditorHideFlags: 0", $"  m_Script: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}",
                "  m_Name: ", $"  m_EditorClassIdentifier: {classIdentifier ?? ""}",
            };
            return AddComponentDocument(go, docLines, id);
        }

        long AddComponentDocument(GameObject go, List<string> docLines, long id)
        {
            var goId = go.FileId;
            Restructure(lines =>
            {
                lines.InsertRange(DocumentInsertIndex(lines), docLines);
                var list = Find<GameObject>(goId)!.Find("m_Component") ?? throw new InvalidOperationException($"&{goId} has no m_Component list.");
                AppendItem(lines, list, $"component: {{fileID: {Id(id)}}}");
            });
            return id;
        }

        /// <summary>
        /// Add an instance of a prefab under the transform <paramref name="parentTransformId"/> (0: the scene's root),
        /// named <paramref name="name"/>, at <paramref name="local"/>. Applied right away. Returns its file ID.
        /// </summary>
        public long InstantiatePrefab(string sourcePrefabGuid, PrefabRoot root, string? name, Pose local, long parentTransformId = 0)
        {
            CheckParent(parentTransformId);
            var id = AddPrefabInstance(sourcePrefabGuid, root, name, local);
            if (parentTransformId == 0) { Restructure(_ => { }); return id; }
            // Under a parent: the instance names it, and the parent lists the instance root's stand-in transform.
            addedRoots.Remove(id);
            Restructure(_ => { });
            Find<PrefabInstance>(id)!.SetRaw("m_Modification.m_TransformParent.fileID", Id(parentTransformId));
            var strippedId = EnsureStrippedRoot(id, root);
            Restructure(lines => AppendItem(lines, Find<Transform>(parentTransformId)!.Find("m_Children")!, $"{{fileID: {Id(strippedId)}}}"));
            return id;
        }

        // The stand-in ("stripped") transform for an instance's root: the one the file has, else a new one (any
        // unused ID: Unity keeps the IDs a file gives them).
        long EnsureStrippedRoot(long instanceId, PrefabRoot root)
        {
            var inst = Find<PrefabInstance>(instanceId)!;
            var existing = Objects.OfType<Transform>().FirstOrDefault(t => t.IsStripped && t.PrefabInstanceId == instanceId
                && t.GetReference("m_CorrespondingSourceObject")?.FileId == root.RootTransformId);
            if (existing != null) return existing.FileId;
            var id = NewFileId();
            var guid = inst.SourcePrefab.Guid;
            var docLines = new List<string>
            {
                $"--- !u!4 &{Id(id)} stripped", "Transform:",
                $"  m_CorrespondingSourceObject: {{fileID: {Id(root.RootTransformId)}, guid: {guid}, type: 3}}",
                $"  m_PrefabInstance: {{fileID: {Id(instanceId)}}}", "  m_PrefabAsset: {fileID: 0}",
            };
            Restructure(lines => lines.InsertRange(DocumentInsertIndex(lines), docLines));
            return id;
        }

        void CheckParent(long parentTransformId)
        {
            if (parentTransformId == 0) return;
            var parent = Find<Transform>(parentTransformId) ?? throw new KeyNotFoundException($"&{parentTransformId} isn't a Transform in this file.");
            if (parent.IsStripped) throw new InvalidOperationException("The parent is inside a prefab instance; objects can't be added inside a prefab instance here.");
        }

        // Make a transform (or a prefab instance's root) a child of parentTransformId, or a root.
        void Attach(long transformId, long parentTransformId)
        {
            if (parentTransformId == 0)
            {
                var roots = SceneRoots?.Find("m_Roots");
                if (roots != null) Restructure(lines => AppendItem(lines, roots, $"{{fileID: {Id(transformId)}}}"));
                return;
            }
            Find<Transform>(transformId)!.SetRaw("m_Father.fileID", Id(parentTransformId));
            Restructure(lines => AppendItem(lines, Find<Transform>(parentTransformId)!.Find("m_Children")!, $"{{fileID: {Id(transformId)}}}"));
        }

        // ── re-parenting ────────────────────────────────────────────────────

        /// <summary>
        /// Move a GameObject (by its GameObject or Transform ID) or a prefab instance under the transform
        /// <paramref name="parentTransformId"/> (0: the scene's root). With <paramref name="local"/>, its local
        /// pose is set too (to keep it where it was in the world, the caller computes it). The instance's prefab
        /// <paramref name="root"/> is needed when a prefab instance moves under a parent.
        /// </summary>
        public void SetParent(long objectId, long parentTransformId, Pose? local = null, PrefabRoot? root = null)
        {
            CheckParent(parentTransformId);
            if (Find(objectId) is PrefabInstance inst) { SetInstanceParent(inst.FileId, parentTransformId, local, root); return; }
            var t = (Find(objectId) as GameObject)?.Transform ?? Find<Transform>(objectId)
                ?? throw new KeyNotFoundException($"&{objectId} isn't a GameObject, Transform or prefab instance in this file.");
            if (t.IsStripped) throw new InvalidOperationException("That object is inside a prefab instance; move the instance instead.");
            var tId = t.FileId;
            for (var p = Find<Transform>(parentTransformId); p != null; p = p.Father)
                if (p.FileId == tId) throw new InvalidOperationException("An object can't be moved under itself or one of its children.");
            var oldFather = t.FatherId;
            Restructure(lines =>
            {
                if (oldFather != 0) RemoveItems(lines, Find<Transform>(oldFather)?.Find("m_Children"), i => Refers(i, tId));
                else RemoveItems(lines, SceneRoots?.Find("m_Roots"), i => Refers(i, tId));
            });
            if (parentTransformId == 0) Find<Transform>(tId)!.SetRaw("m_Father.fileID", "0");
            Attach(tId, parentTransformId);
            if (local is { } pose) { Find<Transform>(tId)!.SetLocalPose(pose); Restructure(_ => { }); }
        }

        void SetInstanceParent(long instanceId, long parentTransformId, Pose? local, PrefabRoot? root)
        {
            var inst = Find<PrefabInstance>(instanceId)!;
            var stand = Objects.OfType<Transform>().Where(t => t.IsStripped && t.PrefabInstanceId == instanceId).Select(t => t.FileId).ToList();
            var oldParent = inst.TransformParentId;
            Restructure(lines =>
            {
                if (oldParent != 0) RemoveItems(lines, Find<Transform>(oldParent)?.Find("m_Children"), i => stand.Any(s => Refers(i, s)));
                else RemoveItems(lines, SceneRoots?.Find("m_Roots"), i => Refers(i, instanceId));
            });
            Find<PrefabInstance>(instanceId)!.SetRaw("m_Modification.m_TransformParent.fileID", Id(parentTransformId));
            if (parentTransformId == 0)
            {
                var roots = SceneRoots?.Find("m_Roots");
                if (roots != null) Restructure(lines => AppendItem(lines, roots, $"{{fileID: {Id(instanceId)}}}"));
            }
            else
            {
                Restructure(_ => { });
                var strippedId = root != null ? EnsureStrippedRoot(instanceId, root)
                    : stand.FirstOrDefault(s => Find(s) != null) is var s && s != 0 ? s
                    : throw new InvalidOperationException("The prefab's root is needed to move this instance under a parent.");
                Restructure(lines => AppendItem(lines, Find<Transform>(parentTransformId)!.Find("m_Children")!, $"{{fileID: {Id(strippedId)}}}"));
            }
            if (local is { } pose)
            {
                if (root == null) throw new InvalidOperationException("The prefab's root is needed to place this instance.");
                Find<PrefabInstance>(instanceId)!.SetRootPose(root, pose);
                Restructure(_ => { });
            }
        }

        // ── deleting ────────────────────────────────────────────────────────

        /// <summary>
        /// Delete a GameObject with its components and children, or a prefab instance with its stand-in objects.
        /// References to what was deleted become <c>{fileID: 0}</c>. Returns the file IDs deleted.
        /// </summary>
        public IReadOnlyList<long> Delete(long objectId)
        {
            var gone = new HashSet<long>();
            void Collect(UnityObject? o)
            {
                switch (o)
                {
                    case PrefabInstance inst when gone.Add(inst.FileId):
                        foreach (var part in Objects.Where(x => x.GetReference("m_PrefabInstance")?.FileId == inst.FileId)) gone.Add(part.FileId);
                        // Objects parented under the instance's stand-in transforms go too.
                        foreach (var t in Objects.OfType<Transform>().Where(t => !t.IsStripped && gone.Contains(t.FatherId)).ToList()) Collect(t.GameObject);
                        foreach (var i in Objects.OfType<PrefabInstance>().Where(i => gone.Contains(i.TransformParentId)).ToList()) Collect(i);
                        break;
                    case GameObject go when gone.Add(go.FileId):
                        foreach (var c in go.Components) gone.Add(c.FileId);
                        if (go.Transform is { } tr)
                        {
                            foreach (var childId in tr.ChildIds)
                                Collect(Find(childId) is Transform ct ? ct.IsStripped ? Find(ct.PrefabInstanceId) : ct.GameObject : null);
                            foreach (var i in Objects.OfType<PrefabInstance>().Where(i => i.TransformParentId == tr.FileId).ToList()) Collect(i);
                        }
                        break;
                    case Transform t when !t.IsStripped:
                        Collect(t.GameObject);
                        break;
                }
            }
            var target = Find(objectId) ?? throw new KeyNotFoundException($"&{objectId} isn't in this file.");
            if (!(target is GameObject || target is PrefabInstance || target is Transform))
                throw new InvalidOperationException($"&{objectId} is a {target.TypeName}; delete GameObjects or prefab instances (or use RemoveComponent).");
            Collect(target);
            // First the list items that point at what goes (children, roots, components), bottom-up…
            Restructure(lines =>
            {
                foreach (var o in Objects.Where(o => !gone.Contains(o.FileId)).OrderByDescending(o => o.Document.Line))
                    foreach (var key in new[] { "m_Children", "m_Roots", "m_Component" })
                        RemoveItems(lines, o.Find(key), i => ObjectReference.From(i.Child("component") ?? i) is { IsLocal: true } r && gone.Contains(r.FileId));
            });
            // …then the documents, last first.
            Restructure(lines =>
            {
                foreach (var doc in yaml.Documents.Where(d => gone.Contains(d.FileId)).OrderByDescending(d => d.Line))
                {
                    var (start, end) = DocumentLines(doc);
                    lines.RemoveRange(start, end - start);
                }
            });
            ClearReferences(gone);
            return gone.ToList();
        }

        /// <summary>Remove a component (not the Transform).</summary>
        public void RemoveComponent(long componentId)
        {
            var c = Find(componentId) ?? throw new KeyNotFoundException($"&{componentId} isn't in this file.");
            if (c is Transform) throw new InvalidOperationException("A GameObject's Transform can't be removed.");
            var goId = c.GetReference("m_GameObject")?.FileId ?? 0;
            Restructure(lines => RemoveItems(lines, Find<GameObject>(goId)?.Find("m_Component"), i => Refers(i, componentId)));
            Restructure(lines =>
            {
                var (start, end) = DocumentLines(Find(componentId)!.Document);
                lines.RemoveRange(start, end - start);
            });
            ClearReferences(new HashSet<long> { componentId });
        }

        // Local references to deleted objects become {fileID: 0}.
        void ClearReferences(HashSet<long> gone)
        {
            var any = false;
            foreach (var o in Objects)
                foreach (var node in Walk(o.Root))
                    if (node.Kind == YamlNodeKind.Flow && node.Item("guid") == null && node.Item("fileID") is { } id
                        && long.TryParse(id.Raw.Trim(), out var f) && gone.Contains(f) && !node.ReadOnly)
                    { o.File.SetRaw(o, id, "0"); any = true; }
            if (any) Restructure(_ => { });
        }

        static IEnumerable<YamlNode> Walk(YamlNode n)
        {
            yield return n;
            foreach (var c in n.Children) foreach (var d in Walk(c)) yield return d;
        }
    }
}
