using System;
using System.Collections.Generic;
using Godot;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// 主仓尺度的「全联通房间骨架」迷宫，供 <c>--daddy-route=maze</c> 观察 Daddy 在真实关卡
/// 尺度里的移动效果。
///
/// 几何常量逐项镜像主仓 <c>RandomRoomRuntime.Generation.CoordinateMapper</c> 与
/// <c>RoomBuilder</c>：3.0m 格、3.2m 净高、0.18m 墙体/楼板、墙高 = 净高 + 2×楼板。
/// **真相源是主仓那两个类**；这里只是只读镜像，改主仓尺度后必须同步本文件。
///
/// 刻意不做的：门扇、门框、锁、单向、楼梯/梯子、多层、导航网格。需求只要求
/// 「全联通房间骨架」——房间之间是整格（3m 宽 × 3.2m 高）通口，与主仓在门格
/// 整段跳过墙体的做法一致（RoomBuilder.TryDescribeWallEdge）。
///
/// 只在 route=maze 时构建，因此其余路线的地形与哈希逐位不变。
/// </summary>
public sealed class DaddyLongLegsMazeBuilder
{
    // ---- 主仓尺度镜像（CoordinateMapper）----
    public const float CellSize = 3.0f;
    public const float RoomHeight = 3.2f;
    public const float WallThickness = 0.18f;
    public const float SlabThickness = 0.18f;

    /// <summary>≙ RoomBuilder.WallHeight：墙体上下各吃掉一层楼板厚度，封住板边缝。</summary>
    public const float WallHeight = RoomHeight + SlabThickness * 2.0f;

    /// <summary>格网尺寸：20×16 格 = 60m × 48m，四周一圈实心边界。</summary>
    private const int GridWidth = 20;
    private const int GridDepth = 16;

    private readonly record struct CellRect(int MinX, int MinZ, int MaxX, int MaxZ);
    private readonly record struct Cell(int X, int Z);

    /// <summary>房间（含边界格）。名字只用于日志与断言消息。</summary>
    private static readonly (string Name, CellRect Rect)[] Rooms =
    {
        ("hall",     new CellRect(1, 1, 6, 5)),      // 6×5 格 = 18×15m 大厅
        ("gallery",  new CellRect(10, 1, 17, 4)),    // 8×4 格 长条房
        ("junction", new CellRect(8, 7, 10, 9)),     // 3×3 格 小中枢
        ("vault",    new CellRect(14, 7, 18, 11)),   // 5×5 格 方厅
        ("pit",      new CellRect(1, 10, 4, 14)),    // 4×5 格 角房
    };

    /// <summary>1 格宽走廊（3m 宽通道，等价主仓两房之间的整格通口串联）。</summary>
    private static readonly (string Name, CellRect Rect)[] Corridors =
    {
        ("hall-gallery",   new CellRect(7, 3, 9, 3)),    // 东西直廊
        ("gallery-junction", new CellRect(10, 5, 10, 6)),// 南北短廊
        ("junction-vault", new CellRect(11, 8, 13, 8)),  // 东西直廊
        ("hall-pit",       new CellRect(2, 6, 2, 9)),    // 南北长廊
        ("pit-spur",       new CellRect(5, 12, 8, 12)),  // 东西直廊
        ("spur-junction",  new CellRect(8, 10, 8, 12)),  // 南北直廊（与上条 L 形相接）
        ("gallery-vault",  new CellRect(17, 5, 17, 6)),  // 回环廊：让拓扑有环而非纯树
        ("hall-alcove",    new CellRect(7, 1, 8, 1)),    // 死胡同：验证到不了点时的行为
    };

    /// <summary>
    /// 预规划路径的拐点（格坐标）。相邻两点必须轴对齐，中间格由 <see cref="ExpandPath"/>
    /// 逐格展开——展开后的逐格点才是喂给内核的「邻近可达点」（契约 §4：喂点必须邻近，
    /// 不能把远处终点直接丢给内核）。首尾同格，因此 <c>--daddy-path-loop=on</c> 可无缝循环。
    /// </summary>
    private static readonly Cell[] PathCorners =
    {
        new(2, 3), new(6, 3), new(9, 3), new(12, 3), new(12, 2), new(16, 2),
        new(17, 2), new(17, 4), new(17, 6), new(17, 9), new(15, 9), new(15, 8),
        new(14, 8), new(11, 8), new(9, 8), new(8, 8), new(8, 9), new(8, 12),
        new(5, 12), new(3, 12), new(2, 12), new(2, 13), new(2, 10), new(2, 6),
        new(2, 3),
    };

    private readonly bool[,] _open = new bool[GridWidth, GridDepth];
    private readonly List<Vector3> _waypoints = new();
    private readonly List<(Vector3 Center, Vector3 HalfSize)> _wallBoxes = new();
    private Node3D? _ceilingVisuals;
    private Node3D? _pathMarkers;
    private MeshInstance3D? _activeMarker;
    private bool _markersVisible = true;

    public Vector3 Origin { get; }

    /// <summary>逐格展开后的路径点（**地面高度**，y = Origin.Y）。驱动侧负责抬到身体高度。</summary>
    public IReadOnlyList<Vector3> Waypoints => _waypoints;

    /// <summary>出生点：路径首格中心，抬到房间半高。</summary>
    public Vector3 SpawnPosition =>
        CellCenter(PathCorners[0]) + new Vector3(0f, RoomHeight * 0.5f, 0f);

    public DaddyLongLegsMazeBuilder(Vector3 origin)
    {
        Origin = origin;
        Rasterize();
        ValidateConnectivity();
        ExpandPath();
    }

    // ---- 格 ↔ 世界 ----

    private Vector3 CellCenter(Cell cell) => Origin + new Vector3(
        (cell.X + 0.5f) * CellSize,
        0f,
        (cell.Z + 0.5f) * CellSize);

    private bool IsOpen(int x, int z) =>
        x >= 0 && x < GridWidth && z >= 0 && z < GridDepth && _open[x, z];

    // ---- 装配 ----

    private void Rasterize()
    {
        foreach ((string name, CellRect rect) in Rooms)
            Fill(rect, name);
        foreach ((string name, CellRect rect) in Corridors)
            Fill(rect, name);
    }

    private void Fill(CellRect rect, string name)
    {
        if (rect.MinX < 1 || rect.MinZ < 1
            || rect.MaxX > GridWidth - 2 || rect.MaxZ > GridDepth - 2)
        {
            throw new InvalidOperationException(
                $"maze region '{name}' escapes the solid border " +
                $"(x {rect.MinX}..{rect.MaxX}, z {rect.MinZ}..{rect.MaxZ}; " +
                $"grid {GridWidth}x{GridDepth})");
        }
        for (int x = rect.MinX; x <= rect.MaxX; x++)
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                _open[x, z] = true;
    }

    /// <summary>
    /// 「全联通」是需求硬条件，因此用洪泛真断言而不是靠肉眼看布局表：任何开放格够不到
    /// 出生格就直接抛错，避免把一段跑不通的路当成 Daddy 的运动缺陷来查。
    /// </summary>
    private void ValidateConnectivity()
    {
        Cell start = PathCorners[0];
        if (!IsOpen(start.X, start.Z))
            throw new InvalidOperationException($"maze spawn cell {start} is solid");

        var seen = new bool[GridWidth, GridDepth];
        var queue = new Queue<Cell>();
        seen[start.X, start.Z] = true;
        queue.Enqueue(start);
        int reached = 0;
        while (queue.Count > 0)
        {
            Cell cell = queue.Dequeue();
            reached++;
            foreach (Cell next in Neighbours(cell))
            {
                if (!IsOpen(next.X, next.Z) || seen[next.X, next.Z])
                    continue;
                seen[next.X, next.Z] = true;
                queue.Enqueue(next);
            }
        }

        int total = 0;
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridDepth; z++)
                if (_open[x, z]) total++;
        if (reached != total)
        {
            throw new InvalidOperationException(
                $"maze skeleton is not fully connected: {reached}/{total} open cells " +
                $"reachable from {start}");
        }
    }

    private static IEnumerable<Cell> Neighbours(Cell cell)
    {
        yield return new Cell(cell.X + 1, cell.Z);
        yield return new Cell(cell.X - 1, cell.Z);
        yield return new Cell(cell.X, cell.Z + 1);
        yield return new Cell(cell.X, cell.Z - 1);
    }

    /// <summary>拐点 → 逐格路径点，并真断言每一格都开放、每一段都轴对齐。</summary>
    private void ExpandPath()
    {
        _waypoints.Add(CellCenter(PathCorners[0]));
        for (int i = 1; i < PathCorners.Length; i++)
        {
            Cell from = PathCorners[i - 1];
            Cell to = PathCorners[i];
            int dx = Math.Sign(to.X - from.X);
            int dz = Math.Sign(to.Z - from.Z);
            if (dx != 0 && dz != 0)
            {
                throw new InvalidOperationException(
                    $"maze path leg {i} ({from} -> {to}) is not axis aligned");
            }
            if (dx == 0 && dz == 0)
                continue;

            Cell cursor = from;
            while (cursor != to)
            {
                cursor = new Cell(cursor.X + dx, cursor.Z + dz);
                if (!IsOpen(cursor.X, cursor.Z))
                {
                    throw new InvalidOperationException(
                        $"maze path leg {i} ({from} -> {to}) crosses solid cell {cursor}");
                }
                _waypoints.Add(CellCenter(cursor));
            }
        }
    }

    // ---- 场景构建 ----

    /// <summary>
    /// 全部 collider 挂在同一个 StaticBody3D 下：内核只消费命中点/法线/距离，不读 collider
    /// 身份，合并成一个体可省掉上百个物理体的注册与广相开销。
    /// </summary>
    public void Build(Node3D parent)
    {
        var root = new Node3D { Name = "Maze" };
        parent.AddChild(root);

        var body = new StaticBody3D { Name = "MazeCollision" };
        root.AddChild(body);

        var floorVisuals = new Node3D { Name = "FloorVisuals" };
        var wallVisuals = new Node3D { Name = "WallVisuals" };
        _ceilingVisuals = new Node3D { Name = "CeilingVisuals" };
        root.AddChild(floorVisuals);
        root.AddChild(wallVisuals);
        root.AddChild(_ceilingVisuals);

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

        // 楼板/天花板**只铺开放格**（≙ RoomBuilder 逐房间铺板）：实心区在主仓是纯虚空，
        // 不是有顶有地的密闭空腔。这个区别不是美观问题——铺满会让穿墙的触手段落进一个
        // 3.2m 高的封闭盒子里安家，后缀重建永远找不到可行候选，把一次可自愈的穿墙
        // 放大成永久阻断（实测阻断 686 tick）。逐行合并连续开放格，减少共面接缝。
        int slabs = 0;
        for (int z = 0; z < GridDepth; z++)
        {
            int x = 0;
            while (x < GridWidth)
            {
                if (!_open[x, z]) { x++; continue; }
                int run = 0;
                while (x + run < GridWidth && _open[x + run, z]) run++;
                Vector3 center = Origin + new Vector3(
                    (x + run * 0.5f) * CellSize, 0f, (z + 0.5f) * CellSize);
                Vector3 size = new(run * CellSize, SlabThickness, CellSize);
                AddBox(body, floorVisuals, floorMaterial,
                    center + new Vector3(0f, -SlabThickness * 0.5f, 0f), size, "Floor");
                AddBox(body, _ceilingVisuals, ceilingMaterial,
                    center + new Vector3(0f, RoomHeight + SlabThickness * 0.5f, 0f),
                    size, "Ceiling");
                slabs += 2;
                x += run;
            }
        }

        int walls = 0;
        // ≙ RoomBuilder.AddWallRun：墙心在净高中点，墙高上下各多吃一层楼板。
        float wallCenterY = RoomHeight * 0.5f;
        for (int x = 0; x < GridWidth; x++)
        {
            for (int z = 0; z < GridDepth; z++)
            {
                if (!_open[x, z])
                    continue;
                // 只有「开放格 ↔ 实心格/界外」的边才建墙；两侧都开放即整格通口，
                // 与主仓门格跳过墙段的结果完全一致。
                if (!IsOpen(x + 1, z))
                    walls += AddWall(body, wallVisuals, wallMaterial, x, z, MazeFace.PlusX, wallCenterY);
                if (!IsOpen(x - 1, z))
                    walls += AddWall(body, wallVisuals, wallMaterial, x, z, MazeFace.MinusX, wallCenterY);
                if (!IsOpen(x, z + 1))
                    walls += AddWall(body, wallVisuals, wallMaterial, x, z, MazeFace.PlusZ, wallCenterY);
                if (!IsOpen(x, z - 1))
                    walls += AddWall(body, wallVisuals, wallMaterial, x, z, MazeFace.MinusZ, wallCenterY);
            }
        }

        ValidateBuiltGeometry();
        BuildPathMarkers(root);

        GD.Print($"[DADDY-MAZE] built grid={GridWidth}x{GridDepth} " +
                 $"span={GridWidth * CellSize:F1}x{GridDepth * CellSize:F1}m " +
                 $"cell={CellSize:F2}m height={RoomHeight:F2}m wall={WallThickness:F2}m " +
                 $"slabs={slabs} walls={walls} waypoints={_waypoints.Count} " +
                 $"origin=({Origin.X:F1},{Origin.Y:F1},{Origin.Z:F1})");
    }

    /// <summary>
    /// 几何自检：格网上算出来的路径必须在**真的建出来的墙**里走得通。
    /// 格逻辑（连通性、路径展开）与几何发射是两套代码，前者全绿不能证明后者没错位——
    /// 第一版转置的墙就通过了全部格断言，只在跑起来时表现为「生物莫名走不动」。
    /// 这里直接对已发射的墙盒做水平线段-AABB 相交，把这类错位钉死在构建期。
    /// </summary>
    private void ValidateBuiltGeometry()
    {
        for (int i = 1; i < _waypoints.Count; i++)
        {
            Vector3 from = _waypoints[i - 1];
            Vector3 to = _waypoints[i];
            foreach ((Vector3 center, Vector3 halfSize) in _wallBoxes)
            {
                if (!SegmentHitsBoxXZ(from, to, center, halfSize))
                    continue;
                throw new InvalidOperationException(
                    $"maze wall at ({center.X:F2},{center.Z:F2}) blocks the planned path " +
                    $"between waypoint {i - 1} ({from.X:F2},{from.Z:F2}) and " +
                    $"{i} ({to.X:F2},{to.Z:F2}) — grid layout and emitted geometry disagree");
            }
        }
    }

    /// <summary>墙体贯通整层净高，因此水平面上的 slab 判定即可；两端各留半个格避免误报。</summary>
    private static bool SegmentHitsBoxXZ(Vector3 from, Vector3 to, Vector3 center, Vector3 halfSize)
    {
        float dx = to.X - from.X;
        float dz = to.Z - from.Z;
        float enter = 0f;
        float exit = 1f;
        if (!SlabClip(from.X - center.X, dx, halfSize.X, ref enter, ref exit)) return false;
        if (!SlabClip(from.Z - center.Z, dz, halfSize.Z, ref enter, ref exit)) return false;
        return enter <= exit;
    }

    private static bool SlabClip(float origin, float direction, float half, ref float enter, ref float exit)
    {
        if (Math.Abs(direction) < 1e-6f)
            return Math.Abs(origin) <= half;
        float t0 = (-half - origin) / direction;
        float t1 = (half - origin) / direction;
        if (t0 > t1) (t0, t1) = (t1, t0);
        enter = Math.Max(enter, t0);
        exit = Math.Min(exit, t1);
        return enter <= exit;
    }

    private enum MazeFace { MinusX, PlusX, MinusZ, PlusZ }

    /// <summary>
    /// 在格 (cellX,cellZ) 的指定面上建一段墙。参数刻意用「格 + 面」而不是「lane + along」：
    /// 后者的两个整数在 X/Z 两种朝向下含义互换，第一版就是在这里把两者传反，造出一整套
    /// 转置的假墙——生物在开阔走廊里满推进力却纹丝不动，看起来像内核缺陷。
    /// </summary>
    private int AddWall(
        StaticBody3D body,
        Node3D visuals,
        Material material,
        int cellX,
        int cellZ,
        MazeFace face,
        float localCenterY)
    {
        bool alongZ = face is MazeFace.MinusX or MazeFace.PlusX;
        float laneX = (face == MazeFace.PlusX ? cellX + 1 : cellX) * CellSize;
        float laneZ = (face == MazeFace.PlusZ ? cellZ + 1 : cellZ) * CellSize;
        Vector3 center = Origin + (alongZ
            ? new Vector3(laneX, localCenterY, (cellZ + 0.5f) * CellSize)
            : new Vector3((cellX + 0.5f) * CellSize, localCenterY, laneZ));
        Vector3 size = alongZ
            ? new Vector3(WallThickness, WallHeight, CellSize)
            : new Vector3(CellSize, WallHeight, WallThickness);
        AddBox(body, visuals, material, center, size, "Wall");
        _wallBoxes.Add((center, size * 0.5f));
        return 1;
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

    /// <summary>路径可视化：全部路点画小球，另有一个高亮球跟随当前目标。纯渲染。</summary>
    private void BuildPathMarkers(Node3D root)
    {
        _pathMarkers = new Node3D { Name = "PathMarkers" };
        root.AddChild(_pathMarkers);

        var idleMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.75f, 1f, 0.85f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var dot = new SphereMesh { Radius = 0.11f, Height = 0.22f, RadialSegments = 8, Rings = 4 };
        foreach (Vector3 waypoint in _waypoints)
        {
            _pathMarkers.AddChild(new MeshInstance3D
            {
                Mesh = dot,
                MaterialOverride = idleMaterial,
                Position = waypoint + new Vector3(0f, 0.12f, 0f),
            });
        }

        _activeMarker = new MeshInstance3D
        {
            Name = "ActiveWaypoint",
            Mesh = new SphereMesh { Radius = 0.26f, Height = 0.52f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.82f, 0.25f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            TopLevel = true,
        };
        root.AddChild(_activeMarker);
    }

    /// <summary>把高亮球移到当前喂给内核的点（已含抬升）。传 null 隐藏。</summary>
    public void SetActiveWaypoint(Vector3? worldTarget)
    {
        if (_activeMarker is null)
            return;
        // 每帧都会被调用，所以这里必须复读总开关：否则关掉的标记下一帧又被点亮。
        _activeMarker.Visible = _markersVisible && worldTarget is not null;
        if (worldTarget is { } target)
            _activeMarker.GlobalPosition = target;
    }

    /// <summary>隐藏天花板网格但**保留碰撞体**：俯视观察用，不改任何物理或哈希。</summary>
    public void SetCeilingVisible(bool visible)
    {
        if (_ceilingVisuals is not null)
            _ceilingVisuals.Visible = visible;
    }

    /// <summary>
    /// 路径可视化总开关：蓝色路点串 + 黄色当前目标球一起收。第一人称观察时它们会挡视线，
    /// 而且黄球正好悬在人眼高度上。纯渲染，不碰路径驱动本身。
    /// </summary>
    public void SetPathMarkersVisible(bool visible)
    {
        _markersVisible = visible;
        if (_pathMarkers is not null)
            _pathMarkers.Visible = visible;
        if (_activeMarker is not null && !visible)
            _activeMarker.Visible = false;
    }
}
