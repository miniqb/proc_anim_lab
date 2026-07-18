using System.Collections.Generic;
using Godot;
using ProcAnimLab.Physics;
using ProcAnimLab.Terrain;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒场景根节点：在 _PhysicsProcess（固定 40 tick/s）驱动物理内核，
/// 在 _Process 里按物理插值分数渲染。逻辑一律不读 delta——步长恒为 1 tick，
/// 内核里所有速度/力的单位都是「米/tick」（确定性来源，也与雨世界参数表同构）。
///
/// M2：场景主体是四腿 Walker。WASD 给移动意图（世界 XZ 轴），左键拖拽任意 chunk。
/// 确定性回归：--determinism=N 模式禁用输入，改跑脚本化路点巡走（行走本身进哈希），
/// 提速到 400Hz 跑 N tick 打印状态哈希后退出。40Hz 与 400Hz 哈希必须一致。
/// </summary>
public partial class SandboxWorld : Node3D
{
    /// <summary>重力（米/秒²，人类可读单位）。默认 36 = 雨世界 0.9px/tick² 的直接换算。</summary>
    [Export] public float GravityMps2 = 36f;

    [Export] public float AirFriction = 0.98f;
    [Export] public float SurfaceFriction = 0.4f;
    [Export] public int ConstraintIterations = 3;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;

    private const float TickDt = 0.025f; // 40 tick/s，与 project.godot 的 physics_ticks_per_second 一致

    private float _perturb; // --perturb=x 灵敏度自检：初始位置微扰 → 哈希必须变
    private Vector3 _spawn = new(0f, 2f, 0f); // --spawn=x,y,z 覆盖出生点（坡上/墙边测试用）

    /// <summary>确定性模式的巡走路点（XZ 平面）。平地带 = x ∈ (-2, 1.15)：
    /// 缓坡从 x≈1.15 起、台阶在 x∈[-4,-2]，方框必须整体落在两者之间。</summary>
    private static readonly Vector3[] Waypoints =
    {
        new(0.8f, 0f, -2.2f),
        new(0.8f, 0f, 2.2f),
        new(-1.6f, 0f, 2.2f),
        new(-1.6f, 0f, -2.2f),
    };
    private int _waypointIndex;
    private int _waypointsReached;

    private readonly List<Body> _bodies = new();
    private Walker _walker = null!;
    private readonly RaycastTerrainQuery _terrain = new();
    private readonly BodyRenderer _renderer = new();
    private readonly DragController _drag = new();
    private DeterminismProbe? _probe;
    private Camera3D _camera = null!;
    private Vector3 _gravityPerTick;
    private long _tick;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        _drag.Spring = DragSpring;
        _drag.Damping = DragDamping;
        _drag.MaxForce = DragMaxForce;

        ParseDeterminismArgs();
        _walker = BodyFactory.CreateWalker(_spawn);
        Body body = _walker.Body;
        body.AirFriction = AirFriction;
        body.SurfaceFriction = SurfaceFriction;
        body.ConstraintIterations = ConstraintIterations;
        if (_perturb != 0f)
        {
            body.Chunks[0].Pos += new Vector3(_perturb, 0f, 0f);
            body.Chunks[0].LastPos = body.Chunks[0].Pos;
        }
        _bodies.Add(body);
        _renderer.Build(this, _bodies, _walker.Limbs);
        GD.Print($"[SANDBOX] ready, tps={Engine.PhysicsTicksPerSecond}, determinism={(_probe is not null ? "on" : "off")}");
    }

    public override void _PhysicsProcess(double delta)
    {
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);

        if (_probe is null)
        {
            _drag.SampleInput(_camera, _bodies);
            _drag.ApplyDragForce();
            SampleWalkInput();
        }
        else
        {
            SteerAlongWaypoints();
        }

        var ctx = new TickContext(_gravityPerTick, _terrain, _tick);
        _walker.Tick(ctx);

        if (_probe is not null)
        {
            TrackQualityMetrics();
            _probe.Record(_tick, _bodies, _walker.Limbs);
            if (_probe.Finished)
            {
                DumpFinalState();
                GetTree().Quit();
            }
        }
    }

    /// <summary>WASD → 世界 XZ 移动意图（相机固定朝 -Z，W 即「向屏幕里」）。</summary>
    private void SampleWalkInput()
    {
        Vector3 dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir.X += 1f;
        if (dir == Vector3.Zero)
        {
            _walker.MoveDir = Vector3.Zero;
            _walker.RunSpeed = 0f;
            return;
        }
        _walker.MoveDir = dir.Normalized();
        _walker.RunSpeed = 1f;
    }

    /// <summary>确定性模式的脚本化输入：绕路点方框巡走——把「走路」本身纳入回归。</summary>
    private void SteerAlongWaypoints()
    {
        Vector3 target = Waypoints[_waypointIndex];
        Vector3 toTarget = target - _walker.Head.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.4f)
        {
            _waypointIndex = (_waypointIndex + 1) % Waypoints.Length;
            _waypointsReached++;
            target = Waypoints[_waypointIndex];
            toTarget = target - _walker.Head.Pos;
            toTarget.Y = 0f;
        }
        _walker.MoveDir = toTarget.LengthSquared() < 1e-12f ? Vector3.Zero : toTarget.Normalized();
        _walker.RunSpeed = 1f;
    }

    private float _maxConstraintDev; // 主连接距离偏差峰值（验收：<10% RestLength）
    private float _walkDistance;     // 头部 XZ 累计行走里程（验证「走得动」）
    private Vector3 _lastHeadPos;
    private long _gripTickSum;       // Σ 每 tick 抓地腿数（除以 tick 数 = 平均抓地腿数）

    /// <summary>确定性模式下顺带记录质量指标：约束偏差峰值 + 行走里程 + 平均抓地腿数。</summary>
    private void TrackQualityMetrics()
    {
        // 用求解器自己的观测值：碰撞阶段会把 chunk 推开，那不是求解器的误差，
        // 下一 tick 的松弛会立即修正——在 tick 末尾直接量距离会把它误记为约束失效。
        if (_bodies[0].LastRelaxDeviation > _maxConstraintDev)
        {
            _maxConstraintDev = _bodies[0].LastRelaxDeviation;
        }

        if (_lastHeadPos != Vector3.Zero)
        {
            Vector3 step = _walker.Head.Pos - _lastHeadPos;
            step.Y = 0f;
            _walkDistance += step.Length();
        }
        _lastHeadPos = _walker.Head.Pos;
        _gripTickSum += _walker.LegsGripping;
    }

    /// <summary>探针跑完后输出终态：人工核对行走里程/抓地质量/是否 NaN/尾链是否发散。</summary>
    private void DumpFinalState()
    {
        GD.Print($"[METRIC] maxConstraintDev={_maxConstraintDev:F4} " +
                 $"({_maxConstraintDev / _bodies[0].Connections[0].RestLength * 100f:F1}% of rest) " +
                 $"walkDistance={_walkDistance:F2}m waypointsReached={_waypointsReached} " +
                 $"avgLegsGripping={(float)_gripTickSum / _tick:F2}/{_walker.Limbs.Count}");
        for (int b = 0; b < _bodies.Count; b++)
        {
            Body body = _bodies[b];
            for (int i = 0; i < body.Chunks.Count; i++)
            {
                BodyChunk c = body.Chunks[i];
                GD.Print($"[FINAL] body={b} chunk={i} pos=({c.Pos.X:F4},{c.Pos.Y:F4},{c.Pos.Z:F4}) " +
                         $"vel={c.Vel.Length():F5} contact={c.TerrainContact} r={c.Radius:F2}");
            }
        }
        for (int i = 0; i < _walker.Limbs.Count; i++)
        {
            Limb l = _walker.Limbs[i];
            GD.Print($"[FINAL] limb={i} pos=({l.Pos.X:F4},{l.Pos.Y:F4},{l.Pos.Z:F4}) " +
                     $"grip={l.GripCounter} reaching={l.ReachingForTerrain} contact={l.TerrainContact}");
        }
    }

    public override void _Process(double delta)
    {
        _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
    }

    /// <summary>解析 `-- --determinism=N [--tps=400]`：无头回归模式，禁输入、可加速跑。</summary>
    private void ParseDeterminismArgs()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--determinism="))
            {
                int ticks = int.Parse(arg["--determinism=".Length..]);
                _probe = new DeterminismProbe(ticks);
            }
            else if (arg.StartsWith("--tps="))
            {
                int tps = int.Parse(arg["--tps=".Length..]);
                Engine.PhysicsTicksPerSecond = tps;
                Engine.MaxPhysicsStepsPerFrame = 100;
            }
            else if (arg.StartsWith("--perturb="))
            {
                _perturb = float.Parse(arg["--perturb=".Length..],
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--spawn="))
            {
                string[] parts = arg["--spawn=".Length..].Split(',');
                _spawn = new Vector3(
                    float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
