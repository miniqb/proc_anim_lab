using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒场景根节点：在 _PhysicsProcess（固定 40 tick/s）驱动物理内核，
/// 在 _Process 里按物理插值分数渲染。逻辑一律不读 delta——步长恒为 1 tick，
/// 内核里所有速度/力的单位都是「米/tick」（确定性来源，也与雨世界参数表同构）。
///
/// M3：场景主体是四腿 Walker。WASD 给移动意图（世界 XZ 轴）——推着墙走会被支撑系
/// 重定向为向上爬（走/爬涌现，无模式键）；左键拖拽任意 chunk。
/// 确定性回归：--determinism=N 模式禁用输入，改跑脚本化路点巡走（上坡→下坡→撞墙爬墙
/// 全部进哈希），提速到 400Hz 跑 N tick 打印状态哈希后退出。40Hz 与 400Hz 哈希必须一致。
/// </summary>
public partial class SandboxWorld : Node3D
{
    /// <summary>重力（米/秒²，人类可读单位）。默认 36 = 雨世界 0.9px/tick² 的直接换算。</summary>
    [Export] public float GravityMps2 = 36f;

    // 空气/表面摩擦由 Walker 按重力开关双档切换（≙ RW），不再由场景导出参数控制。
    [Export] public int ConstraintIterations = 3;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;

    private const float TickDt = 0.025f; // 40 tick/s，与 project.godot 的 physics_ticks_per_second 一致

    private float _perturb; // --perturb=x 灵敏度自检：初始位置微扰 → 哈希必须变
    private Vector3 _spawn = new(0f, 2f, 0f); // --spawn=x,y,z 覆盖出生点（坡上/墙边测试用）
    private long _yankTick = -1; // --yank=T：T tick 调 Walker.Launch 抛掷（「拎起再摔」+击飞 API 的实际覆盖）
    private ulong? _expectHash; // --expect-hash=X16：终值哈希对基线断言（回归脚本传文档基线，防「确定但错误」）
    private bool _fatal; // CLI 畸形等致命错：停帧防 NRE 无限刷屏，退出码 2

    /// <summary>确定性模式的巡走路点（XZ 平面，Y 忽略）。M3 路线覆盖三种地形：
    /// ① 上缓坡到坡面中段（坡 x∈[1.15,6.85]）② 下坡横穿平地 ③ 目标点在 x=-6 墙的
    /// 背面——撞墙后移动意图被支撑系重定向为向上爬；翻过墙顶则落地续走并循环路线，
    /// 翻不过就贴墙爬到 tick 预算用尽，两种结局都确定性地进哈希。</summary>
    private static readonly Vector3[] DefaultRoute =
    {
        new(4.2f, 0f, 0f),
        new(0.5f, 0f, 1.8f),
        new(-7.5f, 0f, 0f),
    };

    /// <summary>--route=wall：两路点垂直夹着 x=-6 的墙来回穿——每 +1 个 waypointsReached
    /// = 一次成功翻越（含正反两向），配 --spawn=-4,0.5,0 即「正面推墙」的翻越成功率测试。</summary>
    private static readonly Vector3[] WallRoute =
    {
        new(-7.5f, 0f, 0f),
        new(-4f, 0f, 0f),
    };

    /// <summary>--route=stand：零路点零输入的站桩路线。配 --spawn=-6,3.7,0（空降薄墙顶）
    /// 复现闲置姿态：悬空侧的脚找不到落点又无移动意图 → 应垂回身侧（IdlePose）而非悬在前伸位。</summary>
    private static readonly Vector3[] StandRoute = System.Array.Empty<Vector3>();

    private Vector3[] _waypoints = DefaultRoute;
    private int _waypointIndex;
    private int _waypointsReached;

    private readonly List<Body> _bodies = new();
    private Walker _walker = null!;
    private BreedParams _breed = BodyFactory.Default();
    private readonly RaycastTerrainQuery _terrain = new();
    private RayDebugDraw _rayDebug = null!;
    private readonly BodyRenderer _renderer = new();
    private readonly DragController _drag = new();
    private DeterminismProbe? _probe;
    private Camera3D _camera = null!;
    private Vector3 _gravityPerTick;
    private long _tick;

    public override void _Ready()
    {
        // 输出与解析统一不变文化：矩阵脚本按「小数点 + 逗号分隔」解析 [FINAL]/[METRIC]，
        // 逗号小数 locale（de_DE 等）会让 embed/wallside 位置断言静默退化为恒 PASS（终审 C0）。
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        _camera = GetNode<Camera3D>("Camera3D");
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        _drag.Spring = DragSpring;
        _drag.Damping = DragDamping;
        _drag.MaxForce = DragMaxForce;

        if (!ParseDeterminismArgs())
        {
            _fatal = true;
            GetTree().Quit(2);
            return;
        }
        _rayDebug = new RayDebugDraw(_terrain);
        _rayDebug.Build(this);
        SpawnWalker(_breed, _spawn);
        if (_perturb != 0f)
        {
            _walker.Body.Chunks[0].Pos += new Vector3(_perturb, 0f, 0f);
            _walker.Body.Chunks[0].LastPos = _walker.Body.Chunks[0].Pos;
        }
        GD.Print($"[SANDBOX] ready, tps={Engine.PhysicsTicksPerSecond}, breed={_breed.Name}, " +
                 $"determinism={(_probe is not null ? "on" : "off")}");
    }

    /// <summary>（重）生成行走体：替换物理对象并重建渲染节点（数字键换品种共用此路径）。</summary>
    private void SpawnWalker(BreedParams breed, Vector3 origin)
    {
        _breed = breed;
        _walker = BodyFactory.CreateWalker(origin, breed);
        _walker.Body.ConstraintIterations = ConstraintIterations;
        _bodies.Clear();
        _bodies.Add(_walker.Body);
        _drag.Release();
        _renderer.Clear();
        _renderer.Build(this, _bodies, _walker.Limbs, _walker);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
        {
            return;
        }
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);
        _rayDebug.BeginTick();

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

        if (_yankTick >= 0 && _tick == _yankTick)
        {
            // 走正式击飞 API（曾直接抠 Head.Vel 五个 tick——Launch 因此零回归覆盖，终审 C11）。
            _walker.Launch(new Vector3(0.1f, 0.4f, 0.15f));
        }

        // 地形查询经 _rayDebug 转发（纯观测装饰器）：F3 可视化打出的所有射线。
        var ctx = new TickContext(_gravityPerTick, _rayDebug, _tick);
        _walker.Tick(ctx);

        if (_probe is not null)
        {
            TrackQualityMetrics();
            _probe.Record(_tick, _bodies, _walker.Limbs);
            if (_probe.Finished)
            {
                GetTree().Quit(DumpFinalState());
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
        if (_waypoints.Length == 0)
        {
            _walker.MoveDir = Vector3.Zero;
            _walker.RunSpeed = 0f;
            return;
        }
        Vector3 target = _waypoints[_waypointIndex];
        Vector3 toTarget = target - _walker.Head.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.4f)
        {
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _waypointsReached++;
            target = _waypoints[_waypointIndex];
            toTarget = target - _walker.Head.Pos;
            toTarget.Y = 0f;
        }
        _walker.MoveDir = toTarget.LengthSquared() < 1e-12f ? Vector3.Zero : toTarget.Normalized();
        _walker.RunSpeed = 1f;
    }

    private float _maxConstraintDev; // 主连接距离偏差峰值（验收：<10% RestLength）
    private float _maxFoldIntrusion; // 防折叠支柱被压入的峰值深度（米）——脊柱折叠程度的直接观测
    private long _foldTicks;         // 深折叠（压入 > 支柱下限 1/3）持续 tick 数：区分落地瞬态与持续折叠
    private float _walkDistance;     // 头部 XZ 累计行走里程（验证「走得动」）
    private Vector3 _lastHeadPos;
    private long _gripTickSum;       // Σ 每 tick 抓地腿数（除以 tick 数 = 平均抓地腿数）
    private long _gravityOffTicks;   // 重力被关（站稳/攀爬）的 tick 数（M3 涌现验证）
    private float _maxHeadY;         // 头部最高点（爬墙验证：平地行走 ≈0.3，上墙应明显更高）
    private float _endDevRatio;      // tick 末（碰撞后）连接偏差率（偏差/该连接 RestLength），每 tick 覆盖 → 终值即终态
    private float _maxEndDevRatio;   // 同上的峰值（落地冲击等瞬态也计入，只观测不判定）
    private long _stretchTicks;      // tick 末偏差率 >0.5 的 tick 数：瞬态尖峰只占几 tick，跨墙卡链会长期累积
    private long _deepRun;           // 当前连续「深度违反」（偏差率 >1 = 卡链释放的触发带）tick 数
    private long _maxDeepRun;        // 深度违反的最长连跑——判定用：释放机制保证单环 ≤10、整链级联 ≤~50，
                                     // 断裂未恢复是 800+；终态快照式判据会在合法释放窗口上误报，这个不会
    private bool _nonFinite;         // 任意 chunk/limb 状态出现 NaN/Inf（一票 FAIL）

    /// <summary>确定性模式下顺带记录质量指标：约束偏差峰值 + 行走里程 + 抓地/重力开关统计。</summary>
    private void TrackQualityMetrics()
    {
        // 用求解器自己的观测值：碰撞阶段会把 chunk 推开，那不是求解器的误差，
        // 下一 tick 的松弛会立即修正——在 tick 末尾直接量距离会把它误记为约束失效。
        if (_bodies[0].LastRelaxDeviation > _maxConstraintDev)
        {
            _maxConstraintDev = _bodies[0].LastRelaxDeviation;
        }

        // 防折叠支柱（SoftOnly PushOnly）的压入深度：脊柱折得越狠值越大（0 = 从未折过下限）。
        bool deepFold = false;
        foreach (ChunkConnection conn in _bodies[0].Connections)
        {
            if (conn.SoftOnly && conn.ConstraintMode == ChunkConnection.Mode.PushOnly)
            {
                float intrusion = conn.RestLength - (conn.B.Pos - conn.A.Pos).Length();
                if (intrusion > _maxFoldIntrusion)
                {
                    _maxFoldIntrusion = intrusion;
                }
                deepFold |= intrusion > conn.RestLength / 3f;
            }
        }
        if (deepFold)
        {
            _foldTicks++;
        }

        if (_lastHeadPos != Vector3.Zero)
        {
            Vector3 step = _walker.Head.Pos - _lastHeadPos;
            step.Y = 0f;
            _walkDistance += step.Length();
        }
        _lastHeadPos = _walker.Head.Pos;
        _gripTickSum += _walker.LegsGripping;
        if (!_walker.ApplyGravity)
        {
            _gravityOffTicks++;
        }
        if (_walker.Head.Pos.Y > _maxHeadY)
        {
            _maxHeadY = _walker.Head.Pos.Y;
        }

        // tick 末（碰撞后）的连接偏差：LastRelaxDeviation 只看松弛末，碰撞把 chunk 推回
        // 墙外造成的持续断裂（尾链跨墙卡死）只有这里看得见——按偏差率判定，尾链细节不被长节稀释。
        float endRatio = 0f;
        foreach (ChunkConnection conn in _bodies[0].Connections)
        {
            if (conn.SoftOnly)
            {
                continue;
            }
            float err = (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength;
            float dev = conn.ConstraintMode switch
            {
                ChunkConnection.Mode.PullOnly => Mathf.Max(0f, err),
                ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -err),
                _ => Mathf.Abs(err),
            };
            float ratio = conn.RestLength > 1e-6f ? dev / conn.RestLength : 0f;
            if (ratio > endRatio)
            {
                endRatio = ratio;
            }
        }
        _endDevRatio = endRatio;
        if (endRatio > _maxEndDevRatio)
        {
            _maxEndDevRatio = endRatio;
        }
        if (endRatio > 0.5f)
        {
            _stretchTicks++;
        }
        _deepRun = endRatio > 1f ? _deepRun + 1 : 0;
        if (_deepRun > _maxDeepRun)
        {
            _maxDeepRun = _deepRun;
        }

        foreach (BodyChunk c in _bodies[0].Chunks)
        {
            _nonFinite |= !c.Pos.IsFinite() || !c.Vel.IsFinite();
        }
        foreach (Limb l in _walker.Limbs)
        {
            _nonFinite |= !l.Pos.IsFinite();
        }
    }

    /// <summary>探针跑完后输出终态与判定（[RESULT] PASS/FAIL），返回进程退出码。
    /// 判定项：有限值、终态约束偏差（碰撞后！）、哈希对基线（--expect-hash 提供时）。
    /// 只打印不断言的旧版是假绿——NaN、尾链跨墙断裂、哈希漂移全都照样退 0。</summary>
    private int DumpFinalState()
    {
        GD.Print($"[METRIC] maxConstraintDev={_maxConstraintDev:F4} " +
                 $"({_maxConstraintDev / _bodies[0].Connections[0].RestLength * 100f:F1}% of rest) " +
                 $"maxFoldIntrusion={_maxFoldIntrusion:F3}m foldTicks={_foldTicks} " +
                 $"walkDistance={_walkDistance:F2}m waypointsReached={_waypointsReached} " +
                 $"avgLegsGripping={(float)_gripTickSum / _tick:F2}/{_walker.Limbs.Count} " +
                 $"gravityOff={(float)_gravityOffTicks / _tick * 100f:F0}% maxHeadY={_maxHeadY:F2} " +
                 $"endDev={_endDevRatio:F2}x maxEndDev={_maxEndDevRatio:F2}x stretchTicks={_stretchTicks} " +
                 $"maxDeepRun={_maxDeepRun} snagReleases={_bodies[0].SnagReleases}");
        Vector3 sn = _walker.SupportNormal;
        GD.Print($"[FINAL] walker applyGravity={_walker.ApplyGravity} footing={_walker.FootingCounter} " +
                 $"noGrip={_walker.NoGripCounter} stall={_walker.StallTicks} " +
                 $"headVel={_walker.Head.Vel.Length():F4} support=({sn.X:F3},{sn.Y:F3},{sn.Z:F3})");
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
                     $"grip={l.GripCounter} reaching={l.ReachingForTerrain} idle={l.IdlePose} " +
                     $"extra={l.ExtraLongStep} contact={l.TerrainContact}");
        }

        var reasons = new List<string>();
        if (_nonFinite)
        {
            reasons.Add("状态出现 NaN/Inf");
        }
        if (_maxDeepRun > 100)
        {
            reasons.Add($"约束深度断裂持续 {_maxDeepRun} tick（>100）——卡链释放未起效/求解崩坏");
        }
        if (_expectHash is ulong expect && _probe!.Hash != expect)
        {
            reasons.Add($"哈希 {_probe.Hash:X16} ≠ 基线 {expect:X16}（有意改内核请同步两处真相源：矩阵脚本 + smoke ExpectedHash）");
        }
        if (_bodies[0].SnagReleases > 60)
        {
            // maxDeepRun 检不出这种 churn：释放每 10 tick 把深违反清零，慢性传送震荡照样全绿（终审 C5）。
            reasons.Add($"卡链释放 {_bodies[0].SnagReleases} 次（>60）——传送震荡/慢性卡死");
        }
        bool pass = reasons.Count == 0;
        GD.Print(pass ? "[RESULT] PASS" : $"[RESULT] FAIL: {string.Join("; ", reasons)}");
        return pass ? 0 : 1;
    }

    public override void _Process(double delta)
    {
        if (_fatal)
        {
            return;
        }
        _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
        _rayDebug.Draw(_camera, _walker);
    }

    /// <summary>F3：开关射线+推进目标（胡萝卜）可视化（只影响绘制）。数字键 1~N：现场换品种重生（交互模式限定）。</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (key.PhysicalKeycode == Key.F3)
        {
            _rayDebug.Enabled = !_rayDebug.Enabled;
            GD.Print($"[SANDBOX] ray debug {(_rayDebug.Enabled ? "on" : "off")}");
            return;
        }
        if (_probe is not null || key.PhysicalKeycode < Key.Key1 || key.PhysicalKeycode > Key.Key9)
        {
            return;
        }
        BreedParams[] breeds = BodyFactory.AllBreeds();
        int index = (int)(key.PhysicalKeycode - Key.Key1);
        if (index < breeds.Length)
        {
            // 在原地上方重生：旧身体整体替换（物理与渲染都换新），品种对比不用重启场景。
            SpawnWalker(breeds[index], _walker.Hips.Pos + Vector3.Up * 0.5f);
            GD.Print($"[SANDBOX] breed -> {breeds[index].Name}");
        }
    }

    /// <summary>解析 `-- --determinism=N [--tps=400]`：无头回归模式，禁输入、可加速跑。
    /// 返回 false = 参数畸形（含未知开关、非有限数）。必须快速失败——解析半途抛异常曾把
    /// _Ready 留在残局，随后 _PhysicsProcess 每帧 NRE、进程不退出、日志无限膨胀。</summary>
    private bool ParseDeterminismArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (arg.StartsWith("--determinism="))
                {
                    int ticks = int.Parse(arg["--determinism=".Length..], inv);
                    if (ticks <= 0)
                    {
                        throw new System.FormatException("tick 数必须为正");
                    }
                    _probe = new DeterminismProbe(ticks);
                }
                else if (arg.StartsWith("--tps="))
                {
                    int tps = int.Parse(arg["--tps=".Length..], inv);
                    if (tps <= 0)
                    {
                        throw new System.FormatException("tps 必须为正");
                    }
                    Engine.PhysicsTicksPerSecond = tps;
                    Engine.MaxPhysicsStepsPerFrame = 100;
                }
                else if (arg.StartsWith("--yank="))
                {
                    _yankTick = long.Parse(arg["--yank=".Length..], inv);
                }
                else if (arg == "--route=wall")
                {
                    _waypoints = WallRoute;
                }
                else if (arg == "--route=stand")
                {
                    _waypoints = StandRoute;
                }
                else if (arg.StartsWith("--breed="))
                {
                    _breed = BodyFactory.ByName(arg["--breed=".Length..]);
                }
                else if (arg.StartsWith("--perturb="))
                {
                    _perturb = float.Parse(arg["--perturb=".Length..], inv);
                    if (!float.IsFinite(_perturb))
                    {
                        throw new System.FormatException("perturb 必须是有限数（NaN 会污染全部状态且哈希照打）");
                    }
                }
                else if (arg.StartsWith("--spawn="))
                {
                    string[] parts = arg["--spawn=".Length..].Split(',');
                    if (parts.Length != 3)
                    {
                        throw new System.FormatException("spawn 需要 x,y,z 三个分量");
                    }
                    _spawn = new Vector3(
                        float.Parse(parts[0], inv), float.Parse(parts[1], inv), float.Parse(parts[2], inv));
                    if (!_spawn.IsFinite())
                    {
                        throw new System.FormatException("spawn 必须是有限数");
                    }
                }
                else if (arg.StartsWith("--expect-hash="))
                {
                    _expectHash = ulong.Parse(arg["--expect-hash=".Length..],
                        System.Globalization.NumberStyles.HexNumber, inv);
                }
                else
                {
                    // 打错的开关静默忽略 = 跑了错误配置还对着基线绿灯,必须硬拒。
                    throw new System.FormatException("未知参数");
                }
            }
            catch (System.Exception e) when (e is System.FormatException or System.OverflowException)
            {
                GD.PrintErr($"[SANDBOX] 参数畸形: {arg}（{e.Message}）");
                return false;
            }
        }
        if (_expectHash is not null && _probe is null)
        {
            // 断言只在探针结束时执行——没有探针它静默蒸发且进程永不退出（终审 C6）。
            GD.PrintErr("[SANDBOX] --expect-hash 需要配合 --determinism");
            return false;
        }
        return true;
    }
}
