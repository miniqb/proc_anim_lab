using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core.Diagnostics;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Deer;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.DeerSandbox;

/// <summary>
/// Deer 独立白盒：固定 40Hz 核心 tick、完整多节腿显示、专用交互和退出码回归。
/// 它不经过主 SandboxWorld，也不读取其它物种控制器。
/// </summary>
public partial class DeerSandboxWorld : Node3D
{
    private const float TickDt = 0.025f;

    [Export] public float GravityMps2 = 36f;
    [Export] public float CameraFlySpeed = 8f;
    [Export] public float CameraMouseSensitivity = 0.003f;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;

    private readonly RaycastTerrainQuery _raycast = new();
    private readonly DeerBodyRenderer _renderer = new();
    private readonly DragController _drag = new();
    private readonly DeterminismHasher _hasher = new();

    private RayDebugDraw _terrain = null!;
    private DeerLocomotionController _deer = null!;
    private DeerParams[] _presets = Array.Empty<DeerParams>();
    private DeerParams _preset = null!;
    private DeerSandboxHud? _hud;
    private Camera3D _camera = null!;
    private OmniLight3D _warmFill = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _spawnSurface;
    private Vector3 _initialCenter;
    private Vector3 _lastCenter;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cameraFlying;
    private bool _rendererBuilt;
    private bool _fatal;
    private Vector2? _pendingTargetPick;
    private long _tick;

    // 无头入口。
    private int _determinismTicks;
    private int _requestedTps = 40;
    private string _presetName = "original";
    private string _route = "flat";
    private float _perturb;
    private ulong? _expectHash;
    private int _routeDirection = 1;
    private int _targetWaypoint;

    // 行为观测量。
    private bool _nonFinite;
    private bool _gravityChanged;
    private bool _sawAtTarget;
    private bool _launchCalled;
    private bool _shiftContract;
    private bool _teleportContract;
    private bool _launchContract;
    private bool _lifecycleTargetRetained;
    private long _launchTick = -1;
    private int _waypointsReached;
    private int _pairAirViolations;
    private int _maxPlanted;
    private int _recoveryRun;
    private int _maxRecoveryRun;
    private int _metricSamples;
    private float _travelDistance;
    private float _forwardProgress;
    private float _maximumHeightGain;
    private float _supportSum;
    private float _rawSupportSum;
    private float _minimumRawSupport = float.PositiveInfinity;
    private float _maximumRawSupport = float.NegativeInfinity;
    private float _minimumTotalSupport = float.PositiveInfinity;
    private float _maximumTotalSupport = float.NegativeInfinity;
    private float _heightErrorSum;
    private float _minimumActualHeight = float.PositiveInfinity;
    private float _maximumActualHeight = float.NegativeInfinity;
    private float _minimumDesiredHeight = float.PositiveInfinity;
    private float _maximumDesiredHeight = float.NegativeInfinity;
    private float _endConstraintDeviation;
    private float _maximumConstraintDeviation;
    private float _maximumLegConstraintError;
    private long _maximumLegConstraintErrorTick = -1;
    private int _maximumLegConstraintErrorLeg = -1;
    private int[] _initialLandingSerials = Array.Empty<int>();
    private bool[] _pairWasGrounded = Array.Empty<bool>();
    private Queue<string>[] _pairHistories = Array.Empty<Queue<string>>();
    private int _pairDiagnosticPair = -1;
    private long _pairDiagnosticUntil = -1;
    private int _restLastVoluntaryReleaseSerial;
    private int[] _restLastStepSerials = Array.Empty<int>();
    private int[] _restLastLandingSerials = Array.Empty<int>();
    private int[] _restLastForcedReleaseSerials = Array.Empty<int>();
    private int[] _restOutstandingReplants = Array.Empty<int>();
    private long _restQualifiedTick = -1;
    private int _restPrematureActiveReleases;
    private int _restRetractionReleases;
    private int _restRetractionReplants;
    private int _restReleaseMappingFailures;
    private bool _wallContactObserved;
    private long _wallContactTick = -1;
    private int _wallContactTicks;
    private Vector3 _wallContactCenter;
    private float _wallPostContactAdvance;
    private float _wallLateTravel;
    private float _wallMaximumCenterHeight = float.NegativeInfinity;
    private float _wallMinimumSupportUpDot = 1f;
    private int _wallGripTicks;
    private bool _turnStarted;
    private Vector3 _turnCenter;
    private Vector3 _turnForwardBefore = Vector3.Right;
    private Vector3 _turnPhysicalAxisBefore = Vector3.Right;
    private int[] _turnLandingSerials = Array.Empty<int>();
    private float _turnFirstAxisProgress;
    private float _turnSecondAxisProgress;
    private int _turnAlignmentRun;
    private int _turnMaximumAlignmentRun;
    private bool _reverseStarted;
    private long _reverseTick = -1;
    private Vector3 _reverseCenter;
    private Vector3 _reversePhysicalAxisBefore = Vector3.Right;
    private int[] _reverseLandingSerials = Array.Empty<int>();
    private int[] _reverseObservedLandingSerials = Array.Empty<int>();
    private int[] _reverseLandingSamples = Array.Empty<int>();
    private int[] _reverseForwardLandings = Array.Empty<int>();
    private float[] _reverseLandingProgressSums = Array.Empty<float>();
    private float[] _reversePoleAlignmentSums = Array.Empty<float>();
    private float[] _reversePoleLongitudinalSums = Array.Empty<float>();
    private float[] _reversePoleOutwardSums = Array.Empty<float>();
    private int[] _reversePoleSamples = Array.Empty<int>();
    private float[] _reverseAttachedSignedBowSums = Array.Empty<float>();
    private int[] _reverseAttachedBowSamples = Array.Empty<int>();
    private int[] _reverseAttachedComparable = Array.Empty<int>();
    private int[] _reverseAttachedMatches = Array.Empty<int>();
    private float[] _reverseSwingSignedBowSums = Array.Empty<float>();
    private int[] _reverseSwingBowSamples = Array.Empty<int>();
    private int[] _reverseSwingComparable = Array.Empty<int>();
    private int[] _reverseSwingMatches = Array.Empty<int>();
    private float[] _reverseSwingBowPoleSums = Array.Empty<float>();
    private int[] _reverseSwingBowPoleComparable = Array.Empty<int>();
    private int[] _reverseSwingBowPoleMatches = Array.Empty<int>();
    private int[] _reverseSwingWrongRuns = Array.Empty<int>();
    private int[] _reverseMaximumSwingWrongRuns = Array.Empty<int>();
    private float[] _reverseMaximumSwingWrongBows = Array.Empty<float>();
    private int[] _reverseSwingCorrectRuns = Array.Empty<int>();
    private int[] _reverseLegRecoveryTicks = Array.Empty<int>();
    private float _reverseOutboundProgress;
    private float _reverseReturnProgress;
    private int _reverseBodyAlignmentRun;
    private int _reverseMaximumBodyAlignmentRun;
    private int _reverseRecoveryRun;
    private int _reverseRecoveryTicks = -1;
    private int _reverseSettledSamples;
    private int _reverseAllSameSignRun;
    private int _reverseMaximumAllSameSignRun;
    private int _reverseAllOppositeRun;
    private int _reverseMaximumAllOppositeRun;
    private float _reverseMinimumInstantPoleAlignment = 1f;
    private float _minimumPoleDot = 1f;
    private float _maximumSupportLateral;
    private float _maximumResidualPenetration;
    private string _maximumPenetrationPart = "none";
    private long _maximumPenetrationTick = -1;
    private float _roughMinimumGripHeight = float.PositiveInfinity;
    private float _roughMaximumGripHeight = float.NegativeInfinity;
    private int _roughRaisedGripTicks;
    private int _roughLowSupportRun;
    private int _roughMaximumLowSupportRun;
    private int _activePostureSamples;
    private int _activePostureBadSamples;
    private int _standingPostureSamples;
    private int _restPostureSamples;
    private float _activeHeightSum;
    private float _standingHeightSum;
    private float _restHeightSum;
    private float _restReachLimitSum;
    private float _maximumRestFootReach;
    private int _settledRestFootSamples;
    private float _minimumActiveClearance = float.PositiveInfinity;
    private float _minimumRestClearance = float.PositiveInfinity;
    private float _minimumActiveAntlerUp = float.PositiveInfinity;
    private float _minimumActiveHeadUp = float.PositiveInfinity;
    private float _minimumActiveHeadForward = float.PositiveInfinity;
    private float _maximumActiveAntlerTrunkIntrusion;
    private float _minimumRestAntlerUp = float.PositiveInfinity;
    private float _minimumRestHeadUp = float.PositiveInfinity;
    private float _minimumRestHeadForward = float.PositiveInfinity;
    private float _maximumRestAntlerTrunkIntrusion;

    private bool Deterministic => _determinismTicks > 0;

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _camera = GetNode<Camera3D>("Camera3D");
        _warmFill = GetNode<OmniLight3D>("WarmFill");
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        if (!ParseArguments())
        {
            _fatal = true;
            GD.Print("[DEER-RESULT] FAIL: invalid command-line arguments");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
        {
            Engine.MaxPhysicsStepsPerFrame = 100;
        }

        _terrain = new RayDebugDraw(_raycast);
        _terrain.Build(this);
        _drag.Spring = DragSpring;
        _drag.Damping = DragDamping;
        _drag.MaxForce = DragMaxForce;

        _presets = DeerFactory.AllPresets();
        _preset = ResolvePreset(_presetName);
        _spawnSurface = SpawnForRoute(_route);
        SpawnDeer(_spawnSurface, Vector3.Right, _preset);
        _camera.LookAt(BodyCenter(), Vector3.Up);
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;

        if (_perturb != 0f)
        {
            _deer.Head.Pos += Vector3.Back * _perturb;
            _deer.Head.LastPos = _deer.Head.Pos;
            _initialCenter = BodyCenter();
            _lastCenter = _initialCenter;
        }

        _renderer.Build(this, _deer);
        _rendererBuilt = true;

        if (!Deterministic)
        {
            string[] ids = Array.ConvertAll(_presets, parameters => parameters.StableId);
            _hud = new DeerSandboxHud();
            _hud.Build(this, ids, SelectPreset);
            _hud.SyncPreset(Array.FindIndex(_presets, parameters => parameters.StableId == _preset.StableId));
        }

        GD.Print($"[DEER-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.StableId} route={_route} determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnDeer(Vector3 surfaceOrigin, Vector3 forward, DeerParams parameters)
    {
        _drag.Release();
        _preset = parameters;
        _deer = DeerFactory.CreateController(surfaceOrigin, forward, parameters);
        _initialCenter = BodyCenter();
        _lastCenter = _initialCenter;
        _restQualifiedTick = -1;
        _restPrematureActiveReleases = 0;
        _restRetractionReleases = 0;
        _restRetractionReplants = 0;
        _restReleaseMappingFailures = 0;
        _initialLandingSerials = new int[_deer.Legs.Count];
        _restLastStepSerials = new int[_deer.Legs.Count];
        _restLastLandingSerials = new int[_deer.Legs.Count];
        _restLastForcedReleaseSerials = new int[_deer.Legs.Count];
        _restOutstandingReplants = new int[_deer.Legs.Count];
        _restLastVoluntaryReleaseSerial = _deer.VoluntaryReleaseSerial;
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            DeerLeg leg = _deer.Legs[i];
            _initialLandingSerials[i] = leg.LandingSerial;
            _restLastStepSerials[i] = leg.StepSerial;
            _restLastLandingSerials[i] = leg.LandingSerial;
            _restLastForcedReleaseSerials[i] = leg.ForcedReleaseSerial;
        }
        int maximumPair = -1;
        foreach (DeerLeg leg in _deer.Legs)
        {
            maximumPair = Math.Max(maximumPair, leg.PairIndex);
        }
        _pairWasGrounded = new bool[maximumPair + 1];
        _pairHistories = new Queue<string>[maximumPair + 1];
        for (int pair = 0; pair < _pairHistories.Length; pair++)
        {
            _pairHistories[pair] = new Queue<string>();
        }
        if (_rendererBuilt)
        {
            _renderer.Build(this, _deer);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
        {
            return;
        }

        _tick++;
        _raycast.Bind(GetWorld3D().DirectSpaceState);
        _terrain.BeginTick();

        if (Deterministic)
        {
            DriveDeterministicRoute();
        }
        else
        {
            SampleInteractiveInput();
            ProcessPendingTargetPick();
            if (!_cameraFlying)
            {
                _drag.SampleInput(_camera, new[] { _deer.Body });
                _drag.ApplyDragForce();
            }
        }

        _deer.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
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

    private void SampleInteractiveInput()
    {
        if (WantCameraFly)
        {
            _deer.MoveDir = Vector3.Zero;
            _deer.RunSpeed = 0f;
            return;
        }

        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction += Vector3.Forward;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction += Vector3.Back;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction += Vector3.Left;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction += Vector3.Right;

        if (direction.LengthSquared() > 1e-10f)
        {
            _deer.MoveTarget = null;
            _deer.MoveDir = direction.Normalized();
            _deer.RunSpeed = 1f;
        }
        else if (_deer.MoveTarget is null)
        {
            _deer.MoveDir = Vector3.Zero;
            _deer.RunSpeed = 0f;
        }
        else
        {
            _deer.RunSpeed = 1f;
        }
    }

    private void ProcessPendingTargetPick()
    {
        if (_pendingTargetPick is not Vector2 mouse)
        {
            return;
        }
        _pendingTargetPick = null;
        Vector3 from = _camera.ProjectRayOrigin(mouse);
        Vector3 to = from + _camera.ProjectRayNormal(mouse) * 120f;
        if (_terrain.Raycast(from, to, out TerrainHit hit))
        {
            _deer.MoveTarget = hit.Point;
            _deer.RunSpeed = 1f;
            GD.Print($"[DEER-SANDBOX] MoveTarget -> ({_deer.MoveTarget.Value.X:F2}," +
                     $"{_deer.MoveTarget.Value.Y:F2},{_deer.MoveTarget.Value.Z:F2})");
        }
    }

    private void DriveDeterministicRoute()
    {
        // lifecycle 专项必须让目标跨 tick 存活，才能真实验证 Teleport 清除与 Launch 保留；
        // 其它脚本路线仍由各 tick 明确重建自己的输入。
        if (_route != "lifecycle")
        {
            _deer.MoveTarget = null;
        }
        _deer.MoveDir = Vector3.Zero;
        _deer.RunSpeed = 0f;

        switch (_route)
        {
            case "flat":
                DriveFlatPatrol();
                break;
            case "slope":
            case "steps":
            case "rough":
                DriveForwardCourse();
                break;
            case "wall":
                DriveWallRoute();
                break;
            case "turn":
                DriveTurnRoute();
                break;
            case "reverse":
                DriveReverseRoute();
                break;
            case "rest":
                break;
            case "launch":
                DriveLaunchRoute();
                break;
            case "target":
                DriveMoveTargetRoute();
                break;
            case "lifecycle":
                DriveLifecycleRoute();
                break;
        }
    }

    private void DriveFlatPatrol()
    {
        float x = BodyCenter().X;
        if (_routeDirection > 0 && x >= -11f)
        {
            _routeDirection = -1;
            _waypointsReached++;
        }
        else if (_routeDirection < 0 && x <= -25f)
        {
            _routeDirection = 1;
            _waypointsReached++;
        }
        _deer.MoveDir = Vector3.Right * _routeDirection;
        _deer.RunSpeed = 1f;
    }

    private void DriveForwardCourse()
    {
        float stopX = _route switch
        {
            "slope" => 23f,
            "rough" => 18f,
            _ => 17f,
        };
        if (BodyCenter().X < stopX)
        {
            _deer.MoveDir = Vector3.Right;
            _deer.RunSpeed = 1f;
        }
    }

    private void DriveWallRoute()
    {
        // 持续给 +X 意图，验证到达 EndWall 后是碰撞安全停住，而不是宿主提前刹车。
        _deer.MoveDir = Vector3.Right;
        _deer.RunSpeed = 1f;
    }

    private void DriveTurnRoute()
    {
        Vector3 center = BodyCenter();
        if (!_turnStarted && center.X - _initialCenter.X >= 7f)
        {
            _turnStarted = true;
            _turnCenter = center;
            _turnFirstAxisProgress = center.X - _initialCenter.X;
            _turnForwardBefore = HorizontalUnit(_deer.Forward, Vector3.Right);
            _turnPhysicalAxisBefore = PhysicalBodyAxis();
            _turnLandingSerials = new int[_deer.Legs.Count];
            for (int i = 0; i < _deer.Legs.Count; i++)
            {
                _turnLandingSerials[i] = _deer.Legs[i].LandingSerial;
            }
            GD.Print($"[DEER-SCENARIO] turn tick={_tick} center={Format(center)} " +
                     $"forward={Format(_turnForwardBefore)} " +
                     $"physicalAxis={Format(_turnPhysicalAxisBefore)}");
        }

        if (!_turnStarted)
        {
            _deer.MoveDir = Vector3.Right;
            _deer.RunSpeed = 1f;
        }
        else if (center.Z < 18f)
        {
            // Godot 的 Back 是世界 +Z；与前半程 +X 正交，是真正的地面 3D 转向。
            _deer.MoveDir = Vector3.Back;
            _deer.RunSpeed = 1f;
        }
    }

    private void DriveReverseRoute()
    {
        Vector3 center = BodyCenter();
        if (!_reverseStarted && center.X - _initialCenter.X >= 7f)
        {
            _reverseStarted = true;
            _reverseTick = _tick;
            _reverseCenter = center;
            _reverseOutboundProgress = center.X - _initialCenter.X;
            _reversePhysicalAxisBefore = PhysicalBodyAxis();
            _reverseLandingSerials = new int[_deer.Legs.Count];
            _reverseObservedLandingSerials = new int[_deer.Legs.Count];
            _reverseLandingSamples = new int[_deer.Legs.Count];
            _reverseForwardLandings = new int[_deer.Legs.Count];
            _reverseLandingProgressSums = new float[_deer.Legs.Count];
            _reversePoleAlignmentSums = new float[_deer.Legs.Count];
            _reversePoleLongitudinalSums = new float[_deer.Legs.Count];
            _reversePoleOutwardSums = new float[_deer.Legs.Count];
            _reversePoleSamples = new int[_deer.Legs.Count];
            _reverseAttachedSignedBowSums = new float[_deer.Legs.Count];
            _reverseAttachedBowSamples = new int[_deer.Legs.Count];
            _reverseAttachedComparable = new int[_deer.Legs.Count];
            _reverseAttachedMatches = new int[_deer.Legs.Count];
            _reverseSwingSignedBowSums = new float[_deer.Legs.Count];
            _reverseSwingBowSamples = new int[_deer.Legs.Count];
            _reverseSwingComparable = new int[_deer.Legs.Count];
            _reverseSwingMatches = new int[_deer.Legs.Count];
            _reverseSwingBowPoleSums = new float[_deer.Legs.Count];
            _reverseSwingBowPoleComparable = new int[_deer.Legs.Count];
            _reverseSwingBowPoleMatches = new int[_deer.Legs.Count];
            _reverseSwingWrongRuns = new int[_deer.Legs.Count];
            _reverseMaximumSwingWrongRuns = new int[_deer.Legs.Count];
            _reverseMaximumSwingWrongBows = new float[_deer.Legs.Count];
            _reverseSwingCorrectRuns = new int[_deer.Legs.Count];
            _reverseLegRecoveryTicks = new int[_deer.Legs.Count];
            Array.Fill(_reverseLegRecoveryTicks, -1);
            for (int i = 0; i < _deer.Legs.Count; i++)
            {
                _reverseLandingSerials[i] = _deer.Legs[i].LandingSerial;
                _reverseObservedLandingSerials[i] = _deer.Legs[i].LandingSerial;
            }
            GD.Print($"[DEER-SCENARIO] reverse tick={_tick} center={Format(center)} " +
                     $"forward={Format(HorizontalUnit(_deer.Forward, Vector3.Right))} " +
                     $"physicalAxis={Format(_reversePhysicalAxisBefore)}");
        }

        if (!_reverseStarted)
        {
            _deer.MoveDir = Vector3.Right;
            _deer.RunSpeed = 1f;
        }
        else if (center.X > _initialCenter.X - 20f)
        {
            // 单次精确 180° 反转；回程足够让四腿都完成旧支点→新支点循环，随后停下，
            // 避免第二次反转污染“有限恢复”窗口。
            _deer.MoveDir = Vector3.Left;
            _deer.RunSpeed = 1f;
        }
    }

    private void DriveLaunchRoute()
    {
        if (_tick < 110 || _tick > 220)
        {
            _deer.MoveDir = Vector3.Right;
            _deer.RunSpeed = 1f;
        }
        if (_tick == 110)
        {
            Vector3 impulse = new(0.08f, 0.34f, 0.025f);
            Vector3[] velocities = SnapshotBodyVelocities();
            _deer.Launch(impulse);
            _launchCalled = true;
            _launchTick = _tick;
            _launchContract = CheckLaunchDelta(velocities, impulse)
                && AllLegsReleased()
                && _deer.Body.GravityScale == 1f;
            GD.Print($"[DEER-SCENARIO] launch tick={_tick} contract={_launchContract}");
        }
    }

    private void DriveMoveTargetRoute()
    {
        Vector3[] targets =
        {
            new(-17f, 0f, 0f),
            new(-11.5f, 0f, 0f),
        };
        if (_targetWaypoint >= targets.Length)
        {
            return;
        }

        _deer.MoveTarget = targets[_targetWaypoint];
        _deer.RunSpeed = 1f;
        if (_deer.AtMoveTarget)
        {
            _sawAtTarget = true;
            _targetWaypoint++;
            _waypointsReached++;
            _deer.MoveTarget = _targetWaypoint < targets.Length ? targets[_targetWaypoint] : null;
        }
    }

    private void DriveLifecycleRoute()
    {
        if (_tick == 20)
        {
            _deer.MoveTarget = SurfacePointBelowBody() + new Vector3(5f, 0f, 0f);
            Vector3 delta = new(0f, 0f, 0.75f);
            Vector3 bodyBefore = _deer.Head.Pos;
            Vector3[] segmentBefore = SnapshotLegSegments();
            Vector3 targetBefore = _deer.MoveTarget.Value;
            _deer.Shift(delta);
            _shiftContract = Near(_deer.Head.Pos - bodyBefore, delta)
                && CheckLegSegmentShift(segmentBefore, delta)
                && _deer.MoveTarget is Vector3 shiftedTarget
                && Near(shiftedTarget - targetBefore, delta);
            GD.Print($"[DEER-SCENARIO] shift tick={_tick} contract={_shiftContract}");
        }

        if (_tick == 80)
        {
            bool hadTarget = _deer.MoveTarget is not null;
            Vector3 delta = new(0f, 0f, 1.5f);
            _deer.Teleport(delta);
            _teleportContract = hadTarget
                && _deer.MoveTarget is null
                && !_deer.AtMoveTarget
                && AllLegsReleased()
                && _deer.TotalSupport == 0f;
            GD.Print($"[DEER-SCENARIO] teleport tick={_tick} contract={_teleportContract}");
        }

        if (_tick == 120)
        {
            _deer.MoveTarget = SurfacePointBelowBody() + new Vector3(4f, 0f, 0f);
            _deer.RunSpeed = 1f;
        }
        else if (_tick > 120 && _tick < 260 && _deer.MoveTarget is not null)
        {
            _deer.RunSpeed = 1f;
            _sawAtTarget |= _deer.AtMoveTarget;
        }

        if (_tick == 260)
        {
            Vector3? targetBefore = _deer.MoveTarget;
            Vector3 impulse = new(0.07f, 0.30f, -0.02f);
            Vector3[] velocities = SnapshotBodyVelocities();
            _deer.Launch(impulse);
            _launchCalled = true;
            _launchTick = _tick;
            _launchContract = CheckLaunchDelta(velocities, impulse) && AllLegsReleased();
            _lifecycleTargetRetained = targetBefore is not null
                && _deer.MoveTarget == targetBefore;
            GD.Print($"[DEER-SCENARIO] lifecycle-launch tick={_tick} " +
                     $"contract={_launchContract} targetRetained={_lifecycleTargetRetained}");
        }
    }

    private void TrackMetrics()
    {
        Vector3 center = BodyCenter();
        float tickTravel = center.DistanceTo(_lastCenter);
        bool wallContactThisTick = false;
        _travelDistance += tickTravel;
        _lastCenter = center;
        _forwardProgress = center.X - _initialCenter.X;
        _maximumHeightGain = MathF.Max(_maximumHeightGain, center.Y - _initialCenter.Y);

        _endConstraintDeviation = _deer.Body.CurrentMaxDeviation();
        _maximumConstraintDeviation = MathF.Max(_maximumConstraintDeviation, _endConstraintDeviation);
        _gravityChanged |= _deer.Body.GravityScale != 1f;
        _maxPlanted = Math.Max(_maxPlanted, _deer.PlantedLegCount);

        foreach (DeerLeg leg in _deer.Legs)
        {
            // 出生后的前 100 tick 是段链从解析出生弓度进入真实碰撞的装配瞬态；
            // 稳态、换步、台阶和 Launch 冲击仍全部进入正式误差门。
            if (_tick >= 100)
            {
                if (leg.MaxConstraintError > _maximumLegConstraintError)
                {
                    _maximumLegConstraintError = leg.MaxConstraintError;
                    _maximumLegConstraintErrorTick = _tick;
                    _maximumLegConstraintErrorLeg = leg.Index;
                }
            }
            _minimumPoleDot = MathF.Min(_minimumPoleDot, leg.MinimumPoleDot);
            if (_route == "wall" && leg.AttachedAtTip
                && leg.GripNormal.X < -0.75f && MathF.Abs(leg.GripNormal.Y) < 0.5f)
            {
                _wallGripTicks++;
            }
            if (_route == "rough" && _tick >= 100 && leg.AttachedAtTip)
            {
                _roughMinimumGripHeight = MathF.Min(_roughMinimumGripHeight, leg.GripPoint.Y);
                _roughMaximumGripHeight = MathF.Max(_roughMaximumGripHeight, leg.GripPoint.Y);
                if (leg.GripPoint.Y >= 0.12f)
                {
                    _roughRaisedGripTicks++;
                }
            }
            _nonFinite |= !Finite(leg.DesiredGripPoint) || !Finite(leg.GripPoint)
                || !Finite(leg.CandidatePoint) || !Finite(leg.BendPole)
                || !float.IsFinite(leg.SupportContribution)
                || !float.IsFinite(leg.MaxConstraintError);
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                _nonFinite |= !Finite(segment.Pos) || !Finite(segment.Vel)
                    || !Finite(segment.ContactNormal);
            }
        }
        foreach (BodyChunk chunk in _deer.Body.Chunks)
        {
            _nonFinite |= !Finite(chunk.Pos) || !Finite(chunk.Vel);
            if (_route == "wall" && chunk.TerrainContact
                && chunk.ContactNormal.X < -0.70f)
            {
                wallContactThisTick = true;
            }
        }
        if (wallContactThisTick)
        {
            _wallContactTicks++;
            if (!_wallContactObserved)
            {
                _wallContactObserved = true;
                _wallContactTick = _tick;
                _wallContactCenter = center;
                GD.Print($"[DEER-SCENARIO] wall-contact tick={_tick} center={Format(center)}");
            }
        }
        _nonFinite |= !Finite(_deer.Forward) || !Finite(_deer.Up) || !Finite(_deer.Right)
            || !Finite(_deer.SupportNormal)
            || !float.IsFinite(_deer.TotalSupport)
            || !float.IsFinite(_deer.ActualBodyHeight)
            || !float.IsFinite(_deer.DesiredBodyHeight)
            || !float.IsFinite(_deer.Hesitation);

        ObserveRestStepCycle();
        ObservePairGuard();
        TrackPostureMetrics();
        if (_route == "reverse" && _reverseStarted)
        {
            TrackReverseMetrics(center);
        }
        if (_route == "wall")
        {
            _wallMaximumCenterHeight = MathF.Max(_wallMaximumCenterHeight, center.Y);
            if (_tick >= 100)
            {
                _wallMinimumSupportUpDot = MathF.Min(
                    _wallMinimumSupportUpDot, _deer.SupportNormal.Dot(Vector3.Up));
            }
            if (_wallContactObserved)
            {
                _wallPostContactAdvance = MathF.Max(
                    _wallPostContactAdvance, center.X - _wallContactCenter.X);
            }
            if (_tick > _determinismTicks - 120)
            {
                _wallLateTravel += tickTravel;
            }
        }
        else if (_route == "turn" && _turnStarted)
        {
            _turnSecondAxisProgress = MathF.Max(
                _turnSecondAxisProgress, center.Z - _turnCenter.Z);
            if (PhysicalBodyAxis().Dot(Vector3.Back) >= 0.70f)
            {
                _turnAlignmentRun++;
                _turnMaximumAlignmentRun = Math.Max(_turnMaximumAlignmentRun, _turnAlignmentRun);
            }
            else
            {
                _turnAlignmentRun = 0;
            }
        }
        else if (_route == "rough")
        {
            _maximumSupportLateral = MathF.Max(
                _maximumSupportLateral, MathF.Abs(_deer.SupportNormal.Z));
            if (_tick >= 5)
            {
                ObserveRoughPenetration();
            }
            if (_tick >= 100)
            {
                if (_deer.TotalSupport < 0.55f)
                {
                    _roughLowSupportRun++;
                    _roughMaximumLowSupportRun = Math.Max(
                        _roughMaximumLowSupportRun, _roughLowSupportRun);
                }
                else
                {
                    _roughLowSupportRun = 0;
                }
            }
        }
        if (_tick >= 100)
        {
            _metricSamples++;
            _supportSum += _deer.TotalSupport;
            _rawSupportSum += _deer.RawSupport;
            _minimumRawSupport = MathF.Min(_minimumRawSupport, _deer.RawSupport);
            _maximumRawSupport = MathF.Max(_maximumRawSupport, _deer.RawSupport);
            _minimumTotalSupport = MathF.Min(_minimumTotalSupport, _deer.TotalSupport);
            _maximumTotalSupport = MathF.Max(_maximumTotalSupport, _deer.TotalSupport);
            _heightErrorSum += MathF.Abs(_deer.ActualBodyHeight - _deer.DesiredBodyHeight);
            _minimumActualHeight = MathF.Min(_minimumActualHeight, _deer.ActualBodyHeight);
            _maximumActualHeight = MathF.Max(_maximumActualHeight, _deer.ActualBodyHeight);
            _minimumDesiredHeight = MathF.Min(_minimumDesiredHeight, _deer.DesiredBodyHeight);
            _maximumDesiredHeight = MathF.Max(_maximumDesiredHeight, _deer.DesiredBodyHeight);
        }

        if (_launchTick >= 0 && _tick - _launchTick > 80 && _deer.PlantedLegCount >= 2)
        {
            _recoveryRun++;
            _maxRecoveryRun = Math.Max(_maxRecoveryRun, _recoveryRun);
        }
        else
        {
            _recoveryRun = 0;
        }
    }

    private void TrackPostureMetrics()
    {
        bool recovered = _launchTick < 0 || _tick - _launchTick > 80;
        bool supported = _deer.TotalSupport >= 0.55f;
        bool activeRoute = _route is "flat" or "slope" or "steps" or "wall" or "turn"
            or "reverse"
            or "rough" or "launch" or "target" or "lifecycle";
        float legLength = float.PositiveInfinity;
        foreach (DeerLeg leg in _deer.Legs)
        {
            legLength = MathF.Min(legLength, leg.MaxLength);
        }
        if (!float.IsFinite(legLength))
        {
            return;
        }

        float floorHeight = _deer.HasCurrentFloor
            ? _deer.CurrentFloorPoint.Y
            : _spawnSurface.Y;
        float clearance = _deer.Head.Pos.Y - _deer.Head.Radius - floorHeight;
        foreach (BodyChunk trunk in _deer.Trunk)
        {
            clearance = MathF.Min(clearance, trunk.Pos.Y - trunk.Radius - floorHeight);
        }

        float antlerLink = _preset.HeadRadius + _preset.AntlerRadius
            - _preset.AntlerHeadOverlap;
        Vector3 antlerAxis = (_deer.Antler.Pos - _deer.Head.Pos) / antlerLink;
        Vector3 headAxis = (_deer.Head.Pos - _deer.Trunk[0].Pos).Normalized();
        float antlerUp = antlerAxis.Dot(_deer.Up);
        float headUp = headAxis.Dot(_deer.Up);
        float headForward = headAxis.Dot(_deer.Forward);
        float intrusionRatio = 0f;
        foreach (BodyChunk trunk in _deer.Trunk)
        {
            float penetration = MathF.Max(
                0f, _deer.Antler.Radius + trunk.Radius
                    - _deer.Antler.Pos.DistanceTo(trunk.Pos));
            intrusionRatio = MathF.Max(
                intrusionRatio, penetration / MathF.Min(_deer.Antler.Radius, trunk.Radius));
        }

        if (_tick >= 100 && recovered && supported && activeRoute && _deer.HasMoveIntent)
        {
            _activePostureSamples++;
            _activeHeightSum += _deer.ActualBodyHeight;
            _minimumActiveClearance = MathF.Min(_minimumActiveClearance, clearance);
            _minimumActiveAntlerUp = MathF.Min(_minimumActiveAntlerUp, antlerUp);
            _minimumActiveHeadUp = MathF.Min(_minimumActiveHeadUp, headUp);
            _minimumActiveHeadForward = MathF.Min(_minimumActiveHeadForward, headForward);
            _maximumActiveAntlerTrunkIntrusion = MathF.Max(
                _maximumActiveAntlerTrunkIntrusion, intrusionRatio);
            bool bad = _deer.ActualBodyHeight < legLength * 0.45f
                || _deer.ActualBodyHeight > legLength * 0.90f
                || clearance < legLength * 0.25f
                || antlerUp < 0.85f
                || headUp <= 0.55f
                || headForward <= 0.08f
                || intrusionRatio > 0.10f;
            if (bad)
            {
                _activePostureBadSamples++;
            }
        }

        if (_route == "rest" && _tick >= 100 && supported)
        {
            if (_deer.RestAmount <= 0.02f)
            {
                _standingPostureSamples++;
                _standingHeightSum += _deer.ActualBodyHeight;
            }
            if (_deer.RestAmount >= 0.95f)
            {
                _restPostureSamples++;
                _restHeightSum += _deer.ActualBodyHeight;
                _restReachLimitSum += _deer.Legs[0].CurrentReachLimit;
                _minimumRestClearance = MathF.Min(_minimumRestClearance, clearance);
                _minimumRestAntlerUp = MathF.Min(_minimumRestAntlerUp, antlerUp);
                _minimumRestHeadUp = MathF.Min(_minimumRestHeadUp, headUp);
                _minimumRestHeadForward = MathF.Min(_minimumRestHeadForward, headForward);
                _maximumRestAntlerTrunkIntrusion = MathF.Max(
                    _maximumRestAntlerTrunkIntrusion, intrusionRatio);
                // 取得休息资格后的前 60 tick 是体高/可达上限连续收敛，随后还要让四条腿
                // 逐条走完 release→cooldown→touchdown。只把延迟后 120 tick 的窗口当稳态，
                // 过渡期允许旧脚暂时保留在 hard MaxLength 内，不能把连续换步误报为穿限。
                if (_deer.IdleTicks > _preset.RestDelayTicks + 120)
                {
                    _settledRestFootSamples++;
                    foreach (DeerLeg leg in _deer.Legs)
                    {
                        _maximumRestFootReach = MathF.Max(
                            _maximumRestFootReach,
                            leg.Anchor.Pos.DistanceTo(leg.Tip.Pos));
                    }
                }
            }
        }
    }

    private void TrackReverseMetrics(Vector3 center)
    {
        const int stableReverseAge = 140;
        _reverseReturnProgress = MathF.Max(_reverseReturnProgress, _reverseCenter.X - center.X);
        Vector3 physicalAxis = PhysicalBodyAxis();
        Vector3 controllerForward = HorizontalUnit(_deer.Forward, physicalAxis);
        bool bodyAligned = physicalAxis.Dot(Vector3.Left) >= 0.70f
            && controllerForward.Dot(Vector3.Left) >= 0.70f;
        if (bodyAligned)
        {
            _reverseBodyAlignmentRun++;
            _reverseMaximumBodyAlignmentRun = Math.Max(
                _reverseMaximumBodyAlignmentRun, _reverseBodyAlignmentRun);
        }
        else
        {
            _reverseBodyAlignmentRun = 0;
        }

        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            DeerLeg leg = _deer.Legs[i];
            int landingDelta = leg.LandingSerial - _reverseObservedLandingSerials[i];
            if (landingDelta > 0)
            {
                float progress = (leg.LastLandingPoint - leg.LastReleasePoint).Dot(Vector3.Left);
                _reverseLandingSamples[i] += landingDelta;
                _reverseLandingProgressSums[i] += progress * landingDelta;
                if (progress > leg.FootRadius * 0.20f)
                {
                    _reverseForwardLandings[i] += landingDelta;
                }
                _reverseObservedLandingSerials[i] = leg.LandingSerial;
            }
        }

        if (!_deer.HasMoveIntent || !bodyAligned)
        {
            _reverseRecoveryRun = 0;
            _reverseAllSameSignRun = 0;
            _reverseAllOppositeRun = 0;
            Array.Fill(_reverseSwingWrongRuns, 0);
            Array.Fill(_reverseSwingCorrectRuns, 0);
            return;
        }

        const float comparableBowEpsilon = 0.0025f;
        int positiveBows = 0;
        int negativeBows = 0;
        int comparableBows = 0;
        float minimumPoleAlignment = 1f;
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            DeerLeg leg = _deer.Legs[i];
            if (!TryMeasureLegBend(
                    leg,
                    out float signedBow,
                    out float poleAlignment,
                    out float longitudinalPoleAlignment,
                    out float outwardPoleAlignment,
                    out float bowPoleAlignment,
                    out float physicalBowMagnitude))
            {
                _reverseRecoveryRun = 0;
                _reverseAllSameSignRun = 0;
                _reverseAllOppositeRun = 0;
                return;
            }

            _reversePoleAlignmentSums[i] += poleAlignment;
            _reversePoleLongitudinalSums[i] += longitudinalPoleAlignment;
            _reversePoleOutwardSums[i] += outwardPoleAlignment;
            _reversePoleSamples[i]++;
            minimumPoleAlignment = MathF.Min(minimumPoleAlignment, poleAlignment);
            float expectedSign = MathF.Sign(leg.ForwardSplay);
            bool comparable = MathF.Abs(signedBow) >= comparableBowEpsilon;
            bool matches = comparable && signedBow * expectedSign > 0f;
            if (leg.AttachedAtTip)
            {
                _reverseAttachedBowSamples[i]++;
                _reverseAttachedSignedBowSums[i] += signedBow;
                if (comparable)
                {
                    _reverseAttachedComparable[i]++;
                    _reverseAttachedMatches[i] += matches ? 1 : 0;
                }
                // Plant-and-trail 的接触期允许身体越过世界抓点，真实纵向弓向可以暂时落后；
                // 它只进入诊断，不参与恢复门。
                _reverseSwingWrongRuns[i] = 0;
                _reverseSwingCorrectRuns[i] = 0;
            }
            else
            {
                bool swingShapeCorrect = matches
                    && physicalBowMagnitude >= comparableBowEpsilon
                    && bowPoleAlignment > 0f
                    && poleAlignment >= 0.25f
                    && longitudinalPoleAlignment > 0f
                    && outwardPoleAlignment > 0f;
                if (swingShapeCorrect)
                {
                    _reverseSwingWrongRuns[i] = 0;
                    _reverseSwingCorrectRuns[i]++;
                    if (_reverseLegRecoveryTicks[i] < 0
                        && _reverseSwingCorrectRuns[i] >= 3)
                    {
                        _reverseLegRecoveryTicks[i] = (int)(_tick - _reverseTick - 2);
                    }
                }
                else
                {
                    _reverseSwingCorrectRuns[i] = 0;
                }

                // 前 140 tick 是允许存在的 180° 转向恢复窗；稳定统计和连续错向门只看其后，
                // 否则会把“有限恢复”本身重复算作稳态错误。
                bool stableSwing = _tick - _reverseTick >= stableReverseAge;
                if (stableSwing)
                {
                    _reverseSwingBowSamples[i]++;
                    _reverseSwingSignedBowSums[i] += signedBow;
                    if (comparable)
                    {
                        _reverseSwingComparable[i]++;
                        _reverseSwingMatches[i] += matches ? 1 : 0;
                    }
                    if (physicalBowMagnitude >= comparableBowEpsilon)
                    {
                        _reverseSwingBowPoleComparable[i]++;
                        _reverseSwingBowPoleSums[i] += bowPoleAlignment;
                        _reverseSwingBowPoleMatches[i] += bowPoleAlignment > 0f ? 1 : 0;
                    }
                    // 连续错向只回答用户截图中的侧视问题：纵向弓向是否仍背离
                    // 该腿的解剖前后偏好。完整 3D bow·pole 另有独立统计与门，
                    // 不能让外撇分量的暂时偏差伪装成“向后凹”的连续时长。
                    if (comparable && !matches)
                    {
                        _reverseSwingWrongRuns[i]++;
                        _reverseMaximumSwingWrongRuns[i] = Math.Max(
                            _reverseMaximumSwingWrongRuns[i], _reverseSwingWrongRuns[i]);
                        _reverseMaximumSwingWrongBows[i] = MathF.Max(
                            _reverseMaximumSwingWrongBows[i], MathF.Abs(signedBow));
                    }
                    else
                    {
                        _reverseSwingWrongRuns[i] = 0;
                    }
                }
                else
                {
                    _reverseSwingWrongRuns[i] = 0;
                }
            }
            if (comparable)
            {
                comparableBows++;
                if (signedBow > 0f) positiveBows++;
                else negativeBows++;
            }
        }

        _reverseSettledSamples++;
        _reverseMinimumInstantPoleAlignment = MathF.Min(
            _reverseMinimumInstantPoleAlignment, minimumPoleAlignment);

        bool allComparable = comparableBows == _deer.Legs.Count;
        bool allSameSign = allComparable && (positiveBows == _deer.Legs.Count
            || negativeBows == _deer.Legs.Count);
        if (allSameSign)
        {
            _reverseAllSameSignRun++;
            _reverseMaximumAllSameSignRun = Math.Max(
                _reverseMaximumAllSameSignRun, _reverseAllSameSignRun);
        }
        else
        {
            _reverseAllSameSignRun = 0;
        }

        if (allComparable && negativeBows == _deer.Legs.Count)
        {
            _reverseAllOppositeRun++;
            _reverseMaximumAllOppositeRun = Math.Max(
                _reverseMaximumAllOppositeRun, _reverseAllOppositeRun);
        }
        else
        {
            _reverseAllOppositeRun = 0;
        }

        bool everyLegRecovered = _reverseLegRecoveryTicks.Length == _deer.Legs.Count;
        int latestRecovery = 0;
        for (int i = 0; i < _reverseLegRecoveryTicks.Length; i++)
        {
            everyLegRecovered &= _reverseLegRecoveryTicks[i] >= 0;
            latestRecovery = Math.Max(latestRecovery, _reverseLegRecoveryTicks[i]);
        }
        bool bendRecovered = everyLegRecovered && minimumPoleAlignment >= 0.25f;
        if (bendRecovered)
        {
            _reverseRecoveryRun++;
            if (_reverseRecoveryTicks < 0)
            {
                _reverseRecoveryTicks = latestRecovery;
            }
        }
        else
        {
            _reverseRecoveryRun = 0;
        }
    }

    private bool TryMeasureLegBend(
        DeerLeg leg,
        out float signedBow,
        out float poleAlignment,
        out float longitudinalPoleAlignment,
        out float outwardPoleAlignment,
        out float bowPoleAlignment,
        out float physicalBowMagnitude)
    {
        signedBow = 0f;
        poleAlignment = -1f;
        longitudinalPoleAlignment = -1f;
        outwardPoleAlignment = -1f;
        bowPoleAlignment = -1f;
        physicalBowMagnitude = 0f;
        Vector3 chord = leg.Tip.Pos - leg.Anchor.Pos;
        if (chord.LengthSquared() < 1e-8f || leg.Segments.Length < 2)
        {
            return false;
        }

        // 用户看到的是侧视“膝向前还是向后凹”。先移除左右外撇，只在当前
        // Forward/Up 解剖矢状面量中段到 Root→Tip 弦的有符号距离。
        Vector3 sagittalChord = chord - _deer.Right * chord.Dot(_deer.Right);
        Vector3 sagittalDirection = NormalizeOr(sagittalChord, -_deer.Up);
        Vector3 anatomicalForward = ProjectDirectionOntoPlane(
            _deer.Forward, sagittalDirection, _deer.Up);
        int middleIndex = Math.Max(0, (leg.Segments.Length - 2) / 2);
        Vector3 middleOffset = leg.Segments[middleIndex].Pos - leg.Anchor.Pos;
        Vector3 sagittalMiddleOffset = middleOffset
            - _deer.Right * middleOffset.Dot(_deer.Right);
        float closestT = Mathf.Clamp(
            sagittalMiddleOffset.Dot(sagittalChord)
                / MathF.Max(sagittalChord.LengthSquared(), 1e-8f),
            0f,
            1f);
        signedBow = (sagittalMiddleOffset - sagittalChord * closestT)
            .Dot(anatomicalForward);

        float physicalClosestT = Mathf.Clamp(
            middleOffset.Dot(chord) / MathF.Max(chord.LengthSquared(), 1e-8f),
            0f,
            1f);
        Vector3 physicalBow = middleOffset - chord * physicalClosestT;
        physicalBowMagnitude = physicalBow.Length();

        // 生产代码把 shape pole 约束在当前可见 Root→Tip 轴上；候选点只驱动足端，不能再
        // 用未来 candidate 轴评价当前关节，否则会把正确可见弓向误报成 pole 错位。
        Vector3 poleAxis = chord.Normalized();
        Vector3 expectedPole = _deer.Forward * leg.ForwardSplay
            + _deer.Right * (leg.Side * leg.OutwardSplay);
        expectedPole = ProjectDirectionOntoPlane(
            expectedPole,
            poleAxis,
            anatomicalForward * MathF.Sign(leg.ForwardSplay));
        if (expectedPole.LengthSquared() < 1e-8f || leg.BendPole.LengthSquared() < 1e-8f)
        {
            return false;
        }
        Vector3 currentPole = leg.BendPole.Normalized();
        poleAlignment = currentPole.Dot(expectedPole.Normalized());
        Vector3 expectedLongitudinal = ProjectDirectionOntoPlane(
            _deer.Forward * MathF.Sign(leg.ForwardSplay), poleAxis, expectedPole);
        Vector3 expectedOutward = ProjectDirectionOntoPlane(
            _deer.Right * leg.Side, poleAxis, expectedPole);
        longitudinalPoleAlignment = currentPole.Dot(expectedLongitudinal);
        outwardPoleAlignment = currentPole.Dot(expectedOutward);
        bowPoleAlignment = physicalBowMagnitude > 1e-6f
            ? physicalBow.Dot(currentPole) / physicalBowMagnitude
            : 0f;
        return float.IsFinite(signedBow)
            && float.IsFinite(poleAlignment)
            && float.IsFinite(longitudinalPoleAlignment)
            && float.IsFinite(outwardPoleAlignment)
            && float.IsFinite(bowPoleAlignment)
            && float.IsFinite(physicalBowMagnitude);
    }

    private static Vector3 ProjectDirectionOntoPlane(
        Vector3 direction,
        Vector3 planeNormal,
        Vector3 fallback)
    {
        Vector3 projected = direction - planeNormal * direction.Dot(planeNormal);
        if (projected.LengthSquared() > 1e-8f)
        {
            return projected.Normalized();
        }
        projected = fallback - planeNormal * fallback.Dot(planeNormal);
        return NormalizeOr(projected, Vector3.Right);
    }

    private void ObserveRoughPenetration()
    {
        for (int i = 0; i < _deer.Body.Chunks.Count; i++)
        {
            BodyChunk chunk = _deer.Body.Chunks[i];
            if (chunk.CollideWithTerrain)
            {
                ObservePenetration($"body{i}", chunk.Pos, chunk.TerrainRadius);
            }
        }
        foreach (DeerLeg leg in _deer.Legs)
        {
            for (int i = 0; i < leg.Segments.Length; i++)
            {
                DeerLegSegmentState segment = leg.Segments[i];
                ObservePenetration($"leg{leg.Index}/segment{i}", segment.Pos, segment.Radius);
            }
        }
    }

    private void ObservePenetration(string part, Vector3 center, float radius)
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

    private void ObservePairGuard()
    {
        bool guardRoute = _route is "flat" or "slope" or "steps" or "target"
            or "wall" or "turn" or "reverse" or "rough" or "rest";
        // 行进路线跳过出生段链装配瞬态；rest 则从第一拍开始观察，并在每对首次得到
        // AttachedAtTip 支点后要求它永远不能双脚同时腾空。出生找地不是主动抬脚。
        if (!guardRoute || (_route != "rest" && _tick < 100))
        {
            return;
        }
        for (int pair = 0; pair < _pairWasGrounded.Length; pair++)
        {
            DeerLeg? first = null;
            DeerLeg? second = null;
            foreach (DeerLeg leg in _deer.Legs)
            {
                if (leg.PairIndex != pair)
                {
                    continue;
                }
                if (first is null) first = leg;
                else second = leg;
            }
            if (first is null || second is null)
            {
                continue;
            }
            _pairWasGrounded[pair] |= first.AttachedAtTip || second.AttachedAtTip;
            string snapshot = PairDiagnosticSnapshot(pair, first, second);
            bool bothAir = _pairWasGrounded[pair]
                && !first.AttachedAtTip && !second.AttachedAtTip;
            if (bothAir)
            {
                _pairAirViolations++;
                if (_pairDiagnosticPair < 0)
                {
                    _pairDiagnosticPair = pair;
                    _pairDiagnosticUntil = _tick + 5;
                    foreach (string history in _pairHistories[pair])
                    {
                        GD.Print($"[DEER-PAIR-DIAG] history {history}");
                    }
                    GD.Print($"[DEER-PAIR-DIAG] first {snapshot}");
                }
                else if (_pairDiagnosticPair == pair && _tick <= _pairDiagnosticUntil)
                {
                    GD.Print($"[DEER-PAIR-DIAG] follow {snapshot}");
                }
            }
            else if (_pairDiagnosticPair == pair && _tick <= _pairDiagnosticUntil)
            {
                GD.Print($"[DEER-PAIR-DIAG] follow {snapshot}");
            }
            if (_pairDiagnosticPair < 0)
            {
                _pairHistories[pair].Enqueue(snapshot);
                while (_pairHistories[pair].Count > 10)
                {
                    _pairHistories[pair].Dequeue();
                }
            }
        }
    }

    private void ObserveRestStepCycle()
    {
        if (_route != "rest")
        {
            return;
        }

        bool qualified = _deer.IdleTicks > _preset.RestDelayTicks
            && _deer.RestAmount > 0f;
        if (qualified && _restQualifiedTick < 0)
        {
            _restQualifiedTick = _tick;
        }

        int voluntaryDelta = _deer.VoluntaryReleaseSerial
            - _restLastVoluntaryReleaseSerial;
        if (voluntaryDelta < 0)
        {
            _restReleaseMappingFailures++;
            voluntaryDelta = 0;
        }
        int mappedActiveReleases = 0;
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            DeerLeg leg = _deer.Legs[i];
            int stepDelta = leg.StepSerial - _restLastStepSerials[i];
            int landingDelta = leg.LandingSerial - _restLastLandingSerials[i];
            int forcedDelta = leg.ForcedReleaseSerial - _restLastForcedReleaseSerials[i];
            if (stepDelta < 0 || landingDelta < 0 || forcedDelta < 0)
            {
                _restReleaseMappingFailures += Math.Abs(Math.Min(0, stepDelta))
                    + Math.Abs(Math.Min(0, landingDelta))
                    + Math.Abs(Math.Min(0, forcedDelta));
                stepDelta = Math.Max(0, stepDelta);
                landingDelta = Math.Max(0, landingDelta);
                forcedDelta = Math.Max(0, forcedDelta);
            }
            if (forcedDelta > stepDelta)
            {
                _restReleaseMappingFailures += forcedDelta - stepDelta;
                forcedDelta = stepDelta;
            }
            int activeStepDelta = stepDelta - forcedDelta;
            mappedActiveReleases += activeStepDelta;
            if (qualified)
            {
                // Tick 内腿先处理 touchdown，控制器最后才统一选择 release。必须先用本拍
                // LandingSerial 结清旧债，再登记本拍新释放；否则“同拍先落后抬”会用较早的
                // touchdown 错误冲销刚发生的 release，让事件门假绿。
                int completedReplants = Math.Min(
                    _restOutstandingReplants[i], landingDelta);
                _restOutstandingReplants[i] -= completedReplants;
                _restRetractionReplants += completedReplants;
                _restOutstandingReplants[i] += activeStepDelta;
            }

            _restLastStepSerials[i] = leg.StepSerial;
            _restLastLandingSerials[i] = leg.LandingSerial;
            _restLastForcedReleaseSerials[i] = leg.ForcedReleaseSerial;
        }
        if (mappedActiveReleases != voluntaryDelta)
        {
            _restReleaseMappingFailures += Math.Abs(mappedActiveReleases - voluntaryDelta);
        }
        if (!qualified)
        {
            _restPrematureActiveReleases += voluntaryDelta;
        }
        else
        {
            _restRetractionReleases += voluntaryDelta;
        }
        _restLastVoluntaryReleaseSerial = _deer.VoluntaryReleaseSerial;
    }

    private string PairDiagnosticSnapshot(int pair, DeerLeg first, DeerLeg second) =>
        $"tick={_tick} pair={pair} planted={_deer.PlantedLegCount} " +
        $"voluntary={_deer.VoluntaryReleaseSerial} stall={_deer.StallTicks} " +
        $"A[{LegDiagnostic(first)}] B[{LegDiagnostic(second)}]";

    private static string LegDiagnostic(DeerLeg leg) =>
        $"i={leg.Index} attached={leg.AttachedAtTip} gripping={leg.Gripping} " +
        $"cooldown={leg.GripCooldown} step={leg.StepSerial} forced={leg.ForcedReleaseSerial} " +
        $"reach={leg.ReachRatio:F3} " +
        $"candidate={leg.HasCandidate}/{leg.CandidateConfirmCounter} " +
        $"tip={Format(leg.Tip.Pos)} desired={Format(leg.DesiredGripPoint)} " +
        $"candidatePoint={Format(leg.CandidatePoint)} grip={Format(leg.GripPoint)}";

    private static string Format(Vector3 value) =>
        $"({value.X:F3},{value.Y:F3},{value.Z:F3})";

    private void RecordDeterminism()
    {
        _hasher.FoldBody(_deer.Body);
        _deer.FoldDeterministicState(_hasher);
        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[DEER-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
    }

    private int DumpDeterministicResult()
    {
        int totalLandings = 0;
        int minimumLegLandings = int.MaxValue;
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            int landings = _deer.Legs[i].LandingSerial - _initialLandingSerials[i];
            totalLandings += landings;
            minimumLegLandings = Math.Min(minimumLegLandings, landings);
        }
        float averageSupport = _metricSamples > 0 ? _supportSum / _metricSamples : 0f;
        float averageRawSupport = _metricSamples > 0 ? _rawSupportSum / _metricSamples : 0f;
        float averageHeightError = _metricSamples > 0 ? _heightErrorSum / _metricSamples : 0f;
        int minimumPostTurnLandings = int.MaxValue;
        if (_turnStarted && _turnLandingSerials.Length == _deer.Legs.Count)
        {
            for (int i = 0; i < _deer.Legs.Count; i++)
            {
                minimumPostTurnLandings = Math.Min(minimumPostTurnLandings,
                    _deer.Legs[i].LandingSerial - _turnLandingSerials[i]);
            }
        }
        else
        {
            minimumPostTurnLandings = 0;
        }
        Vector3 finalForward = HorizontalUnit(_deer.Forward, _turnForwardBefore);
        Vector3 finalPhysicalAxis = PhysicalBodyAxis();
        float turnDot = Mathf.Clamp(_turnPhysicalAxisBefore.Dot(finalPhysicalAxis), -1f, 1f);
        float turnAngleDegrees = Mathf.RadToDeg(MathF.Acos(turnDot));
        int minimumPostReverseLandings = MinimumPostReverseLandings();
        float reverseDot = Mathf.Clamp(
            _reversePhysicalAxisBefore.Dot(finalPhysicalAxis), -1f, 1f);
        float reverseAngleDegrees = Mathf.RadToDeg(MathF.Acos(reverseDot));
        float minimumMeanPoleAlignment = MinimumReverseMean(
            _reversePoleAlignmentSums, _reversePoleSamples);
        float minimumMeanLongitudinalPoleAlignment = MinimumReverseMean(
            _reversePoleLongitudinalSums, _reversePoleSamples);
        float minimumMeanOutwardPoleAlignment = MinimumReverseMean(
            _reversePoleOutwardSums, _reversePoleSamples);
        float finalHorizontalSpeed = AverageHorizontalBodySpeed();
        float roughGripHeightSpan = float.IsFinite(_roughMinimumGripHeight)
            && float.IsFinite(_roughMaximumGripHeight)
            ? _roughMaximumGripHeight - _roughMinimumGripHeight
            : 0f;
        float activeHeight = _activePostureSamples > 0
            ? _activeHeightSum / _activePostureSamples : 0f;
        float standingHeight = _standingPostureSamples > 0
            ? _standingHeightSum / _standingPostureSamples : 0f;
        float restHeight = _restPostureSamples > 0
            ? _restHeightSum / _restPostureSamples : 0f;
        float restReachLimit = _restPostureSamples > 0
            ? _restReachLimitSum / _restPostureSamples : 0f;

        GD.Print($"[DEER-METRIC] route={_route} preset={_preset.StableId} " +
                 $"travel={_travelDistance:F3}m progress={_forwardProgress:F3}m " +
                 $"heightGain={_maximumHeightGain:F3}m waypoints={_waypointsReached} " +
                 $"plantedMax={_maxPlanted} rawAvg={averageRawSupport:F3} " +
                 $"rawRange={_minimumRawSupport:F3}..{_maximumRawSupport:F3} " +
                 $"supportAvg={averageSupport:F3} " +
                 $"supportRange={_minimumTotalSupport:F3}..{_maximumTotalSupport:F3} " +
                 $"heightActual={_minimumActualHeight:F3}..{_maximumActualHeight:F3} " +
                 $"heightDesired={_minimumDesiredHeight:F3}..{_maximumDesiredHeight:F3} " +
                 $"heightErrorAvg={averageHeightError:F3} landings={totalLandings} " +
                 $"minLegLandings={minimumLegLandings} pairAir={_pairAirViolations} " +
                 $"bodyDevEnd={_endConstraintDeviation:F4} bodyDevMax={_maximumConstraintDeviation:F4} " +
                 $"legErrorMax={_maximumLegConstraintError:F4}" +
                 $"@leg{_maximumLegConstraintErrorLeg}/tick{_maximumLegConstraintErrorTick} " +
                 $"recoveryRun={_maxRecoveryRun} " +
                 $"poleMin={_minimumPoleDot:F3} supportLateralMax={_maximumSupportLateral:F3} " +
                 $"penetrationMax={_maximumResidualPenetration:F5}" +
                 $"@{_maximumPenetrationPart}/tick{_maximumPenetrationTick}");
        GD.Print($"[DEER-POSTURE] activeSamples={_activePostureSamples} " +
                 $"bad={_activePostureBadSamples} activeHeight={activeHeight:F3} " +
                 $"clearanceMin={_minimumActiveClearance:F3} antlerUpMin={_minimumActiveAntlerUp:F3} " +
                 $"headUp/forwardMin={_minimumActiveHeadUp:F3}/{_minimumActiveHeadForward:F3} " +
                 $"antlerIntrusionMax={_maximumActiveAntlerTrunkIntrusion:F3} " +
                 $"stand/restSamples={_standingPostureSamples}/{_restPostureSamples} " +
                 $"stand/restHeight={standingHeight:F3}/{restHeight:F3} " +
                 $"restClearance={_minimumRestClearance:F3} restReach={restReachLimit:F3} " +
                 $"restFootReachMax={_maximumRestFootReach:F3}" +
                 $"@{_settledRestFootSamples}samples " +
                 $"restAntlerUpMin={_minimumRestAntlerUp:F3} " +
                 $"restHeadUp/forwardMin={_minimumRestHeadUp:F3}/{_minimumRestHeadForward:F3} " +
                 $"restAntlerIntrusionMax={_maximumRestAntlerTrunkIntrusion:F3}");
        foreach (DeerLeg leg in _deer.Legs)
        {
            GD.Print($"[DEER-LEG] {LegDiagnostic(leg)}");
        }

        if (_route == "wall")
        {
            GD.Print($"[DEER-SCENARIO] wall contact={_wallContactObserved}@{_wallContactTick} " +
                     $"contactTicks={_wallContactTicks} " +
                     $"progress={_forwardProgress:F3} postAdvance={_wallPostContactAdvance:F3} " +
                     $"lateTravel={_wallLateTravel:F3} finalHorizontalSpeed={finalHorizontalSpeed:F4} " +
                     $"finalX={BodyCenter().X:F3} " +
                     $"heightGain={_wallMaximumCenterHeight - _initialCenter.Y:F3} " +
                     $"supportUpMin={_wallMinimumSupportUpDot:F3} wallGripTicks={_wallGripTicks}");
        }
        else if (_route == "turn")
        {
            GD.Print($"[DEER-SCENARIO] turn started={_turnStarted} " +
                     $"firstAxis={_turnFirstAxisProgress:F3} secondAxis={_turnSecondAxisProgress:F3} " +
                     $"physicalAngle={turnAngleDegrees:F1}deg " +
                     $"prePhysical={Format(_turnPhysicalAxisBefore)} " +
                     $"postPhysical={Format(finalPhysicalAxis)} " +
                     $"controllerForward={Format(finalForward)} alignedRun={_turnMaximumAlignmentRun} " +
                     $"minPostLandings={minimumPostTurnLandings} poleMin={_minimumPoleDot:F3}");
        }
        else if (_route == "reverse")
        {
            var legBendMeans = new List<string>();
            for (int i = 0; i < _deer.Legs.Count; i++)
            {
                int poleSamples = _reversePoleSamples[i];
                int attachedSamples = _reverseAttachedBowSamples[i];
                int swingSamples = _reverseSwingBowSamples[i];
                int bowPoleSamples = _reverseSwingBowPoleComparable[i];
                float poleMean = MeanAt(_reversePoleAlignmentSums, _reversePoleSamples, i);
                float longitudinalMean = MeanAt(
                    _reversePoleLongitudinalSums, _reversePoleSamples, i);
                float outwardMean = MeanAt(_reversePoleOutwardSums, _reversePoleSamples, i);
                float attachedBow = MeanAt(
                    _reverseAttachedSignedBowSums, _reverseAttachedBowSamples, i);
                float swingBow = MeanAt(
                    _reverseSwingSignedBowSums, _reverseSwingBowSamples, i);
                float swingMatch = RatioAt(
                    _reverseSwingMatches, _reverseSwingComparable, i);
                float bowPoleMean = MeanAt(
                    _reverseSwingBowPoleSums, _reverseSwingBowPoleComparable, i);
                float bowPoleMatch = RatioAt(
                    _reverseSwingBowPoleMatches, _reverseSwingBowPoleComparable, i);
                float landingProgress = MeanAt(
                    _reverseLandingProgressSums, _reverseLandingSamples, i);
                float landingForward = RatioAt(
                    _reverseForwardLandings, _reverseLandingSamples, i);
                legBendMeans.Add($"{i}:p={poleMean:F2}/{longitudinalMean:F2}/" +
                    $"{outwardMean:F2}@{poleSamples} a={attachedBow:F2}@{attachedSamples} " +
                    $"s={swingBow:F2}/{swingMatch:F2}@{swingSamples} " +
                    $"bp={bowPoleMean:F2}/{bowPoleMatch:F2}@{bowPoleSamples} " +
                    $"wrong={_reverseMaximumSwingWrongRuns[i]}/" +
                    $"{_reverseMaximumSwingWrongBows[i]:F2}m " +
                    $"recover={_reverseLegRecoveryTicks[i]} " +
                    $"land={landingProgress:F2}/{landingForward:F2}@{_reverseLandingSamples[i]}");
            }
            GD.Print($"[DEER-SCENARIO] reverse started={_reverseStarted}@{_reverseTick} " +
                     $"outbound={_reverseOutboundProgress:F3} return={_reverseReturnProgress:F3} " +
                     $"physicalAngle={reverseAngleDegrees:F1}deg " +
                     $"prePhysical={Format(_reversePhysicalAxisBefore)} " +
                     $"postPhysical={Format(finalPhysicalAxis)} " +
                     $"controllerForward={Format(finalForward)} " +
                     $"bodyAlignedRun={_reverseMaximumBodyAlignmentRun} " +
                     $"minPostLandings={minimumPostReverseLandings} " +
                     $"settledSamples={_reverseSettledSamples} recoveryTicks={_reverseRecoveryTicks} " +
                     $"poleMeanMin={minimumMeanPoleAlignment:F3}/" +
                     $"{minimumMeanLongitudinalPoleAlignment:F3}/" +
                     $"{minimumMeanOutwardPoleAlignment:F3} " +
                     $"poleInstantMin={_reverseMinimumInstantPoleAlignment:F3} " +
                     $"allSameRun={_reverseMaximumAllSameSignRun} " +
                     $"allOppositeRun={_reverseMaximumAllOppositeRun} " +
                     $"legMeans=[{string.Join(",", legBendMeans)}]");
        }
        else if (_route == "rough")
        {
            GD.Print($"[DEER-SCENARIO] rough progress={_forwardProgress:F3} " +
                     $"heightErrorAvg={averageHeightError:F3} supportAvg={averageSupport:F3} " +
                     $"supportLateralMax={_maximumSupportLateral:F3} " +
                     $"gripHeight={_roughMinimumGripHeight:F3}..{_roughMaximumGripHeight:F3} " +
                     $"gripSpan={roughGripHeightSpan:F3} raisedGripTicks={_roughRaisedGripTicks} " +
                     $"lowSupportRun={_roughMaximumLowSupportRun} " +
                     $"minLegLandings={minimumLegLandings} penetration={_maximumResidualPenetration:F5}" +
                     $"@{_maximumPenetrationPart}/tick{_maximumPenetrationTick}");
        }
        else if (_route == "rest")
        {
            int finalGroundedPairs = CountGroundedPairs();
            int outstandingReplants = CountRestOutstandingReplants();
            GD.Print($"[DEER-SCENARIO] rest qualified={_restQualifiedTick} " +
                     $"delay={_preset.RestDelayTicks} " +
                     $"prematureRelease={_restPrematureActiveReleases} " +
                     $"retractionRelease/replant={_restRetractionReleases}/" +
                     $"{_restRetractionReplants} outstanding={outstandingReplants} " +
                     $"mappingFailures={_restReleaseMappingFailures} " +
                     $"pairAir={_pairAirViolations} finalGroundedPairs={finalGroundedPairs}/" +
                     $"{_pairWasGrounded.Length}");
        }

        var failures = new List<string>();
        if (_nonFinite) failures.Add("state contains NaN/Inf");
        if (_gravityChanged) failures.Add("Body.GravityScale changed from 1");
        if (_endConstraintDeviation >= 0.10f)
            failures.Add($"end body connection deviation {_endConstraintDeviation:F4}m >= 0.10m");
        if (_maximumLegConstraintError >= 0.25f)
            failures.Add($"leg chain constraint error {_maximumLegConstraintError:F4}m >= 0.25m");
        if (_expectHash is ulong expected && _hasher.Value != expected)
            failures.Add($"hash {_hasher.Value:X16} != {expected:X16}");
        if (_pairAirViolations > 0)
            failures.Add($"paired legs were simultaneously airborne for {_pairAirViolations} pair-ticks");

        switch (_route)
        {
            case "flat":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 12f);
                RequireActivePosture(failures);
                if (_waypointsReached < 2)
                    failures.Add($"flat patrol reached only {_waypointsReached} turn points");
                break;
            case "slope":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 12f);
                RequireActivePosture(failures);
                if (_forwardProgress < 12f)
                    failures.Add($"slope forward progress {_forwardProgress:F2}m < 12m");
                if (_maximumHeightGain < 0.45f)
                    failures.Add($"slope height gain {_maximumHeightGain:F2}m < 0.45m");
                break;
            case "steps":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 12f);
                RequireActivePosture(failures);
                if (_forwardProgress < 12f)
                    failures.Add($"step forward progress {_forwardProgress:F2}m < 12m");
                if (_maximumHeightGain < 0.30f)
                    failures.Add($"step height gain {_maximumHeightGain:F2}m < 0.30m");
                break;
            case "wall":
                if (!_wallContactObserved)
                    failures.Add("EndWall contact was never observed");
                if (_wallContactTicks < 40)
                    failures.Add($"EndWall contact persisted for only {_wallContactTicks} ticks");
                if (_forwardProgress < 6f)
                    failures.Add($"wall approach progress {_forwardProgress:F2}m < 6m");
                if (_wallGripTicks != 0)
                    failures.Add($"vertical EndWall became a planted grip for {_wallGripTicks} leg-ticks");
                float standableDot = Mathf.Cos(Mathf.DegToRad(_preset.MaxStandableSlopeDegrees));
                if (_wallMinimumSupportUpDot < standableDot - 0.02f)
                    failures.Add($"wall normal contaminated the support frame " +
                                 $"(up dot={_wallMinimumSupportUpDot:F3}, floor={standableDot:F3})");
                if (_wallMaximumCenterHeight - _initialCenter.Y > 0.60f)
                    failures.Add($"wall push climbed {_wallMaximumCenterHeight - _initialCenter.Y:F2}m > 0.60m");
                // 前端 chunk 先碰墙后，约 1.6m 的粗躯干仍会合法跟进；真正的安全门是
                // 整体中心不能越过墙面，且末段位移/速度必须收敛。
                if (_wallPostContactAdvance > 2.00f || BodyCenter().X >= 39.20f)
                    failures.Add($"wall did not contain the body " +
                                 $"(postAdvance={_wallPostContactAdvance:F2}m, finalX={BodyCenter().X:F2})");
                if (_wallLateTravel > 0.60f || finalHorizontalSpeed > 0.03f)
                    failures.Add($"wall stop unstable (lateTravel={_wallLateTravel:F2}m, " +
                                 $"speed={finalHorizontalSpeed:F3}m/tick)");
                if (averageSupport < 0.45f || minimumLegLandings < 1)
                    failures.Add("wall approach did not retain supported complete gait");
                RequireActivePosture(failures);
                break;
            case "turn":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 20f);
                RequireActivePosture(failures);
                if (!_turnStarted)
                    failures.Add("90-degree turn trigger was never reached");
                if (_turnFirstAxisProgress < 6f || _turnSecondAxisProgress < 20f)
                    failures.Add($"turn net progress too small " +
                                 $"({_turnFirstAxisProgress:F2}m X, {_turnSecondAxisProgress:F2}m Z)");
                if (minimumPostTurnLandings < 3)
                    failures.Add($"a leg completed only {minimumPostTurnLandings} post-turn landings");
                if (_minimumPoleDot <= 0.05f)
                    failures.Add($"leg bend pole flipped or collapsed (min dot={_minimumPoleDot:F3})");
                if (_turnPhysicalAxisBefore.Dot(Vector3.Right) < 0.65f
                    || finalPhysicalAxis.Dot(Vector3.Back) < 0.65f
                    || _turnMaximumAlignmentRun < 12
                    || turnAngleDegrees is < 65f or > 115f)
                {
                    failures.Add($"body did not complete an approximately 90-degree 3D turn " +
                                 $"(angle={turnAngleDegrees:F1}, preX={_turnPhysicalAxisBefore.X:F2}, " +
                                 $"postZ={finalPhysicalAxis.Z:F2}, alignedRun={_turnMaximumAlignmentRun})");
                }
                break;
            case "reverse":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 20f);
                RequireActivePosture(failures);
                if (!_reverseStarted)
                    failures.Add("180-degree reverse trigger was never reached");
                if (_reverseOutboundProgress < 6f || _reverseReturnProgress < 24f)
                    failures.Add($"reverse net progress too small " +
                                 $"({_reverseOutboundProgress:F2}m outbound, " +
                                 $"{_reverseReturnProgress:F2}m return)");
                if (minimumPostReverseLandings < 2)
                    failures.Add($"a leg completed only {minimumPostReverseLandings} " +
                                 "post-reverse landings");
                if (_reversePhysicalAxisBefore.Dot(Vector3.Right) < 0.65f
                    || finalPhysicalAxis.Dot(Vector3.Left) < 0.65f
                    || _reverseMaximumBodyAlignmentRun < 20
                    || reverseAngleDegrees is < 150f or > 180.01f)
                {
                    failures.Add($"body did not complete an approximately 180-degree turn " +
                                 $"(angle={reverseAngleDegrees:F1}, " +
                                 $"preX={_reversePhysicalAxisBefore.X:F2}, " +
                                 $"postX={finalPhysicalAxis.X:F2}, " +
                                 $"alignedRun={_reverseMaximumBodyAlignmentRun})");
                }
                if (_reverseSettledSamples < 40)
                    failures.Add($"only {_reverseSettledSamples} settled post-reverse bow samples");
                if (_reverseRecoveryTicks < 0 || _reverseRecoveryTicks > 180)
                    failures.Add($"swinging leg shape did not recover within 180 ticks " +
                                 $"(recovery={_reverseRecoveryTicks})");
                if (minimumMeanPoleAlignment < 0.35f)
                    failures.Add($"bend poles remained in the old anatomical hemisphere " +
                                 $"(minimum mean alignment={minimumMeanPoleAlignment:F3})");
                if (minimumMeanLongitudinalPoleAlignment <= 0f
                    || minimumMeanOutwardPoleAlignment <= 0f)
                    failures.Add($"bend poles lost longitudinal/outward anatomy " +
                                 $"(minimum means={minimumMeanLongitudinalPoleAlignment:F3}/" +
                                 $"{minimumMeanOutwardPoleAlignment:F3})");
                for (int i = 0; i < _deer.Legs.Count; i++)
                {
                    DeerLeg leg = _deer.Legs[i];
                    float expectedSign = MathF.Sign(leg.ForwardSplay);
                    float swingMean = MeanAt(
                        _reverseSwingSignedBowSums, _reverseSwingBowSamples, i);
                    float swingMatch = RatioAt(
                        _reverseSwingMatches, _reverseSwingComparable, i);
                    float bowPoleMean = MeanAt(
                        _reverseSwingBowPoleSums, _reverseSwingBowPoleComparable, i);
                    float bowPoleMatch = RatioAt(
                        _reverseSwingBowPoleMatches, _reverseSwingBowPoleComparable, i);
                    float landingProgress = MeanAt(
                        _reverseLandingProgressSums, _reverseLandingSamples, i);
                    float landingForward = RatioAt(
                        _reverseForwardLandings, _reverseLandingSamples, i);
                    if (_reversePoleSamples[i] < 40)
                        failures.Add($"leg {i} has only {_reversePoleSamples[i]} pole samples");
                    if (_reverseSwingBowSamples[i] < 8
                        || _reverseSwingComparable[i] < 6)
                        failures.Add($"leg {i} has insufficient swinging bow samples " +
                                     $"({_reverseSwingBowSamples[i]}/" +
                                     $"{_reverseSwingComparable[i]})");
                    else if (swingMatch < 0.60f || swingMean * expectedSign <= 0.003f)
                        failures.Add($"leg {i} swinging chain kept the wrong longitudinal bend " +
                                     $"(mean={swingMean:F3}m, match={swingMatch:F3})");
                    if (_reverseSwingBowPoleComparable[i] < 6
                        || bowPoleMatch < 0.60f || bowPoleMean <= 0.10f)
                        failures.Add($"leg {i} swinging bow did not follow its pole " +
                                     $"(samples={_reverseSwingBowPoleComparable[i]}, " +
                                     $"mean={bowPoleMean:F3}, match={bowPoleMatch:F3})");
                    if (_reverseMaximumSwingWrongRuns[i] > 8)
                        failures.Add($"leg {i} swinging bend stayed wrong for " +
                                     $"{_reverseMaximumSwingWrongRuns[i]} consecutive ticks");
                    if (_reverseLegRecoveryTicks[i] < 0
                        || _reverseLegRecoveryTicks[i] > 180)
                        failures.Add($"leg {i} swinging bend recovery took " +
                                     $"{_reverseLegRecoveryTicks[i]} ticks");
                    if (_reverseLandingSamples[i] < 2
                        || landingForward < 0.65f || landingProgress <= 0.04f)
                        failures.Add($"leg {i} did not retain forward touchdown " +
                                     $"(samples={_reverseLandingSamples[i]}, " +
                                     $"progress={landingProgress:F3}m, " +
                                     $"forward={landingForward:F3})");
                }
                break;
            case "rough":
                RequireGait(failures, totalLandings, minimumLegLandings,
                    averageSupport, averageRawSupport, averageHeightError, 20f);
                RequireActivePosture(failures);
                if (_forwardProgress < 24f)
                    failures.Add($"rough-course progress {_forwardProgress:F2}m < 24m");
                if (_maximumHeightGain < 0.15f || _maximumSupportLateral < 0.025f)
                    failures.Add($"rough geometry was not expressed in height/support frame " +
                                 $"(heightGain={_maximumHeightGain:F2}, lateral={_maximumSupportLateral:F3})");
                if (roughGripHeightSpan < 0.12f || _roughRaisedGripTicks < 20)
                    failures.Add($"rough course did not produce sustained staggered footholds " +
                                 $"(span={roughGripHeightSpan:F3}m, raisedTicks={_roughRaisedGripTicks})");
                if (_roughMaximumLowSupportRun > 30)
                    failures.Add($"rough course lost stable support for " +
                                 $"{_roughMaximumLowSupportRun} consecutive ticks");
                if (_maximumResidualPenetration > 0.002f)
                    failures.Add($"rough course left {_maximumResidualPenetration:F4}m penetration " +
                                 $"at {_maximumPenetrationPart}/tick{_maximumPenetrationTick}");
                break;
            case "rest":
                if (_maxPlanted < 3 || averageSupport <= 0.55f)
                    failures.Add($"rest never established support (planted={_maxPlanted}, support={averageSupport:F3})");
                if (_restQualifiedTick < 0)
                    failures.Add($"rest never qualified after delay {_preset.RestDelayTicks}");
                if (_restPrematureActiveReleases > 0)
                    failures.Add($"rest actively released {_restPrematureActiveReleases} leg(s) " +
                                 $"before RestDelayTicks={_preset.RestDelayTicks}");
                int outstandingReplants = CountRestOutstandingReplants();
                if (_restRetractionReleases < 1)
                    failures.Add("rest did not actively release any leg for retraction");
                if (_restRetractionReplants != _restRetractionReleases
                    || outstandingReplants != 0)
                    failures.Add($"rest did not replant every retraction release " +
                                 $"(release/replant={_restRetractionReleases}/" +
                                 $"{_restRetractionReplants}, outstanding={outstandingReplants})");
                if (_restReleaseMappingFailures > 0)
                    failures.Add($"rest release observation could not map " +
                                 $"{_restReleaseMappingFailures} active release event(s) to a leg");
                int finalGroundedPairs = CountGroundedPairs();
                if (finalGroundedPairs != _pairWasGrounded.Length)
                    failures.Add($"rest ended with only {finalGroundedPairs}/" +
                                 $"{_pairWasGrounded.Length} pairs physically grounded");
                float restBudget = _preset.PreferredBodyHeight * (_preset.RestHeightRatio + 0.10f);
                if (_deer.DesiredBodyHeight > restBudget)
                    failures.Add($"rest target height {_deer.DesiredBodyHeight:F3}m > {restBudget:F3}m");
                if (MathF.Abs(_deer.ActualBodyHeight - _deer.DesiredBodyHeight)
                    > _preset.PreferredBodyHeight * 0.20f)
                    failures.Add("rest actual height did not converge toward lowered target");
                RequireRestPosture(failures, standingHeight, restHeight, restReachLimit);
                break;
            case "launch":
                if (!_launchCalled || !_launchContract)
                    failures.Add("Launch did not preserve impulse/release/gravity contract");
                if (_maximumHeightGain < 0.35f)
                    failures.Add($"Launch rose only {_maximumHeightGain:F2}m");
                if (_maxRecoveryRun < 20)
                    failures.Add($"Launch recovered two planted legs for only {_maxRecoveryRun} consecutive ticks");
                RequireActivePosture(failures);
                break;
            case "target":
                if (!_sawAtTarget || _waypointsReached < 1)
                    failures.Add($"MoveTarget arrival not observed (waypoints={_waypointsReached})");
                if (_travelDistance < 2f)
                    failures.Add($"MoveTarget travel {_travelDistance:F2}m < 2m");
                if (averageSupport < 0.55f || minimumLegLandings < 2
                    || averageHeightError > _preset.PreferredBodyHeight * 0.20f)
                    failures.Add("MoveTarget route did not retain supported complete gait");
                RequireActivePosture(failures);
                break;
            case "lifecycle":
                if (!_shiftContract) failures.Add("Shift did not move full state and MoveTarget continuously");
                if (!_teleportContract) failures.Add("Teleport did not clear target/grips/support");
                if (!_launchContract) failures.Add("Launch did not add the requested impulse and release legs");
                if (!_lifecycleTargetRetained) failures.Add("Launch unexpectedly changed host MoveTarget");
                if (_maxRecoveryRun < 20)
                    failures.Add($"lifecycle Launch recovery lasted only {_maxRecoveryRun} ticks");
                RequireActivePosture(failures);
                break;
        }

        bool pass = failures.Count == 0;
        GD.Print(pass
            ? "[DEER-RESULT] PASS"
            : $"[DEER-RESULT] FAIL: {string.Join("; ", failures)}");
        return pass ? 0 : 1;
    }

    private void RequireActivePosture(List<string> failures)
    {
        if (_activePostureSamples < 40)
        {
            failures.Add($"only {_activePostureSamples} stable active-posture samples");
            return;
        }
        int allowedBad = Math.Max(1, (int)MathF.Ceiling(_activePostureSamples * 0.02f));
        if (_activePostureBadSamples > allowedBad)
        {
            failures.Add($"active posture failed {_activePostureBadSamples}/" +
                         $"{_activePostureSamples} samples (allowed {allowedBad})");
        }
    }

    private void RequireRestPosture(
        List<string> failures,
        float standingHeight,
        float restHeight,
        float restReachLimit)
    {
        float legLength = _deer.Legs[0].MaxLength;
        if (_standingPostureSamples < 20 || _restPostureSamples < 20)
        {
            failures.Add($"insufficient stand/rest samples " +
                         $"({_standingPostureSamples}/{_restPostureSamples})");
            return;
        }
        if (_minimumRestClearance < legLength * 0.15f)
            failures.Add($"rest body clearance {_minimumRestClearance:F2}m is too low");
        if (standingHeight - restHeight < legLength * 0.15f
            || restHeight / MathF.Max(standingHeight, 1e-4f) > 0.75f)
            failures.Add($"stand/rest height separation is too small " +
                         $"({standingHeight:F2}/{restHeight:F2}m)");
        float expectedReach = legLength * _preset.RestLegReachRatio;
        if (MathF.Abs(restReachLimit - expectedReach) > legLength * 0.015f)
            failures.Add($"rest reach {restReachLimit:F2}m != {expectedReach:F2}m");
        if (_settledRestFootSamples < 20)
            failures.Add($"only {_settledRestFootSamples} settled rest-foot samples");
        else if (_maximumRestFootReach > restReachLimit * 1.08f)
            failures.Add($"rest foot reach {_maximumRestFootReach:F2}m exceeds " +
                         $"current limit {restReachLimit:F2}m");
        if (_minimumRestAntlerUp < 0.85f
            || _minimumRestHeadUp <= 0.55f
            || _minimumRestHeadForward <= 0.08f
            || _maximumRestAntlerTrunkIntrusion > 0.10f)
        {
            failures.Add($"rest head/antler posture regressed " +
                         $"(antlerUp={_minimumRestAntlerUp:F2}, " +
                         $"head={_minimumRestHeadUp:F2}/{_minimumRestHeadForward:F2}, " +
                         $"intrusion={_maximumRestAntlerTrunkIntrusion:F2})");
        }
    }

    private void RequireGait(
        List<string> failures,
        int totalLandings,
        int minimumLegLandings,
        float averageSupport,
        float averageRawSupport,
        float averageHeightError,
        float minimumTravel)
    {
        if (_travelDistance < minimumTravel)
            failures.Add($"gait travel {_travelDistance:F2}m < {minimumTravel:F2}m");
        if (_maxPlanted < 3)
            failures.Add($"at most {_maxPlanted}/4 legs planted");
        if (averageSupport <= 0.55f || averageRawSupport <= 0.35f)
            failures.Add($"support too low (raw={averageRawSupport:F3}, filtered={averageSupport:F3})");
        if (totalLandings < 12 || minimumLegLandings < 3)
            failures.Add($"incomplete step cycle (landings={totalLandings}, min per leg={minimumLegLandings})");
        if (averageHeightError > _preset.PreferredBodyHeight * 0.20f)
            failures.Add($"height servo error {averageHeightError:F2}m is too large");
    }

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

        float interpolation = (float)Engine.GetPhysicsInterpolationFraction();
        _renderer.Draw(interpolation, _deer.SupportNormal, _deer.TotalSupport,
            _deer.ActualBodyHeight, _deer.DesiredBodyHeight);
        MoveTargetKind targetKind = _deer.MoveTarget is null
            ? MoveTargetKind.None
            : MoveTargetKind.External;
        _terrain.Draw(_camera, _deer.Head.LerpPos(interpolation), targetKind,
            _deer.MoveTarget ?? Vector3.Zero);
        _hud?.UpdateStatus(_preset.StableId, _deer.PlantedLegCount, _deer.TotalSupport,
            _deer.DesiredBodyHeight, _deer.ActualBodyHeight, _deer.Hesitation,
            _deer.AtMoveTarget, LegStateText());
    }

    private void UpdateShowcaseCamera()
    {
        Vector3 center = BodyCenter();
        Vector3 offset = new(-8f, 8f, 16f);
        _camera.GlobalPosition = center + offset;
        _camera.Fov = 60f;
        _camera.LookAt(center, Vector3.Up);
        _warmFill.GlobalPosition = center + new Vector3(-2f, 5f, 5f);
    }

    private bool WantCameraFly =>
        Input.IsMouseButtonPressed(MouseButton.Right)
        && !Input.IsPhysicalKeyPressed(Key.Shift);

    private void UpdateCameraFly(float delta)
    {
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
                _cameraPitch = Mathf.Clamp(_cameraPitch - motion.Relative.Y * CameraMouseSensitivity,
                    -1.5f, 1.5f);
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
            _pendingTargetPick = mouseButton.Position;
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (key.PhysicalKeycode == Key.F3)
        {
            _terrain.Enabled = !_terrain.Enabled;
        }
        else if (key.PhysicalKeycode == Key.Space)
        {
            _deer.Launch(new Vector3(0.06f, 0.32f, 0.02f));
        }
        else if (key.PhysicalKeycode == Key.T)
        {
            _deer.Teleport(new Vector3(0f, 0.35f, 1.5f));
        }
        else if (key.PhysicalKeycode == Key.H)
        {
            _deer.Shift(new Vector3(0f, 0f, 1.5f));
        }
        else if (key.PhysicalKeycode == Key.R)
        {
            SpawnDeer(_spawnSurface, Vector3.Right, _preset);
        }
        else if (key.PhysicalKeycode is >= Key.Key1 and <= Key.Key3)
        {
            SelectPreset((int)(key.PhysicalKeycode - Key.Key1));
        }
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Length)
        {
            return;
        }
        Vector3 surface = SurfaceBelow(BodyCenter());
        SpawnDeer(surface, _deer.Forward, _presets[index]);
        _hud?.SyncPreset(index);
        GD.Print($"[DEER-SANDBOX] preset -> {_presets[index].StableId}");
    }

    private Vector3 SurfaceBelow(Vector3 point)
    {
        _raycast.Bind(GetWorld3D().DirectSpaceState);
        if (_raycast.Raycast(point + Vector3.Up * 2f, point - Vector3.Up * 20f, out TerrainHit hit))
        {
            return hit.Point;
        }
        return point - Vector3.Up * _preset.PreferredBodyHeight;
    }

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--deer-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--deer-determinism=".Length..], CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0) throw new FormatException("tick count must be positive");
                }
                else if (argument.StartsWith("--deer-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(
                        argument["--deer-tps=".Length..], CultureInfo.InvariantCulture);
                    if (_requestedTps <= 0) throw new FormatException("tps must be positive");
                }
                else if (argument.StartsWith("--deer-preset=", StringComparison.Ordinal))
                {
                    _presetName = argument["--deer-preset=".Length..].ToLowerInvariant();
                    _ = ResolvePreset(_presetName);
                }
                else if (argument.StartsWith("--deer-route=", StringComparison.Ordinal))
                {
                    _route = argument["--deer-route=".Length..].ToLowerInvariant();
                    if (_route is not ("flat" or "slope" or "steps" or "rest"
                        or "launch" or "target" or "lifecycle"
                        or "wall" or "turn" or "reverse" or "rough"))
                    {
                        throw new FormatException("unknown deer route");
                    }
                }
                else if (argument.StartsWith("--deer-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(
                        argument["--deer-perturb=".Length..], CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb)) throw new FormatException("perturb must be finite");
                }
                else if (argument.StartsWith("--deer-expect-hash=", StringComparison.Ordinal))
                {
                    string text = argument["--deer-expect-hash=".Length..];
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                    _expectHash = ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else if (argument.StartsWith("--deer-", StringComparison.Ordinal))
                {
                    throw new FormatException($"unknown option {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException
                or ArgumentException)
            {
                GD.PushError($"[DEER-CLI] {argument}: {exception.Message}");
                return false;
            }
        }
        return true;
    }

    private static DeerParams ResolvePreset(string name) => name switch
    {
        "original" or DeerFactory.OriginalId => DeerFactory.Original(),
        "compact" or DeerFactory.CompactId => DeerFactory.Compact(),
        "strider" or DeerFactory.StriderId => DeerFactory.Strider(),
        _ => throw new ArgumentException($"unknown Deer preset '{name}'"),
    };

    private static Vector3 SpawnForRoute(string route) => route switch
    {
        // 活动腿水平展开约 4..6m；必须在坡脚前留出完整四足落地区，不能让出生候选
        // 一半位于斜坡、一半位于地板交叠缝。
        "slope" => new Vector3(-18f, 0f, -18f),
        "steps" => new Vector3(-10f, 0f, 18f),
        "wall" => new Vector3(28f, 0f, 0f),
        "turn" => new Vector3(-24f, 0f, -14f),
        // 180° 路线要给回程留下足够步数，又不能让 10m 长腿越出 80m 地板边缘。
        "reverse" => new Vector3(-8f, 0f, 0f),
        "rough" => new Vector3(-12f, 0f, 9f),
        _ => new Vector3(-24f, 0f, 0f),
    };

    private Vector3 BodyCenter()
    {
        return _deer.BodyCenter;
    }

    private Vector3 SurfacePointBelowBody()
    {
        if (_deer.HasCurrentFloor)
        {
            return _deer.CurrentFloorPoint;
        }
        Vector3 center = BodyCenter();
        return new Vector3(center.X, 0f, center.Z);
    }

    private string LegStateText()
    {
        var states = new string[_deer.Legs.Count];
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            DeerLeg leg = _deer.Legs[i];
            states[i] = leg.Gripping ? $"L{leg.Index}:G{leg.SupportContribution:F2}"
                : leg.HasCandidate ? $"L{leg.Index}:C"
                : $"L{leg.Index}:S";
        }
        return string.Join("  ", states);
    }

    private Vector3[] SnapshotBodyVelocities()
    {
        var values = new Vector3[_deer.Body.Chunks.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = _deer.Body.Chunks[i].Vel;
        }
        return values;
    }

    private Vector3[] SnapshotLegSegments()
    {
        var values = new List<Vector3>();
        foreach (DeerLeg leg in _deer.Legs)
        {
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                values.Add(segment.Pos);
            }
        }
        return values.ToArray();
    }

    private bool CheckLegSegmentShift(Vector3[] before, Vector3 delta)
    {
        int index = 0;
        foreach (DeerLeg leg in _deer.Legs)
        {
            foreach (DeerLegSegmentState segment in leg.Segments)
            {
                if (!Near(segment.Pos - before[index++], delta))
                {
                    return false;
                }
            }
        }
        return index == before.Length;
    }

    private bool CheckLaunchDelta(Vector3[] before, Vector3 impulse)
    {
        if (before.Length != _deer.Body.Chunks.Count)
        {
            return false;
        }
        for (int i = 0; i < before.Length; i++)
        {
            if (!Near(_deer.Body.Chunks[i].Vel - before[i], impulse, 2e-5f))
            {
                return false;
            }
        }
        return true;
    }

    private bool AllLegsReleased()
    {
        foreach (DeerLeg leg in _deer.Legs)
        {
            if (leg.AttachedAtTip) return false;
        }
        return true;
    }

    private int CountGroundedPairs()
    {
        int grounded = 0;
        for (int pair = 0; pair < _pairWasGrounded.Length; pair++)
        {
            bool pairGrounded = false;
            foreach (DeerLeg leg in _deer.Legs)
            {
                if (leg.PairIndex == pair && leg.AttachedAtTip)
                {
                    pairGrounded = true;
                    break;
                }
            }
            if (pairGrounded)
            {
                grounded++;
            }
        }
        return grounded;
    }

    private int CountRestOutstandingReplants()
    {
        int outstanding = 0;
        foreach (int count in _restOutstandingReplants)
        {
            outstanding += count;
        }
        return outstanding;
    }

    private float AverageHorizontalBodySpeed()
    {
        float sum = 0f;
        foreach (BodyChunk chunk in _deer.Body.Chunks)
        {
            sum += new Vector2(chunk.Vel.X, chunk.Vel.Z).Length();
        }
        return sum / _deer.Body.Chunks.Count;
    }

    private int MinimumPostReverseLandings()
    {
        if (!_reverseStarted || _reverseLandingSerials.Length != _deer.Legs.Count)
        {
            return 0;
        }
        int minimum = int.MaxValue;
        for (int i = 0; i < _deer.Legs.Count; i++)
        {
            minimum = Math.Min(minimum,
                _deer.Legs[i].LandingSerial - _reverseLandingSerials[i]);
        }
        return minimum == int.MaxValue ? 0 : minimum;
    }

    private float MinimumReverseMean(float[] sums, int[] samples)
    {
        if (sums.Length != _deer.Legs.Count
            || samples.Length != _deer.Legs.Count)
        {
            return -1f;
        }
        float minimum = float.PositiveInfinity;
        for (int i = 0; i < sums.Length; i++)
        {
            if (samples[i] <= 0)
            {
                return -1f;
            }
            minimum = MathF.Min(minimum, sums[i] / samples[i]);
        }
        return float.IsFinite(minimum) ? minimum : -1f;
    }

    private static float MeanAt(float[] sums, int[] samples, int index) =>
        index >= 0 && index < sums.Length && index < samples.Length && samples[index] > 0
            ? sums[index] / samples[index]
            : 0f;

    private static float RatioAt(int[] numerator, int[] denominator, int index) =>
        index >= 0 && index < numerator.Length && index < denominator.Length
            && denominator[index] > 0
            ? (float)numerator[index] / denominator[index]
            : 0f;

    private Vector3 PhysicalBodyAxis() => HorizontalUnit(
        _deer.Head.Pos - _deer.Trunk[^1].Pos, _deer.Forward);

    private static Vector3 HorizontalUnit(Vector3 value, Vector3 fallback)
    {
        value.Y = 0f;
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        fallback.Y = 0f;
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Right;
    }

    private static bool Near(Vector3 a, Vector3 b, float epsilon = 1e-5f) =>
        (a - b).LengthSquared() <= epsilon * epsilon;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
