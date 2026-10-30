using System.Text.Json;
using Unity.DataModel.Levels;

namespace Unity.DataModel.Tests;

// The same changes the browser-side JS model made, compared with what it wrote.
public class GoldenTests
{
    [Fact]
    public void PrefabValueEdits_MatchTheOldEditor()
    {
        var file = Fixture.Parse("Barrel.prefab");
        var go = file.Objects.OfType<GameObject>().First();
        var t = file.Objects.OfType<Transform>().First();
        go.SetString("m_Name", "Barrel: renamed");
        go.SetBool("m_IsActive", false);
        t.SetNumber("m_LocalPosition.x", "2.5");
        t.SetNumber("m_LocalScale.z", "-1e-3");
        Assert.Equal(Fixture.Text("Expected/Barrel.prefab.edited"), file.ToText());
        Assert.Equal(4, file.Changes.Count);
    }

    [Fact]
    public void MaterialValueEdits_MatchTheOldEditor()
    {
        var file = Fixture.Parse("Toon.mat");
        var mat = file.Objects.Single(o => o.ClassId == 21);
        mat.SetString("m_Name", "Toon 'copy'");
        mat.SetNumber("m_SavedProperties.m_Colors.0._Color.r", "0.25");
        Assert.Equal(Fixture.Text("Expected/Toon.mat.edited"), file.ToText());
    }

    [Theory]
    [InlineData("TreeSample.unity")]
    [InlineData("VillageCorner.unity")]
    public void Placeables_MatchTheOldLevelEditor(string scene)
    {
        var file = Fixture.Parse(scene);
        var (items, _) = new LevelScene(file, Fixture.Prefabs(file)).Placeables();
        var expected = JsonDocument.Parse(Fixture.Text($"Expected/{scene}.placeables.json")).RootElement.EnumerateArray().ToList();
        Assert.Equal(expected.Count, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var e = expected[i];
            Assert.Equal(e.GetProperty("id").GetString(), items[i].Id.ToString());
            Assert.Equal(e.GetProperty("kind").GetString(), items[i].Kind == PlaceableKind.Instance ? "instance" : "object");
            Assert.Equal(e.GetProperty("name").GetString(), items[i].Name);
            Assert.Equal(e.GetProperty("assetGuid").GetString(), items[i].AssetGuid);
            var w = e.GetProperty("world");
            double At(string k, int n) => w.GetProperty(k)[n].GetDouble();
            var world = items[i].World;
            for (var n = 0; n < 3; n++)
            {
                Fixture.Close(At("p", n), world.Position[n]);
                Fixture.Close(At("s", n), world.Scale[n]);
            }
            for (var n = 0; n < 4; n++) Fixture.Close(At("q", n), world.Rotation[n]);
        }
    }

    [Theory]
    [InlineData("TreeSample.unity")]
    [InlineData("VillageCorner.unity")]
    public void MovingAndAdding_WritesWhatTheOldLevelEditorWrote(string scene)
    {
        var file = Fixture.Parse(scene);
        var prefabs = Fixture.Prefabs(file);
        var level = new LevelScene(file, prefabs);
        var (items, _) = level.Placeables();
        for (var k = 0; k < items.Count; k += 2)
        {
            var w = items[k].World;
            level.Move(items[k], new Pose(new Vector3(w.Position.X + 1.25, w.Position.Y, w.Position.Z - 0.5),
                (Quaternion.Euler(0, 30, 0) * w.Rotation).Normalized, new Vector3(w.Scale.X * 1.5, w.Scale.Y * 1.5, w.Scale.Z * 1.5)));
        }
        var guid = prefabs.First(p => p.Value != null).Key;
        level.Add(new NewInstance(guid, "Added A", new Pose(new Vector3(3, 0, 4), Quaternion.Euler(0, 45, 0), Vector3.One), 7000000001));
        level.Add(new NewInstance(guid, "Added 'B'", new Pose(new Vector3(-2, 1, 0), Quaternion.Identity, new Vector3(2, 2, 2)), 7000000002));
        Assert.Equal(Fixture.Text($"Expected/{scene}.saved"), file.ToText());
    }

    [Fact]
    public void SavedScene_ReadsBackWithTheMovedAndAddedItems()
    {
        var file = Fixture.Parse("TreeSample.unity");
        var prefabs = Fixture.Prefabs(file);
        var level = new LevelScene(file, prefabs);
        var first = level.Placeables().Items[0];
        var target = new Pose(new Vector3(5, 1, -3), Quaternion.Euler(0, 90, 0), new Vector3(2, 2, 2));
        level.Move(first, target);
        var guid = prefabs.First(p => p.Value != null).Key;
        var added = level.Add(new NewInstance(guid, "New tree", new Pose(new Vector3(1, 0, 1), Quaternion.Identity, Vector3.One)));

        var saved = new LevelScene(file.ApplyChanges(), prefabs);
        var items = saved.Placeables().Items;
        var moved = items.Single(i => i.Id == first.Id).World;
        for (var n = 0; n < 3; n++)
        {
            Fixture.Close(target.Position[n], moved.Position[n], 1e-5);
            Fixture.Close(target.Scale[n], moved.Scale[n], 1e-5);
        }
        Assert.Contains(items, i => i.Id == added && i.Name == "New tree");
        Assert.Contains(added, saved.File.SceneRoots!.RootIds);
    }
}
