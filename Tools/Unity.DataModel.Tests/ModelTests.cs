using Unity.DataModel.Levels;
using Unity.DataModel.Yaml;

namespace Unity.DataModel.Tests;

public class ParsingTests
{
    [Fact]
    public void ReadsDocumentsAndTypes()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        Assert.Equal(9, file.Objects.Count);
        Assert.IsType<GameObject>(file.Find(100));
        Assert.IsType<Transform>(file.Find(101));
        Assert.IsType<PrefabInstance>(file.Find(300));
        Assert.True(file.Find(301)!.IsStripped);
        Assert.Equal(long.MaxValue, file.SceneRoots!.FileId);
        Assert.Equal("MonoBehaviour", file.Find(400)!.TypeName);
    }

    [Fact]
    public void KnowsEachValuesKindPathAndPlace()
    {
        var t = Scene().Find<Transform>(101)!;
        var pos = t.Find("m_LocalPosition")!;
        Assert.Equal(YamlNodeKind.Flow, pos.Kind);
        Assert.Equal("m_LocalPosition.y", pos.Item("y")!.Path);
        Assert.Equal(YamlNodeKind.Sequence, t.Find("m_Children")!.Kind);
        Assert.Equal("m_Children.1", t.Find("m_Children.1")!.Path);
        Assert.Equal(YamlNodeKind.FlowSequence, Scene().Find(201)!.Find("m_Children")!.Kind);
        var mods = Scene().Find(300)!.Find("m_Modification.m_Modifications.1.value")!;
        Assert.Equal("Crate", mods.Text);
    }

    [Fact]
    public void ValuesOnSeveralLinesAreReadOnly()
    {
        var mb = Scene().Find(400)!;
        Assert.True(mb.Find("m_Text")!.ReadOnly);
        Assert.True(mb.Find("m_Points")!.ReadOnly);
        Assert.False(mb.Find("m_Speed")!.ReadOnly);   // the line after the wrapped values is read as usual
        Assert.Equal(1.5, mb.GetNumber("m_Speed"));
        Assert.Throws<InvalidOperationException>(() => mb.SetString("m_Text", "x"));
    }

    [Fact]
    public void UnchangedFileIsWrittenBackByteForByte()
    {
        var crlf = Fixture.Scene.Replace("\n", "\r\n");
        Assert.Equal(crlf, UnityFile.Parse(crlf).ToText());
        Assert.Equal(Fixture.Scene, UnityFile.Parse(Fixture.Scene).ToText());
    }

    [Fact]
    public void RejectsBinaryFiles() => Assert.Throws<FormatException>(() => UnityFile.Parse("\u0000\u0001binary"));

    // Every YAML file in the Unity project around these tools reads and writes back unchanged.
    [Fact]
    public void EveryProjectFileRoundTrips()
    {
        var assets = Fixture.ProjectAssets();
        if (assets == null) return;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".unity") || p.EndsWith(".prefab") || p.EndsWith(".mat") || p.EndsWith(".asset")))
        {
            var text = File.ReadAllText(path);
            if (!UnityFile.IsYaml(text)) continue;
            var file = UnityFile.Parse(text);
            Assert.Equal(text, file.ToText());
            _ = file.Hierarchy;
            // Change and revert every editable top-level number: the text is unchanged.
            foreach (var o in file.Objects)
                foreach (var c in o.Root.Children.Where(c => c.Kind == YamlNodeKind.Scalar && !c.ReadOnly && YamlText.IsNumber(c.Raw)))
                {
                    var before = c.Raw;
                    o.SetRaw(c.Path, "12345");
                    o.SetRaw(c.Path, before);
                }
            Assert.False(file.HasChanges, path);
            count++;
        }
        Assert.True(count > 0);
    }

    static UnityFile Scene() => UnityFile.Parse(Fixture.Scene);
}

public class EditingTests
{
    [Fact]
    public void SettingAValueRewritesOnlyItsCharacters()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        file.Find<Transform>(201)!.SetNumber("m_LocalPosition.y", "7.25");
        var expected = Fixture.Scene.Replace("m_LocalPosition: {x: 1, y: 2, z: 3}", "m_LocalPosition: {x: 1, y: 7.25, z: 3}");
        Assert.Equal(expected, file.ToText());
        var change = Assert.Single(file.Changes);
        Assert.Equal("m_LocalPosition.y", change.PropertyPath);
        Assert.Equal("2", change.OldRaw);
        Assert.Equal("7.25", change.NewRaw);
    }

    [Fact]
    public void ReadsSeeTheNewValues()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var go = file.Find<GameObject>(200)!;
        go.SetString("m_Name", "Crate");
        Assert.Equal("Crate", go.Name);
        Assert.Contains("m_Name: 'Crate'", file.ToText());   // it was quoted, so it stays quoted
    }

    [Fact]
    public void SettingTheOriginalValueAgainIsNoChange()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var t = file.Find<Transform>(101)!;
        t.SetNumber("m_LocalPosition.x", "3");
        t.SetNumber("m_LocalPosition.x", "10");
        Assert.False(file.HasChanges);
    }

    [Fact]
    public void RejectsBadEdits()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var t = file.Find<Transform>(101)!;
        Assert.Throws<FormatException>(() => t.SetNumber("m_LocalPosition.x", "ten"));
        Assert.Throws<KeyNotFoundException>(() => t.SetNumber("m_Nope", "1"));
        Assert.Throws<ArgumentException>(() => t.SetRaw("m_LocalPosition.x", "1\n2"));
        Assert.Throws<InvalidOperationException>(() => t.SetRaw("m_LocalPosition", "{x: 1}"));   // a flow map: set its entries
        Assert.Throws<InvalidOperationException>(() => t.SetRaw("m_Children", "[]"));
    }

    [Fact]
    public void SetVector3WritesOnlyTheAxesThatChange()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        file.Find<Transform>(201)!.SetVector3("m_LocalPosition", new Vector3(1, 2.0000001, 9));
        Assert.Equal("m_LocalPosition.z", Assert.Single(file.Changes).PropertyPath);
    }

    [Fact]
    public void RevertForgetsEverything()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var t = file.Find<Transform>(201)!;
        t.SetNumber("m_LocalPosition.x", "5");
        file.AddPrefabInstance(Fixture.CrateGuid, Fixture.CrateRoot, "X", Pose.Identity);
        file.Revert();
        Assert.Equal(1, t.LocalPosition.X);
        Assert.Equal(Fixture.Scene, file.ToText());
    }
}

public class ObjectModelTests
{
    [Fact]
    public void GameObjectsKnowTheirComponentsAndTransforms()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var cube = file.Find<GameObject>(200)!;
        Assert.Equal("Cube: one", cube.Name);
        Assert.False(cube.IsActive);
        Assert.Equal(new long[] { 201, 202 }, cube.Components.Select(c => c.FileId));
        Assert.Equal(101, cube.Transform!.FatherId);
        Assert.Equal(100, cube.Transform.Father!.GameObjectId);
        Assert.Equal(new long[] { 201, 301 }, file.Find<Transform>(101)!.ChildIds);
        Assert.Equal(new ObjectReference(10202, "0000000000000000e000000000000000", 0), file.Find(202)!.GetReference("m_Mesh"));
    }

    [Fact]
    public void HierarchyNestsObjectsAndPrefabInstances()
    {
        var h = UnityFile.Parse(Fixture.Scene).Hierarchy;
        var root = Assert.Single(h.Roots);
        Assert.Equal("Root", root.Label);
        Assert.Equal(new[] { "Cube: one", "Crate" }, root.Children.Select(c => c.Label));
        Assert.Equal(HierarchyNodeKind.PrefabInstance, root.Children[1].Kind);
        Assert.Equal(new[] { "Settings", "SceneRoots" }, h.Others.Select(o => o.Label));
    }

    [Fact]
    public void PrefabInstancesReadAndChangeTheirOverrides()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var inst = file.Find<PrefabInstance>(300)!;
        Assert.Equal("Crate", inst.Name);
        Assert.Equal(Fixture.CrateGuid, inst.SourcePrefab.Guid);
        Assert.Equal(101, inst.TransformParentId);
        var pose = inst.RootPose(Fixture.CrateRoot);
        Assert.Equal(4, pose.Position.X);
        Assert.Equal(0.5, pose.Position.Y);   // not overridden: the prefab's own

        var target = new ObjectReference(5, Fixture.CrateGuid, 3);
        inst.SetModification(target, "m_LocalPosition.x", "6");      // changes the override
        inst.SetModification(target, "m_LocalPosition.z", "-2");     // adds one
        var after = file.ApplyChanges().Find<PrefabInstance>(300)!;
        Assert.Equal("6", after.FindModification(target, "m_LocalPosition.x")!.Value);
        Assert.Equal("-2", after.FindModification(target, "m_LocalPosition.z")!.Value);
        Assert.Equal(3, after.Modifications.Count);
    }

    [Fact]
    public void AddedPrefabInstancesGoBeforeSceneRootsAndIntoIt()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var id = file.AddPrefabInstance(Fixture.CrateGuid, Fixture.CrateRoot, "Crate 2",
            new Pose(new Vector3(1, 0.5, 0), Quaternion.Identity, Vector3.One));
        Assert.Equal(new[] { id }, file.AddedObjects);
        var text = file.ToText();
        Assert.True(text.IndexOf($"--- !u!1001 &{id}") < text.IndexOf("--- !u!1660057539"));
        var read = UnityFile.Parse(text);
        var inst = read.Find<PrefabInstance>(id)!;
        Assert.Equal("Crate 2", inst.Name);
        Assert.Equal(0, inst.TransformParentId);
        Assert.Equal(new long[] { 101, id }, read.SceneRoots!.RootIds);
        Assert.Null(inst.FindModification(new ObjectReference(5, Fixture.CrateGuid), "m_LocalScale.x"));   // scale 1 isn't written
        Assert.Throws<ArgumentException>(() => file.AddPrefabInstance(Fixture.CrateGuid, Fixture.CrateRoot, null, Pose.Identity, id));
    }

    [Fact]
    public void NewFileIdsAreUnused()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var ids = Enumerable.Range(0, 200).Select(_ => file.NewFileId()).ToList();
        Assert.All(ids, id => Assert.True(id > 1000000000 && file.Find(id) == null));
    }

    [Fact]
    public void PrefabRootIsTheTransformWithoutAFather()
    {
        var root = PrefabRoot.Read(Fixture.Parse("Barrel.prefab"))!;
        Assert.Equal("Barrel_1F", root.Name);
        Assert.NotEqual(0, root.RootTransformId);
        Assert.NotEqual(0, root.RootGameObjectId);
    }
}

public class LevelSceneTests
{
    static LevelScene Level(UnityFile file) => new(file, new Dictionary<string, PrefabRoot?> { [Fixture.CrateGuid] = Fixture.CrateRoot });

    [Fact]
    public void PlacesObjectsAndInstancesInWorldSpace()
    {
        var (items, skipped) = Level(UnityFile.Parse(Fixture.Scene)).Placeables();
        Assert.Empty(skipped);
        var crate = items.Single(i => i.Kind == PlaceableKind.Instance);
        // Root at x=10, scale 2: the crate's local (4, 0.5, 0) lands at (18, 1, 0).
        Fixture.Close(18, crate.World.Position.X);
        Fixture.Close(1, crate.World.Position.Y);
        var cube = items.Single(i => i.Kind == PlaceableKind.Object);
        Assert.Equal("Cube", cube.Primitive);
        Assert.Null(cube.AssetGuid);
        Fixture.Close(12, cube.World.Position.X);
    }

    [Fact]
    public void UnknownPrefabsAreSkipped()
    {
        var (items, skipped) = new LevelScene(UnityFile.Parse(Fixture.Scene), new Dictionary<string, PrefabRoot?>()).Placeables();
        Assert.Equal(300, Assert.Single(skipped).Id);
        Assert.Single(items);
    }

    [Fact]
    public void MovingWritesLocalValuesUnderTheParent()
    {
        var file = UnityFile.Parse(Fixture.Scene);
        var level = Level(file);
        var items = level.Placeables().Items;
        level.Move(items.Single(i => i.Kind == PlaceableKind.Object), new Pose(new Vector3(14, 0, 0), Quaternion.Identity, new Vector3(2, 2, 2)));
        level.Move(items.Single(i => i.Kind == PlaceableKind.Instance), new Pose(new Vector3(10, 1, 6), Quaternion.Identity, new Vector3(2, 2, 2)));
        var after = file.ApplyChanges();
        Assert.Equal(new Vector3(2, 0, 0).X, after.Find<Transform>(201)!.LocalPosition.X);
        Assert.Equal(1, after.Find<Transform>(201)!.LocalScale.X);
        var inst = after.Find<PrefabInstance>(300)!;
        var target = new ObjectReference(5, Fixture.CrateGuid);
        Assert.Equal("0", inst.FindModification(target, "m_LocalPosition.x")!.Value);
        Assert.Equal("3", inst.FindModification(target, "m_LocalPosition.z")!.Value);
        Assert.Null(inst.FindModification(target, "m_LocalPosition.y"));   // 0.5 is the prefab's own value
    }
}

public class TextAndMathTests
{
    [Theory]
    [InlineData("Crate", null, "Crate")]
    [InlineData("Crate", "'Old'", "'Crate'")]
    [InlineData("a: b", null, "'a: b'")]
    [InlineData("it's", null, "it's")]
    [InlineData("-dash", null, "'-dash'")]
    [InlineData(" padded", null, "' padded'")]
    [InlineData("#tag", null, "'#tag'")]
    [InlineData("O'Brien: x", null, "'O''Brien: x'")]
    public void StringsAreQuotedLikeUnity(string text, string? previous, string expected) =>
        Assert.Equal(expected, YamlText.FormatString(text, previous));

    [Theory]
    [InlineData("'it''s'", "it's")]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData(" plain ", "plain")]
    public void ScalarsAreUnquoted(string raw, string expected) => Assert.Equal(expected, YamlText.Unquote(raw));

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(5e-8, "0")]
    [InlineData(1.0, "1")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1.23456789, "1.2345679")]
    [InlineData(0.30000000000000004, "0.3")]
    [InlineData(0.00001234, "0.00001234")]
    [InlineData(5.4e-7, "5.4e-7")]
    [InlineData(123456789.0, "123456790")]
    public void NumbersAreWrittenLikeTheOldEditor(double v, string expected) => Assert.Equal(expected, YamlText.FormatNumber(v));

    [Fact]
    public void EulerRoundTrips()
    {
        var q = Quaternion.Euler(20, 45, -10);
        var e = q.EulerAngles;
        Fixture.Close(20, e.X, 1e-9);
        Fixture.Close(45, e.Y, 1e-9);
        Fixture.Close(-10, e.Z, 1e-9);
    }

    [Fact]
    public void ToLocalUndoesCompose()
    {
        var parent = new Pose(new Vector3(1, 2, 3), Quaternion.Euler(10, 70, 5), new Vector3(2, 3, 0.5));
        var child = new Pose(new Vector3(-1, 4, 2), Quaternion.Euler(0, 30, 0), new Vector3(1, 2, 1));
        var back = Pose.ToLocal(parent, Pose.Compose(parent, child));
        for (var n = 0; n < 3; n++)
        {
            Fixture.Close(child.Position[n], back.Position[n], 1e-9);
            Fixture.Close(child.Scale[n], back.Scale[n], 1e-9);
        }
        for (var n = 0; n < 4; n++) Fixture.Close(child.Rotation[n], back.Rotation[n], 1e-9);
    }
}
