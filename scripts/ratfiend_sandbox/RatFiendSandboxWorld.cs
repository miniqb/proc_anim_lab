using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.RatFiend;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Render;

namespace ProcAnimLab.RatFiendSandbox;

/// <summary>
/// 独立 RatFiend（鼠煞）回归沙盒：40 tick 固定序驱动核心，渲染帧只做插值与自由摄像机。
/// 交互与无头专项矩阵都只经过本场景，不改其它物种沙盒。
/// 地形：平地 + 0.3m 台阶（walk/crawl-step）+ 0.82m 翻越桌 + 转向墙；
/// 其余断肢/爬行/攻击路线全在平地完成。
/// 断肢路线用脚本化固定 tick 调 Sever——普通路线从不调它，两族哈希基线正交。
/// </summary>
public partial class RatFiendSandboxWorld : Node3D
{
    private const float TickDt = 0.025f;
    private static readonly Vector3[] WalkRoute =
    {
        new(2f, 0.5f, 2f),
        new(8f, 0.8f, 0f),
        new(2f, 0.5f, -2f),
        new(-4f, 0.5f, 0f),
    };
    private static readonly string[] Routes =
    {
        "walk", "run", "yank", "sever-leg", "sever-arm-walk", "sever-both-legs",
        "sever-all", "attack", "sever-during-attack", "crawl-step", "traversal",
        "traversal-loop",
    };

    /// <summary>K 键的断肢演示顺序（爬行观感优先：先断腿看三肢爬，再逐级削弱）。</summary>
    private static readonly RatFiendLimbId[] SeverDemoOrder =
    {
        RatFiendLimbId.LegLeft, RatFiendLimbId.ArmLeft,
        RatFiendLimbId.LegRight, RatFiendLimbId.ArmRight,
    };

    [Export] public float GravityMps2 = 36f;
    [Export] public float CameraFlySpeed = 7f;
    [Export] public float CameraMouseSensitivity = 0.003f;

    private readonly RaycastTerrainQuery _terrain = new();
    private readonly RatFiendRenderer _renderer = new();
    private RatFiendFormalRenderer? _formal;
    private bool _formalView = true;
    private readonly DeterminismHasher _hasher = new();

    private RatFiendLocomotionController _rat = null!;
    private RatFiendParams[] _presets = Array.Empty<RatFiendParams>();
    private RatFiendParams _preset = null!;
    private RatFiendSandboxHud? _hud;
    private Camera3D _camera = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _lastCenter;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cameraFlying;
    private bool _pendingLaunch;
    private bool _pendingSeverNext;
    private bool _pendingMouthToggle;
    private bool _pendingClearTarget;
    private bool _pendingRespawn;
    private Vector2? _pendingTargetPick;
    private bool _rendererBuilt;
    private bool _fatal;
    private long _tick;

    // 无头专项入口。
    private int _determinismTicks;
    private int _requestedTps = 40;
    private string _presetName = "gaunt";
    private string _route = "walk";
    private float _perturb;
    private ulong? _expectHash;
    private string? _screenshotPath; // --ratfiend-screenshot=path[@tick]：截图后退出
    private long _screenshotTick = 120;

    // 路线状态与指标。
    private int _waypointIndex;
    private int _waypointsReached;
    private Vector3 _spawnCenter;
    private float _travelDistance;
    private float _endConstraintDeviation;
    private float _maxConstraintDeviation;
    private bool _nonFinite;
    private float _maxRunBlend;
    private float _runMouthSum;
    private int _runMouthSamples;
    private float _standChestY;
    private long _fellTick = -1;
    private bool _gravityAlwaysAfterSever = true;
    private int _maxGripsAfterSever;
    private float _crawlWindowStartTravel;
    private Vector3 _crawlWindowStartPos;
    private bool _crawlWindowStarted;
    private bool _patrolToB = true;
    private bool _stumpAlwaysDangle = true;
    private long _grabSetTick = -1;
    private long _reachTick = -1;
    private float _mouthPeak;
    private bool _handsClearAfterRelease = true;
    private long _releaseTick = -1;
    private bool _severedHandCleared = true;
    private long _severDuringAttackTick = -1;
    private bool _sawAirborne;
    private long _refootTick = -1;
    private float _recoverStartX;
    private float _travelAfterRecover;
    private float _lastRecoverX;
    private RatTraversalPhase _traversalPhase;
    private int _traversalPhaseChanges;
    private int _traversalStableTicks;
    private long _traversalCompleteTick = -1;
    private bool _traversalCrossedExit;
    private float _traversalMaxTickDisplacement;
    private float _traversalMaxResidualPenetration;
    private int _traversalMaxPlantedHands;
    private Vector3[] _traversalLastChunks = Array.Empty<Vector3>();
    private bool _traversalLoopForward = true;
    private int _traversalLoopDwell;
    private int _traversalLoops;
    private RatTraversalIntent? _autoVault;
    private Vector3 _autoVaultDir;
    private int _autoVaultTicks;
    private Vector3 _driveDir; // --ratfiend-drive：交互模式恒向驾驶（演示/验证钩子）

    private bool Deterministic => _determinismTicks > 0;

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _camera = GetNode<Camera3D>("Camera3D");
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        if (!ParseArguments())
        {
            _fatal = true;
            GD.Print("[RATFIEND-RESULT] FAIL：命令行参数无效");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
        {
            Engine.MaxPhysicsStepsPerFrame = 100;
        }

        _presets = RatFiendFactory.AllPresets();
        _preset = RatFiendFactory.ById($"ratfiend/{_presetName}");
        SpawnRat(SpawnForRoute(_route), ForwardForRoute(_route), _preset);

        if (_perturb != 0f)
        {
            _rat.Chest.Pos += Vector3.Right * _perturb;
            _rat.Chest.LastPos = _rat.Chest.Pos;
        }

        _renderer.Build(this, _rat);
        _rendererBuilt = true;
        RebuildFormalRenderer();

        if (!Deterministic)
        {
            string[] names = Array.ConvertAll(_presets, p => p.Id);
            _hud = new RatFiendSandboxHud();
            _hud.Build(this, names, SelectPreset);
            _hud.SyncPreset(Array.FindIndex(_presets, p => p.Id == _preset.Id));
        }

        GD.Print($"[RATFIEND-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.Id} route={_route} " +
                 $"determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnRat(Vector3 origin, Vector3 forward, RatFiendParams parameters)
    {
        _preset = parameters;
        _rat = RatFiendFactory.CreateController(origin, forward, parameters);
        _spawnCenter = BodyCenter();
        _lastCenter = _spawnCenter;
        _traversalPhase = RatTraversalPhase.Approach;
        _traversalPhaseChanges = 0;
        _traversalStableTicks = 0;
        _traversalCompleteTick = -1;
        _traversalCrossedExit = false;
        _traversalMaxTickDisplacement = 0f;
        _traversalMaxResidualPenetration = 0f;
        _traversalMaxPlantedHands = 0;
        _traversalLastChunks = new[] { _rat.Chest.Pos, _rat.Hips.Pos, _rat.Head.Pos };
        _traversalLoopForward = true;
        _traversalLoopDwell = 0;
        _traversalLoops = 0;
        _autoVault = null;
        if (_rendererBuilt)
        {
            _renderer.Build(this, _rat);
            RebuildFormalRenderer();
        }
    }

    private static string ShortPresetName(RatFiendParams p)
    {
        int slash = p.Id.IndexOf('/');
        return slash >= 0 ? p.Id[(slash + 1)..] : p.Id;
    }

    private void RebuildFormalRenderer()
    {
        _formal?.Clear();
        _formal = new RatFiendFormalRenderer(_rat, ShortPresetName(_preset));
        _formal.Build(this);
        ApplyRenderView();
    }

    private void ApplyRenderView()
    {
        bool formalOn = _formal is not null && _formalView;
        _renderer.SetVisible(!formalOn);
        _formal?.SetVisible(formalOn);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
        {
            return;
        }
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);

        if (Deterministic)
        {
            DriveRoute();
        }
        else if (_route == "traversal-loop")
        {
            // 演示路线在窗口模式也自跑：翻越意图接管移动（WASD 不参与），
            // 相机飞行 / K 断肢 / R 重生 / 1-4 预设热键照常可用。
            ProcessPendingInteraction();
            _rat.MoveTarget = null;
            _rat.TraversalIntent = null;
            _rat.MoveDir = Vector3.Zero;
            _rat.RunSpeed = 0f;
            DriveTraversalLoop();
        }
        else
        {
            SampleInput();
            ProcessPendingInteraction();
            MaybeAutoVault();
        }

        _rat.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        TrackMetrics();

        if (!Deterministic)
        {
            return;
        }
        RecordDeterminism();
        if (_tick >= _determinismTicks)
        {
            GetTree().Quit(DumpDeterministicResult());
        }
    }

    // ============================================================ 交互模式

    private void SampleInput()
    {
        if (WantCameraFly)
        {
            _rat.MoveDir = Vector3.Zero;
            _rat.RunSpeed = 0f;
            return;
        }
        Vector3 direction = _driveDir;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction.X += 1f;
        _rat.MoveTarget = null;
        _rat.MoveDir = direction.LengthSquared() > 1e-10f
            ? direction.Normalized()
            : Vector3.Zero;
        // Shift = 慢走（看驼背垂手摆臂），默认全速跑（看前伸抬头大张嘴）。
        _rat.RunSpeed = direction == Vector3.Zero
            ? 0f
            : Input.IsPhysicalKeyPressed(Key.Shift) ? 0.4f : 1f;
    }

    private void ProcessPendingInteraction()
    {
        if (_pendingTargetPick is Vector2 targetMouse)
        {
            _pendingTargetPick = null;
            Vector3 from = _camera.ProjectRayOrigin(targetMouse);
            Vector3 to = from + _camera.ProjectRayNormal(targetMouse) * 100f;
            if (_terrain.Raycast(from, to, out TerrainHit hit))
            {
                _rat.GrabTarget = hit.Point + new Vector3(0f, 0.5f, 0f);
                GD.Print("[RATFIEND-SANDBOX] grab target set");
            }
        }
        if (_pendingClearTarget)
        {
            _pendingClearTarget = false;
            _rat.GrabTarget = null;
        }
        if (_pendingMouthToggle)
        {
            _pendingMouthToggle = false;
            _rat.MouthDrive = _rat.MouthDrive > 0.5f ? 0f : 1f;
            GD.Print($"[RATFIEND-SANDBOX] mouth drive = {_rat.MouthDrive}");
        }
        if (_pendingSeverNext)
        {
            _pendingSeverNext = false;
            foreach (RatFiendLimbId id in SeverDemoOrder)
            {
                if (!_rat.IsSevered(id))
                {
                    _rat.Sever(id, _rat.Facing * -0.04f);
                    GD.Print($"[RATFIEND-SANDBOX] severed {id}（K 演示序）");
                    break;
                }
            }
        }
        if (_pendingLaunch)
        {
            _pendingLaunch = false;
            _rat.Launch(_rat.Facing * 0.1f + Vector3.Up * 0.3f);
        }
        if (_pendingRespawn)
        {
            _pendingRespawn = false;
            SpawnRat(SpawnForRoute(_route), ForwardForRoute(_route), _preset);
            GD.Print("[RATFIEND-SANDBOX] respawn（断肢复原）");
        }
    }

    /// <summary>交互模式的自动翻越检测（≙ 主仓 RatFiendTraversalExecutor 的沙盒极简替身）：
    /// 驾驶方向 0.9m 内探到竖直立面、且立面之后是高出脚下 0.35~1.15m（普通高抬步之外、
    /// 主仓 CrossObstacle MaxRise 之内）的可站顶面时，合成 MountAndCross 意图——内核接管
    /// 导向并触发 R22 手撑。到点 / 用户转向背离 / 4s 看门狗即撤销还给 WASD。只在交互
    /// 模式运行，不进任何确定性路线。</summary>
    private void MaybeAutoVault()
    {
        _rat.TraversalIntent = null;
        Vector3 wish = _rat.MoveDir;
        if (_autoVault is { } active)
        {
            _autoVaultTicks++;
            bool steeredAway = wish != Vector3.Zero && wish.Dot(_autoVaultDir) < 0.2f;
            if (_rat.AtTraversalTarget || steeredAway || _autoVaultTicks > 160)
            {
                _autoVault = null;
            }
            else
            {
                // 翻越是承诺动作：接管期间恒满油（≙ 主仓执行器 RunSpeed=1），
                // 相机飞行把 RunSpeed 归零也不中断，靠看门狗兜底。
                _rat.TraversalIntent = active;
                _rat.RunSpeed = 1f;
                return;
            }
        }
        if (wish == Vector3.Zero || _rat.RunSpeed <= 0f)
        {
            return;
        }

        Vector3 up = Vector3.Up;
        Vector3 fwd = wish.Normalized();
        Vector3 chest = _rat.Chest.Pos;
        if (!_terrain.Raycast(chest, chest - up * 2f, out TerrainHit floorHit))
        {
            return; // 悬空/出界：无脚下基准
        }
        float floorY = floorHit.Point.Y;
        // 立面探测高度 0.45m：高于 WalkStepMaxRise（0.35）——普通台阶不触发。
        Vector3 from = new(chest.X, floorY + 0.45f, chest.Z);
        if (!_terrain.Raycast(from, from + fwd * 0.9f, out TerrainHit wall)
            || Mathf.Abs(wall.Normal.Dot(up)) > 0.4f)
        {
            return;
        }
        // 立面之后探顶面：起点高于最大可翻高度，法线必须朝上（斜坡/内壁拒收）。
        Vector3 overTop = new(wall.Point.X + fwd.X * 0.25f, floorY + 1.6f,
            wall.Point.Z + fwd.Z * 0.25f);
        if (!_terrain.Raycast(overTop, overTop - up * 1.55f, out TerrainHit top)
            || top.Normal.Dot(up) < 0.5f)
        {
            return;
        }
        float rise = top.Point.Y - floorY;
        if (rise is < 0.35f or > 1.15f)
        {
            return;
        }
        Vector3 guide = wall.Point + fwd * 0.9f;
        _autoVault = new RatTraversalIntent(RatTraversalPhase.MountAndCross,
            new Vector3(guide.X, top.Point.Y, guide.Z));
        _autoVaultDir = fwd;
        _autoVaultTicks = 0;
        _rat.TraversalIntent = _autoVault;
        _rat.RunSpeed = 1f;
        GD.Print($"[RATFIEND-SANDBOX] auto-vault rise={rise:F2}m top={top.Point.Y:F2} " +
                 $"guide=({guide.X:F2},{guide.Z:F2}) tick={_tick}");
    }

    // ============================================================ 无头路线

    private void DriveRoute()
    {
        _rat.MoveTarget = null;
        _rat.TraversalIntent = null;
        _rat.MoveDir = Vector3.Zero;
        _rat.RunSpeed = 0f;
        switch (_route)
        {
            case "walk":
                DriveWalk();
                break;
            case "run":
                DrivePatrol(new Vector3(-14f, 0.5f, 6f), new Vector3(14f, 0.5f, 6f), 1f);
                break;
            case "yank":
                DriveYank();
                break;
            case "sever-leg":
                DriveSeverLeg();
                break;
            case "sever-arm-walk":
                DriveSeverArmWalk();
                break;
            case "sever-both-legs":
                DriveSeverBothLegs();
                break;
            case "sever-all":
                DriveSeverAll();
                break;
            case "attack":
            case "sever-during-attack":
                DriveAttack();
                break;
            case "crawl-step":
                DriveCrawlStep();
                break;
            case "traversal":
                DriveTraversal();
                break;
            case "traversal-loop":
                DriveTraversalLoop();
                break;
        }
    }

    private void DriveTraversal()
    {
        if (_traversalCompleteTick < 0 && _rat.AtTraversalTarget)
        {
            if (_traversalPhase == RatTraversalPhase.Stabilize)
            {
                _traversalStableTicks++;
                if (_traversalStableTicks >= 8)
                {
                    _traversalCompleteTick = _tick;
                    _waypointsReached = 1;
                }
            }
            else
            {
                _traversalPhase = (RatTraversalPhase)((int)_traversalPhase + 1);
                _traversalPhaseChanges++;
                _traversalStableTicks = 0;
            }
        }
        else if (_traversalCompleteTick < 0
            && _traversalPhase == RatTraversalPhase.Stabilize)
        {
            _traversalStableTicks = 0;
        }

        Vector3 target = _traversalPhase switch
        {
            RatTraversalPhase.Approach => new Vector3(-0.65f, 0f, 12f),
            RatTraversalPhase.MountAndCross => new Vector3(0.75f, 0.82f, 12f),
            _ => new Vector3(2.2f, 0f, 12f),
        };
        _rat.TraversalIntent = new RatTraversalIntent(_traversalPhase, target);
        _rat.RunSpeed = _traversalCompleteTick < 0 ? 1f : 0f;
    }

    /// <summary>演示专用（不进矩阵）：桌子两侧往返翻越。相位机与 traversal 相同，
    /// 完成一趟后原地站 1.2s 再折返——手撑窗口每 ~6s 重现一次，肉眼可反复观察；
    /// 三段目标按桌心 x=0.5 镜像。窗口模式无需 --ratfiend-determinism 即自跑。</summary>
    private void DriveTraversalLoop()
    {
        if (_traversalLoopDwell > 0)
        {
            _traversalLoopDwell--;
            if (_traversalLoopDwell == 0)
            {
                _traversalLoopForward = !_traversalLoopForward;
                _traversalPhase = RatTraversalPhase.Approach;
            }
            return; // 意图已清空 → 原地站立
        }

        if (_rat.AtTraversalTarget)
        {
            if (_traversalPhase == RatTraversalPhase.Stabilize)
            {
                _traversalStableTicks++;
                if (_traversalStableTicks >= 8)
                {
                    _traversalLoops++;
                    _waypointsReached++;
                    _traversalStableTicks = 0;
                    _traversalLoopDwell = 48;
                    return;
                }
            }
            else
            {
                _traversalPhase = (RatTraversalPhase)((int)_traversalPhase + 1);
                _traversalPhaseChanges++;
                _traversalStableTicks = 0;
            }
        }
        else if (_traversalPhase == RatTraversalPhase.Stabilize)
        {
            _traversalStableTicks = 0;
        }

        float sign = _traversalLoopForward ? 1f : -1f;
        Vector3 target = _traversalPhase switch
        {
            RatTraversalPhase.Approach => new Vector3(0.5f - 1.15f * sign, 0f, 12f),
            RatTraversalPhase.MountAndCross => new Vector3(0.5f + 0.25f * sign, 0.82f, 12f),
            _ => new Vector3(0.5f + 1.7f * sign, 0f, 12f),
        };
        _rat.TraversalIntent = new RatTraversalIntent(_traversalPhase, target);
        _rat.RunSpeed = 1f;
    }

    private void DriveWalk()
    {
        Vector3 target = WalkRoute[_waypointIndex];
        _rat.MoveTarget = target;
        _rat.RunSpeed = 0.6f;
        Vector3 horizontal = target - BodyCenter();
        horizontal.Y = 0f;
        if (_rat.AtMoveTarget || horizontal.Length() < 0.5f)
        {
            _waypointIndex = (_waypointIndex + 1) % WalkRoute.Length;
            _waypointsReached++;
            _rat.MoveTarget = WalkRoute[_waypointIndex];
        }
    }

    /// <summary>有界巡逻（两点乒乓）：平地带宽有限，长路线不能用恒向 MoveDir 直线跑。</summary>
    private void DrivePatrol(Vector3 a, Vector3 b, float runSpeed)
    {
        Vector3 target = _patrolToB ? b : a;
        _rat.MoveTarget = target;
        _rat.RunSpeed = runSpeed;
        Vector3 horizontal = target - BodyCenter();
        horizontal.Y = 0f;
        if (_rat.AtMoveTarget || horizontal.Length() < 0.6f)
        {
            _patrolToB = !_patrolToB;
            _waypointsReached++;
        }
    }

    private void DriveYank()
    {
        DrivePatrol(new Vector3(-12f, 0.5f, -4f), new Vector3(12f, 0.5f, -4f), 0.7f);
        if (_tick == 600)
        {
            _rat.Launch(new Vector3(0.15f, 0.3f, 0.05f));
        }
        if (_tick > 600)
        {
            if (!_sawAirborne && _rat.ApplyGravity && !_rat.Grounded)
            {
                _sawAirborne = true;
            }
            if (_sawAirborne && _refootTick < 0 && _rat.Grounded && !_rat.ApplyGravity)
            {
                _refootTick = _tick;
                _recoverStartX = _rat.Chest.Pos.X;
                _lastRecoverX = _recoverStartX;
            }
            if (_refootTick > 0)
            {
                // 累计行走里程，不取末刻位移：巡逻乒乓下末刻位移是周期敏感量——
                // R11 速度分离改变巡逻节奏后，路线末端恰好折返回落点附近（|Δ|=1.46m）
                // 曾把正常恢复（总里程 87m）误判成未恢复。
                _travelAfterRecover += Mathf.Abs(_rat.Chest.Pos.X - _lastRecoverX);
                _lastRecoverX = _rat.Chest.Pos.X;
            }
        }
    }

    private void DriveSeverLeg()
    {
        DrivePatrol(new Vector3(-14f, 0.5f, -8f), new Vector3(14f, 0.5f, -8f),
            _tick <= 500 ? 0.7f : 1f);
        if (_tick == 499)
        {
            _standChestY = _rat.Chest.Pos.Y;
        }
        if (_tick == 500)
        {
            _rat.Sever(RatFiendLimbId.LegRight);
        }
        if (_tick == 700)
        {
            BeginCrawlWindow();
        }
    }

    /// <summary>爬行翻台阶路线：目标点在 0.3m 台阶面上（x∈[3,10]）——断腿爬行必须
    /// 手拉体升翻上台阶才能到点（内核 CRAWL-STEP 门的引擎侧对照，验证 Godot 射线
    /// HitFromInside 语义与解析地形一致）。到点只记一次，之后原地趴在台面上。</summary>
    private void DriveCrawlStep()
    {
        Vector3 target = _tick <= 500 ? new Vector3(-1f, 0.5f, 0f) : new Vector3(8f, 0.8f, 0f);
        _rat.MoveTarget = target;
        _rat.RunSpeed = _tick <= 500 ? 0.6f : 1f;
        if (_tick == 499)
        {
            _standChestY = _rat.Chest.Pos.Y;
        }
        if (_tick == 500)
        {
            _rat.Sever(RatFiendLimbId.LegRight);
        }
        if (_tick == 700)
        {
            BeginCrawlWindow();
        }
        Vector3 horizontal = target - BodyCenter();
        horizontal.Y = 0f;
        if (_waypointsReached == 0 && _tick > 500
            && (_rat.AtMoveTarget || horizontal.Length() < 0.6f))
        {
            _waypointsReached++;
        }
    }

    private void DriveSeverArmWalk()
    {
        DrivePatrol(new Vector3(-14f, 0.5f, 8f), new Vector3(14f, 0.5f, 8f), 0.7f);
        if (_tick == 300)
        {
            _rat.Sever(RatFiendLimbId.ArmLeft);
        }
        if (_tick == 400)
        {
            BeginCrawlWindow();
        }
        if (_tick > 305)
        {
            _stumpAlwaysDangle &= _rat.Arms[0].Mode == RatArm.ArmMode.Dangle;
        }
    }

    private void DriveSeverBothLegs()
    {
        DrivePatrol(new Vector3(-14f, 0.5f, -8f), new Vector3(14f, 0.5f, -8f),
            _tick <= 400 ? 0.7f : 1f);
        if (_tick == 400)
        {
            _standChestY = _rat.Chest.Pos.Y;
            _rat.Sever(RatFiendLimbId.LegLeft);
        }
        if (_tick == 700)
        {
            _rat.Sever(RatFiendLimbId.LegRight);
        }
        if (_tick == 900)
        {
            BeginCrawlWindow();
        }
    }

    private void DriveSeverAll()
    {
        // 全断后推进归零，MoveDir 恒向不会走出地板——蠕动原地挣扎正是断言对象。
        _rat.MoveDir = Vector3.Right;
        _rat.RunSpeed = _tick <= 300 ? 0f : 1f;
        switch (_tick)
        {
            case 300:
                _rat.Sever(RatFiendLimbId.LegLeft);
                break;
            case 320:
                _rat.Sever(RatFiendLimbId.LegRight);
                break;
            case 340:
                _rat.Sever(RatFiendLimbId.ArmLeft);
                break;
            case 360:
                _rat.Sever(RatFiendLimbId.ArmRight);
                break;
            case 600:
                BeginCrawlWindow();
                break;
        }
    }

    private void BeginCrawlWindow()
    {
        _crawlWindowStarted = true;
        _crawlWindowStartTravel = _travelDistance;
        _crawlWindowStartPos = _rat.Chest.Pos;
    }

    /// <summary>攻击接缝路线（宿主脚本 = 竞技场时序的确定性替身）：站定 → 喂 GrabTarget →
    /// 双手到位 → MouthDrive 咬合脉冲 → 清目标放开；sever-during-attack 变体在到位后
    /// 断掉执行臂之一，断臂手的「抓住」观测必须立即消失（竞技场并发规则的内核侧保证）。</summary>
    private void DriveAttack()
    {
        Vector3 target = new(-2.0f, 0.9f, 0f);
        if (_tick == 200)
        {
            _grabSetTick = _tick;
            _rat.GrabTarget = target;
        }
        if (_grabSetTick > 0 && _reachTick < 0
            && _rat.HandsOnTarget[0] && _rat.HandsOnTarget[1])
        {
            _reachTick = _tick;
        }
        if (_reachTick > 0)
        {
            if (_route == "sever-during-attack" && _severDuringAttackTick < 0
                && _tick == _reachTick + 5)
            {
                _severDuringAttackTick = _tick;
                _rat.Sever(RatFiendLimbId.ArmRight);
            }
            if (_severDuringAttackTick > 0 && _tick > _severDuringAttackTick)
            {
                _severedHandCleared &= !_rat.HandsOnTarget[1];
            }
            _rat.MouthDrive = _tick >= _reachTick + 20 && _tick < _reachTick + 40 ? 1f : 0f;
            if (_tick == _reachTick + 60)
            {
                _releaseTick = _tick;
                _rat.GrabTarget = null;
            }
        }
        if (_releaseTick > 0 && _tick > _releaseTick + 20)
        {
            _handsClearAfterRelease &= !_rat.HandsOnTarget[0] && !_rat.HandsOnTarget[1];
            // 放开后有界巡逻走开（断臂变体顺带验证断臂行走；恒向 MoveDir 会走出地板边坠落）。
            DrivePatrol(new Vector3(-12f, 0.5f, 0f), new Vector3(12f, 0.5f, 0f), 0.7f);
        }
    }

    // ============================================================ 指标

    private void TrackMetrics()
    {
        Vector3 center = BodyCenter();
        _travelDistance += center.DistanceTo(_lastCenter);
        _lastCenter = center;
        _endConstraintDeviation = _rat.Body.CurrentMaxDeviation();
        _maxConstraintDeviation = Mathf.Max(_maxConstraintDeviation, _endConstraintDeviation);
        _maxRunBlend = Mathf.Max(_maxRunBlend, _rat.RunBlend);
        _mouthPeak = Mathf.Max(_mouthPeak, _rat.MouthOpen);

        _nonFinite |= !IsFinite(_rat.Facing);
        foreach (BodyChunk chunk in _rat.Body.Chunks)
        {
            _nonFinite |= !IsFinite(chunk.Pos) || !IsFinite(chunk.Vel);
        }
        foreach (var leg in _rat.Legs)
        {
            _nonFinite |= !IsFinite(leg.Pos) || !IsFinite(leg.Vel);
        }
        foreach (RatArm arm in _rat.Arms)
        {
            _nonFinite |= !IsFinite(arm.Pos) || !IsFinite(arm.Vel);
        }

        if (_route == "run" && _tick is > 500 and <= 700)
        {
            _runMouthSum += _rat.MouthOpen;
            _runMouthSamples++;
        }
        if (_rat.SeveredLimbCount > 0)
        {
            _gravityAlwaysAfterSever &= _rat.ApplyGravity || _rat.CanStand;
            _maxGripsAfterSever = Math.Max(_maxGripsAfterSever, _rat.CrawlGripCount);
            if (_fellTick < 0 && _standChestY > 0f
                && _rat.Chest.Pos.Y < _standChestY * 0.55f)
            {
                _fellTick = _tick;
            }
        }

        if (_route is "traversal" or "traversal-loop")
        {
            BodyChunk[] chunks = { _rat.Chest, _rat.Hips, _rat.Head };
            for (int i = 0; i < chunks.Length; i++)
            {
                _traversalMaxTickDisplacement = Mathf.Max(_traversalMaxTickDisplacement,
                    chunks[i].Pos.DistanceTo(_traversalLastChunks[i]));
                _traversalLastChunks[i] = chunks[i].Pos;
                if (_terrain.SpherePenetration(chunks[i].Pos, chunks[i].TerrainRadius,
                        out _, out float depth))
                {
                    _traversalMaxResidualPenetration = Mathf.Max(
                        _traversalMaxResidualPenetration, depth);
                }
            }
            _traversalCrossedExit |= _rat.Chest.Pos.X > 1.15f && _rat.Hips.Pos.X > 1.15f;
            if (_traversalPhase == RatTraversalPhase.MountAndCross)
            {
                // R22 翻越手撑：Mount 期撑稳手数峰值（完好路线必须两手都撑上桌面）。
                _traversalMaxPlantedHands = Math.Max(_traversalMaxPlantedHands,
                    _rat.PlantedHandCount);
            }
        }
    }

    private void RecordDeterminism()
    {
        _rat.FoldState(_hasher);
        if (_route == "traversal")
        {
            _hasher.Fold((int)_traversalPhase);
            _hasher.Fold(_traversalStableTicks);
        }
        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[RATFIEND-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
    }

    private int DumpDeterministicResult()
    {
        float crawlTravel = _crawlWindowStarted ? _travelDistance - _crawlWindowStartTravel : 0f;
        Vector3 windowDrift = _crawlWindowStarted ? _rat.Chest.Pos - _crawlWindowStartPos : default;
        float wriggleDrift = new Vector3(windowDrift.X, 0f, windowDrift.Z).Length();
        GD.Print($"[RATFIEND-METRIC] route={_route} preset={_preset.Id} " +
                 $"travel={_travelDistance:F3}m waypoints={_waypointsReached} " +
                 $"endDev={_endConstraintDeviation:F5}m maxDev={_maxConstraintDeviation:F5}m " +
                 $"maxRunBlend={_maxRunBlend:F2} " +
                 $"runMouth={(_runMouthSamples > 0 ? _runMouthSum / _runMouthSamples : 0f):F2} " +
                 $"severs={_rat.SeverSerial} fell={_fellTick} " +
                 $"gravAfterSever={_gravityAlwaysAfterSever} maxGrips={_maxGripsAfterSever} " +
                 $"crawlTravel={crawlTravel:F2} stumpDangle={_stumpAlwaysDangle} " +
                 $"attack={_grabSetTick}/{_reachTick}/{_releaseTick} mouthPeak={_mouthPeak:F2} " +
                 $"handsClear={_handsClearAfterRelease} severedHandClear={_severedHandCleared} " +
                 $"yank={_refootTick}/{_travelAfterRecover:F2} " +
                 $"traversal={_traversalPhaseChanges}/{_traversalCompleteTick}/" +
                 $"{_traversalStableTicks} exit={_traversalCrossedExit} " +
                 $"maxStep={_traversalMaxTickDisplacement:F4}m " +
                 $"penetration={_traversalMaxResidualPenetration:E3}m " +
                 $"planted={_traversalMaxPlantedHands} loops={_traversalLoops}");

        var failures = new List<string>();
        if (_nonFinite)
        {
            failures.Add("状态出现 NaN/Inf");
        }
        if (_expectHash is ulong expected && _hasher.Value != expected)
        {
            failures.Add($"hash {_hasher.Value:X16} != {expected:X16}");
        }
        // 断肢摔落的着地冲击有一次性约束偏差（下 tick 松弛收回）；末态必须收敛。
        if (_endConstraintDeviation >= 0.06f)
        {
            failures.Add($"连接末偏差 {_endConstraintDeviation:F5}m >= 0.06m");
        }

        switch (_route)
        {
            case "walk":
                if (_waypointsReached < 8 || _travelDistance < 25f)
                {
                    failures.Add($"巡走覆盖不足（wp={_waypointsReached}, travel={_travelDistance:F1}）");
                }
                break;
            case "run":
                float runMouth = _runMouthSamples > 0 ? _runMouthSum / _runMouthSamples : 0f;
                if (_travelDistance < 60f || _maxRunBlend < 0.9f || runMouth < 0.6f)
                {
                    failures.Add($"跑步未达标（travel={_travelDistance:F1}, " +
                                 $"blend={_maxRunBlend:F2}, mouth={runMouth:F2}）");
                }
                break;
            case "yank":
                if (!_sawAirborne || _refootTick < 0 || _refootTick > 600 + 300
                    || _travelAfterRecover < 5f)
                {
                    failures.Add($"击飞恢复未达标（airborne={_sawAirborne}, " +
                                 $"refoot={_refootTick}, travel={_travelAfterRecover:F2}）");
                }
                break;
            case "sever-leg":
                if (_rat.SeverSerial != 1 || _fellTick is < 0 or > 600
                    || !_gravityAlwaysAfterSever || _maxGripsAfterSever < 3
                    || crawlTravel < 5f)
                {
                    failures.Add($"断腿爬行未达标（fell={_fellTick}, " +
                                 $"grips={_maxGripsAfterSever}, crawl={crawlTravel:F2}）");
                }
                break;
            case "crawl-step":
                if (_rat.SeverSerial != 1 || _fellTick is < 0 or > 600
                    || _maxGripsAfterSever < 3 || _waypointsReached < 1
                    || _rat.Chest.Pos.Y < 0.35f)
                {
                    failures.Add($"爬行翻台阶未达标（fell={_fellTick}, " +
                                 $"grips={_maxGripsAfterSever}, wp={_waypointsReached}, " +
                                 $"chestY={_rat.Chest.Pos.Y:F2}）");
                }
                break;
            case "sever-arm-walk":
                if (_rat.SeverSerial != 1 || !_stumpAlwaysDangle || crawlTravel < 15f
                    || !_rat.CanStand)
                {
                    failures.Add($"断臂行走未达标（dangle={_stumpAlwaysDangle}, " +
                                 $"travel={crawlTravel:F2}, canStand={_rat.CanStand}）");
                }
                break;
            case "sever-both-legs":
                if (_rat.SeverSerial != 2 || _fellTick is < 0 or > 500
                    || _maxGripsAfterSever < 2 || crawlTravel < 3f)
                {
                    failures.Add($"双断腿爬行未达标（fell={_fellTick}, " +
                                 $"grips={_maxGripsAfterSever}, crawl={crawlTravel:F2}）");
                }
                break;
            case "sever-all":
                if (_rat.SeverSerial != 4 || wriggleDrift > 0.6f)
                {
                    failures.Add($"全断蠕动未达标（severs={_rat.SeverSerial}, " +
                                 $"drift={wriggleDrift:F2}）");
                }
                break;
            case "attack":
                if (_reachTick < 0 || _reachTick > _grabSetTick + 300 || _mouthPeak < 0.95f
                    || !_handsClearAfterRelease)
                {
                    failures.Add($"攻击接缝未达标（reach={_reachTick}, " +
                                 $"mouth={_mouthPeak:F2}, handsClear={_handsClearAfterRelease}）");
                }
                break;
            case "sever-during-attack":
                if (_reachTick < 0 || _severDuringAttackTick < 0 || !_severedHandCleared
                    || _rat.SeverSerial != 1 || !_handsClearAfterRelease)
                {
                    failures.Add($"攻击中断臂未达标（reach={_reachTick}, " +
                                 $"sever={_severDuringAttackTick}, " +
                                 $"severedHandClear={_severedHandCleared}）");
                }
                break;
            case "traversal":
                if (_traversalPhaseChanges != 2 || _traversalCompleteTick < 0
                    || _traversalStableTicks < 8 || !_traversalCrossedExit
                    || _traversalMaxTickDisplacement >= 0.25f
                    || _traversalMaxResidualPenetration >= 0.01f
                    || _traversalMaxPlantedHands != 2)
                {
                    failures.Add($"翻越未达标（phases={_traversalPhaseChanges}, " +
                                 $"done={_traversalCompleteTick}, stable={_traversalStableTicks}, " +
                                 $"exit={_traversalCrossedExit}, " +
                                 $"step={_traversalMaxTickDisplacement:F4}, " +
                                 $"penetration={_traversalMaxResidualPenetration:E3}, " +
                                 $"planted={_traversalMaxPlantedHands}）");
                }
                break;
            case "traversal-loop":
                // 演示路线的确定性自检：至少往返各一趟，且每趟 Mount 都双手撑稳。
                if (_traversalLoops < 2 || _traversalMaxPlantedHands != 2)
                {
                    failures.Add($"往返翻越未达标（loops={_traversalLoops}, " +
                                 $"planted={_traversalMaxPlantedHands}）");
                }
                break;
        }

        bool pass = failures.Count == 0;
        GD.Print(pass
            ? "[RATFIEND-RESULT] PASS"
            : $"[RATFIEND-RESULT] FAIL：{string.Join("; ", failures)}");
        return pass ? 0 : 1;
    }

    // ============================================================ 摄像机与输入

    public override void _Process(double delta)
    {
        if (_fatal)
        {
            return;
        }
        if (Deterministic)
        {
            UpdateShowcaseCamera();
        }
        else if (_driveDir != Vector3.Zero && !WantCameraFly)
        {
            UpdateShowcaseCamera(); // 恒向驾驶演示：解放键盘时顺带跟拍
        }
        else
        {
            UpdateCameraFly((float)delta);
        }
        float alpha = (float)Engine.GetPhysicsInterpolationFraction();
        if (_rendererBuilt)
        {
            _renderer.Draw(alpha);
            _formal?.Draw(alpha, (float)delta);
        }
        MaybeCaptureScreenshot();
        _hud?.UpdateStatus(
            $"preset  {_preset.Id}\n" +
            $"gait  {_rat.Gait:F2} (r={_rat.RunBlend:F2})   mouth  {_rat.MouthOpen:F2}\n" +
            $"grounded  {_rat.Grounded} ({_rat.GroundedCounter})   " +
            $"gravity  {_rat.ApplyGravity}   upright  {_rat.Uprightness:F2}\n" +
            $"crawl  {_rat.Crawling} f={_rat.CrawlFactor:F2}   grips  {_rat.CrawlGripCount}\n" +
            $"severed  {LimbFlag(RatFiendLimbId.ArmLeft)}{LimbFlag(RatFiendLimbId.ArmRight)}" +
            $"{LimbFlag(RatFiendLimbId.LegLeft)}{LimbFlag(RatFiendLimbId.LegRight)}   " +
            $"serial  {_rat.SeverSerial}\n" +
            $"grab  {(_rat.GrabTarget is not null ? "on" : "off")}   " +
            $"hands  {(_rat.HandsOnTarget[0] ? "L" : "-")}{(_rat.HandsOnTarget[1] ? "R" : "-")}   " +
            $"vault  {(_autoVault is not null ? $"on planted={_rat.PlantedHandCount}" : "off")}");
    }

    private string LimbFlag(RatFiendLimbId id) => _rat.IsSevered(id) ? "X" : "o";

    private void UpdateShowcaseCamera()
    {
        Vector3 center = BodyCenter();
        Vector3 offset = _route switch
        {
            "sever-leg" or "sever-both-legs" or "sever-all" or "crawl-step"
                => new Vector3(1.6f, 0.9f, 2.4f),
            "attack" or "sever-during-attack" => new Vector3(1.4f, 0.8f, 2.2f),
            "traversal" or "traversal-loop" => new Vector3(1.8f, 1.2f, 3.2f),
            _ => new Vector3(2.0f, 1.2f, 2.8f),
        };
        _camera.GlobalPosition = center + offset;
        _camera.Fov = 45f;
        _camera.LookAt(center, Vector3.Up);
    }

    private bool WantCameraFly => Input.IsMouseButtonPressed(MouseButton.Right);

    private void UpdateCameraFly(float delta)
    {
        if (Deterministic)
        {
            return;
        }
        bool flying = WantCameraFly;
        if (flying != _cameraFlying)
        {
            Input.MouseMode = flying
                ? Input.MouseModeEnum.Captured
                : Input.MouseModeEnum.Visible;
            _cameraFlying = flying;
        }
        if (!flying)
        {
            return;
        }
        Basis basis = _camera.GlobalTransform.Basis;
        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction -= basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction += basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction -= basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction += basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction -= Vector3.Up;
        if (direction.LengthSquared() > 1e-10f)
        {
            _camera.GlobalPosition += direction.Normalized() * CameraFlySpeed * delta;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            if (WantCameraFly)
            {
                _cameraYaw -= motion.Relative.X * CameraMouseSensitivity;
                _cameraPitch = Mathf.Clamp(
                    _cameraPitch - motion.Relative.Y * CameraMouseSensitivity, -1.5f, 1.5f);
                _camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0f);
            }
            return;
        }
        if (Deterministic)
        {
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        switch (key.PhysicalKeycode)
        {
            case Key.V:
                _formalView = !_formalView;
                ApplyRenderView();
                break;
            case Key.K:
                _pendingSeverNext = true;
                break;
            case Key.B:
                _pendingLaunch = true;
                break;
            case Key.M:
                _pendingMouthToggle = true;
                break;
            case Key.T:
                _pendingTargetPick = GetViewport().GetMousePosition();
                break;
            case Key.Y:
                _pendingClearTarget = true;
                break;
            case Key.R:
                _pendingRespawn = true;
                break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4:
                SelectPreset((int)(key.PhysicalKeycode - Key.Key1));
                break;
        }
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Length)
        {
            return;
        }
        Vector3 respawn = BodyCenter() + Vector3.Up * 0.3f;
        SpawnRat(respawn, _rat.Facing, _presets[index]);
        _hud?.SyncPreset(index);
        GD.Print($"[RATFIEND-SANDBOX] preset -> {_presets[index].Id}");
    }

    // ============================================================ CLI

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--ratfiend-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--ratfiend-determinism=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0)
                    {
                        throw new FormatException("determinism tick 必须为正");
                    }
                }
                else if (argument.StartsWith("--ratfiend-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(argument["--ratfiend-tps=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_requestedTps <= 0)
                    {
                        throw new FormatException("tps 必须为正");
                    }
                }
                else if (argument.StartsWith("--ratfiend-preset=", StringComparison.Ordinal))
                {
                    _presetName = argument["--ratfiend-preset=".Length..].ToLowerInvariant();
                    if (_presetName is not ("gaunt" or "dusk" or "broad" or "whelp"))
                    {
                        throw new FormatException("preset 只接受 gaunt|dusk|broad|whelp");
                    }
                }
                else if (argument.StartsWith("--ratfiend-route=", StringComparison.Ordinal))
                {
                    _route = argument["--ratfiend-route=".Length..].ToLowerInvariant();
                    if (Array.IndexOf(Routes, _route) < 0)
                    {
                        throw new FormatException($"未知 ratfiend route：{_route}");
                    }
                }
                else if (argument.StartsWith("--ratfiend-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(argument["--ratfiend-perturb=".Length..],
                        CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb))
                    {
                        throw new FormatException("perturb 必须有限");
                    }
                }
                else if (argument.StartsWith("--ratfiend-drive=", StringComparison.Ordinal))
                {
                    _driveDir = argument["--ratfiend-drive=".Length..].ToLowerInvariant() switch
                    {
                        "+x" => Vector3.Right,
                        "-x" => Vector3.Left,
                        "+z" => Vector3.Back,
                        "-z" => Vector3.Forward,
                        _ => throw new FormatException("drive 只接受 +x|-x|+z|-z"),
                    };
                }
                else if (argument == "--ratfiend-formal=off")
                {
                    _formalView = false;
                }
                else if (argument.StartsWith("--ratfiend-screenshot=", StringComparison.Ordinal))
                {
                    string spec = argument["--ratfiend-screenshot=".Length..];
                    int at = spec.LastIndexOf('@');
                    if (at > 0)
                    {
                        _screenshotPath = spec[..at];
                        _screenshotTick = long.Parse(spec[(at + 1)..],
                            CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        _screenshotPath = spec;
                    }
                }
                else if (argument.StartsWith("--ratfiend-expect-hash=", StringComparison.Ordinal))
                {
                    string text = argument["--ratfiend-expect-hash=".Length..];
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[2..];
                    }
                    _expectHash = ulong.Parse(text, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture);
                }
                else if (argument.StartsWith("--ratfiend-", StringComparison.Ordinal))
                {
                    throw new FormatException($"未知参数 {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                GD.PushError($"[RATFIEND-CLI] {argument}: {exception.Message}");
                return false;
            }
        }
        return true;
    }

    private static Vector3 SpawnForRoute(string route) => route switch
    {
        "run" => new Vector3(-14f, 0.5f, 6f),
        "yank" => new Vector3(-12f, 0.5f, -4f),
        "sever-leg" or "sever-both-legs" => new Vector3(-14f, 0.5f, -8f),
        "sever-arm-walk" => new Vector3(-14f, 0.5f, 8f),
        "crawl-step" => new Vector3(-6f, 0.5f, 0f),
        "traversal" or "traversal-loop" => new Vector3(-2.4f, 0.5f, 12f),
        _ => new Vector3(-3f, 0.5f, 0f),
    };

    private static Vector3 ForwardForRoute(string route) => Vector3.Right;

    private void MaybeCaptureScreenshot()
    {
        if (_screenshotPath is null || _tick < _screenshotTick)
        {
            return;
        }
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_screenshotPath);
        GD.Print($"[RATFIEND-SANDBOX] screenshot " +
                 $"{(err == Error.Ok ? "saved" : $"FAILED ({err})")}: " +
                 $"{_screenshotPath} (tick {_tick})");
        _screenshotPath = null;
        GetTree().Quit(err == Error.Ok ? 0 : 3);
    }

    private Vector3 BodyCenter() =>
        (_rat.Chest.Pos + _rat.Hips.Pos + _rat.Head.Pos) / 3f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
