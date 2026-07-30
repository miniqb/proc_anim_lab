using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.SpiderSandbox;

/// <summary>
/// 蜘蛛专用白盒：40 tick 固定步驱动独立 SpiderLocomotionController。
/// 交互模式提供 WASD、Shift+右键直喂目标、左键拖拽和 1/2 切换体型；
    /// --determinism 模式运行可断言的直线步态、平地急转、地面/斜坡/墙角/墙顶、
    /// 窄墙抱边、墙墙 L 角与墙到天花板路线。
/// </summary>
public partial class SpiderSandboxWorld : Node3D
{
    [Export] public float GravityMps2 = 36f;
    /// <summary>0=使用品种配置；正数仅供交互调试时显式覆盖。</summary>
    [Export] public int ConstraintIterations;
    [Export] public float DragSpring = 0.2f;
    [Export] public float DragDamping = 0.3f;
    [Export] public float DragMaxForce = 0.5f;
    [Export] public float CameraFlySpeed = 7f;
    [Export] public float CameraMouseSensitivity = 0.003f;

    private const float TickDt = 0.025f;
    private const float DeepConstraintThreshold = 0.12f;
    private const int DeepConstraintRunLimit = 45;
    private const int ZeroGripRunLimit = 80;
    private const float MaxBodyStepLimit = 0.45f;
    private const float MaxKneeStepRatioLimit = 0.88f;
    private const float MaxIkErrorLimit = 0.001f;
    // Jolt 盒体棱边 + 球体约束在换面瞬间允许 1cm 的单 tick 数值余量；
    // 仍远小于最小身体/脚半径，且最终态会另由持续场景断言背书。
    private const float MaxPostSolvePenetration = 0.01f;
    private const float MaxReachExcessLimit = 0.005f;
    private const float MaxReachDeficitLimit = 0.005f;
    private const int MaxSideRecoveryRunLimit = 35;
    private const int TurnLeadTicks = 120;
    private const int TurnDriveBudgetTicks = 260;
    private const float TurnDriveDistance = 3f;
    private const float TurnLaneToleranceRatio = 0.02f;

    private static readonly Vector3[] CourseRoute =
    {
        new(4.2f, 0f, 0f),
        new(0.5f, 0f, 1.8f),
        new(-7.5f, 0f, 0f),
    };

    private enum Route
    {
        Course,
        Gait,
        NarrowWall,
        WallL,
        WallCeiling,
        Turn,
        Stand,
    }

    private enum TurnKind
    {
        Left,
        Right,
        Around,
    }

    private readonly record struct TurnBalanceSample(
        int Tick,
        float SideGap,
        float PairMeanGap,
        float PairMaxGap,
        float TargetSideGap,
        float TargetPairMaxGap,
        float MinPairRatio,
        float MinFootNominalRatio,
        float MinTargetNominalRatio);

    private readonly RaycastTerrainQuery _terrain = new();
    private readonly SpiderBodyRenderer _renderer = new();
    private readonly DragController _drag = new();
    private readonly List<Body> _bodies = new();
    private readonly DeterminismHasher _hasher = new();
    private readonly Dictionary<SpiderLeg, Vector3> _previousPoles = new();
    private readonly Dictionary<SpiderLeg, int> _sideRecoveryRuns = new();

    private SpiderLocomotionController _controller = null!;
    private SpiderBreedParams _breed = null!;
    private BreedSelectorUI? _breedUi;
    private Camera3D _camera = null!;
    private RayDebugDraw _rayDebug = null!;
    private Vector3 _gravityPerTick;

    private Route _route = Route.Course;
    private TurnKind _turnKind = TurnKind.Left;
    private Vector3 _spawn = new(0f, 0.55f, 0f);
    private bool _spawnExplicit;
    private int _determinismTicks;
    private ulong? _expectHash;
    private float _perturb;
    private long _yankTick = -1;
    private bool _fatal;
    private long _tick;

    private int _courseWaypoint;
    private int _waypointsReached;
    private int _routePhase;
    private long _phaseStartedAt;
    private Vector3 _phaseStartPosition;

    private bool _sawGrip;
    private int _zeroGripRun;
    private int _maxZeroGripRun;
    private int _deepConstraintRun;
    private int _maxDeepConstraintRun;
    private float _maxConstraintDeviation;
    private float _maxBodyStep;
    private long _maxBodyStepTick;
    private int _maxBodyStepChunk = -1;
    private float _maxSettledBodyStep;
    private float _maxKneeStep;
    private float _maxKneeStepRatio;
    private float _maxLocalKneeStepRatio;
    private float _maxIkError;
    private float _maxReachExcess;
    private float _maxReachDeficit;
    private float _maxPenetration;
    private long _maxPenetrationTick;
    private string _maxPenetrationPart = "none";
    private int _poleFlipViolations;
    private int _maxSideRecoveryRun;
    private bool _nonFinite;

    private bool _floorSeen;
    private bool _rampSeen;
    private bool _wallSeen;
    private bool _cornerGripOverlapSeen;
    private bool _mixedSupportSeen;
    private bool _outerCornerSeen;
    private bool _ceilingSeen;
    private bool _firstLWallSeen;
    private bool _secondLWallSeen;
    private long _wallTransitionStart = -1;
    private long _wallTransitionTicks = -1;
    private float _wallStartHeight;
    private float _maxWallRise;
    private float _postTransitionProgress;

    private bool _narrowWallSeen;
    private float _narrowWallStartHeight;
    private float _maxNarrowWallRise;
    private int _narrowObservationTicks;
    private int _narrowMajorityRun;
    private int _maxNarrowMajorityRun;
    private int _narrowBothSidesRun;
    private int _maxNarrowBothSidesRun;
    private int _maxNarrowSideGrips;
    private int _narrowSideGripRun;
    private int _maxNarrowSideGripRun;
    private int _narrowFinalSideGrips;
    private bool _narrowSupportFinite = true;
    private float _narrowMinSupportLength = 1f;
    private float _narrowMaxSupportStep;
    private Vector3 _previousNarrowSupport;
    private bool _hasPreviousNarrowSupport;

    private int[] _gaitPreviousStepSerial = Array.Empty<int>();
    private int[] _gaitPreviousLandingSerial = Array.Empty<int>();
    private int[] _gaitPreviousEmergencySteps = Array.Empty<int>();
    private bool[] _gaitPreviousGripping = Array.Empty<bool>();
    private bool[] _gaitStepActive = Array.Empty<bool>();
    private float[] _gaitStepStartRelative = Array.Empty<float>();
    private int[] _gaitStepLandingSerial = Array.Empty<int>();
    private float[] _gaitStepPeakClearance = Array.Empty<float>();
    private int[] _gaitStepsPerLeg = Array.Empty<int>();
    private float[] _gaitForwardGainPerLeg = Array.Empty<float>();
    private int[] _gaitMicroStepsPerLeg = Array.Empty<int>();
    private float[] _gaitClearancePerLeg = Array.Empty<float>();
    private int[] _gaitEmergencyStepsPerLeg = Array.Empty<int>();
    private bool[] _gaitStanceActive = Array.Empty<bool>();
    private Vector3[] _gaitStanceStartRoot = Array.Empty<Vector3>();
    private float[] _gaitStanceAdvancePerLeg = Array.Empty<float>();
    private int[] _gaitStanceCountPerLeg = Array.Empty<int>();
    private float[] _gaitPlantedTrailPerLeg = Array.Empty<float>();
    private int[] _gaitPlantedSamplesPerLeg = Array.Empty<int>();
    private int[] _gaitSevereSamplesPerLeg = Array.Empty<int>();
    private int[] _gaitSevereRunPerLeg = Array.Empty<int>();
    private int[] _gaitSwingSamplesPerLeg = Array.Empty<int>();
    private int[] _gaitForwardSwingSamplesPerLeg = Array.Empty<int>();
    private int _gaitObservationTicks;
    private int _gaitCompletedSteps;
    private int _gaitMicroSteps;
    private int _gaitDirectRetargets;
    private int _gaitEmergencySteps;
    private float _gaitForwardGainSum;
    private float _gaitMinForwardGain = float.PositiveInfinity;
    private float _gaitPlantedTrailSum;
    private int _gaitPlantedSamples;
    private int _gaitSevereTrailSamples;
    private int _gaitMaxSevereTrailRun;

    // 平地急转专项：以同一腿对两根 RootPos 的外向轴定义真实局部 Side lane；
    // 同时用 SpiderLeg 的派生名义宽度排除“两侧一起贴身”的对称假绿。
    private int[] _turnStartStepSerial = Array.Empty<int>();
    private int[] _turnStartLandingSerial = Array.Empty<int>();
    private int[] _turnFootWrongRuns = Array.Empty<int>();
    private int[] _turnTargetWrongRuns = Array.Empty<int>();
    private bool[] _turnTargetSeen = Array.Empty<bool>();
    private bool[] _turnLegReplanted = Array.Empty<bool>();
    private Queue<float>[] _turnFootLaneWindows =
        Array.Empty<Queue<float>>();
    private float[] _turnLatestTargetLanes = Array.Empty<float>();
    private bool[] _turnLatestTargetSeen = Array.Empty<bool>();
    private bool[] _turnAlignedTargetSeen = Array.Empty<bool>();
    private int[] _turnBalanceStepSerial = Array.Empty<int>();
    private readonly List<TurnBalanceSample> _turnBalanceSamples = new();
    private int _turnBalanceWindow;
    private int _turnBalanceRun;
    private int _turnBalanceRecoveryTick = -1;
    private bool _turnStarted;
    private long _turnStartTick = -1;
    private Vector3 _turnStartPosition;
    private Vector3 _turnDirection = Vector3.Back;
    private int _turnObservationTicks;
    private float _turnMaxProgress;
    private float _turnFinalProgress;
    private float _turnMinFootLane = float.PositiveInfinity;
    private float _turnMinFrozenTargetLane = float.PositiveInfinity;
    private float _turnFinalMinFootLane = float.PositiveInfinity;
    private float _turnFinalMinTargetLane = float.PositiveInfinity;
    private int _turnMaxFootWrongRun;
    private int _turnMaxTargetWrongRun;
    private int _turnLastFootViolationTick = -1;
    private int _turnLastTargetViolationTick = -1;
    private int _turnLastBodyViolationTick = -1;
    private int _turnBodyAlignedRun;
    private int _turnMaxBodyAlignedRun;
    private float _turnFinalBodyAlignment;
    private float _turnFinalForwardAlignment;
    private int _turnMinGripping = int.MaxValue;
    private int _turnZeroGripRun;
    private int _turnMaxZeroGripRun;
    private int _turnGravityRun;
    private int _turnMaxGravityRun;
    private int _turnTargetsSeen;
    private int _turnLegsReplanted;
    private int _turnAllLegsReplantedTick = -1;
    private int _turnFinalOwnFeet;
    private int _turnFinalOwnTargets;
    private int _turnFinalOwnPairs;
    private float _turnMinPoleStepDot = 1f;
    private float _turnMaxLocalKneeStepRatio;
    private int _turnPoleFlipsAtStart;
    private float _turnMinSupportUp = 1f;

    private float _camYaw;
    private float _camPitch;
    private bool _cameraFlying;

    public override void _Ready()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        _breed = SpiderFactory.SmallSpider();
        if (!ParseArgs())
        {
            _fatal = true;
            GetTree().Quit(2);
            return;
        }

        _camera = GetNode<Camera3D>("Camera3D");
        _rayDebug = new RayDebugDraw(_terrain);
        _rayDebug.Build(this);
        _camYaw = _camera.Rotation.Y;
        _camPitch = _camera.Rotation.X;
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);
        _drag.Spring = DragSpring;
        _drag.Damping = DragDamping;
        _drag.MaxForce = DragMaxForce;

        SpawnSpider(_breed, _spawn);
        if (_perturb != 0f)
        {
            BodyChunk primary = _controller.Primary;
            primary.Pos += new Vector3(_perturb, 0f, 0f);
            primary.LastPos = primary.Pos;
        }

        if (_determinismTicks == 0)
        {
            SpiderBreedParams[] breeds = SpiderFactory.AllBreeds();
            string[] names = Array.ConvertAll(breeds, static breed => breed.Name);
            _breedUi = new BreedSelectorUI();
            _breedUi.Build(this, names, SelectBreed);
            _breedUi.SyncSelection(Array.FindIndex(breeds, candidate => candidate.Name == _breed.Name));
        }

        GD.Print($"[SPIDER-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"breed={_breed.Name} route={_route} determinism={(_determinismTicks > 0 ? "on" : "off")}");
    }

    private void SpawnSpider(SpiderBreedParams breed, Vector3 origin)
    {
        _breed = breed;
        _controller = SpiderFactory.CreateSpiderController(origin, breed);
        if (ConstraintIterations > 0)
        {
            _controller.Body.ConstraintIterations = ConstraintIterations;
        }
        _bodies.Clear();
        _bodies.Add(_controller.Body);
        _drag.Release();
        _renderer.Clear();
        _renderer.Build(this, _controller);
        _previousPoles.Clear();
        _sideRecoveryRuns.Clear();
        foreach (SpiderLeg leg in _controller.Legs)
        {
            _previousPoles.Add(leg, leg.BendPole);
            _sideRecoveryRuns.Add(leg, 0);
        }
        _gaitPreviousStepSerial = new int[_controller.Legs.Count];
        _gaitPreviousLandingSerial = new int[_controller.Legs.Count];
        _gaitPreviousEmergencySteps = new int[_controller.Legs.Count];
        _gaitPreviousGripping = new bool[_controller.Legs.Count];
        _gaitStepActive = new bool[_controller.Legs.Count];
        _gaitStepStartRelative = new float[_controller.Legs.Count];
        _gaitStepLandingSerial = new int[_controller.Legs.Count];
        _gaitStepPeakClearance = new float[_controller.Legs.Count];
        _gaitStepsPerLeg = new int[_controller.Legs.Count];
        _gaitForwardGainPerLeg = new float[_controller.Legs.Count];
        _gaitMicroStepsPerLeg = new int[_controller.Legs.Count];
        _gaitClearancePerLeg = new float[_controller.Legs.Count];
        _gaitEmergencyStepsPerLeg = new int[_controller.Legs.Count];
        _gaitStanceActive = new bool[_controller.Legs.Count];
        _gaitStanceStartRoot = new Vector3[_controller.Legs.Count];
        _gaitStanceAdvancePerLeg = new float[_controller.Legs.Count];
        _gaitStanceCountPerLeg = new int[_controller.Legs.Count];
        _gaitPlantedTrailPerLeg = new float[_controller.Legs.Count];
        _gaitPlantedSamplesPerLeg = new int[_controller.Legs.Count];
        _gaitSevereSamplesPerLeg = new int[_controller.Legs.Count];
        _gaitSevereRunPerLeg = new int[_controller.Legs.Count];
        _gaitSwingSamplesPerLeg = new int[_controller.Legs.Count];
        _gaitForwardSwingSamplesPerLeg = new int[_controller.Legs.Count];
        _turnStartStepSerial = new int[_controller.Legs.Count];
        _turnStartLandingSerial = new int[_controller.Legs.Count];
        _turnFootWrongRuns = new int[_controller.Legs.Count];
        _turnTargetWrongRuns = new int[_controller.Legs.Count];
        _turnTargetSeen = new bool[_controller.Legs.Count];
        _turnLegReplanted = new bool[_controller.Legs.Count];
        _turnBalanceWindow = Math.Max(16, 2 * _breed.GaitPhaseTicks);
        _turnFootLaneWindows =
            new Queue<float>[_controller.Legs.Count];
        _turnLatestTargetLanes = new float[_controller.Legs.Count];
        _turnLatestTargetSeen = new bool[_controller.Legs.Count];
        _turnAlignedTargetSeen = new bool[_controller.Legs.Count];
        _turnBalanceStepSerial = new int[_controller.Legs.Count];
        for (int i = 0; i < _turnFootLaneWindows.Length; i++)
        {
            _turnFootLaneWindows[i] =
                new Queue<float>(_turnBalanceWindow);
        }
        _gaitObservationTicks = 0;
        _gaitCompletedSteps = 0;
        _gaitMicroSteps = 0;
        _gaitDirectRetargets = 0;
        _gaitEmergencySteps = 0;
        _gaitForwardGainSum = 0f;
        _gaitMinForwardGain = float.PositiveInfinity;
        _gaitPlantedTrailSum = 0f;
        _gaitPlantedSamples = 0;
        _gaitSevereTrailSamples = 0;
        _gaitMaxSevereTrailRun = 0;
        _turnStarted = false;
        _turnStartTick = -1;
        _turnStartPosition = Vector3.Zero;
        _turnDirection = TurnDirection();
        _turnObservationTicks = 0;
        _turnMaxProgress = 0f;
        _turnFinalProgress = 0f;
        _turnMinFootLane = float.PositiveInfinity;
        _turnMinFrozenTargetLane = float.PositiveInfinity;
        _turnFinalMinFootLane = float.PositiveInfinity;
        _turnFinalMinTargetLane = float.PositiveInfinity;
        _turnMaxFootWrongRun = 0;
        _turnMaxTargetWrongRun = 0;
        _turnLastFootViolationTick = -1;
        _turnLastTargetViolationTick = -1;
        _turnLastBodyViolationTick = -1;
        _turnBodyAlignedRun = 0;
        _turnMaxBodyAlignedRun = 0;
        _turnFinalBodyAlignment = 0f;
        _turnFinalForwardAlignment = 0f;
        _turnMinGripping = int.MaxValue;
        _turnZeroGripRun = 0;
        _turnMaxZeroGripRun = 0;
        _turnGravityRun = 0;
        _turnMaxGravityRun = 0;
        _turnTargetsSeen = 0;
        _turnLegsReplanted = 0;
        _turnAllLegsReplantedTick = -1;
        _turnFinalOwnFeet = 0;
        _turnFinalOwnTargets = 0;
        _turnFinalOwnPairs = 0;
        _turnBalanceSamples.Clear();
        _turnBalanceRun = 0;
        _turnBalanceRecoveryTick = -1;
        _turnMinPoleStepDot = 1f;
        _turnMaxLocalKneeStepRatio = 0f;
        _turnPoleFlipsAtStart = 0;
        _turnMinSupportUp = 1f;
        for (int i = 0; i < _controller.Legs.Count; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            _gaitPreviousStepSerial[i] = leg.StepSerial;
            _gaitPreviousLandingSerial[i] = leg.LandingSerial;
            _gaitPreviousEmergencySteps[i] = leg.EmergencyStepCount;
            _gaitPreviousGripping[i] = leg.Gripping;
        }
        _phaseStartedAt = _tick;
        _phaseStartPosition = _controller.Primary.Pos;
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

        if (_determinismTicks > 0)
        {
            DriveRegressionRoute();
        }
        else
        {
            _drag.SampleInput(_camera, _bodies);
            _drag.ApplyDragForce();
            SampleInteractiveInput();
        }

        if (_yankTick >= 0 && _tick == _yankTick)
        {
            _controller.Launch(new Vector3(0.12f, 0.38f, 0.08f));
        }

        _controller.Tick(new TickContext(_gravityPerTick, _rayDebug, _tick));

        if (_determinismTicks <= 0)
        {
            return;
        }

        RecordMetrics();
        _hasher.FoldBody(_controller.Body);
        _hasher.FoldSpiderBodyState(_controller.Body);
        _hasher.FoldSpiderLegs(_controller.Legs);
        _hasher.FoldSpiderControllerState(_controller);
        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[SPIDER-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
        if (_tick >= _determinismTicks)
        {
            GetTree().Quit(DumpFinalState());
        }
    }

    private void SampleInteractiveInput()
    {
        if (WantCameraFly)
        {
            _controller.MoveDir = Vector3.Zero;
            _controller.RunSpeed = 0f;
            return;
        }

        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction.Y += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction.Y -= 1f;

        if (direction != Vector3.Zero)
        {
            _controller.MoveTarget = null;
            _controller.MoveDir = direction.Normalized();
            _controller.RunSpeed = 1f;
            return;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Right)
            && Input.IsPhysicalKeyPressed(Key.Shift))
        {
            Vector2 mouse = _camera.GetViewport().GetMousePosition();
            Vector3 from = _camera.ProjectRayOrigin(mouse);
            Vector3 ray = _camera.ProjectRayNormal(mouse);
            if (_rayDebug.Raycast(from, from + ray * 100f, out TerrainHit hit))
            {
                _controller.MoveTarget = hit.Point;
                // AtMoveTarget 属于上一个 core tick；新目标写入后必须让下一 tick 重新判定，
                // 不能被旧目标刚到达的残留值在同一帧清掉。
                _controller.RunSpeed = 1f;
                return;
            }
        }

        if (_controller.MoveTarget is not null && !_controller.AtMoveTarget)
        {
            _controller.RunSpeed = 1f;
            return;
        }

        if (_controller.AtMoveTarget)
        {
            _controller.MoveTarget = null;
        }
        _controller.MoveDir = Vector3.Zero;
        _controller.RunSpeed = 0f;
    }

    private void DriveRegressionRoute()
    {
        switch (_route)
        {
            case Route.Course:
                DriveCourse();
                break;
            case Route.Gait:
                SetDrive(Vector3.Left, 1f);
                break;
            case Route.NarrowWall:
                DriveNarrowWall();
                break;
            case Route.WallL:
                DriveWallL();
                break;
            case Route.WallCeiling:
                DriveWallCeiling();
                break;
            case Route.Turn:
                DriveTurn();
                break;
            default:
                SetDrive(Vector3.Zero, 0f);
                break;
        }
    }

    private void DriveCourse()
    {
        Vector3 target = CourseRoute[_courseWaypoint];
        Vector3 toTarget = target - _controller.Primary.Pos;
        toTarget.Y = 0f;
        if (toTarget.Length() < 0.45f)
        {
            _courseWaypoint = (_courseWaypoint + 1) % CourseRoute.Length;
            _waypointsReached++;
            target = CourseRoute[_courseWaypoint];
            toTarget = target - _controller.Primary.Pos;
            toTarget.Y = 0f;
        }
        SetDrive(toTarget, 1f);
    }

    /// <summary>
    /// 在远离其它白盒几何的平地先沿 -X 稳定行走 120 tick，再把输入一次性切到
    /// 左转(+Z)、右转(-Z)或掉头(+X)。达到 3m 转后推进即停驶，余下 tick 用来证明
    /// 足端和冻结 AEP 已连续回到各自 RootPos 所定义的本侧，而不是只偶然过线一次。
    /// </summary>
    private void DriveTurn()
    {
        if (_routePhase == 0)
        {
            if (_tick <= TurnLeadTicks)
            {
                SetDrive(Vector3.Left, 0.55f);
                return;
            }

            BeginTurn();
            AdvancePhase();
        }

        if (_routePhase == 1)
        {
            if (_turnMaxProgress >= TurnDriveDistance
                || _tick - _turnStartTick >= TurnDriveBudgetTicks)
            {
                AdvancePhase();
                SetDrive(Vector3.Zero, 0f);
                return;
            }
            SetDrive(_turnDirection, 1f);
            return;
        }

        SetDrive(Vector3.Zero, 0f);
    }

    private void BeginTurn()
    {
        _turnStarted = true;
        _turnStartTick = _tick;
        _turnStartPosition = _controller.Primary.Pos;
        _turnDirection = TurnDirection();
        _turnPoleFlipsAtStart = _poleFlipViolations;
        for (int i = 0; i < _controller.Legs.Count; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            _turnStartStepSerial[i] = leg.StepSerial;
            _turnStartLandingSerial[i] = leg.LandingSerial;
            _turnBalanceStepSerial[i] = leg.StepSerial;
            _turnFootLaneWindows[i].Clear();
            _turnLatestTargetSeen[i] = false;
            _turnAlignedTargetSeen[i] = false;
        }
    }

    private Vector3 TurnDirection()
    {
        return _turnKind switch
        {
            TurnKind.Left => Vector3.Back,
            TurnKind.Right => Vector3.Forward,
            TurnKind.Around => Vector3.Right,
            _ => Vector3.Back,
        };
    }

    /// <summary>
    /// 从 +Z 方向贴上 x 方向仅 0.36m 宽的墙端面，爬升 1.5m 后停住观察。
    /// 路线只提供连续移动意图；腿是否越过两条凸棱抱住 ±X 侧面完全由抓点搜索涌现。
    /// </summary>
    private void DriveNarrowWall()
    {
        Vector3 p = _controller.Primary.Pos;
        Vector3 n = _controller.SupportNormal;
        switch (_routePhase)
        {
            case 0:
                SetDrive(Vector3.Forward, 1f);
                if (n.Z > 0.45f && p.Z < -5.55f)
                {
                    AdvancePhase();
                }
                break;
            case 1:
                SetDrive(new Vector3(0f, 1f, -0.16f), 1f);
                if (p.Y > 2.15f)
                {
                    AdvancePhase();
                }
                break;
            default:
                SetDrive(Vector3.Zero, 0f);
                break;
        }
    }

    /// <summary>
    /// 先贴上 x=6 的竖墙并爬离地面，再沿 +Z 到垂直的 z=6 墙；
    /// 支撑法线由 -X 旋向 -Z 后沿第二面 -X 行走。
    /// </summary>
    private void DriveWallL()
    {
        Vector3 p = _controller.Primary.Pos;
        Vector3 n = _controller.SupportNormal;
        switch (_routePhase)
        {
            case 0:
                SetDrive(Vector3.Right, 1f);
                if (n.X < -0.55f)
                {
                    AdvancePhase();
                }
                break;
            case 1:
                SetDrive(new Vector3(0.15f, 1f, 0f), 1f);
                if (p.Y > 1.45f)
                {
                    AdvancePhase();
                }
                break;
            case 2:
                SetDrive(new Vector3(0.12f, 0f, 1f), 1f);
                if (n.Z < -0.45f)
                {
                    AdvancePhase();
                }
                break;
            default:
                _postTransitionProgress = Mathf.Max(_postTransitionProgress,
                    _phaseStartPosition.X - p.X);
                SetDrive(_postTransitionProgress < 0.9f
                    ? new Vector3(-1f, 0f, 0.12f)
                    : Vector3.Zero, _postTransitionProgress < 0.9f ? 1f : 0f);
                break;
        }
    }

    /// <summary>
    /// 贴上 x=0 的墙，沿墙爬到 y=6 的内角，再沿天花板底面 +X 行走。
    /// 驱动只给连续方向；是否抓到墙/顶面以及何时换面由腿的真实射线结果决定。
    /// </summary>
    private void DriveWallCeiling()
    {
        Vector3 p = _controller.Primary.Pos;
        Vector3 n = _controller.SupportNormal;
        switch (_routePhase)
        {
            case 0:
                SetDrive(Vector3.Left, 1f);
                if (n.X > 0.55f)
                {
                    AdvancePhase();
                }
                break;
            case 1:
                SetDrive(new Vector3(-0.12f, 1f, 0f), 1f);
                if (p.Y > 5.15f)
                {
                    AdvancePhase();
                }
                break;
            case 2:
                SetDrive(new Vector3(0.55f, 0.85f, 0f), 1f);
                if (n.Y < -0.45f)
                {
                    AdvancePhase();
                }
                break;
            default:
                _postTransitionProgress = Mathf.Max(_postTransitionProgress,
                    p.X - _phaseStartPosition.X);
                SetDrive(_postTransitionProgress < 1.1f
                    ? Vector3.Right
                    : Vector3.Zero, _postTransitionProgress < 1.1f ? 1f : 0f);
                break;
        }
    }

    private void SetDrive(Vector3 direction, float speed)
    {
        _controller.MoveTarget = null;
        _controller.MoveDir = direction.LengthSquared() < 1e-10f
            ? Vector3.Zero
            : direction.Normalized();
        _controller.RunSpeed = speed;
    }

    private void AdvancePhase()
    {
        _routePhase++;
        _phaseStartedAt = _tick;
        _phaseStartPosition = _controller.Primary.Pos;
    }

    private void RecordMetrics()
    {
        Vector3 support = _controller.SupportNormal;
        Vector3 primary = _controller.Primary.Pos;
        int gripping = _controller.LegsGripping;
        if (gripping > 0)
        {
            _sawGrip = true;
            _zeroGripRun = 0;
        }
        else
        {
            _zeroGripRun++;
            _maxZeroGripRun = Math.Max(_maxZeroGripRun, _zeroGripRun);
        }
        bool stableGrip = gripping >= _controller.MinGroundedLegs
            && !_controller.ApplyGravity;
        bool upGrip = false;
        bool ceilingGrip = false;
        bool positiveXGrip = false;
        bool negativeXGrip = false;
        bool positiveZGrip = false;
        bool negativeZGrip = false;
        bool rampGrip = false;
        bool upContact = false;
        bool ceilingContact = false;
        bool positiveXContact = false;
        bool negativeXContact = false;
        bool negativeZContact = false;
        int positiveXGripCount = 0;
        int negativeXGripCount = 0;

        float constraint = _controller.Body.CurrentMaxDeviation();
        _maxConstraintDeviation = Mathf.Max(_maxConstraintDeviation, constraint);
        _deepConstraintRun = constraint > DeepConstraintThreshold ? _deepConstraintRun + 1 : 0;
        _maxDeepConstraintRun = Math.Max(_maxDeepConstraintRun, _deepConstraintRun);

        for (int chunkIndex = 0; chunkIndex < _controller.Body.Chunks.Count; chunkIndex++)
        {
            BodyChunk chunk = _controller.Body.Chunks[chunkIndex];
            _nonFinite |= !chunk.Pos.IsFinite() || !chunk.Vel.IsFinite();
            float bodyStep = (chunk.Pos - chunk.LastPos).Length();
            if (bodyStep > _maxBodyStep)
            {
                _maxBodyStep = bodyStep;
                _maxBodyStepTick = _tick;
                _maxBodyStepChunk = chunkIndex;
            }
            if (_tick > 60)
            {
                _maxSettledBodyStep = Mathf.Max(_maxSettledBodyStep, bodyStep);
            }
            if (_terrain.SpherePenetration(chunk.Pos, chunk.TerrainRadius,
                    out _, out float depth))
            {
                if (depth > _maxPenetration)
                {
                    _maxPenetration = depth;
                    _maxPenetrationTick = _tick;
                    _maxPenetrationPart = $"chunk{chunkIndex}";
                }
            }
        }

        foreach (SpiderLeg leg in _controller.Legs)
        {
            _nonFinite |= !leg.Pos.IsFinite() || !leg.Vel.IsFinite()
                || !leg.KneePos.IsFinite() || !leg.BendPole.IsFinite()
                || !leg.RootPos.IsFinite();
            float kneeStep = (leg.KneePos - leg.LastKneePos).Length();
            _maxKneeStep = Mathf.Max(_maxKneeStep, kneeStep);
            _maxKneeStepRatio = Mathf.Max(_maxKneeStepRatio,
                kneeStep / (leg.UpperLength + leg.LowerLength));
            Vector3 localKnee = leg.KneePos - leg.RootPos;
            Vector3 lastLocalKnee = leg.LastKneePos - leg.LastRootPos;
            float localKneeStepRatio = (localKnee - lastLocalKnee).Length()
                / (leg.UpperLength + leg.LowerLength);
            _maxLocalKneeStepRatio = Mathf.Max(_maxLocalKneeStepRatio,
                localKneeStepRatio);
            if (_turnStarted)
            {
                _turnMaxLocalKneeStepRatio = Mathf.Max(
                    _turnMaxLocalKneeStepRatio, localKneeStepRatio);
            }
            float upperError = Mathf.Abs((leg.KneePos - leg.RootPos).Length()
                - leg.UpperLength);
            float lowerError = Mathf.Abs((leg.Pos - leg.KneePos).Length()
                - leg.LowerLength);
            _maxIkError = Mathf.Max(_maxIkError, Mathf.Max(upperError, lowerError));
            _maxReachExcess = Mathf.Max(_maxReachExcess,
                (leg.Pos - leg.RootPos).Length() - leg.MaxReach);
            _maxReachDeficit = Mathf.Max(_maxReachDeficit,
                leg.MinimumReach - (leg.Pos - leg.RootPos).Length());

            Vector3 axis = leg.Pos - leg.RootPos;
            if (axis.LengthSquared() > 1e-8f)
            {
                Vector3 projected = leg.RootPos
                    + axis * ((leg.KneePos - leg.RootPos).Dot(axis) / axis.LengthSquared());
                Vector3 bend = leg.KneePos - projected;
                if (bend.LengthSquared() > 1e-8f && bend.Dot(leg.BendPole) < -1e-5f)
                {
                    _poleFlipViolations++;
                }
            }
            if (_previousPoles.TryGetValue(leg, out Vector3 previous)
                && previous.LengthSquared() > 1e-8f
                && leg.BendPole.LengthSquared() > 1e-8f)
            {
                float poleStepDot =
                    previous.Normalized().Dot(leg.BendPole.Normalized());
                if (_turnStarted)
                {
                    _turnMinPoleStepDot = Mathf.Min(
                        _turnMinPoleStepDot, poleStepDot);
                }
                if (poleStepDot < -0.2f)
                {
                    _poleFlipViolations++;
                }
            }
            _previousPoles[leg] = leg.BendPole;
            if (leg.Pair is { } pair)
            {
                Vector3 outward = leg.RootPos - pair.RootPos;
                int run = 0;
                if (outward.LengthSquared() > 1e-8f
                    && leg.BendPole.Dot(outward.Normalized()) <= 0.02f)
                {
                    run = _sideRecoveryRuns[leg] + 1;
                }
                _sideRecoveryRuns[leg] = run;
                _maxSideRecoveryRun = Math.Max(_maxSideRecoveryRun, run);
            }

            if (_terrain.SpherePenetration(leg.Pos, leg.Radius,
                    out _, out float footDepth))
            {
                if (footDepth > _maxPenetration)
                {
                    _maxPenetration = footDepth;
                    _maxPenetrationTick = _tick;
                    _maxPenetrationPart = $"leg{leg.PairIndex}/{leg.Side}";
                }
            }
            if (leg.Gripping)
            {
                Vector3 grip = leg.GripNormal;
                upGrip |= grip.Y > 0.55f;
                ceilingGrip |= grip.Y < -0.45f;
                positiveXGrip |= grip.X > 0.55f;
                negativeXGrip |= grip.X < -0.55f;
                positiveZGrip |= grip.Z > 0.55f;
                negativeZGrip |= grip.Z < -0.45f;
                if (grip.X > 0.55f) positiveXGripCount++;
                if (grip.X < -0.55f) negativeXGripCount++;
                rampGrip |= grip.Y > 0.65f && grip.Y < 0.99f
                    && Mathf.Abs(grip.X) > 0.08f;
            }
            if (leg.GripCounter > 0)
            {
                Vector3 contact = leg.GripNormal;
                upContact |= contact.Y > 0.55f;
                ceilingContact |= contact.Y < -0.45f;
                positiveXContact |= contact.X > 0.55f;
                negativeXContact |= contact.X < -0.55f;
                negativeZContact |= contact.Z < -0.45f;
            }
        }

        RecordGaitMetrics(stableGrip, support);
        RecordTurnMetrics(support);

        if (stableGrip && upGrip && support.Y > 0.75f && primary.Y < 1.2f)
        {
            _floorSeen = true;
        }
        if (stableGrip && rampGrip && support.Y > 0.65f && support.Y < 0.99f
            && Mathf.Abs(support.X) > 0.08f && primary.X > 1f)
        {
            _rampSeen = true;
        }

        if (_route == Route.NarrowWall)
        {
            bool inNarrowRegion = Mathf.Abs(primary.X + 9f) < 0.8f
                && Mathf.Abs(primary.Z + 6f) < 0.8f;
            bool mounted = inNarrowRegion && stableGrip
                && positiveZGrip && support.Z > 0.35f && primary.Y > 0.65f;
            if (mounted && !_narrowWallSeen)
            {
                _narrowWallStartHeight = primary.Y;
            }
            _narrowWallSeen |= mounted;
            if (_narrowWallSeen)
            {
                _maxNarrowWallRise = Mathf.Max(
                    _maxNarrowWallRise, primary.Y - _narrowWallStartHeight);
            }

            // phase 2 停驶后给足端 20 tick 收敛，再观察一个不受换步相位干扰的稳定窗口。
            bool observing = _routePhase >= 2 && _tick - _phaseStartedAt >= 20;
            if (observing)
            {
                _narrowObservationTicks++;
                bool majority = gripping * 2 > _controller.Legs.Count;
                _narrowMajorityRun = majority ? _narrowMajorityRun + 1 : 0;
                _maxNarrowMajorityRun = Math.Max(
                    _maxNarrowMajorityRun, _narrowMajorityRun);

                bool bothSides = positiveXGrip && negativeXGrip;
                _narrowBothSidesRun = bothSides ? _narrowBothSidesRun + 1 : 0;
                _maxNarrowBothSidesRun = Math.Max(
                    _maxNarrowBothSidesRun, _narrowBothSidesRun);
                int sideGrips = positiveXGripCount + negativeXGripCount;
                _maxNarrowSideGrips = Math.Max(_maxNarrowSideGrips, sideGrips);
                _narrowSideGripRun = sideGrips >= 4 ? _narrowSideGripRun + 1 : 0;
                _maxNarrowSideGripRun = Math.Max(
                    _maxNarrowSideGripRun, _narrowSideGripRun);
                _narrowFinalSideGrips = sideGrips;

                float supportLength = support.Length();
                bool finiteSupport = support.IsFinite() && float.IsFinite(supportLength);
                _narrowSupportFinite &= finiteSupport;
                if (finiteSupport)
                {
                    _narrowMinSupportLength = Mathf.Min(
                        _narrowMinSupportLength, supportLength);
                    if (_hasPreviousNarrowSupport)
                    {
                        _narrowMaxSupportStep = Mathf.Max(
                            _narrowMaxSupportStep,
                            (support - _previousNarrowSupport).Length());
                    }
                    _previousNarrowSupport = support;
                    _hasPreviousNarrowSupport = true;
                }
            }
        }
        else if (_route == Route.Course)
        {
            bool wall = stableGrip && positiveXGrip
                && support.X > 0.55f && primary.X < -5.2f;
            if (wall && !_wallSeen)
            {
                _wallStartHeight = primary.Y;
            }
            _wallSeen |= wall;
            if (_wallSeen)
            {
                _maxWallRise = Mathf.Max(_maxWallRise, primary.Y - _wallStartHeight);
                if (_wallTransitionStart < 0 && primary.Y > 2.35f && upContact)
                {
                    // 第一条腿真实命中新面起算；从墙脚爬完整面不混入换面预算。
                    _wallTransitionStart = _tick;
                }
                _cornerGripOverlapSeen |= stableGrip
                    && positiveXContact && upContact
                    && primary.X < -5.2f && primary.Y < 1.2f;
                _mixedSupportSeen |= stableGrip
                    && positiveXContact && upContact
                    && support.X > 0.18f && support.Y > 0.18f
                    && primary.X < -5.2f && primary.Y < 1.2f;
                bool top = stableGrip && upGrip
                    && support.Y > 0.55f && primary.Y > 2.55f;
                if (top)
                {
                    _outerCornerSeen = true;
                    if (_wallTransitionTicks < 0 && _wallTransitionStart >= 0)
                    {
                        _wallTransitionTicks = _tick - _wallTransitionStart;
                    }
                }
            }
        }
        else if (_route == Route.WallL)
        {
            _firstLWallSeen |= stableGrip && negativeXGrip
                && support.X < -0.55f && primary.Y > 0.8f;
            if (_firstLWallSeen && _wallTransitionStart < 0 && negativeZContact)
            {
                _wallTransitionStart = _tick;
            }
            _cornerGripOverlapSeen |= stableGrip
                && negativeXContact && negativeZContact;
            _mixedSupportSeen |= stableGrip
                && negativeXContact && negativeZContact
                && support.X < -0.15f && support.Z < -0.15f;
            _secondLWallSeen |= stableGrip && negativeZGrip
                && support.Z < -0.45f && primary.Y > 0.8f;
            if (_secondLWallSeen && _wallTransitionTicks < 0)
            {
                _wallTransitionTicks = _wallTransitionStart < 0
                    ? 0
                    : _tick - _wallTransitionStart;
            }
        }
        else if (_route == Route.WallCeiling)
        {
            bool wall = stableGrip && positiveXGrip
                && support.X > 0.55f && primary.Y > 0.8f;
            if (wall && !_wallSeen)
            {
                _wallStartHeight = primary.Y;
            }
            _wallSeen |= wall;
            if (_wallSeen)
            {
                _maxWallRise = Mathf.Max(_maxWallRise, primary.Y - _wallStartHeight);
            }
            _ceilingSeen |= stableGrip && ceilingGrip
                && support.Y < -0.45f && primary.Y > 5f;
            _cornerGripOverlapSeen |= stableGrip
                && positiveXContact && ceilingContact && primary.Y > 5f;
            _mixedSupportSeen |= stableGrip
                && positiveXContact && ceilingContact
                && support.X > 0.15f && support.Y < -0.15f
                && primary.Y > 5f;
            if (_wallSeen && _wallTransitionStart < 0 && ceilingContact)
            {
                _wallTransitionStart = _tick;
            }
            if (_ceilingSeen && _wallTransitionTicks < 0
                && _wallTransitionStart >= 0)
            {
                _wallTransitionTicks = _tick - _wallTransitionStart;
            }
        }
    }

    /// <summary>
    /// 在无障碍地面直行中统计完整主动换步。只采样已经抓稳、朝向与速度都稳定的窗口，
    /// 排除出生收敛和正常抬腿瞬态；指标直接描述足端是否长期落后、每条腿是否都参与换步。
    /// </summary>
    private void RecordGaitMetrics(bool stableGrip, Vector3 support)
    {
        if (_route != Route.Gait)
        {
            return;
        }

        Vector3 forward = Vector3.Left;
        bool observing = _tick >= 80
            && stableGrip
            && support.Y > 0.95f
            && _controller.Forward.Dot(forward) > 0.95f
            && _controller.Primary.Vel.Dot(forward) > 0.01f;
        if (observing)
        {
            _gaitObservationTicks++;
        }

        for (int i = 0; i < _controller.Legs.Count; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            float reach = Mathf.Max(leg.MaxReach, 1e-5f);
            float relative = (leg.Pos - leg.RootPos).Dot(forward) / reach;
            bool stepEvent = leg.StepSerial != _gaitPreviousStepSerial[i];
            bool landingEvent = leg.LandingSerial != _gaitPreviousLandingSerial[i];

            if (observing && stepEvent)
            {
                if (_gaitStanceActive[i])
                {
                    _gaitStanceAdvancePerLeg[i] +=
                        (leg.RootPos - _gaitStanceStartRoot[i]).Dot(forward) / reach;
                    _gaitStanceCountPerLeg[i]++;
                    _gaitStanceActive[i] = false;
                }
                _gaitStepActive[i] = true;
                _gaitStepStartRelative[i] = relative;
                _gaitStepLandingSerial[i] = leg.LandingSerial;
                _gaitStepPeakClearance[i] = Mathf.Max(
                    0f, (leg.Pos.Y - leg.Radius) / reach);
            }
            if (observing && landingEvent
                && _gaitPreviousGripping[i] && !stepEvent && !_gaitStepActive[i])
            {
                _gaitDirectRetargets++;
            }
            if (observing)
            {
                int emergencyDelta =
                    leg.EmergencyStepCount - _gaitPreviousEmergencySteps[i];
                if (emergencyDelta > 0)
                {
                    _gaitEmergencySteps += emergencyDelta;
                    _gaitEmergencyStepsPerLeg[i] += emergencyDelta;
                }
            }
            if (_gaitStepActive[i])
            {
                _gaitStepPeakClearance[i] = Mathf.Max(
                    _gaitStepPeakClearance[i],
                    Mathf.Max(0f, (leg.Pos.Y - leg.Radius) / reach));
            }
            if (_gaitStepActive[i] && leg.Gripping
                && leg.LandingSerial != _gaitStepLandingSerial[i])
            {
                if (observing)
                {
                    float gain = relative - _gaitStepStartRelative[i];
                    _gaitForwardGainSum += gain;
                    _gaitMinForwardGain = Mathf.Min(_gaitMinForwardGain, gain);
                    _gaitCompletedSteps++;
                    _gaitStepsPerLeg[i]++;
                    _gaitForwardGainPerLeg[i] += gain;
                    _gaitClearancePerLeg[i] += _gaitStepPeakClearance[i];
                    if (gain < 0.12f)
                    {
                        _gaitMicroSteps++;
                        _gaitMicroStepsPerLeg[i]++;
                    }
                    _gaitStanceActive[i] = true;
                    _gaitStanceStartRoot[i] = leg.RootPos;
                }
                _gaitStepActive[i] = false;
            }

            if (observing && !leg.ReachingForTerrain)
            {
                _gaitSwingSamplesPerLeg[i]++;
                if ((leg.HuntPos - leg.Pos).Dot(forward) > 0f)
                {
                    _gaitForwardSwingSamplesPerLeg[i]++;
                }
            }

            if (observing && leg.Gripping)
            {
                float trail = -relative;
                _gaitPlantedTrailSum += trail;
                _gaitPlantedSamples++;
                _gaitPlantedTrailPerLeg[i] += trail;
                _gaitPlantedSamplesPerLeg[i]++;
                if (trail > 0.35f)
                {
                    _gaitSevereTrailSamples++;
                    _gaitSevereSamplesPerLeg[i]++;
                    _gaitSevereRunPerLeg[i]++;
                    _gaitMaxSevereTrailRun = Math.Max(
                        _gaitMaxSevereTrailRun, _gaitSevereRunPerLeg[i]);
                }
                else
                {
                    _gaitSevereRunPerLeg[i] = 0;
                }
            }
            else
            {
                _gaitSevereRunPerLeg[i] = 0;
            }
            _gaitPreviousStepSerial[i] = leg.StepSerial;
            _gaitPreviousLandingSerial[i] = leg.LandingSerial;
            _gaitPreviousEmergencySteps[i] = leg.EmergencyStepCount;
            _gaitPreviousGripping[i] = leg.Gripping;
        }
    }

    private void RecordTurnMetrics(Vector3 support)
    {
        if (_route != Route.Turn || !_turnStarted)
        {
            return;
        }

        int elapsed = (int)(_tick - _turnStartTick);
        _turnObservationTicks++;
        float progress =
            (_controller.Primary.Pos - _turnStartPosition).Dot(_turnDirection);
        _turnFinalProgress = progress;
        _turnMaxProgress = Mathf.Max(_turnMaxProgress, progress);
        _turnMinSupportUp = Mathf.Min(_turnMinSupportUp, support.Dot(Vector3.Up));

        Vector3 bodyAxis = _controller.Primary.Pos - _controller.Rear.Pos;
        bodyAxis -= Vector3.Up * bodyAxis.Dot(Vector3.Up);
        _turnFinalBodyAlignment = bodyAxis.LengthSquared() > 1e-8f
            ? bodyAxis.Normalized().Dot(_turnDirection)
            : -1f;
        _turnFinalForwardAlignment = _controller.Forward.Dot(_turnDirection);
        bool bodyAligned = _turnFinalBodyAlignment >= 0.88f
            && _turnFinalForwardAlignment >= 0.92f;
        if (bodyAligned)
        {
            _turnBodyAlignedRun++;
            _turnMaxBodyAlignedRun = Math.Max(
                _turnMaxBodyAlignedRun, _turnBodyAlignedRun);
        }
        else
        {
            _turnBodyAlignedRun = 0;
            _turnLastBodyViolationTick = elapsed;
        }

        int gripping = _controller.LegsGripping;
        _turnMinGripping = Math.Min(_turnMinGripping, gripping);
        _turnZeroGripRun = gripping == 0 ? _turnZeroGripRun + 1 : 0;
        _turnMaxZeroGripRun = Math.Max(_turnMaxZeroGripRun, _turnZeroGripRun);
        _turnGravityRun = _controller.ApplyGravity ? _turnGravityRun + 1 : 0;
        _turnMaxGravityRun = Math.Max(_turnMaxGravityRun, _turnGravityRun);

        _turnFinalOwnFeet = 0;
        _turnFinalOwnTargets = 0;
        _turnFinalOwnPairs = 0;
        _turnFinalMinFootLane = float.PositiveInfinity;
        _turnFinalMinTargetLane = float.PositiveInfinity;

        for (int i = 0; i < _controller.Legs.Count; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            bool hasFootLane = TrySideLane(leg, leg.Pos, out float footLane);
            if (hasFootLane)
            {
                _turnMinFootLane = Mathf.Min(_turnMinFootLane, footLane);
                _turnFinalMinFootLane = Mathf.Min(
                    _turnFinalMinFootLane, footLane);
            }
            bool footOwn = hasFootLane
                && footLane >= -TurnLaneToleranceRatio;
            if (footOwn)
            {
                _turnFinalOwnFeet++;
                _turnFootWrongRuns[i] = 0;
            }
            else
            {
                _turnFootWrongRuns[i]++;
                _turnMaxFootWrongRun = Math.Max(
                    _turnMaxFootWrongRun, _turnFootWrongRuns[i]);
                _turnLastFootViolationTick = elapsed;
            }

            if (hasFootLane)
            {
                Queue<float> window = _turnFootLaneWindows[i];
                window.Enqueue(footLane);
                while (window.Count > _turnBalanceWindow)
                {
                    window.Dequeue();
                }
            }
            else
            {
                _turnFootLaneWindows[i].Clear();
            }

            if (leg.StepSerial > _turnBalanceStepSerial[i])
            {
                if (leg.HasFrozenSwingTarget
                    && TrySideLane(
                        leg, leg.SwingLandingGoal, out float plannedLane))
                {
                    _turnLatestTargetLanes[i] = plannedLane;
                    _turnLatestTargetSeen[i] = true;
                    if (bodyAligned)
                    {
                        _turnAlignedTargetSeen[i] = true;
                    }
                }
                _turnBalanceStepSerial[i] = leg.StepSerial;
            }

            bool targetOwn = true;
            if (leg.HasFrozenSwingTarget)
            {
                if (!_turnTargetSeen[i])
                {
                    _turnTargetSeen[i] = true;
                    _turnTargetsSeen++;
                }
                Vector3 targetCenter =
                    leg.SwingLandingGoal + Vector3.Up * leg.Radius;
                bool hasTargetLane =
                    TrySideLane(leg, targetCenter, out float targetLane);
                if (hasTargetLane)
                {
                    _turnMinFrozenTargetLane = Mathf.Min(
                        _turnMinFrozenTargetLane, targetLane);
                    _turnFinalMinTargetLane = Mathf.Min(
                        _turnFinalMinTargetLane, targetLane);
                }
                targetOwn = hasTargetLane
                    && targetLane >= -TurnLaneToleranceRatio;
            }
            else if (_turnLatestTargetSeen[i])
            {
                float targetLane = _turnLatestTargetLanes[i];
                _turnFinalMinTargetLane = Mathf.Min(
                    _turnFinalMinTargetLane, targetLane);
                targetOwn = targetLane >= -TurnLaneToleranceRatio;
            }
            if (targetOwn)
            {
                _turnFinalOwnTargets++;
                _turnTargetWrongRuns[i] = 0;
            }
            else
            {
                _turnTargetWrongRuns[i]++;
                _turnMaxTargetWrongRun = Math.Max(
                    _turnMaxTargetWrongRun, _turnTargetWrongRuns[i]);
                _turnLastTargetViolationTick = elapsed;
            }

            if (!_turnLegReplanted[i]
                && leg.StepSerial > _turnStartStepSerial[i]
                && leg.LandingSerial > _turnStartLandingSerial[i]
                && leg.Gripping && footOwn)
            {
                _turnLegReplanted[i] = true;
                _turnLegsReplanted++;
                if (_turnLegsReplanted == _controller.Legs.Count)
                {
                    _turnAllLegsReplantedTick = elapsed;
                }
            }
        }

        foreach (SpiderLeg leg in _controller.Legs)
        {
            if (leg.Side >= 0 || leg.Pair is not { } pair)
            {
                continue;
            }
            bool leftOwn = TrySideLane(leg, leg.Pos, out float leftLane)
                && leftLane >= -TurnLaneToleranceRatio;
            bool rightOwn = TrySideLane(pair, pair.Pos, out float rightLane)
                && rightLane >= -TurnLaneToleranceRatio;
            if (leftOwn && rightOwn)
            {
                _turnFinalOwnPairs++;
            }
        }

        if (TryMeasureTurnBalance(elapsed, out TurnBalanceSample balance))
        {
            _turnBalanceSamples.Add(balance);
            bool balanced = bodyAligned
                && balance.SideGap <= 0.10f
                && balance.PairMeanGap <= 0.12f
                && balance.PairMaxGap <= 0.15f
                && balance.TargetSideGap <= 0.10f
                && balance.TargetPairMaxGap <= 0.15f
                && balance.MinPairRatio >= 0.80f
                && balance.MinFootNominalRatio >= 0.80f
                && balance.MinTargetNominalRatio >= 0.80f;
            _turnBalanceRun = balanced ? _turnBalanceRun + 1 : 0;
            if (_turnBalanceRecoveryTick < 0 && _turnBalanceRun >= 20)
            {
                _turnBalanceRecoveryTick = elapsed - 19;
            }
        }
        else
        {
            _turnBalanceRun = 0;
        }
    }

    private static bool TrySideLane(
        SpiderLeg leg,
        Vector3 point,
        out float lane)
    {
        lane = float.NegativeInfinity;
        if (leg.Pair is not { } pair)
        {
            return false;
        }
        Vector3 pairCenter = (leg.RootPos + pair.RootPos) * 0.5f;
        Vector3 outward = leg.RootPos - pairCenter;
        if (outward.LengthSquared() <= 1e-8f)
        {
            return false;
        }
        float reach = Mathf.Max(leg.MaxReach, 1e-5f);
        lane = (point - leg.RootPos).Dot(outward.Normalized()) / reach;
        return float.IsFinite(lane);
    }

    private bool TryMeasureTurnBalance(
        int elapsed,
        out TurnBalanceSample sample)
    {
        sample = default;
        int legCount = _controller.Legs.Count;
        var footMeans = new float[legCount];
        float leftFoot = 0f;
        float rightFoot = 0f;
        float leftTarget = 0f;
        float rightTarget = 0f;
        int leftCount = 0;
        int rightCount = 0;
        float minFootNominalRatio = float.PositiveInfinity;
        float minTargetNominalRatio = float.PositiveInfinity;
        for (int i = 0; i < legCount; i++)
        {
            Queue<float> window = _turnFootLaneWindows[i];
            if (window.Count < _turnBalanceWindow
                || !_turnLatestTargetSeen[i]
                || !_turnAlignedTargetSeen[i])
            {
                return false;
            }
            float sum = 0f;
            foreach (float value in window)
            {
                sum += value;
            }
            footMeans[i] = sum / window.Count;
            float nominal = Mathf.Max(
                _controller.Legs[i].NominalLateralReachRatio, 1e-5f);
            minFootNominalRatio = Mathf.Min(
                minFootNominalRatio, footMeans[i] / nominal);
            minTargetNominalRatio = Mathf.Min(
                minTargetNominalRatio,
                _turnLatestTargetLanes[i] / nominal);
            if (_controller.Legs[i].Side < 0)
            {
                leftFoot += footMeans[i];
                leftTarget += _turnLatestTargetLanes[i];
                leftCount++;
            }
            else
            {
                rightFoot += footMeans[i];
                rightTarget += _turnLatestTargetLanes[i];
                rightCount++;
            }
        }
        if (leftCount == 0 || rightCount == 0)
        {
            return false;
        }

        float pairGapSum = 0f;
        float pairMaxGap = 0f;
        float targetPairMaxGap = 0f;
        float minPairRatio = 1f;
        int pairCount = 0;
        for (int i = 0; i < legCount; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            if (leg.Side >= 0 || leg.Pair is not { } pair)
            {
                continue;
            }
            int pairIndex = _controller.Legs.IndexOf(pair);
            if (pairIndex < 0)
            {
                return false;
            }
            float a = footMeans[i];
            float b = footMeans[pairIndex];
            float gap = Mathf.Abs(a - b);
            pairGapSum += gap;
            pairMaxGap = Mathf.Max(pairMaxGap, gap);
            targetPairMaxGap = Mathf.Max(
                targetPairMaxGap,
                Mathf.Abs(
                    _turnLatestTargetLanes[i]
                    - _turnLatestTargetLanes[pairIndex]));
            float maximum = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            minPairRatio = Mathf.Min(
                minPairRatio,
                maximum > 1e-5f
                    ? Mathf.Min(Mathf.Abs(a), Mathf.Abs(b)) / maximum
                    : 1f);
            pairCount++;
        }
        if (pairCount == 0)
        {
            return false;
        }

        sample = new TurnBalanceSample(
            elapsed,
            Mathf.Abs(leftFoot / leftCount - rightFoot / rightCount),
            pairGapSum / pairCount,
            pairMaxGap,
            Mathf.Abs(leftTarget / leftCount - rightTarget / rightCount),
            targetPairMaxGap,
            minPairRatio,
            minFootNominalRatio,
            minTargetNominalRatio);
        return float.IsFinite(sample.SideGap)
            && float.IsFinite(sample.PairMeanGap)
            && float.IsFinite(sample.PairMaxGap)
            && float.IsFinite(sample.TargetSideGap)
            && float.IsFinite(sample.TargetPairMaxGap)
            && float.IsFinite(sample.MinPairRatio)
            && float.IsFinite(sample.MinFootNominalRatio)
            && float.IsFinite(sample.MinTargetNominalRatio);
    }

    private int TurnBalanceBudget() =>
        _turnKind == TurnKind.Around ? 100 : 80;

    private void SummarizeTurnBalance(
        out float sideGapP95,
        out float pairMeanGapP95,
        out float pairMaxGapP95,
        out float targetSideGapP95,
        out float targetPairMaxGapP95,
        out float pairRatioP05,
        out float footNominalRatioP05,
        out float targetNominalRatioP05,
        out TurnBalanceSample final)
    {
        int budget = TurnBalanceBudget();
        var sideGaps = new List<float>();
        var pairMeanGaps = new List<float>();
        var pairMaxGaps = new List<float>();
        var targetSideGaps = new List<float>();
        var targetPairMaxGaps = new List<float>();
        var pairRatios = new List<float>();
        var footNominalRatios = new List<float>();
        var targetNominalRatios = new List<float>();
        foreach (TurnBalanceSample sample in _turnBalanceSamples)
        {
            if (sample.Tick <= budget)
            {
                continue;
            }
            sideGaps.Add(sample.SideGap);
            pairMeanGaps.Add(sample.PairMeanGap);
            pairMaxGaps.Add(sample.PairMaxGap);
            targetSideGaps.Add(sample.TargetSideGap);
            targetPairMaxGaps.Add(sample.TargetPairMaxGap);
            pairRatios.Add(sample.MinPairRatio);
            footNominalRatios.Add(sample.MinFootNominalRatio);
            targetNominalRatios.Add(sample.MinTargetNominalRatio);
        }
        sideGapP95 = PercentileOr(
            sideGaps, 0.95f, float.PositiveInfinity);
        pairMeanGapP95 = PercentileOr(
            pairMeanGaps, 0.95f, float.PositiveInfinity);
        pairMaxGapP95 = PercentileOr(
            pairMaxGaps, 0.95f, float.PositiveInfinity);
        targetSideGapP95 = PercentileOr(
            targetSideGaps, 0.95f, float.PositiveInfinity);
        targetPairMaxGapP95 = PercentileOr(
            targetPairMaxGaps, 0.95f, float.PositiveInfinity);
        pairRatioP05 = PercentileOr(
            pairRatios, 0.05f, float.NegativeInfinity);
        footNominalRatioP05 = PercentileOr(
            footNominalRatios, 0.05f, float.NegativeInfinity);
        targetNominalRatioP05 = PercentileOr(
            targetNominalRatios, 0.05f, float.NegativeInfinity);
        final = _turnBalanceSamples.Count > 0
            ? _turnBalanceSamples[^1]
            : new TurnBalanceSample(
                _turnObservationTicks,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
    }

    private static float PercentileOr(
        List<float> values, float percentile, float fallback)
    {
        if (values.Count == 0)
        {
            return fallback;
        }
        float[] sorted = values.ToArray();
        Array.Sort(sorted);
        int index = Mathf.Clamp(
            Mathf.CeilToInt(percentile * sorted.Length) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private int DumpFinalState()
    {
        int finalInwardGripping = 0;
        foreach (SpiderLeg leg in _controller.Legs)
        {
            if (leg.Gripping && leg.Pair is { } pair)
            {
                Vector3 outward = leg.RootPos - pair.RootPos;
                if (outward.LengthSquared() <= 1e-8f
                    || leg.BendPole.Dot(outward.Normalized()) <= 0.02f)
                {
                    finalInwardGripping++;
                }
            }
        }
        int gaitMinSteps = int.MaxValue;
        int gaitMaxSteps = 0;
        float gaitMinLegMeanGain = float.PositiveInfinity;
        float gaitMaxLegMeanTrail = float.NegativeInfinity;
        float gaitMaxLegSevereFraction = 0f;
        float gaitMinLegForwardSwingFraction = float.PositiveInfinity;
        int gaitMaxEmergencyStepsPerLeg = 0;
        int rearPairIndex = -1;
        foreach (SpiderLeg leg in _controller.Legs)
        {
            rearPairIndex = Math.Max(rearPairIndex, leg.PairIndex);
        }
        int gaitRearSteps = 0;
        int gaitRearMicroSteps = 0;
        int gaitRearEmergencySteps = 0;
        int gaitRearStanceCount = 0;
        float gaitRearGain = 0f;
        float gaitRearClearance = 0f;
        float gaitRearStanceAdvance = 0f;
        for (int i = 0; i < _gaitStepsPerLeg.Length; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            int steps = _gaitStepsPerLeg[i];
            gaitMinSteps = Math.Min(gaitMinSteps, steps);
            gaitMaxSteps = Math.Max(gaitMaxSteps, steps);
            gaitMaxEmergencyStepsPerLeg = Math.Max(
                gaitMaxEmergencyStepsPerLeg, _gaitEmergencyStepsPerLeg[i]);
            if (steps > 0)
            {
                gaitMinLegMeanGain = Mathf.Min(
                    gaitMinLegMeanGain, _gaitForwardGainPerLeg[i] / steps);
            }
            if (_gaitPlantedSamplesPerLeg[i] > 0)
            {
                gaitMaxLegMeanTrail = Mathf.Max(
                    gaitMaxLegMeanTrail,
                    _gaitPlantedTrailPerLeg[i] / _gaitPlantedSamplesPerLeg[i]);
                gaitMaxLegSevereFraction = Mathf.Max(
                    gaitMaxLegSevereFraction,
                    _gaitSevereSamplesPerLeg[i]
                    / (float)_gaitPlantedSamplesPerLeg[i]);
            }
            if (_gaitSwingSamplesPerLeg[i] > 0)
            {
                gaitMinLegForwardSwingFraction = Mathf.Min(
                    gaitMinLegForwardSwingFraction,
                    _gaitForwardSwingSamplesPerLeg[i]
                    / (float)_gaitSwingSamplesPerLeg[i]);
            }
            if (leg.PairIndex == rearPairIndex)
            {
                gaitRearSteps += steps;
                gaitRearMicroSteps += _gaitMicroStepsPerLeg[i];
                gaitRearEmergencySteps += _gaitEmergencyStepsPerLeg[i];
                gaitRearGain += _gaitForwardGainPerLeg[i];
                gaitRearClearance += _gaitClearancePerLeg[i];
                gaitRearStanceAdvance += _gaitStanceAdvancePerLeg[i];
                gaitRearStanceCount += _gaitStanceCountPerLeg[i];
            }
        }
        if (gaitMinSteps == int.MaxValue)
        {
            gaitMinSteps = 0;
        }
        if (float.IsPositiveInfinity(gaitMinLegMeanGain))
        {
            gaitMinLegMeanGain = 0f;
        }
        if (float.IsNegativeInfinity(gaitMaxLegMeanTrail))
        {
            gaitMaxLegMeanTrail = 0f;
        }
        if (float.IsPositiveInfinity(gaitMinLegForwardSwingFraction))
        {
            gaitMinLegForwardSwingFraction = 0f;
        }
        float gaitMeanGain = _gaitCompletedSteps > 0
            ? _gaitForwardGainSum / _gaitCompletedSteps
            : 0f;
        float gaitMeanTrail = _gaitPlantedSamples > 0
            ? _gaitPlantedTrailSum / _gaitPlantedSamples
            : 0f;
        float gaitSevereFraction = _gaitPlantedSamples > 0
            ? _gaitSevereTrailSamples / (float)_gaitPlantedSamples
            : 0f;
        float gaitMicroFraction = _gaitCompletedSteps > 0
            ? _gaitMicroSteps / (float)_gaitCompletedSteps
            : 0f;
        float gaitRearMeanGain = gaitRearSteps > 0
            ? gaitRearGain / gaitRearSteps
            : 0f;
        float gaitRearMicroFraction = gaitRearSteps > 0
            ? gaitRearMicroSteps / (float)gaitRearSteps
            : 0f;
        float gaitRearMeanClearance = gaitRearSteps > 0
            ? gaitRearClearance / gaitRearSteps
            : 0f;
        float gaitRearMeanStanceAdvance = gaitRearStanceCount > 0
            ? gaitRearStanceAdvance / gaitRearStanceCount
            : 0f;
        GD.Print($"[SPIDER-METRIC] breed={_breed.Name} route={_route} " +
                 $"waypointsReached={_waypointsReached} gripping={_controller.LegsGripping}/{_controller.Legs.Count} " +
                 $"maxZeroGripRun={_maxZeroGripRun} maxConstraint={_maxConstraintDeviation:F4} " +
                 $"maxConstraintRun={_maxDeepConstraintRun} maxBodyStep={_maxBodyStep:F4}" +
                 $"@{_maxBodyStepTick}/c{_maxBodyStepChunk} " +
                 $"maxSettledStep={_maxSettledBodyStep:F4} " +
                 $"maxKneeStep={_maxKneeStep:F4} maxKneeRatio={_maxKneeStepRatio:F3} " +
                 $"maxLocalKneeRatio={_maxLocalKneeStepRatio:F3} " +
                 $"maxIkError={_maxIkError:F5} reachExcess={_maxReachExcess:F5} " +
                 $"reachDeficit={_maxReachDeficit:F5} " +
                 $"poleFlips={_poleFlipViolations} sideRecovery={_maxSideRecoveryRun} " +
                 $"finalInwardGrip={finalInwardGripping} maxPenetration={_maxPenetration:F5}" +
                 $"@{_maxPenetrationTick}/{_maxPenetrationPart}");
        GD.Print($"[SPIDER-SCENARIO] floor={_floorSeen} ramp={_rampSeen} wall={_wallSeen} " +
                 $"cornerOverlap={_cornerGripOverlapSeen} mixedSupport={_mixedSupportSeen} " +
                 $"outerCorner={_outerCornerSeen} " +
                 $"firstL={_firstLWallSeen} secondL={_secondLWallSeen} ceiling={_ceilingSeen} " +
                 $"transitionTicks={_wallTransitionTicks} wallRise={_maxWallRise:F3} " +
                 $"postProgress={_postTransitionProgress:F3} phase={_routePhase}");
        GD.Print($"[SPIDER-NARROW] seen={_narrowWallSeen} rise={_maxNarrowWallRise:F3} " +
                 $"observe={_narrowObservationTicks} majorityRun={_maxNarrowMajorityRun} " +
                 $"bothSidesRun={_maxNarrowBothSidesRun} maxSideGrips={_maxNarrowSideGrips} " +
                 $"sideGripRun={_maxNarrowSideGripRun} finalSideGrips={_narrowFinalSideGrips} " +
                 $"supportFinite={_narrowSupportFinite} minSupport={_narrowMinSupportLength:F3} " +
                 $"maxSupportStep={_narrowMaxSupportStep:F3}");
        GD.Print($"[SPIDER-GAIT] observe={_gaitObservationTicks} " +
                 $"steps={_gaitCompletedSteps}/{gaitMinSteps}..{gaitMaxSteps} " +
                 $"gain={gaitMeanGain:F3}/{(float.IsPositiveInfinity(_gaitMinForwardGain) ? 0f : _gaitMinForwardGain):F3} " +
                 $"legGainMin={gaitMinLegMeanGain:F3} " +
                 $"trail={gaitMeanTrail:F3}/{gaitMaxLegMeanTrail:F3} " +
                 $"severe={gaitSevereFraction:P1}/{gaitMaxLegSevereFraction:P1}/" +
                 $"{_gaitMaxSevereTrailRun} swingForwardMin=" +
                 $"{gaitMinLegForwardSwingFraction:P1} " +
                 $"retarget/emergency={_gaitDirectRetargets}/{_gaitEmergencySteps}" +
                 $"(max/rear={gaitMaxEmergencyStepsPerLeg}/{gaitRearEmergencySteps}) " +
                 $"micro={gaitMicroFraction:P1} rear=" +
                 $"{gaitRearMeanGain:F3}/{gaitRearMicroFraction:P1}/" +
                 $"{gaitRearMeanClearance:F3}/{gaitRearMeanStanceAdvance:F3}");
        int turnFootRecovery = _turnLastFootViolationTick < 0
            ? 0
            : _turnLastFootViolationTick + 1;
        int turnTargetRecovery = _turnLastTargetViolationTick < 0
            ? 0
            : _turnLastTargetViolationTick + 1;
        int turnBodyRecovery = _turnLastBodyViolationTick < 0
            ? 0
            : _turnLastBodyViolationTick + 1;
        float turnMinFootLane = float.IsPositiveInfinity(_turnMinFootLane)
            ? 0f
            : _turnMinFootLane;
        float turnMinTargetLane =
            float.IsPositiveInfinity(_turnMinFrozenTargetLane)
                ? 0f
                : _turnMinFrozenTargetLane;
        float turnFinalFootLane =
            float.IsPositiveInfinity(_turnFinalMinFootLane)
                ? 0f
                : _turnFinalMinFootLane;
        float turnFinalTargetLane =
            float.IsPositiveInfinity(_turnFinalMinTargetLane)
                ? 0f
                : _turnFinalMinTargetLane;
        int turnPoleFlips = _turnStarted
            ? _poleFlipViolations - _turnPoleFlipsAtStart
            : 0;
        float turnSideGapP95 = 0f;
        float turnPairMeanGapP95 = 0f;
        float turnPairMaxGapP95 = 0f;
        float turnTargetSideGapP95 = 0f;
        float turnTargetPairMaxGapP95 = 0f;
        float turnPairRatioP05 = 1f;
        float turnFootNominalRatioP05 = 1f;
        float turnTargetNominalRatioP05 = 1f;
        TurnBalanceSample turnFinalBalance = default;
        if (_route == Route.Turn)
        {
            SummarizeTurnBalance(
                out turnSideGapP95,
                out turnPairMeanGapP95,
                out turnPairMaxGapP95,
                out turnTargetSideGapP95,
                out turnTargetPairMaxGapP95,
                out turnPairRatioP05,
                out turnFootNominalRatioP05,
                out turnTargetNominalRatioP05,
                out turnFinalBalance);
        }
        GD.Print($"[SPIDER-TURN] kind={_turnKind} observe={_turnObservationTicks} " +
                 $"progress={_turnFinalProgress:F3}/{_turnMaxProgress:F3} " +
                 $"align={_turnFinalBodyAlignment:F3}/{_turnFinalForwardAlignment:F3} " +
                 $"recover(body/foot/target)={turnBodyRecovery}/{turnFootRecovery}/" +
                 $"{turnTargetRecovery} alignedRun={_turnMaxBodyAlignedRun} " +
                 $"laneMin={turnMinFootLane:F3}/{turnMinTargetLane:F3} " +
                 $"laneFinal={turnFinalFootLane:F3}/{turnFinalTargetLane:F3} " +
                 $"wrongRun={_turnMaxFootWrongRun}/{_turnMaxTargetWrongRun} " +
                 $"balance={_turnBalanceRecoveryTick}/{TurnBalanceBudget()} " +
                 $"gapP95={turnSideGapP95:F3}/{turnPairMeanGapP95:F3}/" +
                 $"{turnPairMaxGapP95:F3} target=" +
                 $"{turnTargetSideGapP95:F3}/{turnTargetPairMaxGapP95:F3} " +
                 $"ratioP05={turnPairRatioP05:F3} nominalP05=" +
                 $"{turnFootNominalRatioP05:F3}/" +
                 $"{turnTargetNominalRatioP05:F3} finalGap=" +
                 $"{turnFinalBalance.SideGap:F3}/" +
                 $"{turnFinalBalance.PairMaxGap:F3}/" +
                 $"{turnFinalBalance.TargetSideGap:F3}/" +
                 $"{turnFinalBalance.TargetPairMaxGap:F3}/" +
                 $"{turnFinalBalance.MinPairRatio:F3} nominal=" +
                 $"{turnFinalBalance.MinFootNominalRatio:F3}/" +
                 $"{turnFinalBalance.MinTargetNominalRatio:F3} " +
                 $"replanted={_turnLegsReplanted}/{_controller.Legs.Count}" +
                 $"@{_turnAllLegsReplantedTick} targets={_turnTargetsSeen}/" +
                 $"{_controller.Legs.Count} own={_turnFinalOwnFeet}/" +
                 $"{_turnFinalOwnTargets}/pairs{_turnFinalOwnPairs} " +
                 $"gripMin={(_turnMinGripping == int.MaxValue ? 0 : _turnMinGripping)} " +
                 $"zero/gravity={_turnMaxZeroGripRun}/{_turnMaxGravityRun} " +
                 $"supportUp={_turnMinSupportUp:F3} poleDot={_turnMinPoleStepDot:F3} " +
                 $"poleFlips={turnPoleFlips} localKnee={_turnMaxLocalKneeStepRatio:F3}");

        Vector3 support = _controller.SupportNormal;
        GD.Print($"[SPIDER-FINAL] applyGravity={_controller.ApplyGravity} " +
                 $"footing={_controller.FootingCounter} noGrip={_controller.NoGripCounter} " +
                 $"stall={_controller.StallTicks} support=({support.X:F3},{support.Y:F3},{support.Z:F3})");
        for (int i = 0; i < _controller.Body.Chunks.Count; i++)
        {
            BodyChunk chunk = _controller.Body.Chunks[i];
            GD.Print($"[SPIDER-FINAL] chunk={i} pos=({chunk.Pos.X:F4},{chunk.Pos.Y:F4},{chunk.Pos.Z:F4}) " +
                     $"vel={chunk.Vel.Length():F5} contact={chunk.TerrainContact}");
        }
        for (int i = 0; i < _controller.Legs.Count; i++)
        {
            SpiderLeg leg = _controller.Legs[i];
            GD.Print($"[SPIDER-FINAL] leg={i} anchor={SegmentIndex(leg.Anchor)} side={leg.Side} " +
                     $"foot=({leg.Pos.X:F4},{leg.Pos.Y:F4},{leg.Pos.Z:F4}) " +
                     $"knee=({leg.KneePos.X:F4},{leg.KneePos.Y:F4},{leg.KneePos.Z:F4}) " +
                     $"grip={leg.GripCounter} hasGrip={leg.HasGrip} acquire={leg.AcquisitionTicks} " +
                     $"targetContact={leg.TargetSurfaceContact} " +
                     $"normal=({leg.GripNormal.X:F3},{leg.GripNormal.Y:F3},{leg.GripNormal.Z:F3}) " +
                     $"hunt=({leg.HuntPos.X:F4},{leg.HuntPos.Y:F4},{leg.HuntPos.Z:F4}) " +
                     $"reaching={leg.ReachingForTerrain} frozen={leg.HasFrozenSwingTarget} " +
                     $"step={leg.StepSerial}/{leg.LastStepReason} " +
                     $"emergency={leg.EmergencyStepCount} landing={leg.LandingSerial}");
        }

        var reasons = new List<string>();
        if (_nonFinite) reasons.Add("身体/足端/膝点出现 NaN 或 Inf");
        if (_expectHash is ulong expected && _hasher.Value != expected)
        {
            reasons.Add($"哈希 {_hasher.Value:X16} != 基线 {expected:X16}");
        }
        if (!_sawGrip) reasons.Add("全程没有真实抓地");
        if (_maxZeroGripRun > ZeroGripRunLimit)
        {
            reasons.Add($"最长零抓地窗口 {_maxZeroGripRun} tick > {ZeroGripRunLimit}");
        }
        if (_maxDeepConstraintRun > DeepConstraintRunLimit)
        {
            reasons.Add($"身体约束深违反持续 {_maxDeepConstraintRun} tick > {DeepConstraintRunLimit}");
        }
        if (_maxBodyStep > MaxBodyStepLimit)
        {
            reasons.Add($"身体异常单 tick 位移 {_maxBodyStep:F3}m > {MaxBodyStepLimit:F2}m");
        }
        // 身体碰撞恢复本身已有 MaxBodyStepLimit；膝点连续性应剔除根部同帧平移，
        // 否则一次合法的整身 MTD 会被误记成 IK 翻折。这里约束的是相对腿根姿态跳变。
        if (_maxLocalKneeStepRatio > MaxKneeStepRatioLimit)
        {
            reasons.Add($"膝点相对腿根的单 tick 跳变达到腿长 {_maxLocalKneeStepRatio:P0} " +
                        $"> {MaxKneeStepRatioLimit:P0}");
        }
        if (_maxIkError > MaxIkErrorLimit)
        {
            reasons.Add($"两段 IK 长度误差 {_maxIkError:F4}m > {MaxIkErrorLimit:F3}m");
        }
        if (_maxReachExcess > MaxReachExcessLimit)
        {
            reasons.Add($"足端超出最大可达距离 {_maxReachExcess:F4}m > {MaxReachExcessLimit:F3}m");
        }
        if (_maxReachDeficit > MaxReachDeficitLimit)
        {
            reasons.Add($"足端进入两段腿内不可达区 {_maxReachDeficit:F4}m > {MaxReachDeficitLimit:F3}m");
        }
        if (_poleFlipViolations > 0)
        {
            reasons.Add($"bend pole/膝弯折反侧 {_poleFlipViolations} 次");
        }
        if (_maxSideRecoveryRun > MaxSideRecoveryRunLimit)
        {
            reasons.Add($"膝弯折落入身体内侧持续 {_maxSideRecoveryRun} tick " +
                        $"> {MaxSideRecoveryRunLimit}");
        }
        // Course 的最后一个 tick 可能正落在换面/坠落中；这种瞬态由持续时长和 pole
        // 翻面门约束。只有已经恢复稳定支撑时，才要求终态每条承重腿都弯向本侧。
        bool finalStableStance = !_controller.ApplyGravity
            && _controller.LegsGripping >= _controller.MinPlantedLegs;
        if (finalStableStance && finalInwardGripping > 0)
        {
            reasons.Add($"终态仍有 {finalInwardGripping} 条抓稳腿向身体内侧弯折");
        }
        if (_maxPenetration > MaxPostSolvePenetration)
        {
            reasons.Add($"tick 末身体/足端穿透 {_maxPenetration:F4}m > {MaxPostSolvePenetration:F3}m");
        }

        switch (_route)
        {
            case Route.Gait:
                if (_gaitObservationTicks < 200)
                {
                    reasons.Add($"大型蜘蛛稳定直行观察只有 {_gaitObservationTicks} tick < 200");
                }
                if (_gaitCompletedSteps < 80 || gaitMinSteps < 8)
                {
                    reasons.Add($"大型蜘蛛换步不积极: 完整步={_gaitCompletedSteps}, " +
                                $"每腿={gaitMinSteps}..{gaitMaxSteps}（要求总计 ≥80、每腿 ≥8）");
                }
                if (gaitMeanTrail > 0.40f || gaitSevereFraction > 0.65f)
                {
                    reasons.Add($"大型蜘蛛长期拖腿: 平均={gaitMeanTrail:F3}, " +
                                $"严重占比={gaitSevereFraction:P1}（上限 0.40/65%）");
                }
                if (gaitMeanGain < 0.25f)
                {
                    reasons.Add($"大型蜘蛛每步平均只向前追回 {gaitMeanGain:F3}×腿长");
                }
                if (gaitMinLegMeanGain < 0.20f)
                {
                    reasons.Add($"大型蜘蛛最弱单腿平均局部复位只有 " +
                                $"{gaitMinLegMeanGain:F3}×腿长");
                }
                if (gaitRearMeanGain < 0.24f
                    || gaitRearMicroFraction > 0.20f)
                {
                    reasons.Add($"大型蜘蛛后腿仍是碎步: 平均复位=" +
                                $"{gaitRearMeanGain:F3}×腿长, 微步=" +
                                $"{gaitRearMicroFraction:P1}");
                }
                if (_gaitDirectRetargets > 0
                    || gaitRearEmergencySteps > 2
                    || gaitMaxEmergencyStepsPerLeg > 8)
                {
                    reasons.Add($"大型蜘蛛仍依赖无摆动换点/超距恢复: direct=" +
                                $"{_gaitDirectRetargets}, emergency=" +
                                $"{_gaitEmergencySteps}, max/rear=" +
                                $"{gaitMaxEmergencyStepsPerLeg}/{gaitRearEmergencySteps}");
                }
                if (gaitRearMeanClearance < 0.04f
                    || gaitRearMeanStanceAdvance < 0.18f)
                {
                    reasons.Add($"大型蜘蛛后腿缺少抬脚/支撑推蹬: clearance=" +
                                $"{gaitRearMeanClearance:F3}, stance=" +
                                $"{gaitRearMeanStanceAdvance:F3}");
                }
                if (gaitMinLegForwardSwingFraction < 0.90f)
                {
                    reasons.Add($"大型蜘蛛最弱单腿只有 " +
                                $"{gaitMinLegForwardSwingFraction:P1} 的摆动采样朝前");
                }
                if (gaitMaxLegMeanTrail > 0.54f
                    || _gaitMaxSevereTrailRun > 32)
                {
                    reasons.Add($"大型蜘蛛存在局部长期拖腿: 单腿平均最大=" +
                                $"{gaitMaxLegMeanTrail:F3}, 严重占比最大=" +
                                $"{gaitMaxLegSevereFraction:P1}, 连续=" +
                                $"{_gaitMaxSevereTrailRun} tick");
                }
                break;
            case Route.NarrowWall:
                if (!_narrowWallSeen || _maxNarrowWallRise < 1f)
                {
                    reasons.Add("窄墙路线没有形成稳定端面抓握并向上爬升至少 1m");
                }
                if (_narrowObservationTicks < 120)
                {
                    reasons.Add($"窄墙稳定观察窗口只有 {_narrowObservationTicks} tick < 120");
                }
                if (_maxNarrowMajorityRun < 80)
                {
                    reasons.Add($"窄墙多数腿连续抓稳仅 {_maxNarrowMajorityRun} tick < 80");
                }
                if (_maxNarrowBothSidesRun < 20)
                {
                    reasons.Add($"窄墙左右侧面同时真实抓稳仅 {_maxNarrowBothSidesRun} tick < 20");
                }
                if (_maxNarrowSideGrips < 4 || _maxNarrowSideGripRun < 80
                    || _narrowFinalSideGrips < 4)
                {
                    reasons.Add($"窄墙四腿抱边不持续: max={_maxNarrowSideGrips}, " +
                                $"run={_maxNarrowSideGripRun} tick, " +
                                $"final={_narrowFinalSideGrips}（要求 ≥4 条持续 ≥80 tick）");
                }
                if (!_narrowSupportFinite || _narrowMinSupportLength < 0.95f
                    || _narrowMaxSupportStep > 0.4f)
                {
                    reasons.Add($"窄墙支撑法线不稳定: finite={_narrowSupportFinite} " +
                                $"minLength={_narrowMinSupportLength:F3} " +
                                $"maxStep={_narrowMaxSupportStep:F3}");
                }
                break;
            case Route.Course:
                if (_waypointsReached < 2) reasons.Add($"标准路线只到达 {_waypointsReached} 个路点");
                if (!_floorSeen || !_rampSeen) reasons.Add("标准路线没有覆盖地面和斜坡支撑");
                if (!_wallSeen || _maxWallRise < 1f) reasons.Add("标准路线没有在墙面持续上升至少 1m");
                if (!_cornerGripOverlapSeen) reasons.Add("地面到墙面的内角没有真实跨面抓握");
                if (!_mixedSupportSeen) reasons.Add("地面到墙面的内角没有形成中间支撑法线");
                if (!_outerCornerSeen) reasons.Add("墙顶外角后没有在顶面重新抓稳");
                if (_wallTransitionTicks < 0 || _wallTransitionTicks > 140)
                {
                    reasons.Add($"墙面到墙顶换面耗时 {_wallTransitionTicks} tick，不在 0..140");
                }
                break;
            case Route.WallL:
                if (!_firstLWallSeen || !_secondLWallSeen)
                {
                    reasons.Add("墙墙 L 角没有依次抓到两张垂直面");
                }
                if (!_cornerGripOverlapSeen) reasons.Add("墙墙 L 角没有真实跨面抓握");
                if (!_mixedSupportSeen) reasons.Add("墙墙 L 角没有形成中间支撑法线");
                if (_wallTransitionTicks < 0 || _wallTransitionTicks > 100)
                {
                    reasons.Add($"墙墙 L 角换面耗时 {_wallTransitionTicks} tick，不在 0..100");
                }
                if (_postTransitionProgress < 0.45f)
                {
                    reasons.Add($"换到第二面后只前进 {_postTransitionProgress:F2}m");
                }
                break;
            case Route.WallCeiling:
                if (!_wallSeen || _maxWallRise < 2f)
                {
                    reasons.Add("墙到天花板路线没有形成稳定墙爬升");
                }
                if (!_cornerGripOverlapSeen || !_mixedSupportSeen || !_ceilingSeen)
                {
                    reasons.Add("墙顶内角缺少跨面抓握、中间支撑或天花板抓稳");
                }
                if (_wallTransitionTicks < 0 || _wallTransitionTicks > 120)
                {
                    reasons.Add($"墙到天花板换面耗时 {_wallTransitionTicks} tick，不在 0..120");
                }
                if (_postTransitionProgress < 0.55f)
                {
                    reasons.Add($"天花板上只前进 {_postTransitionProgress:F2}m");
                }
                break;
            case Route.Turn:
                int footRecovery = _turnLastFootViolationTick < 0
                    ? 0
                    : _turnLastFootViolationTick + 1;
                int targetRecovery = _turnLastTargetViolationTick < 0
                    ? 0
                    : _turnLastTargetViolationTick + 1;
                int bodyRecovery = _turnLastBodyViolationTick < 0
                    ? 0
                    : _turnLastBodyViolationTick + 1;
                int turnPoleFlipCount =
                    _poleFlipViolations - _turnPoleFlipsAtStart;
                int expectedPairs = _controller.Legs.Count / 2;
                int footWrongRunLimit =
                    _turnKind == TurnKind.Around ? 55 : 45;
                int balanceBudget = TurnBalanceBudget();
                SummarizeTurnBalance(
                    out float sideGapP95,
                    out float pairMeanGapP95,
                    out float pairMaxGapP95,
                    out float targetSideGapP95,
                    out float targetPairMaxGapP95,
                    out float pairRatioP05,
                    out float footNominalRatioP05,
                    out float targetNominalRatioP05,
                    out TurnBalanceSample finalBalance);
                if (!_turnStarted || _turnObservationTicks < 240)
                {
                    reasons.Add($"急转观察窗口只有 {_turnObservationTicks} tick < 240");
                }
                if (_turnMaxProgress < 2.8f)
                {
                    reasons.Add($"急转后只前进 {_turnMaxProgress:F2}m < 2.8m");
                }
                if (_turnLegsReplanted != _controller.Legs.Count
                    || _turnAllLegsReplantedTick < 0
                    || _turnAllLegsReplantedTick > 180)
                {
                    reasons.Add($"急转后逐腿重植 {_turnLegsReplanted}/" +
                                $"{_controller.Legs.Count}，全部完成 tick=" +
                                $"{_turnAllLegsReplantedTick}（上限 180）");
                }
                if (_turnTargetsSeen != _controller.Legs.Count)
                {
                    reasons.Add($"急转后只有 {_turnTargetsSeen}/" +
                                $"{_controller.Legs.Count} 条腿产生冻结下一落点");
                }
                if (_turnMaxFootWrongRun > footWrongRunLimit
                    || footRecovery > 110)
                {
                    reasons.Add($"足端跨身恢复过慢: 连续={_turnMaxFootWrongRun}, " +
                                $"最终恢复={footRecovery} tick（上限 " +
                                $"{footWrongRunLimit}/110）");
                }
                if (_turnMaxTargetWrongRun > 12 || targetRecovery > 80)
                {
                    reasons.Add($"冻结 AEP 跨身恢复过慢: 连续={_turnMaxTargetWrongRun}, " +
                                $"最终恢复={targetRecovery} tick（上限 12/80）");
                }
                if (_turnFinalOwnFeet != _controller.Legs.Count
                    || _turnFinalOwnTargets != _controller.Legs.Count
                    || _turnFinalOwnPairs != expectedPairs)
                {
                    reasons.Add($"急转终态未全部回到本侧: feet={_turnFinalOwnFeet}/" +
                                $"{_controller.Legs.Count}, targets={_turnFinalOwnTargets}/" +
                                $"{_controller.Legs.Count}, pairs={_turnFinalOwnPairs}/" +
                                $"{expectedPairs}");
                }
                if (_turnBalanceRecoveryTick < 0
                    || _turnBalanceRecoveryTick > balanceBudget)
                {
                    reasons.Add($"急转后左右站距未按预算恢复: tick=" +
                                $"{_turnBalanceRecoveryTick}（上限 " +
                                $"{balanceBudget}）");
                }
                if (sideGapP95 > 0.10f
                    || pairMeanGapP95 > 0.12f
                    || pairMaxGapP95 > 0.15f
                    || targetSideGapP95 > 0.10f
                    || targetPairMaxGapP95 > 0.15f
                    || pairRatioP05 < 0.80f
                    || footNominalRatioP05 < 0.80f
                    || targetNominalRatioP05 < 0.80f)
                {
                    reasons.Add($"急转预算后站距持续不平衡: p95=" +
                                $"{sideGapP95:F3}/{pairMeanGapP95:F3}/" +
                                $"{pairMaxGapP95:F3}, target=" +
                                $"{targetSideGapP95:F3}/" +
                                $"{targetPairMaxGapP95:F3}, ratioP05=" +
                                $"{pairRatioP05:F3}, nominalP05=" +
                                $"{footNominalRatioP05:F3}/" +
                                $"{targetNominalRatioP05:F3}");
                }
                if (finalBalance.SideGap > 0.10f
                    || finalBalance.PairMaxGap > 0.15f
                    || finalBalance.TargetSideGap > 0.10f
                    || finalBalance.TargetPairMaxGap > 0.15f
                    || finalBalance.MinPairRatio < 0.80f
                    || finalBalance.MinFootNominalRatio < 0.80f
                    || finalBalance.MinTargetNominalRatio < 0.80f)
                {
                    reasons.Add($"急转终态左右站距不平衡: gap=" +
                                $"{finalBalance.SideGap:F3}/" +
                                $"{finalBalance.PairMaxGap:F3}, target=" +
                                $"{finalBalance.TargetSideGap:F3}/" +
                                $"{finalBalance.TargetPairMaxGap:F3}, ratio=" +
                                $"{finalBalance.MinPairRatio:F3}, nominal=" +
                                $"{finalBalance.MinFootNominalRatio:F3}/" +
                                $"{finalBalance.MinTargetNominalRatio:F3}");
                }
                if (_turnFinalBodyAlignment < 0.88f
                    || _turnFinalForwardAlignment < 0.92f
                    || _turnMaxBodyAlignedRun < 80
                    || bodyRecovery > 150)
                {
                    reasons.Add($"身体未连续对齐新方向: final=" +
                                $"{_turnFinalBodyAlignment:F3}/" +
                                $"{_turnFinalForwardAlignment:F3}, run=" +
                                $"{_turnMaxBodyAlignedRun}, recovery={bodyRecovery}");
                }
                if (_turnMaxZeroGripRun > 16 || _turnMaxGravityRun > 16)
                {
                    reasons.Add($"急转失抓: zero={_turnMaxZeroGripRun}, " +
                                $"gravity={_turnMaxGravityRun} tick（上限 16）");
                }
                if (_turnMinSupportUp < 0.92f)
                {
                    reasons.Add($"平地急转支撑法线偏离 Up: min={_turnMinSupportUp:F3}");
                }
                if (turnPoleFlipCount > 0
                    || _turnMinPoleStepDot < 0.30f
                    || _turnMaxLocalKneeStepRatio > 0.82f)
                {
                    reasons.Add($"急转 IK/pole 不连续: flips={turnPoleFlipCount}, " +
                                $"poleDot={_turnMinPoleStepDot:F3}, " +
                                $"localKnee={_turnMaxLocalKneeStepRatio:F3}");
                }
                break;
        }

        bool pass = reasons.Count == 0;
        GD.Print(pass
            ? "[SPIDER-RESULT] PASS"
            : $"[SPIDER-RESULT] FAIL: {string.Join("; ", reasons)}");
        return pass ? 0 : 1;
    }

    private int SegmentIndex(BodyChunk chunk)
    {
        for (int i = 0; i < _controller.Segments.Count; i++)
        {
            if (_controller.Segments[i] == chunk)
            {
                return i;
            }
        }
        return -1;
    }

    public override void _Process(double delta)
    {
        if (_fatal)
        {
            return;
        }
        UpdateCameraFly((float)delta);
        _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
        _rayDebug.Draw(_camera, _controller.Primary.Pos,
            _controller.LastMoveTargetKind, _controller.LastMoveTarget);
    }

    private bool WantCameraFly =>
        Input.IsMouseButtonPressed(MouseButton.Right)
        && !Input.IsPhysicalKeyPressed(Key.Shift);

    private void UpdateCameraFly(float delta)
    {
        if (_determinismTicks > 0)
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
        if (direction != Vector3.Zero)
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
                _camYaw -= motion.Relative.X * CameraMouseSensitivity;
                _camPitch = Mathf.Clamp(
                    _camPitch - motion.Relative.Y * CameraMouseSensitivity,
                    -1.5f, 1.5f);
                _camera.Rotation = new Vector3(_camPitch, _camYaw, 0f);
            }
            return;
        }
        if (_determinismTicks > 0
            || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (key.PhysicalKeycode == Key.F3)
        {
            _rayDebug.Enabled = !_rayDebug.Enabled;
            GD.Print($"[SPIDER-SANDBOX] ray debug {(_rayDebug.Enabled ? "on" : "off")}");
            return;
        }
        if (key.PhysicalKeycode < Key.Key1 || key.PhysicalKeycode > Key.Key2)
        {
            return;
        }
        SelectBreed((int)(key.PhysicalKeycode - Key.Key1));
    }

    private void SelectBreed(int index)
    {
        SpiderBreedParams[] breeds = SpiderFactory.AllBreeds();
        if (index < 0 || index >= breeds.Length)
        {
            return;
        }
        SpawnSpider(breeds[index], _controller.Rear.Pos + Vector3.Up * 0.6f);
        _breedUi?.SyncSelection(index);
        GD.Print($"[SPIDER-SANDBOX] breed -> {breeds[index].Name}");
    }

    private bool ParseArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (arg.StartsWith("--determinism="))
                {
                    _determinismTicks = int.Parse(arg["--determinism=".Length..], inv);
                    if (_determinismTicks <= 0) throw new FormatException("tick 数必须为正");
                }
                else if (arg.StartsWith("--tps="))
                {
                    int tps = int.Parse(arg["--tps=".Length..], inv);
                    if (tps <= 0) throw new FormatException("tps 必须为正");
                    Engine.PhysicsTicksPerSecond = tps;
                    Engine.MaxPhysicsStepsPerFrame = 100;
                }
                else if (arg.StartsWith("--breed="))
                {
                    _breed = SpiderFactory.ByName(arg["--breed=".Length..]);
                }
                else if (arg == "--route=course")
                {
                    _route = Route.Course;
                }
                else if (arg == "--route=gait")
                {
                    _route = Route.Gait;
                }
                else if (arg == "--route=narrow-wall")
                {
                    _route = Route.NarrowWall;
                }
                else if (arg == "--route=wall-l")
                {
                    _route = Route.WallL;
                }
                else if (arg == "--route=wall-ceiling")
                {
                    _route = Route.WallCeiling;
                }
                else if (arg == "--route=turn")
                {
                    _route = Route.Turn;
                }
                else if (arg == "--route=stand")
                {
                    _route = Route.Stand;
                }
                else if (arg.StartsWith("--turn="))
                {
                    _turnKind = arg["--turn=".Length..] switch
                    {
                        "left" => TurnKind.Left,
                        "right" => TurnKind.Right,
                        "around" => TurnKind.Around,
                        _ => throw new FormatException(
                            "turn 只接受 left、right 或 around"),
                    };
                }
                else if (arg.StartsWith("--spawn="))
                {
                    string[] values = arg["--spawn=".Length..].Split(',');
                    if (values.Length != 3) throw new FormatException("spawn 需要 x,y,z");
                    _spawn = new Vector3(float.Parse(values[0], inv),
                        float.Parse(values[1], inv), float.Parse(values[2], inv));
                    if (!_spawn.IsFinite()) throw new FormatException("spawn 必须为有限数");
                    _spawnExplicit = true;
                }
                else if (arg.StartsWith("--perturb="))
                {
                    _perturb = float.Parse(arg["--perturb=".Length..], inv);
                    if (!float.IsFinite(_perturb)) throw new FormatException("perturb 必须为有限数");
                }
                else if (arg.StartsWith("--expect-hash="))
                {
                    _expectHash = ulong.Parse(arg["--expect-hash=".Length..],
                        System.Globalization.NumberStyles.HexNumber, inv);
                }
                else if (arg.StartsWith("--yank="))
                {
                    _yankTick = long.Parse(arg["--yank=".Length..], inv);
                }
                else
                {
                    throw new FormatException("未知参数");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                GD.PrintErr($"[SPIDER-SANDBOX] 参数畸形: {arg} ({exception.Message})");
                return false;
            }
        }

        if (_expectHash is not null && _determinismTicks == 0)
        {
            GD.PrintErr("[SPIDER-SANDBOX] --expect-hash 需要 --determinism");
            return false;
        }
        if (!_spawnExplicit)
        {
            // 窄墙前表面位于 z=-6。出生距离按首节半径缩放，给大小体型都留出
            // 约 0.8×半径的接近行程；固定 -5.72 会让大型体一出生就几乎贴墙，
            // 首次 MTD/步态相位主导结果，测到的是场景装配而不是抱边能力。
            float narrowSpawnZ = -6f + _breed.BodySegments[0].Radius * 1.8f;
            _spawn = _route switch
            {
                // 离地出生隔离“抱住窄墙”与地面候选竞争；仍由控制器自行抓墙、爬升和停稳。
                Route.NarrowWall => new Vector3(-9.15f, 1.5f, narrowSpawnZ),
                Route.Gait => new Vector3(10f, 0.55f, 8f),
                Route.WallL => new Vector3(5f, 0.55f, 2f),
                Route.WallCeiling => new Vector3(1.15f, 0.55f, -6f),
                Route.Turn => new Vector3(11f, 0.55f, 0f),
                _ => new Vector3(0f, 0.55f, 0f),
            };
        }
        return true;
    }
}
