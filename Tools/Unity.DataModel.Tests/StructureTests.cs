using Unity.DataModel.Yaml;

namespace Unity.DataModel.Tests;

// Creating, deleting and re-parenting objects, and adding and removing components.
public class StructureTests
{
    static UnityFile Scene() => UnityFile.Parse(Fixture.Scene);

    [Fact]
    public void CreatesAPrimitiveAtTheRoot()
    {
        var file = Scene();
        var id = file.CreateGameObject("Crate", "cube");
        var go = file.Find<GameObject>(id)!;
        Assert.Equal("Crate", go.Name);
        Assert.Equal(new[] { "Transform", "MeshFilter", "BoxCollider", "MeshRenderer" }, go.Components.Select(c => c.TypeName).OrderBy(n => n == "Transform" ? 0 : 1).ThenBy(n => n == "MeshFilter" ? 0 : n == "BoxCollider" ? 1 : 2));
        Assert.Equal(new ObjectReference(10202, "0000000000000000e000000000000000", 0), go.Components.Single(c => c.TypeName == "MeshFilter").GetReference("m_Mesh"));
        Assert.Contains(go.Transform!.FileId, file.SceneRoots!.RootIds);
        Assert.Equal(0, go.Transform.FatherId);
        // Written as text and read back: the same.
        var again = UnityFile.Parse(file.ToText());
        Assert.Equal("Crate", again.Find<GameObject>(id)!.Name);
        Assert.Equal("/Crate", again.Hierarchy.PathOf(id));
        Assert.True(again.Text.IndexOf($"&{id}") < again.Text.IndexOf("--- !u!1660057539"));   // before SceneRoots
    }

    [Fact]
    public void CreatesUnderAParentWithAnEmptyChildList()
    {
        var file = Scene();
        var id = file.CreateGameObject("Lid", null, 201);   // the cube's transform: m_Children: []
        var t = file.Find<GameObject>(id)!.Transform!;
        Assert.Equal(201, t.FatherId);
        Assert.Equal(new[] { t.FileId }, file.Find<Transform>(201)!.ChildIds);
        Assert.Equal(YamlNodeKind.Sequence, file.Find<Transform>(201)!.Find("m_Children")!.Kind);
        Assert.Equal("/Root/Cube: one/Lid", file.Hierarchy.PathOf(id));
        Assert.Equal(new[] { "Transform" }, file.Find<GameObject>(id)!.Components.Select(c => c.TypeName));
    }

    [Fact]
    public void RejectsUnknownPrimitivesAndParents()
    {
        var file = Scene();
        Assert.Throws<ArgumentException>(() => file.CreateGameObject("X", "teapot"));
        Assert.Throws<KeyNotFoundException>(() => file.CreateGameObject("X", null, 999));
        Assert.Throws<InvalidOperationException>(() => file.CreateGameObject("X", null, 301));   // inside a prefab instance
        Assert.Equal(Fixture.Scene, file.ToText());
    }

    [Fact]
    public void AddsAndRemovesComponents()
    {
        var file = Scene();
        var rb = file.AddComponent(100, "Rigidbody");
        Assert.Equal("Rigidbody", file.Find(rb)!.TypeName);
        Assert.Equal(100, file.Find(rb)!.GetReference("m_GameObject")!.Value.FileId);
        Assert.Contains(rb, file.Find<GameObject>(100)!.Components.Select(c => c.FileId));
        var script = file.AddScript(100, "0123456789abcdef0123456789abcdef");
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Find(script)!.GetReference("m_Script")!.Value.Guid);

        file.RemoveComponent(rb);
        file.RemoveComponent(script);
        Assert.Null(file.Find(rb));
        Assert.Equal(new long[] { 101 }, file.Find<GameObject>(100)!.Components.Select(c => c.FileId));
        Assert.Equal(Fixture.Scene, file.ToText());   // back to the bytes it started with
        Assert.Throws<InvalidOperationException>(() => file.RemoveComponent(101));
        Assert.Throws<KeyNotFoundException>(() => file.AddComponent(100, "Teapot"));
    }

    [Fact]
    public void DeletesAnObjectWithItsChildrenAndInstances()
    {
        var file = Scene();
        var gone = file.Delete(100);
        Assert.Equal(new long[] { 100, 101, 200, 201, 202, 300, 301 }, gone.OrderBy(i => i));
        Assert.Equal(new long[] { 400, long.MaxValue }, file.Objects.Select(o => o.FileId));
        Assert.Empty(file.SceneRoots!.RootIds);
        Assert.Contains("m_Roots: []", file.ToText());
    }

    [Fact]
    public void DeletingAChildLeavesTheParent()
    {
        var file = Scene();
        file.Delete(200);
        Assert.Equal(new long[] { 301 }, file.Find<Transform>(101)!.ChildIds);
        file.Delete(300);   // the prefab instance and its stand-in
        Assert.Null(file.Find(301));
        Assert.Contains("  m_Children: []", file.ToText());
        Assert.Single(file.Hierarchy.Roots);
    }

    [Fact]
    public void ReferencesToDeletedObjectsAreCleared()
    {
        var file = Scene();
        var light = file.CreateGameObject("Light");
        var rb = file.AddComponent(light, "Rigidbody");
        file.Delete(200);
        // A field that pointed at the deleted cube would now hold {fileID: 0}; here, the cube's own components are gone too.
        Assert.Null(file.Find(202));
        Assert.NotNull(file.Find(rb));
    }

    [Fact]
    public void ReparentsObjectsAndRefusesCycles()
    {
        var file = Scene();
        file.SetParent(200, 0);
        Assert.Equal(0, file.Find<Transform>(201)!.FatherId);
        Assert.Equal(new long[] { 101, 201 }, file.SceneRoots!.RootIds);
        Assert.Equal(new long[] { 301 }, file.Find<Transform>(101)!.ChildIds);
        Assert.Equal("/Cube: one", file.Hierarchy.PathOf(200));

        file.SetParent(100, 201, new Pose(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One));
        Assert.Equal(201, file.Find<Transform>(101)!.FatherId);
        Assert.Equal(1, file.Find<Transform>(101)!.LocalPosition.X);
        Assert.Equal(new long[] { 201 }, file.SceneRoots!.RootIds);
        Assert.Equal("/Cube: one/Root/Crate", file.Hierarchy.PathOf(300));

        Assert.Throws<InvalidOperationException>(() => file.SetParent(200, 101));   // its own child
    }

    [Fact]
    public void MovesAndPlacesPrefabInstances()
    {
        var file = Scene();
        file.SetParent(300, 0);
        Assert.Equal(0, file.Find<PrefabInstance>(300)!.TransformParentId);
        Assert.Contains(300L, file.SceneRoots!.RootIds);
        Assert.Equal(new long[] { 201 }, file.Find<Transform>(101)!.ChildIds);

        file.SetParent(300, 201, new Pose(new Vector3(0, 2, 0), Quaternion.Identity, Vector3.One), Fixture.CrateRoot);
        Assert.Equal(201, file.Find<PrefabInstance>(300)!.TransformParentId);
        Assert.Equal(new long[] { 301 }, file.Find<Transform>(201)!.ChildIds);   // its existing stand-in
        Assert.DoesNotContain(300L, file.SceneRoots!.RootIds);
        Assert.Equal(2, file.Find<PrefabInstance>(300)!.RootPose(Fixture.CrateRoot).Position.Y);
    }

    [Fact]
    public void InstantiatesUnderAParent()
    {
        var file = Scene();
        var id = file.InstantiatePrefab(Fixture.CrateGuid, Fixture.CrateRoot, "Crate 2", Pose.Identity, 201);
        var inst = file.Find<PrefabInstance>(id)!;
        Assert.Equal(201, inst.TransformParentId);
        var stand = Assert.Single(file.Objects.OfType<Transform>(), t => t.IsStripped && t.PrefabInstanceId == id);
        Assert.Equal(new[] { stand.FileId }, file.Find<Transform>(201)!.ChildIds);
        Assert.Equal("/Root/Cube: one/Crate 2", file.Hierarchy.PathOf(id));
        Assert.DoesNotContain(id, file.SceneRoots!.RootIds);

        var atRoot = file.InstantiatePrefab(Fixture.CrateGuid, Fixture.CrateRoot, "Crate 3", Pose.Identity);
        Assert.Contains(atRoot, file.SceneRoots!.RootIds);
    }

    [Fact]
    public void ObjectsFoundBeforeAStructuralChangeAreStale()
    {
        var file = Scene();
        var t = file.Find<Transform>(201)!;
        file.CreateGameObject("X");
        Assert.Throws<InvalidOperationException>(() => t.SetNumber("m_LocalPosition.x", "5"));
        file.Find<Transform>(201)!.SetNumber("m_LocalPosition.x", "5");   // found again: fine
    }

    [Fact]
    public void ValueChangesSurviveStructuralChanges()
    {
        var file = Scene();
        file.Find<GameObject>(200)!.SetString("m_Name", "Box");
        file.CreateGameObject("X");
        Assert.Equal("Box", file.Find<GameObject>(200)!.Name);
        Assert.True(file.HasChanges);
        file.Revert();
        Assert.Equal(Fixture.Scene, file.ToText());
    }

    [Fact]
    public void FindsByHierarchyPath()
    {
        var h = Scene().Hierarchy;
        Assert.Equal(200, Assert.Single(h.FindPath("/Root/Cube: one")).Object.FileId);
        Assert.Equal(200, Assert.Single(h.FindPath("Cube: one")).Object.FileId);
        Assert.Equal(300, Assert.Single(h.FindPath("Root/Crate")).Object.FileId);
        Assert.Empty(h.FindPath("/Crate"));
        Assert.Equal(300, h.NodeOf(301)!.Object.FileId);   // the instance's stand-in transform
    }

    [Fact]
    public void TemplatesCoverThePrimitivesAndCommonComponents()
    {
        foreach (var p in Templates.Primitives) Assert.Equal(p, Templates.PrimitiveName(p.ToLowerInvariant()));
        foreach (var c in new[] { "Rigidbody", "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "Light", "Camera", "AudioSource", "AudioListener", "MeshFilter", "MeshRenderer" })
            Assert.True(Templates.HasComponent(c), c);
    }
}

public class TemplateCloneTests
{
    // Every local reference in what CreateGameObject writes points at an object in the file (Unity reported
    // "Broken text PPtr" for a template parent that leaked through).
    [Theory]
    [InlineData(null)]
    [InlineData("Cube")]
    [InlineData("Plane")]
    public void NewObjectsOnlyReferToObjectsInTheFile(string? primitive)
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var id = file.CreateGameObject("X", primitive);
        file.AddComponent(id, "Light");
        var go = file.Find<GameObject>(id)!;
        foreach (var o in go.Components.Prepend(go))
            foreach (var node in All(o.Root))
                if (ObjectReference.From(node) is { IsLocal: true, IsNone: false } r)
                    Assert.True(file.Find(r.FileId) != null, $"{o.TypeName}.{node.Path} -> &{r.FileId}");
    }

    static IEnumerable<Unity.DataModel.Yaml.YamlNode> All(Unity.DataModel.Yaml.YamlNode n) => n.Children.SelectMany(All).Prepend(n);
}
