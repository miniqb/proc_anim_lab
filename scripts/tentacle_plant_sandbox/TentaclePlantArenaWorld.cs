using System;
using System.Globalization;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Species.TentaclePlant;
using ProcAnim.Core.Terrain;
using ProcAnimLab.Render;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 拟态草（肉质触手怪）吊顶伏击竞技场：第一人称亲历「天花板上那盏灯是活的」。
/// 探索场景，不进矩阵（≙ rat_arena 纪律：命令行零参数、全 Inspector 导出、
/// 默认值是唯一真相源、生效值打在 ready 行）。
///
/// 玩法弧线：怪物出生即伪装成房间正中天花板的吸顶灯（内核 DisguiseIntent + 慢速
/// DisguiseAmount）→ 玩家走进触发半径，伪装态加速充能十几 tick 突袭咬人（象征性
/// 伤害：BITTEN 计数 + 镜头 kick + 推离，快咬弹开不抓持）→ 玩家留在附近则常态
/// 循环攻击，每次突刺后压时间戳冷却（**冷却不占相位**——R19 教训），冷却中喂
/// HostVisible=false 的目标：不充能但仍 Tracking = 闭嘴、朝向玩家、扭动身体 →
/// 玩家退出脱离半径并持续 RearmDelay 后回伪装，等下一次伏击。
///
/// 宿主相位只有 Ambush/Engage 两个；攻击本身是内核涌现的（充能满自动突刺），
/// 宿主只用 Target 快照的 HostVisible 开关充能门。玩家恒 HostGrabbable=false，
/// 咬中判定宿主自做（Striking 期 Hand 距玩家胸心 ≤ BiteRadius，每 AttackSerial
/// 只结算一次）——绕开 PositionCorrection 全量交付语义与自驱玩家打架的已知坑。
/// </summary>
public partial class TentaclePlantArenaWorld : Node3D
{
    private const double TickDt = 0.025;
    private const float TicksPerSecond = 40f;
    private const ulong PlayerStableId = 0x504C414E544C414DUL;
    // 玩家喂内核的目标球：球心取眼位（= 相机高度，ArenaFirstPersonPlayer.EyePosition，
    // 脚底 +1.55m）——吊顶伏击者要冲着脸咬，不是腰腹；半径沿用胶囊半径。
    private const float PlayerChunkRadius = 0.35f;

    [ExportGroup("Arena")]
    [Export] public string DefaultPreset = "lurker";
    [Export(PropertyHint.Range, "40,1000,1")] public int HostPhysicsTps = 40;
    [Export] public float GravityMps2 = 36f;
    [Export(PropertyHint.Range, "12,40,0.5")] public float ArenaWidth = 16f;
    [Export(PropertyHint.Range, "12,40,0.5")] public float ArenaDepth = 16f;
    [Export(PropertyHint.Range, "1,8,0.5")] public float SpawnEndInset = 4f;

    [ExportGroup("Behavior")]
    /// <summary>伏击触发半径（玩家水平距挂点）：走进这圈才开始被伪装态感知。</summary>
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float AmbushTriggerRadius = 2.2f;
    /// <summary>交战脱离半径（迟滞 &gt; 触发半径）：超出后起脱离计时。</summary>
    [Export(PropertyHint.Range, "1,12,0.1")] public float EngageReleaseRadius = 4.5f;
    /// <summary>持续远离这么久后恢复伪装。</summary>
    [Export(PropertyHint.Range, "0.5,15,0.1")] public float RearmDelaySeconds = 3.0f;
    /// <summary>每次突刺后的攻击冷却（时间戳制，冷却中闭嘴盯人）。</summary>
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float AttackCooldownSeconds = 2.5f;

    [ExportGroup("Bite")]
    /// <summary>咬合半径：Striking 期 Hand 到玩家胸心的判距（≈ 嘴 + 玩家胶囊）。</summary>
    [Export(PropertyHint.Range, "0.2,2,0.05")] public float BiteRadius = 0.55f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float BiteShoveSpeed = 2.5f;
    [Export(PropertyHint.Range, "0.2,5,0.1")] public float BittenPromptSeconds = 1.2f;

    [ExportGroup("Camera Kick")]
    [Export(PropertyHint.Range, "0,4,0.05")] public float KickDegrees = 0.8f;
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float KickOffsetMeters = 0.012f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float KickFrequencyHz = 11f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float KickDecaySeconds = 0.4f;

    private enum HostPhase
    {
        Ambush,
        Engage,
    }

    private readonly RaycastTerrainQuery _terrain = new();
    private BoxRoomArenaBuilder _arena = null!;
    private TentaclePlantParams _preset = null!;
    private TentaclePlantController _plant = null!;
    private TentaclePlantFormalRenderer? _formal;
    private ArenaFirstPersonPlayer _player = null!;
    private TentaclePlantArenaHud _hud = null!;
    private OmniLight3D _lampLight = null!;

    private Vector3 _gravityPerTick;
    private Vector3 _mountPoint;
    private double _tickAccumulator;
    private long _tick;
    private bool _fatal;

    private HostPhase _phase = HostPhase.Ambush;
    private long _attackReadyTick;
    private long _rearmAtTick = -1;
    private long _prevAttackSerial;
    private long _lastBiteSerial;
    private int _biteCount;
    private long _bittenPromptUntilTick = -1;
    private Vector3 _playerAim;
    private Vector3 _prevPlayerAim;
    private bool _playerAimInitialized;
    private string _toastText = "";
    private float _toastTtl;

    // 镜头 kick（纯渲染侧化妆：多条不可通约正弦 × 冲量包络，无 RNG——RatArena 同款）。
    private float _kick;
    private float _kickTime;
    private bool _kickApplied;

    public override void _Ready()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        if (!ValidateExports())
        {
            _fatal = true;
            GetTree().Quit(2);
            return;
        }

        _gravityPerTick = new Vector3(0f, -GravityMps2 * (float)(TickDt * TickDt), 0f);
        Engine.PhysicsTicksPerSecond = HostPhysicsTps;

        try
        {
            _arena = new BoxRoomArenaBuilder(
                Vector3.Zero, ArenaWidth, ArenaDepth, SpawnEndInset);
        }
        catch (InvalidOperationException error)
        {
            GD.PushError($"[PLANT-ARENA] {error.Message}");
            _fatal = true;
            GetTree().Quit(2);
            return;
        }
        _arena.Build(this);

        // 挂点：房间正中天花板走面（BoxRoomArenaBuilder 净高 = RoomHeight）。
        _mountPoint = _arena.Origin + new Vector3(
            ArenaWidth * 0.5f,
            DaddyLongLegsSandbox.DaddyLongLegsMazeBuilder.RoomHeight,
            ArenaDepth * 0.5f);

        // 挂点下的暖光：既是"这盏吸顶灯是亮的"的舞台调度，也是引玩家走近的信号；
        // 亮度随 DisguiseAmount 走——揭露时灯"熄"，嘴里的灯泡只剩自发光。
        _lampLight = new OmniLight3D
        {
            Name = "LampLight",
            Position = _mountPoint - new Vector3(0f, 0.35f, 0f),
            LightColor = new Color(1f, 0.82f, 0.58f),
            LightEnergy = 2.5f,
            OmniRange = 7f,
            ShadowEnabled = true,
        };
        AddChild(_lampLight);

        SpawnPlant();

        _player = new ArenaFirstPersonPlayer { Name = "ArenaPlayer" };
        AddChild(_player);
        _player.Place(_arena.PlayerSpawn, _mountPoint);
        _player.SetActive(true);

        _hud = new TentaclePlantArenaHud();
        _hud.Build(this);

        GD.Print($"[PLANT-ARENA] ready preset={_preset.Name} tps={HostPhysicsTps} " +
                 $"room={ArenaWidth:F0}x{ArenaDepth:F0}m " +
                 $"trigger={AmbushTriggerRadius:F1}m release={EngageReleaseRadius:F1}m " +
                 $"rearm={RearmDelaySeconds:F1}s cooldown={AttackCooldownSeconds:F1}s " +
                 $"bite={BiteRadius:F2}m shove={BiteShoveSpeed:F1}mps");
    }

    private bool ValidateExports()
    {
        try
        {
            _preset = TentaclePlantFactory.ByName($"tentacle-plant/{DefaultPreset}");
        }
        catch (ArgumentException)
        {
            return Fail($"DefaultPreset '{DefaultPreset}' is not original/short/hunter/lurker");
        }
        if (HostPhysicsTps is < 40 or > 1000)
            return Fail($"HostPhysicsTps must be in [40,1000], got {HostPhysicsTps}");
        if (!FinitePositive(GravityMps2))
            return Fail($"GravityMps2 must be finite and positive, got {GravityMps2}");
        if (!FinitePositive(AmbushTriggerRadius))
            return Fail($"AmbushTriggerRadius must be finite and positive, got {AmbushTriggerRadius}");
        if (!float.IsFinite(EngageReleaseRadius) || EngageReleaseRadius <= AmbushTriggerRadius)
            return Fail($"EngageReleaseRadius must exceed AmbushTriggerRadius " +
                        $"({EngageReleaseRadius} vs {AmbushTriggerRadius})");
        if (!FinitePositive(RearmDelaySeconds))
            return Fail($"RearmDelaySeconds must be finite and positive, got {RearmDelaySeconds}");
        if (!FinitePositive(AttackCooldownSeconds))
            return Fail($"AttackCooldownSeconds must be finite and positive, got {AttackCooldownSeconds}");
        if (!FinitePositive(BiteRadius))
            return Fail($"BiteRadius must be finite and positive, got {BiteRadius}");
        if (!float.IsFinite(BiteShoveSpeed) || BiteShoveSpeed < 0f)
            return Fail($"BiteShoveSpeed must be finite and >= 0, got {BiteShoveSpeed}");
        if (!FinitePositive(BittenPromptSeconds))
            return Fail($"BittenPromptSeconds must be finite and positive, got {BittenPromptSeconds}");
        if (!float.IsFinite(KickDegrees) || KickDegrees < 0f
            || !float.IsFinite(KickOffsetMeters) || KickOffsetMeters < 0f)
        {
            return Fail($"kick amounts must be finite and >= 0, got " +
                        $"{KickDegrees}/{KickOffsetMeters}");
        }
        if (!FinitePositive(KickFrequencyHz) || !FinitePositive(KickDecaySeconds))
            return Fail($"kick timing must be finite and positive, got " +
                        $"{KickFrequencyHz}/{KickDecaySeconds}");
        // 触手全伸不能打穿地板视线之外的东西——只是提示性检查：房间净高 3.2m，
        // lurker Length 3.2m 恰好够到地面。
        if (_preset.Length > DaddyLongLegsSandbox.DaddyLongLegsMazeBuilder.RoomHeight + 0.5f)
        {
            GD.Print($"[PLANT-ARENA] note: preset length {_preset.Length:F1}m exceeds " +
                     $"room height; strikes will drag along the floor");
        }
        return true;

        static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
        static bool Fail(string message)
        {
            GD.PushError($"[PLANT-ARENA] invalid scene configuration: {message}");
            return false;
        }
    }

    /// <summary>（重）建控制器与正式渲染件（R 重开安全）。出生即伪装。</summary>
    private void SpawnPlant()
    {
        ulong colliderId = GetNode<StaticBody3D>("Arena/ArenaCollision").GetInstanceId();
        var mount = new TentaclePlantMount(
            _mountPoint, Vector3.Down, Vector3.Right, colliderId);
        _plant = TentaclePlantFactory.CreateController(in mount, _preset, 0x414D4255534821UL);
        _plant.DisguiseIntent = true;
        _prevAttackSerial = _plant.AttackSerial;
        _lastBiteSerial = _plant.AttackSerial;

        _formal?.Clear();
        _formal = new TentaclePlantFormalRenderer(_plant, _preset.Name);
        _formal.Build(this);
        _formal.SetVisible(true);
    }

    // ---- 固定步长循环（RatArena 同款累加器）----

    public override void _PhysicsProcess(double delta)
    {
        if (_fatal)
            return;

        _tickAccumulator += delta;
        int safety = 0;
        while (_tickAccumulator + 1e-12 >= TickDt && safety++ < 32)
        {
            _tickAccumulator -= TickDt;
            RunCoreTick();
            if (_fatal)
                break;
        }
    }

    /// <summary>固定 tick 序：计数 → 绑定地形 → 相位喂输入 → 内核 Tick →
    /// 相位推进（读 Tick 后观测量：突刺沿、咬中判定、防御断言）。</summary>
    private void RunCoreTick()
    {
        _tick++;
        _terrain.Bind(GetWorld3D().DirectSpaceState);
        FeedPlant();
        _plant.Tick(new TickContext(_gravityPerTick, _terrain, _tick));
        AdvancePhase();
    }

    // ---- 相位喂入（内核 Tick 之前写输入面）----

    private void FeedPlant()
    {
        _playerAim = _player.EyePosition;
        if (!_playerAimInitialized)
        {
            _prevPlayerAim = _playerAim;
            _playerAimInitialized = true;
        }
        Vector3 velocityPerTick = _playerAim - _prevPlayerAim;
        _prevPlayerAim = _playerAim;
        float horizontalDistance = HorizontalDistanceToMount(_playerAim);

        switch (_phase)
        {
            case HostPhase.Ambush:
                _plant.DisguiseIntent = true;
                if (horizontalDistance <= AmbushTriggerRadius)
                {
                    // 猎物进圈：喂可见目标——伪装态加速充能，突袭由内核涌现。
                    FeedPlayerTarget(velocityPerTick, hostVisible: true);
                }
                else
                {
                    _plant.Target = null;
                }
                break;

            case HostPhase.Engage:
                _plant.DisguiseIntent = false;
                bool cooldownOver = _tick >= _attackReadyTick;
                if (horizontalDistance <= EngageReleaseRadius)
                {
                    // 冷却中 HostVisible=false：不充能但仍 Tracking——
                    // 闭嘴、嘴对着玩家、扭动身体，正是要的冷却观感。
                    FeedPlayerTarget(velocityPerTick, hostVisible: cooldownOver);
                    _rearmAtTick = -1;
                }
                else
                {
                    // 出圈仍盯人；持续 RearmDelay 后放弃、回伪装。
                    FeedPlayerTarget(velocityPerTick, hostVisible: false);
                    if (_rearmAtTick < 0)
                    {
                        _rearmAtTick = _tick + (long)MathF.Ceiling(
                            RearmDelaySeconds * TicksPerSecond);
                    }
                    else if (_tick >= _rearmAtTick)
                    {
                        _phase = HostPhase.Ambush;
                        _rearmAtTick = -1;
                        _plant.DisguiseIntent = true;
                        _plant.Target = null;
                        GD.Print($"[PLANT-ARENA] rearm t={_tick} " +
                                 $"dist={horizontalDistance:F1}m");
                    }
                }
                break;
        }
    }

    private void FeedPlayerTarget(Vector3 velocityPerTick, bool hostVisible)
    {
        // 恒 HostGrabbable=false：快咬弹开制——内核绝不建立抓持，
        // PositionCorrection 永远不会与玩家自驱打架；咬中判定见 AdvancePhase。
        _plant.Target = new TentaclePlantTargetSnapshot(
            PlayerStableId,
            _playerAim,
            velocityPerTick,
            PlayerChunkRadius,
            1f,
            hostVisible,
            hostGrabbable: false);
    }

    // ---- 相位推进（内核 Tick 之后读观测量）----

    private void AdvancePhase()
    {
        // 突刺出手沿：压攻击冷却；伏击出手即转入交战。
        if (_plant.AttackSerial != _prevAttackSerial)
        {
            _prevAttackSerial = _plant.AttackSerial;
            _attackReadyTick = _tick + Math.Max(1,
                (long)MathF.Ceiling(AttackCooldownSeconds * TicksPerSecond));
            if (_phase == HostPhase.Ambush)
            {
                _phase = HostPhase.Engage;
                _rearmAtTick = -1;
                GD.Print($"[PLANT-ARENA] ambush strike t={_tick} serial={_plant.AttackSerial}");
            }
        }

        // 咬中判定：Striking 期 Hand 距玩家眼位（= 瞄准点）≤ BiteRadius，每次突刺只结算一次。
        if (_plant.Phase == TentaclePlantPhase.Striking &&
            _lastBiteSerial != _plant.AttackSerial &&
            _plant.Hand.Pos.DistanceTo(_playerAim) <= BiteRadius)
        {
            _lastBiteSerial = _plant.AttackSerial;
            LandBite();
        }

        // 防御断言：HostGrabbable=false 下内核不应建立任何抓持关系。
        if (_plant.TargetEffect.CaptureStarted || _plant.TargetEffect.Held)
        {
            GD.PushWarning("[PLANT-ARENA] unexpected capture with hostGrabbable=false; " +
                           "releasing defensively");
            _plant.ReleaseHeldTarget();
        }
    }

    /// <summary>咬中结算（象征性伤害）：计数 + 提示 + 镜头 kick + 背向推离。</summary>
    private void LandBite()
    {
        _biteCount++;
        _bittenPromptUntilTick = _tick
            + (long)MathF.Ceiling(BittenPromptSeconds * TicksPerSecond);
        ShowToast($"BITTEN x{_biteCount}");
        _kick = 1f;
        if (BiteShoveSpeed > 0f)
        {
            Vector3 away = _playerAim - _plant.Hand.Pos;
            away.Y = 0f;
            if (away.LengthSquared() > 1e-8f)
            {
                // 必须走外部冲量通道：直写 Velocity 会被玩家「无输入即刻停」
                // 语义下一物理步一步归零（RatArena R18b 实证）。
                _player.AddImpulse(away.Normalized() * BiteShoveSpeed);
            }
        }
        GD.Print($"[PLANT-ARENA] bite lands count={_biteCount} t={_tick}");
    }

    private float HorizontalDistanceToMount(Vector3 point)
    {
        Vector3 delta = point - _mountPoint;
        delta.Y = 0f;
        return delta.Length();
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTtl = 1.6f;
    }

    private void ResetRun()
    {
        _phase = HostPhase.Ambush;
        _attackReadyTick = 0;
        _rearmAtTick = -1;
        _biteCount = 0;
        _bittenPromptUntilTick = -1;
        _toastTtl = 0f;
        _kick = 0f;
        _kickTime = 0f;
        _player.SetCameraShake(Vector3.Zero, Vector3.Zero);
        _kickApplied = false;
        _playerAimInitialized = false;
        SpawnPlant();
        _player.InputLocked = false;
        _player.Place(_arena.PlayerSpawn, _mountPoint);
        _player.SetActive(true); // 重新捕获鼠标（Esc 释放过也一并恢复）——RatArena 同款
        GD.Print($"[PLANT-ARENA] reset t={_tick}");
    }

    // ---- 渲染帧：kick + 渲染 + 灯光 + HUD ----

    public override void _Process(double delta)
    {
        if (_fatal)
            return;

        float physicsDelta = 1f / Math.Max(1, Engine.PhysicsTicksPerSecond);
        float interpolation = Mathf.Clamp(
            (float)(_tickAccumulator / TickDt
                + Engine.GetPhysicsInterpolationFraction() * physicsDelta / TickDt),
            0f, 1f);

        UpdateCameraKick((float)delta);
        _formal?.Draw(interpolation, (float)delta);

        // 吸顶灯光随伪装程度走：揭露时熄灭，只剩嘴里灯泡的自发光。
        _lampLight.LightEnergy = Mathf.Lerp(0.15f, 2.5f, _plant.DisguiseAmount);

        UpdateHud((float)delta);
    }

    /// <summary>咬合镜头 kick：多条不可通约正弦 × 快衰减冲量包络（RatArena 同款）。</summary>
    private void UpdateCameraKick(float delta)
    {
        _kick *= MathF.Exp(-delta / MathF.Max(0.05f, KickDecaySeconds));
        if (_kick < 0.005f)
        {
            if (_kickApplied)
            {
                _player.SetCameraShake(Vector3.Zero, Vector3.Zero);
                _kickApplied = false;
            }
            _kickTime = 0f;
            return;
        }

        _kickTime += delta * KickFrequencyHz * Mathf.Tau;
        float t = _kickTime;
        float rot = Mathf.DegToRad(KickDegrees) * _kick;
        var euler = new Vector3(
            (MathF.Sin(t) * 0.6f + MathF.Sin(t * 0.61f + 1.7f) * 0.4f) * rot,
            (MathF.Sin(t * 0.83f + 4.2f) * 0.6f + MathF.Sin(t * 0.47f + 0.9f) * 0.4f) * rot,
            MathF.Sin(t * 0.73f + 2.6f) * rot * 0.5f);
        float sway = KickOffsetMeters * _kick;
        var offset = new Vector3(
            MathF.Sin(t * 0.89f + 0.4f) * sway,
            MathF.Sin(t * 1.13f + 3.1f) * sway * 0.7f,
            0f);
        _player.SetCameraShake(offset, euler);
        _kickApplied = true;
    }

    private void UpdateHud(float delta)
    {
        _toastTtl = MathF.Max(0f, _toastTtl - delta);
        _hud.SetToast(_toastTtl > 0f ? _toastText : "");

        float cooldown = MathF.Max(0f, (_attackReadyTick - _tick) / TicksPerSecond);
        float rearm = _rearmAtTick < 0
            ? 0f
            : MathF.Max(0f, (_rearmAtTick - _tick) / TicksPerSecond);
        _hud.SetStatus(
            $"PLANT AMBUSH ARENA — host={_phase} plant={_plant.Phase} " +
            $"disguise={_plant.DisguiseAmount:F2} charge={_plant.AttackCharge:F2} " +
            $"cooldown={cooldown:F1}s rearm={rearm:F1}s " +
            $"dist={HorizontalDistanceToMount(_playerAim):F1}m bites={_biteCount}\n" +
            "[R] restart  [F1] hud  [Esc] mouse");

        if (_tick < _bittenPromptUntilTick)
            _hud.SetPrompt($"BITTEN x{_biteCount}");
        else
            _hud.SetPrompt("");
    }

    // ---- 输入 ----

    public override void _Input(InputEvent @event)
    {
        if (_fatal)
            return;
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.PhysicalKeycode)
        {
            case Key.R:
                ResetRun();
                break;
            case Key.F1:
                _hud.ToggleStatusVisibility();
                break;
            case Key.Escape:
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible
                    : Input.MouseModeEnum.Captured;
                break;
        }
    }
}
