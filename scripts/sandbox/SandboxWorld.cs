using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species;
using ProcAnim.Core.Species.Centipede;
using ProcAnim.Core.Species.Humanoid;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.Vulture;
using ProcAnim.Core.Terrain;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒场景根节点：在 _PhysicsProcess（固定 40 tick/s）驱动物理内核，
/// 在 _Process 里按物理插值分数渲染。逻辑一律不读 delta——步长恒为 1 tick，
/// 内核里所有速度/力的单位都是「米/tick」（确定性来源，也与雨世界参数表同构）。
///
/// 场景可装配并列的 LizardLocomotionController 或 CentipedeLocomotionController。
/// WASD 给移动意图（世界 XZ 轴），Shift+右键直喂目标，左键拖拽任意 chunk；
/// 蜈蚣用 R 在两端间切换宿主指定的领航端。
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
    private const string CommandLineHelp =
        "[SANDBOX] usage: --determinism=N [--tps=N] " +
        "[--breed=default|heavy|sprinter|hexapod|vulture|king|swift|quad | " +
        "--creature=centipede/short|long|armored|ribbon [--lead=start|end] | " +
        "--species=humanoid [--breed=scavenger|brute|waif] [--stun=T,D]] " +
        "[--route=...|fly|perch|centipede-step-down|centipede-narrow-wall|hwalk|hact] " +
        "[--spawn=x,y,z] [--perturb=x] " +
        "[--yank=T] [--expect-hash=X16]\n" +
        "[SANDBOX] interactive: 1–4 lizards, 5–8 centipedes, 9/0/-/= vultures, " +
        "humanoids via dropdown (P point / C carry / T throw), " +
        "R swaps centipede lead, F3 debug";

    private float _perturb; // --perturb=x 灵敏度自检：初始位置微扰 → 哈希必须变
    private Vector3 _spawn = new(0f, 2f, 0f); // --spawn=x,y,z 覆盖出生点（坡上/墙边测试用）
    private string? _centipedeId; // --creature=centipede/...；null 保持既有 --breed 蜥蜴路径
    private bool _breedExplicit; // --breed 与 --creature 是互斥的装配选择器，防矩阵误跑
    private CentipedeLeadEnd _requestedCentipedeLeadEnd = CentipedeLeadEnd.Start;
    private bool _leadExplicit;
    private int _scriptedCentipedeLeadConfirmTicks;
    private bool _showCommandHelp;
    private long _yankTick = -1; // --yank=T：T tick 调当前控制器 Launch（「拎起再摔」+击飞 API 覆盖）
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
    private const float CentipedeNarrowWallFarFaceX = -6.2f;

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

    /// <summary>--route=fly：秃鹫 3D 巡航环线（喂 MoveTarget）。绕 x=-6 的 3m 薄墙一个往返：
    /// 前场低飞 → 拉高 → **高位横穿墙顶** → 墙后垂直降 → 原路爬回 → 越墙返场。
    /// 直喂契约要求喂点间无阻隔可达——且要给真实轨迹留垂度余量：下降腿会触发滑翔
    /// 相位冻结，实际轨迹比直线下垂 ~1m（第一版斜穿墙顶的路线就是这么撞在墙面上的），
    /// 所以横穿腿走 y=5.5→4.5 的高位，降到低点的腿放在墙后 x=-7.5 纯竖直进行。</summary>
    /// 另一课（实测）：巡航路点必须离地形 ≥ 降落贴地探测深度（1.2m）——第一版把
    /// 前场路点放在缓坡面上方 0.54m，控制器如实判定「目标贴地」并整套降落栖息了。
    private static readonly Vector3[] VultureFlyRoute =
    {
        new(4f, 2.8f, 0f),
        new(-4f, 5.5f, 0f),
        new(-7.5f, 4.5f, 0f),
        new(-7.5f, 2f, 0f),
        new(-7.5f, 4.5f, 0f),
        new(-4f, 5.5f, 0f),
    };

    /// <summary>--route=perch：两个空中路点后喂地面目标——降落从「落点贴地探测 + 进入
    /// 触发半径」涌现（翅膀转 Grab、吸附栖息），[RESULT] 断言真的落了地。</summary>
    private static readonly Vector3[] VulturePerchRoute =
    {
        new(4f, 2f, 0f),
        new(-4f, 5.5f, 0f),
    };

    private static readonly Vector3 VulturePerchTarget = new(2f, 0f, 2f);

    private Vector3[] _waypoints = DefaultRoute;
    private int _waypointIndex;
    private int _waypointsReached;
    private bool _carrotDrive; // --route=carrot：路点经 MoveTarget 直喂（否则 MoveDir 方向驱动）
    private bool _carrotTurnDrive;
    private bool _vultureRouteSelected; // --route=fly/perch（秃鹫专属路线）
    private bool _vulturePerchDrive;    // --route=perch：路点跑完后喂地面目标降落
    private bool _centipedeCourseDrive;
    private bool _centipedeStepDownDrive;
    private bool _centipedeNarrowWallDrive;
    private int _centipedeNarrowWallSettledTicks;
    private string _routeName = "default";

    private enum CentipedeCourseDrivePhase
    {
        AcrossFloorAndTop,
        DownOuterWall,
        AlongCeiling,
    }

    private enum CentipedeCourseStage
    {
        Floor,
        Slope,
        InnerWall,
        Top,
        OuterWall,
        Ceiling,
        Count,
    }

    private static readonly string[] CentipedeCourseStageNames =
    {
        "floor", "slope", "inner-wall", "top", "outer-wall", "ceiling",
    };

    // --route=centipede-step-down：z=-8 的专用平台保留旧 Step 的箱体外角语义，但足够长，
    // 可让 armored 全身先在顶面展开。固定 Start 领航并持续 +X，专测水平输入跨越外角时
    // 能否保留向下切向，随后让尾端完整落到地板；不靠宿主在换面后补 Vector3.Down。
    private const float CentipedeStepDownEdgeX = 2f;
    private const float CentipedeStepDownTopY = 0.8f;
    private const float CentipedeStepDownLandingY = 0.40f;
    private const float CentipedeStepDownMinimumProgress = 2.5f;
    private const float CentipedeStepDownSevereOverlapRatio = 0.55f;
    private const float CentipedeStepDownFinalSeparationRatio = 0.75f;
    private const int CentipedeStepDownPileRunBudget = 8;

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
    private CentipedeLocomotionController? _centipedeController;
    private ISandboxCreatureAdapter _creature = null!;
    private BreedParams _breed = BodyFactory.Default();
    private VultureFlightController? _vultureController; // 非 null = 当前生物是秃鹫
    private VultureBreedParams? _vultureBreed;           // --breed= 落在秃鹫表 → 秃鹫模式

    // —— 人形物种（--species=humanoid / 下拉框换品种）：驱动/指标/渲染全部收在 driver 里，
    // 本类只做物种分流的早退分支——蜥蜴代码路径一字不动（既有哈希基线不受牵连）。 ——
    private HumanoidSandboxDriver? _humanoid;
    private bool _speciesHumanoid;
    private string? _breedName; // --breed= 原始名（物种确定后再各自解析）
    private Vector3[] _humanoidWaypoints = System.Array.Empty<Vector3>();
    private bool _humanoidAct;
    private long _stunTick = -1;
    private int _stunDuration;
    private bool _lizardRouteSet; // 蜥蜴专属路线开关被使用（与 --species=humanoid 互斥校验）
    private readonly RaycastTerrainQuery _terrain = new();
    private RayDebugDraw _rayDebug = null!;
    private readonly BodyRenderer _renderer = new();
    // —— 正式（美化）渲染层：与 debug 白盒并存，V 键切换；未覆盖物种自动回落白盒。——
    private ProcAnimLab.Render.IFormalRenderer? _formalRenderer;
    private bool _formalView = true; // --formal=off 或 V 键关闭
    private string? _screenshotPath; // --screenshot=path[@tick]：截图后退出（视觉验证回路）
    private long _screenshotTick = 90;
    private Vector3? _autoWalkDir; // --autowalk=dx,dz：交互模式恒定行走（配截图用）
    private (Vector3 Pos, Vector3 LookAt)? _camOverride; // --cam=px,py,pz,lx,ly,lz
    private Vector3? _camFollowOffset; // --camfollow=ox,oy,oz：相机 = 生物头 + 偏移，注视头
    private readonly DragController _drag = new();
    private CreatureSelectorUI? _creatureUI;
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
        if (_showCommandHelp)
        {
            _fatal = true;
            GD.Print(CommandLineHelp);
            GetTree().Quit();
            return;
        }
        _rayDebug = new RayDebugDraw(_terrain);
        _rayDebug.Build(this);
        if (_speciesHumanoid)
        {
            SpawnHumanoid(BodyFactory.HumanoidByName(_breedName ?? "scavenger"), _spawn);
        }
        else if (_vultureBreed is { } vultureBreed)
        {
            SpawnVulture(vultureBreed, _spawn);
        }
        else if (_centipedeId is null)
        {
            SpawnLizard(_breed, _spawn);
        }
        else
        {
            SpawnCentipede(_centipedeId, _spawn);
        }
        if (_perturb != 0f)
        {
            // 微扰打在活动身体的 chunk 0 上（蜥蜴=头 / 人形=胸），物种各自等价。
            _creature.Body.Chunks[0].Pos += new Vector3(_perturb, 0f, 0f);
            _creature.Body.Chunks[0].LastPos = _creature.Body.Chunks[0].Pos;
        }
        if (_camOverride is { } cam)
        {
            _camera.Position = cam.Pos;
            _camera.LookAt(cam.LookAt, Vector3.Up);
            _camYaw = _camera.Rotation.Y;
            _camPitch = _camera.Rotation.X;
        }
        if (_probe is null)
        {
            // 确定性回归模式禁交互输入（含生物切换），选择面板只在交互模式下建。
            // 生物表 = 蜥蜴四 + 蜈蚣四 + 秃鹫四（数字行 12 键）+ 人形三（数字行已满，仅下拉框可选）。
            BreedParams[] breeds = BodyFactory.AllBreeds();
            CentipedeParams[] centipedes = CentipedeFactory.AllPresets();
            VultureBreedParams[] vultures = BodyFactory.AllVultureBreeds();
            HumanoidParams[] humanoids = BodyFactory.AllHumanoids();
            var names = new string[breeds.Length + centipedes.Length + vultures.Length + humanoids.Length];
            for (int i = 0; i < breeds.Length; i++)
            {
                names[i] = $"lizard/{breeds[i].Name}";
            }
            for (int i = 0; i < centipedes.Length; i++)
            {
                names[breeds.Length + i] = centipedes[i].StableId;
            }
            for (int i = 0; i < vultures.Length; i++)
            {
                names[breeds.Length + centipedes.Length + i] = $"vulture/{vultures[i].Name}";
            }
            for (int i = 0; i < humanoids.Length; i++)
            {
                names[breeds.Length + centipedes.Length + vultures.Length + i] = $"humanoid/{humanoids[i].Name}";
            }
            _creatureUI = new CreatureSelectorUI();
            _creatureUI.Build(this, names, SelectCreature);
            int selected = _speciesHumanoid
                ? breeds.Length + centipedes.Length + vultures.Length
                    + Array.FindIndex(humanoids, h => h.Name == _humanoid!.Breed.Name)
                : _vultureBreed is { } vb
                    ? breeds.Length + centipedes.Length + Array.FindIndex(vultures, v => v.Name == vb.Name)
                    : _centipedeId is null
                        ? Array.FindIndex(breeds, b => b.Name == _breed.Name)
                        : breeds.Length + Array.FindIndex(centipedes, p => p.StableId == _centipedeId);
            _creatureUI.SyncSelection(selected);
            SyncLeadEndUI();
        }
        GD.Print($"[SANDBOX] ready, tps={Engine.PhysicsTicksPerSecond}, creature={_creature.StableId}, " +
                 $"determinism={(_probe is not null ? "on" : "off")}" +
                 (_centipedeController is null
                     ? string.Empty
                     : $", requestedLead={_centipedeController.RequestedLeadEnd}"));
    }

    /// <summary>（重）生成人形：驱动器负责物理/渲染重建（下拉框换品种共用此路径）。</summary>
    private void SpawnHumanoid(HumanoidParams breed, Vector3 origin)
    {
        _centipedeId = null;
        _centipedeController = null;
        _lizardController = null!;
        _vultureController = null;
        _vultureBreed = null;
        _humanoid ??= new HumanoidSandboxDriver
        {
            Waypoints = _humanoidWaypoints,
            ActScript = _humanoidAct,
            StunTick = _stunTick,
            StunDuration = _stunDuration,
        };
        _renderer.Clear(); // 清掉蜥蜴/蜈蚣/秃鹫的 BodyRenderer 残留（若刚跨物种切换过来）
        _drag.Release();
        _humanoid.Spawn(this, breed, origin, ConstraintIterations, _bodies);
        _creature = new HumanoidSandboxCreatureAdapter(_humanoid);
        RebuildFormalRenderer();
    }

    /// <summary>跨物种切换离开人形时的整体拆除：专用渲染节点与驱动器一起清。</summary>
    private void ClearHumanoid()
    {
        if (_humanoid is null)
        {
            return;
        }
        _humanoid.Renderer.Clear();
        _humanoid = null;
    }

    /// <summary>重建正式渲染器（物种/品种切换共用）：未覆盖物种 TryCreate 返回 null → 回落白盒。</summary>
    private void RebuildFormalRenderer()
    {
        _formalRenderer?.Clear();
        _formalRenderer = ProcAnimLab.Render.FormalRendererFactory.TryCreate(_creature);
        _formalRenderer?.Build(this);
        ApplyRenderView();
    }

    /// <summary>正式/白盒双渲染的显隐仲裁：正式渲染就绪且开启时白盒整体隐藏（Draw 也跳过）。
    /// 人形的白盒是独立 HumanoidRenderer，一并仲裁。</summary>
    private void ApplyRenderView()
    {
        bool formalOn = _formalRenderer is not null && _formalView;
        _renderer.SetVisible(!formalOn);
        _humanoid?.Renderer.SetVisible(!formalOn);
        _formalRenderer?.SetVisible(formalOn);
    }

    /// <summary>（重）生成行走体：替换物理对象并重建渲染节点（数字键换品种共用此路径）。</summary>
    private void SpawnLizard(BreedParams breed, Vector3 origin)
    {
        _centipedeId = null;
        _centipedeController = null;
        _breed = breed;
        _vultureController = null;
        _vultureBreed = null;
        ClearHumanoid();
        _lizardController = BodyFactory.CreateLizardController(origin, breed);
        _creature = new LizardSandboxCreatureAdapter(_lizardController, breed.Name);
        _lizardController.Body.ConstraintIterations = ConstraintIterations;
        _bodies.Clear();
        _bodies.Add(_lizardController.Body);
        _drag.Release();
        _renderer.Clear();
        _creature.BuildRenderer(_renderer, this);
        RebuildFormalRenderer();
    }

    /// <summary>按稳定 ID 装配蜈蚣；未知 ID 已在 CLI/选择表边界快速失败。</summary>
    private void SpawnCentipede(string stableId, Vector3 origin)
    {
        _centipedeId = stableId;
        _lizardController = null!;
        _vultureController = null;
        _vultureBreed = null;
        ClearHumanoid();
        _centipedeController = CentipedeFactory.CreateController(origin, stableId);
        _centipedeController.RequestedLeadEnd = _requestedCentipedeLeadEnd;
        _scriptedCentipedeLeadConfirmTicks = 0;
        // Inspector 的历史默认值 3 属于短链蜥蜴；不能反向降低蜈蚣工厂为长链收敛设定的下限。
        _centipedeController.Body.ConstraintIterations = Math.Max(
            ConstraintIterations, _centipedeController.Body.ConstraintIterations);
        _creature = new CentipedeSandboxCreatureAdapter(_centipedeController, stableId);
        ResetCentipedeCourseMetrics(_centipedeController);
        _bodies.Clear();
        _bodies.Add(_centipedeController.Body);
        _drag.Release();
        _renderer.Clear();
        _creature.BuildRenderer(_renderer, this);
        RebuildFormalRenderer();
    }

    private void ResetCentipedeCourseMetrics(CentipedeLocomotionController controller)
    {
        _centipedeCoursePhase = CentipedeCourseDrivePhase.AcrossFloorAndTop;
        _centipedeCourseLeadTicks = new long[(int)CentipedeCourseStage.Count];
        _centipedeCourseTailTicks = new long[(int)CentipedeCourseStage.Count];
        Array.Fill(_centipedeCourseLeadTicks, -1L);
        Array.Fill(_centipedeCourseTailTicks, -1L);
        _centipedeCourseConnectionRuns = new int[controller.Body.Connections.Count];
        _centipedeCourseConnectionMaxRuns = new int[controller.Body.Connections.Count];
        _centipedeConnectionRuns = new int[controller.Body.Connections.Count];
        _centipedeCourseNoneRun = 0;
        _centipedeCourseMaxNoneRun = 0;
        _centipedeCourseBlockedRun = 0;
        _centipedeCourseMaxBlockedRun = 0;
        _centipedeStepDownStartCenterX = float.NaN;
        _centipedeStepDownNetProgress = 0f;
        _centipedeStepDownLeadTick = -1;
        _centipedeStepDownTailTick = -1;
        _centipedeStepDownLeadWallTick = -1;
        _centipedeStepDownTailWallTick = -1;
        _centipedeStepDownLeadWallTicks = 0;
        _centipedeStepDownTailWallTicks = 0;
        _centipedeStepDownMinSeparationRatio = float.PositiveInfinity;
        _centipedeStepDownFinalSeparationRatio = float.PositiveInfinity;
        _centipedeStepDownPileRun = 0;
        _centipedeStepDownMaxPileRun = 0;
        _centipedeStepDownLeadChanged = false;
    }

    /// <summary>（重）生成秃鹫：与 SpawnLizard 平行的装配路径（渲染带翅链与羽毛线扇）。</summary>
    private void SpawnVulture(VultureBreedParams breed, Vector3 origin)
    {
        _centipedeId = null;
        _centipedeController = null;
        _lizardController = null!;
        ClearHumanoid();
        _vultureBreed = breed;
        VultureFlightController controller = BodyFactory.CreateVultureController(origin, breed);
        _vultureController = controller;
        controller.Body.ConstraintIterations = ConstraintIterations;
        _creature = new VultureSandboxCreatureAdapter(controller, breed);
        _bodies.Clear();
        _bodies.Add(controller.Body);
        _drag.Release();
        _renderer.Clear();
        _creature.BuildRenderer(_renderer, this);
        RebuildFormalRenderer();
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

        if (_humanoid is not null)
        {
            HumanoidPhysicsTick();
            return;
        }

        if (_vultureController is { } vulture)
        {
            // 秃鹫平行主循环：输入/巡航 → Tick → 指标/探针。蜥蜴路径零改动。
            if (_probe is null)
            {
                _drag.SampleInput(_camera, _bodies);
                _drag.ApplyDragForce();
                SampleVultureInput();
            }
            else
            {
                SteerVultureRoute();
            }
            if (_yankTick >= 0 && _tick == _yankTick)
            {
                vulture.Launch(new Vector3(0.1f, 0.4f, 0.15f));
            }
            var vctx = new TickContext(_gravityPerTick, _rayDebug, _tick);
            vulture.Tick(vctx);
            if (_probe is not null)
            {
                TrackVultureMetrics();
                _probe.Record(_tick, _bodies, null, vulture.Wings);
                if (_probe.Finished)
                {
                    GetTree().Quit(DumpVultureFinalState());
                }
            }
            return;
        }

        if (_probe is null)
        {
            _drag.SampleInput(_camera, _bodies);
            _drag.ApplyDragForce();
            SampleWalkInput();
        }
        else
        {
            if (_centipedeController is null)
            {
                SteerAlongWaypoints();
            }
            else
            {
                SteerCentipedeAlongWaypoints();
            }
        }

        if (_yankTick >= 0 && _tick == _yankTick)
        {
            // 走正式击飞 API（曾直接抠 Head.Vel 五个 tick——Launch 因此零回归覆盖，终审 C11）。
            _creature.Launch(new Vector3(0.1f, 0.4f, 0.15f));
        }

        // 地形查询经 _rayDebug 转发（纯观测装饰器）：F3 可视化打出的所有射线。
        var ctx = new TickContext(_gravityPerTick, _rayDebug, _tick);
        _creature.Tick(ctx);

        if (_probe is not null)
        {
            if (_centipedeController is null)
            {
                TrackQualityMetrics();
                _probe.Record(_tick, _bodies, _lizardController.Limbs);
            }
            else
            {
                TrackCentipedeQualityMetrics();
                _probe.Record(_tick, _bodies, _creature);
            }
            if (_probe.Finished)
            {
                GetTree().Quit(DumpFinalState());
            }
        }
    }

    /// <summary>人形物种的固定步长分支（蜥蜴路径的物种并列版；驱动细节全在 driver 里）。</summary>
    private void HumanoidPhysicsTick()
    {
        HumanoidSandboxDriver driver = _humanoid!;
        if (_probe is null)
        {
            _drag.SampleInput(_camera, _bodies);
            _drag.ApplyDragForce();
            if (_autoWalkDir is { } autoDir)
            {
                // --autowalk：截图/视觉验证的恒定行走，人形分支与蜥蜴路径同语义。
                driver.Controller.MoveTarget = null;
                driver.Controller.MoveDir = autoDir;
                driver.Controller.RunSpeed = 1f;
            }
            else
            {
                driver.SampleWalkInput(_camera, _rayDebug, WantCameraFly);
            }
        }
        else
        {
            driver.SteerScripted(_tick);
        }

        if (_yankTick >= 0 && _tick == _yankTick)
        {
            driver.Controller.Launch(new Vector3(0.1f, 0.4f, 0.15f));
            driver.NotifyLaunch(_tick);
        }

        var ctx = new TickContext(_gravityPerTick, _rayDebug, _tick);
        driver.Controller.Tick(ctx);
        driver.PostTick(_tick, _gravityPerTick);

        if (_probe is not null)
        {
            _probe.Record(_tick, _bodies, driver.Controller.Legs, arms: driver.Controller.Arms);
            if (_probe.Finished)
            {
                GetTree().Quit(driver.DumpFinalState(_probe, _expectHash, _tick));
            }
        }
    }

    /// <summary>WASD → 世界 XZ 移动意图（相机固定朝 -Z，W 即「向屏幕里」）；
    /// Shift+右键点地形 → MoveTarget 直喂（路线2 手测通路：命中点就是喂点，胡萝卜画紫色），
    /// 到点自动清除。WASD 一按立即接管（清 MoveTarget 回方向驱动）。
    /// 右键（不按 Shift）= 自由摄像机飞行态：WASD 让位给 UpdateCameraFly，本函数直接短路。</summary>
    private void SampleWalkInput()
    {
        if (_autoWalkDir is { } autoDir)
        {
            // --autowalk：截图/视觉验证用的恒定行走，优先于键鼠。
            _creature.MoveTarget = null;
            _creature.MoveDir = autoDir;
            _creature.RunSpeed = 1f;
            return;
        }
        if (WantCameraFly)
        {
            _creature.MoveDir = Vector3.Zero;
            _creature.RunSpeed = 0f;
            return;
        }

        Vector3 dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir.X += 1f;

        if (dir != Vector3.Zero)
        {
            _creature.MoveTarget = null;
            _creature.MoveDir = dir.Normalized();
            _creature.RunSpeed = 1f;
            return;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Right) && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            Vector2 mouse = _camera.GetViewport().GetMousePosition();
            Vector3 origin = _camera.ProjectRayOrigin(mouse);
            Vector3 rayDir = _camera.ProjectRayNormal(mouse);
            if (_rayDebug.Raycast(origin, origin + rayDir * 100f, out TerrainHit hit))
            {
                _creature.MoveTarget = hit.Point;
            }
        }

        if (_creature.MoveTarget is not null)
        {
            if (_creature.AtMoveTarget)
            {
                _creature.MoveTarget = null;
                _creature.RunSpeed = 0f;
                _creature.MoveDir = Vector3.Zero;
                return;
            }
            _creature.RunSpeed = 1f;
            return;
        }

        _creature.MoveDir = Vector3.Zero;
        _creature.RunSpeed = 0f;
    }

    /// <summary>秃鹫交互输入：WASD 世界 XZ + Space 升 / C 降（3D 意图，与蜥蜴的平面意图
    /// 不同）；Shift+右键点地形 → MoveTarget 直喂（飞过去，落点贴地则自动降落栖息）——
    /// 到点不清目标：保持喂点即悬停锚定（station-keeping）。WASD/升降一按立即接管。</summary>
    private void SampleVultureInput()
    {
        VultureFlightController c = _vultureController!;
        if (WantCameraFly)
        {
            c.MoveDir = Vector3.Zero;
            c.RunSpeed = 0f;
            return;
        }

        Vector3 dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Space)) dir.Y += 1f;
        if (Input.IsPhysicalKeyPressed(Key.C)) dir.Y -= 1f;

        if (dir != Vector3.Zero)
        {
            c.MoveTarget = null;
            c.MoveDir = dir.Normalized();
            c.RunSpeed = 1f;
            return;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Right) && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            Vector2 mouse = _camera.GetViewport().GetMousePosition();
            Vector3 origin = _camera.ProjectRayOrigin(mouse);
            Vector3 rayDir = _camera.ProjectRayNormal(mouse);
            if (_rayDebug.Raycast(origin, origin + rayDir * 100f, out TerrainHit hit))
            {
                c.MoveTarget = hit.Point;
            }
        }

        c.MoveDir = Vector3.Zero;
        c.RunSpeed = c.MoveTarget is not null ? 1f : 0f;
    }

    /// <summary>秃鹫确定性巡航：3D 路点直喂（MoveTarget 通路是秃鹫的原生输入形态）。
    /// fly 路线循环绕圈；perch 路线跑完路点后喂地面目标，降落自然涌现并记录 landedTick。</summary>
    private void SteerVultureRoute()
    {
        VultureFlightController c = _vultureController!;
        if (_waypoints.Length == 0)
        {
            c.MoveTarget = null;
            c.RunSpeed = 0f;
            return;
        }
        c.RunSpeed = 1f;
        if (_vulturePerchDrive && _waypointIndex >= _waypoints.Length)
        {
            c.MoveTarget = VulturePerchTarget;
            if (_vultureLandedTick < 0 && c.AnyWingAttached && !c.AirBorne)
            {
                _vultureLandedTick = _tick;
            }
            return;
        }
        c.MoveTarget = _waypoints[_waypointIndex];
        if (c.AtMoveTarget)
        {
            _waypointsReached++;
            _waypointIndex++;
            if (!_vulturePerchDrive)
            {
                _waypointIndex %= _waypoints.Length;
            }
            if (_waypointIndex < _waypoints.Length)
            {
                c.MoveTarget = _waypoints[_waypointIndex];
            }
        }
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

    /// <summary>
    /// 蜈蚣确定性宿主路线。路点和到达计数沿用既有场景数据，但不进入任何蜥蜴专属
    /// 转身/脊柱恢复逻辑。无头 default 巡逻用本宿主自己的端点评分/去抖策略明确写入
    /// RequestedLeadEnd；交互模式与传入 --lead 的回归都不会启用该策略。
    /// </summary>
    private void SteerCentipedeAlongWaypoints()
    {
        if (_centipedeNarrowWallDrive)
        {
            SteerCentipedeNarrowWall();
            return;
        }
        if (_centipedeStepDownDrive)
        {
            SteerCentipedeStepDown();
            return;
        }
        if (_centipedeCourseDrive)
        {
            SteerCentipedeCourse();
            return;
        }
        if (_waypoints.Length == 0)
        {
            _creature.MoveTarget = null;
            _creature.MoveDir = Vector3.Zero;
            _creature.RunSpeed = 0f;
            return;
        }

        if (_carrotDrive)
        {
            if (_creature.AtMoveTarget)
            {
                _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
                _waypointsReached++;
            }
            Vector3 wp = _waypoints[_waypointIndex];
            Vector3 fed = wp;
            if (_rayDebug.Raycast(wp + Vector3.Up * 3f, wp + Vector3.Down, out TerrainHit hit))
            {
                fed = hit.Point;
            }
            _creature.MoveTarget = fed;
            _creature.RunSpeed = 1f;
            return;
        }

        _creature.MoveTarget = null;
        Vector3 target = _waypoints[_waypointIndex];
        Vector3 toTarget = target - _creature.LeadChunk.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.4f)
        {
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _waypointsReached++;
            target = _waypoints[_waypointIndex];
            toTarget = target - _creature.LeadChunk.Pos;
            toTarget.Y = 0f;
        }
        _creature.MoveDir = toTarget.LengthSquared() < 1e-12f
            ? Vector3.Zero
            : toTarget.Normalized();
        _creature.RunSpeed = 1f;
        ApplyScriptedCentipedeLeadPolicy();
    }

    /// <summary>
    /// 固定 End 端向前翻越旧 0.4m 墙一次，随后停驶。固定收敛窗口避免把某个运动相位
    /// 恰巧采为终态，专项同时受通用 10% 连接偏差和 2mm 穿透门约束。
    /// </summary>
    private void SteerCentipedeNarrowWall()
    {
        _creature.MoveTarget = null;
        if (_waypointsReached >= 1)
        {
            _creature.MoveDir = Vector3.Zero;
            _creature.RunSpeed = 0f;
            _centipedeNarrowWallSettledTicks++;
            return;
        }

        Vector3 target = _waypoints[_waypointIndex];
        Vector3 toTarget = target - _creature.LeadChunk.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.4f)
        {
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _waypointsReached++;
            target = _waypoints[_waypointIndex];
            toTarget = target - _creature.LeadChunk.Pos;
            toTarget.Y = 0f;
        }
        _creature.MoveDir = toTarget.LengthSquared() < 1e-12f
            ? Vector3.Zero
            : toTarget.Normalized();
        _creature.RunSpeed = 1f;
    }

    /// <summary>
    /// 固定 Start 端、恒定水平输入穿过平台外角。这里故意不根据支撑法线切换成 Down：
    /// 下降切向必须由控制器对上一段表面轨迹做平行运输，而不是由测试宿主演出答案。
    /// 尾端抵达下层地板后停驶，留下充足预算观察身体是否重新展开。
    /// </summary>
    private void SteerCentipedeStepDown()
    {
        CentipedeLocomotionController controller = _centipedeController!;
        controller.MoveTarget = null;
        controller.MoveDir = Vector3.Right;
        controller.RunSpeed = _centipedeStepDownTailTick >= 0 ? 0f : 1f;
    }

    /// <summary>
    /// 仅属于无头巡逻脚本的示例上层策略。它可以看路线方向并显式写 RequestedLeadEnd；
    /// 控制器本身不再看 MoveDir/MoveTarget 做头尾决策。--lead 会完全关闭此策略。
    /// </summary>
    private void ApplyScriptedCentipedeLeadPolicy()
    {
        if (_leadExplicit || !ReferenceEquals(_waypoints, DefaultRoute))
        {
            return;
        }
        CentipedeLocomotionController controller = _centipedeController!;
        Vector3 intent = _creature.MoveDir;
        if (intent.LengthSquared() < 1e-10f)
        {
            _scriptedCentipedeLeadConfirmTicks = 0;
            return;
        }
        intent = intent.Normalized();
        Vector3 startOut = controller.Segments[0].Chunk.Pos
            - controller.Segments[1].Chunk.Pos;
        startOut = startOut.LengthSquared() > 1e-10f ? startOut.Normalized() : intent;
        Vector3 endOut = controller.Segments[^1].Chunk.Pos
            - controller.Segments[^2].Chunk.Pos;
        endOut = endOut.LengthSquared() > 1e-10f ? endOut.Normalized() : intent;
        float startScore = intent.Dot(startOut);
        float endScore = intent.Dot(endOut);
        CentipedeLeadEnd preferred = startScore >= endScore
            ? CentipedeLeadEnd.Start : CentipedeLeadEnd.End;
        if (preferred == controller.LeadEnd || Mathf.Abs(startScore - endScore) <= 0.2f)
        {
            _scriptedCentipedeLeadConfirmTicks = 0;
            return;
        }
        if (++_scriptedCentipedeLeadConfirmTicks >= 3)
        {
            _requestedCentipedeLeadEnd = preferred;
            controller.RequestedLeadEnd = preferred;
            _scriptedCentipedeLeadConfirmTicks = 0;
        }
    }

    /// <summary>
    /// z=20 专用课程的宿主输入。Right 在地面/斜坡/内角墙上分别自然投影为前进/前进/
    /// 世界向上；领端真正取得外墙 +X 法线后才切 Down，取得梁底 -Y 法线后切 Left。
    /// 不用位置计时器替代接触证据，避免控制器未换面时宿主仍把路线“演完”。
    /// </summary>
    private void SteerCentipedeCourse()
    {
        CentipedeLocomotionController controller = _centipedeController!;
        bool completed = _centipedeCourseTailTicks.Length > (int)CentipedeCourseStage.Ceiling
            && _centipedeCourseTailTicks[(int)CentipedeCourseStage.Ceiling] >= 0;
        if (completed)
        {
            // 六阶段与尾随预算已经完成；有限梁底到此即为课程终点。保留 Left 朝向但停驶，
            // 让任意较长探针预算都观测同一个稳定终态，而不是把驶出白盒末端后的自由落体
            // 误算成换面/约束回归。
            controller.MoveTarget = null;
            controller.MoveDir = Vector3.Left;
            controller.RunSpeed = 0f;
            return;
        }
        CentipedeSegment lead = controller.RequestedLeadEnd == CentipedeLeadEnd.Start
            ? controller.Segments[0] : controller.Segments[^1];
        Vector3 normal = lead.SupportNormal;
        if (_centipedeCoursePhase == CentipedeCourseDrivePhase.AcrossFloorAndTop
            && lead.SupportConfidence > 0.15f
            && lead.Chunk.Pos.X > 12.1f && normal.X > 0.65f)
        {
            _centipedeCoursePhase = CentipedeCourseDrivePhase.DownOuterWall;
        }
        if (_centipedeCoursePhase == CentipedeCourseDrivePhase.DownOuterWall
            && lead.SupportConfidence > 0.15f
            && lead.Chunk.Pos.Y < 3.35f && normal.Y < -0.65f)
        {
            _centipedeCoursePhase = CentipedeCourseDrivePhase.AlongCeiling;
        }

        controller.MoveTarget = null;
        controller.MoveDir = _centipedeCoursePhase switch
        {
            CentipedeCourseDrivePhase.DownOuterWall => Vector3.Down,
            CentipedeCourseDrivePhase.AlongCeiling => Vector3.Left,
            _ => Vector3.Right,
        };
        controller.RunSpeed = 1f;
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
    private long _deepRun;           // Lizard 当前连续 >100% 深违反（历史门保持不变）
    private long _maxDeepRun;        // 当前生物的最长单连接连跑；Centipede 另按每条连接 >10% 独立计
    private bool _nonFinite;         // 任意 chunk/limb 状态出现 NaN/Inf（一票 FAIL）
    private float _minTerrainSqueeze = 1f;
    private float _maxPostRecoveryPenetration;
    private float _maxCentipedeBodyPenetration;
    private float _maxCentipedeFootPenetration;
    private string _maxPenetrationSource = "none";
    private long _maxPenetrationTick = -1;
    private double _centipedeSupportRatioSum;
    private bool _invalidCentipedeTrailArc;
    private CentipedeCourseDrivePhase _centipedeCoursePhase;
    private long[] _centipedeCourseLeadTicks = Array.Empty<long>();
    private long[] _centipedeCourseTailTicks = Array.Empty<long>();
    private int[] _centipedeCourseConnectionRuns = Array.Empty<int>();
    private int[] _centipedeCourseConnectionMaxRuns = Array.Empty<int>();
    private int[] _centipedeConnectionRuns = Array.Empty<int>();
    private int _centipedeCourseNoneRun;
    private int _centipedeCourseMaxNoneRun;
    private int _centipedeCourseBlockedRun;
    private int _centipedeCourseMaxBlockedRun;
    private float _centipedeStepDownStartCenterX = float.NaN;
    private float _centipedeStepDownNetProgress;
    private long _centipedeStepDownLeadTick = -1;
    private long _centipedeStepDownTailTick = -1;
    private long _centipedeStepDownLeadWallTick = -1;
    private long _centipedeStepDownTailWallTick = -1;
    private int _centipedeStepDownLeadWallTicks;
    private int _centipedeStepDownTailWallTicks;
    private float _centipedeStepDownMinSeparationRatio = float.PositiveInfinity;
    private float _centipedeStepDownFinalSeparationRatio = float.PositiveInfinity;
    private int _centipedeStepDownPileRun;
    private int _centipedeStepDownMaxPileRun;
    private bool _centipedeStepDownLeadChanged;

    // —— 秃鹫路线指标（fly/perch；与蜥蜴指标块互斥使用）——
    private long _vultureLandedTick = -1; // perch：首次「吸附且非飞行」的 tick
    private long _vultureAirTicks;        // 全翅 Flap 的 tick 数
    private long _vultureAttachedTicks;   // 有翅吸附的 tick 数

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

    /// <summary>蜈蚣专属质量指标；不复用蜥蜴脊柱角/尾链/TurnAssist 场景状态。</summary>
    private void TrackCentipedeQualityMetrics()
    {
        CentipedeLocomotionController controller = _centipedeController!;
        Body body = controller.Body;
        _maxConstraintDev = Mathf.Max(_maxConstraintDev, body.LastRelaxDeviation);

        // LeadEnd 可被宿主显式切换；若直接累计 LeadChunk，会把端点切换误记成整段身体的瞬移里程。
        Vector3 leadPos = controller.LeadChunk.Pos;
        Vector3 center = Vector3.Zero;
        foreach (CentipedeSegment segment in controller.Segments)
        {
            center += segment.Chunk.Pos;
        }
        center /= controller.Segments.Count;
        bool hasMoveIntent = controller.RunSpeed > 1e-5f
            && (controller.MoveTarget is not null || controller.MoveDir.LengthSquared() > 1e-10f);
        if (_lastHeadPos != Vector3.Zero && hasMoveIntent && controller.SupportedSegmentCount > 0)
        {
            _walkDistance += center.DistanceTo(_lastHeadPos);
        }
        _lastHeadPos = center;
        _gripTickSum += _creature.GrippingAppendageCount;
        _centipedeSupportRatioSum += controller.SupportRatio;
        if (controller.SupportRatio >= 0.5f)
        {
            _gravityOffTicks++;
        }
        _maxHeadY = Mathf.Max(_maxHeadY, leadPos.Y);

        float endRatio = 0f;
        for (int connectionIndex = 0;
             connectionIndex < body.Connections.Count;
             connectionIndex++)
        {
            ChunkConnection conn = body.Connections[connectionIndex];
            if (conn.SoftOnly)
            {
                _centipedeConnectionRuns[connectionIndex] = 0;
                continue;
            }
            float error = (conn.B.Pos - conn.A.Pos).Length() - conn.RestLength;
            float deviation = conn.ConstraintMode switch
            {
                ChunkConnection.Mode.PullOnly => Mathf.Max(0f, error),
                ChunkConnection.Mode.PushOnly => Mathf.Max(0f, -error),
                _ => Mathf.Abs(error),
            };
            float ratio = conn.RestLength > 1e-6f ? deviation / conn.RestLength : 0f;
            endRatio = Mathf.Max(endRatio, ratio);
            _centipedeConnectionRuns[connectionIndex] = ratio > 0.10f
                ? _centipedeConnectionRuns[connectionIndex] + 1
                : 0;
            _maxDeepRun = Math.Max(
                _maxDeepRun, _centipedeConnectionRuns[connectionIndex]);
        }
        _endDevRatio = endRatio;
        _maxEndDevRatio = Mathf.Max(_maxEndDevRatio, endRatio);
        if (endRatio > 0.5f)
        {
            _stretchTicks++;
        }
        // 逐连接计数：相邻环依次越线不等于同一环持续断裂。与 core smoke/专用课程
        // 使用同一 10%/20 tick 定义，避免长体把沿链传播的合法恢复窗口误合并。

        for (int i = 0; i < body.Chunks.Count; i++)
        {
            BodyChunk chunk = body.Chunks[i];
            bool finite = chunk.Pos.IsFinite() && chunk.LastPos.IsFinite() && chunk.Vel.IsFinite()
                && float.IsFinite(chunk.TerrainRadius);
            _nonFinite |= !finite;
            if (finite)
            {
                ObserveCentipedePenetration($"chunk:{i}", chunk.Pos, chunk.TerrainRadius, isFoot: false);
            }
        }
        foreach (CentipedeSegment segment in controller.Segments)
        {
            _nonFinite |= !segment.SupportPoint.IsFinite()
                || !segment.SupportNormal.IsFinite()
                || !segment.Forward.IsFinite()
                || !segment.Side.IsFinite()
                || !segment.TargetCenter.IsFinite()
                || !float.IsFinite(segment.SupportConfidence);
        }
        float previousArc = -1e-6f;
        foreach (CentipedeSurfaceSample sample in controller.SurfaceTrail)
        {
            _nonFinite |= !sample.Point.IsFinite() || !sample.Normal.IsFinite()
                || !float.IsFinite(sample.ArcLength);
            _invalidCentipedeTrailArc |= sample.ArcLength + 1e-6f < previousArc;
            previousArc = sample.ArcLength;
        }
        _nonFinite |= !controller.MoveDir.IsFinite() || !float.IsFinite(controller.RunSpeed)
            || (controller.MoveTarget is { } moveTarget && !moveTarget.IsFinite())
            || !controller.LastMoveTarget.IsFinite() || !float.IsFinite(controller.SupportRatio);
        for (int i = 0; i < controller.Legs.Count; i++)
        {
            CentipedeLeg leg = controller.Legs[i];
            bool finite = leg.Pos.IsFinite() && leg.LastPos.IsFinite() && leg.Vel.IsFinite()
                && leg.GripPoint.IsFinite() && leg.GripNormal.IsFinite()
                && float.IsFinite(leg.Radius) && float.IsFinite(leg.Phase);
            _nonFinite |= !finite;
            if (finite)
            {
                ObserveCentipedePenetration($"leg:{i}", leg.Pos, leg.Radius, isFoot: true);
            }
        }
        _nonFinite |= !_creature.AppendageStateIsFinite;
        if (_centipedeStepDownDrive)
        {
            TrackCentipedeStepDownMetrics(controller, center);
        }
        if (_centipedeCourseDrive)
        {
            TrackCentipedeCourseMetrics(controller);
        }
    }

    private void TrackCentipedeStepDownMetrics(
        CentipedeLocomotionController controller, Vector3 center)
    {
        if (!float.IsFinite(_centipedeStepDownStartCenterX))
        {
            _centipedeStepDownStartCenterX = center.X;
        }
        _centipedeStepDownNetProgress = center.X - _centipedeStepDownStartCenterX;
        _centipedeStepDownLeadChanged |= controller.LeadEnd != CentipedeLeadEnd.Start
            || controller.RequestedLeadEnd != CentipedeLeadEnd.Start;

        CentipedeSegment lead = controller.Segments[0];
        CentipedeSegment tail = controller.Segments[^1];
        bool leadOnOuterWall = lead.SupportConfidence >= 0.15f
            && lead.SupportNormal.X >= 0.80f
            && lead.Chunk.Pos.Y > CentipedeStepDownLandingY
            && lead.Chunk.Pos.Y < CentipedeStepDownTopY + lead.Chunk.Radius + 0.10f;
        bool tailOnOuterWall = tail.SupportConfidence >= 0.15f
            && tail.SupportNormal.X >= 0.80f
            && tail.Chunk.Pos.Y > CentipedeStepDownLandingY
            && tail.Chunk.Pos.Y < CentipedeStepDownTopY + tail.Chunk.Radius + 0.10f;
        if (leadOnOuterWall)
        {
            _centipedeStepDownLeadWallTick = _centipedeStepDownLeadWallTick < 0
                ? _tick : _centipedeStepDownLeadWallTick;
            _centipedeStepDownLeadWallTicks++;
        }
        if (tailOnOuterWall)
        {
            _centipedeStepDownTailWallTick = _centipedeStepDownTailWallTick < 0
                ? _tick : _centipedeStepDownTailWallTick;
            _centipedeStepDownTailWallTicks++;
        }
        if (_centipedeStepDownLeadTick < 0
            && lead.Chunk.Pos.X - lead.Chunk.Radius > CentipedeStepDownEdgeX + 0.02f
            && lead.Chunk.Pos.Y < CentipedeStepDownLandingY
            && lead.SupportConfidence >= 0.15f && lead.SupportNormal.Y >= 0.70f)
        {
            _centipedeStepDownLeadTick = _tick;
        }
        if (_centipedeStepDownTailTick < 0
            && tail.Chunk.Pos.X - tail.Chunk.Radius > CentipedeStepDownEdgeX + 0.02f
            && tail.Chunk.Pos.Y < CentipedeStepDownLandingY
            && tail.SupportConfidence >= 0.15f && tail.SupportNormal.Y >= 0.70f)
        {
            _centipedeStepDownTailTick = _tick;
        }

        float tickMinSeparationRatio = float.PositiveInfinity;
        for (int i = 0; i < controller.Segments.Count; i++)
        {
            BodyChunk a = controller.Segments[i].Chunk;
            // 相邻与隔一节由刚性连接/SoftOnly 支柱决定，只有索引差 >2 才属于身体自交。
            for (int j = i + 3; j < controller.Segments.Count; j++)
            {
                BodyChunk b = controller.Segments[j].Chunk;
                float radiusSum = a.Radius + b.Radius;
                if (radiusSum > 1e-6f)
                {
                    tickMinSeparationRatio = Mathf.Min(
                        tickMinSeparationRatio, a.Pos.DistanceTo(b.Pos) / radiusSum);
                }
            }
        }
        _centipedeStepDownFinalSeparationRatio = tickMinSeparationRatio;
        _centipedeStepDownMinSeparationRatio = Mathf.Min(
            _centipedeStepDownMinSeparationRatio, tickMinSeparationRatio);
        _centipedeStepDownPileRun =
            tickMinSeparationRatio < CentipedeStepDownSevereOverlapRatio
                ? _centipedeStepDownPileRun + 1
                : 0;
        _centipedeStepDownMaxPileRun = Math.Max(
            _centipedeStepDownMaxPileRun, _centipedeStepDownPileRun);
    }

    private void TrackCentipedeCourseMetrics(CentipedeLocomotionController controller)
    {
        CentipedeSegment lead = controller.LeadEnd == CentipedeLeadEnd.Start
            ? controller.Segments[0] : controller.Segments[^1];
        CentipedeSegment tail = controller.LeadEnd == CentipedeLeadEnd.Start
            ? controller.Segments[^1] : controller.Segments[0];
        int leadStage = ClassifyCentipedeCourseStage(lead, includeLanding: false);
        int tailStage = ClassifyCentipedeCourseStage(tail, includeLanding: false);
        if (leadStage >= 0 && _centipedeCourseLeadTicks[leadStage] < 0)
        {
            _centipedeCourseLeadTicks[leadStage] = _tick;
        }
        if (tailStage >= 0 && _centipedeCourseTailTicks[tailStage] < 0)
        {
            _centipedeCourseTailTicks[tailStage] = _tick;
        }

        // “None”指领端已进入课程后却不属于任一连续支撑区，而不是出生下落。
        // landing 归入 slope 区间，仅用于连续性统计；斜坡首达仍要求真实 18° 法线。
        bool courseStarted = _centipedeCourseLeadTicks[(int)CentipedeCourseStage.Floor] >= 0;
        bool courseCompleted = _centipedeCourseTailTicks[(int)CentipedeCourseStage.Ceiling] >= 0;
        int supportRegion = ClassifyCentipedeCourseStage(lead, includeLanding: true);
        _centipedeCourseNoneRun = courseStarted && !courseCompleted && supportRegion < 0
            ? _centipedeCourseNoneRun + 1
            : 0;
        _centipedeCourseMaxNoneRun = Math.Max(
            _centipedeCourseMaxNoneRun, _centipedeCourseNoneRun);
        _centipedeCourseBlockedRun = courseStarted && !courseCompleted
            && controller.LeadSurfaceBlocked
                ? _centipedeCourseBlockedRun + 1
                : 0;
        _centipedeCourseMaxBlockedRun = Math.Max(
            _centipedeCourseMaxBlockedRun, _centipedeCourseBlockedRun);

        for (int i = 0; i < controller.Body.Connections.Count; i++)
        {
            ChunkConnection connection = controller.Body.Connections[i];
            if (connection.SoftOnly)
            {
                _centipedeCourseConnectionRuns[i] = 0;
                continue;
            }
            float error = Mathf.Abs(
                connection.A.Pos.DistanceTo(connection.B.Pos) - connection.RestLength);
            float ratio = connection.RestLength > 1e-6f ? error / connection.RestLength : 0f;
            _centipedeCourseConnectionRuns[i] = ratio > 0.10f
                ? _centipedeCourseConnectionRuns[i] + 1
                : 0;
            _centipedeCourseConnectionMaxRuns[i] = Math.Max(
                _centipedeCourseConnectionMaxRuns[i],
                _centipedeCourseConnectionRuns[i]);
        }
    }

    private static int ClassifyCentipedeCourseStage(
        CentipedeSegment segment, bool includeLanding)
    {
        if (segment.SupportConfidence < 0.15f
            || Mathf.Abs(segment.Chunk.Pos.Z - 20f) > 1.8f)
        {
            return -1;
        }

        Vector3 p = segment.Chunk.Pos;
        Vector3 n = segment.SupportNormal;
        if (n.Y < -0.70f && p.Y < 3.45f && p.X > 8.35f && p.X < 12.85f)
        {
            return (int)CentipedeCourseStage.Ceiling;
        }
        if (n.X > 0.70f && p.X > 12.1f && p.Y > 2.75f && p.Y < 5.2f)
        {
            return (int)CentipedeCourseStage.OuterWall;
        }
        if (n.X < -0.70f && p.X > 8.25f && p.X < 9.05f
            && p.Y > 1.55f && p.Y < 5.2f)
        {
            return (int)CentipedeCourseStage.InnerWall;
        }

        var rampNormal = new Vector3(-0.30901673f, 0.9510566f, 0f);
        if (n.Dot(rampNormal) > 0.94f && p.X > 0.75f && p.X < 7.1f
            && p.Y < 2.25f)
        {
            return (int)CentipedeCourseStage.Slope;
        }
        if (n.Y > 0.75f && p.Y > 4.45f && p.X > 8.25f && p.X < 12.85f)
        {
            return (int)CentipedeCourseStage.Top;
        }
        if (n.Y > 0.75f && p.Y < 0.75f && p.X < 1.35f)
        {
            return (int)CentipedeCourseStage.Floor;
        }
        if (includeLanding && n.Y > 0.70f
            && p.X >= 6.45f && p.X < 8.75f && p.Y < 2.25f)
        {
            return (int)CentipedeCourseStage.Slope;
        }
        return -1;
    }

    private void ObserveCentipedePenetration(string source, Vector3 center, float radius, bool isFoot)
    {
        if (!_rayDebug.SpherePenetration(center, radius, out _, out float depth))
        {
            return;
        }
        if (isFoot)
        {
            _maxCentipedeFootPenetration = Mathf.Max(_maxCentipedeFootPenetration, depth);
        }
        else
        {
            _maxCentipedeBodyPenetration = Mathf.Max(_maxCentipedeBodyPenetration, depth);
        }
        if (depth <= _maxPostRecoveryPenetration || depth <= 1e-6f)
        {
            return;
        }
        _maxPostRecoveryPenetration = depth;
        _maxPenetrationSource = source;
        _maxPenetrationTick = _tick;
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
        if (_centipedeController is not null)
        {
            return DumpCentipedeFinalState();
        }
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

    /// <summary>秃鹫路线的质量指标（与蜥蜴 TrackQualityMetrics 平行；3D 里程、
    /// 飞行/栖息占比、tick 末约束偏差率与深断裂连跑同口径）。</summary>
    private void TrackVultureMetrics()
    {
        VultureFlightController c = _vultureController!;
        if (_lastHeadPos != Vector3.Zero)
        {
            _walkDistance += (c.FrontSpine.Pos - _lastHeadPos).Length(); // 飞行是 3D 里程
        }
        _lastHeadPos = c.FrontSpine.Pos;
        if (c.FrontSpine.Pos.Y > _maxHeadY)
        {
            _maxHeadY = c.FrontSpine.Pos.Y;
        }
        if (c.AirBorne)
        {
            _vultureAirTicks++;
        }
        if (c.AnyWingAttached)
        {
            _vultureAttachedTicks++;
        }
        if (_bodies[0].LastRelaxDeviation > _maxConstraintDev)
        {
            _maxConstraintDev = _bodies[0].LastRelaxDeviation;
        }

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

        foreach (BodyChunk chunk in _bodies[0].Chunks)
        {
            _nonFinite |= !chunk.Pos.IsFinite() || !chunk.Vel.IsFinite();
        }
        foreach (VultureWing w in c.Wings)
        {
            foreach (VultureWing.WingSegment s in w.Segments)
            {
                _nonFinite |= !s.Pos.IsFinite() || !s.Vel.IsFinite();
            }
        }
    }

    /// <summary>秃鹫探针终态输出与判定（与蜥蜴 DumpFinalState 平行；[FINAL] body 行格式
    /// 保持一致供矩阵位置断言复用）。fly 路线要求飞行占比与越墙高度；perch 路线要求
    /// 真的降落吸附且终态栖息。</summary>
    private int DumpVultureFinalState()
    {
        VultureFlightController c = _vultureController!;
        GD.Print($"[METRIC] flightDistance={_walkDistance:F2}m waypointsReached={_waypointsReached} " +
                 $"maxHeadY={_maxHeadY:F2} airTicks={_vultureAirTicks} " +
                 $"attachedTicks={_vultureAttachedTicks} landedTick={_vultureLandedTick} " +
                 $"maxConstraintDev={_maxConstraintDev:F4} endDev={_endDevRatio:F2}x " +
                 $"maxEndDev={_maxEndDevRatio:F2}x stretchTicks={_stretchTicks} " +
                 $"maxDeepRun={_maxDeepRun} snagReleases={_bodies[0].SnagReleases}");
        GD.Print($"[FINAL] controller airBorne={c.AirBorne} attached={c.AnyWingAttached} " +
                 $"support={c.SupportValue:F2} phase={c.WingFlap:F2} amp={c.WingFlapAmplitude:F2} " +
                 $"frontVel={c.FrontSpine.Vel.Length():F4}");
        for (int i = 0; i < _bodies[0].Chunks.Count; i++)
        {
            BodyChunk chunk = _bodies[0].Chunks[i];
            GD.Print($"[FINAL] body=0 chunk={i} pos=({chunk.Pos.X:F4},{chunk.Pos.Y:F4},{chunk.Pos.Z:F4}) " +
                     $"vel={chunk.Vel.Length():F5} contact={chunk.TerrainContact} r={chunk.Radius:F2}");
        }
        for (int i = 0; i < c.Wings.Count; i++)
        {
            VultureWing w = c.Wings[i];
            VultureWing.WingSegment tip = w.Segments[^1];
            GD.Print($"[FINAL] wing={i} mode={w.Mode} attached={w.Attached} grip={w.GripCounter} " +
                     $"grippingSegs={w.GrippingSegments} fly={w.FlyingMode:F2} " +
                     $"tip=({tip.Pos.X:F4},{tip.Pos.Y:F4},{tip.Pos.Z:F4})");
        }

        var reasons = new List<string>();
        if (_nonFinite)
        {
            reasons.Add("状态出现 NaN/Inf");
        }
        if (_maxDeepRun > 100)
        {
            reasons.Add($"约束深度断裂持续 {_maxDeepRun} tick（>100）");
        }
        if (_expectHash is ulong expect && _probe!.Hash != expect)
        {
            reasons.Add($"哈希 {_probe.Hash:X16} ≠ 基线 {expect:X16}（有意改内核请同步两处真相源：矩阵脚本 + smoke ExpectedHash）");
        }
        if (_bodies[0].SnagReleases > 60)
        {
            reasons.Add($"卡链释放 {_bodies[0].SnagReleases} 次（>60）——传送震荡/慢性卡死");
        }
        if (_vulturePerchDrive)
        {
            if (_vultureLandedTick < 0)
            {
                reasons.Add("perch 路线没有发生降落吸附（场景覆盖失效）");
            }
            if (!c.AnyWingAttached || c.AirBorne)
            {
                reasons.Add($"perch 终态未栖息（attached={c.AnyWingAttached}, airBorne={c.AirBorne}）");
            }
        }
        else if (_vultureRouteSelected)
        {
            if (_vultureAirTicks < _tick * 8 / 10)
            {
                reasons.Add($"fly 路线飞行占比不足（{_vultureAirTicks}/{_tick} tick）");
            }
            if (_maxHeadY < 4f)
            {
                reasons.Add($"fly 路线最高只飞到 {_maxHeadY:F2}m（<4——没有完成越墙爬升）");
            }
        }
        bool pass = reasons.Count == 0;
        GD.Print(pass ? "[RESULT] PASS" : $"[RESULT] FAIL: {string.Join("; ", reasons)}");
        return pass ? 0 : 1;
    }

    private int DumpCentipedeFinalState()
    {
        CentipedeLocomotionController controller = _centipedeController!;
        float narrowWallWholeBodyClearance = float.PositiveInfinity;
        int narrowWallFinalGrips = 0;
        if (_centipedeNarrowWallDrive)
        {
            foreach (CentipedeSegment segment in controller.Segments)
            {
                narrowWallWholeBodyClearance = Mathf.Min(
                    narrowWallWholeBodyClearance,
                    CentipedeNarrowWallFarFaceX
                    - (segment.Chunk.Pos.X + segment.Chunk.Radius));
            }
            foreach (CentipedeLeg leg in controller.Legs)
            {
                if (leg.Gripping)
                {
                    narrowWallFinalGrips++;
                }
            }
        }
        int legBarrierRecoveries = 0;
        foreach (CentipedeLeg leg in controller.Legs)
        {
            legBarrierRecoveries += leg.TerrainBarrierRecoveries;
        }
        float firstRest = _bodies[0].Connections.Count > 0
            ? _bodies[0].Connections[0].RestLength
            : 1f;
        GD.Print($"[METRIC] creature={_creature.StableId} segments={controller.Segments.Count} " +
                 $"maxConstraintDev={_maxConstraintDev:F4} " +
                 $"({_maxConstraintDev / firstRest * 100f:F1}% of rest) " +
                 $"walkDistance={_walkDistance:F2}m waypointsReached={_waypointsReached} " +
                 $"avgLegsGripping={(float)_gripTickSum / _tick:F2}/{controller.Legs.Count} " +
                 $"avgSupport={_centipedeSupportRatioSum / _tick:P0} " +
                 $"supportMajority={(float)_gravityOffTicks / _tick:P0} maxLeadY={_maxHeadY:F2} " +
                 $"endDev={_endDevRatio:F2}x maxEndDev={_maxEndDevRatio:F2}x " +
                 $"stretchTicks={_stretchTicks} maxDeepRun={_maxDeepRun} " +
                 $"snagReleases={controller.Body.SnagReleases} " +
                 $"legBarrierRecoveries={legBarrierRecoveries} " +
                 $"penetration={_maxPostRecoveryPenetration:F6}m " +
                 $"bodyPenetration={_maxCentipedeBodyPenetration:F6}m " +
                 $"footPenetration={_maxCentipedeFootPenetration:F6}m " +
                 $"penetrationAt={_maxPenetrationSource}@{_maxPenetrationTick}");
        GD.Print($"[FINAL] centipede leadEnd={controller.LeadEnd} " +
                 $"supported={controller.SupportedSegmentCount}/{controller.Segments.Count} " +
                 $"supportRatio={controller.SupportRatio:F3} atTarget={controller.AtMoveTarget} " +
                 $"targetKind={controller.LastMoveTargetKind} trailSamples={controller.SurfaceTrail.Count}");
        int courseMaxConnectionRun = 0;
        int courseMaxTailLag = 0;
        int courseTailBudget = 40 + 8 * controller.Segments.Count;
        if (_centipedeCourseDrive)
        {
            var connectionRuns = new List<string>();
            for (int i = 0; i < _centipedeCourseConnectionMaxRuns.Length; i++)
            {
                courseMaxConnectionRun = Math.Max(
                    courseMaxConnectionRun, _centipedeCourseConnectionMaxRuns[i]);
                if (!controller.Body.Connections[i].SoftOnly)
                {
                    connectionRuns.Add($"{i}:{_centipedeCourseConnectionMaxRuns[i]}");
                }
            }
            for (int i = 0; i < (int)CentipedeCourseStage.Count; i++)
            {
                long leadTick = _centipedeCourseLeadTicks[i];
                long tailTick = _centipedeCourseTailTicks[i];
                long lag = leadTick >= 0 && tailTick >= 0 ? tailTick - leadTick : -1;
                if (lag >= 0)
                {
                    courseMaxTailLag = Math.Max(courseMaxTailLag, (int)lag);
                }
                GD.Print($"[CENTIPEDE-COURSE] stage={CentipedeCourseStageNames[i]} " +
                         $"lead={leadTick} tail={tailTick} lag={lag}");
            }
            GD.Print($"[CENTIPEDE-COURSE] drive={_centipedeCoursePhase} " +
                     $"maxNoneRun={_centipedeCourseMaxNoneRun} " +
                     $"maxBlockedRun={_centipedeCourseMaxBlockedRun} " +
                     $"maxConnectionRun={courseMaxConnectionRun} " +
                     $"maxTailLag={courseMaxTailLag} tailBudget={courseTailBudget} " +
                     $"connectionRuns=[{string.Join(',', connectionRuns)}]");
        }
        if (_centipedeStepDownDrive)
        {
            long tailLag = _centipedeStepDownLeadTick >= 0 && _centipedeStepDownTailTick >= 0
                ? _centipedeStepDownTailTick - _centipedeStepDownLeadTick
                : -1;
            GD.Print($"[CENTIPEDE-STEP-DOWN] lead={_centipedeStepDownLeadTick} " +
                     $"tail={_centipedeStepDownTailTick} lag={tailLag} " +
                     $"leadWall={_centipedeStepDownLeadWallTick}/" +
                     $"{_centipedeStepDownLeadWallTicks} " +
                     $"tailWall={_centipedeStepDownTailWallTick}/" +
                     $"{_centipedeStepDownTailWallTicks} " +
                     $"netProgress={_centipedeStepDownNetProgress:F3}m " +
                     $"minNonAdjacent={_centipedeStepDownMinSeparationRatio:F3}x " +
                     $"finalNonAdjacent={_centipedeStepDownFinalSeparationRatio:F3}x " +
                     $"maxPileRun={_centipedeStepDownMaxPileRun} " +
                     $"leadChanged={_centipedeStepDownLeadChanged}");
        }
        if (_centipedeNarrowWallDrive)
        {
            GD.Print($"[CENTIPEDE-NARROW-WALL] waypoints={_waypointsReached}/1 " +
                     $"settled={_centipedeNarrowWallSettledTicks}/80 " +
                     $"lead={controller.LeadEnd}/End " +
                     $"wholeBodyClearance={narrowWallWholeBodyClearance:F3}/-0.002m " +
                     $"finalGrips={narrowWallFinalGrips} maxLeadY={_maxHeadY:F2}/3.05");
        }
        for (int i = 0; i < controller.Segments.Count; i++)
        {
            CentipedeSegment segment = controller.Segments[i];
            BodyChunk chunk = segment.Chunk;
            GD.Print($"[FINAL] body=0 chunk={i} pos=({chunk.Pos.X:F4},{chunk.Pos.Y:F4},{chunk.Pos.Z:F4}) " +
                     $"vel={chunk.Vel.Length():F5} contact={chunk.TerrainContact} r={chunk.Radius:F2} " +
                     $"support={segment.SupportConfidence:F3} normal=({segment.SupportNormal.X:F3}," +
                     $"{segment.SupportNormal.Y:F3},{segment.SupportNormal.Z:F3})");
        }
        for (int i = 0; i < controller.Legs.Count; i++)
        {
            CentipedeLeg leg = controller.Legs[i];
            GD.Print($"[FINAL] centipedeLeg={i} segment={leg.Anchor.Index} " +
                     $"pos=({leg.Pos.X:F4},{leg.Pos.Y:F4},{leg.Pos.Z:F4}) " +
                     $"grip={leg.GripCounter} hasGrip={leg.HasGrip} swinging={leg.IsSwinging}");
        }

        var reasons = new List<string>();
        if (_nonFinite)
        {
            reasons.Add("蜈蚣状态出现 NaN/Inf");
        }
        if (_maxDeepRun > 20)
        {
            reasons.Add($"连接深度断裂持续 {_maxDeepRun} tick（>20）");
        }
        if (_endDevRatio > 0.10f)
        {
            reasons.Add($"终态连接偏差 {_endDevRatio:P1}（>10%）");
        }
        if (_maxPostRecoveryPenetration > 0.002f)
        {
            reasons.Add($"身体/足端穿透 {_maxPostRecoveryPenetration:F4}m（>0.002m）");
        }
        if (_invalidCentipedeTrailArc)
        {
            reasons.Add("表面轨迹累计弧长非单调");
        }
        if (_centipedeStepDownDrive)
        {
            long tailLag = _centipedeStepDownLeadTick >= 0 && _centipedeStepDownTailTick >= 0
                ? _centipedeStepDownTailTick - _centipedeStepDownLeadTick
                : -1;
            if (_centipedeStepDownLeadChanged)
            {
                reasons.Add("下阶梯期间显式 Start 领航端发生变化");
            }
            if (_centipedeStepDownLeadWallTick < 0 || _centipedeStepDownLeadWallTicks < 1
                || _centipedeStepDownTailWallTick < 0 || _centipedeStepDownTailWallTicks < 1)
            {
                reasons.Add($"下阶梯未取得真实外侧立面支撑（lead=" +
                            $"{_centipedeStepDownLeadWallTick}/{_centipedeStepDownLeadWallTicks}, " +
                            $"tail={_centipedeStepDownTailWallTick}/{_centipedeStepDownTailWallTicks}）");
            }
            if (_centipedeStepDownLeadTick < 0 || _centipedeStepDownTailTick < 0)
            {
                reasons.Add($"固定头下阶梯未完整通过（lead={_centipedeStepDownLeadTick}, " +
                            $"tail={_centipedeStepDownTailTick}）");
            }
            else if (tailLag < 0 || tailLag > courseTailBudget)
            {
                reasons.Add($"下阶梯尾端滞后 {tailLag} tick（要求 0..{courseTailBudget}）");
            }
            if (_centipedeStepDownNetProgress < CentipedeStepDownMinimumProgress)
            {
                reasons.Add($"下阶梯身体净前进 {_centipedeStepDownNetProgress:F2}m" +
                            $"（<{CentipedeStepDownMinimumProgress:F2}m）");
            }
            if (_centipedeStepDownMaxPileRun > CentipedeStepDownPileRunBudget)
            {
                reasons.Add($"下阶梯非相邻节严重重叠连续 {_centipedeStepDownMaxPileRun} tick" +
                            $"（>{CentipedeStepDownPileRunBudget}）");
            }
            if (_centipedeStepDownFinalSeparationRatio < CentipedeStepDownFinalSeparationRatio)
            {
                reasons.Add($"下阶梯终态非相邻节最小间距仅 " +
                            $"{_centipedeStepDownFinalSeparationRatio:F2}×半径和" +
                            $"（<{CentipedeStepDownFinalSeparationRatio:F2}）");
            }
        }
        if (_centipedeNarrowWallDrive)
        {
            if (_waypointsReached < 1)
            {
                reasons.Add($"窄墙向前翻越只完成 {_waypointsReached}/1 次");
            }
            if (_centipedeNarrowWallSettledTicks < 80)
            {
                reasons.Add($"窄墙翻越后只收敛 {_centipedeNarrowWallSettledTicks}/80 tick");
            }
            if (controller.LeadEnd != CentipedeLeadEnd.End
                || controller.RequestedLeadEnd != CentipedeLeadEnd.End)
            {
                reasons.Add("窄墙回归期间显式 End 领航端发生变化");
            }
            if (narrowWallWholeBodyClearance < -0.002f)
            {
                reasons.Add($"窄墙停驶后仍有身体球未完整越过远侧墙面" +
                            $"（clearance={narrowWallWholeBodyClearance:F3}m < -0.002m）");
            }
            if (narrowWallFinalGrips < 1)
            {
                reasons.Add("窄墙停驶终态没有真实抓足");
            }
            if (_maxHeadY < 3.05f)
            {
                reasons.Add($"窄墙领航端最高仅 {_maxHeadY:F2}m（<3.05m，未越过墙顶）");
            }
        }
        if (_centipedeCourseDrive)
        {
            for (int i = 0; i < (int)CentipedeCourseStage.Count; i++)
            {
                long leadTick = _centipedeCourseLeadTicks[i];
                long tailTick = _centipedeCourseTailTicks[i];
                if (leadTick < 0 || tailTick < 0)
                {
                    reasons.Add($"课程阶段 {CentipedeCourseStageNames[i]} 未完整通过" +
                                $"（lead={leadTick}, tail={tailTick}）");
                    continue;
                }
                long lag = tailTick - leadTick;
                if (lag < 0)
                {
                    reasons.Add($"课程阶段 {CentipedeCourseStageNames[i]} 尾端先于领端到达" +
                                $"（lag={lag}）");
                }
                else if (lag > courseTailBudget)
                {
                    reasons.Add($"课程阶段 {CentipedeCourseStageNames[i]} 尾端滞后 {lag} tick" +
                                $"（>{courseTailBudget}）");
                }
            }
            if (_centipedeCourseMaxNoneRun > 40)
            {
                reasons.Add($"课程换面无有效支撑连续 {_centipedeCourseMaxNoneRun} tick（>40）");
            }
            if (_centipedeCourseMaxBlockedRun > 40)
            {
                reasons.Add($"课程领航路径阻塞连续 {_centipedeCourseMaxBlockedRun} tick（>40）");
            }
            if (courseMaxConnectionRun > 20)
            {
                reasons.Add($"课程相邻连接偏差 >10% 连续 {courseMaxConnectionRun} tick（>20）");
            }
        }
        if (_expectHash is ulong expect && _probe!.Hash != expect)
        {
            reasons.Add($"哈希 {_probe.Hash:X16} ≠ 基线 {expect:X16}");
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
        if (_camFollowOffset is { } camOffset)
        {
            // 截图/视觉验证的跟踪相机：钉在生物头部偏移处注视头部（纯渲染，不进物理）。
            Vector3 focus = _creature.LeadChunk.LerpPos((float)Engine.GetPhysicsInterpolationFraction());
            _camera.Position = focus + camOffset;
            _camera.LookAt(focus, Vector3.Up);
        }
        if (_humanoid is not null)
        {
            float humanoidAlpha = (float)Engine.GetPhysicsInterpolationFraction();
            if (_formalRenderer is { } humanoidFormal && _formalView)
            {
                humanoidFormal.Draw(humanoidAlpha, (float)delta);
            }
            else
            {
                _humanoid.Renderer.Draw(humanoidAlpha, _humanoid.ThrownProp,
                    _rayDebug.Enabled);
            }
            _rayDebug.Draw(_camera);
            MaybeCaptureScreenshot();
            return;
        }
        float alpha = (float)Engine.GetPhysicsInterpolationFraction();
        if (_formalRenderer is { } formal && _formalView)
        {
            formal.Draw(alpha, (float)delta);
        }
        else
        {
            _renderer.Draw(alpha);
        }
        _creature.DrawDebug(_rayDebug, _camera);
        MaybeCaptureScreenshot();
    }

    /// <summary>--screenshot 视觉验证回路：到达指定 tick 后保存视口帧并退出。
    /// 渲染专用旁路——不触碰物理与哈希；headless 下图像为空，别在矩阵里用。</summary>
    private void MaybeCaptureScreenshot()
    {
        if (_screenshotPath is null || _tick < _screenshotTick)
        {
            return;
        }
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_screenshotPath);
        GD.Print($"[SANDBOX] screenshot {(err == Error.Ok ? "saved" : $"FAILED ({err})")}: " +
            $"{_screenshotPath} (tick {_tick})");
        _screenshotPath = null;
        GetTree().Quit(err == Error.Ok ? 0 : 3);
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

    /// <summary>F3：开关射线+推进目标（胡萝卜）可视化（只影响绘制）。数字键 1~4 保留蜥蜴，
    /// 5~8 切换蜈蚣实例；R 切换当前蜈蚣由哪一端领航（都只在交互模式生效）。
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
        // 人形演示键位（交互模式限定）：P=指向鼠标点、C=持物开关、T=按住蓄力/松开投掷。
        // T 要响应松开事件，必须在下面的 Pressed 过滤之前处理。
        if (_humanoid is not null && _probe is null && @event is InputEventKey hk)
        {
            if (hk.PhysicalKeycode == Key.T)
            {
                if (hk is { Pressed: true, Echo: false })
                {
                    _humanoid.BeginThrowCharge();
                }
                else if (!hk.Pressed)
                {
                    _humanoid.ReleaseThrowInteractive();
                }
                return;
            }
            if (hk is { Pressed: true, Echo: false })
            {
                if (hk.PhysicalKeycode == Key.P)
                {
                    _humanoid.TogglePoint(_camera, _rayDebug);
                    return;
                }
                if (hk.PhysicalKeycode == Key.C)
                {
                    _humanoid.ToggleCarry();
                    return;
                }
            }
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
        if (key.PhysicalKeycode == Key.V)
        {
            _formalView = !_formalView;
            ApplyRenderView();
            GD.Print($"[SANDBOX] formal render {(_formalView ? "on" : "off")}" +
                (_formalRenderer is null ? "（该物种暂无正式渲染，仍为白盒）" : ""));
            return;
        }
        if (key.PhysicalKeycode == Key.R)
        {
            if (_probe is null && _centipedeController is not null)
            {
                _requestedCentipedeLeadEnd =
                    _centipedeController.RequestedLeadEnd == CentipedeLeadEnd.Start
                        ? CentipedeLeadEnd.End
                        : CentipedeLeadEnd.Start;
                _centipedeController.RequestedLeadEnd = _requestedCentipedeLeadEnd;
                SyncLeadEndUI();
                GD.Print($"[SANDBOX] centipede requested lead -> {_requestedCentipedeLeadEnd}");
            }
            return;
        }
        // 数字行 1..9,0,-,= 依次映射 12 个生物（4 蜥蜴 + 4 蜈蚣 + 4 秃鹫）。
        int creatureIndex = key.PhysicalKeycode switch
        {
            >= Key.Key1 and <= Key.Key9 => (int)(key.PhysicalKeycode - Key.Key1),
            Key.Key0 => 9,
            Key.Minus => 10,
            Key.Equal => 11,
            _ => -1,
        };
        if (_probe is not null || creatureIndex < 0)
        {
            return;
        }
        SelectCreature(creatureIndex);
    }

    /// <summary>数字行与下拉面板共用的换生物入口；蜥蜴四预设在前、蜈蚣四预设居中、
    /// 秃鹫四预设续接、人形三预设列尾（数字行 12 键已满，仅下拉框可达）——选中即换生物类别。</summary>
    private void SelectCreature(int index)
    {
        BreedParams[] breeds = BodyFactory.AllBreeds();
        CentipedeParams[] centipedes = CentipedeFactory.AllPresets();
        VultureBreedParams[] vultures = BodyFactory.AllVultureBreeds();
        HumanoidParams[] humanoids = BodyFactory.AllHumanoids();
        if (index < 0 || index >= breeds.Length + centipedes.Length + vultures.Length + humanoids.Length)
        {
            return;
        }
        // 在原地上方重生：旧身体整体替换（物理与渲染都换新），品种对比不用重启场景。
        Vector3 origin = _creature.RespawnAnchor.Pos + Vector3.Up * 0.5f;
        if (index < breeds.Length)
        {
            SpawnLizard(breeds[index], origin);
        }
        else if (index < breeds.Length + centipedes.Length)
        {
            SpawnCentipede(centipedes[index - breeds.Length].StableId, origin);
        }
        else if (index < breeds.Length + centipedes.Length + vultures.Length)
        {
            SpawnVulture(vultures[index - breeds.Length - centipedes.Length], origin);
        }
        else
        {
            SpawnHumanoid(humanoids[index - breeds.Length - centipedes.Length - vultures.Length], origin);
        }
        _creatureUI?.SyncSelection(index);
        SyncLeadEndUI();
        GD.Print($"[SANDBOX] creature -> {_creature.StableId}" +
                 (_centipedeController is null
                     ? string.Empty
                     : $", requestedLead={_centipedeController.RequestedLeadEnd}"));
    }

    private void SyncLeadEndUI()
    {
        _creatureUI?.SyncLeadEnd(_centipedeController is null
            ? null
            : _centipedeController.RequestedLeadEnd.ToString().ToLowerInvariant());
    }

    /// <summary>解析 `-- --determinism=N [--tps=400] [--creature=… --lead=start|end]`：
    /// 无头回归模式，禁输入、可加速跑。`--help` 打印完整宿主用法。
    /// 返回 false = 参数畸形（含未知开关、非有限数）。必须快速失败——解析半途抛异常曾把
    /// _Ready 留在残局，随后 _PhysicsProcess 每帧 NRE、进程不退出、日志无限膨胀。</summary>
    private bool ParseDeterminismArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (arg is "--help" or "-h")
                {
                    _showCommandHelp = true;
                }
                else if (arg.StartsWith("--determinism="))
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
                else if (arg.StartsWith("--screenshot="))
                {
                    // 视觉验证回路（仅交互模式有意义；headless 下取不到帧）：到 tick 截图并退出。
                    string spec = arg["--screenshot=".Length..];
                    int at = spec.LastIndexOf('@');
                    if (at > 0)
                    {
                        _screenshotPath = spec[..at];
                        _screenshotTick = long.Parse(spec[(at + 1)..], inv);
                    }
                    else
                    {
                        _screenshotPath = spec;
                    }
                }
                else if (arg.StartsWith("--cam="))
                {
                    string[] parts = arg["--cam=".Length..].Split(',');
                    if (parts.Length != 6)
                    {
                        throw new System.FormatException("--cam 需要 px,py,pz,lx,ly,lz 六个分量");
                    }
                    _camOverride = (
                        new Vector3(float.Parse(parts[0], inv), float.Parse(parts[1], inv),
                            float.Parse(parts[2], inv)),
                        new Vector3(float.Parse(parts[3], inv), float.Parse(parts[4], inv),
                            float.Parse(parts[5], inv)));
                }
                else if (arg.StartsWith("--camfollow="))
                {
                    string[] parts = arg["--camfollow=".Length..].Split(',');
                    if (parts.Length != 3)
                    {
                        throw new System.FormatException("--camfollow 需要 ox,oy,oz 三个分量");
                    }
                    _camFollowOffset = new Vector3(float.Parse(parts[0], inv),
                        float.Parse(parts[1], inv), float.Parse(parts[2], inv));
                }
                else if (arg.StartsWith("--autowalk="))
                {
                    string[] parts = arg["--autowalk=".Length..].Split(',');
                    if (parts.Length != 2)
                    {
                        throw new System.FormatException("--autowalk 需要 dx,dz 两个分量");
                    }
                    var dir = new Vector3(float.Parse(parts[0], inv), 0f, float.Parse(parts[1], inv));
                    if (dir.LengthSquared() < 1e-8f)
                    {
                        throw new System.FormatException("--autowalk 方向不能为零");
                    }
                    _autoWalkDir = dir.Normalized();
                }
                else if (arg == "--formal=off")
                {
                    _formalView = false;
                }
                else if (arg == "--route=centipede-course")
                {
                    _waypoints = StandRoute;
                    _centipedeCourseDrive = true;
                    _routeName = "centipede-course";
                }
                else if (arg == "--route=centipede-step-down")
                {
                    _waypoints = StandRoute;
                    _centipedeStepDownDrive = true;
                    _routeName = "centipede-step-down";
                }
                else if (arg == "--route=centipede-narrow-wall")
                {
                    _waypoints = WallRoute;
                    _centipedeNarrowWallDrive = true;
                    _routeName = "centipede-narrow-wall";
                }
                else if (arg == "--route=wall")
                {
                    _waypoints = WallRoute;
                    _lizardRouteSet = true;
                    _routeName = "wall";
                }
                else if (arg == "--route=turn")
                {
                    _waypoints = TurnRoute;
                    _regressionScenario = RegressionScenario.Turn;
                    _lizardRouteSet = true;
                    _routeName = "turn";
                }
                else if (arg == "--route=wall-turn")
                {
                    _waypoints = StandRoute;
                    _regressionScenario = RegressionScenario.Turn;
                    _wallTurnDrive = true;
                    _lizardRouteSet = true;
                    _routeName = "wall-turn";
                }
                else if (arg == "--route=wall-tail")
                {
                    _waypoints = WallRoute;
                    _regressionScenario = RegressionScenario.Tail;
                    _lizardRouteSet = true;
                    _routeName = "wall-tail";
                }
                else if (arg == "--route=wall-corner")
                {
                    _waypoints = WallRoute;
                    _regressionScenario = RegressionScenario.Corner;
                    _lizardRouteSet = true;
                    _routeName = "wall-corner";
                }
                else if (arg == "--route=stand")
                {
                    _waypoints = StandRoute;
                    _routeName = "stand";
                }
                else if (arg == "--route=carrot")
                {
                    _waypoints = CarrotRoute;
                    _carrotDrive = true;
                    _lizardRouteSet = true;
                    _routeName = "carrot";
                }
                else if (arg == "--route=carrot-turn")
                {
                    _waypoints = StandRoute;
                    _regressionScenario = RegressionScenario.CarrotTurn;
                    _carrotTurnDrive = true;
                    _lizardRouteSet = true;
                    _routeName = "carrot-turn";
                }
                else if (arg == "--route=fly")
                {
                    _waypoints = VultureFlyRoute;
                    _vultureRouteSelected = true;
                }
                else if (arg == "--route=perch")
                {
                    _waypoints = VulturePerchRoute;
                    _vultureRouteSelected = true;
                    _vulturePerchDrive = true;
                }
                else if (arg == "--species=humanoid")
                {
                    _speciesHumanoid = true;
                }
                else if (arg == "--route=hwalk")
                {
                    _humanoidWaypoints = HumanoidSandboxDriver.WalkRoute;
                    _routeName = "hwalk";
                }
                else if (arg == "--route=hact")
                {
                    _humanoidAct = true;
                    _routeName = "hact";
                }
                else if (arg.StartsWith("--stun="))
                {
                    string[] parts = arg["--stun=".Length..].Split(',');
                    if (parts.Length != 2)
                    {
                        throw new System.FormatException("stun 需要 T,D 两个分量（起始 tick, 持续 tick）");
                    }
                    _stunTick = long.Parse(parts[0], inv);
                    _stunDuration = int.Parse(parts[1], inv);
                    if (_stunTick < 1 || _stunDuration < 1)
                    {
                        throw new System.FormatException("stun 的起始与持续都必须为正");
                    }
                }
                else if (arg.StartsWith("--breed="))
                {
                    // 品种名分派生物类别：落在秃鹫表 → 秃鹫模式；否则蜥蜴（未知名回落 default）。
                    // 人形物种在 _Ready 按原始名重解析（HumanoidByName），打错名由 CLI 校验硬拒。
                    _breedName = arg["--breed=".Length..];
                    if (BodyFactory.IsVultureBreed(_breedName))
                    {
                        _vultureBreed = BodyFactory.VultureByName(_breedName);
                    }
                    else
                    {
                        _breed = BodyFactory.ByName(_breedName);
                    }
                    _breedExplicit = true;
                }
                else if (arg.StartsWith("--creature="))
                {
                    string stableId = arg["--creature=".Length..];
                    if (!CentipedeFactory.TryByStableId(stableId, out _))
                    {
                        throw new System.FormatException(
                            "未知 creature ID；可用 centipede/short|long|armored|ribbon");
                    }
                    _centipedeId = stableId;
                }
                else if (arg.StartsWith("--lead="))
                {
                    _requestedCentipedeLeadEnd = arg["--lead=".Length..] switch
                    {
                        "start" => CentipedeLeadEnd.Start,
                        "end" => CentipedeLeadEnd.End,
                        _ => throw new System.FormatException(
                            "未知 lead；可用 --lead=start|end"),
                    };
                    _leadExplicit = true;
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
        if (_vultureRouteSelected && _vultureBreed is null)
        {
            GD.PrintErr("[SANDBOX] fly/perch 路线是秃鹫专属（--breed=vulture|king|swift|quad）");
            return false;
        }
        if (_probe is not null && _vultureBreed is not null && !_vultureRouteSelected)
        {
            // 蜥蜴路线的指标/判定读不到秃鹫控制器——静默跑错配置比报错更危险。
            GD.PrintErr("[SANDBOX] 秃鹫确定性回归需要 --route=fly 或 --route=perch");
            return false;
        }
        if (_centipedeId is not null && _breedExplicit)
        {
            GD.PrintErr("[SANDBOX] --breed 与 --creature 互斥；一次只能装配一种生物");
            return false;
        }
        if (_leadExplicit && _centipedeId is null)
        {
            GD.PrintErr("[SANDBOX] --lead 仅适用于 --creature=centipede/...");
            return false;
        }
        if (_centipedeCourseDrive && _centipedeId is null)
        {
            GD.PrintErr("[SANDBOX] --route=centipede-course 仅适用于 centipede");
            return false;
        }
        if (_centipedeStepDownDrive && _centipedeId is null)
        {
            GD.PrintErr("[SANDBOX] --route=centipede-step-down 仅适用于 centipede");
            return false;
        }
        if (_centipedeNarrowWallDrive && _centipedeId is null)
        {
            GD.PrintErr("[SANDBOX] --route=centipede-narrow-wall 仅适用于 centipede");
            return false;
        }
        if (_centipedeStepDownDrive
            && (!_leadExplicit || _requestedCentipedeLeadEnd != CentipedeLeadEnd.Start))
        {
            GD.PrintErr("[SANDBOX] --route=centipede-step-down 需要显式 --lead=start");
            return false;
        }
        if (_centipedeNarrowWallDrive
            && (!_leadExplicit || _requestedCentipedeLeadEnd != CentipedeLeadEnd.End))
        {
            GD.PrintErr("[SANDBOX] --route=centipede-narrow-wall 需要显式 --lead=end");
            return false;
        }
        if (_centipedeId is not null && _regressionScenario != RegressionScenario.None)
        {
            // 这些路线的驱动与硬门直接读取 LizardLocomotionController 的脊柱/尾链状态。
            // 让蜈蚣静止跑完再报 PASS 是危险假绿；蜈蚣课程使用独立路线与断言接入前先硬拒。
            GD.PrintErr($"[SANDBOX] --route={_routeName} 仅适用于 lizard；" +
                        "centipede 当前可用 default|wall|stand|carrot|centipede-course|" +
                        "centipede-step-down|centipede-narrow-wall");
            return false;
        }
        if (_speciesHumanoid && _centipedeId is not null)
        {
            GD.PrintErr("[SANDBOX] --species=humanoid 与 --creature= 互斥；一次只能装配一种生物");
            return false;
        }
        if (_speciesHumanoid && _lizardRouteSet)
        {
            // 物种/路线错配必须硬拒——人形静默跑蜥蜴路线（或反之）会对着错误基线绿灯。
            GD.PrintErr("[SANDBOX] --species=humanoid 只能配 --route=hwalk|hact|stand（蜥蜴路线是蜥蜴专属）");
            return false;
        }
        if (_speciesHumanoid && _vultureRouteSelected)
        {
            GD.PrintErr("[SANDBOX] --species=humanoid 不能配秃鹫 fly/perch 路线");
            return false;
        }
        if (!_speciesHumanoid
            && (_humanoidWaypoints.Length > 0 || _humanoidAct || _stunTick >= 0))
        {
            GD.PrintErr("[SANDBOX] --route=hwalk|hact / --stun= 需要配合 --species=humanoid");
            return false;
        }
        if (_breedName is { } rawBreed)
        {
            // 内核 ByName/HumanoidByName 契约是静默回落（内核零日志）；CLI 层必须硬拒——
            // 品种名打错安静跑成默认品种，会对着错误基线绿灯（与未知参数硬拒同一条纪律）。
            // 秃鹫名在解析期已由 IsVultureBreed 精确匹配，无需回验。
            string resolved = _speciesHumanoid
                ? BodyFactory.HumanoidByName(rawBreed).Name
                : BodyFactory.IsVultureBreed(rawBreed)
                    ? rawBreed
                    : BodyFactory.ByName(rawBreed).Name;
            if (resolved != rawBreed)
            {
                GD.PrintErr($"[SANDBOX] 未知品种: {rawBreed}" +
                            $"（{(_speciesHumanoid ? "humanoid" : "lizard")} 物种下无此名）");
                return false;
            }
        }
        if (_probe is not null)
        {
            // 事件时刻越出探针预算 = 事件从未发生、场景断言静默蒸发（假绿）。给恢复窗留余量。
            const int recoveryBudget = 250;
            if (_yankTick >= 0 && _yankTick + recoveryBudget > _determinismTicks)
            {
                GD.PrintErr($"[SANDBOX] --yank={_yankTick} 越界：须 ≤ determinism-{recoveryBudget}");
                return false;
            }
            if (_stunTick >= 0 && _stunTick + _stunDuration + recoveryBudget > _determinismTicks)
            {
                GD.PrintErr($"[SANDBOX] --stun={_stunTick},{_stunDuration} 越界：" +
                            $"苏醒+恢复窗须落在 determinism 预算内（余量 {recoveryBudget}）");
                return false;
            }
        }
        return true;
    }
}
