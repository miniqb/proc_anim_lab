using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DropBug;
using ProcAnim.Core.Terrain;

namespace ProcAnimLab.DropBugSandbox;

/// <summary>
/// 独立 DropBug 白盒：40 tick 固定序驱动核心，渲染帧只做插值与自由摄像机。
/// 交互与无头专项矩阵都只经过本场景，不改其它物种沙盒。
/// 地形分区：平地巡走 + 0.3m 台阶 + 卡住墙 + 悬挂区（台柱 + 3.4m 顶）+
/// 8m 俯冲塔 + 18° 坡道，各路线互不交叠。
/// </summary>
public partial class DropBugSandboxWorld : Node3D
{
    private const float TickDt = 0.025f;
    private static readonly Vector3[] WalkRoute =
    {
        new(2f, 0.3f, 2f),
        new(8f, 0.65f, 0f),
        new(2f, 0.3f, -2f),
        new(-4f, 0.3f, 0f),
    };
    private static readonly string[] Routes =
    {
        "walk", "slope", "hop", "stuck", "backward", "hang", "hang-exit", "hang-launch",
        "dive", "pounce", "pounce-abandon", "carry", "launch", "lifecycle",
    };

    [Export] public float GravityMps2 = 36f;
    [Export] public float CameraFlySpeed = 7f;
    [Export] public float CameraMouseSensitivity = 0.003f;

    private readonly RaycastTerrainQuery _terrain = new();
    private readonly DropBugRenderer _renderer = new();
    private readonly DeterminismHasher _hasher = new();

    private DropBugLocomotionController _bug = null!;
    private DropBugParams[] _presets = Array.Empty<DropBugParams>();
    private DropBugParams _preset = null!;
    private DropBugSandboxHud? _hud;
    private Camera3D _camera = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _lastCenter;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cameraFlying;
    private bool _pendingSpace;
    private bool _pendingLaunch;
    private bool _pendingCarryToggle;
    private bool _pendingClearAnchor;
    private bool _pendingClearTarget;
    private Vector2? _pendingAnchorPick;
    private Vector2? _pendingTargetPick;
    private bool _rendererBuilt;
    private bool _fatal;
    private long _tick;

    // 无头专项入口。
    private int _determinismTicks;
    private int _requestedTps = 40;
    private string _presetName = "original";
    private string _route = "walk";
    private float _perturb;
    private ulong? _expectHash;

    // 路线状态与指标。
    private int _waypointIndex;
    private int _waypointsReached;
    private Vector3 _spawnCenter;
    private float _travelDistance;
    private float _endConstraintDeviation;
    private float _maxConstraintDeviation;
    private float _maxPoseError;
    private bool _nonFinite;
    private bool _anchorAssigned;
    private bool _diveReleased;
    private bool _sawHanging;
    private long _firstHangTick = -1;
    private long _clearAnchorTick = -1;
    private long _refootTick = -1;
    private float _maxHeadSpeedAfterExit;
    private float _hangDrift;
    private float _hangMaxVel;
    private Vector3 _hangMeanCenter;
    private int _hangStillSamples;
    private float _maxStuckSignal;
    private float _maxStuckShake;
    private float _maxShakeJitter;
    private int _backwardTicks;
    private int _tailLeadTicks;
    private float _diveClosest = float.MaxValue;
    private int _diveCooldownAtLand = -1;
    private int _diveHeadFirstTicks;
    private int _diveFlightTicks;
    private long _pounceStartTick = -1;
    private long _pounceLeapTick = -1;
    private float _pounceClosest = float.MaxValue;
    private float _pounceRecoil;
    private Vector3 _pounceMidStart;
    private Vector3 _pounceTargetPoint;
    private float _maxClimb;
    private float _carryTravelUnloaded;
    private float _carryTravelLoaded;
    private float _carrySegmentStartX;
    private long _launchTick = -1;
    private long _launchRefootTick = -1;
    private float _hlFactorInWindow;
    private bool _hlEscaped;
    private Vector3 _hlEngagePoint;
    private float _travelAfterRecover;
    private bool _shiftExact;
    private bool _shiftDone;
    private bool _teleportCleared;
    private bool _teleportDone;

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
            GD.Print("[DROPBUG-RESULT] FAIL：命令行参数无效");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
        {
            Engine.MaxPhysicsStepsPerFrame = 100;
        }

        _presets = DropBugFactory.AllPresets();
        _preset = DropBugFactory.ById($"dropbug/{_presetName}");
        SpawnBug(SpawnForRoute(_route), ForwardForRoute(_route), _preset);

        if (_perturb != 0f)
        {
            _bug.Head.Pos += Vector3.Right * _perturb;
            _bug.Head.LastPos = _bug.Head.Pos;
        }

        _renderer.Build(this, _bug);
        _rendererBuilt = true;

        if (!Deterministic)
        {
            string[] names = Array.ConvertAll(_presets, p => p.Id);
            _hud = new DropBugSandboxHud();
            _hud.Build(this, names, SelectPreset);
            _hud.SyncPreset(Array.FindIndex(_presets, p => p.Id == _preset.Id));
        }

        GD.Print($"[DROPBUG-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.Id} route={_route} " +
                 $"determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnBug(Vector3 origin, Vector3 forward, DropBugParams parameters)
    {
        _preset = parameters;
        _bug = DropBugFactory.CreateController(origin, forward, parameters);
        _spawnCenter = BodyCenter();
        _lastCenter = _spawnCenter;
        if (_rendererBuilt)
        {
            _renderer.Build(this, _bug);
        }
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
        else
        {
            SampleInput();
            ProcessPendingInteraction();
        }

        _bug.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
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
            _bug.MoveDir = Vector3.Zero;
            _bug.RunSpeed = 0f;
            return;
        }
        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction.X += 1f;
        _bug.MoveTarget = null;
        _bug.MoveDir = direction.LengthSquared() > 1e-10f
            ? direction.Normalized()
            : Vector3.Zero;
        _bug.RunSpeed = direction == Vector3.Zero ? 0f : 1f;
    }

    private void ProcessPendingInteraction()
    {
        if (_pendingAnchorPick is Vector2 anchorMouse)
        {
            _pendingAnchorPick = null;
            Vector3 from = _camera.ProjectRayOrigin(anchorMouse);
            Vector3 to = from + _camera.ProjectRayNormal(anchorMouse) * 100f;
            if (_terrain.Raycast(from, to, out TerrainHit hit))
            {
                bool ok = _bug.TryAssignHangAnchor(in hit, _terrain);
                GD.Print(ok
                    ? $"[DROPBUG-SANDBOX] hang anchor accepted at " +
                      $"({hit.Point.X:F2},{hit.Point.Y:F2},{hit.Point.Z:F2})"
                    : $"[DROPBUG-SANDBOX] hang anchor rejected: {_bug.LastHangRejection}");
            }
        }
        if (_pendingTargetPick is Vector2 targetMouse)
        {
            _pendingTargetPick = null;
            Vector3 from = _camera.ProjectRayOrigin(targetMouse);
            Vector3 to = from + _camera.ProjectRayNormal(targetMouse) * 100f;
            if (_terrain.Raycast(from, to, out TerrainHit hit))
            {
                _bug.AttackTarget = new DropBugAttackTarget(
                    hit.Point + new Vector3(0f, 0.15f, 0f), Vector3.Zero);
                GD.Print("[DROPBUG-SANDBOX] attack target set");
            }
        }
        if (_pendingClearAnchor)
        {
            _pendingClearAnchor = false;
            _bug.ClearHangAnchor();
        }
        if (_pendingClearTarget)
        {
            _pendingClearTarget = false;
            _bug.AttackTarget = null;
        }
        if (_pendingCarryToggle)
        {
            _pendingCarryToggle = false;
            _bug.CarriedMass = _bug.CarriedMass > 0f ? 0f : 2f;
            GD.Print($"[DROPBUG-SANDBOX] carried mass = {_bug.CarriedMass}");
        }
        if (_pendingLaunch)
        {
            _pendingLaunch = false;
            _bug.Launch(_bug.Forward * 0.1f + Vector3.Up * 0.3f);
        }
        if (_pendingSpace)
        {
            _pendingSpace = false;
            if (_bug.HangFactor > 0f)
            {
                _bug.ReleaseHangDive();
            }
            else if (!_bug.TryStartPounce())
            {
                GD.Print("[DROPBUG-SANDBOX] pounce refused " +
                         $"(footing={_bug.Footing} target={_bug.AttackTarget is not null} " +
                         $"cooldown={_bug.AttackCooldown} carried={_bug.CarriedMass})");
            }
        }
    }

    // ============================================================ 无头路线

    private void DriveRoute()
    {
        _bug.MoveTarget = null;
        _bug.MoveDir = Vector3.Zero;
        _bug.RunSpeed = 0f;
        switch (_route)
        {
            case "walk":
                DriveWalk();
                break;
            case "slope":
                _bug.MoveTarget = new Vector3(7.4f, 1.75f, 6.5f); // 坡顶，到点即停
                _bug.RunSpeed = 1f;
                break;
            case "hop":
                _bug.MoveDir = _tick <= 60 ? Vector3.Left : Vector3.Right;
                _bug.RunSpeed = 1f;
                break;
            case "stuck":
                _bug.MoveDir = Vector3.Left;
                _bug.RunSpeed = 1f;
                break;
            case "backward":
                DriveBackward();
                break;
            case "hang":
            case "hang-exit":
                DriveHang();
                break;
            case "hang-launch":
                DriveHangLaunch();
                break;
            case "dive":
                DriveDive();
                break;
            case "pounce":
            case "pounce-abandon":
                DrivePounce();
                break;
            case "carry":
                DriveCarry();
                break;
            case "launch":
                DriveLaunch();
                break;
            case "lifecycle":
                DriveLifecycle();
                break;
        }
    }

    private void DriveWalk()
    {
        Vector3 target = WalkRoute[_waypointIndex];
        _bug.MoveTarget = target;
        _bug.RunSpeed = 1f;
        if (_bug.AtMoveTarget || (target - BodyCenter()).Length() < 0.4f)
        {
            _waypointIndex = (_waypointIndex + 1) % WalkRoute.Length;
            _waypointsReached++;
            _bug.MoveTarget = WalkRoute[_waypointIndex];
        }
    }

    private void DriveBackward()
    {
        if (!_anchorAssigned && _tick >= 10)
        {
            _anchorAssigned = AssignAnchor(new Vector3(0f, 3f, -6f),
                new Vector3(0f, 3.6f, -6f));
        }
        _bug.MoveTarget = new Vector3(0f, 0.3f, -6f);
        _bug.RunSpeed = 1f;
        if (_bug.MovingBackwards)
        {
            _backwardTicks++;
            Vector3 toTarget = new Vector3(0f, 0.3f, -6f) - BodyCenter();
            toTarget.Y = 0f;
            if (toTarget.LengthSquared() > 1e-6f &&
                (_bug.Tail.Pos - _bug.Head.Pos).Dot(toTarget.Normalized()) > 0f)
            {
                _tailLeadTicks++;
            }
        }
    }

    private void DriveHang()
    {
        if (!_anchorAssigned && _tick >= 30)
        {
            _anchorAssigned = AssignAnchor(new Vector3(0f, 3f, -6f),
                new Vector3(0f, 3.6f, -6f));
        }
        if (_route == "hang-exit" && _tick == 350)
        {
            _clearAnchorTick = _tick;
            _bug.ClearHangAnchor();
        }
        if (_route == "hang-exit" && _clearAnchorTick > 0 && _refootTick > 0)
        {
            _bug.MoveDir = Vector3.Left;
            _bug.RunSpeed = 1f;
        }
    }

    /// <summary>悬挂中被击飞（外部评审 P1 的 Godot 侧覆盖）：贴附稳定后 Launch，
    /// 断言冷却窗内不重贴附、真实逃逸吸附圈、弹道落地恢复、意图锚保留。</summary>
    private void DriveHangLaunch()
    {
        if (!_anchorAssigned && _tick >= 30)
        {
            _anchorAssigned = AssignAnchor(new Vector3(0f, 3f, -6f),
                new Vector3(0f, 3.6f, -6f));
        }
        if (_launchTick < 0 && _sawHanging && _firstHangTick > 0 &&
            _tick - _firstHangTick >= 60)
        {
            _launchTick = _tick;
            if (_bug.HangAnchor is { } anchor)
            {
                _hlEngagePoint = anchor.Point + anchor.Normal * _preset.HangSurfaceInset;
            }
            // 0.3 m/tick 斜向下（修复前该量级被吸附伺服整个吃掉，无引擎实测）。
            _bug.Launch(new Vector3(-0.268f, -0.134f, 0f));
        }
        if (_launchTick > 0)
        {
            if (_tick - _launchTick <= _preset.HangRegrabDelayTicks)
            {
                _hlFactorInWindow = Mathf.Max(_hlFactorInWindow, _bug.HangFactor);
            }
            if (_bug.Mid.Pos.DistanceTo(_hlEngagePoint) > _preset.HangEngageDistance)
            {
                _hlEscaped = true;
            }
            if (_launchRefootTick < 0 && _bug.Footing && _bug.Mid.Pos.Y < 1f &&
                _tick > _launchTick + 3)
            {
                _launchRefootTick = _tick;
            }
        }
    }

    private void DriveDive()
    {
        if (!_anchorAssigned && _tick >= 2)
        {
            _anchorAssigned = AssignAnchor(new Vector3(-7f, 7.6f, 7f),
                new Vector3(-7f, 8.3f, 7f));
        }
        if (!_diveReleased && _sawHanging && _firstHangTick > 0 &&
            _tick - _firstHangTick >= 40)
        {
            _bug.AttackTarget = new DropBugAttackTarget(new Vector3(-4.5f, 0.2f, 7f),
                Vector3.Zero);
            _diveReleased = _bug.ReleaseHangDive();
        }
    }

    private void DrivePounce()
    {
        if (_tick == 120)
        {
            _pounceTargetPoint = new Vector3(0.4f, 0.2f, -2f);
            _bug.AttackTarget = new DropBugAttackTarget(_pounceTargetPoint, Vector3.Zero);
        }
        if (_tick == 121)
        {
            if (_bug.TryStartPounce())
            {
                _pounceStartTick = _tick;
                _pounceMidStart = _bug.Mid.Pos;
            }
        }
        if (_route == "pounce-abandon" && _pounceStartTick > 0 &&
            _tick == _pounceStartTick + 6)
        {
            _bug.AttackTarget = new DropBugAttackTarget(new Vector3(9f, 0.2f, -2f),
                Vector3.Zero);
        }
    }

    private void DriveCarry()
    {
        if (_tick == 61)
        {
            _carrySegmentStartX = _bug.Head.Pos.X;
        }
        if (_tick == 261)
        {
            _carryTravelUnloaded = _bug.Head.Pos.X - _carrySegmentStartX;
            _carrySegmentStartX = _bug.Head.Pos.X;
        }
        if (_tick == 461)
        {
            _carryTravelLoaded = _bug.Head.Pos.X - _carrySegmentStartX;
        }
        if (_tick > 60)
        {
            _bug.MoveDir = Vector3.Right;
            _bug.RunSpeed = 1f;
            _bug.CarriedMass = _tick > 260 ? 3f : 0f;
        }
    }

    private void DriveLaunch()
    {
        _bug.MoveTarget = new Vector3(18f, 0.3f, 4f); // 有界目标，不走出地板
        _bug.RunSpeed = 1f;
        if (_tick == 150)
        {
            _launchTick = _tick;
            _bug.Launch(new Vector3(0.12f, 0.3f, 0.05f));
        }
        if (_launchTick > 0 && _launchRefootTick < 0 && _bug.Footing &&
            _tick > _launchTick + 3)
        {
            _launchRefootTick = _tick;
            _carrySegmentStartX = _bug.Head.Pos.X;
        }
        if (_launchRefootTick > 0)
        {
            _travelAfterRecover = Mathf.Abs(_bug.Head.Pos.X - _carrySegmentStartX);
        }
    }

    private void DriveLifecycle()
    {
        _bug.MoveDir = Vector3.Right;
        _bug.RunSpeed = 1f;
        if (_tick == 100 && !_shiftDone)
        {
            _shiftDone = true;
            Vector3 delta = new(0.5f, 0f, 0.25f);
            Vector3 head = _bug.Head.Pos;
            Vector3 leg0 = _bug.Legs[0].Pos;
            _bug.MoveTarget = null;
            _bug.Shift(delta);
            _shiftExact = _bug.Head.Pos == head + delta &&
                          _bug.Legs[0].Pos == leg0 + delta;
        }
        if (_tick == 250 && !_teleportDone)
        {
            _teleportDone = true;
            _bug.MoveTarget = new Vector3(20f, 0.3f, 4f);
            _bug.AttackTarget = new DropBugAttackTarget(new Vector3(1f, 0.2f, 1f),
                Vector3.Zero);
            _bug.Teleport(new Vector3(-2f, 0.4f, -1f));
            _teleportCleared = _bug.MoveTarget is null && _bug.AttackTarget is null &&
                               _bug.HangAnchor is null && _bug.AttackCooldown == 0;
        }
    }

    private bool AssignAnchor(Vector3 from, Vector3 to) =>
        _terrain.Raycast(from, to, out TerrainHit hit) &&
        _bug.TryAssignHangAnchor(in hit, _terrain);

    // ============================================================ 指标

    private void TrackMetrics()
    {
        Vector3 center = BodyCenter();
        _travelDistance += center.DistanceTo(_lastCenter);
        _lastCenter = center;
        _maxClimb = Mathf.Max(_maxClimb, center.Y - _spawnCenter.Y);
        _endConstraintDeviation = _bug.Body.CurrentMaxDeviation();
        if (_bug.StuckShake <= 1e-3f)
        {
            _maxConstraintDeviation = Mathf.Max(_maxConstraintDeviation,
                _endConstraintDeviation);
        }

        Vector3 f = _bug.Forward;
        Vector3 u = _bug.Up;
        Vector3 r = _bug.Right;
        float poseError = Mathf.Max(
            Mathf.Max(Mathf.Abs(f.Dot(u)), Mathf.Abs(f.Dot(r))),
            Mathf.Max(Mathf.Abs(u.Dot(r)),
                Mathf.Max(Mathf.Abs(f.Length() - 1f),
                    Mathf.Max(Mathf.Abs(u.Length() - 1f), Mathf.Abs(r.Length() - 1f)))));
        _maxPoseError = Mathf.Max(_maxPoseError, poseError);

        _nonFinite |= !IsFinite(f) || !IsFinite(u) || !IsFinite(r);
        foreach (BodyChunk chunk in _bug.Body.Chunks)
        {
            _nonFinite |= !IsFinite(chunk.Pos) || !IsFinite(chunk.Vel);
        }
        foreach (DropBugLeg leg in _bug.Legs)
        {
            _nonFinite |= !IsFinite(leg.Pos) || !IsFinite(leg.Vel);
        }

        _maxStuckSignal = Mathf.Max(_maxStuckSignal, _bug.StuckSignal);
        _maxStuckShake = Mathf.Max(_maxStuckShake, _bug.StuckShake);
        if (_bug.StuckShake > 0.5f)
        {
            _maxShakeJitter = Mathf.Max(_maxShakeJitter,
                center.DistanceTo(_lastCenter) + (center - _lastCenter).Length());
            // center 已更新，用头部单 tick 位移作抖动幅度观测。
            _maxShakeJitter = Mathf.Max(_maxShakeJitter,
                _bug.Head.Pos.DistanceTo(_bug.Head.LastPos));
        }

        if (_bug.Hanging)
        {
            _sawHanging = true;
            if (_firstHangTick < 0)
            {
                _firstHangTick = _tick;
            }
            if (_route is "hang" or "hang-exit" && _clearAnchorTick < 0 &&
                _firstHangTick > 0 && _tick > _firstHangTick + 50)
            {
                _hangStillSamples++;
                _hangMeanCenter += center;
                Vector3 mean = _hangMeanCenter / _hangStillSamples;
                _hangDrift = Mathf.Max(_hangDrift, center.DistanceTo(mean));
                _hangMaxVel = Mathf.Max(_hangMaxVel,
                    Mathf.Max(_bug.Head.Vel.Length(),
                        Mathf.Max(_bug.Mid.Vel.Length(), _bug.Tail.Vel.Length())));
            }
        }
        if (_clearAnchorTick > 0)
        {
            _maxHeadSpeedAfterExit = Mathf.Max(_maxHeadSpeedAfterExit,
                _bug.Head.Vel.Length());
            if (_refootTick < 0 && _bug.Footing && _bug.Mid.Pos.Y < 1f)
            {
                _refootTick = _tick;
                _carrySegmentStartX = _bug.Head.Pos.X;
            }
            if (_refootTick > 0)
            {
                _travelAfterRecover = Mathf.Abs(_bug.Head.Pos.X - _carrySegmentStartX);
            }
        }

        if (_route == "dive" && _diveReleased)
        {
            if (_bug.Diving)
            {
                _diveFlightTicks++;
                if (_bug.AttackTarget is { } target)
                {
                    _diveClosest = Mathf.Min(_diveClosest,
                        _bug.Head.Pos.DistanceTo(target.Point));
                }
                Vector3 velocity = _bug.Head.Vel + _bug.Mid.Vel;
                if (velocity.LengthSquared() > 1e-8f &&
                    (_bug.Head.Pos - _bug.Mid.Pos).Dot(velocity.Normalized()) > 0f)
                {
                    _diveHeadFirstTicks++;
                }
            }
            else if (_diveCooldownAtLand < 0 && _bug.LastDiveLandingTick > 0)
            {
                _diveCooldownAtLand = _bug.AttackCooldown;
            }
        }

        if (_route is "pounce" or "pounce-abandon" && _pounceStartTick > 0)
        {
            if (_bug.ChargingPounce)
            {
                _pounceRecoil = Mathf.Max(_pounceRecoil,
                    (_pounceMidStart - _bug.Mid.Pos).Dot(
                        (_pounceTargetPoint - _pounceMidStart).Normalized()));
            }
            if (_bug.Jumping)
            {
                if (_pounceLeapTick < 0)
                {
                    _pounceLeapTick = _tick;
                }
                _pounceClosest = Mathf.Min(_pounceClosest,
                    _bug.Head.Pos.DistanceTo(_pounceTargetPoint));
            }
        }
    }

    private void RecordDeterminism()
    {
        _bug.FoldState(_hasher);
        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[DROPBUG-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
    }

    private int DumpDeterministicResult()
    {
        GD.Print($"[DROPBUG-METRIC] route={_route} preset={_preset.Id} " +
                 $"travel={_travelDistance:F3}m waypoints={_waypointsReached} " +
                 $"endDev={_endConstraintDeviation:F5}m maxDev={_maxConstraintDeviation:F5}m " +
                 $"poseErr={_maxPoseError:E2} hops={_bug.HopSerial} " +
                 $"stuck={_maxStuckSignal:F2}/{_maxStuckShake:F2}/{_maxShakeJitter:F3} " +
                 $"backward={_backwardTicks}/{_tailLeadTicks} " +
                 $"hang={_firstHangTick}/{_hangDrift:F4}/{_hangMaxVel:F4} " +
                 $"exit={_clearAnchorTick}/{_refootTick}/{_maxHeadSpeedAfterExit:F3} " +
                 $"dive={_diveClosest:F3}/{_diveFlightTicks}/{_diveCooldownAtLand} " +
                 $"pounce={_pounceLeapTick}/{_pounceRecoil:F3}/{_pounceClosest:F3}/" +
                 $"{_bug.PounceLeapSerial}/{_bug.PounceAbandonSerial} " +
                 $"carry={_carryTravelUnloaded:F2}/{_carryTravelLoaded:F2} " +
                 $"launch={_launchTick}/{_launchRefootTick}/{_travelAfterRecover:F2} " +
                 $"lifecycle={_shiftExact}/{_teleportCleared}");

        var failures = new List<string>();
        if (_nonFinite)
        {
            failures.Add("状态出现 NaN/Inf");
        }
        if (_maxPoseError >= 1e-4f)
        {
            failures.Add($"姿态正交误差 {_maxPoseError:E2} >= 1e-4");
        }
        if (_expectHash is ulong expected && _hasher.Value != expected)
        {
            failures.Add($"hash {_hasher.Value:X16} != {expected:X16}");
        }
        if (_route != "stuck" && _endConstraintDeviation >= 0.06f)
        {
            failures.Add($"连接末偏差 {_endConstraintDeviation:F5}m >= 0.06m");
        }

        switch (_route)
        {
            case "walk":
                // 门取全预设可行域（original 1200 tick 实测 10wp/61m；bulky 体格慢一档）。
                if (_waypointsReached < 5 || _travelDistance < 12f)
                {
                    failures.Add($"巡走覆盖不足（wp={_waypointsReached}, travel={_travelDistance:F1}）");
                }
                break;
            case "slope":
                float advance = BodyCenter().X - _spawnCenter.X;
                if (_maxClimb < 0.9f || advance < 3f)
                {
                    failures.Add($"上坡不足（maxClimb={_maxClimb:F2}, dx={advance:F2}）");
                }
                break;
            case "hop":
                if (_bug.HopSerial <= 0 || _bug.Head.Pos.X < 4f)
                {
                    failures.Add($"越障未点火或未跨越（hops={_bug.HopSerial}, x={_bug.Head.Pos.X:F2}）");
                }
                break;
            case "stuck":
                if (_maxStuckSignal < 0.99f || _maxStuckShake < 0.9f ||
                    _maxShakeJitter < 0.03f)
                {
                    failures.Add($"卡住抖动未达标（signal={_maxStuckSignal:F2}, " +
                                 $"shake={_maxStuckShake:F2}, jitter={_maxShakeJitter:F3}）");
                }
                break;
            case "backward":
                if (!_anchorAssigned || _backwardTicks < 40 ||
                    _tailLeadTicks < _backwardTicks * 0.6f)
                {
                    failures.Add($"倒退接近未达标（anchor={_anchorAssigned}, " +
                                 $"back={_backwardTicks}, tailLead={_tailLeadTicks}）");
                }
                break;
            case "hang":
                bool restShrunk =
                    Mathf.Abs(_bug.Body.Connections[0].RestLength - _preset.HangHeadMidLength) < 1e-4f &&
                    Mathf.Abs(_bug.Body.Connections[1].RestLength - _preset.HangMidTailLength) < 1e-4f;
                if (!_sawHanging || _firstHangTick > 250 || _hangDrift > 0.05f ||
                    _hangMaxVel > 0.03f || !restShrunk ||
                    _bug.Mid.CollideWithTerrain || _bug.Tail.CollideWithTerrain)
                {
                    failures.Add($"悬挂未达标（hang={_sawHanging}@{_firstHangTick}, " +
                                 $"drift={_hangDrift:F4}, vel={_hangMaxVel:F4}, " +
                                 $"restShrunk={restShrunk}）");
                }
                break;
            case "hang-exit":
                bool restRestored =
                    Mathf.Abs(_bug.Body.Connections[0].RestLength - _preset.HeadMidLength) < 1e-4f;
                if (!_sawHanging || _clearAnchorTick < 0 || _refootTick < 0 ||
                    _refootTick > _clearAnchorTick + 250 ||
                    _maxHeadSpeedAfterExit > 0.8f || !restRestored ||
                    _travelAfterRecover < 0.5f)
                {
                    failures.Add($"悬挂退出未达标（refoot={_refootTick}, " +
                                 $"maxSpeed={_maxHeadSpeedAfterExit:F3}, " +
                                 $"restRestored={restRestored}, travel={_travelAfterRecover:F2}）");
                }
                break;
            case "hang-launch":
                if (!_sawHanging || _launchTick < 0 || _hlFactorInWindow > 0f ||
                    !_hlEscaped || _bug.HangFactor != 0f ||
                    _launchRefootTick < 0 || _launchRefootTick > _launchTick + 250 ||
                    _bug.HangAnchor is null)
                {
                    failures.Add($"悬挂中击飞未达标（hang={_sawHanging}@{_firstHangTick}, " +
                                 $"launch={_launchTick}, fWindow={_hlFactorInWindow:F3}, " +
                                 $"escaped={_hlEscaped}, refoot={_launchRefootTick}, " +
                                 $"anchorKept={_bug.HangAnchor is not null}）");
                }
                break;
            case "dive":
                if (!_diveReleased || _diveClosest > 0.8f || _diveFlightTicks <= 4 ||
                    _diveCooldownAtLand < 15 ||
                    _diveHeadFirstTicks < _diveFlightTicks * 0.5f || _bug.Diving)
                {
                    failures.Add($"俯冲未达标（released={_diveReleased}, " +
                                 $"closest={_diveClosest:F3}, flight={_diveFlightTicks}, " +
                                 $"cooldown={_diveCooldownAtLand}, " +
                                 $"headFirst={_diveHeadFirstTicks}）");
                }
                break;
            case "pounce":
                // 起跳窗口按预设蓄力时长参数化（外部评审 P2：nimble 1/12 起跳更早，
                // 固定 12..25 窗口是按 original 1/15 写死的）；±1 容忍驱动与物理 tick
                // 的相位差，+10 容忍任何滞后。
                long expectedCharge = Mathf.CeilToInt(
                    (1f - _preset.PounceChargeStart) / _preset.PounceChargeRate);
                long leapDelta = _pounceLeapTick - _pounceStartTick;
                if (_bug.PounceLeapSerial != 1 || _pounceLeapTick < 0 ||
                    leapDelta < expectedCharge - 1 || leapDelta > expectedCharge + 10 ||
                    _pounceClosest > 0.6f || _pounceRecoil < 0.01f)
                {
                    failures.Add($"蓄力扑击未达标（leap={leapDelta}, " +
                                 $"expected≈{expectedCharge}, " +
                                 $"closest={_pounceClosest:F3}, recoil={_pounceRecoil:F3}）");
                }
                break;
            case "pounce-abandon":
                if (_bug.PounceAbandonSerial < 1 || _bug.PounceLeapSerial != 0)
                {
                    failures.Add($"目标逃逸未放弃（abandon={_bug.PounceAbandonSerial}, " +
                                 $"leap={_bug.PounceLeapSerial}）");
                }
                break;
            case "carry":
                if (_carryTravelUnloaded < 3f ||
                    _carryTravelLoaded > _carryTravelUnloaded * 0.75f)
                {
                    failures.Add($"负重未减速（unloaded={_carryTravelUnloaded:F2}, " +
                                 $"loaded={_carryTravelLoaded:F2}）");
                }
                break;
            case "launch":
                if (_launchTick < 0 || _launchRefootTick < 0 ||
                    _launchRefootTick > _launchTick + 200 || _travelAfterRecover < 1f)
                {
                    failures.Add($"击飞恢复未达标（refoot={_launchRefootTick}, " +
                                 $"travel={_travelAfterRecover:F2}）");
                }
                break;
            case "lifecycle":
                if (!_shiftExact || !_teleportCleared || !_bug.Footing)
                {
                    failures.Add($"生命周期未达标（shift={_shiftExact}, " +
                                 $"teleport={_teleportCleared}, footing={_bug.Footing}）");
                }
                break;
        }

        bool pass = failures.Count == 0;
        GD.Print(pass
            ? "[DROPBUG-RESULT] PASS"
            : $"[DROPBUG-RESULT] FAIL：{string.Join("; ", failures)}");
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
        else
        {
            UpdateCameraFly((float)delta);
        }
        if (_rendererBuilt)
        {
            _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
        }
        _hud?.UpdateStatus(
            $"preset  {_preset.Id}\n" +
            $"footing  {_bug.Footing} ({_bug.FootingCounter})   backwards  {_bug.MovingBackwards}\n" +
            $"hang  {_bug.HangState} f={_bug.HangFactor:F2}   " +
            $"rest=({_bug.Body.Connections[0].RestLength:F3}/" +
            $"{_bug.Body.Connections[1].RestLength:F3}/" +
            $"{_bug.Body.Connections[2].RestLength:F3})\n" +
            $"charge  {_bug.PounceCharge:F2}   jump  {_bug.Jumping}   dive  {_bug.Diving}   " +
            $"cooldown  {_bug.AttackCooldown}\n" +
            $"stuck  {_bug.StuckSignal:F2}/{_bug.StuckShake:F2}   " +
            $"carried  {_bug.CarriedMass:F1}   runCycle  {_bug.RunCycle:F1}");
    }

    private void UpdateShowcaseCamera()
    {
        Vector3 center = BodyCenter();
        Vector3 offset = _route switch
        {
            "dive" => new Vector3(2.6f, 1.2f, 3.2f),
            "hang" or "hang-exit" or "hang-launch" or "backward" =>
                new Vector3(1.8f, 0.4f, 2.6f),
            _ => new Vector3(1.9f, 1.1f, 2.6f),
        };
        _camera.GlobalPosition = center + offset;
        _camera.Fov = 45f;
        _camera.LookAt(center, Vector3.Up);
    }

    private bool WantCameraFly =>
        Input.IsMouseButtonPressed(MouseButton.Right)
        && !Input.IsPhysicalKeyPressed(Key.Shift);

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
        if (@event is InputEventMouseButton
            {
                ButtonIndex: MouseButton.Right,
                Pressed: true,
            } mouseButton && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            _pendingAnchorPick = mouseButton.Position;
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        switch (key.PhysicalKeycode)
        {
            case Key.Space:
                _pendingSpace = true;
                break;
            case Key.T:
                _pendingTargetPick = GetViewport().GetMousePosition();
                break;
            case Key.Y:
                _pendingClearTarget = true;
                break;
            case Key.G:
                _pendingClearAnchor = true;
                break;
            case Key.C:
                _pendingCarryToggle = true;
                break;
            case Key.B:
                _pendingLaunch = true;
                break;
            case Key.Key1 or Key.Key2 or Key.Key3:
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
        SpawnBug(respawn, _bug.Forward, _presets[index]);
        _hud?.SyncPreset(index);
        GD.Print($"[DROPBUG-SANDBOX] preset -> {_presets[index].Id}");
    }

    // ============================================================ CLI

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--dropbug-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--dropbug-determinism=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0)
                    {
                        throw new FormatException("determinism tick 必须为正");
                    }
                }
                else if (argument.StartsWith("--dropbug-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(argument["--dropbug-tps=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_requestedTps <= 0)
                    {
                        throw new FormatException("tps 必须为正");
                    }
                }
                else if (argument.StartsWith("--dropbug-preset=", StringComparison.Ordinal))
                {
                    _presetName = argument["--dropbug-preset=".Length..].ToLowerInvariant();
                    if (_presetName is not ("original" or "nimble" or "bulky"))
                    {
                        throw new FormatException("preset 只接受 original|nimble|bulky");
                    }
                }
                else if (argument.StartsWith("--dropbug-route=", StringComparison.Ordinal))
                {
                    _route = argument["--dropbug-route=".Length..].ToLowerInvariant();
                    if (Array.IndexOf(Routes, _route) < 0)
                    {
                        throw new FormatException($"未知 dropbug route：{_route}");
                    }
                }
                else if (argument.StartsWith("--dropbug-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(argument["--dropbug-perturb=".Length..],
                        CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb))
                    {
                        throw new FormatException("perturb 必须有限");
                    }
                }
                else if (argument.StartsWith("--dropbug-expect-hash=", StringComparison.Ordinal))
                {
                    string text = argument["--dropbug-expect-hash=".Length..];
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[2..];
                    }
                    _expectHash = ulong.Parse(text, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture);
                }
                else if (argument.StartsWith("--dropbug-", StringComparison.Ordinal))
                {
                    throw new FormatException($"未知参数 {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                GD.PushError($"[DROPBUG-CLI] {argument}: {exception.Message}");
                return false;
            }
        }
        return true;
    }

    private static Vector3 SpawnForRoute(string route) => route switch
    {
        "slope" => new Vector3(2.6f, 0.5f, 6.5f),
        "hop" => new Vector3(0.5f, 0.3f, 0f),
        "stuck" => new Vector3(-4f, 0.3f, 0f),
        "backward" => new Vector3(-3.2f, 0.3f, -6f),
        "hang" or "hang-exit" or "hang-launch" => new Vector3(1.2f, 3.15f, -6f),
        "dive" => new Vector3(-7f, 7.72f, 7f),
        "pounce" or "pounce-abandon" => new Vector3(-1.8f, 0.3f, -2f),
        "carry" or "launch" or "lifecycle" => new Vector3(-4f, 0.3f, 4f),
        _ => new Vector3(-3f, 0.3f, 0f),
    };

    private static Vector3 ForwardForRoute(string route) => route switch
    {
        "stuck" => Vector3.Left,
        "backward" or "hang" or "hang-exit" or "hang-launch" => Vector3.Left,
        _ => Vector3.Right,
    };

    private Vector3 BodyCenter() =>
        (_bug.Head.Pos + _bug.Mid.Pos + _bug.Tail.Pos) / 3f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
