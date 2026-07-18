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
/// 确定性回归：--determinism=N 模式禁用拖拽、提速到 400Hz 跑 N tick 打印状态哈希后退出。
/// 40Hz 与 400Hz 哈希必须一致——内核无 delta 依赖的最强断言。
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

    private readonly List<Body> _bodies = new();
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
        Body body = BodyFactory.CreateSlugcatWithTail(_spawn);
        body.AirFriction = AirFriction;
        body.SurfaceFriction = SurfaceFriction;
        body.ConstraintIterations = ConstraintIterations;
        if (_perturb != 0f)
        {
            body.Chunks[0].Pos += new Vector3(_perturb, 0f, 0f);
            body.Chunks[0].LastPos = body.Chunks[0].Pos;
        }
        _bodies.Add(body);
        _renderer.Build(this, _bodies);
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
        }

        var ctx = new TickContext(_gravityPerTick, _terrain, _tick);
        foreach (Body body in _bodies)
        {
            body.Tick(ctx);
        }

        if (_probe is not null)
        {
            TrackQualityMetrics();
            _probe.Record(_tick, _bodies);
            if (_probe.Finished)
            {
                DumpFinalState();
                GetTree().Quit();
            }
        }
    }

    private float _maxConstraintDev; // 主连接距离偏差峰值（验收：<10% RestLength）
    private float _tumbleAngle;      // 头-髋轴累计转角（弧度，验证「能滚」）
    private Vector3 _lastAxis;

    /// <summary>确定性模式下顺带记录质量指标：约束偏差峰值（松弛结束时采样）+ 翻滚累计角。</summary>
    private void TrackQualityMetrics()
    {
        // 用求解器自己的观测值：碰撞阶段会把 chunk 推开，那不是求解器的误差，
        // 下一 tick 的松弛会立即修正——在 tick 末尾直接量距离会把它误记为约束失效。
        if (_bodies[0].LastRelaxDeviation > _maxConstraintDev)
        {
            _maxConstraintDev = _bodies[0].LastRelaxDeviation;
        }

        ChunkConnection conn = _bodies[0].Connections[0];
        Vector3 axis = conn.B.Pos - conn.A.Pos;
        if (axis.LengthSquared() > 1e-12f)
        {
            axis = axis.Normalized();
            if (_lastAxis != Vector3.Zero)
            {
                _tumbleAngle += _lastAxis.AngleTo(axis);
            }
            _lastAxis = axis;
        }
    }

    /// <summary>探针跑完后输出终态：人工核对落地高度/是否 NaN/尾链是否发散。</summary>
    private void DumpFinalState()
    {
        GD.Print($"[METRIC] maxConstraintDev={_maxConstraintDev:F4} " +
                 $"({_maxConstraintDev / _bodies[0].Connections[0].RestLength * 100f:F1}% of rest) " +
                 $"tumbleAngle={Mathf.RadToDeg(_tumbleAngle):F0}deg");
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
    }

    public override void _Process(double delta)
    {
        _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
    }

    /// <summary>解析 `-- --determinism=N [--tps=400]`：无头回归模式，禁拖拽、可加速跑。</summary>
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
