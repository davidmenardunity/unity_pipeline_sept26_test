using System.Text.Json.Nodes;
using CoAuthoring.Commands;
using CoAuthoring.Engine;
using Unity.DataModel;
using Unity.DataModel.Commands;

namespace CoAuthoring.Tests;

public sealed class Harness : IDisposable
{
    public readonly string Data = Path.Combine(Path.GetTempPath(), "coauthor-tests", Guid.NewGuid().ToString("N"));
    public readonly FolderWorkbench Workbench = new(Path.Combine(AppContext.BaseDirectory, "Project"));
    public readonly Catalog Catalog = Catalog.Builtin();
    public readonly Runner Runner;
    public readonly Sessions Sessions;
    public const string Scene = "Assets/Scenes/Level.unity";

    public Harness()
    {
        Catalog.AddCustom(typeof(SampleCommands).Assembly);
        Runner = new Runner(Catalog);
        Sessions = new Sessions(Workbench, Data);
    }

    public async Task<JsonObject> Run(string actor, params string[] commands) => await Run(actor, false, commands);

    public async Task<JsonObject> Run(string actor, bool transaction, params string[] commands)
    {
        var body = new JsonObject { ["commands"] = new JsonArray(commands.Select(c => (JsonNode)c).ToArray()), ["transaction"] = transaction };
        return await Runner.RunAsync(await Sessions.GetAsync("wb"), new Runner.Request(actor, "agent", Scene, body, null), CancellationToken.None);
    }

    public async Task<JsonObject> RunBody(string actor, JsonObject body, string? key = null) =>
        await Runner.RunAsync(await Sessions.GetAsync("wb"), new Runner.Request(actor, "agent", Scene, body, key), CancellationToken.None);

    public async Task<UnityFile> Draft() => UnityFile.Parse((await (await Sessions.GetAsync("wb")).DraftTextAsync(Scene, CancellationToken.None))!);

    public static JsonObject Result(JsonObject answer, int i = 0) => answer["results"]![i]!.AsObject();
    public static string Status(JsonObject answer, int i = 0) => Result(answer, i)["status"]!.GetValue<string>();

    public void Dispose()
    {
        Sessions.Dispose();
        try { Directory.Delete(Data, true); } catch (IOException) { }
    }
}

// A custom command, declared the pipeline-package way.
public static class SampleCommands
{
    [CliCommand("scatter_cubes", "Place cubes in a row.")]
    public static object Scatter(ICommandContext context, [CliArg("count", "How many.")] int count = 3, [CliArg("spacing", "Metres apart.")] float spacing = 2)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = context.Scene.CreateGameObject($"Cube {i}", "Cube");
            context.Scene.Find<GameObject>(id)!.Transform!.SetVector3("m_LocalPosition", new Vector3(i * spacing, 0.5, 0));
            ids.Add(id.ToString());
        }
        return new { created = ids };
    }
}

public class CommandLineTests
{
    readonly Catalog catalog = Catalog.Builtin();

    [Fact]
    public void ReadsLinesLikeTheCli()
    {
        var def = catalog.Find("create_gameobject")!;
        var args = CommandLine.Bind(def, CommandLine.Tokenize("create_gameobject --name 'Big box' --primitive=cube").Skip(1).ToList());
        Assert.Equal("Big box", args["name"]!.GetValue<string>());
        Assert.Equal("cube", args["primitive"]!.GetValue<string>());
        var set = CommandLine.Bind(catalog.Find("set_transform")!, CommandLine.Tokenize("set_transform /Root --position [1, 2, 3]").Skip(1).ToList());
        Assert.Equal("/Root", set["target"]!.GetValue<string>());   // positional: the required parameter
        Assert.Equal(3, set["position"]!.AsArray().Count);
        Assert.Equal(["create_gameobject"], CommandLine.Tokenize("unity command create_gameobject"));
    }

    [Fact]
    public void ExplainsMistakes()
    {
        var e = Assert.Throws<CommandException>(() => CommandLine.Resolve(catalog, ["create_gameobjct"]));
        Assert.Equal(ErrorCodes.NotFound, e.Code);
        Assert.Contains("create_gameobject", e.Message);
        var bad = Assert.Throws<CommandException>(() => CommandLine.Bind(catalog.Find("set_active")!, ["--target", "X"]));
        Assert.Contains("--active", bad.Message);
        Assert.Throws<CommandException>(() => CommandLine.Bind(catalog.Find("set_active")!, ["--nope", "1"]));
    }

    [Fact]
    public void CatalogComesFromThePackage()
    {
        Assert.True(catalog.All.Count >= 20);
        Assert.Empty(catalog.NotImplemented);
        var p = catalog.Find("set_parent")!.Param("world_position_stays")!;
        Assert.Equal("Boolean", p.Type);
        Assert.All(catalog.All, c => Assert.Equal("Unity.Pipeline.Editor", c.Package));
    }
}

public class SessionTests : IDisposable
{
    readonly Harness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public async Task CreatesAndChangesObjects()
    {
        var r = await h.Run("ana", "create_gameobject --name Crate --primitive cube", "set_transform --target /Crate --position [1,2,3] --rotation [0,90,0]",
            "add_component --target /Crate --type Rigidbody", "set_component_properties --target /Crate --type Rigidbody --properties {\"m_Mass\": 3.5}",
            "rename_gameobject --target /Crate --name Box");
        Assert.All(r["results"]!.AsArray(), x => Assert.Equal("ok", x!["status"]!.GetValue<string>()));
        Assert.Equal(5, r["seq"]!.GetValue<long>());
        Assert.StartsWith("GlobalObjectId_V1-2-11112222333344445555666677778888-", Harness.Result(r)["result"]!["globalId"]!.GetValue<string>());
        var scene = await h.Draft();
        var box = Assert.Single(scene.Hierarchy.FindPath("/Box"));
        var go = (GameObject)box.Object;
        Assert.Equal(2, go.Transform!.LocalPosition.Y);
        Assert.Equal(90, go.Transform.LocalRotation.EulerAngles.Y, 4);
        Assert.Equal("3.5", go.Components.Single(c => c.TypeName == "Rigidbody").GetString("m_Mass"));
    }

    [Fact]
    public async Task ReadsWithoutChangingAnything()
    {
        await h.Run("ana", "create_gameobject --name Crate --primitive cube");
        var r = await h.Run("ana", "get_component_properties --target /Crate --type BoxCollider", "find_gameobjects --type MeshRenderer", "get_scene_hierarchy");
        Assert.Equal(1, r["seq"]!.GetValue<long>());
        var props = Harness.Result(r)["result"]!["components"]![0]!["properties"]!;
        Assert.Equal(3, props["m_Size"]!.AsArray().Count);
        var found = Harness.Result(r, 1)["result"]!["gameObjects"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Crate", found);
        Assert.Contains("Plane", found);
        var roots = Harness.Result(r, 2)["result"]!["roots"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Main Camera", "Directional Light", "Plane", "Crate"], roots);
    }

    [Fact]
    public async Task AFailedCommandChangesNothingAndCanBeResent()
    {
        var before = (await h.Draft()).Text;
        var r = await h.Run("ana", "set_transform --target /Nope --position [1,2,3]");
        Assert.Equal("error", Harness.Status(r));
        Assert.Equal(ErrorCodes.NotFound, Harness.Result(r)["code"]!.GetValue<string>());
        Assert.Equal(0, r["seq"]!.GetValue<long>());
        Assert.Equal(before, (await h.Draft()).Text);
        // Resent once the object exists: fine.
        await h.Run("ana", "create_gameobject --name Nope");
        Assert.Equal("ok", Harness.Status(await h.Run("ana", "set_transform --target /Nope --position [1,2,3]")));
    }

    [Fact]
    public async Task ReferencesEarlierResults()
    {
        var r = await h.RunBody("ana", new JsonObject
        {
            ["commands"] = new JsonArray(
                new JsonObject { ["id"] = "box", ["command"] = "create_gameobject", ["params"] = new JsonObject { ["name"] = "Box", ["primitive"] = "cube" } },
                new JsonObject { ["command"] = "create_gameobject", ["params"] = new JsonObject { ["name"] = "Lid", ["parent"] = "$box.globalId" } }),
        });
        Assert.Equal("/Box/Lid", Harness.Result(r, 1)["result"]!["hierarchyPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATransactionIsAllOrNothing()
    {
        var before = (await h.Draft()).Text;
        var r = await h.Run("ana", true, "create_gameobject --name A", "create_gameobject --name B", "set_active --target /Missing --active false");
        Assert.Equal(["reverted", "reverted", "error"], r["results"]!.AsArray().Select(x => x!["status"]!.GetValue<string>()));
        Assert.Equal(before, (await h.Draft()).Text);
        var ok = await h.Run("ana", true, "create_gameobject --name A", "create_gameobject --name B");
        Assert.Equal(1, ok["seq"]!.GetValue<long>());   // one change
    }

    [Fact]
    public async Task RunsTheCliBatch()
    {
        var ops = "[{\"id\":\"a\",\"command\":\"create_gameobject\",\"params\":{\"name\":\"A\"}},{\"command\":\"set_transform\",\"params\":{\"target\":\"$a.hierarchyPath\",\"position\":[0,5,0]}}]";
        var r = await h.Run("ana", $"batch --operations {ops}");
        Assert.Equal("ok", Harness.Status(r));
        Assert.Equal(2, Harness.Result(r)["result"]!["applied"]!.GetValue<int>());
        Assert.Equal(5, ((GameObject)(await h.Draft()).Hierarchy.FindPath("/A")[0].Object).Transform!.LocalPosition.Y);
    }

    [Fact]
    public async Task PrefabInstances()
    {
        var r = await h.Run("ana", "instantiate_prefab --prefab Assets/Prefabs/Barrel.prefab --name Keg", "set_transform --target /Keg --position [4,0,4]",
            "set_active --target /Keg --active false", "create_gameobject --name Shelf", "set_parent --target /Keg --parent /Shelf");
        Assert.All(r["results"]!.AsArray(), x => Assert.Equal("ok", x!["status"]!.GetValue<string>()));
        Assert.Equal("Assets/Prefabs/Barrel.prefab", Harness.Result(r)["result"]!["prefab"]!.GetValue<string>());
        var scene = await h.Draft();
        var inst = (PrefabInstance)Assert.Single(scene.Hierarchy.FindPath("/Shelf/Keg")).Object;
        Assert.Equal("0", inst.Modifications.Single(m => m.PropertyPath == "m_IsActive").Value);
        var rm = await h.Run("ana", "add_component --target /Shelf/Keg --type Rigidbody");
        Assert.Equal(ErrorCodes.Unsupported, Harness.Result(rm)["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task TagsLayersAndScripts()
    {
        await h.Run("ana", "create_gameobject --name P");
        Assert.Equal("ok", Harness.Status(await h.Run("ana", "set_tag --target /P --tag Player")));
        Assert.Equal("error", Harness.Status(await h.Run("ana", "set_tag --target /P --tag Nope")));
        Assert.Equal("ok", Harness.Status(await h.Run("ana", "set_layer --target /P --layer \"Ignore Raycast\"")));
        var s = await h.Run("ana", "add_component --target /P --type Spinner");
        Assert.Equal("ok", Harness.Status(s));
        var go = (GameObject)(await h.Draft()).Hierarchy.FindPath("/P")[0].Object;
        Assert.Equal("Player", go.GetString("m_TagString"));
        Assert.Equal("2", go.GetString("m_Layer"));
        Assert.Contains(go.Components, c => c.GetReference("m_Script")?.Guid == "9999888877776666555544443333aaaa");
        Assert.Equal("ok", Harness.Status(await h.Run("ana", "remove_component --target /P --type Spinner")));
    }

    [Fact]
    public async Task CustomCommandsRunOnTheDataModel()
    {
        var def = h.Catalog.Find("scatter_cubes")!;
        Assert.True(def.Custom);
        Assert.Equal(["count", "spacing"], def.Params.Select(p => p.Name));
        var r = await h.Run("ana", "scatter_cubes --count 4 --spacing 1.5");
        Assert.Equal("ok", Harness.Status(r));
        Assert.Equal(4, Harness.Result(r)["result"]!["created"]!.AsArray().Count);
        Assert.Equal(4.5, ((GameObject)(await h.Draft()).Hierarchy.FindPath("/Cube 3")[0].Object).Transform!.LocalPosition.X);
    }

    [Fact]
    public async Task StaleEditsConflict()
    {
        await h.Run("ana", "create_gameobject --name Door");
        var seq = (await h.Sessions.GetAsync("wb")).Seq;
        await h.Run("ben", "set_transform --target /Door --position [1,0,0]");
        var stale = await h.RunBody("ana", new JsonObject { ["commands"] = new JsonArray((JsonNode)"set_transform --target /Door --position [2,0,0]"), ["baseSeq"] = seq });
        Assert.Equal(ErrorCodes.Conflict, Harness.Result(stale)["code"]!.GetValue<string>());
        Assert.NotNull(Harness.Result(stale)["details"]!["fields"]);
        // A field nobody else touched merges.
        var other = await h.RunBody("ana", new JsonObject { ["commands"] = new JsonArray((JsonNode)"rename_gameobject --target /Door --name Gate"), ["baseSeq"] = seq });
        Assert.Equal("ok", Harness.Status(other));
    }

    [Fact]
    public async Task ARetriedRequestIsAnsweredOnce()
    {
        var body = new JsonObject { ["commands"] = new JsonArray((JsonNode)"create_gameobject --name Once") };
        await h.RunBody("ana", body, "key-1");
        await h.RunBody("ana", (JsonObject)body.DeepClone(), "key-1");
        Assert.Single((await h.Draft()).Hierarchy.FindPath("/Once"));
    }

    [Fact]
    public async Task ManyActorsAtOnce()
    {
        await h.Run("setup", "create_gameobject --name Shared");
        var actors = Enumerable.Range(0, 16).Select(i => $"actor{i}").ToList();
        var answers = await Task.WhenAll(actors.Select(async a =>
        {
            var results = new List<JsonObject>();
            results.Add(await h.Run(a, $"create_gameobject --name {a} --primitive cube"));
            for (var k = 0; k < 10; k++)
            {
                results.Add(await h.Run(a, $"set_transform --target /{a} --position [{k},0,0]"));
                results.Add(await h.Run(a, $"rename_gameobject --target /Shared --name Shared"));   // everyone edits the same object
            }
            return results;
        }));
        var seqs = answers.SelectMany(x => x).SelectMany(r => r["results"]!.AsArray()).Where(x => x!["seq"] != null).Select(x => x!["seq"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());   // every change got its own number
        Assert.All(answers.SelectMany(x => x), r => Assert.Equal("ok", Harness.Status(r)));
        var scene = await h.Draft();
        foreach (var a in actors) Assert.Equal(9, ((GameObject)scene.Hierarchy.FindPath("/" + a)[0].Object).Transform!.LocalPosition.X);
        Assert.Single(scene.Hierarchy.FindPath("/Shared"));
    }

    [Fact]
    public async Task SavesOneCommitAndKeepsTheDraftOnDisk()
    {
        await h.Run("ana", "create_gameobject --name A");
        await h.Run("ben", "create_gameobject --name B");
        // A restart: the draft comes back from disk.
        h.Sessions.Dispose();
        using var again = new Sessions(h.Workbench, h.Data);
        var session = await again.GetAsync("wb");
        Assert.Equal(2, session.Seq);
        Assert.Contains(Harness.Scene, session.DirtyFiles);
        var saved = await session.SaveAsync("ana", null, CancellationToken.None);
        Assert.Equal("2", saved["revision"]!.GetValue<string>());
        var commit = Assert.Single(h.Workbench.Commits);
        Assert.Equal([Harness.Scene], commit.Paths);
        Assert.Contains("2 changes by ana, ben", commit.Message);
        Assert.Empty(session.DirtyFiles);
        Assert.Contains("m_Name: B", (await h.Workbench.ReadTextAsync("wb", "2", Harness.Scene, default))!);
    }

    [Fact]
    public async Task SavingOverSomeoneElsesCommitToTheSameFileConflicts()
    {
        await h.Run("ana", "create_gameobject --name A");
        var text = (await h.Workbench.ReadTextAsync("wb", "1", Harness.Scene, default))!;
        h.Workbench.CommitExternally(Harness.Scene, text.Replace("m_Name: Plane", "m_Name: Floor"));
        var session = await h.Sessions.GetAsync("wb");
        var e = await Assert.ThrowsAsync<CommandException>(() => session.SaveAsync("ana", null, default));
        Assert.Equal(ErrorCodes.Conflict, e.Code);
        await session.DiscardAsync("ana", default);
        Assert.Empty(session.DirtyFiles);
        Assert.Contains("m_Name: Floor", (await h.Draft()).Text);
    }

    [Fact]
    public async Task TellsEveryoneWhatChanged()
    {
        var session = await h.Sessions.GetAsync("wb");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<SessionEvent>();
        var listening = Task.Run(async () => { await foreach (var e in session.Events.Subscribe(0, session.Seq, cts.Token)) { events.Add(e); if (events.Any(x => x.Type == "write") && events.Any(x => x.Type == "presence")) break; } });
        await Task.Delay(100);
        await h.Run("ana", "create_gameobject --name Lamp");
        await listening;
        var write = events.First(e => e.Type == "write");
        Assert.Equal("ana", write.Actor);
        Assert.Equal("Lamp", write.Data!["changes"]![Harness.Scene]!["created"]![0]!["name"]!.GetValue<string>());
        Assert.Contains(events, e => e.Type == "presence");
    }

    [Fact]
    public async Task PresenceListsWhoIsWhere()
    {
        var session = await h.Sessions.GetAsync("wb");
        session.Presence.Report("ana", "human", new JsonObject { ["scene"] = Harness.Scene, ["camera"] = new JsonObject { ["p"] = new JsonArray(0, 1, -5) } });
        await h.Run("bot", "create_gameobject --name Thing");
        var list = session.Presence.List();
        Assert.Equal(["ana", "bot"], list.Select(p => p!["actor"]!.GetValue<string>()));
        Assert.Equal("agent", list[1]!["kind"]!.GetValue<string>());
        Assert.Equal("create_gameobject --name Thing", list[1]!["lastCommand"]!["command"]!.GetValue<string>());
        Assert.Matches("^#[0-9a-f]{6}$", list[0]!["color"]!.GetValue<string>());
    }
}

public class DemoCrowdTests : IDisposable
{
    readonly Harness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public async Task PretendPeopleEditTheSceneThroughTheQueue()
    {
        var session = await h.Sessions.GetAsync("wb");
        var crowd = new DemoCrowd(session, h.Runner);
        crowd.Start(Harness.Scene, 3, TimeSpan.FromSeconds(8), new DemoCrowd.Vector(0, 0, 0));
        await Task.Delay(TimeSpan.FromSeconds(5));
        var who = session.Presence.List().Select(p => p!["actor"]!.GetValue<string>()).ToList();
        Assert.Equal(["Alex", "Bea", "Chen"], who);
        Assert.NotNull(session.Presence.List()[0]!["state"]!["camera"]!["q"]);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(crowd.IsRunning);
        Assert.True(session.Seq >= 3, $"only {session.Seq} changes");
        var scene = await h.Draft();
        Assert.Contains(scene.Hierarchy.All, n => n.Label.StartsWith("Alex "));
        Assert.DoesNotContain(session.Describe()["changes"]!.AsArray(), c => c!["actor"]!.GetValue<string>() == "anonymous");
    }

    [Fact]
    public async Task PretendPeoplePlacePrefabsInTheirOwnAreas()
    {
        var session = await h.Sessions.GetAsync("wb");
        var crowd = new DemoCrowd(session, h.Runner);
        crowd.Start(Harness.Scene, 2, TimeSpan.FromSeconds(7), new DemoCrowd.Vector(0, 0, 0), ["Assets/Prefabs/Barrel.prefab"]);
        await Task.Delay(TimeSpan.FromSeconds(8));
        var scene = await h.Draft();
        var placed = scene.Objects.OfType<PrefabInstance>().Where(i => i.Name?.StartsWith("Alex ") == true || i.Name?.StartsWith("Bea ") == true).ToList();
        Assert.NotEmpty(placed);
        Assert.All(placed, i => Assert.Equal("aaaabbbbccccddddeeeeffff00001111", i.SourcePrefab.Guid));
        // Alex works east of the centre, Bea west (two people: opposite sides, 16 m out, give or take 8).
        var level = new Unity.DataModel.Levels.LevelScene(scene, new Dictionary<string, PrefabRoot?> { ["aaaabbbbccccddddeeeeffff00001111"] = PrefabRoot.Read(Unity.DataModel.UnityFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Project/Assets/Prefabs/Barrel.prefab")))) });
        foreach (var i in placed.Where(i => i.Name!.StartsWith("Alex ")))
            Assert.InRange(level.WorldPose(i.FileId).Position.X, 7.9, 24.1);
        foreach (var i in placed.Where(i => i.Name!.StartsWith("Bea ")))
            Assert.InRange(level.WorldPose(i.FileId).Position.X, -24.1, -7.9);
    }
}
