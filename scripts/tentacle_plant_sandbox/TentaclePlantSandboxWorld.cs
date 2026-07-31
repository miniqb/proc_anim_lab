using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 独立 TentaclePlant 白盒：固定 40 tick 驱动核心，宿主只提供静态地形、安装 frame 和
/// 一个纯值 mock prey。idle/hit/miss/occluded 均可交互观看，也可通过 CLI 跑硬断言矩阵。
/// </summary>
public partial class TentaclePlantSandboxWorld : Node3D
{
    private const float TickDt = 0.025f;
    private const ulong TargetStableId = 0x54504C414E54554CUL;
    private const ulong DefaultInstanceSeed = 0x54454E5441434C45UL;
    private const float TargetRadius = 0.16f;

    private static readonly string[] MountNames = { "floor", "wall", "ceiling" };
    private static readonly string[] RouteNames = { "idle", "hit", "miss", "occluded" };
    private static readonly string[] PresetNames =
    {
        "tentacle-plant/original",
        "tentacle-plant/short",
        "tentacle-plant/hunter",
    };

    [Export] public float GravityMps2 = 36f;
    [Export] public float CameraFlySpeed = 8f;
    [Export] public float CameraMouseSensitivity = 0.003f;

    private readonly RaycastTerrainQuery _terrain = new();
    private readonly TentaclePlantRenderer _renderer = new();
    private DeterminismHasher _hasher = new();

    private TentaclePlantController _plant = null!;
    private TentaclePlantParams[] _presets = Array.Empty<TentaclePlantParams>();
    private TentaclePlantParams _preset = null!;
    private TentaclePlantSandboxHud? _hud;
    private Camera3D _camera = null!;
    private OmniLight3D _fillLight = null!;
    private StaticBody3D _occluder = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _mountPoint;
    private Vector3 _mountOutward;
    private Vector3 _mountTangent;
    private Vector3 _mountBitangent;
    private ulong _mountColliderId;
    private Vector3 _rootAnchor;
    private Vector3 _targetPos;
    private Vector3 _targetVelocity;
    private bool _targetActive;
    private bool _manualTarget;
    private bool _missYanked;
    private bool _hostCaptured;
    private bool _targetConsumed;
    private bool _fatal;
    private bool _rendererBuilt;
    private bool _cameraFlying;
    private float _cameraYaw;
    private float _cameraPitch;
    private long _tick;

    // CLI。
    private int _determinismTicks;
    private int _requestedTps = 40;
    private ulong _instanceSeed = DefaultInstanceSeed;
    private string _presetName = PresetNames[0];
    private string _mountName = MountNames[0];
    private string _route = "hit";
    private Vector3? _targetLocalOverride;
    private float _perturb;
    private ulong? _expectHash;

    // 硬断言指标。
    private bool _nonFinite;
    private bool _sawWandering;
    private bool _sawTracking;
    private bool _sawWindup;
    private bool _sawStriking;
    private bool _sawRecovering;
    private bool _sawHolding;
    private bool _sawWanderingAfterRecovery;
    private bool _sawCaptureEffect;
    private bool _sawHeldEffect;
    private bool _sawReleaseEffect;
    private bool _sawConsumeEffect;
    private bool _effectTargetMismatch;
    private bool _holdingContinuityBroken;
    private int _captureEvents;
    private int _consumeEvents;
    private int _releaseEvents;
    private long _captureTick = -1;
    private long _consumeTick = -1;
    private long _releaseTick = -1;
    private long _firstStrikeTick = -1;
    private long _firstRecoverTick = -1;
    private long _firstHoldingTick = -1;
    private int _holdingTicks;
    private int _maxGuidePoints;
    private int _maxBacktrackFrom = -1;
    private int _maxTickQueries;
    private int _peakQueries;
    private long _finalAttackSerial;
    private float _handTravel;
    private float _maxAttackCharge;
    private float _maxEffectMagnitude;
    private float _captureRootDistance;
    private float _consumeRootDistance;
    private float _minHeldExtension = 1f;
    private float _maxHeldTargetRootDistance;
    private float _maxHeldTargetSpeed;
    private float _maxOccludedOutwardExtent;
    private float _maxRootError;
    private float _maxResidualPenetration;
    private int _maxPenetrationSegment = -1;
    private long _maxPenetrationTick = -1;
    private Vector3 _lastHand;

    private bool Deterministic => _determinismTicks > 0;
    private bool WantCameraFly => Input.IsMouseButtonPressed(MouseButton.Right);

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _camera = GetNode<Camera3D>("Camera3D");
        _fillLight = GetNode<OmniLight3D>("FillLight");
        _occluder = GetNode<StaticBody3D>("Terrain/Occluder");
        _cameraYaw = _camera.Rotation.Y;
        _cameraPitch = _camera.Rotation.X;
        _gravityPerTick = new Vector3(0f, -GravityMps2 * TickDt * TickDt, 0f);

        if (!ParseArguments())
        {
            _fatal = true;
            GD.Print("[TENTACLE-PLANT-RESULT] FAIL：命令行参数无效");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
        {
            Engine.MaxPhysicsStepsPerFrame = 100;
        }

        _presets = TentaclePlantFactory.AllPresets();
        _preset = TentaclePlantFactory.ByName(_presetName);
        SpawnPlant(_preset, _mountName);

        if (!Deterministic)
        {
            _hud = new TentaclePlantSandboxHud();
            _hud.Build(this, PresetNames, MountNames, RouteNames,
                SelectPreset, SelectMount, SelectRoute);
            SyncHudPickers();
        }

        GD.Print($"[TENTACLE-PLANT-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.Name} mount={_mountName} route={_route} " +
                 $"seed=0x{_instanceSeed:X16} " +
                 $"determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnPlant(TentaclePlantParams preset, string mountName)
    {
        _preset = preset;
        ConfigureMount(mountName);
        var mount = new TentaclePlantMount(
            _mountPoint,
            _mountOutward,
            _mountTangent,
            _mountColliderId);
        _plant = TentaclePlantFactory.CreateController(in mount, preset, _instanceSeed);
        _rootAnchor = _plant.Root.Pos;
        if (_perturb != 0f)
        {
            _plant.Hand.Pos += _plant.Tangent * _perturb;
            _plant.Hand.LastPos = _plant.Hand.Pos;
        }

        ConfigureOccluder(_route == "occluded");
        ResetTarget();
        ResetMetrics();

        if (_rendererBuilt)
        {
            _renderer.Build(this, _plant);
        }
        else
        {
            _renderer.Build(this, _plant);
            _rendererBuilt = true;
        }
    }

    private void ConfigureMount(string mountName)
    {
        _mountName = mountName;
        StaticBody3D mountBody;
        switch (mountName)
        {
            case "wall":
                _mountPoint = new Vector3(0f, 4f, -9.8f);
                _mountOutward = Vector3.Back;
                _mountTangent = Vector3.Right;
                mountBody = GetNode<StaticBody3D>("Terrain/BackWall");
                break;
            case "ceiling":
                _mountPoint = new Vector3(0f, 7.8f, -1f);
                _mountOutward = Vector3.Down;
                _mountTangent = Vector3.Right;
                mountBody = GetNode<StaticBody3D>("Terrain/Ceiling");
                break;
            default:
                _mountPoint = new Vector3(0f, 0f, 2f);
                _mountOutward = Vector3.Up;
                _mountTangent = Vector3.Right;
                mountBody = GetNode<StaticBody3D>("Terrain/Floor");
                break;
        }
        _mountOutward = SafeDirection(_mountOutward, Vector3.Up);
        _mountTangent -= _mountOutward * _mountTangent.Dot(_mountOutward);
        _mountTangent = SafeDirection(_mountTangent, Vector3.Right);
        _mountBitangent = SafeDirection(_mountOutward.Cross(_mountTangent), Vector3.Forward);
        _mountColliderId = mountBody.GetInstanceId();
    }

    private void ConfigureOccluder(bool enabled)
    {
        if (!enabled)
        {
            _occluder.GlobalTransform =
                new Transform3D(Basis.Identity, new Vector3(1000f, 1000f, 1000f));
            _occluder.Visible = false;
            return;
        }

        Vector3 localZ = SafeDirection(_mountTangent.Cross(_mountOutward), _mountBitangent);
        var basis = new Basis(_mountTangent, _mountOutward, localZ);
        _occluder.GlobalTransform = new Transform3D(
            basis,
            LocalPoint(3.0f, 0f, 0f));
        _occluder.Visible = true;
    }

    private void ResetTarget()
    {
        _manualTarget = false;
        _missYanked = false;
        _hostCaptured = false;
        _targetConsumed = false;
        _targetActive = _route != "idle" &&
            (Deterministic || _route != "hit");
        _targetPos = TargetForRoute(_route);
        _targetVelocity = Vector3.Zero;
        _plant.Target = null;
    }

    private void ResetMetrics()
    {
        _tick = 0;
        _hasher = new DeterminismHasher();
        _nonFinite = false;
        _sawWandering = false;
        _sawTracking = false;
        _sawWindup = false;
        _sawStriking = false;
        _sawRecovering = false;
        _sawHolding = false;
        _sawWanderingAfterRecovery = false;
        _sawCaptureEffect = false;
        _sawHeldEffect = false;
        _sawReleaseEffect = false;
        _sawConsumeEffect = false;
        _effectTargetMismatch = false;
        _holdingContinuityBroken = false;
        _captureEvents = 0;
        _consumeEvents = 0;
        _releaseEvents = 0;
        _captureTick = -1;
        _consumeTick = -1;
        _releaseTick = -1;
        _firstStrikeTick = -1;
        _firstRecoverTick = -1;
        _firstHoldingTick = -1;
        _holdingTicks = 0;
        _maxGuidePoints = 0;
        _maxBacktrackFrom = -1;
        _maxTickQueries = 0;
        _peakQueries = 0;
        _finalAttackSerial = 0;
        _handTravel = 0f;
        _maxAttackCharge = 0f;
        _maxEffectMagnitude = 0f;
        _captureRootDistance = 0f;
        _consumeRootDistance = 0f;
        _minHeldExtension = 1f;
        _maxHeldTargetRootDistance = 0f;
        _maxHeldTargetSpeed = 0f;
        _maxOccludedOutwardExtent = 0f;
        _maxRootError = 0f;
        _maxResidualPenetration = 0f;
        _maxPenetrationSegment = -1;
        _maxPenetrationTick = -1;
        _lastHand = _plant.Hand.Pos;
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
            DriveScriptedScenario();
        }
        else
        {
            SampleTargetInput();
            if (!_manualTarget)
            {
                DriveScriptedScenario();
            }
        }
        if (_targetActive && (_manualTarget || _hostCaptured))
        {
            // mock prey 的宿主积分统一只发生一次：手动目标与已抓目标都先推进，
            // 再把本 tick 快照交给核心；TargetEffect 则留给下方宿主应用阶段。
            _targetPos += _targetVelocity;
        }

        FeedTargetSnapshot();
        _plant.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        ApplyTargetEffect();
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

    private void DriveScriptedScenario()
    {
        switch (_route)
        {
            case "idle":
                _targetActive = false;
                break;
            case "hit":
                // 交互默认演示先让人看见无目标游走，再让 prey 自动进入；确定性路线
                // 从 tick 1 喂目标，便于把攻击时序钉成稳定回归。
                _targetActive = !_targetConsumed &&
                    (Deterministic || _manualTarget || _tick > 80);
                if (_plant.HeldTargetId is null)
                {
                    _targetPos = TargetForRoute("hit");
                    _targetVelocity = Vector3.Zero;
                }
                break;
            case "miss":
                if (!_missYanked)
                {
                    _targetActive = true;
                    _targetPos = TargetForRoute("hit");
                    // 等控制器已经锁定方向并真正开始第一 tick 扑击，再把 prey
                    // 从场景移走；否则在 89→90 的充能边界提前移走会阻止扑击。
                    if (_plant.AttackSerial > 0)
                    {
                        _missYanked = true;
                        _targetActive = false;
                    }
                }
                else
                {
                    _targetActive = false;
                    _targetVelocity = Vector3.Zero;
                }
                break;
            case "occluded":
                _targetActive = true;
                _targetPos = TargetForRoute("occluded");
                _targetVelocity = Vector3.Zero;
                break;
        }
    }

    private void SampleTargetInput()
    {
        if (WantCameraFly)
        {
            return;
        }

        Vector3 delta = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) delta += _mountBitangent;
        if (Input.IsPhysicalKeyPressed(Key.S)) delta -= _mountBitangent;
        if (Input.IsPhysicalKeyPressed(Key.D)) delta += _mountTangent;
        if (Input.IsPhysicalKeyPressed(Key.A)) delta -= _mountTangent;
        if (Input.IsPhysicalKeyPressed(Key.E)) delta += _mountOutward;
        if (Input.IsPhysicalKeyPressed(Key.Q)) delta -= _mountOutward;
        if (delta.LengthSquared() > 1e-10f)
        {
            _manualTarget = true;
            _targetActive = true;
            _targetConsumed = false;
            _targetVelocity = delta.Normalized() * 0.055f;
        }
        else if (!_hostCaptured)
        {
            _targetVelocity = Vector3.Zero;
        }
    }

    private void FeedTargetSnapshot()
    {
        if (!_targetActive)
        {
            _plant.Target = null;
            return;
        }
        _plant.Target = new TentaclePlantTargetSnapshot(
            TargetStableId,
            _targetPos,
            _targetVelocity,
            TargetRadius,
            0.25f,
            hostVisible: true,
            // miss 专门验证完整锁向扑击；设为不可抓，避免首扑同 tick
            // 的几何偶合把正确的扑空脚本变成一次短暂 Holding。hit 也
            // 到锁向扑击开始后才开放抓取；被动低速捕获由 core smoke 专测。
            hostGrabbable: _route != "miss" &&
                           (_route != "hit" || _plant.AttackSerial > 0));
    }

    private void ApplyTargetEffect()
    {
        TentaclePlantTargetEffect effect = _plant.TargetEffect;
        bool hasEffect = effect.CaptureStarted || effect.Held || effect.Released ||
                         effect.ConsumeRequested ||
                         effect.PositionCorrection.LengthSquared() > 0f ||
                         effect.VelocityDelta.LengthSquared() > 0f;
        _effectTargetMismatch |= hasEffect && effect.TargetId != TargetStableId;
        if (_hostCaptured && !effect.Held && !effect.Released)
        {
            _holdingContinuityBroken = true;
        }
        _sawCaptureEffect |= effect.CaptureStarted;
        _sawHeldEffect |= effect.Held;
        _sawReleaseEffect |= effect.Released;
        _sawConsumeEffect |= effect.ConsumeRequested;
        _maxEffectMagnitude = Mathf.Max(
            _maxEffectMagnitude,
            Mathf.Max(effect.PositionCorrection.Length(), effect.VelocityDelta.Length()));

        if (effect.CaptureStarted)
        {
            _captureEvents++;
            _captureTick = _captureTick < 0 ? _tick : _captureTick;
            _captureRootDistance = _targetPos.DistanceTo(_plant.Root.Pos);
            _hostCaptured = true;
        }
        if (_targetActive)
        {
            // TargetEffect 是核心对宿主的纯值请求；mock prey 仍由本适配器持有并积分。
            _targetPos += effect.PositionCorrection;
            _targetVelocity += effect.VelocityDelta;
        }
        if (effect.Released)
        {
            _releaseEvents++;
            _releaseTick = _releaseTick < 0 ? _tick : _releaseTick;
            _hostCaptured = false;
        }
        if (effect.ConsumeRequested)
        {
            _consumeEvents++;
            _consumeTick = _consumeTick < 0 ? _tick : _consumeTick;
            _consumeRootDistance = _targetPos.DistanceTo(_plant.Root.Pos);
            _hostCaptured = false;
            _targetConsumed = true;
            _targetActive = false;
            _targetVelocity = Vector3.Zero;
        }
        if (_hostCaptured && _targetActive)
        {
            _maxHeldTargetRootDistance = Mathf.Max(
                _maxHeldTargetRootDistance,
                _targetPos.DistanceTo(_plant.Root.Pos));
            _maxHeldTargetSpeed = Mathf.Max(
                _maxHeldTargetSpeed,
                _targetVelocity.Length());
        }
    }

    private void TrackMetrics()
    {
        string phase = _plant.Phase.ToString();
        _sawWandering |= phase == "Wandering";
        _sawTracking |= phase == "Tracking";
        _sawWindup |= phase == "Windup";
        _sawStriking |= phase == "Striking";
        _sawRecovering |= phase == "Recovering";
        _sawHolding |= phase == "Holding";

        if (phase == "Striking" && _firstStrikeTick < 0)
        {
            _firstStrikeTick = _tick;
        }
        if (phase == "Recovering" && _firstRecoverTick < 0)
        {
            _firstRecoverTick = _tick;
        }
        if (phase == "Holding")
        {
            _firstHoldingTick = _firstHoldingTick < 0 ? _tick : _firstHoldingTick;
            _holdingTicks++;
            _minHeldExtension = Mathf.Min(_minHeldExtension, _plant.Extension);
        }
        if (_firstRecoverTick >= 0 && phase == "Wandering")
        {
            _sawWanderingAfterRecovery = true;
        }

        Vector3 hand = _plant.Hand.Pos;
        _handTravel += hand.DistanceTo(_lastHand);
        _lastHand = hand;
        _maxAttackCharge = Mathf.Max(_maxAttackCharge, _plant.AttackCharge);
        _maxRootError = Mathf.Max(_maxRootError, _plant.Root.Pos.DistanceTo(_rootAnchor));
        _maxGuidePoints = Math.Max(_maxGuidePoints, _plant.GuidePoints.Count);
        _maxBacktrackFrom = Math.Max(_maxBacktrackFrom, _plant.BacktrackFrom);
        _maxTickQueries = Math.Max(_maxTickQueries, _plant.TickQueryCount);
        _peakQueries = Math.Max(_peakQueries, _plant.PeakQueryCount);
        _finalAttackSerial = _plant.AttackSerial;

        _nonFinite |= !IsFinite(_plant.Root.Pos) || !IsFinite(_plant.Root.Vel)
            || !IsFinite(_plant.Hand.Pos) || !IsFinite(_plant.Hand.Vel)
            || !IsFinite(_plant.WanderGoal)
            || !IsFinite(_plant.Outward)
            || !IsFinite(_plant.Tangent)
            || !IsFinite(_plant.Bitangent)
            || !IsFinite(_plant.TargetEffect.PositionCorrection)
            || !IsFinite(_plant.TargetEffect.VelocityDelta)
            || (_targetActive && (!IsFinite(_targetPos) || !IsFinite(_targetVelocity)))
            || !float.IsFinite(_plant.AttackCharge)
            || !float.IsFinite(_plant.CanGrab)
            || !float.IsFinite(_plant.Extension);

        for (int i = 0; i < _plant.Segments.Count; i++)
        {
            TentacleSegmentState segment = _plant.Segments[i];
            _nonFinite |= !IsFinite(segment.Pos) || !IsFinite(segment.Vel) ||
                          !IsFinite(segment.ContactNormal);
            if (_terrain.SpherePenetration(
                    segment.Pos,
                    segment.Radius,
                    out _,
                    out float depth))
            {
                if (depth > _maxResidualPenetration)
                {
                    _maxResidualPenetration = depth;
                    _maxPenetrationSegment = i;
                    _maxPenetrationTick = _tick;
                }
            }
            if (_route == "occluded")
            {
                float extent = (segment.Pos - _mountPoint).Dot(_mountOutward) +
                               segment.Radius;
                _maxOccludedOutwardExtent = Mathf.Max(
                    _maxOccludedOutwardExtent,
                    extent);
            }
        }
        if (_route == "occluded")
        {
            float handExtent = (_plant.Hand.Pos - _mountPoint).Dot(_mountOutward) +
                               _plant.Params.TipRadius;
            _maxOccludedOutwardExtent = Mathf.Max(
                _maxOccludedOutwardExtent,
                handExtent);
        }
        foreach (Vector3 guide in _plant.GuidePoints)
        {
            _nonFinite |= !IsFinite(guide);
        }
    }

    private void RecordDeterminism()
    {
        _hasher.FoldBody(_plant.Body);
        _hasher.Fold(_plant.Outward);
        _hasher.Fold(_plant.Tangent);
        _hasher.Fold(_plant.Bitangent);
        _hasher.Fold(_plant.WanderGoal);
        _hasher.Fold(_plant.AttackCharge);
        _hasher.Fold(_plant.Extension);
        _hasher.Fold(unchecked((ulong)_plant.AttackSerial));
        _hasher.Fold((int)_plant.Phase);
        _hasher.Fold(_plant.PhaseTick);
        _hasher.Fold(_plant.CanGrab);
        _hasher.Fold(_plant.BacktrackFrom);
        _hasher.Fold(_plant.TickQueryCount);
        _hasher.Fold(_plant.PeakQueryCount);
        _hasher.Fold(_plant.HeldTargetId is not null);
        _hasher.Fold(_plant.HeldTargetId ?? 0UL);
        _hasher.Fold(_plant.TargetEffect.TargetId);
        _hasher.Fold(_plant.TargetEffect.CaptureStarted);
        _hasher.Fold(_plant.TargetEffect.Held);
        _hasher.Fold(_plant.TargetEffect.PositionCorrection);
        _hasher.Fold(_plant.TargetEffect.VelocityDelta);
        _hasher.Fold(_plant.TargetEffect.Released);
        _hasher.Fold(_plant.TargetEffect.ConsumeRequested);
        _hasher.Fold(_plant.GuidePoints.Count);
        foreach (Vector3 guide in _plant.GuidePoints)
        {
            _hasher.Fold(guide);
        }
        foreach (TentacleSegmentState segment in _plant.Segments)
        {
            _hasher.Fold(segment.Pos);
            _hasher.Fold(segment.Vel);
            _hasher.Fold(segment.ContactNormal);
            _hasher.Fold(segment.TerrainContact);
        }
        _hasher.Fold(_targetActive);
        _hasher.Fold(_hostCaptured);
        _hasher.Fold(_targetConsumed);
        if (_targetActive)
        {
            _hasher.Fold(_targetPos);
            _hasher.Fold(_targetVelocity);
        }

        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[TENTACLE-PLANT-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
    }

    private int DumpDeterministicResult()
    {
        GD.Print($"[TENTACLE-PLANT-METRIC] route={_route} preset={_preset.Name} mount={_mountName} " +
                 $"handTravel={_handTravel:F3}m rootError={_maxRootError:F4}m " +
                 $"maxPenetration={_maxResidualPenetration:F5}m" +
                 $"@segment{_maxPenetrationSegment}/tick{_maxPenetrationTick} " +
                 $"guide={_maxGuidePoints} backtrack={_maxBacktrackFrom} " +
                 $"queries={_maxTickQueries}/{_peakQueries} attackSerial={_finalAttackSerial} " +
                 $"maxCharge={_maxAttackCharge:F4} " +
                 $"firstStrike={_firstStrikeTick} firstRecover={_firstRecoverTick} " +
                 $"firstHolding={_firstHoldingTick} holdingTicks={_holdingTicks} " +
                 $"effects=capture:{_sawCaptureEffect}/held:{_sawHeldEffect}/" +
                 $"release:{_sawReleaseEffect}/consume:{_sawConsumeEffect} " +
                 $"effectTicks={_captureTick}/{_consumeTick}/{_releaseTick} " +
                 $"effectCounts={_captureEvents}/{_consumeEvents}/{_releaseEvents} " +
                 $"effectMax={_maxEffectMagnitude:F4} " +
                 $"preyRoot={_captureRootDistance:F3}->{_consumeRootDistance:F3}m " +
                 $"heldMax={_maxHeldTargetRootDistance:F3}m/" +
                 $"{_maxHeldTargetSpeed:F3}mpt " +
                 $"minExtension={_minHeldExtension:F4} " +
                 $"occludedExtent={_maxOccludedOutwardExtent:F3}m " +
                 $"consumed={_targetConsumed} " +
                 $"phase={_plant.Phase} held={_plant.HeldTargetId?.ToString() ?? "none"}");

        var failures = new List<string>();
        if (_nonFinite)
        {
            failures.Add("状态出现 NaN/Inf");
        }
        if (_maxRootError > 0.0001f)
        {
            failures.Add($"根部漂移 {_maxRootError:F6}m > 0.0001m");
        }
        if (_peakQueries <= 0 || _peakQueries > 96)
        {
            failures.Add($"查询峰值 {_peakQueries} 不在 1..96");
        }
        if (_maxResidualPenetration > 0.002f)
        {
            failures.Add($"根外段链残留穿透 {_maxResidualPenetration:F5}m > 0.002m");
        }
        if (_effectTargetMismatch)
        {
            failures.Add("TargetEffect.TargetId 与宿主目标稳定 ID 不一致");
        }
        if (_holdingContinuityBroken)
        {
            failures.Add("Capture 与 Release 之间出现未声明 Held 的断帧");
        }
        if (_route != "hit" && _maxEffectMagnitude > 0.0001f)
        {
            failures.Add($"未抓持路线输出了目标位移/速度效果 " +
                         $"({_maxEffectMagnitude:F6})");
        }
        if (_expectHash is ulong expected && _hasher.Value != expected)
        {
            failures.Add($"hash {_hasher.Value:X16} != {expected:X16}");
        }

        switch (_route)
        {
            case "idle":
                if (!_sawWandering || _handTravel < 0.10f)
                {
                    failures.Add($"idle 游走不足（wandering={_sawWandering}, travel={_handTravel:F2}）");
                }
                if (_sawWindup || _sawStriking || _sawHolding || _finalAttackSerial != 0)
                {
                    failures.Add("idle 无猎物却进入攻击/抓持");
                }
                if (_sawCaptureEffect || _sawHeldEffect || _sawReleaseEffect ||
                    _sawConsumeEffect || _captureEvents != 0 ||
                    _consumeEvents != 0 || _releaseEvents != 0)
                {
                    failures.Add("idle 无目标却输出了宿主目标效果");
                }
                break;
            case "hit":
                if (!_sawTracking || !_sawWindup || !_sawStriking || !_sawHolding)
                {
                    failures.Add($"hit 阶段不完整（track/wind/strike/hold=" +
                                 $"{_sawTracking}/{_sawWindup}/{_sawStriking}/{_sawHolding}）");
                }
                if (_holdingTicks < 10)
                {
                    failures.Add($"hit 抓持不足 10 tick（ticks={_holdingTicks}）");
                }
                if (!_targetConsumed && _plant.HeldTargetId != TargetStableId)
                {
                    failures.Add(
                        $"hit 既未保持也未吞入目标（held={_plant.HeldTargetId}）");
                }
                if (!_sawCaptureEffect || !_sawHeldEffect || !_sawConsumeEffect ||
                    !_sawReleaseEffect || !_targetConsumed ||
                    _plant.HeldTargetId is not null ||
                    _plant.Phase == TentaclePlantPhase.Holding)
                {
                    failures.Add($"hit TargetEffect/收尾不完整（capture/held/consume/release/" +
                                 $"host-consumed/final-held/final-phase=" +
                                 $"{_sawCaptureEffect}/{_sawHeldEffect}/" +
                                 $"{_sawConsumeEffect}/{_sawReleaseEffect}/{_targetConsumed}/" +
                                 $"{_plant.HeldTargetId?.ToString() ?? "none"}/{_plant.Phase}）");
                }
                bool orderedEffects = _captureEvents == 1 &&
                                      _consumeEvents == 1 &&
                                      _releaseEvents == 1 &&
                                      _captureTick >= _firstStrikeTick &&
                                      _captureTick < _consumeTick &&
                                      _consumeTick < _releaseTick;
                bool physicalPull = _maxEffectMagnitude > 0.001f &&
                                    _consumeRootDistance <
                                    _captureRootDistance - 0.50f &&
                                    _maxHeldTargetRootDistance <=
                                    _preset.Length * 1.25f + TargetRadius &&
                                    _maxHeldTargetSpeed <= 1.0f &&
                                    _minHeldExtension <= 0.0001f &&
                                    _holdingTicks >= _preset.RetractTicks &&
                                    _holdingTicks <=
                                    _preset.RetractTicks + _preset.ConsumeForceTicks;
                if (!orderedEffects || !physicalPull)
                {
                    failures.Add($"hit 时序/牵引不成立（ordered={orderedEffects}, " +
                                 $"counts={_captureEvents}/{_consumeEvents}/{_releaseEvents}, " +
                                 $"ticks={_captureTick}/{_consumeTick}/{_releaseTick}, " +
                                 $"effect={_maxEffectMagnitude:F4}, preyRoot=" +
                                 $"{_captureRootDistance:F3}->{_consumeRootDistance:F3}, " +
                                 $"heldMax={_maxHeldTargetRootDistance:F3}m/" +
                                 $"{_maxHeldTargetSpeed:F3}mpt, " +
                                 $"extension={_minHeldExtension:F4}, hold={_holdingTicks}）");
                }
                break;
            case "miss":
                if (!_missYanked || !_sawWindup || !_sawStriking || !_sawRecovering)
                {
                    failures.Add($"miss 未覆盖蓄势/扑出/回收（yank/wind/strike/recover=" +
                                 $"{_missYanked}/{_sawWindup}/{_sawStriking}/{_sawRecovering}）");
                }
                if (!_sawWanderingAfterRecovery)
                {
                    failures.Add("miss 回收后未回到 Wandering");
                }
                if (_sawHolding || _sawCaptureEffect || _sawHeldEffect ||
                    _sawReleaseEffect || _sawConsumeEffect ||
                    _plant.HeldTargetId is not null)
                {
                    failures.Add("miss 错误抓住已移走的目标");
                }
                if (_finalAttackSerial != 1)
                {
                    failures.Add($"miss 扑空后出现额外扑击（serial={_finalAttackSerial}）");
                }
                break;
            case "occluded":
                if (_sawHolding || _sawCaptureEffect || _sawHeldEffect ||
                    _sawReleaseEffect || _sawConsumeEffect ||
                    _plant.HeldTargetId is not null)
                {
                    failures.Add("occluded 隔墙错误抓住目标");
                }
                if (!_sawTracking || _maxAttackCharge > 0.0001f ||
                    _sawWindup || _sawStriking || _finalAttackSerial != 0)
                {
                    failures.Add($"occluded 视线门失效（track={_sawTracking}, " +
                                 $"charge={_maxAttackCharge:F4}, wind/strike=" +
                                 $"{_sawWindup}/{_sawStriking}, serial={_finalAttackSerial}）");
                }
                // 大遮挡板近面位于 mount 局部 outward=2.8m；用完整段半径
                // 检查所有可见链和手都留在根侧。
                if (_maxOccludedOutwardExtent > 2.802f)
                {
                    failures.Add($"occluded 段链越过封闭板近面 " +
                                 $"({_maxOccludedOutwardExtent:F4}m > 2.802m)");
                }
                break;
        }

        bool pass = failures.Count == 0;
        GD.Print(pass
            ? "[TENTACLE-PLANT-RESULT] PASS"
            : $"[TENTACLE-PLANT-RESULT] FAIL：{string.Join("; ", failures)}");
        return pass ? 0 : 1;
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

        _renderer.Draw(
            (float)Engine.GetPhysicsInterpolationFraction(),
            _targetActive,
            _targetPos,
            TargetRadius);
        Vector3 targetOffset = _targetPos - _mountPoint;
        Vector3 targetLocal = new(
            targetOffset.Dot(_mountOutward),
            targetOffset.Dot(_mountTangent),
            targetOffset.Dot(_mountBitangent));
        _hud?.UpdateStatus(
            _preset.Name,
            _mountName,
            _route,
            _plant.Phase.ToString(),
            _plant.AttackCharge,
            _plant.CanGrab,
            _plant.Extension,
            _plant.HeldTargetId,
            _targetActive,
            _plant.TargetStatus,
            targetLocal,
            _targetPos.DistanceTo(_plant.Root.Pos),
            _plant.Params.Length,
            _hostCaptured,
            _targetConsumed,
            _plant.TargetEffect.CaptureStarted,
            _plant.TargetEffect.Held,
            _plant.TargetEffect.Released,
            _plant.TargetEffect.ConsumeRequested,
            _plant.GuidePoints.Count,
            _plant.BacktrackFrom,
            _plant.TickQueryCount,
            _plant.PeakQueryCount,
            _plant.AttackSerial);
    }

    private void UpdateShowcaseCamera()
    {
        // 依预设工作半径居中，避免 original/hunter 扑击时手端与猎物跑出画面。
        float focusDistance = Mathf.Min(3.2f, _preset.Length * 0.42f);
        Vector3 focus = _mountPoint + _mountOutward * focusDistance;
        Vector3 offset;
        if (_mountName == "wall")
        {
            // 墙面的局部 bitangent 恰为世界 Up；从下前方看，避免相机跑到顶板上方。
            offset = _mountTangent * 5.2f - _mountBitangent * 2.4f +
                _mountOutward * 5.4f;
        }
        else
        {
            offset = _mountTangent * 5.2f + _mountBitangent * 6.4f +
                _mountOutward * 2.7f;
            if (_mountName == "ceiling")
            {
                offset += Vector3.Down * 1.7f;
            }
        }
        _camera.GlobalPosition = focus + offset;
        _camera.Fov = 60f;
        _camera.LookAt(focus, Mathf.Abs((focus - _camera.GlobalPosition).Normalized().Dot(Vector3.Up)) > 0.96f
            ? Vector3.Forward
            : Vector3.Up);
        _fillLight.GlobalPosition = focus + offset * 0.35f;
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
                    _cameraPitch - motion.Relative.Y * CameraMouseSensitivity,
                    -1.5f,
                    1.5f);
                _camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0f);
            }
            return;
        }

        if (Deterministic || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        switch (key.PhysicalKeycode)
        {
            case Key.Space:
                _plant.ReleaseHeldTarget();
                ResetTarget();
                break;
            case Key.Key1:
            case Key.Key2:
            case Key.Key3:
                SelectPreset((int)(key.PhysicalKeycode - Key.Key1));
                break;
            case Key.F:
                SelectMount(0);
                break;
            case Key.G:
                SelectMount(1);
                break;
            case Key.H:
                SelectMount(2);
                break;
            case Key.Key4:
            case Key.Key5:
            case Key.Key6:
            case Key.Key7:
                SelectRoute((int)(key.PhysicalKeycode - Key.Key4));
                break;
        }
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Length)
        {
            return;
        }
        SpawnPlant(_presets[index], _mountName);
        SyncHudPickers();
        GD.Print($"[TENTACLE-PLANT-SANDBOX] preset -> {_preset.Name}");
    }

    private void SelectMount(int index)
    {
        if (index < 0 || index >= MountNames.Length)
        {
            return;
        }
        SpawnPlant(_preset, MountNames[index]);
        SyncHudPickers();
        GD.Print($"[TENTACLE-PLANT-SANDBOX] mount -> {_mountName}");
    }

    private void SelectRoute(int index)
    {
        if (index < 0 || index >= RouteNames.Length)
        {
            return;
        }
        _route = RouteNames[index];
        ConfigureOccluder(_route == "occluded");
        _plant.ReleaseHeldTarget();
        ResetTarget();
        ResetMetrics();
        SyncHudPickers();
        GD.Print($"[TENTACLE-PLANT-SANDBOX] route -> {_route}");
    }

    private void SyncHudPickers()
    {
        int preset = Array.FindIndex(_presets, p => p.Name == _preset.Name);
        int mount = Array.IndexOf(MountNames, _mountName);
        int route = Array.IndexOf(RouteNames, _route);
        _hud?.Sync(preset, mount, route);
    }

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--plant-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--plant-determinism=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0)
                    {
                        throw new FormatException("determinism tick 必须为正");
                    }
                }
                else if (argument.StartsWith("--plant-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(
                        argument["--plant-tps=".Length..],
                        CultureInfo.InvariantCulture);
                    if (_requestedTps <= 0)
                    {
                        throw new FormatException("tps 必须为正");
                    }
                }
                else if (argument.StartsWith("--plant-preset=", StringComparison.Ordinal))
                {
                    string value = argument["--plant-preset=".Length..].ToLowerInvariant();
                    _presetName = value switch
                    {
                        "original" => PresetNames[0],
                        "short" => PresetNames[1],
                        "hunter" => PresetNames[2],
                        _ when Array.IndexOf(PresetNames, value) >= 0 => value,
                        _ => throw new FormatException("preset 只接受 original|short|hunter 或完整稳定 ID"),
                    };
                }
                else if (argument.StartsWith("--plant-mount=", StringComparison.Ordinal))
                {
                    _mountName = argument["--plant-mount=".Length..].ToLowerInvariant();
                    if (Array.IndexOf(MountNames, _mountName) < 0)
                    {
                        throw new FormatException("mount 只接受 floor|wall|ceiling");
                    }
                }
                else if (argument.StartsWith("--plant-route=", StringComparison.Ordinal))
                {
                    _route = argument["--plant-route=".Length..].ToLowerInvariant();
                    if (Array.IndexOf(RouteNames, _route) < 0)
                    {
                        throw new FormatException("route 只接受 idle|hit|miss|occluded");
                    }
                }
                else if (argument.StartsWith("--plant-seed=", StringComparison.Ordinal))
                {
                    string text = argument["--plant-seed=".Length..];
                    _instanceSeed = ParseUInt64(text);
                }
                else if (argument.StartsWith("--plant-target-local=", StringComparison.Ordinal))
                {
                    _targetLocalOverride = ParseLocalVector(
                        argument["--plant-target-local=".Length..]);
                }
                else if (argument.StartsWith("--plant-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(
                        argument["--plant-perturb=".Length..],
                        CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb))
                    {
                        throw new FormatException("perturb 必须有限");
                    }
                }
                else if (argument.StartsWith("--plant-expect-hash=", StringComparison.Ordinal))
                {
                    string text = argument["--plant-expect-hash=".Length..];
                    _expectHash = ParseUInt64(text, hexadecimalByDefault: true);
                }
                else if (argument.StartsWith("--plant-", StringComparison.Ordinal))
                {
                    throw new FormatException($"未知参数 {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                GD.PushError($"[TENTACLE-PLANT-CLI] {argument}: {exception.Message}");
                return false;
            }
        }

        if (_expectHash is not null && _determinismTicks <= 0)
        {
            GD.PushError("[TENTACLE-PLANT-CLI] --expect-hash 需要 --determinism");
            return false;
        }
        return true;
    }

    private Vector3 TargetForRoute(string route)
    {
        if (_targetLocalOverride is { } local)
        {
            return LocalPoint(local.X, local.Y, local.Z);
        }
        return route switch
        {
            // 初生手位于 outward≈2.675m；遮挡板从 2.8m 开始，目标放在其后，
            // 避免“手出生在墙后、tick 1 被动触碰”的假失败。
            "occluded" => LocalPoint(4.20f, 0.10f, 0.08f),
            // 把演示猎物放在各预设约 65% 的工作半径处：既能展示长距离锁向
            // 扑击与回收，又不会让 short 越出自身攻击球。
            _ => LocalPoint(_preset.Length * 0.65f, 0.90f, 0.45f),
        };
    }

    private Vector3 LocalPoint(float outward, float tangent, float bitangent) =>
        _mountPoint
        + _mountOutward * outward
        + _mountTangent * tangent
        + _mountBitangent * bitangent;

    private static Vector3 ParseLocalVector(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new FormatException(
                "target-local 必须是 outward,tangent,bitangent 三个逗号分隔数值");
        }
        var value = new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
        if (!IsFinite(value))
        {
            throw new FormatException("target-local 必须全部有限");
        }
        return value;
    }

    private static ulong ParseUInt64(string text, bool hexadecimalByDefault = false)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return ulong.Parse(
            text,
            hexadecimalByDefault ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture);
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
