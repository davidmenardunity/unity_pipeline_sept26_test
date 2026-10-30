using System.Text.RegularExpressions;
using Unity.DataModel.Levels;

namespace Unity.DataModel.Tests;

// Test files: Fixtures/ (copied from the project's Assets), and Fixtures/Expected/, written by the browser-side
// JS model this library replaced (unityyaml.js / levelscene.js), so the tests pin the old behaviour byte for byte.
static class Fixture
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string Text(string name) => File.ReadAllText(Path.Combine(Dir, name));
    public static UnityFile Parse(string name) => UnityFile.Parse(Text(name));

    /// <summary>Prefab roots for a scene, from Fixtures/Prefabs/{guid}.prefab.</summary>
    public static Dictionary<string, PrefabRoot?> Prefabs(UnityFile scene) => LevelScene.PrefabGuids(scene).ToDictionary(g => g, g =>
    {
        var path = Path.Combine(Dir, "Prefabs", g + ".prefab");
        return File.Exists(path) ? PrefabRoot.Read(UnityFile.Parse(File.ReadAllText(path))) : null;
    });

    /// <summary>The Unity project these tools sit in (Tools/ next to Assets/), or null when the tests run elsewhere.</summary>
    public static string? ProjectAssets()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Assets")) && Directory.Exists(Path.Combine(d.FullName, "Tools")))
                return Path.Combine(d.FullName, "Assets");
        return null;
    }

    public static void Close(double expected, double actual, double tolerance = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    // A small scene: a root with a child cube, a prefab instance under the root, and SceneRoots.
    public const string Scene = """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!1 &100
        GameObject:
          m_ObjectHideFlags: 0
          serializedVersion: 6
          m_Component:
          - component: {fileID: 101}
          m_Layer: 0
          m_Name: Root
          m_IsActive: 1
        --- !u!4 &101
        Transform:
          m_GameObject: {fileID: 100}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 10, y: 0, z: 0}
          m_LocalScale: {x: 2, y: 2, z: 2}
          m_Children:
          - {fileID: 201}
          - {fileID: 301}
          m_Father: {fileID: 0}
        --- !u!1 &200
        GameObject:
          m_Component:
          - component: {fileID: 201}
          - component: {fileID: 202}
          m_Name: 'Cube: one'
          m_IsActive: 0
        --- !u!4 &201
        Transform:
          m_GameObject: {fileID: 200}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 1, y: 2, z: 3}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 101}
        --- !u!33 &202
        MeshFilter:
          m_GameObject: {fileID: 200}
          m_Mesh: {fileID: 10202, guid: 0000000000000000e000000000000000, type: 0}
        --- !u!1001 &300
        PrefabInstance:
          m_ObjectHideFlags: 0
          serializedVersion: 2
          m_Modification:
            serializedVersion: 3
            m_TransformParent: {fileID: 101}
            m_Modifications:
            - target: {fileID: 5, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
              propertyPath: m_LocalPosition.x
              value: 4
              objectReference: {fileID: 0}
            - target: {fileID: 6, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
              propertyPath: m_Name
              value: Crate
              objectReference: {fileID: 0}
            m_RemovedComponents: []
          m_SourcePrefab: {fileID: 100100000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
        --- !u!4 &301 stripped
        Transform:
          m_CorrespondingSourceObject: {fileID: 5, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
          m_PrefabInstance: {fileID: 300}
        --- !u!114 &400
        MonoBehaviour:
          m_Name: Settings
          m_Text: 'it''s long and
            wraps'
          m_Points: {x: 1, y:
            2}
          m_Speed: 1.5
        --- !u!1660057539 &9223372036854775807
        SceneRoots:
          m_ObjectHideFlags: 0
          m_Roots:
          - {fileID: 101}

        """;

    public static readonly PrefabRoot CrateRoot = new(5, 6, "Crate", new Pose(new Vector3(0, 0.5, 0), Quaternion.Identity, Vector3.One));
    public const string CrateGuid = "abcdefabcdefabcdefabcdefabcdefab";
}
