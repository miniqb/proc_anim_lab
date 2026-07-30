using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.CicadaSandbox;

/// <summary>
/// 独立 Cicada 白盒：40 tick 固定序驱动核心，渲染帧只做插值和自由摄像机。
/// 交互与无头专项矩阵都只经过本场景，不改 Lizard 沙盒或其输入语义。
/// </summary>
public partial class CicadaSandboxWorld : Node3D
{
    private const float TickDt = 0.025f;
    private static readonly Vector3[] FlightRoute =
    {
        new(4.2f, 2.2f, 2.8f),
        new(-1.4f, 4.3f, 1.2f),
        new(-5.1f, 2.6f, 3.1f),
        new(-1.2f, 1.3f, -1.7f),
        new(3.8f, 3.7f, -2.4f),
        new(0.7f, 2.1f, 4.4f),
    };

    [Export] public float GravityMps2 = 36f;
    [Export] public int ConstraintIterations = 3;
    [Export] public float CameraFlySpeed = 7f;
    [Export] public float CameraMouseSensitivity = 0.003f;

    private readonly RaycastTerrainQuery _terrain = new();
    private readonly CicadaRenderer _renderer = new();
    private readonly DeterminismHasher _hasher = new();

    private CicadaLocomotionController _cicada = null!;
    private CicadaParams[] _presets = Array.Empty<CicadaParams>();
    private CicadaParams _preset = null!;
    private CicadaSandboxHud? _hud;
    private Camera3D _camera = null!;
    private OmniLight3D _warmFill = null!;
    private Vector3 _gravityPerTick;
    private Vector3 _spawn;
    private Vector3 _spawnCenter;
    private Vector3 _lastCenter;
    private Vector3 _desiredDirection;
    private Vector2? _pendingPerchPick;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cameraFlying;
    private bool _pendingSpace;
    private bool _rendererBuilt;
    private bool _fatal;
    private long _tick;

    // 无头专项入口。
    private int _determinismTicks;
    private int _requestedTps = 40;
    private string _presetName = "light";
    private string _route = "flight";
    private float _perturb;
    private ulong? _expectHash;
    private int _flightWaypoint;
    private int _waypointsReached;
    private bool _perchRequestAccepted;
    private bool _takeoffRequested;
    private bool _chargeRequested;
    private TerrainHit _requestedPerch;

    // 场景行为门。
    private bool _nonFinite;
    private bool _sawPerched;
    private bool _sawWindup;
    private bool _sawDash;
    private bool _sawStunned;
    private bool _sawChargeTerrainContact;
    private long _firstPerchedTick = -1;
    private long _takeoffTick = -1;
    private int _currentPerchedRun;
    private int _maxPerchedRun;
    private float _travelDistance;
    private float _maxConstraintDeviation;
    private float _endConstraintDeviation;
    private float _maxPoseError;
    private float _maxTakeoffDistance;

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
            GD.Print("[CICADA-RESULT] FAIL：命令行参数无效");
            GetTree().Quit(2);
            return;
        }

        Engine.PhysicsTicksPerSecond = _requestedTps;
        if (Deterministic)
        {
            Engine.MaxPhysicsStepsPerFrame = 100;
        }

        _presets = CicadaFactory.AllPresets();
        _preset = CicadaFactory.ByName(_presetName);
        _spawn = SpawnForRoute(_route);
        SpawnCicada(_spawn, Vector3.Forward, _preset);

        if (_perturb != 0f)
        {
            _cicada.Front.Pos += Vector3.Right * _perturb;
            _cicada.Front.LastPos = _cicada.Front.Pos;
            _spawnCenter = BodyCenter();
            _lastCenter = _spawnCenter;
        }

        // 确定性路线也装配表现层：物理哈希不读取 renderer，但 Godot 矩阵因此能同时
        // 冒烟 ImmediateMesh/Basis/材质路径，并可用 --write-movie 留存可复现视觉证据。
        _renderer.Build(this, _cicada, _preset);
        _rendererBuilt = true;

        if (!Deterministic)
        {
            string[] names = Array.ConvertAll(_presets, p => p.Name);
            _hud = new CicadaSandboxHud();
            _hud.Build(this, names, SelectPreset);
            _hud.SyncPreset(Array.FindIndex(_presets, p => p.Name == _preset.Name));
        }

        GD.Print($"[CICADA-SANDBOX] ready tps={Engine.PhysicsTicksPerSecond} " +
                 $"preset={_preset.Name} route={_route} determinism={(Deterministic ? "on" : "off")}");
    }

    private void SpawnCicada(Vector3 origin, Vector3 forward, CicadaParams parameters)
    {
        _preset = parameters;
        _cicada = CicadaFactory.CreateController(origin, forward, parameters);
        _cicada.Body.ConstraintIterations = ConstraintIterations;
        _spawnCenter = BodyCenter();
        _lastCenter = _spawnCenter;

        if (_rendererBuilt)
        {
            _renderer.Build(this, _cicada, _preset);
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
            DriveDeterministicRoute();
        }
        else
        {
            SampleFlightInput();
            ProcessPendingInteraction();
        }

        _cicada.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
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

    private void SampleFlightInput()
    {
        if (WantCameraFly)
        {
            _desiredDirection = Vector3.Zero;
            _cicada.MoveTarget = null;
            _cicada.MoveDir = Vector3.Zero;
            _cicada.RunSpeed = 0f;
            return;
        }

        Vector3 direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction.Z -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction.Z += 1f;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction.Y += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction.Y -= 1f;

        _desiredDirection = direction.LengthSquared() > 1e-10f ? direction.Normalized() : Vector3.Zero;
        _cicada.MoveTarget = null;
        _cicada.MoveDir = _desiredDirection;
        _cicada.RunSpeed = _desiredDirection == Vector3.Zero ? 0f : 1f;
    }

    private void ProcessPendingInteraction()
    {
        if (_pendingPerchPick is Vector2 mouse)
        {
            _pendingPerchPick = null;
            Vector3 from = _camera.ProjectRayOrigin(mouse);
            Vector3 to = from + _camera.ProjectRayNormal(mouse) * 100f;
            if (_terrain.Raycast(from, to, out TerrainHit hit) && _cicada.RequestPerch(in hit))
            {
                GD.Print($"[CICADA-SANDBOX] perch requested collider={hit.ColliderId} " +
                         $"normal=({hit.Normal.X:F2},{hit.Normal.Y:F2},{hit.Normal.Z:F2})");
            }
        }

        if (!_pendingSpace)
        {
            return;
        }
        _pendingSpace = false;

        string mode = _cicada.Mode.ToString();
        if (mode is "Perched" or "Landing")
        {
            Vector3 takeoff = PerchNormal();
            if (takeoff.LengthSquared() < 1e-10f)
            {
                takeoff = _cicada.Up;
            }
            takeoff += _cicada.Forward * 0.25f;
            _cicada.TakeOff(takeoff.Normalized());
            return;
        }

        Vector3 chargeDirection = _desiredDirection.LengthSquared() > 1e-10f
            ? _desiredDirection
            : _cicada.Forward;
        _cicada.TryStartCharge(chargeDirection);
    }

    private void DriveDeterministicRoute()
    {
        _cicada.MoveTarget = null;
        _cicada.MoveDir = Vector3.Zero;
        _cicada.RunSpeed = 0f;

        switch (_route)
        {
            case "flight":
                DriveFlightRoute();
                break;
            case "wall":
                RequestScriptedPerch(new Vector3(0f, 2.8f, -5.4f),
                    new Vector3(0f, 2.8f, -9.2f));
                break;
            case "ceiling":
                RequestScriptedPerch(new Vector3(-3.7f, 3f, -4.6f),
                    new Vector3(-3.7f, 6.6f, -4.6f));
                break;
            case "takeoff":
                DriveTakeoffRoute();
                break;
            case "charge-wall":
                DriveChargeWallRoute();
                break;
        }
    }

    private void DriveFlightRoute()
    {
        Vector3 target = FlightRoute[_flightWaypoint];
        _cicada.MoveTarget = target;
        _cicada.RunSpeed = 1f;
        if (_cicada.AtMoveTarget || (target - BodyCenter()).Length() < 0.38f)
        {
            _flightWaypoint = (_flightWaypoint + 1) % FlightRoute.Length;
            _waypointsReached++;
            _cicada.MoveTarget = FlightRoute[_flightWaypoint];
        }
    }

    private void DriveTakeoffRoute()
    {
        if (!_perchRequestAccepted)
        {
            RequestScriptedPerch(new Vector3(3f, 2f, 2.5f), new Vector3(3f, -1f, 2.5f));
            return;
        }

        if (!_takeoffRequested && _firstPerchedTick >= 0 && _tick - _firstPerchedTick >= 20)
        {
            _takeoffRequested = true;
            _takeoffTick = _tick;
            _cicada.TakeOff(_requestedPerch.Normal);
        }

        if (_takeoffRequested)
        {
            Vector3 direction = (_requestedPerch.Normal + Vector3.Forward * 0.18f).Normalized();
            _cicada.MoveDir = direction;
            _cicada.RunSpeed = 1f;
        }
    }

    private void DriveChargeWallRoute()
    {
        _cicada.MoveDir = Vector3.Forward;
        _cicada.RunSpeed = 1f;
        if (!_chargeRequested && _tick >= 12)
        {
            _chargeRequested = _cicada.TryStartCharge(Vector3.Forward);
        }
    }

    private void RequestScriptedPerch(Vector3 from, Vector3 to)
    {
        if (_perchRequestAccepted || !_terrain.Raycast(from, to, out TerrainHit hit))
        {
            return;
        }
        if (_cicada.RequestPerch(in hit))
        {
            _requestedPerch = hit;
            _perchRequestAccepted = true;
        }
    }

    private void TrackMetrics()
    {
        Vector3 center = BodyCenter();
        _travelDistance += center.DistanceTo(_lastCenter);
        _lastCenter = center;
        _endConstraintDeviation = _cicada.Body.CurrentMaxDeviation();
        _maxConstraintDeviation = Mathf.Max(_maxConstraintDeviation, _endConstraintDeviation);

        Vector3 f = _cicada.Forward;
        Vector3 u = _cicada.Up;
        Vector3 r = _cicada.Right;
        float poseError = Mathf.Max(
            Mathf.Max(Mathf.Abs(f.Dot(u)), Mathf.Abs(f.Dot(r))),
            Mathf.Max(Mathf.Abs(u.Dot(r)),
                Mathf.Max(Mathf.Abs(f.Length() - 1f),
                    Mathf.Max(Mathf.Abs(u.Length() - 1f), Mathf.Abs(r.Length() - 1f)))));
        _maxPoseError = Mathf.Max(_maxPoseError, poseError);

        _nonFinite |= !IsFinite(f) || !IsFinite(u) || !IsFinite(r)
            || !float.IsFinite(_cicada.FlightPower)
            || !float.IsFinite(_cicada.Bank)
            || !float.IsFinite(_cicada.FlapPhase);
        bool terrainContact = false;
        foreach (BodyChunk chunk in _cicada.Body.Chunks)
        {
            _nonFinite |= !IsFinite(chunk.Pos) || !IsFinite(chunk.Vel);
            terrainContact |= chunk.TerrainContact;
        }
        foreach (CicadaWingState wing in _cicada.Wings)
        {
            _nonFinite |= !IsFinite(wing.Pos) || !IsFinite(wing.Tip);
        }
        foreach (CicadaTentacleState tentacle in _cicada.Tentacles)
        {
            _nonFinite |= !IsFinite(tentacle.Pos) || !IsFinite(tentacle.Vel);
        }

        string mode = _cicada.Mode.ToString();
        string charge = _cicada.ChargePhase.ToString();
        if (mode == "Perched")
        {
            _sawPerched = true;
            _firstPerchedTick = _firstPerchedTick < 0 ? _tick : _firstPerchedTick;
            _currentPerchedRun++;
            _maxPerchedRun = Math.Max(_maxPerchedRun, _currentPerchedRun);
        }
        else
        {
            _currentPerchedRun = 0;
        }
        _sawStunned |= mode == "Stunned";
        _sawWindup |= charge == "Windup";
        _sawDash |= charge == "Dash";
        _sawChargeTerrainContact |= _sawDash && terrainContact;

        if (_takeoffTick >= 0 && _tick - _takeoffTick <= 30)
        {
            float distance = (center - _requestedPerch.Point).Dot(_requestedPerch.Normal);
            _maxTakeoffDistance = Mathf.Max(_maxTakeoffDistance, distance);
        }
    }

    private void RecordDeterminism()
    {
        _hasher.FoldBody(_cicada.Body);
        _hasher.Fold(_cicada.Forward);
        _hasher.Fold(_cicada.Up);
        _hasher.Fold(_cicada.Right);
        _hasher.Fold(new Vector3(_cicada.FlightPower, _cicada.Bank, _cicada.FlapPhase));
        foreach (CicadaWingState wing in _cicada.Wings)
        {
            _hasher.Fold(wing.Pos);
            _hasher.Fold(wing.Tip);
        }
        foreach (CicadaTentacleState tentacle in _cicada.Tentacles)
        {
            _hasher.Fold(tentacle.Pos);
            _hasher.Fold(tentacle.Vel);
        }

        if (_tick % 100 == 0 || _tick >= _determinismTicks)
        {
            GD.Print($"[CICADA-DET] tick={_tick} hash={_hasher.Value:X16}");
        }
    }

    private int DumpDeterministicResult()
    {
        GD.Print($"[CICADA-METRIC] route={_route} preset={_preset.Name} " +
                 $"travel={_travelDistance:F3}m waypoints={_waypointsReached} " +
                 $"endConstraintDev={_endConstraintDeviation:F5}m " +
                 $"maxConstraintDev={_maxConstraintDeviation:F5}m maxPoseError={_maxPoseError:E2} " +
                 $"firstPerch={_firstPerchedTick} maxPerchedRun={_maxPerchedRun} " +
                 $"takeoffDistance={_maxTakeoffDistance:F3}m " +
                 $"windup={_sawWindup} dash={_sawDash} stunned={_sawStunned} " +
                 $"chargeContact={_sawChargeTerrainContact}");

        var failures = new List<string>();
        if (_nonFinite)
        {
            failures.Add("状态出现 NaN/Inf");
        }
        if (_endConstraintDeviation >= 0.02f)
        {
            failures.Add($"连接末偏差 {_endConstraintDeviation:F5}m >= 0.02m");
        }
        if (_maxPoseError >= 1e-4f)
        {
            failures.Add($"姿态正交误差 {_maxPoseError:E2} >= 1e-4");
        }
        if (_expectHash is ulong expected && _hasher.Value != expected)
        {
            failures.Add($"hash {_hasher.Value:X16} != {expected:X16}");
        }

        switch (_route)
        {
            case "flight":
                if (_travelDistance < 4f || _waypointsReached < 1)
                {
                    failures.Add($"自由飞行覆盖不足（travel={_travelDistance:F2}, waypoints={_waypointsReached}）");
                }
                break;
            case "wall":
            case "ceiling":
                if (!_perchRequestAccepted || !_sawPerched || _firstPerchedTick > 160)
                {
                    failures.Add($"停驻未在 160 tick 内完成（accepted={_perchRequestAccepted}, first={_firstPerchedTick}）");
                }
                if (_maxPerchedRun < 120)
                {
                    failures.Add($"停驻稳定仅 {_maxPerchedRun} tick（需要 >=120）");
                }
                break;
            case "takeoff":
                if (!_sawPerched || !_takeoffRequested)
                {
                    failures.Add("起飞场景未先完成停驻并调用 TakeOff");
                }
                if (_maxTakeoffDistance < 0.5f)
                {
                    failures.Add($"起飞后 30 tick 离面仅 {_maxTakeoffDistance:F3}m（需要 >=0.5m）");
                }
                break;
            case "charge-wall":
                if (!_sawWindup || !_sawDash)
                {
                    failures.Add("Charge 未覆盖 Windup 与 Dash");
                }
                if (!_sawChargeTerrainContact || !_sawStunned)
                {
                    failures.Add($"高速撞墙未触发接触+Stunned（contact={_sawChargeTerrainContact}, stunned={_sawStunned}）");
                }
                break;
        }

        bool pass = failures.Count == 0;
        GD.Print(pass
            ? "[CICADA-RESULT] PASS"
            : $"[CICADA-RESULT] FAIL：{string.Join("; ", failures)}");
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
        if (_rendererBuilt)
        {
            _renderer.Draw((float)Engine.GetPhysicsInterpolationFraction());
        }
        _hud?.UpdateStatus(_preset.Name, _cicada.Mode.ToString(), _cicada.ChargePhase.ToString(),
            _cicada.FlightPower, Mathf.RadToDeg(_cicada.Bank), _cicada.ActivePerch is not null);
    }

    /// <summary>
    /// 专项路线的可复现跟拍机位。它只读身体位置，不参与 tick，既让离线留图看清翼面/触须，
    /// 也避免固定全景机位把 0.35m 的 Cicada 缩成几个像素。
    /// </summary>
    private void UpdateShowcaseCamera()
    {
        Vector3 center = BodyCenter();
        Vector3 offset = _route == "ceiling"
            ? new Vector3(1.35f, -0.85f, 2.05f)
            : new Vector3(1.45f, 0.80f, 2.15f);
        _camera.GlobalPosition = center + offset;
        _camera.Fov = 42f;
        _camera.LookAt(center, Vector3.Up);
        _warmFill.GlobalPosition = center + offset * 0.45f;
        _warmFill.OmniRange = 6f;
        _warmFill.LightEnergy = 3.2f;
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
            _pendingPerchPick = mouseButton.Position;
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (key.PhysicalKeycode == Key.Space)
        {
            _pendingSpace = true;
        }
        else if (key.PhysicalKeycode is Key.Key1 or Key.Key2)
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
        Vector3 respawn = BodyCenter() + Vector3.Up * 0.5f;
        SpawnCicada(respawn, _cicada.Forward, _presets[index]);
        _hud?.SyncPreset(index);
        GD.Print($"[CICADA-SANDBOX] preset -> {_presets[index].Name}");
    }

    private bool ParseArguments()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            try
            {
                if (argument.StartsWith("--cicada-determinism=", StringComparison.Ordinal))
                {
                    _determinismTicks = int.Parse(
                        argument["--cicada-determinism=".Length..], CultureInfo.InvariantCulture);
                    if (_determinismTicks <= 0)
                    {
                        throw new FormatException("determinism tick 必须为正");
                    }
                }
                else if (argument.StartsWith("--cicada-tps=", StringComparison.Ordinal))
                {
                    _requestedTps = int.Parse(
                        argument["--cicada-tps=".Length..], CultureInfo.InvariantCulture);
                    if (_requestedTps <= 0)
                    {
                        throw new FormatException("tps 必须为正");
                    }
                }
                else if (argument.StartsWith("--cicada-preset=", StringComparison.Ordinal))
                {
                    _presetName = argument["--cicada-preset=".Length..].ToLowerInvariant();
                    if (_presetName is not ("light" or "dark"))
                    {
                        throw new FormatException("preset 只接受 light|dark");
                    }
                }
                else if (argument.StartsWith("--cicada-route=", StringComparison.Ordinal))
                {
                    _route = argument["--cicada-route=".Length..].ToLowerInvariant();
                    if (_route is not ("flight" or "wall" or "ceiling" or "takeoff" or "charge-wall"))
                    {
                        throw new FormatException("未知 cicada route");
                    }
                }
                else if (argument.StartsWith("--cicada-perturb=", StringComparison.Ordinal))
                {
                    _perturb = float.Parse(
                        argument["--cicada-perturb=".Length..], CultureInfo.InvariantCulture);
                    if (!float.IsFinite(_perturb))
                    {
                        throw new FormatException("perturb 必须有限");
                    }
                }
                else if (argument.StartsWith("--cicada-expect-hash=", StringComparison.Ordinal))
                {
                    string text = argument["--cicada-expect-hash=".Length..];
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[2..];
                    }
                    _expectHash = ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else if (argument.StartsWith("--cicada-", StringComparison.Ordinal))
                {
                    throw new FormatException($"未知参数 {argument}");
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                GD.PushError($"[CICADA-CLI] {argument}: {exception.Message}");
                return false;
            }
        }
        return true;
    }

    private static Vector3 SpawnForRoute(string route) => route switch
    {
        "wall" => new Vector3(0f, 2.8f, -5.2f),
        "ceiling" => new Vector3(-3.7f, 3.3f, -4.2f),
        "takeoff" => new Vector3(3f, 1.4f, 2.5f),
        "charge-wall" => new Vector3(2f, 2.7f, -5.3f),
        _ => new Vector3(0.5f, 2.4f, 2.8f),
    };

    private Vector3 BodyCenter() => _cicada.Front.Pos.Lerp(_cicada.Rear.Pos, 0.5f);

    private Vector3 PerchNormal() => _cicada.ActivePerch?.Normal ?? Vector3.Zero;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
