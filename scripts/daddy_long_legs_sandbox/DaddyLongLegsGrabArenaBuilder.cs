using System;
using Godot;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// 抓取竞技场：单个封闭矩形房间（地板 + 天花板 + 四墙，共 6 个盒子）。
///
/// 尺度常量**直接引用** <see cref="DaddyLongLegsMazeBuilder"/> 的公共常量（它是主仓
/// <c>CoordinateMapper</c>/<c>RoomBuilder</c> 的只读镜像，单一真相源）：净高 3.2m、
/// 墙/楼板 0.18m、墙高 = 净高 + 2×楼板。与迷宫的差别只有一个——内径（宽 × 深）可调，
/// 追逐 + 抓取测试需要比迷宫最大房（hall 18×15m）更大的连续空间。
///
/// 墙心钉在内径边界平面上（与迷宫墙骑在 lane 线上的惯例一致），四角互相加长半墙厚封角；
/// 全部 collider 合并进一个 StaticBody3D（内核只消费命中点/法线/距离，不读 collider 身份）。
/// </summary>
public sealed class DaddyLongLegsGrabArenaBuilder
{
    private const float MinimumInterior = 12f;

    private Node3D? _ceilingVisuals;

    public Vector3 Origin { get; }
    public float InteriorWidth { get; }
    public float InteriorDepth { get; }

    /// <summary>怪物出生端：+X 端中线，抬到房间半高（同迷宫出生惯例）。</summary>
    public Vector3 MonsterSpawn { get; }

    /// <summary>玩家出生端：−X 端中线，地面高度（Place 自带出生抬升）。</summary>
    public Vector3 PlayerSpawn { get; }

    public DaddyLongLegsGrabArenaBuilder(
        Vector3 origin,
        float interiorWidth,
        float interiorDepth,
        float spawnEndInset)
    {
        if (!float.IsFinite(interiorWidth) || !float.IsFinite(interiorDepth)
            || interiorWidth < MinimumInterior || interiorDepth < MinimumInterior)
        {
            throw new InvalidOperationException(
                $"arena interior {interiorWidth:F1}x{interiorDepth:F1}m is invalid " +
                $"(both sides must be finite and >= {MinimumInterior:F0}m)");
        }
        if (!float.IsFinite(spawnEndInset) || spawnEndInset <= 0f
            || spawnEndInset * 2f >= interiorWidth)
        {
            throw new InvalidOperationException(
                $"arena spawn end inset {spawnEndInset:F1}m is invalid for interior " +
                $"width {interiorWidth:F1}m (needs 0 < inset < width/2)");
        }

        Origin = origin;
        InteriorWidth = interiorWidth;
        InteriorDepth = interiorDepth;
        MonsterSpawn = origin + new Vector3(
            interiorWidth - spawnEndInset,
            DaddyLongLegsMazeBuilder.RoomHeight * 0.5f,
            interiorDepth * 0.5f);
        PlayerSpawn = origin + new Vector3(spawnEndInset, 0f, interiorDepth * 0.5f);
    }

    public void Build(Node3D parent)
    {
        var root = new Node3D { Name = "Arena" };
        parent.AddChild(root);

        var body = new StaticBody3D { Name = "ArenaCollision" };
        root.AddChild(body);

        var floorVisuals = new Node3D { Name = "FloorVisuals" };
        var wallVisuals = new Node3D { Name = "WallVisuals" };
        _ceilingVisuals = new Node3D { Name = "CeilingVisuals" };
        root.AddChild(floorVisuals);
        root.AddChild(wallVisuals);
        root.AddChild(_ceilingVisuals);

        // 材质配色沿用迷宫，第一人称下三面一眼可分。
        var floorMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.17f, 0.19f, 0.23f),
            Roughness = 0.94f,
        };
        var wallMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.30f, 0.32f, 0.39f),
            Roughness = 0.84f,
        };
        var ceilingMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.24f, 0.28f, 0.36f),
            Roughness = 0.86f,
        };

        float width = InteriorWidth;
        float depth = InteriorDepth;
        float roomHeight = DaddyLongLegsMazeBuilder.RoomHeight;
        float wall = DaddyLongLegsMazeBuilder.WallThickness;
        float slab = DaddyLongLegsMazeBuilder.SlabThickness;
        float wallHeight = DaddyLongLegsMazeBuilder.WallHeight;
        Vector3 center = new(width * 0.5f, 0f, depth * 0.5f);

        // 楼板/天花板：板心分别压在走面下方与净高上方半个板厚（≙ 迷宫铺板惯例）。
        AddBox(body, floorVisuals, floorMaterial,
            Origin + center + new Vector3(0f, -slab * 0.5f, 0f),
            new Vector3(width, slab, depth), "Floor");
        AddBox(body, _ceilingVisuals, ceilingMaterial,
            Origin + center + new Vector3(0f, roomHeight + slab * 0.5f, 0f),
            new Vector3(width, slab, depth), "Ceiling");

        // 四墙：墙心在净高中点、骑在边界平面上；沿长边各加长一整墙厚，四角互相咬合封死。
        float wallCenterY = roomHeight * 0.5f;
        AddBox(body, wallVisuals, wallMaterial,
            Origin + new Vector3(0f, wallCenterY, depth * 0.5f),
            new Vector3(wall, wallHeight, depth + wall * 2f), "WallWest");
        AddBox(body, wallVisuals, wallMaterial,
            Origin + new Vector3(width, wallCenterY, depth * 0.5f),
            new Vector3(wall, wallHeight, depth + wall * 2f), "WallEast");
        AddBox(body, wallVisuals, wallMaterial,
            Origin + new Vector3(width * 0.5f, wallCenterY, 0f),
            new Vector3(width + wall * 2f, wallHeight, wall), "WallNorth");
        AddBox(body, wallVisuals, wallMaterial,
            Origin + new Vector3(width * 0.5f, wallCenterY, depth),
            new Vector3(width + wall * 2f, wallHeight, wall), "WallSouth");

        GD.Print($"[DADDY-ARENA] built room interior={width:F1}x{depth:F1}m " +
                 $"height={roomHeight:F2}m wall={wall:F2}m " +
                 $"monsterSpawn=({MonsterSpawn.X:F1},{MonsterSpawn.Y:F1},{MonsterSpawn.Z:F1}) " +
                 $"playerSpawn=({PlayerSpawn.X:F1},{PlayerSpawn.Y:F1},{PlayerSpawn.Z:F1})");
    }

    /// <summary>隐藏天花板网格但保留碰撞体（俯视排查用；本场景默认可见）。</summary>
    public void SetCeilingVisible(bool visible)
    {
        if (_ceilingVisuals is not null)
            _ceilingVisuals.Visible = visible;
    }

    private static void AddBox(
        StaticBody3D body,
        Node3D visuals,
        Material material,
        Vector3 center,
        Vector3 size,
        string name)
    {
        body.AddChild(new CollisionShape3D
        {
            Name = $"{name}Shape",
            Shape = new BoxShape3D { Size = size },
            Position = center,
        });
        visuals.AddChild(new MeshInstance3D
        {
            Name = $"{name}Mesh",
            Mesh = new BoxMesh { Size = size },
            Position = center,
            MaterialOverride = material,
        });
    }
}
