using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒场景根节点：在 _PhysicsProcess（固定 40 tick/s）驱动物理内核，
/// 在 _Process 里按物理插值分数渲染。逻辑一律不读 delta——步长恒为 1 tick，
/// 内核里所有速度/力的单位都是「米/tick」（确定性来源，也与雨世界参数表同构）。
///
/// M3：场景主体是四腿 LizardLocomotionController。WASD 给移动意图（世界 XZ 轴）——推着墙走会被支撑系
/// 重定向为向上爬（走/爬涌现，无模式键）；左键拖拽任意 chunk。
/// 确定性回归：--determinism=N 模式禁用输入，改跑脚本化路点巡走（上坡→下坡→撞墙爬墙
/// 全部进哈希），提速到 400Hz 跑 N tick 打印状态哈希后退出。40Hz 与 400Hz 哈希必须一致。
/// </summary>
public partial class SandboxWorld : Node3D
{
    /// <summary>重力（米/秒²，人类可读单位）。默认 36 = 雨世界 0.9px/tick² 的直接换算。</summary>
    [Export] public float GravityMps2 = 36f;

    // 空气/表面摩擦由 LizardLocomotionController 按重力开关双档切换（≙ RW），不再由场景导出参数控制。
    [Export] public int ConstraintIterations = 3;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;

    // 编辑器式自由摄像机（右键，不按 Shift）：纯观察工具，跑在 _Process，不进物理 tick/确定性哈希。
    [Export] public float CameraFlySpeed = 6f; // 米/秒
    [Export] public float CameraMouseSensitivity = 0.003f; // 弧度/像素

    private const float TickDt = 0.025f; // 40 tick/s，与 project.godot 的 physics_ticks_per_second 一致

    private float _perturb; // --perturb=x 灵敏度自检：初始位置微扰 → 哈希必须变
    private Vector3 _spawn = new(0f, 2f, 0f); // --spawn=x,y,z 覆盖出生点（坡上/墙边测试用）
    private long _yankTick = -1; // --yank=T：T tick 调 LizardLocomotionController.Launch 抛掷（「拎起再摔」+击飞 API 的实际覆盖）
    private int _determinismTicks; // 场景事件在结束前预留恢复预算，避免最后一帧新开事件形成假红
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

    /// <summary>--route=turn：远离障碍物的纯平地 180° 往返。沿 Z 轴布置，避免坡、台阶和墙，
    /// 只验证多节脊柱能否绕支撑法线完成掉头，而不是让头/髋从彼此中间穿过去。</summary>
    private static readonly Vector3[] TurnRoute =
    {
        new(0f, 0f, 3f),
        new(0f, 0f, 7f),
    };

    /// <summary>--route=stand：零路点零输入的站桩路线。配 --spawn=-6,3.7,0（空降薄墙顶）
    /// 复现闲置姿态：悬空侧的脚找不到落点又无移动意图 → 应垂回身侧（IdlePose）而非悬在前伸位。</summary>
    private static readonly Vector3[] StandRoute = System.Array.Empty<Vector3>();

    /// <summary>--route=carrot：路线2输入通路（MoveTarget 直喂，≙ RW 寻路器喂路径格）回归。
    /// 宿主侧每 tick 把当前路点贴地采样后直喂内核，AtMoveTarget 到达信号驱动换点——
    /// 上坡/下坡/跨台阶全部走 External 胡萝卜。不含墙路点：隔墙远点是直喂契约违规
    /// （RW 寻路器只喂邻近可达格），爬墙由 default/wall 路线的 MoveDir 通路覆盖。</summary>
    private static readonly Vector3[] CarrotRoute =
    {
        new(4.2f, 0f, 0f),
        new(0.5f, 0f, 1.8f),
        new(-3f, 0f, 0f),
    };

    /// <summary>--route=carrot-turn：纯平地上的行进中 90° 胡萝卜重规划。目标不是到点后
    /// 自动换路点，而是在身体已沿旧方向稳定推进时，把一个新的固定地面点放到头部侧前方；
    /// 因而专测 FollowConnection 双点驱动，不触发只服务近 180° 掉头的 TurnAssist。</summary>
    private const float CarrotTurnTargetDistance = 4f;
    private const float CarrotTurnArmDistance = 0.6f;
    private const float CarrotTurnMinStep = 0.01f;
    private const int CarrotTurnRequiredArmTicks = 3;
    private const float CarrotTurnMiddleLeadThresholdDeg = 5f;
    private static readonly Vector3[] CarrotTurnDirections =
    {
        Vector3.Back, Vector3.Right, Vector3.Forward, Vector3.Left,
        Vector3.Back, Vector3.Left, Vector3.Forward, Vector3.Right,
    };

    private Vector3[] _waypoints = DefaultRoute;
    private int _waypointIndex;
    private int _waypointsReached;
    private bool _carrotDrive; // --route=carrot：路点经 MoveTarget 直喂（否则 MoveDir 方向驱动）
    private bool _carrotTurnDrive;

    private enum RegressionScenario
    {
        None,
        Turn,
        CarrotTurn,
        Tail,
        Corner,
    }

    private RegressionScenario _regressionScenario;
    private bool _wallTurnDrive;

    private readonly List<Body> _bodies = new();
    private LizardLocomotionController _lizardController = null!;
    private BreedParams _breed = BodyFactory.Default();
    private readonly RaycastTerrainQuery _terrain = new();
    private RayDebugDraw _rayDebug = null!;
    private readonly BodyRenderer _renderer = new();
    private readonly DragController _drag = new();
    private BreedSelectorUI? _breedUI;
    private DeterminismProbe? _probe;
    private Camera3D _camera = null!;
    private float _camYaw;
    private float _camPitch;
    private bool _cameraFlying; // 上一帧是否处于飞行态，仅用于检测切换边沿（含鼠标捕获模式）
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
        _camYaw = _camera.Rotation.Y;
        _camPitch = _camera.Rotation.X;
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
        SpawnLizard(_breed, _spawn);
        if (_perturb != 0f)
        {
            _lizardController.Body.Chunks[0].Pos += new Vector3(_perturb, 0f, 0f);
            _lizardController.Body.Chunks[0].LastPos = _lizardController.Body.Chunks[0].Pos;
        }
        if (_probe is null)
        {
            // 确定性回归模式禁交互输入（含品种切换），UI 面板只在交互模式下建——与数字键的既有限制对齐。
            BreedParams[] breeds = BodyFactory.AllBreeds();
            var names = new string[breeds.Length];
            for (int i = 0; i < breeds.Length; i++)
            {
                names[i] = breeds[i].Name;
            }
            _breedUI = new BreedSelectorUI();
            _breedUI.Build(this, names, SelectBreed);
            _breedUI.SyncSelection(Array.FindIndex(breeds, b => b.Name == _breed.Name));
        }
        GD.Print($"[SANDBOX] ready, tps={Engine.PhysicsTicksPerSecond}, breed={_breed.Name}, " +
                 $"determinism={(_probe is not null ? "on" : "off")}");
    }

    /// <summary>（重）生成行走体：替换物理对象并重建渲染节点（数字键换品种共用此路径）。</summary>
    private void SpawnLizard(BreedParams breed, Vector3 origin)
    {
        _breed = breed;
        _lizardController = BodyFactory.CreateLizardController(origin, breed);
        _lizardController.Body.ConstraintIterations = ConstraintIterations;
        _bodies.Clear();
        _bodies.Add(_lizardController.Body);
        _drag.Release();
        _renderer.Clear();
        _renderer.Build(this, _bodies, _lizardController.Limbs, _lizardController);
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
            _lizardController.Launch(new Vector3(0.1f, 0.4f, 0.15f));
        }

        // 地形查询经 _rayDebug 转发（纯观测装饰器）：F3 可视化打出的所有射线。
        var ctx = new TickContext(_gravityPerTick, _rayDebug, _tick);
        _lizardController.Tick(ctx);

        if (_probe is not null)
        {
            TrackQualityMetrics();
            _probe.Record(_tick, _bodies, _lizardController.Limbs);
            if (_probe.Finished)
            {
                GetTree().Quit(DumpFinalState());
            }
        }
    }

    /// <summary>WASD → 世界 XZ 移动意图（相机固定朝 -Z，W 即「向屏幕里」）；
    /// Shift+右键点地形 → MoveTarget 直喂（路线2 手测通路：命中点就是喂点，胡萝卜画紫色），
    /// 到点自动清除。WASD 一按立即接管（清 MoveTarget 回方向驱动）。
    /// 右键（不按 Shift）= 自由摄像机飞行态：WASD 让位给 UpdateCameraFly，本函数直接短路。</summary>
    private void SampleWalkInput()
    {
        if (WantCameraFly)
        {
            _lizardController.MoveDir = Vector3.Zero;
            _lizardController.RunSpeed = 0f;
            return;
        }

        Vector3 dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir.X += 1f;

        if (dir != Vector3.Zero)
        {
            _lizardController.MoveTarget = null;
            _lizardController.MoveDir = dir.Normalized();
            _lizardController.RunSpeed = 1f;
            return;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Right) && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            Vector2 mouse = _camera.GetViewport().GetMousePosition();
            Vector3 origin = _camera.ProjectRayOrigin(mouse);
            Vector3 rayDir = _camera.ProjectRayNormal(mouse);
            if (_rayDebug.Raycast(origin, origin + rayDir * 100f, out TerrainHit hit))
            {
                _lizardController.MoveTarget = hit.Point;
            }
        }

        if (_lizardController.MoveTarget is not null)
        {
            if (_lizardController.AtMoveTarget)
            {
                _lizardController.MoveTarget = null;
                _lizardController.RunSpeed = 0f;
                _lizardController.MoveDir = Vector3.Zero;
                return;
            }
            _lizardController.RunSpeed = 1f;
            return;
        }

        _lizardController.MoveDir = Vector3.Zero;
        _lizardController.RunSpeed = 0f;
    }

    /// <summary>确定性模式的脚本化输入：绕路点方框巡走——把「走路」本身纳入回归。</summary>
    private void SteerAlongWaypoints()
    {
        if (_wallTurnDrive)
        {
            SteerWallTurns();
            return;
        }
        if (_carrotTurnDrive)
        {
            SteerCarrotTurns();
            return;
        }
        if (_waypoints.Length == 0)
        {
            _lizardController.MoveDir = Vector3.Zero;
            _lizardController.RunSpeed = 0f;
            return;
        }
        if (_carrotDrive)
        {
            SteerCarrotWaypoints();
            return;
        }
        Vector3 target = _waypoints[_waypointIndex];
        Vector3 toTarget = target - _lizardController.Head.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.4f)
        {
            // 与 wall-turn 同一预算门：恢复观测 ~20 tick（对齐+3 tick 稳定），窗口尾部点火的
            // 反转必然 pending 假红（RearBrace 轮掉头节奏前移，第 16 次落进结尾窗口即红）。
            // 预算不足时原地停驶——不反转也不产生未观测的掉头，断言保持严格。
            if (_regressionScenario == RegressionScenario.Turn
                && _determinismTicks > 0 && _tick > _determinismTicks - 45)
            {
                _lizardController.MoveDir = Vector3.Zero;
                _lizardController.RunSpeed = 0f;
                return;
            }
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _waypointsReached++;
            if (_regressionScenario == RegressionScenario.Turn)
            {
                BeginTurnRecovery();
            }
            target = _waypoints[_waypointIndex];
            toTarget = target - _lizardController.Head.Pos;
            toTarget.Y = 0f;
        }
        _lizardController.MoveDir = toTarget.LengthSquared() < 1e-12f ? Vector3.Zero : toTarget.Normalized();
        _lizardController.RunSpeed = 1f;
    }

    /// <summary>行进中 90° 胡萝卜重规划：先沿 +Z 在纯平地上建立稳定运动；随后每当身体沿
    /// 当前方向净推进至少 0.6m、保持展开且连续 3 tick 确实在移动，就把目标侧转 90°；
    /// 四次同手性后反向覆盖另一侧。新目标以点击瞬间的头部位置为基准固定在 4m 外，之后不跟随。</summary>
    private void SteerCarrotTurns()
    {
        if (!_carrotTurnInitialized)
        {
            _carrotTurnDirectionIndex = 0;
            _carrotTurnDirection = CarrotTurnDirections[_carrotTurnDirectionIndex];
            _carrotTurnPhaseStart = _lizardController.Head.Pos;
            SetCarrotTurnTarget(_carrotTurnDirection);
            _carrotTurnInitialized = true;
        }

        bool movingOnFlat = _lizardController.LastMoveTargetKind == MoveTargetKind.External
            && _lizardController.HasMoveIntent
            && !_lizardController.AtMoveTarget
            && !_lizardController.ApplyGravity
            && _lizardController.SupportNormal.Dot(Vector3.Up) >= 0.9f
            && _lastHeadPlanarStep.Dot(_carrotTurnDirection) >= CarrotTurnMinStep;
        Vector3 forward = _lizardController.Head.Pos - _lizardController.Hips.Pos;
        forward.Y = 0f;
        Vector3 front = _lizardController.Head.Pos - _lizardController.SpineFollower.Pos;
        front.Y = 0f;
        Vector3 middle = _lizardController.SpineFollower.Pos - _lizardController.Hips.Pos;
        middle.Y = 0f;
        bool aligned = forward.LengthSquared() > 1e-10f
            && forward.Normalized().Dot(_carrotTurnDirection) >= 0.5f;
        bool segmentsAligned = front.LengthSquared() > 1e-10f && middle.LengthSquared() > 1e-10f
            && front.Normalized().Dot(_carrotTurnDirection) >= 0.5f
            && middle.Normalized().Dot(_carrotTurnDirection) >= 0.5f;
        float progress = (_lizardController.Head.Pos - _carrotTurnPhaseStart).Dot(_carrotTurnDirection);
        bool hasRecoveryBudget = _determinismTicks <= 0 || _tick <= _determinismTicks - 45;
        _carrotTurnArmTicks = !_carrotTurnActive && hasRecoveryBudget && movingOnFlat && aligned
            && segmentsAligned
            && _spineRecoveredLastTick && progress >= CarrotTurnArmDistance
            ? _carrotTurnArmTicks + 1
            : 0;

        if (_carrotTurnArmTicks >= CarrotTurnRequiredArmTicks)
        {
            Vector3 eventUp = _lizardController.SupportNormal.LengthSquared() > 1e-10f
                ? _lizardController.SupportNormal.Normalized()
                : Vector3.Up;
            Vector3 oldDirection = ActualCarrotDirection(eventUp);
            float oldTargetRemaining = _lizardController.MoveTarget is { } oldTarget
                ? ProjectOnPlane(oldTarget + eventUp * _lizardController.RideHeight - _lizardController.Head.Pos,
                    eventUp).Length()
                : 0f;
            _carrotTurnDirectionIndex = (_carrotTurnDirectionIndex + 1) % CarrotTurnDirections.Length;
            _carrotTurnDirection = CarrotTurnDirections[_carrotTurnDirectionIndex];
            _carrotTurnPhaseStart = _lizardController.Head.Pos;
            SetCarrotTurnTarget(_carrotTurnDirection);
            Vector3 newDirection = ActualCarrotDirection(eventUp);
            BeginCarrotTurn(oldDirection, newDirection, oldTargetRemaining, eventUp,
                eventUp.Dot(oldDirection.Cross(newDirection)));
            _carrotTurnArmTicks = 0;
        }

        _lizardController.RunSpeed = 1f;
    }

    private void SetCarrotTurnTarget(Vector3 direction)
    {
        Vector3 target = _lizardController.Head.Pos + direction * CarrotTurnTargetDistance;
        target.Y = 0f;
        _lizardController.MoveTarget = target;
    }

    private Vector3 ActualCarrotDirection(Vector3 up)
    {
        if (_lizardController.MoveTarget is not { } target)
        {
            return Vector3.Zero;
        }
        Vector3 direction = ProjectOnPlane(target + up * _lizardController.RideHeight - _lizardController.Head.Pos, up);
        return direction.LengthSquared() > 1e-10f ? direction.Normalized() : Vector3.Zero;
    }

    private void BeginCarrotTurn(Vector3 oldDirection, Vector3 newDirection, float oldTargetRemaining,
        Vector3 eventUp, float scheduledTurnSign)
    {
        _carrotTurnsObserved++;
        if (_carrotTurnActive)
        {
            _carrotTurnOverlapFailures++;
            return;
        }

        _carrotTurnActive = true;
        _carrotTurnStartedAt = _tick;
        _carrotTurnDesired = newDirection;
        _carrotTurnEventUp = eventUp;
        _carrotTurnHeadStart = _lizardController.Head.Pos;
        _carrotTurnFollowerStart = _lizardController.SpineFollower.Pos;
        _carrotTurnStableRun = 0;
        _carrotTurnMiddleLeadRun = 0;
        _carrotTurnMiddleCrossTick = -1;
        _carrotTurnFrontCrossTick = -1;
        _carrotTurnAxisLagRecorded = false;

        Vector3 frontAxis = ProjectOnPlane(_lizardController.Head.Pos - _lizardController.SpineFollower.Pos,
            _carrotTurnEventUp);
        Vector3 middleAxis = ProjectOnPlane(_lizardController.SpineFollower.Pos - _lizardController.Hips.Pos,
            _carrotTurnEventUp);
        _carrotTurnInitialFrontErrorDeg = AngleDeg(frontAxis, newDirection);
        _carrotTurnInitialMiddleErrorDeg = AngleDeg(middleAxis, newDirection);
        _maxCarrotTurnAbsDirectionDot = Mathf.Max(_maxCarrotTurnAbsDirectionDot,
            Mathf.Abs(oldDirection.Dot(newDirection)));
        _minCarrotTurnPreStep = Mathf.Min(_minCarrotTurnPreStep,
            _lastHeadPlanarStep.Dot(oldDirection));
        _minCarrotTurnOldTargetRemaining = Mathf.Min(_minCarrotTurnOldTargetRemaining,
            oldTargetRemaining);
        if (scheduledTurnSign >= 0f)
        {
            _carrotTurnPositiveHandTurns++;
        }
        else
        {
            _carrotTurnNegativeHandTurns++;
        }
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * value.Dot(normal);

    private static float AngleDeg(Vector3 from, Vector3 to)
    {
        return from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f
            ? 180f
            : Mathf.RadToDeg(from.AngleTo(to));
    }

    /// <summary>先正面推上 x=-5.8 墙面；支撑法线稳定后每 30 tick 反转一次沿墙 Y 意图。
    /// 同时保留少量朝墙分量维持抓握，前后方向点积约 -0.98，专门验证 TurnAssist 真正绕
    /// SupportNormal 而不是 world-up——后者会把竖直目标投影成零并空耗 16 tick。</summary>
    private void SteerWallTurns()
    {
        bool onWall = _lizardController.SupportNormal.X >= 0.8f;
        if (!onWall)
        {
            _wallSupportRun = 0;
            _lizardController.MoveDir = Vector3.Left;
            _lizardController.RunSpeed = 1f;
            return;
        }

        _wallSupportRun++;
        if (_wallSupportRun < 5)
        {
            _lizardController.MoveDir = Vector3.Left;
            _lizardController.RunSpeed = 1f;
            return;
        }

        // 与 carrot-turn 同一预算门：恢复观测要 ~20 tick（对齐+3 tick 稳定），窗口尾部
        // 点火的掉头必然 pending 假红——相位运气曾让 400 tick 恰好放完 11 次（2026-07
        // 拖尾点修复后相位前移即红）。预算不足时维持当前方向，断言保持严格。
        bool hasRecoveryBudget = _determinismTicks <= 0 || _tick <= _determinismTicks - 45;
        if (++_wallTurnPhaseTicks > 30 && hasRecoveryBudget)
        {
            _wallTurnPhaseTicks = 1;
            _wallTurnVertical = -_wallTurnVertical;
            BeginTurnRecovery();
        }
        _lizardController.MoveDir = new Vector3(-0.1f, _wallTurnVertical, 0f).Normalized();
        _lizardController.RunSpeed = 1f;
    }

    private void BeginTurnRecovery()
    {
        _turnsObserved++;
        if (_turnRecoveryActive)
        {
            _turnOverlapFailures++;
            return;
        }
        _turnRecoveryActive = true;
        _turnStartedAt = _tick;
        _turnStableRun = 0;
    }

    /// <summary>路线2（MoveTarget 直喂）的脚本化宿主：当前路点贴地采样后直喂内核，
    /// AtMoveTarget 到达即换下一点——模拟「寻路器逐格递进」的喂点节奏。
    /// 贴地采样走同一 ITerrainQuery（射线进 F3 可视化）≙ 宿主从导航网格取贴地路径点，
    /// 这正是直喂契约要求的「点贴近可达地形」。</summary>
    private void SteerCarrotWaypoints()
    {
        if (_lizardController.AtMoveTarget)
        {
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _waypointsReached++;
        }
        Vector3 wp = _waypoints[_waypointIndex];
        Vector3 fed = wp;
        if (_rayDebug.Raycast(wp + Vector3.Up * 3f, wp + Vector3.Down * 1f, out TerrainHit hit))
        {
            fed = hit.Point;
        }
        _lizardController.MoveTarget = fed;
        _lizardController.RunSpeed = 1f;
    }

    /// <summary>SoftOnly 防折叠支柱允许有弹性形变；低于静止长度 10% 且持续存在才计求解违反。
    /// 角度只保留为可读观测：同一个 100° 对 BodyStiffness=.3（理论允许约 81°）与 .5
    /// （理论允许约 97°）并不等价，不能再作为跨品种硬门。</summary>
    private const float SpineSupportViolationRatio = 0.10f;
    private const long SpineSupportRunFailTicks = 40;

    private const float SpineRecoveredAngleDeg = 140f;
    private const int StableRecoveryTicks = 3;

    private float _maxConstraintDev; // 松弛末主连接偏差峰值（落地瞬态观测；持续破坏由 tick 末 run 门控）
    private float _maxFoldIntrusion; // 防折叠支柱被压入的峰值深度（米）——脊柱折叠程度的直接观测
    private long _foldTicks;         // 深折叠（压入 > 支柱下限 1/3）持续 tick 数：区分落地瞬态与持续折叠
    private float _minSpineAngleDeg = 180f; // 头-SpineFollower-髋夹角最小值（仅 spine≥3 有意义）
    private long _spineAngleUnder100Run;    // 仅诊断：与历史 82-tick 旧指标直接对照，不作跨品种硬门
    private long _maxSpineAngleUnder100Run;
    private long _spineSupportRun;   // 支柱持续违反允许长度的连续 tick 数
    private long _maxSpineSupportRun;
    private float _maxSpineSupportDeficitRatio;

    // --route=turn：路点反转后恢复到展开姿态所需时间。
    private int _turnsObserved;
    private bool _turnRecoveryActive;
    private long _turnStartedAt;
    private int _turnStableRun;
    private long _maxTurnRecoveryTicks;
    private int _turnsRecovered;
    private int _turnOverlapFailures;
    private int _wallSupportRun;
    private int _wallTurnPhaseTicks;
    private float _wallTurnVertical = 1f;
    private int _wallTurnLostSupportRun;
    private int _maxWallTurnLostSupportRun;

    // --route=carrot-turn：行进中 90° MoveTarget 重规划。头前恢复是真断言；中段领先角/时长
    // 只作诊断，先量化用户看到的相位差，再决定是否值得改变 RW 允许的软体瞬态。
    private bool _carrotTurnInitialized;
    private int _carrotTurnDirectionIndex;
    private Vector3 _carrotTurnDirection;
    private Vector3 _carrotTurnPhaseStart;
    private int _carrotTurnArmTicks;
    private bool _carrotTurnActive;
    private long _carrotTurnStartedAt;
    private Vector3 _carrotTurnDesired;
    private Vector3 _carrotTurnEventUp = Vector3.Up;
    private Vector3 _carrotTurnHeadStart;
    private Vector3 _carrotTurnFollowerStart;
    private float _carrotTurnInitialFrontErrorDeg;
    private float _carrotTurnInitialMiddleErrorDeg;
    private int _carrotTurnStableRun;
    private int _carrotTurnMiddleLeadRun;
    private long _carrotTurnMiddleCrossTick = -1;
    private long _carrotTurnFrontCrossTick = -1;
    private bool _carrotTurnAxisLagRecorded;
    private int _carrotTurnsObserved;
    private int _carrotTurnsRecovered;
    private int _carrotTurnOverlapFailures;
    private int _carrotTurnExternalViolations;
    private long _maxCarrotTurnRecoveryTicks;
    private long _maxCarrotTurnFrontLagTicks;
    private int _carrotTurnAxisLagSamples;
    private int _carrotTurnMiddleFirstEvents;
    private float _maxCarrotTurnMiddleLeadDeg;
    private int _maxCarrotTurnMiddleLeadRun;
    private float _minCarrotTurnFollowerTranslationLead = float.PositiveInfinity;
    private float _maxCarrotTurnFollowerTranslationLead = float.NegativeInfinity;
    private float _maxCarrotTurnAbsDirectionDot;
    private float _minCarrotTurnPreStep = float.PositiveInfinity;
    private float _minCarrotTurnOldTargetRemaining = float.PositiveInfinity;
    private int _carrotTurnPositiveHandTurns;
    private int _carrotTurnNegativeHandTurns;
    private int _carrotTurnAssistViolations;
    private bool _spineRecoveredLastTick;

    // --route=wall-tail：尾链深违反/释放与身体随后恢复分开计量。
    private bool _tailDeepLastTick;
    private int _tailSnagEpisodes;
    private long _tailDeepRun;
    private long _maxTailDeepRun;
    private long _lastTailReleaseCount;
    private long _lastTailReleaseTick = -1;
    private int _tailBodyStableRun;
    private long _postTailRecoveryTicks = -1;
    private long _maxPostTailRecoveryTicks;
    private bool _tailRecoveryActive;
    private int _tailRecoveryCoalescedReleases;

    // --route=wall-corner：首次命中目标墙（不把出生点旁 Step 侧面算进去）后的换面与抬升。
    private const float CornerWallX = -5.8f;
    private long _cornerContactTick = -1;
    private long _cornerSupportTransitionTicks = -1;
    private long _cornerFallbackRun;
    private long _maxCornerFallbackRun;
    private long _cornerBendRun;
    private long _maxCornerBendRun;
    private float _cornerStartHeadY;
    private float _cornerRise60 = float.NaN;
    private float _walkDistance;     // 头部 XZ 累计行走里程（验证「走得动」）
    private Vector3 _lastHeadPos;
    private Vector3 _lastHeadPlanarStep;
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
    private float _minTerrainSqueeze = 1f;
    private float _maxPostRecoveryPenetration;

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
        float spineSupportDeficitRatio = 0f;
        foreach (ChunkConnection conn in _bodies[0].Connections)
        {
            if (conn.SoftOnly && conn.ConstraintMode == ChunkConnection.Mode.PushOnly)
            {
                float intrusion = Mathf.Max(0f, conn.RestLength - (conn.B.Pos - conn.A.Pos).Length());
                if (intrusion > _maxFoldIntrusion)
                {
                    _maxFoldIntrusion = intrusion;
                }
                float ratio = conn.RestLength > 1e-6f ? intrusion / conn.RestLength : 0f;
                spineSupportDeficitRatio = Mathf.Max(spineSupportDeficitRatio, ratio);
                deepFold |= intrusion > conn.RestLength / 3f;
            }
        }
        if (deepFold)
        {
            _foldTicks++;
        }

        if (spineSupportDeficitRatio > _maxSpineSupportDeficitRatio)
        {
            _maxSpineSupportDeficitRatio = spineSupportDeficitRatio;
        }
        _spineSupportRun = spineSupportDeficitRatio > SpineSupportViolationRatio
            ? _spineSupportRun + 1
            : 0;
        _maxSpineSupportRun = Math.Max(_maxSpineSupportRun, _spineSupportRun);

        // 角度只做诊断和事件恢复判据，不再跨品种直接判 FAIL；真正的通用硬门是上面的支柱
        // 长度违反。2 节脊柱下 SpineFollower==Hips，夹角退化无意义，保持 180°。
        float spineAngleDeg = 180f;
        if (_lizardController.SpineFollower != _lizardController.Hips)
        {
            Vector3 toHead = _lizardController.Head.Pos - _lizardController.SpineFollower.Pos;
            Vector3 toHips = _lizardController.Hips.Pos - _lizardController.SpineFollower.Pos;
            if (toHead.LengthSquared() > 1e-9f && toHips.LengthSquared() > 1e-9f)
            {
                spineAngleDeg = Mathf.RadToDeg(toHead.AngleTo(toHips));
                if (spineAngleDeg < _minSpineAngleDeg)
                {
                    _minSpineAngleDeg = spineAngleDeg;
                }
            }
        }
        _spineAngleUnder100Run = spineAngleDeg < 100f ? _spineAngleUnder100Run + 1 : 0;
        _maxSpineAngleUnder100Run = Math.Max(_maxSpineAngleUnder100Run, _spineAngleUnder100Run);

        TrackScenarioMetrics(spineAngleDeg, spineSupportDeficitRatio);

        if (_lastHeadPos != Vector3.Zero)
        {
            Vector3 step = _lizardController.Head.Pos - _lastHeadPos;
            step.Y = 0f;
            _lastHeadPlanarStep = step;
            _walkDistance += step.Length();
        }
        else
        {
            _lastHeadPlanarStep = Vector3.Zero;
        }
        _lastHeadPos = _lizardController.Head.Pos;
        _gripTickSum += _lizardController.LegsGripping;
        _minTerrainSqueeze = Mathf.Min(_minTerrainSqueeze, _lizardController.Hips.TerrainSqueeze);
        if (_lizardController.Body.EnablePostCollisionStructureRecovery)
        {
            foreach (BodyChunk c in _lizardController.Body.Chunks)
            {
                if (_rayDebug.SpherePenetration(c.Pos, c.TerrainRadius, out _, out float depth))
                {
                    _maxPostRecoveryPenetration = Mathf.Max(_maxPostRecoveryPenetration, depth);
                }
            }
        }
        if (!_lizardController.ApplyGravity)
        {
            _gravityOffTicks++;
        }
        if (_lizardController.Head.Pos.Y > _maxHeadY)
        {
            _maxHeadY = _lizardController.Head.Pos.Y;
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
        foreach (Limb l in _lizardController.Limbs)
        {
            _nonFinite |= !l.Pos.IsFinite();
        }
    }

    private void TrackScenarioMetrics(float spineAngleDeg, float spineSupportDeficitRatio)
    {
        bool spineRecovered = spineAngleDeg >= SpineRecoveredAngleDeg
            && spineSupportDeficitRatio <= SpineSupportViolationRatio;
        _spineRecoveredLastTick = spineRecovered;

        if (_regressionScenario == RegressionScenario.Turn && _turnRecoveryActive)
        {
            Vector3 forward = _lizardController.Head.Pos - _lizardController.Hips.Pos;
            forward -= _lizardController.SupportNormal * forward.Dot(_lizardController.SupportNormal);
            Vector3 desired = _lizardController.MoveDir
                - _lizardController.SupportNormal * _lizardController.MoveDir.Dot(_lizardController.SupportNormal);
            bool aligned = forward.LengthSquared() > 1e-10f && desired.LengthSquared() > 1e-10f
                && forward.Normalized().Dot(desired.Normalized()) >= 0.5f;
            bool retainedWall = !_wallTurnDrive
                || (_lizardController.SupportNormal.X >= 0.8f
                    && _lizardController.Head.Pos.X <= CornerWallX + _lizardController.Head.TerrainRadius + 0.08f);
            if (_wallTurnDrive)
            {
                _wallTurnLostSupportRun = retainedWall ? 0 : _wallTurnLostSupportRun + 1;
                _maxWallTurnLostSupportRun = Math.Max(_maxWallTurnLostSupportRun,
                    _wallTurnLostSupportRun);
            }
            _turnStableRun = spineRecovered && aligned && retainedWall ? _turnStableRun + 1 : 0;
            if (_turnStableRun >= StableRecoveryTicks)
            {
                long elapsed = _tick - _turnStartedAt - StableRecoveryTicks + 1;
                _maxTurnRecoveryTicks = Math.Max(_maxTurnRecoveryTicks, elapsed);
                _turnsRecovered++;
                _turnRecoveryActive = false;
            }
        }

        if (_regressionScenario == RegressionScenario.CarrotTurn && _carrotTurnActive)
        {
            TrackCarrotTurnMetrics(spineRecovered);
        }

        if (_regressionScenario == RegressionScenario.Tail)
        {
            bool tailDeep = false;
            foreach (ChunkConnection conn in _bodies[0].Connections)
            {
                if (conn.SoftOnly || conn.ConstraintMode != ChunkConnection.Mode.PullOnly)
                {
                    continue;
                }
                float excess = Mathf.Max(0f, (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength);
                float ratio = conn.RestLength > 1e-6f ? excess / conn.RestLength : 0f;
                tailDeep |= ratio > 1f;
            }
            if (tailDeep && !_tailDeepLastTick)
            {
                _tailSnagEpisodes++;
            }
            _tailDeepRun = tailDeep ? _tailDeepRun + 1 : 0;
            _maxTailDeepRun = Math.Max(_maxTailDeepRun, _tailDeepRun);
            _tailDeepLastTick = tailDeep;

            long releases = CountTailSnagReleases();
            if (releases > _lastTailReleaseCount)
            {
                if (_tailRecoveryActive)
                {
                    // 长尾会逐节释放；未恢复期间的新释放属于同一个脱困 episode，不能把起点
                    // 重置到最后一节而掩盖前半段耗时。保留首释放 tick，只重置稳定连跑。
                    _tailRecoveryCoalescedReleases++;
                }
                else
                {
                    _lastTailReleaseTick = _tick;
                    _tailRecoveryActive = true;
                }
                _lastTailReleaseCount = releases;
                _tailBodyStableRun = 0;
                _postTailRecoveryTicks = -1;
            }
            if (_tailRecoveryActive)
            {
                _tailBodyStableRun = spineRecovered ? _tailBodyStableRun + 1 : 0;
                if (_tailBodyStableRun >= StableRecoveryTicks)
                {
                    _postTailRecoveryTicks = _tick - _lastTailReleaseTick - StableRecoveryTicks + 1;
                    _maxPostTailRecoveryTicks = Math.Max(_maxPostTailRecoveryTicks,
                        _postTailRecoveryTicks);
                    _tailRecoveryActive = false;
                }
            }
        }

        if (_regressionScenario == RegressionScenario.Corner)
        {
            bool wallContact = false;
            foreach (BodyChunk c in _bodies[0].Chunks)
            {
                bool spineChunk = c == _lizardController.Head || c == _lizardController.SpineFollower || c == _lizardController.Hips;
                wallContact |= spineChunk && c.TerrainContact && c.ContactNormal.X >= 0.8f
                    && c.Pos.X <= CornerWallX + c.TerrainRadius + 0.05f;
            }
            if (_cornerContactTick < 0 && wallContact)
            {
                _cornerContactTick = _tick;
                _cornerStartHeadY = _lizardController.Head.Pos.Y;
            }
            if (_cornerContactTick >= 0)
            {
                if (_cornerSupportTransitionTicks < 0 && _lizardController.SupportNormal.X >= 0.8f)
                {
                    _cornerSupportTransitionTicks = _tick - _cornerContactTick;
                }
                _cornerFallbackRun = _lizardController.LastMoveTargetKind == MoveTargetKind.Fallback
                    ? _cornerFallbackRun + 1
                    : 0;
                _maxCornerFallbackRun = Math.Max(_maxCornerFallbackRun, _cornerFallbackRun);
                _cornerBendRun = spineSupportDeficitRatio > SpineSupportViolationRatio
                    ? _cornerBendRun + 1
                    : 0;
                _maxCornerBendRun = Math.Max(_maxCornerBendRun, _cornerBendRun);
                if (float.IsNaN(_cornerRise60) && _tick >= _cornerContactTick + 60)
                {
                    _cornerRise60 = _lizardController.Head.Pos.Y - _cornerStartHeadY;
                }
            }
        }
    }

    private void TrackCarrotTurnMetrics(bool spineRecovered)
    {
        bool external = _lizardController.LastMoveTargetKind == MoveTargetKind.External
            && _lizardController.MoveTarget is not null
            && !_lizardController.AtMoveTarget;
        if (!external)
        {
            _carrotTurnExternalViolations++;
        }
        if (_lizardController.TurnAssistTicks > 0)
        {
            _carrotTurnAssistViolations++;
        }

        Vector3 fullAxis = ProjectOnPlane(_lizardController.Head.Pos - _lizardController.Hips.Pos, _carrotTurnEventUp);
        Vector3 frontAxis = ProjectOnPlane(_lizardController.Head.Pos - _lizardController.SpineFollower.Pos,
            _carrotTurnEventUp);
        Vector3 middleAxis = ProjectOnPlane(_lizardController.SpineFollower.Pos - _lizardController.Hips.Pos,
            _carrotTurnEventUp);

        float frontErrorDeg = AngleDeg(frontAxis, _carrotTurnDesired);
        float middleErrorDeg = AngleDeg(middleAxis, _carrotTurnDesired);
        float frontProgressDeg = _carrotTurnInitialFrontErrorDeg - frontErrorDeg;
        float middleProgressDeg = _carrotTurnInitialMiddleErrorDeg - middleErrorDeg;
        float middleLeadDeg = middleProgressDeg - frontProgressDeg;
        _maxCarrotTurnMiddleLeadDeg = Mathf.Max(_maxCarrotTurnMiddleLeadDeg, middleLeadDeg);
        _carrotTurnMiddleLeadRun = middleLeadDeg > CarrotTurnMiddleLeadThresholdDeg
            ? _carrotTurnMiddleLeadRun + 1
            : 0;
        _maxCarrotTurnMiddleLeadRun = Math.Max(_maxCarrotTurnMiddleLeadRun,
            _carrotTurnMiddleLeadRun);

        float headResponse = (_lizardController.Head.Pos - _carrotTurnHeadStart).Dot(_carrotTurnDesired);
        float followerResponse = (_lizardController.SpineFollower.Pos - _carrotTurnFollowerStart)
            .Dot(_carrotTurnDesired);
        float followerTranslationLead = followerResponse - headResponse;
        _minCarrotTurnFollowerTranslationLead = Mathf.Min(_minCarrotTurnFollowerTranslationLead,
            followerTranslationLead);
        _maxCarrotTurnFollowerTranslationLead = Mathf.Max(_maxCarrotTurnFollowerTranslationLead,
            followerTranslationLead);

        long elapsed = _tick - _carrotTurnStartedAt;
        if (_carrotTurnMiddleCrossTick < 0 && middleAxis.LengthSquared() > 1e-10f
            && middleAxis.Normalized().Dot(_carrotTurnDesired) >= 0.5f)
        {
            _carrotTurnMiddleCrossTick = elapsed;
        }
        if (_carrotTurnFrontCrossTick < 0 && frontAxis.LengthSquared() > 1e-10f
            && frontAxis.Normalized().Dot(_carrotTurnDesired) >= 0.5f)
        {
            _carrotTurnFrontCrossTick = elapsed;
        }
        if (!_carrotTurnAxisLagRecorded
            && _carrotTurnMiddleCrossTick >= 0 && _carrotTurnFrontCrossTick >= 0)
        {
            long lag = _carrotTurnFrontCrossTick - _carrotTurnMiddleCrossTick;
            if (lag > 0)
            {
                _carrotTurnMiddleFirstEvents++;
                _maxCarrotTurnFrontLagTicks = Math.Max(_maxCarrotTurnFrontLagTicks, lag);
            }
            _carrotTurnAxisLagSamples++;
            _carrotTurnAxisLagRecorded = true;
        }

        bool headInFront = frontAxis.Dot(_carrotTurnDesired) >= _lizardController.HeadLinkLength * 0.5f;
        bool fullAxisAligned = fullAxis.LengthSquared() > 1e-10f
            && fullAxis.Normalized().Dot(_carrotTurnDesired) >= 0.5f;
        Vector3 currentStep = _lizardController.Head.Pos - _lastHeadPos;
        currentStep.Y = 0f;
        bool movingTowardTarget = currentStep.Dot(_carrotTurnDesired) >= CarrotTurnMinStep;
        _carrotTurnStableRun = external && headInFront && fullAxisAligned && spineRecovered
            && movingTowardTarget
            ? _carrotTurnStableRun + 1
            : 0;
        if (_carrotTurnStableRun >= StableRecoveryTicks)
        {
            long recoveryTicks = elapsed - StableRecoveryTicks + 1;
            _maxCarrotTurnRecoveryTicks = Math.Max(_maxCarrotTurnRecoveryTicks, recoveryTicks);
            _carrotTurnsRecovered++;
            _carrotTurnActive = false;
        }
    }

    private long CountTailSnagReleases()
    {
        long total = 0;
        foreach (ChunkConnection conn in _bodies[0].Connections)
        {
            if (!conn.SoftOnly && conn.ConstraintMode == ChunkConnection.Mode.PullOnly)
            {
                total += conn.SnagReleases;
            }
        }
        return total;
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
                 $"avgLegsGripping={(float)_gripTickSum / _tick:F2}/{_lizardController.Limbs.Count} " +
                 $"gravityOff={(float)_gravityOffTicks / _tick * 100f:F0}% maxHeadY={_maxHeadY:F2} " +
                 $"endDev={_endDevRatio:F2}x maxEndDev={_maxEndDevRatio:F2}x stretchTicks={_stretchTicks} " +
                 $"maxDeepRun={_maxDeepRun} snagReleases={_bodies[0].SnagReleases} " +
                 $"minSpineAngle={_minSpineAngleDeg:F1}deg " +
                 $"maxAngleUnder100Run={_maxSpineAngleUnder100Run} " +
                 $"maxSpineSupportDeficit={_maxSpineSupportDeficitRatio:F2}x " +
                 $"maxSpineSupportRun={_maxSpineSupportRun} " +
                 $"maxCornerStuck={_lizardController.MaxSpineCornerStuckTicks} minTerrainSqueeze={_minTerrainSqueeze:F2} " +
                 $"postRecoveryPenetration={_maxPostRecoveryPenetration:F4}m");
        if (_regressionScenario == RegressionScenario.Turn)
        {
            GD.Print($"[SCENARIO] turn observed={_turnsObserved} recovered={_turnsRecovered} " +
                     $"maxRecoveryTicks={_maxTurnRecoveryTicks} pending={_turnRecoveryActive} " +
                     $"overlap={_turnOverlapFailures} wall={_wallTurnDrive} " +
                     $"maxLostWallRun={_maxWallTurnLostSupportRun}");
        }
        else if (_regressionScenario == RegressionScenario.CarrotTurn)
        {
            GD.Print($"[SCENARIO] carrot-turn observed={_carrotTurnsObserved} " +
                     $"recovered={_carrotTurnsRecovered} maxRecoveryTicks={_maxCarrotTurnRecoveryTicks} " +
                     $"pending={_carrotTurnActive} overlap={_carrotTurnOverlapFailures} " +
                     $"axisSamples={_carrotTurnAxisLagSamples} middleFirst={_carrotTurnMiddleFirstEvents} " +
                     $"maxFrontLagTicks={_maxCarrotTurnFrontLagTicks} " +
                     $"maxMiddleLead={_maxCarrotTurnMiddleLeadDeg:F1}deg " +
                     $"maxMiddleLeadRun={_maxCarrotTurnMiddleLeadRun} " +
                     $"followerLeadRange=[{_minCarrotTurnFollowerTranslationLead:F3}," +
                     $"{_maxCarrotTurnFollowerTranslationLead:F3}]m " +
                     $"minPreStep={_minCarrotTurnPreStep:F3}m " +
                     $"minOldRemaining={_minCarrotTurnOldTargetRemaining:F2}m " +
                     $"maxActualDirDot={_maxCarrotTurnAbsDirectionDot:F3} " +
                     $"hands={_carrotTurnPositiveHandTurns}/{_carrotTurnNegativeHandTurns} " +
                     $"externalViolations={_carrotTurnExternalViolations} " +
                     $"assistViolations={_carrotTurnAssistViolations}");
        }
        else if (_regressionScenario == RegressionScenario.Tail)
        {
            GD.Print($"[SCENARIO] tail episodes={_tailSnagEpisodes} maxDeepRun={_maxTailDeepRun} " +
                     $"tailReleases={_lastTailReleaseCount} lastRecovery={_postTailRecoveryTicks} " +
                     $"maxRecovery={_maxPostTailRecoveryTicks} pending={_tailRecoveryActive} " +
                     $"coalesced={_tailRecoveryCoalescedReleases}");
        }
        else if (_regressionScenario == RegressionScenario.Corner)
        {
            GD.Print($"[SCENARIO] corner contactTick={_cornerContactTick} " +
                     $"supportTransition={_cornerSupportTransitionTicks} fallbackRun={_maxCornerFallbackRun} " +
                     $"bendRun={_maxCornerBendRun} rise60={_cornerRise60:F2}m");
        }
        Vector3 sn = _lizardController.SupportNormal;
        GD.Print($"[FINAL] controller applyGravity={_lizardController.ApplyGravity} footing={_lizardController.FootingCounter} " +
                 $"noGrip={_lizardController.NoGripCounter} stall={_lizardController.StallTicks} " +
                 $"headVel={_lizardController.Head.Vel.Length():F4} support=({sn.X:F3},{sn.Y:F3},{sn.Z:F3})");
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
        for (int i = 0; i < _lizardController.Limbs.Count; i++)
        {
            Limb l = _lizardController.Limbs[i];
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
        if (_maxSpineSupportRun > SpineSupportRunFailTicks)
        {
            reasons.Add($"防折叠支柱持续违反 >{SpineSupportViolationRatio:P0} 达 {_maxSpineSupportRun} tick" +
                        $"（>{SpineSupportRunFailTicks}）——多节脊柱局部折叠未恢复");
        }
        if (_maxPostRecoveryPenetration > 0.002f)
        {
            reasons.Add($"碰撞后结构恢复把 chunk 留在地形内 {_maxPostRecoveryPenetration:F4}m（>0.002m）");
        }
        if (_regressionScenario == RegressionScenario.Turn)
        {
            int requiredTurns = _wallTurnDrive ? 8 : 12;
            if (_turnsObserved < requiredTurns)
            {
                reasons.Add($"掉头场景只触发 {_turnsObserved} 次反转（需要 ≥{requiredTurns}，场景覆盖失效）");
            }
            if (_turnRecoveryActive || _turnsRecovered != _turnsObserved)
            {
                reasons.Add($"掉头后未恢复展开姿态（observed={_turnsObserved}, recovered={_turnsRecovered}）");
            }
            if (_maxTurnRecoveryTicks > 25)
            {
                reasons.Add($"180° 掉头恢复耗时 {_maxTurnRecoveryTicks} tick（>25）");
            }
            if (_turnOverlapFailures > 0)
            {
                reasons.Add($"上一次掉头未恢复就发生下一次反转（{_turnOverlapFailures} 次）");
            }
            if (_wallTurnDrive && _maxWallTurnLostSupportRun > 2)
            {
                reasons.Add($"墙面掉头恢复期间连续离开目标墙 {_maxWallTurnLostSupportRun} tick（>2）");
            }
        }
        else if (_regressionScenario == RegressionScenario.CarrotTurn)
        {
            const int requiredTurns = 35;
            if (_lizardController.SpineFollower == _lizardController.Hips)
            {
                reasons.Add("胡萝卜转向指标需要 spine≥3（中段与髋不能是同一 chunk）");
            }
            if (_carrotTurnsObserved < requiredTurns)
            {
                reasons.Add($"行进中胡萝卜转向只触发 {_carrotTurnsObserved} 次（需要 ≥{requiredTurns}，场景覆盖失效）");
            }
            if (_carrotTurnActive || _carrotTurnsRecovered != _carrotTurnsObserved)
            {
                reasons.Add($"胡萝卜转向后头前构型未全部恢复（observed={_carrotTurnsObserved}, " +
                            $"recovered={_carrotTurnsRecovered}, pending={_carrotTurnActive}）");
            }
            if (_maxCarrotTurnRecoveryTicks > 25)
            {
                reasons.Add($"胡萝卜 90° 转向头前构型恢复耗时 {_maxCarrotTurnRecoveryTicks} tick（>25）");
            }
            if (_carrotTurnOverlapFailures > 0)
            {
                reasons.Add($"上一次胡萝卜转向未恢复就发生下一次重规划（{_carrotTurnOverlapFailures} 次）");
            }
            if (_carrotTurnExternalViolations > 0)
            {
                reasons.Add($"胡萝卜转向恢复窗口有 {_carrotTurnExternalViolations} tick 未走 External 目标分支");
            }
            if (_minCarrotTurnPreStep < CarrotTurnMinStep - 1e-5f)
            {
                reasons.Add($"胡萝卜切向前实际前进仅 {_minCarrotTurnPreStep:F3}m/tick（<{CarrotTurnMinStep:F3}）");
            }
            if (_minCarrotTurnOldTargetRemaining < 0.8f)
            {
                reasons.Add($"胡萝卜切向时旧目标只剩 {_minCarrotTurnOldTargetRemaining:F2}m（<0.80m）" +
                            "——场景退化成到点换向");
            }
            if (_maxCarrotTurnAbsDirectionDot > 0.15f)
            {
                reasons.Add($"LizardLocomotionController 实际收到的胡萝卜转向偏离 90° " +
                            $"（max |old·new|={_maxCarrotTurnAbsDirectionDot:F3} >0.15）");
            }
            if (_carrotTurnPositiveHandTurns < 12 || _carrotTurnNegativeHandTurns < 12)
            {
                reasons.Add($"胡萝卜转向手性覆盖不足（{_carrotTurnPositiveHandTurns}/" +
                            $"{_carrotTurnNegativeHandTurns}，两侧各需 ≥12）");
            }
            if (_carrotTurnAssistViolations > 0)
            {
                reasons.Add($"90° 胡萝卜场景误触 TurnAssist {_carrotTurnAssistViolations} tick");
            }
        }
        else if (_regressionScenario == RegressionScenario.Tail)
        {
            if (_tailSnagEpisodes < 1 || _lastTailReleaseCount < 1)
            {
                reasons.Add("尾链跨墙场景没有触发深违反+释放（场景覆盖失效）");
            }
            if (_maxTailDeepRun > 50)
            {
                reasons.Add($"尾链深违反连续 {_maxTailDeepRun} tick（>50）");
            }
            if (_tailDeepLastTick)
            {
                reasons.Add("尾链场景结束时仍处于深违反（预算内未完成脱困）");
            }
            if (_tailRecoveryActive || _postTailRecoveryTicks < 0 || _maxPostTailRecoveryTicks > 40)
            {
                reasons.Add($"尾链释放后的身体恢复未全部在 40 tick 内完成（last={_postTailRecoveryTicks}, " +
                            $"max={_maxPostTailRecoveryTicks}, pending={_tailRecoveryActive}）");
            }
            if (_lizardController.Hips.TerrainSqueeze < 0.999f)
            {
                reasons.Add($"尾链恢复后 terrainSqueeze 未复原（{_lizardController.Hips.TerrainSqueeze:F2}）");
            }
        }
        else if (_regressionScenario == RegressionScenario.Corner)
        {
            if (_cornerContactTick < 0)
            {
                reasons.Add("墙角场景没有发生墙面接触（场景覆盖失效）");
            }
            if (_cornerSupportTransitionTicks < 0 || _cornerSupportTransitionTicks > 40)
            {
                reasons.Add($"墙接触后支撑系换面耗时 {_cornerSupportTransitionTicks} tick（要求 0..40）");
            }
            if (_maxCornerFallbackRun > 45)
            {
                reasons.Add($"墙角连续追逐空中 Fallback {_maxCornerFallbackRun} tick（>45）");
            }
            if (_maxCornerBendRun > 30)
            {
                reasons.Add($"墙角防折叠支柱违反连续 {_maxCornerBendRun} tick（>30）");
            }
            if (!float.IsFinite(_cornerRise60) || _cornerRise60 < 1f)
            {
                reasons.Add($"墙接触后 60 tick 头部只抬升 {_cornerRise60:F2}m（<1.00m）");
            }
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
        UpdateCameraFly((float)delta);
        _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
        _rayDebug.Draw(_camera, _lizardController);
    }

    /// <summary>右键held且不按Shift = 想要飞行摄像机（与 Shift+右键放胡萝卜互斥）。
    /// 确定性模式无交互相机，此处不判 _probe——调用侧（SampleWalkInput/UpdateCameraFly）各自把关。</summary>
    private bool WantCameraFly =>
        Input.IsMouseButtonPressed(MouseButton.Right) && !Input.IsPhysicalKeyPressed(Key.Shift);

    /// <summary>编辑器式自由摄像机：右键（不按 Shift）按住时捕获鼠标，WASD 沿视线基向量平移、
    /// E/Q 沿世界竖直轴升降（不受俯仰影响，仰头时也是垂直上升），鼠标位移（_Input 里的
    /// InputEventMouseMotion）旋转视角。纯观察工具，跑在渲染帧、用真实 delta，不进物理 tick、
    /// 不进确定性哈希——松开右键回到 LizardLocomotionController 输入与可见光标。</summary>
    private void UpdateCameraFly(float delta)
    {
        if (_probe is not null)
        {
            return;
        }
        bool flying = WantCameraFly;
        if (flying != _cameraFlying)
        {
            Input.MouseMode = flying ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            _cameraFlying = flying;
        }
        if (!flying)
        {
            return;
        }

        Basis basis = _camera.GlobalTransform.Basis;
        Vector3 dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir -= basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir += basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) dir += Vector3.Up; // 世界竖直轴，与视角俯仰无关
        if (Input.IsPhysicalKeyPressed(Key.Q)) dir -= Vector3.Up;
        if (dir != Vector3.Zero)
        {
            _camera.GlobalPosition += dir.Normalized() * CameraFlySpeed * delta;
        }
    }

    /// <summary>F3：开关射线+推进目标（胡萝卜）可视化（只影响绘制）。数字键 1~9：现场换品种重生
    /// （交互模式限定，与左上角下拉面板走同一个 SelectBreed 入口，互相同步）。
    /// 鼠标移动：飞行摄像机态下累加偏航/俯仰旋转相机（俯仰钳制防止翻过头顶）。</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            if (WantCameraFly)
            {
                _camYaw -= motion.Relative.X * CameraMouseSensitivity;
                _camPitch = Mathf.Clamp(_camPitch - motion.Relative.Y * CameraMouseSensitivity,
                    -1.5f, 1.5f);
                _camera.Rotation = new Vector3(_camPitch, _camYaw, 0f);
            }
            return;
        }
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
        SelectBreed((int)(key.PhysicalKeycode - Key.Key1));
    }

    /// <summary>数字键与下拉面板共用的换品种入口，保证两个输入源互相同步。</summary>
    private void SelectBreed(int index)
    {
        BreedParams[] breeds = BodyFactory.AllBreeds();
        if (index < 0 || index >= breeds.Length)
        {
            return;
        }
        // 在原地上方重生：旧身体整体替换（物理与渲染都换新），品种对比不用重启场景。
        SpawnLizard(breeds[index], _lizardController.Hips.Pos + Vector3.Up * 0.5f);
        _breedUI?.SyncSelection(index);
        GD.Print($"[SANDBOX] breed -> {breeds[index].Name}");
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
                    _determinismTicks = ticks;
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
                else if (arg == "--route=turn")
                {
                    _waypoints = TurnRoute;
                    _regressionScenario = RegressionScenario.Turn;
                }
                else if (arg == "--route=wall-turn")
                {
                    _waypoints = StandRoute;
                    _regressionScenario = RegressionScenario.Turn;
                    _wallTurnDrive = true;
                }
                else if (arg == "--route=wall-tail")
                {
                    _waypoints = WallRoute;
                    _regressionScenario = RegressionScenario.Tail;
                }
                else if (arg == "--route=wall-corner")
                {
                    _waypoints = WallRoute;
                    _regressionScenario = RegressionScenario.Corner;
                }
                else if (arg == "--route=stand")
                {
                    _waypoints = StandRoute;
                }
                else if (arg == "--route=carrot")
                {
                    _waypoints = CarrotRoute;
                    _carrotDrive = true;
                }
                else if (arg == "--route=carrot-turn")
                {
                    _waypoints = StandRoute;
                    _regressionScenario = RegressionScenario.CarrotTurn;
                    _carrotTurnDrive = true;
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
