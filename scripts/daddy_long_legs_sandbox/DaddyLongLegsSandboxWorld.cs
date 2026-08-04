using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DaddyLongLegs;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// DaddyLongLegs 独立白盒宿主：40Hz 逻辑累加器、引擎地形适配、交互调试和退出码回归。
/// 本文件只消费 DaddyLongLegs 的公开宿主接口，不借用任何既有物种控制器。
/// </summary>
public partial class DaddyLongLegsSandboxWorld : Node3D
{
    private const double TickDt = 0.025;
    private const float MaximumAllowedResidualPenetration = 0.002f;
    private const ulong DebugTargetId = 0xDA_DD_1E_65UL;
    private const int InteractiveStunTicks = 160;
    private const int WallIdleDriveTicks = 30;
    private const int WallIdleSettleTicks = 240;
    private const int HeightRetentionStandTicks = 900;
    private const int HeightRetentionStandWindow = 200;
    private const int HeightRetentionMoveTicks = 900;
    private const int HeightRetentionMoveWindow = 240;
    private static readonly Vector3 StuckRouteTarget = new(-6.0f, 1.70f, 12f);

    private static readonly string[] RouteNames =
    {
        "flat", "idle-start", "height-retention", "tap", "course", "wall", "wall-idle",
        "ceiling", "corner", "outer", "stun", "stuck", "target", "launch", "lifecycle",
    };

    [Export] public float GravityMps2 = 36f;
    [Export] public float CameraFlySpeed = 9f;
    [Export] public float CameraMouseSensitivity = 0.003f;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;

    private readonly RaycastTerrainQuery _raycast = new();
    private readonly DragController _drag = new();
    private DeterminismHasher _hasher = new();

    private RayDebugDraw _terrain = null!;
    private DaddyLongLegsRenderer _renderer = null!;
    private DaddyLongLegsLocomotionController _controller = null!;
    private DaddyLongLegsParams[] _presets = Array.Empty<DaddyLongLegsParams>();
    private DaddyLongLegsParams _preset = null!;
    private DaddyLongLegsSandboxHud? _hud;
    private Camera3D _camera = null!;
    private OmniLight3D? _fillLight;
    private Node3D _debugTargetMarker = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _spawn;
    private double _tickAccumulator;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cameraFlying;
    private bool _fatal;
    private bool _rendererBuilt;
    private Vector2? _pendingMoveTargetPick;
    private long _tick;

    // CLI / deterministic host configuration.
    private int _determinismTicks;
    private int _requestedTps = 40;
    private string _presetName = "daddy";
    private string _route = "flat";
    private ulong _stableSeed = 1UL;
    private float _perturb;
    private ulong? _expectHash;
    private string _ablation = "none";
    private int _routeDirection = 1;
    private int _routeWaypoint;

    // Host-owned movable target used by ExternalReach.
    private bool _debugTargetActive;
    private bool _debugTargetPull = true;
    private int _externalTentacle = -1;
    private Vector3 _debugTargetPos;
    private Vector3 _debugTargetVel;

    // General metrics.
    private Vector3 _initialCenter;
    private Vector3 _lastCenter;
    private float _travel;
    private float _maximumDisplacement;
    private float _maximumForwardProgress;
    private float _maximumHeightGain;
    private float _minimumHeightDelta;
    private float _supportSum;
    private int _supportSamples;
    private float _maximumRawSupport;
    private float _maximumEffectiveSupport;
    private float _minimumEffectiveSupport = float.PositiveInfinity;
    private float _maximumGravityCancellation;
    private float _maximumGravityMappingError;
    private float _maximumDriveScale;
    private int _maximumLocomotion;
    private int _minimumLocomotion = int.MaxValue;
    private int _maximumArrived;
    private int _maximumContactSegments;
    private int _maximumPassiveContactSegments;
    private int _maximumActiveGripSegments;
    private int _maximumPlantedTentacles;
    private int _maximumPeelingTentacles;
    private int _maximumReachingTentacles;
    private float _maximumGuideContactRatio;
    private float _maximumActualTentacleContactRatio;
    private float _maximumActualTentacleGripRatio;
    private int[] _highContactRuns = Array.Empty<int>();
    private int _maximumHighContactRun;
    private int _maximumSearchFailure;
    private int _maximumStuckCounter;
    private int _maximumStuckEpisodeSerial;
    private int _maximumStepReleaseSerial;
    private float _maximumLandingReachExcess;
    private float _maximumStuckAmount;
    private float _maximumBodyConstraintDeviation;
    private float _maximumTentacleConstraintError;
    private int[][] _blockedLinkRuns = Array.Empty<int[]>();
    private int[] _blockedTentacleRuns = Array.Empty<int>();
    private int _maximumInteriorBlockedLinks;
    private int _maximumInteriorLinkBlockRun;
    private int _maximumInteriorTentacleBlockRun;
    private int _currentInteriorBlockedLinks;
    private int _maximumInteriorLinkBlockTentacle = -1;
    private int _maximumInteriorLinkBlockSegment = -1;
    private long _maximumInteriorLinkBlockTick = -1;
    private Vector3 _maximumInteriorLinkBlockHitPoint;
    private Vector3 _maximumInteriorLinkBlockHitNormal;
    private Vector3 _maximumInteriorLinkBlockFrom;
    private Vector3 _maximumInteriorLinkBlockTo;
    private int _maximumTerrainBacktrackSerial;
    private int _maximumPostConstraintSweepSerial;
    private float _maximumResidualPenetration;
    private string _maximumPenetrationPart = "none";
    private long _maximumPenetrationTick = -1;
    private int _maximumTickQueries;
    private bool _nonFinite;
    private bool _dutyCountMismatch;
    private bool _activeGripStateMismatch;
    private bool _sawFloorContact;
    private bool _sawSlopeContact;
    private bool _sawWallContact;
    private bool _sawCeilingContact;
    private bool _sawWallContactAfterMount;
    private bool _sawCeilingContactAfterMount;
    private float _minimumPostMountHeight = float.PositiveInfinity;
    private float _maximumPostMountHeight = float.NegativeInfinity;
    private int _wallMountedRun;
    private int _maximumWallMountedRun;
    private int _ceilingMountedRun;
    private int _maximumCeilingMountedRun;
    private int _outerTopRun;
    private int _maximumOuterTopRun;
    private int _outerWallRun;
    private int _maximumOuterWallRun;
    private bool _outerDescentStarted;
    private bool _innerTransitionStarted;
    private bool _sawAtMoveTarget;
    private int _waypointsReached;
    private long _firstFlatReverseTick = -1;
    private float _firstFlatReverseX;
    private int _firstFlatReverseSteps;
    private int _firstFlatReverseStartReplants;
    private float _flatReverseProgress100 = float.NegativeInfinity;
    private int _flatReverseSteps100;
    private int _flatReverseStartReplants100;
    private bool _idleStartCaptured;
    private float _idleStartX;
    private float _idleStartProgress20 = float.NegativeInfinity;
    private float _idleStartProgress40 = float.NegativeInfinity;
    private float _idleStartProgress80 = float.NegativeInfinity;
    private readonly float[] _heightRetentionStandHeights =
        new float[HeightRetentionStandWindow];
    private readonly float[] _heightRetentionMoveHeights =
        new float[HeightRetentionMoveWindow];
    private int _heightRetentionStandSamples;
    private int _heightRetentionMoveSamples;
    private bool _heightRetentionMoveCaptured;
    private float _heightRetentionMoveStartX;
    private int _heightRetentionStepsBeforeMove;
    private int _heightRetentionStartReplantsBeforeMove;
    private int _heightRetentionLandingsBeforeMove;
    private int[] _heightRetentionObservedStepSerials = Array.Empty<int>();
    private bool[] _heightRetentionExplicitReplantInFlight = Array.Empty<bool>();
    private int[] _heightRetentionTentacleLandingsBeforeMove = Array.Empty<int>();
    private int _heightRetentionMaximumInFlightReplants;
    private float _heightRetentionMoveSupportSum;
    private float _heightRetentionMinimumMoveSupport = float.PositiveInfinity;
    private int _heightRetentionStartReplantIndex = -1;
    private bool _heightRetentionStartReplantSawPlanted;
    private int _heightRetentionStepEligibleTicks;
    private int _heightRetentionPreviousActiveReplant = -1;
    private long _heightRetentionFirstReplantCompletionTick = -1;
    private int _heightRetentionCompletionCandidateCount;
    private float _heightRetentionCompletionBestCancellation = float.NegativeInfinity;
    private float _heightRetentionMaximumCandidateCancellation = float.NegativeInfinity;
    private bool _tapCaptured;
    private float _tapStartX;
    private int _tapActiveTicks;
    private int _tapSampleTicks;
    private int _tapNearGroundTicks;
    private int _tapNearGroundRun;
    private int _tapMaximumNearGroundRun;
    private float _tapMinimumBodyClearance = float.PositiveInfinity;
    private float _tapSupportSum;
    private int _tapStartingMovementEpisodes;
    private int _tapStartingReplants;

    private long _wallIdleGraceEndTick = -1;
    private int _wallIdleStepReleaseAtGrace;
    private int _wallIdleLandingSerialAtGrace;
    private int _wallIdleLandingSerialAtSettle;
    private int _wallIdleLastLandingSerial = -1;
    private int _wallIdleLandingChangesAfterGrace;
    private long _wallIdleLastLandingChangeTick = -1;
    private bool _wallIdleObservationCaptured;
    private int _wallIdleObservationTicks;
    private float _wallIdleObservationStartY;
    private float _wallIdleMinimumY = float.PositiveInfinity;
    private float _wallIdleMaximumY = float.NegativeInfinity;
    private float _wallIdleMinimumSupport = float.PositiveInfinity;
    private float _wallIdleMaximumSupport = float.NegativeInfinity;
    private bool _wallIdleSawWallContactAfterStop;
    private int _wallIdleWallContactTicksAfterSettle;

    // Route-specific observations.
    private bool _stunIssued;
    private int _stunnedTentacle = -1;
    private float _supportBeforeStun;
    private int _dutySerialBeforeStun;
    private bool _sawStunnedRole;
    private bool _sawStunnedZeroSupport;
    private bool _sawStunTakeover;
    private bool _sawStunRecovery;
    private float _postStunMaximumSupport;

    private bool _stuckActivated;
    private bool _stuckCrossedObstacle;
    private bool _stuckRecoveredAfterCrossing;
    private float _stuckMinimumTargetDistance = float.PositiveInfinity;

    private bool _externalAssignmentSucceeded;
    private bool _sawTargetReached;
    private bool _sawTargetHeld;
    private bool _sawTargetRelease;
    private bool _externalSupportLeak;
    private float _targetInitialDistance = float.PositiveInfinity;
    private float _targetMinimumDistance = float.PositiveInfinity;

    private bool _launchIssued;
    private bool _launchContract;
    private float _supportBeforeLaunch;
    private float _supportImmediatelyAfterLaunch = float.NaN;
    private float _postLaunchMaximumSupport;

    private bool _shiftContract;
    private bool _teleportContract;
    private bool _lifecycleLaunchContract;
    private bool _lifecycleMoveTargetRetained;
    private bool _lifecycleExternalRetained;

    private bool Deterministic => _determinismTicks > 0;
    private bool WantCameraFly => Input.IsMouseButtonPressed(MouseButton.Right)
        && !Input.IsPhysicalKeyPressed(Key.Shift);

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _camera = GetNode<Camera3D>("Camera3D");
        _fillLight = GetNodeOrNull<OmniLight3D>("FillLight")
            ?? GetNodeOrNull<OmniLight3D>("WarmFill");
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;
        _gravityPerTick = new Vector3(0f,
            -GravityMps2 * (float)(TickDt * TickDt), 0f);

        if (!ParseArguments())
        {
            _fatal = true;
            GD.Print("[DADDY-RESULT] FAIL: invalid command-line arguments");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
            Engine.MaxPhysicsStepsPerFrame = 100;

        _terrain = new RayDebugDraw(_raycast);
        _terrain.Build(this);
        _drag.Spring = DragSpring;
        _drag.Damping = DragDamping;
        _drag.MaxForce = DragMaxForce;

        _presets = DaddyLongLegsFactory.AllPresets();
        _preset = ResolvePreset(_presetName);
        ApplyAblation(_preset, _ablation);
        _spawn = SpawnForRoute(_route);
        SpawnDaddy(_spawn, _preset, _stableSeed);

        _renderer = new DaddyLongLegsRenderer();
        _renderer.Build(this, _controller);
        _rendererBuilt = true;
        BuildDebugTargetMarker();

        if (_perturb != 0f)
        {
            BodyChunk chunk = _controller.Body.Chunks[0];
            chunk.Pos += Vector3.Back * _perturb;
            chunk.LastPos = chunk.Pos;
            // controller.BodyCenter 是上一次 Tick 的缓存；直接改 Pos 之后必须现算
            // 质量加权质心，否则重捕获是 no-op，微扰位移会被首 tick 的 travel 吸收。
            Vector3 weighted = Vector3.Zero;
            float mass = 0f;
            foreach (BodyChunk c in _controller.Body.Chunks)
            {
                weighted += c.Pos * c.Mass;
                mass += c.Mass;
            }
            _initialCenter = weighted / mass;
            _lastCenter = _initialCenter;
        }

        if (!Deterministic)
        {
            string[] ids = Array.ConvertAll(_presets, p => p.StableId);
            _hud = new DaddyLongLegsSandboxHud();
            _hud.Build(this, ids, SelectPreset, ChangeSeed,
                ToggleDebugTarget, ToggleDebugTargetPull);
            _hud.SyncPreset(Array.FindIndex(_presets, p => p.StableId == _preset.StableId));
        }

        _camera.LookAt(_controller.BodyCenter, Vector3.Up);
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;
        GD.Print($"[DADDY-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.StableId} seed={_stableSeed} route={_route} " +
                 $"ablate={_ablation} " +
                 $"body={_controller.Body.Chunks.Count} tentacles={_controller.Tentacles.Count} " +
                 $"segments={_controller.Morphology.TotalTentacleSegments} " +
                 $"determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnDaddy(Vector3 origin, DaddyLongLegsParams preset, ulong seed)
    {
        _drag.Release();
        _preset = preset;
        _stableSeed = seed;
        _controller = DaddyLongLegsFactory.CreateController(origin, preset, seed);
        _highContactRuns = new int[_controller.Tentacles.Count];
        _blockedLinkRuns = _controller.Tentacles
            .Select(t => new int[t.Segments.Count])
            .ToArray();
        _blockedTentacleRuns = new int[_controller.Tentacles.Count];
        _initialCenter = _controller.BodyCenter;
        _lastCenter = _initialCenter;
        _debugTargetActive = false;
        _externalTentacle = -1;
        if (_rendererBuilt)
            _renderer.Build(this, _controller);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
            return;

        _raycast.Bind(GetWorld3D().DirectSpaceState);
        _tickAccumulator += delta;
        int safety = 0;
        while (_tickAccumulator + 1e-12 >= TickDt && safety++ < 32)
        {
            _tickAccumulator -= TickDt;
            RunCoreTick();
            if (_fatal || (Deterministic && _tick >= _determinismTicks))
                break;
        }
    }

    private void RunCoreTick()
    {
        _tick++;
        _terrain.BeginTick();

        if (Deterministic)
            DriveDeterministicRoute();
        else
        {
            SampleInteractiveMovement();
            ProcessPendingMoveTargetPick();
            MoveDebugTargetFromKeys();
            if (!WantCameraFly)
            {
                _drag.SampleInput(_camera, new[] { _controller.Body });
                _drag.ApplyDragForce();
            }
        }

        FeedExternalTarget();
        _controller.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        ApplyExternalTargetEffects();
        TrackMetrics();

        if (!Deterministic)
            return;

        RecordDeterminism();
        if (_tick >= _determinismTicks)
        {
            int result = DumpDeterministicResult();
            _fatal = true;
            GetTree().Quit(result);
        }
    }

    private void SampleInteractiveMovement()
    {
        if (WantCameraFly)
        {
            _controller.MoveDir = Vector3.Zero;
            _controller.RunSpeed = 0f;
            return;
        }

        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction += Vector3.Forward;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction += Vector3.Back;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction += Vector3.Left;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction += Vector3.Right;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction += Vector3.Down;

        if (direction.LengthSquared() > 1e-10f)
        {
            _controller.MoveTarget = null;
            _controller.MoveDir = direction.Normalized();
            _controller.RunSpeed = 1f;
        }
        else if (_controller.MoveTarget is null)
        {
            _controller.MoveDir = Vector3.Zero;
            _controller.RunSpeed = 0f;
        }
        else
        {
            _controller.RunSpeed = 1f;
        }
    }

    private void ProcessPendingMoveTargetPick()
    {
        if (_pendingMoveTargetPick is not Vector2 mouse)
            return;
        _pendingMoveTargetPick = null;
        Vector3 from = _camera.ProjectRayOrigin(mouse);
        Vector3 to = from + _camera.ProjectRayNormal(mouse) * 160f;
        if (!_terrain.Raycast(from, to, out TerrainHit hit))
            return;
        _controller.MoveTarget = hit.Point;
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 1f;
        GD.Print($"[DADDY-SANDBOX] MoveTarget -> {Format(hit.Point)}");
    }

    private void MoveDebugTargetFromKeys()
    {
        if (!_debugTargetActive)
            return;
        Vector3 delta = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.I)) delta += Vector3.Forward;
        if (Input.IsPhysicalKeyPressed(Key.K)) delta += Vector3.Back;
        if (Input.IsPhysicalKeyPressed(Key.J)) delta += Vector3.Left;
        if (Input.IsPhysicalKeyPressed(Key.L)) delta += Vector3.Right;
        if (Input.IsPhysicalKeyPressed(Key.U)) delta += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.O)) delta += Vector3.Down;
        if (delta.LengthSquared() > 1e-10f)
            _debugTargetPos += delta.Normalized() * 0.08f;
    }

    private void DriveDeterministicRoute()
    {
        if (_route != "lifecycle")
            _controller.MoveTarget = null;
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 0f;

        switch (_route)
        {
            case "flat":
                DriveFlat();
                break;
            case "idle-start":
                DriveIdleStart();
                break;
            case "height-retention":
                DriveHeightRetention();
                break;
            case "tap":
                DriveTap();
                break;
            case "course":
                DriveCourse();
                break;
            case "wall":
                DriveWall();
                break;
            case "wall-idle":
                DriveWallIdle();
                break;
            case "ceiling":
                DriveCeiling();
                break;
            case "corner":
                DriveCorner();
                break;
            case "outer":
                DriveOuterCorner();
                break;
            case "stun":
                DriveFlat();
                DriveStun();
                break;
            case "stuck":
                DriveStuck();
                break;
            case "target":
                DriveExternalTarget();
                break;
            case "launch":
                DriveLaunch();
                break;
            case "lifecycle":
                DriveLifecycle();
                break;
        }
    }

    private void DriveFlat()
    {
        float relative = _controller.BodyCenter.X - _initialCenter.X;
        if (_routeDirection > 0 && relative >= 9f)
        {
            if (_firstFlatReverseTick < 0)
            {
                _firstFlatReverseTick = _tick;
                _firstFlatReverseX = _controller.BodyCenter.X;
                _firstFlatReverseSteps = _controller.StepReleaseSerial;
                _firstFlatReverseStartReplants = _controller.StartReplantSerial;
            }
            _routeDirection = -1;
            _waypointsReached++;
        }
        else if (_routeDirection < 0 && relative <= -2f)
        {
            _routeDirection = 1;
            _waypointsReached++;
        }
        _controller.MoveDir = Vector3.Right * _routeDirection;
        _controller.RunSpeed = 1f;
    }

    private void DriveIdleStart()
    {
        if (_tick <= 120)
        {
            _controller.MoveDir = Vector3.Zero;
            _controller.RunSpeed = 0f;
            return;
        }
        if (!_idleStartCaptured)
        {
            _idleStartCaptured = true;
            _idleStartX = _controller.BodyCenter.X;
        }
        _controller.MoveDir = Vector3.Right;
        _controller.RunSpeed = 1f;
    }

    private void DriveHeightRetention()
    {
        if (_tick <= HeightRetentionStandTicks)
            return;
        if (!_heightRetentionMoveCaptured)
        {
            _heightRetentionMoveCaptured = true;
            _heightRetentionMoveStartX = _controller.BodyCenter.X;
            _heightRetentionStepsBeforeMove = _controller.StepReleaseSerial;
            _heightRetentionStartReplantsBeforeMove = _controller.StartReplantSerial;
            _heightRetentionLandingsBeforeMove = TotalLandingSerial();
            _heightRetentionObservedStepSerials = _controller.Tentacles
                .Select(t => t.StepSerial)
                .ToArray();
            _heightRetentionExplicitReplantInFlight =
                new bool[_controller.Tentacles.Count];
            _heightRetentionTentacleLandingsBeforeMove = _controller.Tentacles
                .Select(t => t.LandingSerial)
                .ToArray();
        }
        _controller.MoveDir = Vector3.Right;
        _controller.RunSpeed = 1f;
    }

    private void DriveTap()
    {
        if (_tick <= 120)
            return;
        if (!_tapCaptured)
        {
            _tapCaptured = true;
            _tapStartX = _controller.BodyCenter.X;
            _tapStartingMovementEpisodes = _controller.MovementEpisodeSerial;
            _tapStartingReplants = _controller.StartReplantSerial;
        }
        // 4 tick 按下 / 4 tick 松开：松键时间远短于触手规划 episode，但身体推进仍
        // 严格只发生在按下 tick。它复现交互中快速连点 W，而不是合成持续油门。
        bool pressed = ((int)(_tick - 121) & 7) < 4;
        if (!pressed)
            return;
        _controller.MoveDir = Vector3.Right;
        _controller.RunSpeed = 1f;
        _tapActiveTicks++;
    }

    private void DriveCourse()
    {
        _controller.MoveDir = Vector3.Right;
        _controller.RunSpeed = 1f;
    }

    private void DriveWall()
    {
        // 出生时已贴近墙脚；只给世界 +Y 意图，落点与支撑自己决定是否沿墙攀附。
        _controller.MoveDir = Vector3.Up;
        _controller.RunSpeed = 1f;
    }

    private void DriveWallIdle()
    {
        // 先在真实 Wall collider 上建立侧面支撑，然后完全撤掉宿主输入。
        // DriveDeterministicRoute 已把 MoveDir/RunSpeed 清零，因此 80 tick 后
        // 这里不再写任何运动量；可以真正观察 episode grace 与落点收尾。
        if (_tick <= WallIdleDriveTicks)
        {
            _controller.MoveDir = Vector3.Up;
            _controller.RunSpeed = 1f;
        }
    }

    private void DriveCeiling()
    {
        // 先向安装面收拢，再把宿主给出的邻近 carrot 沿 underside 往返；carrot 的 Y/Z
        // 同时约束身体别在长触手上越吊越低。它仍只走公开 MoveTarget 输入，核心没有倒挂模式。
        // 避开 x=20 的端墙，让本路线只验证倒挂，墙—顶—墙的连续换面留给 corner。
        if (!_sawCeilingContact || _tick < 120)
        {
            _controller.MoveDir = Vector3.Up;
        }
        else
        {
            float x = _controller.BodyCenter.X;
            if (_routeDirection > 0 && (x >= 17.1f || _controller.AtMoveTarget))
                _routeDirection = -1;
            else if (_routeDirection < 0 && (x <= 10.9f || _controller.AtMoveTarget))
                _routeDirection = 1;
            float targetX = _routeDirection > 0 ? 17.5f : 10.5f;
            _controller.MoveTarget = new Vector3(targetX, 7.35f, 0f);
            _controller.MoveDir = Vector3.Zero;
        }
        _controller.RunSpeed = 1f;
    }

    private void DriveCorner()
    {
        // 先把邻近 carrot 沿墙面上移，取得真实的持续侧面支撑后再喂 ceiling carrot。
        // 长触手在低处已能碰到顶面，不能再只看质心高度，否则会跳过墙面阶段。
        if (!_innerTransitionStarted
            && _maximumWallMountedRun >= 40
            && _controller.BodyCenter.Y >= 4.5f)
        {
            _innerTransitionStarted = true;
        }
        if (_innerTransitionStarted)
        {
            _controller.MoveTarget = new Vector3(0f, 7.1f, 4f);
            _controller.MoveDir = Vector3.Zero;
        }
        else
        {
            float targetY = Math.Min(6.0f, _controller.BodyCenter.Y + 1.2f);
            _controller.MoveTarget = new Vector3(7.55f, targetY, 4.0f);
            _controller.MoveDir = Vector3.Zero;
        }
        _controller.RunSpeed = 1f;
    }

    private void DriveOuterCorner()
    {
        // 从 Ceiling 上表面接近 x=20 的凸棱，身体质心越过外缘后改给世界 -Y；
        // 这是宿主路线 carrot 的纯几何切换，不是控制器 locomotion 模式。
        if (!_outerDescentStarted
            && _maximumOuterTopRun >= 40
            && _controller.BodyCenter.X >= 20.55f)
            _outerDescentStarted = true;
        _controller.MoveTarget = _outerDescentStarted
            ? new Vector3(20.75f, 0.75f, 0f)
            : new Vector3(20.75f, 8.9f, 0f);
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 1f;
    }

    private void DriveStun()
    {
        if (_tick != 320 || _stunIssued)
            return;
        int selected = BestLocomotionTentacle();
        if (selected < 0)
            return;
        _stunIssued = true;
        _stunnedTentacle = selected;
        _supportBeforeStun = _controller.EffectiveSupport;
        _dutySerialBeforeStun = _controller.DutyAssignmentSerial;
        _controller.StunTentacle(selected, 160);
        _sawStunnedZeroSupport = _controller.Tentacles[selected].SupportContribution == 0f;
        GD.Print($"[DADDY-EVENT] stun tick={_tick} tentacle={selected} " +
                 $"support={_supportBeforeStun:F3}");
    }

    private void DriveStuck()
    {
        // StuckBlock 位于 z=12 路带；持续目标放在其背面，让核心自行扩大搜索并抖动脱困。
        _controller.MoveTarget = StuckRouteTarget;
        _controller.RunSpeed = 1f;
    }

    private void DriveExternalTarget()
    {
        if (_tick == 40 && !_debugTargetActive)
        {
            _debugTargetPos = _controller.BodyCenter + new Vector3(3.2f, 0.65f, 0.4f);
            _debugTargetVel = Vector3.Zero;
            _debugTargetPull = true;
            _externalAssignmentSucceeded = AssignDebugTarget();
            _targetInitialDistance = _debugTargetPos.DistanceTo(_controller.BodyCenter);
            GD.Print($"[DADDY-EVENT] external-target tick={_tick} " +
                     $"tentacle={_externalTentacle} assigned={_externalAssignmentSucceeded}");
        }
        if (_tick == 520 && _debugTargetActive)
            ClearDebugTarget();
    }

    private void DriveLaunch()
    {
        if (_tick < 300 || _tick > 410)
        {
            _controller.MoveDir = Vector3.Right;
            _controller.RunSpeed = 1f;
        }
        if (_tick != 300 || _launchIssued)
            return;
        _launchIssued = true;
        _supportBeforeLaunch = _controller.EffectiveSupport;
        Vector3 impulse = new(0.075f, 0.34f, 0.035f);
        Vector3[] bodyVel = SnapshotBodyVelocities();
        Vector3[] segmentVel = SnapshotSegmentVelocities();
        _controller.Launch(impulse);
        _launchContract = CheckVelocityDelta(bodyVel, segmentVel, impulse)
            && AllLandingTargetsReleased()
            && _controller.EffectiveSupport == 0f
            && _controller.Body.GravityScale == 1f;
        _supportImmediatelyAfterLaunch = _controller.EffectiveSupport;
        GD.Print($"[DADDY-EVENT] launch tick={_tick} contract={_launchContract} " +
                 $"supportBefore={_supportBeforeLaunch:F3}");
    }

    private void DriveLifecycle()
    {
        if (_tick == 20)
        {
            _controller.MoveTarget = _controller.BodyCenter + new Vector3(3f, 0f, 0f);
            _controller.RunSpeed = 1f;
            _debugTargetPos = _controller.BodyCenter + new Vector3(-2.5f, 0.8f, 0.4f);
            _debugTargetVel = Vector3.Zero;
            _debugTargetPull = false;
            _externalAssignmentSucceeded = AssignDebugTarget();
        }
        if (_tick == 50)
        {
            Vector3 delta = new(0f, 0f, 1.25f);
            Vector3[] bodyPos = SnapshotBodyPositions();
            Vector3[] segmentPos = SnapshotSegmentPositions();
            Vector3 targetBefore = _controller.MoveTarget ?? Vector3.Zero;
            // tick-20 的指派可能失败（_externalTentacle=-1）；与 tick-210 一样防护，
            // 让契约干净地 FAIL 而不是让索引异常打断整个 tick。
            bool externalTracked = _externalTentacle >= 0;
            Vector3 externalBefore = externalTracked
                ? _controller.Tentacles[_externalTentacle].ExternalTarget?.Position
                    ?? Vector3.Zero
                : Vector3.Zero;
            _controller.Shift(delta);
            _debugTargetPos += delta;
            _shiftContract = CheckPositionDelta(bodyPos, segmentPos, delta)
                && _controller.MoveTarget is Vector3 shifted
                && Near(shifted - targetBefore, delta)
                && externalTracked
                && _controller.Tentacles[_externalTentacle].ExternalTarget is { } external
                && Near(external.Position - externalBefore, delta);
            GD.Print($"[DADDY-EVENT] shift tick={_tick} contract={_shiftContract}");
        }
        if (_tick == 110)
        {
            Vector3 delta = new(0f, 0.45f, 1.5f);
            _controller.Teleport(delta);
            _teleportContract = _controller.MoveTarget is null
                && !_controller.AtMoveTarget
                && AllLandingTargetsReleased()
                && AllExternalTargetsReleased()
                && _controller.EffectiveSupport == 0f
                && _controller.LocomotionTentacleCount == _preset.MinimumLocomotionTentacles;
            _debugTargetActive = false;
            _externalTentacle = -1;
            GD.Print($"[DADDY-EVENT] teleport tick={_tick} contract={_teleportContract}");
        }
        if (_tick == 170)
        {
            _controller.MoveTarget = _controller.BodyCenter + new Vector3(4f, 0f, 0f);
            _controller.RunSpeed = 1f;
            _debugTargetPos = _controller.BodyCenter + new Vector3(-2.8f, 0.6f, 0f);
            _debugTargetVel = Vector3.Zero;
            _debugTargetPull = false;
            AssignDebugTarget();
        }
        if (_tick == 210)
        {
            Vector3? moveTargetBefore = _controller.MoveTarget;
            int targetTentacle = _externalTentacle;
            ulong? externalBefore = targetTentacle >= 0
                ? _controller.Tentacles[targetTentacle].ExternalTarget?.StableId
                : null;
            Vector3 impulse = new(0.06f, 0.30f, -0.025f);
            Vector3[] bodyVel = SnapshotBodyVelocities();
            Vector3[] segmentVel = SnapshotSegmentVelocities();
            _controller.Launch(impulse);
            _lifecycleLaunchContract = CheckVelocityDelta(bodyVel, segmentVel, impulse)
                && AllLandingTargetsReleased()
                && _controller.EffectiveSupport == 0f
                && _controller.Body.GravityScale == 1f;
            _lifecycleMoveTargetRetained = moveTargetBefore is not null
                && _controller.MoveTarget == moveTargetBefore;
            _lifecycleExternalRetained = targetTentacle >= 0
                && externalBefore is not null
                && _controller.Tentacles[targetTentacle].ExternalTarget?.StableId == externalBefore;
            GD.Print($"[DADDY-EVENT] lifecycle-launch tick={_tick} " +
                     $"contract={_lifecycleLaunchContract} " +
                     $"moveTargetRetained={_lifecycleMoveTargetRetained} " +
                     $"externalRetained={_lifecycleExternalRetained}");
        }
        if (_tick == 400)
        {
            // 直喂一个已在到达半径内的邻近点，钉住 AtMoveTarget 的宿主握手语义。
            _controller.MoveTarget = _controller.BodyCenter + Vector3.Right * 0.20f;
            _controller.RunSpeed = 1f;
        }
    }

    private void DriveWaypointSequence(Vector3[] points)
    {
        if (_routeWaypoint >= points.Length)
        {
            _controller.MoveTarget = null;
            return;
        }
        _controller.MoveTarget = points[_routeWaypoint];
        _controller.RunSpeed = 1f;
        if (_controller.AtMoveTarget)
        {
            _sawAtMoveTarget = true;
            _waypointsReached++;
            _routeWaypoint++;
            _controller.MoveTarget = _routeWaypoint < points.Length
                ? points[_routeWaypoint]
                : null;
        }
    }

    private void FeedExternalTarget()
    {
        if (!_debugTargetActive || _externalTentacle < 0
            || _externalTentacle >= _controller.Tentacles.Count)
            return;
        _debugTargetPos += _debugTargetVel;
        _debugTargetVel *= 0.94f;
        var snapshot = new DaddyLongLegsTargetSnapshot(
            DebugTargetId, _debugTargetPos, _debugTargetVel, 0.20f, 1f, _debugTargetPull);
        DaddyTentacle tentacle = _controller.Tentacles[_externalTentacle];
        if (tentacle.Role is DaddyTentacleRole.ExternalReach or DaddyTentacleRole.Idle)
            _controller.TryAssignExternalTarget(_externalTentacle, snapshot);
    }

    private void ApplyExternalTargetEffects()
    {
        foreach (DaddyLongLegsTargetEffect effect in _controller.TargetEffects)
        {
            if (effect.TargetId != DebugTargetId)
                continue;
            _sawTargetReached |= effect.Reached;
            _sawTargetHeld |= effect.Held;
            _sawTargetRelease |= effect.Released;
            if (!_debugTargetActive)
                continue;
            _debugTargetPos += effect.PositionCorrection;
            _debugTargetVel += effect.VelocityDelta;
        }
    }

    private bool AssignDebugTarget()
    {
        int index = _controller.FindIdleTentacle();
        if (index < 0)
            return false;
        var snapshot = new DaddyLongLegsTargetSnapshot(
            DebugTargetId, _debugTargetPos, _debugTargetVel, 0.20f, 1f, _debugTargetPull);
        if (!_controller.TryAssignExternalTarget(index, snapshot))
            return false;
        _externalTentacle = index;
        _debugTargetActive = true;
        return true;
    }

    private void ToggleDebugTarget()
    {
        if (_debugTargetActive)
        {
            ClearDebugTarget();
            return;
        }
        _debugTargetPos = _controller.BodyCenter
            + NormalizeOr(_controller.MaterialAxisX + Vector3.Up * 0.25f, Vector3.Right) * 3.5f;
        _debugTargetVel = Vector3.Zero;
        bool assigned = AssignDebugTarget();
        GD.Print($"[DADDY-SANDBOX] external target assigned={assigned} " +
                 $"tentacle={_externalTentacle}");
    }

    private void ClearDebugTarget()
    {
        if (_externalTentacle >= 0 && _externalTentacle < _controller.Tentacles.Count)
            _controller.ClearExternalTarget(_externalTentacle);
        _debugTargetActive = false;
        _externalTentacle = -1;
    }

    private void ToggleDebugTargetPull()
    {
        _debugTargetPull = !_debugTargetPull;
        GD.Print($"[DADDY-SANDBOX] debug target pull={_debugTargetPull}");
    }

    private void TrackMetrics()
    {
        Vector3 center = _controller.BodyCenter;
        bool wallContactThisTick = false;
        bool ceilingContactThisTick = false;
        bool upwardContactThisTick = false;
        bool positiveXContactThisTick = false;
        float tickTravel = center.DistanceTo(_lastCenter);
        _lastCenter = center;
        _travel += tickTravel;
        _maximumDisplacement = Math.Max(_maximumDisplacement, center.DistanceTo(_initialCenter));
        _maximumForwardProgress = Math.Max(
            _maximumForwardProgress, center.X - _initialCenter.X);
        _maximumHeightGain = Math.Max(_maximumHeightGain, center.Y - _initialCenter.Y);
        _minimumHeightDelta = Math.Min(_minimumHeightDelta, center.Y - _initialCenter.Y);
        _maximumRawSupport = Math.Max(_maximumRawSupport, _controller.RawSupport);
        _maximumEffectiveSupport = Math.Max(_maximumEffectiveSupport, _controller.EffectiveSupport);
        _minimumEffectiveSupport = Math.Min(_minimumEffectiveSupport, _controller.EffectiveSupport);
        _maximumGravityCancellation = Math.Max(
            _maximumGravityCancellation, _controller.GravityCancellation);
        float expectedCancellation =
            _controller.ContinuousSupport * _preset.GravityCancellationGain;
        if (!_preset.EnableSupportOvercompensation)
            expectedCancellation = Math.Min(expectedCancellation, 1f);
        if (_preset.EnableIdleSupportNeutrality
            && _controller.MovementEpisodeSerial > 0
            && !_controller.MoveEpisodeActive)
        {
            expectedCancellation = Math.Min(
                expectedCancellation,
                _preset.IdleGravityCancellationMaximum);
        }
        _maximumGravityMappingError = Math.Max(_maximumGravityMappingError,
            Math.Abs(_controller.GravityCancellation - expectedCancellation));
        _maximumDriveScale = Math.Max(_maximumDriveScale, _controller.DriveScale);
        _maximumLocomotion = Math.Max(_maximumLocomotion, _controller.LocomotionTentacleCount);
        _minimumLocomotion = Math.Min(_minimumLocomotion, _controller.LocomotionTentacleCount);
        _maximumArrived = Math.Max(_maximumArrived, _controller.ArrivedTentacleCount);
        _maximumStuckCounter = Math.Max(_maximumStuckCounter, _controller.StuckCounter);
        _maximumStuckEpisodeSerial = Math.Max(
            _maximumStuckEpisodeSerial, _controller.StuckEpisodeSerial);
        _maximumStepReleaseSerial = Math.Max(
            _maximumStepReleaseSerial, _controller.StepReleaseSerial);
        _maximumStuckAmount = Math.Max(_maximumStuckAmount, _controller.StuckAmount);
        _maximumTickQueries = Math.Max(_maximumTickQueries, _controller.TickQueryCount);
        _maximumBodyConstraintDeviation = Math.Max(
            _maximumBodyConstraintDeviation, _controller.Body.CurrentMaxDeviation());
        _sawAtMoveTarget |= _controller.AtMoveTarget;

        int contacts = 0;
        int passiveContacts = 0;
        int activeGrips = 0;
        int planted = 0;
        int peeling = 0;
        int reaching = 0;
        int locomotionRoles = 0;
        int interiorBlockedLinks = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
        {
            int tentacleContacts = 0;
            int tentacleGrips = 0;
            bool tentacleHasBlockedLink = false;
            if (tentacle.Role == DaddyTentacleRole.Locomotion)
                locomotionRoles++;
            if (tentacle.Task == DaddyTentacleTask.Locomotion && tentacle.StunTicks <= 0)
            {
                switch (tentacle.ReplantPhase)
                {
                    case DaddyTentacleReplantPhase.Planted: planted++; break;
                    case DaddyTentacleReplantPhase.Peeling: peeling++; break;
                    case DaddyTentacleReplantPhase.Reaching: reaching++; break;
                }
            }
            if (tentacle.GuideLength > 1e-5f)
            {
                _maximumGuideContactRatio = Math.Max(
                    _maximumGuideContactRatio,
                    tentacle.GuideContactLength / tentacle.GuideLength);
            }
            _maximumSearchFailure = Math.Max(_maximumSearchFailure, tentacle.SearchFailureTicks);
            _maximumTentacleConstraintError = Math.Max(
                _maximumTentacleConstraintError, tentacle.MaximumConstraintError);
            _maximumTerrainBacktrackSerial = Math.Max(
                _maximumTerrainBacktrackSerial, tentacle.TerrainBacktrackSerial);
            _maximumPostConstraintSweepSerial = Math.Max(
                _maximumPostConstraintSweepSerial, tentacle.PostConstraintSweepSerial);
            if (tentacle.Role == DaddyTentacleRole.ExternalReach
                && tentacle.SupportContribution != 0f)
                _externalSupportLeak = true;
            if (tentacle.HasLandingTarget)
            {
                _maximumLandingReachExcess = Math.Max(
                    _maximumLandingReachExcess,
                    tentacle.Anchor.Pos.DistanceTo(tentacle.LandingPoint)
                        - tentacle.Length * 1.22f);
            }
            for (int segmentIndex = 0; segmentIndex < tentacle.Segments.Count; segmentIndex++)
            {
                DaddyTentacleSegmentState segment = tentacle.Segments[segmentIndex];
                Vector3 predecessor = segmentIndex == 0
                    ? tentacle.Anchor.Pos
                    : tentacle.Segments[segmentIndex - 1].Pos;
                float predecessorRadius = segmentIndex == 0
                    ? tentacle.Anchor.TerrainRadius
                    : tentacle.Segments[segmentIndex - 1].Radius;
                if (LinkInteriorBlocked(
                        predecessor,
                        predecessorRadius,
                        segment.Pos,
                        segment.Radius,
                        out TerrainHit blockedHit))
                {
                    interiorBlockedLinks++;
                    tentacleHasBlockedLink = true;
                    int run = ++_blockedLinkRuns[tentacle.Index][segmentIndex];
                    if (run > _maximumInteriorLinkBlockRun)
                    {
                        _maximumInteriorLinkBlockRun = run;
                        _maximumInteriorLinkBlockTentacle = tentacle.Index;
                        _maximumInteriorLinkBlockSegment = segmentIndex;
                        _maximumInteriorLinkBlockTick = _tick;
                        _maximumInteriorLinkBlockHitPoint = blockedHit.Point;
                        _maximumInteriorLinkBlockHitNormal = blockedHit.Normal;
                        _maximumInteriorLinkBlockFrom = predecessor;
                        _maximumInteriorLinkBlockTo = segment.Pos;
                    }
                }
                else
                {
                    _blockedLinkRuns[tentacle.Index][segmentIndex] = 0;
                }
                _nonFinite |= !Finite(segment.Pos) || !Finite(segment.LastPos)
                    || !Finite(segment.Vel) || !Finite(segment.ContactNormal)
                    || !Finite(segment.GripNormal);
                ObserveResidualPenetration(
                    $"tentacle{tentacle.Index}/segment{segmentIndex}",
                    segment.Pos,
                    segment.Radius);
                if (segment.TerrainContact)
                {
                    contacts++;
                    tentacleContacts++;
                    if (!segment.ActiveGrip)
                        passiveContacts++;
                }
                if (!segment.ActiveGrip)
                    continue;
                activeGrips++;
                tentacleGrips++;
                _activeGripStateMismatch |= !segment.TerrainContact;
                Vector3 normal = NormalizeOr(segment.GripNormal, segment.ContactNormal);
                _sawFloorContact |= normal.Y > 0.65f;
                _sawSlopeContact |= normal.Y is > 0.15f and < 0.97f;
                _sawCeilingContact |= normal.Y < -0.65f;
                _sawWallContact |= Math.Abs(normal.X) > 0.65f
                    || Math.Abs(normal.Z) > 0.65f;
                ceilingContactThisTick |= normal.Y < -0.65f;
                upwardContactThisTick |= normal.Y > 0.65f;
                positiveXContactThisTick |= normal.X > 0.65f;
                wallContactThisTick |= Math.Abs(normal.X) > 0.65f
                    || Math.Abs(normal.Z) > 0.65f;
                if (_tick > 120)
                {
                    _sawCeilingContactAfterMount |= normal.Y < -0.65f;
                    _sawWallContactAfterMount |= Math.Abs(normal.X) > 0.65f
                        || Math.Abs(normal.Z) > 0.65f;
                }
            }
            if (tentacleHasBlockedLink)
            {
                int run = ++_blockedTentacleRuns[tentacle.Index];
                _maximumInteriorTentacleBlockRun = Math.Max(
                    _maximumInteriorTentacleBlockRun, run);
            }
            else
            {
                _blockedTentacleRuns[tentacle.Index] = 0;
            }
            if (_tick > 60 && tentacle.Segments.Count > 0)
            {
                float contactRatio = (float)tentacleContacts / tentacle.Segments.Count;
                float gripRatio = (float)tentacleGrips / tentacle.Segments.Count;
                _maximumActualTentacleContactRatio = Math.Max(
                    _maximumActualTentacleContactRatio, contactRatio);
                _maximumActualTentacleGripRatio = Math.Max(
                    _maximumActualTentacleGripRatio, gripRatio);
                float plannedRatio = tentacle.GuideLength > 1e-5f
                    ? tentacle.GuideContactLength / tentacle.GuideLength
                    : 0f;
                int allowedContacts = (int)MathF.Ceiling(
                    plannedRatio * tentacle.Segments.Count) + 1;
                if (tentacleContacts > allowedContacts)
                {
                    int run = ++_highContactRuns[tentacle.Index];
                    _maximumHighContactRun = Math.Max(_maximumHighContactRun, run);
                }
                else
                {
                    _highContactRuns[tentacle.Index] = 0;
                }
            }
        }
        _maximumContactSegments = Math.Max(_maximumContactSegments, contacts);
        _maximumPassiveContactSegments = Math.Max(
            _maximumPassiveContactSegments, passiveContacts);
        _maximumActiveGripSegments = Math.Max(_maximumActiveGripSegments, activeGrips);
        _maximumPlantedTentacles = Math.Max(_maximumPlantedTentacles, planted);
        _maximumPeelingTentacles = Math.Max(_maximumPeelingTentacles, peeling);
        _maximumReachingTentacles = Math.Max(_maximumReachingTentacles, reaching);
        _currentInteriorBlockedLinks = interiorBlockedLinks;
        _maximumInteriorBlockedLinks = Math.Max(
            _maximumInteriorBlockedLinks, interiorBlockedLinks);
        _dutyCountMismatch |= locomotionRoles != _controller.LocomotionTentacleCount;

        bool bodyOnFloor = false;
        float bodyFloorClearance = float.PositiveInfinity;
        for (int chunkIndex = 0; chunkIndex < _controller.Body.Chunks.Count; chunkIndex++)
        {
            BodyChunk chunk = _controller.Body.Chunks[chunkIndex];
            _nonFinite |= !Finite(chunk.Pos) || !Finite(chunk.LastPos) || !Finite(chunk.Vel);
            bodyFloorClearance = Math.Min(
                bodyFloorClearance, chunk.Pos.Y - chunk.TerrainRadius);
            bodyOnFloor |= chunk.TerrainContact && chunk.ContactNormal.Y > 0.65f;
            ObserveResidualPenetration(
                $"body{chunkIndex}", chunk.Pos, chunk.TerrainRadius);
        }
        if (_route == "tap" && _tick > 200)
        {
            _tapSampleTicks++;
            _tapSupportSum += _controller.EffectiveSupport;
            _tapMinimumBodyClearance = Math.Min(
                _tapMinimumBodyClearance, bodyFloorClearance);
            bool nearGround = bodyOnFloor
                || bodyFloorClearance <= _preset.TerrainSkin + 0.05f;
            if (nearGround)
            {
                _tapNearGroundTicks++;
                _tapNearGroundRun++;
                _tapMaximumNearGroundRun = Math.Max(
                    _tapMaximumNearGroundRun, _tapNearGroundRun);
            }
            else
            {
                _tapNearGroundRun = 0;
            }
        }
        _nonFinite |= !Finite(center) || !Finite(_controller.SupportNormal)
            || !Finite(_controller.MaterialAxisX) || !Finite(_controller.MaterialAxisY)
            || !Finite(_controller.MaterialAxisZ)
            || !float.IsFinite(_controller.RawSupport)
            || !float.IsFinite(_controller.EffectiveSupport)
            || !float.IsFinite(_controller.GravityCancellation)
            || !float.IsFinite(_controller.DriveScale);

        if (_tick >= 80)
        {
            _supportSamples++;
            _supportSum += _controller.EffectiveSupport;
        }
        if (_tick > 120)
        {
            _minimumPostMountHeight = Math.Min(_minimumPostMountHeight, center.Y);
            _maximumPostMountHeight = Math.Max(_maximumPostMountHeight, center.Y);
            _wallMountedRun = wallContactThisTick && center.Y >= 1.5f
                ? _wallMountedRun + 1
                : 0;
            _ceilingMountedRun = ceilingContactThisTick && center.Y >= 6.0f
                ? _ceilingMountedRun + 1
                : 0;
            _maximumWallMountedRun = Math.Max(_maximumWallMountedRun, _wallMountedRun);
            _maximumCeilingMountedRun = Math.Max(_maximumCeilingMountedRun, _ceilingMountedRun);
            _outerTopRun = upwardContactThisTick && center.Y >= 8.45f
                ? _outerTopRun + 1
                : 0;
            _outerWallRun = positiveXContactThisTick
                    && center.X >= 20.0f && center.Y is >= 1.5f and <= 8.4f
                ? _outerWallRun + 1
                : 0;
            _maximumOuterTopRun = Math.Max(_maximumOuterTopRun, _outerTopRun);
            _maximumOuterWallRun = Math.Max(_maximumOuterWallRun, _outerWallRun);
        }

        if (_stunIssued && _stunnedTentacle >= 0)
        {
            DaddyTentacle stunned = _controller.Tentacles[_stunnedTentacle];
            _sawStunnedRole |= stunned.Role == DaddyTentacleRole.Stunned;
            _sawStunnedZeroSupport |= stunned.Role == DaddyTentacleRole.Stunned
                && stunned.SupportContribution == 0f;
            _sawStunTakeover |= _controller.LocomotionTentacleCount
                    >= _preset.MinimumLocomotionTentacles
                && (_controller.DutyAssignmentSerial > _dutySerialBeforeStun
                    || _controller.EffectiveSupport >= _supportBeforeStun * 0.60f);
            _sawStunRecovery |= _tick > 480 && stunned.Role != DaddyTentacleRole.Stunned;
            if (_tick > 480)
                _postStunMaximumSupport = Math.Max(
                    _postStunMaximumSupport, _controller.EffectiveSupport);
        }

        if (!_stuckActivated && _controller.StuckAmount >= 0.75f)
            _stuckActivated = true;
        if (_route == "stuck")
        {
            _stuckMinimumTargetDistance = Math.Min(
                _stuckMinimumTargetDistance, center.DistanceTo(StuckRouteTarget));
            _stuckCrossedObstacle |= center.X > -7.50f;
            _stuckRecoveredAfterCrossing |= _stuckCrossedObstacle
                && _controller.StuckAmount <= 0.15f
                && _controller.AtMoveTarget;
        }

        if (_debugTargetActive)
        {
            float distance = _debugTargetPos.DistanceTo(center);
            _targetMinimumDistance = Math.Min(_targetMinimumDistance, distance);
        }
        if (_launchIssued && _tick > 360)
            _postLaunchMaximumSupport = Math.Max(
                _postLaunchMaximumSupport, _controller.EffectiveSupport);
        if (_firstFlatReverseTick >= 0 && _tick - _firstFlatReverseTick == 100)
        {
            _flatReverseProgress100 = _firstFlatReverseX - center.X;
            _flatReverseSteps100 =
                _controller.StepReleaseSerial - _firstFlatReverseSteps;
            _flatReverseStartReplants100 =
                _controller.StartReplantSerial - _firstFlatReverseStartReplants;
        }
        if (_route == "idle-start" && _idleStartCaptured)
        {
            int elapsed = (int)_tick - 120;
            float progress = center.X - _idleStartX;
            if (elapsed == 20) _idleStartProgress20 = progress;
            if (elapsed == 40) _idleStartProgress40 = progress;
            if (elapsed == 80) _idleStartProgress80 = progress;
        }
        if (_route == "height-retention")
            TrackHeightRetention(center);
        if (_route == "wall-idle")
            TrackWallIdle(center, wallContactThisTick);
    }

    private void TrackHeightRetention(Vector3 center)
    {
        int standWindowStart = HeightRetentionStandTicks - HeightRetentionStandWindow + 1;
        if (_tick >= standWindowStart && _tick <= HeightRetentionStandTicks)
        {
            _heightRetentionStandHeights[_heightRetentionStandSamples++] = center.Y;
        }

        int moveWindowStart = HeightRetentionStandTicks + HeightRetentionMoveTicks
            - HeightRetentionMoveWindow + 1;
        if (_tick < moveWindowStart
            || _tick > HeightRetentionStandTicks + HeightRetentionMoveTicks)
        {
            if (_tick <= HeightRetentionStandTicks)
                return;
        }
        if (_tick > HeightRetentionStandTicks)
        {
            int inFlightReplants = 0;
            for (int i = 0; i < _controller.Tentacles.Count; i++)
            {
                DaddyTentacle tentacle = _controller.Tentacles[i];
                if (i >= _heightRetentionObservedStepSerials.Length)
                    continue;
                if (tentacle.StepSerial > _heightRetentionObservedStepSerials[i])
                {
                    _heightRetentionObservedStepSerials[i] = tentacle.StepSerial;
                    _heightRetentionExplicitReplantInFlight[i] = true;
                }
                if (_heightRetentionExplicitReplantInFlight[i])
                {
                    bool completed = tentacle.ReplantPhase
                            == DaddyTentacleReplantPhase.Planted
                        && tentacle.AtGrabDestination;
                    bool interrupted = tentacle.StunTicks > 0
                        || tentacle.Task != DaddyTentacleTask.Locomotion
                        || tentacle.TerrainRecoveryActive;
                    if (completed || interrupted)
                        _heightRetentionExplicitReplantInFlight[i] = false;
                }
                if (_heightRetentionExplicitReplantInFlight[i])
                    inFlightReplants++;
            }
            _heightRetentionMaximumInFlightReplants = Math.Max(
                _heightRetentionMaximumInFlightReplants, inFlightReplants);
            if (_controller.LastStartReplantTentacleIndex >= 0)
                _heightRetentionStartReplantIndex =
                    _controller.LastStartReplantTentacleIndex;
            if (_heightRetentionStartReplantIndex >= 0)
            {
                DaddyTentacle start =
                    _controller.Tentacles[_heightRetentionStartReplantIndex];
                _heightRetentionStartReplantSawPlanted |=
                    start.ReplantPhase == DaddyTentacleReplantPhase.Planted;
            }
            int requiredArrived = Math.Max(
                _preset.MinimumArrivedTentaclesForStep,
                _controller.Tentacles.Count / 2 + 1);
            if (_controller.ArrivedTentacleCount >= requiredArrived
                && _controller.ActiveReplantTentacleIndex < 0)
            {
                _heightRetentionStepEligibleTicks++;
            }
            (int candidateCount, float bestCancellation) =
                HeightRetentionReleaseCandidates();
            _heightRetentionMaximumCandidateCancellation = Math.Max(
                _heightRetentionMaximumCandidateCancellation, bestCancellation);
            int active = _controller.ActiveReplantTentacleIndex;
            if (_heightRetentionPreviousActiveReplant >= 0 && active < 0
                && _heightRetentionFirstReplantCompletionTick < 0)
            {
                _heightRetentionFirstReplantCompletionTick = _tick;
                _heightRetentionCompletionCandidateCount = candidateCount;
                _heightRetentionCompletionBestCancellation = bestCancellation;
            }
            _heightRetentionPreviousActiveReplant = active;
        }
        if (_tick < moveWindowStart
            || _tick > HeightRetentionStandTicks + HeightRetentionMoveTicks)
        {
            return;
        }
        _heightRetentionMoveHeights[_heightRetentionMoveSamples++] = center.Y;
        _heightRetentionMoveSupportSum += _controller.EffectiveSupport;
        _heightRetentionMinimumMoveSupport = Math.Min(
            _heightRetentionMinimumMoveSupport, _controller.EffectiveSupport);
    }

    private (int Count, float BestCancellation) HeightRetentionReleaseCandidates()
    {
        int count = 0;
        float best = float.NegativeInfinity;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
        {
            if (tentacle.Task != DaddyTentacleTask.Locomotion
                || (!_preset.EnableIndependentLocomotionDuty && !tentacle.NeededForLocomotion)
                || !tentacle.AtGrabDestination
                || !tentacle.HasLandingTarget)
            {
                continue;
            }
            count++;
            float rawAfter = Math.Max(0f,
                _controller.RawSupport
                    - tentacle.SupportContribution / _controller.Tentacles.Count);
            float responseAfter = rawAfter > 0f
                ? MathF.Pow(rawAfter, _preset.SupportResponseExponent)
                : 0f;
            float continuousAfter = _preset.EnableSupport
                ? Math.Max(responseAfter, _controller.UnconditionalSupport)
                : 0f;
            float cancellation = continuousAfter * _preset.GravityCancellationGain;
            if (!_preset.EnableSupportOvercompensation)
                cancellation = Math.Min(cancellation, 1f);
            best = Math.Max(best, cancellation);
        }
        return (count, best);
    }

    private void TrackWallIdle(Vector3 center, bool wallContactThisTick)
    {
        int landingSerial = TotalLandingSerial();
        if (_tick > WallIdleDriveTicks)
            _wallIdleSawWallContactAfterStop |= wallContactThisTick;

        if (_wallIdleGraceEndTick < 0)
        {
            if (_tick <= WallIdleDriveTicks
                || _controller.MoveEpisodeActive
                || _controller.MoveEpisodeGraceTicksRemaining != 0)
            {
                return;
            }
            _wallIdleGraceEndTick = _tick;
            _wallIdleStepReleaseAtGrace = _controller.StepReleaseSerial;
            _wallIdleLandingSerialAtGrace = landingSerial;
            _wallIdleLastLandingSerial = landingSerial;
            return;
        }

        if (landingSerial != _wallIdleLastLandingSerial)
        {
            _wallIdleLandingChangesAfterGrace +=
                Math.Abs(landingSerial - _wallIdleLastLandingSerial);
            _wallIdleLastLandingChangeTick = _tick;
            _wallIdleLastLandingSerial = landingSerial;
        }

        if (!_wallIdleObservationCaptured
            && _tick - _wallIdleGraceEndTick >= WallIdleSettleTicks)
        {
            _wallIdleObservationCaptured = true;
            _wallIdleLandingSerialAtSettle = landingSerial;
            _wallIdleObservationStartY = center.Y;
        }
        if (!_wallIdleObservationCaptured)
            return;

        _wallIdleObservationTicks++;
        _wallIdleMinimumY = Math.Min(_wallIdleMinimumY, center.Y);
        _wallIdleMaximumY = Math.Max(_wallIdleMaximumY, center.Y);
        _wallIdleMinimumSupport = Math.Min(
            _wallIdleMinimumSupport, _controller.EffectiveSupport);
        _wallIdleMaximumSupport = Math.Max(
            _wallIdleMaximumSupport, _controller.EffectiveSupport);
        if (wallContactThisTick)
            _wallIdleWallContactTicksAfterSettle++;
    }

    private int TotalLandingSerial()
    {
        int total = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            total += tentacle.LandingSerial;
        return total;
    }

    private void ValidateHeightRetention(List<string> failures)
    {
        if (!_heightRetentionMoveCaptured)
        {
            failures.Add("height-retention route never entered its movement phase");
            return;
        }
        if (_heightRetentionStandSamples != HeightRetentionStandWindow
            || _heightRetentionMoveSamples != HeightRetentionMoveWindow)
        {
            failures.Add($"height-retention sampled " +
                         $"{_heightRetentionStandSamples}/{HeightRetentionStandWindow} standing and " +
                         $"{_heightRetentionMoveSamples}/{HeightRetentionMoveWindow} moving ticks");
            return;
        }

        float[] standing = _heightRetentionStandHeights.ToArray();
        float[] moving = _heightRetentionMoveHeights.ToArray();
        Array.Sort(standing);
        Array.Sort(moving);
        float standingP10 = standing[standing.Length / 10];
        float standingMedian = standing[standing.Length / 2];
        float movingP10 = moving[moving.Length / 10];
        float movingMedian = moving[moving.Length / 2];
        float medianTentacleLength = _controller.Tentacles
            .Select(t => t.Length)
            .Order()
            .ElementAt(_controller.Tentacles.Count / 2);
        float loss = standingP10 - movingP10;
        float retention = standingP10 > 1e-5f ? movingP10 / standingP10 : 0f;
        float progress = _controller.BodyCenter.X - _heightRetentionMoveStartX;
        int steps = _controller.StepReleaseSerial - _heightRetentionStepsBeforeMove;
        int startReplants = _controller.StartReplantSerial
            - _heightRetentionStartReplantsBeforeMove;
        int landings = TotalLandingSerial() - _heightRetentionLandingsBeforeMove;
        int landingTentacles = _controller.Tentacles
            .Where((t, i) => i < _heightRetentionTentacleLandingsBeforeMove.Length
                && t.LandingSerial > _heightRetentionTentacleLandingsBeforeMove[i])
            .Count();
        float averageSupport = _heightRetentionMoveSupportSum / HeightRetentionMoveWindow;
        DaddyTentacle? startReplant = _heightRetentionStartReplantIndex >= 0
            ? _controller.Tentacles[_heightRetentionStartReplantIndex]
            : null;
        string startReplantState = startReplant is null
            ? "none"
            : $"{startReplant.Index}:{startReplant.ReplantPhase}/" +
              $"at{startReplant.AtGrabDestination}/has{startReplant.HasLandingTarget}/" +
              $"fail{startReplant.SearchFailureTicks}/land{startReplant.LandingSerial}/" +
              $"back{startReplant.BacktrackFrom}/recovery{startReplant.TerrainRecoveryActive}/" +
              $"sawPlanted{_heightRetentionStartReplantSawPlanted}";

        GD.Print($"[DADDY-HEIGHT-RETENTION] seed={_stableSeed} ablate={_ablation} " +
                 $"stand={standingP10:F3}/{standingMedian:F3} " +
                 $"move={movingP10:F3}/{movingMedian:F3} " +
                 $"retain={retention:F3}/loss{loss:F3} " +
                 $"progress={progress:F3} steps={steps}/start{startReplants} " +
                 $"landings={landings}/legs{landingTentacles} " +
                 $"inFlight={_heightRetentionMaximumInFlightReplants} " +
                 $"activeReplant={_controller.ActiveReplantTentacleIndex} " +
                 $"startLeg={startReplantState} eligible={_heightRetentionStepEligibleTicks} " +
                 $"complete={_heightRetentionFirstReplantCompletionTick}/" +
                 $"candidates{_heightRetentionCompletionCandidateCount}/" +
                 $"best{_heightRetentionCompletionBestCancellation:F3}/" +
                 $"max{_heightRetentionMaximumCandidateCancellation:F3} " +
                 $"releaseCancel={_controller.LastStepReleasePredictedGravityCancellation:F3}/" +
                 $"min{_controller.MinimumStepReleasePredictedGravityCancellation:F3}/" +
                 $"gate{_preset.StepReleaseMinimumGravityCancellation:F3} " +
                 $"support={_heightRetentionMinimumMoveSupport:F3}/{averageSupport:F3} " +
                 $"queries={_maximumTickQueries}/{_preset.MaximumTerrainQueriesPerTick} " +
                 $"finite={!_nonFinite}");

        if (standingP10 - _initialCenter.Y < medianTentacleLength * 0.18f)
        {
            failures.Add($"height-retention standing rise was only " +
                         $"{standingP10 - _initialCenter.Y:F2}m");
        }
        if (standingP10 < medianTentacleLength * 0.70f)
        {
            failures.Add($"height-retention standing P10 {standingP10:F2}m was below " +
                         $"0.70xL50={medianTentacleLength * 0.70f:F2}m");
        }
        if (movingP10 < standingP10 * 0.80f
            || loss > Math.Max(0.65f, medianTentacleLength * 0.10f))
        {
            failures.Add($"height-retention moving P10 fell from {standingP10:F2}m to " +
                         $"{movingP10:F2}m (retain={retention:F2}, loss={loss:F2}m)");
        }
        // seed 1/33 有普通换步余量，20m 门能把同面候选退化后的粘滞慢行打红；
        // seed 93 的稳定高姿态只有起步释放余量，不能为了凑普通 Peeling 次数而降低支撑门。
        float minimumProgress = _stableSeed switch
        {
            1UL or 33UL => 20f,
            93UL => 15f,
            _ => 5f,
        };
        if (progress < minimumProgress)
        {
            failures.Add($"height-retention progress was only {progress:F2}m " +
                         $"(< {minimumProgress:F2}m for seed {_stableSeed})");
        }
        int minimumSteps = _stableSeed is 1UL or 33UL ? 4 : 1;
        if (steps < minimumSteps || startReplants < 1
            || landings < Math.Max(4, steps) || landingTentacles < 2)
        {
            failures.Add($"height-retention completed only {steps} step release(s), " +
                         $"{startReplants} start replant(s), and {landings} landing(s) " +
                         $"across {landingTentacles} tentacle(s)");
        }
        if (!_heightRetentionStartReplantSawPlanted
            || _controller.ActiveStartReplantTentacleIndex >= 0)
        {
            failures.Add($"height-retention start replant did not complete " +
                         $"(leg={_heightRetentionStartReplantIndex}, " +
                         $"planted={_heightRetentionStartReplantSawPlanted}, " +
                         $"active={_controller.ActiveStartReplantTentacleIndex})");
        }
        if (_heightRetentionMaximumInFlightReplants > 1)
        {
            failures.Add($"height-retention had " +
                         $"{_heightRetentionMaximumInFlightReplants} replants in flight at once");
        }
        if (averageSupport < 0.15f || _heightRetentionMinimumMoveSupport < 0f)
        {
            failures.Add($"height-retention moving support was " +
                         $"{_heightRetentionMinimumMoveSupport:F3}/{averageSupport:F3}");
        }
        if (_preset.EnableMoveStartAssist)
            failures.Add("height-retention unexpectedly enabled the deprecated start assist");
    }

    private void RecordDeterminism()
    {
        _controller.FoldDeterministicState(_hasher);
        _hasher.Fold(_debugTargetActive);
        _hasher.Fold(_debugTargetPull);
        _hasher.Fold(_externalTentacle);
        if (_debugTargetActive)
        {
            _hasher.Fold(_debugTargetPos);
            _hasher.Fold(_debugTargetVel);
        }
        if (_tick % 250 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[DADDY-DET] tick={_tick} hash={_hasher.Value:X16} " +
                     $"center={Format(_controller.BodyCenter)} " +
                     $"support={_controller.RawSupport:F3}/{_controller.EffectiveSupport:F3} " +
                     $"gravityScale={_controller.Body.GravityScale:F3} " +
                     $"duties={_controller.LocomotionTentacleCount}/" +
                     $"{_controller.ArrivedTentacleCount}/{_controller.Tentacles.Count} " +
                     $"touch/grip={CountContactSegments()}/{CountActiveGripSegments()} " +
                     $"queries={_controller.TickQueryCount}");
        }
    }

    private int DumpDeterministicResult()
    {
        float averageSupport = _supportSamples > 0 ? _supportSum / _supportSamples : 0f;
        float tapAverageSupport = _tapSampleTicks > 0
            ? _tapSupportSum / _tapSampleTicks
            : 0f;
        float tapNearGroundRatio = _tapSampleTicks > 0
            ? (float)_tapNearGroundTicks / _tapSampleTicks
            : 0f;
        float tapProgress = _tapCaptured
            ? _controller.BodyCenter.X - _tapStartX
            : float.NegativeInfinity;
        int wallIdleFinalLandingSerial = TotalLandingSerial();
        int wallIdleSettleLandingChanges = _wallIdleLandingSerialAtSettle
            - _wallIdleLandingSerialAtGrace;
        int wallIdlePostSettleLandingChanges = wallIdleFinalLandingSerial
            - _wallIdleLandingSerialAtSettle;
        float wallIdleHeightRange = _wallIdleObservationCaptured
            ? _wallIdleMaximumY - _wallIdleMinimumY
            : float.PositiveInfinity;
        float wallIdleHeightDrift = _wallIdleObservationCaptured
            ? _controller.BodyCenter.Y - _wallIdleObservationStartY
            : float.PositiveInfinity;
        float wallIdleSupportRange = _wallIdleObservationCaptured
            ? _wallIdleMaximumSupport - _wallIdleMinimumSupport
            : float.PositiveInfinity;
        bool wallIdleStartLockClear = _controller.ActiveStartReplantTentacleIndex < 0
            && !_controller.StartReplantPending
            && !_controller.MoveEpisodeActive
            && _controller.MoveEpisodeDirection.LengthSquared() <= 1e-10f
            && _controller.Tentacles.All(t => !t.StartReplantActive
                && t.StartReplantDirection.LengthSquared() <= 1e-10f);
        if (_route == "wall-idle")
        {
            GD.Print($"[DADDY-WALL-IDLE] grace={_wallIdleGraceEndTick}/" +
                     $"{_controller.MoveEpisodeGraceTicksRemaining} " +
                     $"step={_wallIdleStepReleaseAtGrace}->{_controller.StepReleaseSerial} " +
                     $"landing={_wallIdleLandingSerialAtGrace}->" +
                     $"{_wallIdleLandingSerialAtSettle}->{wallIdleFinalLandingSerial}/" +
                     $"changes{_wallIdleLandingChangesAfterGrace}/" +
                     $"last{_wallIdleLastLandingChangeTick} " +
                     $"lockClear={wallIdleStartLockClear} " +
                     $"height={wallIdleHeightRange:F3}/drift{wallIdleHeightDrift:F3} " +
                     $"support={_wallIdleMinimumSupport:F3}/" +
                     $"{_wallIdleMaximumSupport:F3}/range{wallIdleSupportRange:F3} " +
                     $"wall={_wallIdleSawWallContactAfterStop}/" +
                     $"{_wallIdleWallContactTicksAfterSettle}/" +
                     $"observe{_wallIdleObservationTicks}");
            GD.Print("[DADDY-WALL-IDLE-LEGS] " + string.Join(' ',
                _controller.Tentacles.Select(t =>
                    $"{t.Index}:land{t.LandingSerial}/search{t.SearchSerial}/" +
                    $"invalid{t.ReachInvalidationSerial}," +
                    $"{t.ValidationInvalidationSerial}," +
                    $"{t.ResidualInvalidationSerial}/recover" +
                    $"{t.ResidualRecoverySerial}/has{t.HasLandingTarget}/" +
                    $"phase{t.ReplantPhase}/failure{t.SearchFailureTicks}")));
        }
        GD.Print("[DADDY-BACKTRACK] " + string.Join(' ',
            _controller.Tentacles.Select(t =>
                $"{t.Index}:from{t.BacktrackFrom}/max{t.MaximumBarrierTicks}/" +
                $"recover{t.TerrainBacktrackSerial}/sweep{t.PostConstraintSweepSerial}")));
        GD.Print($"[DADDY-METRIC] route={_route} preset={_preset.StableId} seed={_stableSeed} " +
                 $"morph={_controller.Body.Chunks.Count}/{_controller.Tentacles.Count}/" +
                 $"{_controller.Morphology.TotalTentacleSegments} travel={_travel:F3} " +
                 $"displacement={_maximumDisplacement:F3} forward={_maximumForwardProgress:F3} " +
                 $"height={_minimumHeightDelta:F3}/" +
                 $"{_maximumHeightGain:F3} support={averageSupport:F3}/" +
                 $"{_maximumRawSupport:F3}/{_maximumEffectiveSupport:F3} " +
                 $"gravityCancel={_maximumGravityCancellation:F3} drive={_maximumDriveScale:F3} " +
                 $"duties={_minimumLocomotion}/{_maximumLocomotion} arrived={_maximumArrived} " +
                 $"touch={_maximumContactSegments}/passive{_maximumPassiveContactSegments}/" +
                 $"grip{_maximumActiveGripSegments} phases={_maximumPlantedTentacles}/" +
                 $"{_maximumPeelingTentacles}/{_maximumReachingTentacles} " +
                 $"guideContact={_maximumGuideContactRatio:F3} " +
                 $"actualContact={_maximumActualTentacleContactRatio:F3}/" +
                 $"{_maximumActualTentacleGripRatio:F3}/excessRun{_maximumHighContactRun} " +
                 $"idleStart={_idleStartProgress20:F3}/{_idleStartProgress40:F3}/" +
                 $"{_idleStartProgress80:F3} tap={tapProgress:F3}/active{_tapActiveTicks}/" +
                 $"support{tapAverageSupport:F3}/clear{_tapMinimumBodyClearance:F3}/" +
                 $"ground{tapNearGroundRatio:F3}/run{_tapMaximumNearGroundRun}/" +
                 $"episode{_controller.MovementEpisodeSerial - _tapStartingMovementEpisodes}/" +
                 $"start{_controller.StartReplantSerial - _tapStartingReplants} " +
                 $"stuck={_maximumStuckCounter}/" +
                 $"{_maximumStuckAmount:F3}/episodes{_maximumStuckEpisodeSerial} " +
                 $"stepRelease={_maximumStepReleaseSerial}/" +
                 $"forced{_controller.StuckForcedStepReleaseSerial} " +
                 $"reverse100={_flatReverseProgress100:F3}/steps{_flatReverseSteps100}/" +
                 $"start{_flatReverseStartReplants100} " +
                 $"searchFail={_maximumSearchFailure} " +
                 $"stuckEscape={_stuckCrossedObstacle}/{_stuckRecoveredAfterCrossing}/" +
                 $"{_stuckMinimumTargetDistance:F3} " +
                 $"waypoints={_waypointsReached} queries={_maximumTickQueries}/" +
                 $"{_preset.MaximumTerrainQueriesPerTick} constraint=" +
                 $"{_maximumBodyConstraintDeviation:F4}/{_maximumTentacleConstraintError:F4} " +
                 $"linkBlock={_maximumInteriorBlockedLinks}/" +
                 $"run{_maximumInteriorLinkBlockRun}/" +
                 $"episode{_maximumInteriorTentacleBlockRun}/" +
                 $"end{_currentInteriorBlockedLinks} " +
                 $"blockDetail={_maximumInteriorLinkBlockTentacle}/" +
                 $"{_maximumInteriorLinkBlockSegment}/tick{_maximumInteriorLinkBlockTick}/" +
                 $"hit{Format(_maximumInteriorLinkBlockHitPoint)}/" +
                 $"normal{Format(_maximumInteriorLinkBlockHitNormal)}/" +
                 $"ends{Format(_maximumInteriorLinkBlockFrom)}->" +
                 $"{Format(_maximumInteriorLinkBlockTo)} " +
                 $"backtrack={_maximumTerrainBacktrackSerial}/" +
                 $"sweep{_maximumPostConstraintSweepSerial} " +
                 $"reachExcess={_maximumLandingReachExcess:F5} " +
                 $"penetration={_maximumResidualPenetration:F5}@" +
                 $"{_maximumPenetrationPart}/tick{_maximumPenetrationTick} " +
                 $"mountedRuns={_maximumWallMountedRun}/{_maximumCeilingMountedRun}/" +
                 $"{_maximumOuterTopRun}/{_maximumOuterWallRun}");

        var failures = new List<string>();
        ValidateGeneral(failures);
        switch (_route)
        {
            case "flat":
                RequireGroundTravel(failures, 1.0f);
                break;
            case "idle-start":
                RequireGroundTravel(failures, 0.75f);
                if (!_idleStartCaptured)
                    failures.Add("idle-start route never entered its movement phase");
                if (_idleStartProgress20 < 0.25f || _idleStartProgress40 < 0.70f
                    || _idleStartProgress80 < 1.40f)
                {
                    failures.Add($"idle-start progress was only " +
                                 $"{_idleStartProgress20:F2}/{_idleStartProgress40:F2}/" +
                                 $"{_idleStartProgress80:F2}m at 20/40/80 ticks");
                }
                break;
            case "height-retention":
                RequireGroundTravel(failures, 2.0f);
                ValidateHeightRetention(failures);
                break;
            case "tap":
                RequireGroundTravel(failures, 1.0f);
                if (!_tapCaptured || _tapActiveTicks == 0 || _tapSampleTicks == 0)
                    failures.Add("tap route never entered its pulsed movement phase");
                if (_preset.EnableMoveStartAssist)
                    failures.Add("tap route unexpectedly enabled the deprecated start assist");
                if (tapProgress < 3.0f)
                    failures.Add($"tap route advanced only {tapProgress:F2}m");
                if (tapAverageSupport < 0.55f)
                    failures.Add($"tap support averaged only {tapAverageSupport:F3}");
                if (tapNearGroundRatio > 0.10f || _tapMaximumNearGroundRun > 20)
                {
                    failures.Add($"tap input left the body on the floor for " +
                                 $"{tapNearGroundRatio:P1}, max run " +
                                 $"{_tapMaximumNearGroundRun} tick(s)");
                }
                if (_controller.MovementEpisodeSerial - _tapStartingMovementEpisodes != 1)
                {
                    failures.Add("4-on/4-off input was split into multiple movement episodes");
                }
                if (_controller.StartReplantSerial - _tapStartingReplants != 1
                    || _controller.LastStartReplantReleaseDot
                        > _preset.StartReplantReleaseDotMaximum + 1e-5f)
                {
                    failures.Add($"tap start replant count/dot was " +
                                 $"{_controller.StartReplantSerial - _tapStartingReplants}/" +
                                 $"{_controller.LastStartReplantReleaseDot:F3}");
                }
                if (_controller.ActiveStartReplantTentacleIndex >= 0)
                    failures.Add("tap start replant never completed");
                if (_controller.MinimumStepReleasePredictedGravityCancellation + 1e-5f
                    < _preset.StepReleaseMinimumGravityCancellation)
                {
                    failures.Add("tap replant violated the post-release gravity reserve");
                }
                break;
            case "course":
                RequireGroundTravel(failures, 2.0f);
                if (!_sawSlopeContact)
                    failures.Add("course never contacted a non-horizontal walkable surface");
                break;
            case "wall":
                if (!_sawWallContactAfterMount)
                    failures.Add("wall route had no side-surface contact after the initial 120 ticks");
                if (_maximumWallMountedRun < 80)
                    failures.Add($"wall route sustained high wall contact for only " +
                                 $"{_maximumWallMountedRun} tick(s)");
                if (_controller.BodyCenter.Y < 1.5f)
                    failures.Add($"wall route ended on the floor (y={_controller.BodyCenter.Y:F2})");
                break;
            case "wall-idle":
                if (_wallIdleGraceEndTick < 0)
                    failures.Add("wall-idle movement episode never left its grace window");
                if (!_wallIdleObservationCaptured || _wallIdleObservationTicks < 200)
                    failures.Add($"wall-idle stable observation covered only " +
                                 $"{_wallIdleObservationTicks} tick(s)");
                if (!_wallIdleSawWallContactAfterStop
                    || _wallIdleWallContactTicksAfterSettle < 40)
                {
                    failures.Add($"wall-idle lacked real side-surface support after stop " +
                                 $"({_wallIdleSawWallContactAfterStop}/" +
                                 $"{_wallIdleWallContactTicksAfterSettle})");
                }
                if (_controller.StepReleaseSerial != _wallIdleStepReleaseAtGrace)
                {
                    failures.Add($"wall-idle released locomotion steps after grace " +
                                 $"({_wallIdleStepReleaseAtGrace}->" +
                                 $"{_controller.StepReleaseSerial})");
                }
                if (wallIdleSettleLandingChanges > _controller.Tentacles.Count)
                {
                    failures.Add($"wall-idle changed {wallIdleSettleLandingChanges} landing(s) " +
                                 $"during its bounded settle window");
                }
                if (wallIdlePostSettleLandingChanges != 0
                    || _wallIdleLastLandingChangeTick
                        > _wallIdleGraceEndTick + WallIdleSettleTicks)
                {
                    failures.Add($"wall-idle landing targets kept changing after settle " +
                                 $"(post={wallIdlePostSettleLandingChanges}, " +
                                 $"last={_wallIdleLastLandingChangeTick})");
                }
                if (!wallIdleStartLockClear)
                    failures.Add("wall-idle retained a stale start-replant direction lock");
                if (wallIdleHeightRange > 0.35f || Math.Abs(wallIdleHeightDrift) > 0.15f)
                {
                    failures.Add($"wall-idle body kept bobbing/drifting " +
                                 $"(range={wallIdleHeightRange:F3}, " +
                                 $"drift={wallIdleHeightDrift:F3})");
                }
                if (wallIdleSupportRange > 0.15f)
                {
                    failures.Add($"wall-idle support kept oscillating " +
                                 $"(range={wallIdleSupportRange:F3})");
                }
                break;
            case "ceiling":
                if (!_sawCeilingContactAfterMount)
                    failures.Add("ceiling route had no downward-normal contact after the initial 120 ticks");
                if (_maximumCeilingMountedRun < 80)
                    failures.Add($"ceiling route sustained underside contact for only " +
                                 $"{_maximumCeilingMountedRun} tick(s)");
                if (_controller.BodyCenter.Y < 5.5f)
                    failures.Add($"ceiling route ended away from the ceiling (y={_controller.BodyCenter.Y:F2})");
                if (Math.Abs(_controller.BodyCenter.X - _initialCenter.X) < 0.50f)
                    failures.Add("ceiling route did not translate along the underside");
                break;
            case "corner":
                if (!_sawWallContactAfterMount || !_sawCeilingContactAfterMount)
                    failures.Add($"corner transition missing sustained surfaces " +
                                 $"wall={_sawWallContactAfterMount} ceiling={_sawCeilingContactAfterMount}");
                if (_maximumWallMountedRun < 40 || _maximumCeilingMountedRun < 40)
                    failures.Add($"corner route lacked sustained wall/ceiling windows " +
                                 $"({_maximumWallMountedRun}/{_maximumCeilingMountedRun})");
                if (_controller.BodyCenter.Y < 5.5f)
                    failures.Add($"corner route ended off the upper surface (y={_controller.BodyCenter.Y:F2})");
                break;
            case "outer":
                if (!_outerDescentStarted)
                    failures.Add("outer-corner route never crossed the ceiling edge");
                if (_maximumOuterTopRun < 40 || _maximumOuterWallRun < 40)
                    failures.Add($"outer-corner lacked sustained top/wall contact windows " +
                                 $"({_maximumOuterTopRun}/{_maximumOuterWallRun})");
                if (_controller.BodyCenter.X < 19.8f || _controller.BodyCenter.Y > 1.5f)
                    failures.Add($"outer-corner did not complete its outside-wall descent " +
                                 $"(center={Format(_controller.BodyCenter)})");
                break;
            case "stun":
                ValidateStun(failures);
                break;
            case "stuck":
                // 这些配置实测经 detour + 普通换步脱困，强制豁免路径由无引擎 smoke 的
                // 直构探针钉住；这里分别断言 stuck 真的启动过（量达 0.75 且锁存过
                // detour episode）与换步循环在整段路线中存活。
                if (!_stuckActivated)
                    failures.Add($"stuck amount never reached 0.75 " +
                                 $"(max={_maximumStuckAmount:F2})");
                if (_maximumStuckEpisodeSerial == 0)
                    failures.Add("stuck detour never engaged");
                if (_maximumStepReleaseSerial == 0)
                    failures.Add("no locomotion replant occurred during the stuck route");
                if (!_stuckCrossedObstacle)
                    failures.Add($"stuck recovery never crossed the Block far face " +
                                 $"(center={Format(_controller.BodyCenter)})");
                if (_stuckMinimumTargetDistance >= 0.60f)
                    failures.Add($"stuck recovery never approached the target " +
                                 $"(min={_stuckMinimumTargetDistance:F2}m)");
                if (!_stuckRecoveredAfterCrossing)
                    failures.Add($"stuck amount never fell after crossing " +
                                 $"(final={_controller.StuckAmount:F2}, arrived={_controller.AtMoveTarget})");
                break;
            case "target":
                ValidateExternalTarget(failures);
                break;
            case "launch":
                ValidateLaunch(failures);
                break;
            case "lifecycle":
                if (!_shiftContract) failures.Add("Shift did not translate every owned position/target");
                if (!_teleportContract) failures.Add("Teleport did not clear stale locomotion/external state");
                if (!_lifecycleLaunchContract) failures.Add("Launch impulse/release contract failed");
                if (!_lifecycleMoveTargetRetained) failures.Add("Launch did not retain MoveTarget");
                if (!_lifecycleExternalRetained) failures.Add("Launch did not retain external target assignment");
                if (!_sawAtMoveTarget) failures.Add("MoveTarget arrival signal was never observed");
                break;
        }

        if (_expectHash is { } expected && _hasher.Value != expected)
            failures.Add($"hash {_hasher.Value:X16} != expected {expected:X16}");

        if (failures.Count == 0)
        {
            GD.Print($"[DADDY-RESULT] PASS hash={_hasher.Value:X16} route={_route} " +
                     $"preset={_preset.StableId} seed={_stableSeed}");
            return 0;
        }
        GD.Print($"[DADDY-RESULT] FAIL hash={_hasher.Value:X16}: " + string.Join("; ", failures));
        return 1;
    }

    private void ValidateGeneral(List<string> failures)
    {
        if (_nonFinite) failures.Add("non-finite body/tentacle/controller state observed");
        if (_controller.QueryBudgetExceeded)
            failures.Add("terrain query budget was exhausted");
        if (_maximumTickQueries > _preset.MaximumTerrainQueriesPerTick)
            failures.Add($"query peak {_maximumTickQueries} exceeds {_preset.MaximumTerrainQueriesPerTick}");
        if (_maximumGravityMappingError > 2e-5f)
            failures.Add($"continuous gravity mapping error {_maximumGravityMappingError:E3}");
        if (Math.Abs(_controller.Body.GravityScale - 1f) > 1e-7f)
            failures.Add($"Daddy must keep shared Body gravity fully enabled " +
                         $"(scale={_controller.Body.GravityScale:F6})");
        if (_maximumLandingReachExcess > 0.005f)
            failures.Add($"stale landing exceeded the reachable envelope by " +
                         $"{_maximumLandingReachExcess:F4}m");
        if (_maximumResidualPenetration > MaximumAllowedResidualPenetration)
            failures.Add($"tick-end body/tentacle penetration " +
                         $"{_maximumResidualPenetration:F5}m > " +
                         $"{MaximumAllowedResidualPenetration:F3}m at " +
                         $"{_maximumPenetrationPart}/tick{_maximumPenetrationTick}");
        if (_route is "wall" or "wall-idle" or "ceiling" or "corner" or "outer" or "stuck")
        {
            // phase0 在开半空间通常会于释放 tick 原子成功；真实内外角还可能因近端链
            // 自避而需要后续 Fibonacci 半球候选。契约上限必须覆盖完整有限候选集，
            // 同时仍要求终态零阻断；关闭恢复机制的无引擎消融另会真红。
            int maximumBlockRun = _preset.TerrainBacktrackReleaseTicks
                + _preset.TerrainBacktrackCandidatePhases - 1;
            if (_maximumInteriorLinkBlockRun > maximumBlockRun)
            {
                failures.Add($"an adjacent tentacle link stayed terrain-blocked for " +
                             $"{_maximumInteriorLinkBlockRun} tick(s), limit {maximumBlockRun}");
            }
            if (_maximumInteriorTentacleBlockRun > maximumBlockRun)
            {
                failures.Add($"a tentacle had some terrain-blocked link for " +
                             $"{_maximumInteriorTentacleBlockRun} consecutive tick(s), " +
                             $"limit {maximumBlockRun}");
            }
            if (_currentInteriorBlockedLinks != 0)
            {
                failures.Add($"route ended with {_currentInteriorBlockedLinks} " +
                             "adjacent tentacle link(s) crossing terrain");
            }
        }
        if (_maximumContactSegments == 0 && _route is not "lifecycle")
            failures.Add("no tentacle segment ever contacted terrain");
        if (_maximumActiveGripSegments == 0 && _route is not "lifecycle")
            failures.Add("no terrain contact became an active locomotion grip");
        if (_activeGripStateMismatch)
            failures.Add("ActiveGrip existed without matching physical TerrainContact");
        if (_controller.Body.Chunks.Count < _preset.MinimumBodyChunks
            || _controller.Body.Chunks.Count > _preset.MaximumBodyChunks)
            failures.Add("body morphology count fell outside preset range");
        if (_controller.Tentacles.Count < _preset.MinimumTentacles
            || _controller.Tentacles.Count > _preset.MaximumTentacles)
            failures.Add("tentacle morphology count fell outside preset range");
        if (_controller.Morphology.TotalTentacleSegments > _preset.MaximumTotalTentacleSegments)
            failures.Add("morphology exceeded total segment cap");
        if (_externalSupportLeak)
            failures.Add("ExternalReach tentacle leaked locomotion support");
        if (_dutyCountMismatch)
            failures.Add("LocomotionTentacleCount diverged from per-tentacle roles");
    }

    private void ObserveResidualPenetration(string part, Vector3 center, float radius)
    {
        if (!_raycast.SpherePenetration(center, radius, out _, out float depth)
            || depth <= _maximumResidualPenetration)
        {
            return;
        }
        _maximumResidualPenetration = depth;
        _maximumPenetrationPart = part;
        _maximumPenetrationTick = _tick;
    }

    /// <summary>
    /// 沙盒真断言只认相邻物理段的内部阻挡：命中若落在任一端球的包络内，属于合法贴面或
    /// 棱角接触；命中位于两个端球之间才表示直线渲染/约束链实际穿过 collider。
    /// 这里走同一 ITerrainQuery 接缝，但不进入核心查询预算或运动反馈。
    /// </summary>
    private bool LinkInteriorBlocked(
        Vector3 from,
        float fromRadius,
        Vector3 to,
        float toRadius,
        out TerrainHit interiorHit)
    {
        interiorHit = default;
        Vector3 delta = to - from;
        float length = delta.Length();
        float fromClearance = fromRadius;
        float toClearance = toRadius;
        if (length <= fromClearance + toClearance)
            return false;
        Vector3 direction = delta / length;
        if (!_raycast.Raycast(
                from + direction * fromClearance,
                to - direction * toClearance,
                out TerrainHit hit))
        {
            return false;
        }
        interiorHit = hit;
        return true;
    }

    private void RequireGroundTravel(List<string> failures, float minimumTravel)
    {
        if (!_sawFloorContact) failures.Add("ground route never saw an upward surface contact");
        if (_travel < minimumTravel) failures.Add($"ground travel {_travel:F2}m < {minimumTravel:F2}m");
        if (_maximumForwardProgress < 0.75f)
            failures.Add($"directed +X progress {_maximumForwardProgress:F2}m < 0.75m");
        if (_maximumEffectiveSupport < 0.10f)
            failures.Add($"effective support peaked at only {_maximumEffectiveSupport:F3}");
        if (_maximumGravityCancellation < 0.10f)
            failures.Add($"gravity cancellation peaked at only {_maximumGravityCancellation:F3}");
        if (_route is "flat" or "idle-start" or "tap"
            && _maximumHighContactRun > _preset.StepPeelMaximumTicks + 4)
        {
            failures.Add($"a tentacle exceeded its planned contact band plus one " +
                         $"transition segment for {_maximumHighContactRun} ticks after settling");
        }
        if (_route != "flat")
            return;
        float medianTentacleLength = _controller.Tentacles
            .Select(t => t.Length)
            .Order()
            .ElementAt(_controller.Tentacles.Count / 2);
        if (_maximumHeightGain < medianTentacleLength * 0.20f)
            failures.Add($"body rose only {_maximumHeightGain:F2}m " +
                         $"(< 0.20×L50={medianTentacleLength * 0.20f:F2}m)");
        if (_waypointsReached < 2)
            failures.Add($"flat gait completed only {_waypointsReached} direction change(s)");
        if (_firstFlatReverseTick < 0 || _flatReverseProgress100 < 0.30f)
            failures.Add($"180-degree turn recovered only {_flatReverseProgress100:F2}m " +
                         "within 100 ticks");
        if (_flatReverseSteps100 < 1)
        {
            failures.Add($"180-degree turn replanted only {_flatReverseSteps100} " +
                         $"tentacle(s) within 100 ticks (directional start replants: " +
                         $"{_flatReverseStartReplants100})");
        }
        if (_maximumStuckCounter >= _preset.StuckRiseTicks)
            failures.Add($"flat gait needed stuck fallback (counter={_maximumStuckCounter})");
    }

    private void ValidateStun(List<string> failures)
    {
        if (!_stunIssued) failures.Add("stun event was not issued");
        if (!_sawStunnedRole || !_sawStunnedZeroSupport)
            failures.Add("stunned tentacle did not become limp and support-free");
        if (!_sawStunTakeover)
            failures.Add("remaining tentacles did not take over the locomotion budget");
        if (!_sawStunRecovery)
            failures.Add("stunned tentacle did not leave Stunned after its timer");
        if (_postStunMaximumSupport < 0.08f)
            failures.Add($"post-stun support recovered only to {_postStunMaximumSupport:F3}");
        if (_minimumHeightDelta < -4f)
            failures.Add($"body lost control after stun (height delta {_minimumHeightDelta:F2}m)");
    }

    private void ValidateExternalTarget(List<string> failures)
    {
        if (!_externalAssignmentSucceeded) failures.Add("idle tentacle rejected external assignment");
        if (!_sawTargetReached || !_sawTargetHeld)
            failures.Add($"external target was not reached/held ({_sawTargetReached}/{_sawTargetHeld})");
        if (!_sawTargetRelease) failures.Add("clearing external target emitted no release effect");
        if (!float.IsFinite(_targetInitialDistance) || !float.IsFinite(_targetMinimumDistance)
            || _targetMinimumDistance > _targetInitialDistance - 0.15f)
            failures.Add($"pull did not shorten target distance " +
                         $"({_targetInitialDistance:F2}->{_targetMinimumDistance:F2})");
    }

    private void ValidateLaunch(List<string> failures)
    {
        if (!_launchIssued || !_launchContract)
            failures.Add("Launch did not inject all velocities and clear support/landings");
        if (_supportBeforeLaunch < 0.08f)
            failures.Add($"Launch was not tested from a supported state ({_supportBeforeLaunch:F3})");
        if (_supportImmediatelyAfterLaunch != 0f)
            failures.Add($"Launch support did not collapse to zero ({_supportImmediatelyAfterLaunch:F3})");
        if (_postLaunchMaximumSupport < 0.08f)
            failures.Add($"support did not recover after Launch ({_postLaunchMaximumSupport:F3})");
    }

    public override void _Process(double delta)
    {
        if (_fatal)
            return;
        if (Deterministic)
            UpdateShowcaseCamera();
        else
            UpdateCameraFly((float)delta);

        // _tickAccumulator 只在 _PhysicsProcess 里变化：tps=40 时每物理帧被精确排空，
        // 单独用它得到恒 0 的 alpha（渲染永远停在 LastPos、以 40Hz 阶梯运动）。补上
        // 引擎在当前物理帧内的渲染分数×物理帧长，两级累加才覆盖“物理帧→0.025s 核心
        // tick”的一般 tps；只影响渲染，不进任何 tick 或哈希。
        float physicsDelta = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
        float interpolation = Mathf.Clamp(
            (float)(_tickAccumulator / TickDt
                + Engine.GetPhysicsInterpolationFraction() * physicsDelta / TickDt),
            0f, 1f);
        _renderer.Render(interpolation);
        _terrain.Draw(_camera, _controller.BodyCenter,
            _controller.LastMoveTargetKind, _controller.LastMoveTarget);
        _debugTargetMarker.Visible = _debugTargetActive;
        if (_debugTargetActive)
            _debugTargetMarker.GlobalPosition = _debugTargetPos;
        UpdateHud();
    }

    private void UpdateShowcaseCamera()
    {
        Vector3 center = _controller.BodyCenter;
        _camera.GlobalPosition = center + new Vector3(-10f, 8f, 15f);
        _camera.Fov = 61f;
        _camera.LookAt(center, Vector3.Up);
        if (_fillLight is not null)
            _fillLight.GlobalPosition = center + new Vector3(-3f, 6f, 5f);
    }

    private void UpdateCameraFly(float delta)
    {
        bool flying = WantCameraFly;
        if (flying != _cameraFlying)
        {
            Input.MouseMode = flying ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            _cameraFlying = flying;
        }
        if (!flying)
            return;
        Basis basis = _camera.GlobalTransform.Basis;
        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction -= basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction += basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction -= basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction += basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction -= Vector3.Up;
        if (direction.LengthSquared() > 1e-10f)
            _camera.GlobalPosition += direction.Normalized() * CameraFlySpeed * delta;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            if (WantCameraFly)
            {
                _cameraYaw -= motion.Relative.X * CameraMouseSensitivity;
                _cameraPitch = Mathf.Clamp(_cameraPitch - motion.Relative.Y * CameraMouseSensitivity,
                    -1.5f, 1.5f);
                _camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0f);
            }
            return;
        }
        if (Deterministic)
            return;
        if (@event is InputEventMouseButton
            {
                ButtonIndex: MouseButton.Right,
                Pressed: true,
            } mouse && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            _pendingMoveTargetPick = mouse.Position;
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.PhysicalKeycode == Key.F1)
            _hud?.ToggleVisibility();
        else if (key.PhysicalKeycode == Key.F2)
            _hud?.ToggleCompact();
        else if (key.PhysicalKeycode == Key.F3)
            _terrain.Enabled = !_terrain.Enabled;
        else if (key.PhysicalKeycode == Key.Space)
            _controller.Launch(new Vector3(0.07f, 0.34f, 0.03f));
        else if (key.PhysicalKeycode == Key.T)
            _controller.Teleport(new Vector3(0f, 0.5f, 1.5f));
        else if (key.PhysicalKeycode == Key.H)
            _controller.Shift(new Vector3(0f, 0f, 1.5f));
        else if (key.PhysicalKeycode == Key.X)
            ToggleDebugTarget();
        else if (key.PhysicalKeycode == Key.P)
            ToggleDebugTargetPull();
        else if (key.PhysicalKeycode == Key.B)
            ChangeSeed(-1);
        else if (key.PhysicalKeycode == Key.N)
            ChangeSeed(1);
        else if (Input.IsPhysicalKeyPressed(Key.Alt)
                 && TryTentacleIndex(key.PhysicalKeycode, out int tentacle))
            StunInteractiveTentacle(tentacle);
        else if (key.PhysicalKeycode is >= Key.Key1 and <= Key.Key3)
            SelectPreset((int)(key.PhysicalKeycode - Key.Key1));
    }

    private void StunInteractiveTentacle(int index)
    {
        if (index < 0 || index >= _controller.Tentacles.Count)
            return;
        if (index == _externalTentacle)
        {
            _debugTargetActive = false;
            _externalTentacle = -1;
        }
        _controller.StunTentacle(index, InteractiveStunTicks);
        GD.Print($"[DADDY-SANDBOX] stunned tentacle {index} for {InteractiveStunTicks} ticks");
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Length)
            return;
        Vector3 origin = _controller.BodyCenter + Vector3.Up * 1.0f;
        SpawnDaddy(origin, _presets[index], _stableSeed);
        _hud?.SyncPreset(index);
        GD.Print($"[DADDY-SANDBOX] preset -> {_presets[index].StableId} seed={_stableSeed}");
    }

    private void ChangeSeed(int delta)
    {
        _stableSeed = delta < 0
            ? _stableSeed == 0UL ? ulong.MaxValue : _stableSeed - 1UL
            : unchecked(_stableSeed + 1UL);
        Vector3 origin = _controller.BodyCenter + Vector3.Up * 1.0f;
        SpawnDaddy(origin, _preset, _stableSeed);
        GD.Print($"[DADDY-SANDBOX] seed -> {_stableSeed}");
    }

    private void UpdateHud()
    {
        if (_hud is null)
            return;
        int idle = 0;
        int external = 0;
        int stunned = 0;
        int contacts = 0;
        int passiveContacts = 0;
        int activeGrips = 0;
        int planted = 0;
        int peeling = 0;
        int reaching = 0;
        var states = new string[_controller.Tentacles.Count];
        for (int i = 0; i < _controller.Tentacles.Count; i++)
        {
            DaddyTentacle tentacle = _controller.Tentacles[i];
            switch (tentacle.Role)
            {
                case DaddyTentacleRole.Idle: idle++; break;
                case DaddyTentacleRole.ExternalReach: external++; break;
                case DaddyTentacleRole.Stunned: stunned++; break;
            }
            int segmentContacts = 0;
            int segmentPassive = 0;
            int segmentGrips = 0;
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
            {
                if (segment.TerrainContact) segmentContacts++;
                if (segment.TerrainContact && !segment.ActiveGrip) segmentPassive++;
                if (segment.ActiveGrip) segmentGrips++;
            }
            contacts += segmentContacts;
            passiveContacts += segmentPassive;
            activeGrips += segmentGrips;
            char role = tentacle.Role switch
            {
                DaddyTentacleRole.Locomotion => 'M',
                DaddyTentacleRole.ExternalReach => 'E',
                DaddyTentacleRole.Stunned => 'S',
                _ => 'I',
            };
            char phase = tentacle.ReplantPhase switch
            {
                DaddyTentacleReplantPhase.Planted => 'P',
                DaddyTentacleReplantPhase.Peeling => 'X',
                _ => 'R',
            };
            switch (tentacle.ReplantPhase)
            {
                case DaddyTentacleReplantPhase.Planted: planted++; break;
                case DaddyTentacleReplantPhase.Peeling: peeling++; break;
                case DaddyTentacleReplantPhase.Reaching: reaching++; break;
            }
            states[i] = $"{i}:{role}{phase} T{segmentContacts} P{segmentPassive} " +
                $"G{segmentGrips} L{tentacle.GuideLength:F1}/C{tentacle.GuideContactLength:F1} " +
                $"B{tentacle.BacktrackFrom}/R" +
                $"{(tentacle.TerrainRecoveryActive ? tentacle.TerrainRecoveryPhase : -1)}";
        }
        _hud.UpdateStatus(_preset.StableId, _stableSeed,
            _controller.Body.Chunks.Count, _controller.Tentacles.Count,
            _controller.Morphology.TotalTentacleSegments,
            _controller.RawSupport, _controller.EffectiveSupport,
            _controller.GravityCancellation, _controller.DirectionalSupport,
            _controller.DriveScale, _controller.LocomotionTentacleCount,
            _controller.ArrivedTentacleCount, idle, external, stunned,
            contacts, passiveContacts, activeGrips,
            _controller.Morphology.TotalTentacleSegments,
            planted, peeling, reaching,
            _controller.StuckCounter, _controller.StuckAmount,
            _controller.StuckDetourActive, _controller.StuckDetourDirection,
            _controller.StuckEpisodeSerial,
            _controller.MovementEpisodeSerial, _controller.StartReplantSerial,
            _controller.ActiveStartReplantTentacleIndex,
            _controller.MoveEpisodeGraceTicksRemaining,
            _controller.TickQueryCount, _controller.PeakQueryCount,
            _preset.MaximumTerrainQueriesPerTick, _controller.AtMoveTarget,
            _externalTentacle, _debugTargetPull, string.Join("  ", states));
    }

    private void BuildDebugTargetMarker()
    {
        _debugTargetMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.20f, Height = 0.40f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.18f, 0.78f),
                EmissionEnabled = true,
                Emission = new Color(0.55f, 0.04f, 0.30f),
                EmissionEnergyMultiplier = 1.8f,
                Roughness = 0.34f,
            },
            Visible = false,
            TopLevel = true,
        };
        AddChild(_debugTargetMarker);
    }

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--daddy-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--daddy-determinism=".Length..], CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0) throw new FormatException("tick count must be positive");
                }
                else if (argument.StartsWith("--daddy-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(
                        argument["--daddy-tps=".Length..], CultureInfo.InvariantCulture);
                    if (_requestedTps < 40 || _requestedTps > 1000)
                        throw new FormatException("tps must be in [40,1000]");
                }
                else if (argument.StartsWith("--daddy-preset=", StringComparison.Ordinal))
                {
                    _presetName = argument["--daddy-preset=".Length..].ToLowerInvariant();
                    _ = ResolvePreset(_presetName);
                }
                else if (argument.StartsWith("--daddy-seed=", StringComparison.Ordinal))
                {
                    _stableSeed = ParseUInt64(argument["--daddy-seed=".Length..]);
                }
                else if (argument.StartsWith("--daddy-route=", StringComparison.Ordinal))
                {
                    _route = argument["--daddy-route=".Length..].ToLowerInvariant();
                    if (Array.IndexOf(RouteNames, _route) < 0)
                        throw new FormatException($"unknown route '{_route}'");
                }
                else if (argument.StartsWith("--daddy-ablate=", StringComparison.Ordinal))
                {
                    _ablation = ParseAblation(argument["--daddy-ablate=".Length..]);
                }
                else if (argument.StartsWith("--daddy-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(
                        argument["--daddy-perturb=".Length..], CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb)) throw new FormatException("perturb must be finite");
                }
                else if (argument.StartsWith("--daddy-expect-hash=", StringComparison.Ordinal))
                {
                    _expectHash = ParseUInt64(
                        argument["--daddy-expect-hash=".Length..], hexadecimalByDefault: true);
                }
                else if (argument.StartsWith("--daddy-", StringComparison.Ordinal))
                {
                    throw new FormatException($"unknown option {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException
                or OverflowException or ArgumentException)
            {
                GD.PushError($"[DADDY-CLI] {argument}: {exception.Message}");
                return false;
            }
        }
        if (_expectHash is not null && _determinismTicks <= 0)
        {
            GD.PushError("[DADDY-CLI] --daddy-expect-hash requires --daddy-determinism");
            return false;
        }
        if (_route == "height-retention"
            && _determinismTicks < HeightRetentionStandTicks + HeightRetentionMoveTicks)
        {
            GD.PushError($"[DADDY-CLI] height-retention requires at least " +
                         $"{HeightRetentionStandTicks + HeightRetentionMoveTicks} ticks");
            return false;
        }
        if (_ablation != "none" && _route != "height-retention")
        {
            GD.PushError("[DADDY-CLI] --daddy-ablate is scoped to height-retention");
            return false;
        }
        return true;
    }

    private static string ParseAblation(string name) => name.ToLowerInvariant() switch
    {
        "none" => "none",
        "surface-span-replant" => "surface-span-replant",
        "serial-replant" => "serial-replant",
        "support-response-3d" => "support-response-3d",
        _ => throw new FormatException($"unknown ablation '{name}'"),
    };

    private static void ApplyAblation(DaddyLongLegsParams parameters, string ablation)
    {
        switch (ablation)
        {
            case "none":
                return;
            case "surface-span-replant":
                parameters.EnableSurfaceSpanReplant = false;
                return;
            case "serial-replant":
                parameters.EnableSerialReplant = false;
                return;
            case "support-response-3d":
                // 原作二维响应指数；关闭本项目 3D 多面支撑的更缓响应。
                parameters.SupportResponseExponent = 0.30f;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(ablation), ablation, null);
        }
    }

    private static DaddyLongLegsParams ResolvePreset(string name) => name switch
    {
        "brother" or DaddyLongLegsFactory.BrotherId => DaddyLongLegsFactory.Brother(),
        "daddy" or DaddyLongLegsFactory.DaddyId => DaddyLongLegsFactory.Daddy(),
        "terror" or DaddyLongLegsFactory.TerrorId => DaddyLongLegsFactory.Terror(),
        _ => throw new ArgumentException($"unknown DaddyLongLegs preset '{name}'"),
    };

    private static Vector3 SpawnForRoute(string route) => route switch
    {
        "flat" or "idle-start" or "tap" or "stun" or "launch" or "target" or "lifecycle" =>
            new Vector3(-20f, 1.35f, 0f),
        // 独立平地通道避开 x=8 墙体和 z=12 障碍，保证只测水平步态本身。
        "height-retention" => new Vector3(-20f, 1.35f, -24f),
        // Ramp 与地板的实体体积在左端相交；从斜面中部上方出生，避免把“封闭夹层逃逸”
        // 错当成沿斜坡运动，也避免长触手从边缘直接跨过整块斜面。
        "course" => new Vector3(-17f, 2.4f, -48f),
        "wall" or "wall-idle" => new Vector3(7.15f, 4.0f, 0f),
        "ceiling" => new Vector3(10f, 7.15f, 0f),
        "corner" => new Vector3(7.15f, 1.35f, 4.0f),
        "outer" => new Vector3(15.5f, 9.2f, 0f),
        "stuck" => new Vector3(-14f, 1.35f, 12f),
        _ => new Vector3(-20f, 1.35f, 0f),
    };

    private int BestLocomotionTentacle()
    {
        int best = -1;
        float support = float.NegativeInfinity;
        for (int i = 0; i < _controller.Tentacles.Count; i++)
        {
            DaddyTentacle tentacle = _controller.Tentacles[i];
            if (!tentacle.NeededForLocomotion || tentacle.SupportContribution <= support)
                continue;
            best = i;
            support = tentacle.SupportContribution;
        }
        return best;
    }

    private Vector3[] SnapshotBodyVelocities()
    {
        var values = new Vector3[_controller.Body.Chunks.Count];
        for (int i = 0; i < values.Length; i++) values[i] = _controller.Body.Chunks[i].Vel;
        return values;
    }

    private Vector3[] SnapshotSegmentVelocities()
    {
        var values = new Vector3[_controller.Morphology.TotalTentacleSegments];
        int index = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                values[index++] = segment.Vel;
        return values;
    }

    private Vector3[] SnapshotBodyPositions()
    {
        var values = new Vector3[_controller.Body.Chunks.Count];
        for (int i = 0; i < values.Length; i++) values[i] = _controller.Body.Chunks[i].Pos;
        return values;
    }

    private Vector3[] SnapshotSegmentPositions()
    {
        var values = new Vector3[_controller.Morphology.TotalTentacleSegments];
        int index = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                values[index++] = segment.Pos;
        return values;
    }

    private bool CheckVelocityDelta(Vector3[] bodies, Vector3[] segments, Vector3 delta)
    {
        if (bodies.Length != _controller.Body.Chunks.Count
            || segments.Length != _controller.Morphology.TotalTentacleSegments)
            return false;
        for (int i = 0; i < bodies.Length; i++)
            if (!Near(_controller.Body.Chunks[i].Vel - bodies[i], delta, 2e-5f)) return false;
        int index = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                if (!Near(segment.Vel - segments[index++], delta, 2e-5f)) return false;
        return true;
    }

    private bool CheckPositionDelta(Vector3[] bodies, Vector3[] segments, Vector3 delta)
    {
        if (bodies.Length != _controller.Body.Chunks.Count
            || segments.Length != _controller.Morphology.TotalTentacleSegments)
            return false;
        for (int i = 0; i < bodies.Length; i++)
            if (!Near(_controller.Body.Chunks[i].Pos - bodies[i], delta)) return false;
        int index = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                if (!Near(segment.Pos - segments[index++], delta)) return false;
        return true;
    }

    private bool AllLandingTargetsReleased()
    {
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            if (tentacle.HasLandingTarget || tentacle.SupportContribution != 0f) return false;
        return true;
    }

    private bool AllExternalTargetsReleased()
    {
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            if (tentacle.ExternalTarget is not null) return false;
        return true;
    }

    private int CountContactSegments()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                if (segment.TerrainContact) count++;
        return count;
    }

    private int CountActiveGripSegments()
    {
        int count = 0;
        foreach (DaddyTentacle tentacle in _controller.Tentacles)
            foreach (DaddyTentacleSegmentState segment in tentacle.Segments)
                if (segment.ActiveGrip) count++;
        return count;
    }

    private static bool TryTentacleIndex(Key key, out int index)
    {
        if (key is >= Key.Key0 and <= Key.Key9)
        {
            index = (int)(key - Key.Key0);
            return true;
        }
        if (key == Key.Minus)
        {
            index = 10;
            return true;
        }
        if (key == Key.Equal)
        {
            index = 11;
            return true;
        }
        index = -1;
        return false;
    }

    private static ulong ParseUInt64(string text, bool hexadecimalByDefault = false)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ulong.Parse(text,
            hexadecimalByDefault ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-10f ? value.Normalized() : fallback.Normalized();

    private static bool Near(Vector3 a, Vector3 b, float epsilon = 1e-5f) =>
        (a - b).LengthSquared() <= epsilon * epsilon;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string Format(Vector3 value) =>
        $"({value.X:F2},{value.Y:F2},{value.Z:F2})";
}
