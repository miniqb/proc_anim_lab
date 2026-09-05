using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.TentaclePlant;

namespace ProcAnimLab.Render;

/// <summary>
/// TentaclePlant（拟态草→肉质触手怪）正式渲染件。造型：一根反锥度肉管
/// （根粗 → 颈细 → 末端向头膨出，视觉剖面与物理半径解耦——渲染研究 §1.4，原作
/// 拟态草本就反转物理锥度），末端一张蛇/蜥式**双颌对开长吻大嘴**（上下颌各承担
/// 一半张角，能张近 172°），嘴内喉部暗红、中央一颗灯泡状白球器官；无眼无耳。
/// 嘴态：常态闭合微颤 → Windup 随充能慢慢张开 → 出手后全张扑脸（飞行保持窗）→
/// 到脸/抓到瞬间快合 + 前突顿挫（AttackSerial 阶跃沿触发飞行窗，不测平滑域——
/// RatFiend R18b 教训；咬合延到窗末，嘴到猎物前绝不先闭）→ 伪装态张到最大。
/// 伪装（DisguiseAmount）额外做纯化妆下沉：头组件缩进安装面内（允许穿模），
/// 几乎只露灯泡——吊在天花板上读成一盏吸顶灯。
/// 朝向：forward = 末端多段混合 → 宿主光束覆写（SetBeamAim，权重 ease 淡入淡出；
/// 探头/交战期链末段上翘、不代表"照哪"）→ Striking 混突刺速度、伪装混 Outward
/// 兜底缩链退化帧 → 低通；up 逐帧平行传输自由跟随触手 roll，**不做世界竖直对齐**
/// ——张嘴平面顺着触手自身的自然扭转走（用户明确要求）。
/// 颈部姿态：嘴 forward 被光束覆写后与物理链末段解耦，嘴会张向自己的脖子；
/// <see cref="TentaclePlantNeckPose"/> 按"头颈关节有限转角 + 颈椎吸收其余"重画最后
/// 三节的走向（枕点/前枕点/肘替换 s[n−1..n−3] 三个控制点），嘴 forward 分毫不动、
/// 权重 0 逐位回落物理链——见文档 §7.1。
/// 化妆状态（全渲染侧私有，不进物理与哈希）：_bodyUp/_mouthFwd/_mouthUp 低通帧、
/// _mouthOpen 非对称低通（开慢合快——咬合要"啪"地咬死）、_snapDelay 飞行保持、
/// _snapTimer 咬合顿挫、_disguiseEase 伪装缓动、_probeEase 探头缓动、
/// _beamAimDir/_beamAimWeight/_beamEase 宿主光束覆写、_chainDir/_neckState 颈部
/// 姿态、_spot 探照灯、_time 蠕动相位。形状基因 seed（FNV-1a(预设名)）出生冻结。
/// </summary>
internal sealed class TentaclePlantFormalRenderer : IFormalRenderer
{
    private const float SnapSeconds = 0.35f;
    // 出手沿 → 咬合之间的飞行保持窗：全张扑向猎物，到时才"啪"地咬死。
    // 手端突刺速度 ~0.6m/tick、交战出手距离 ≤~5m → 飞行 ≤~8 tick ≈ 0.2s；
    // 取 0.18s 让远程恰好到脸、近程宁可略晚——嘴到脸之前绝不能先闭上。
    // 内核真抓到猎物（HeldTargetId）时提前触发，接触帧即咬合。
    private const float SnapDelaySeconds = 0.18f;

    private readonly TentaclePlantController _c;
    private readonly int _seed;
    private readonly TubeMeshBuilder _tube = new();
    private readonly PlantPalette _pal;

    private Node3D? _root;
    private MeshInstance3D? _bulb;
    private SpotLight3D? _spot;
    private float _spotAngleDeg = 16f;
    private float _spotRange = 4.2f;
    private Vector3 _beamAimDir = Vector3.Down;
    private float _beamAimWeight;
    private float _beamEase;
    private float _neckAtlasDegrees = 45f;
    private float _neckStretchMax = 1f;

    // —— seed 冻结的形状基因 ——
    private float _headSize;
    private float _jawRestDeg;
    private float _jawGapeDeg;
    private float _jawLenMult;
    private float _jawBow;
    private Tooth[] _upperTeeth = Array.Empty<Tooth>();
    private Tooth[] _lowerTeeth = Array.Empty<Tooth>();

    // —— 渲染侧化妆状态 ——
    private Vector3 _bodyUp;
    private Vector3 _mouthFwd;
    private Vector3 _mouthUp;
    private Vector3 _chainDir = Vector3.Down;
    private TentaclePlantNeckPose.State _neckState;
    private TentaclePlantNeckPose.Output _neck;
    private bool _frameInitialized;
    private float _mouthOpen;
    private float _disguiseEase;
    private float _probeEase;
    private float _snapTimer;
    private float _snapDelay;
    private long _prevAttackSerial;
    private float _time;

    private readonly List<TubeStation> _stations = new();
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();

    private readonly record struct Tooth(float T, float Length, float Width);

    private readonly struct PlantPalette
    {
        public readonly Color Root;
        public readonly Color Neck;
        public readonly Color Head;
        public readonly Color Maw;
        public readonly Color Teeth;
        public readonly Color Bulb;

        public PlantPalette(Color root, Color neck, Color head)
        {
            Root = root;
            Neck = neck;
            Head = head;
            Maw = new Color(0.30f, 0.06f, 0.06f);
            Teeth = new Color(0.85f, 0.80f, 0.70f);
            Bulb = new Color(0.97f, 0.94f, 0.86f);
        }
    }

    public TentaclePlantFormalRenderer(TentaclePlantController controller, string presetName)
    {
        _c = controller;
        uint h = 2166136261u;
        foreach (char ch in presetName)
        {
            h = (h ^ ch) * 16777619u;
        }
        _seed = unchecked((int)h);
        _prevAttackSerial = controller.AttackSerial;
        _pal = PaletteForPreset(presetName);
    }

    /// <summary>配色按预设分档：全部"生肉/无脊椎动物"方向、禁绿（sRGB 空间定档，
    /// srgbVertexColors: true 下所见即所写）。根部整体压暗融入安装面剪影。</summary>
    private static PlantPalette PaletteForPreset(string name)
    {
        if (name.EndsWith("/hunter", StringComparison.Ordinal))
        {
            // 暗红肉：伏击猎手。
            return new PlantPalette(
                new Color(0.14f, 0.06f, 0.06f),
                new Color(0.42f, 0.16f, 0.14f),
                new Color(0.56f, 0.24f, 0.20f));
        }
        if (name.EndsWith("/short", StringComparison.Ordinal))
        {
            // 灰褐肉。
            return new PlantPalette(
                new Color(0.17f, 0.12f, 0.10f),
                new Color(0.44f, 0.33f, 0.28f),
                new Color(0.56f, 0.42f, 0.36f));
        }
        if (name.EndsWith("/lurker", StringComparison.Ordinal))
        {
            // 苍白灰粉：吊顶伏击者，贴近天花板色调。
            return new PlantPalette(
                new Color(0.20f, 0.16f, 0.15f),
                new Color(0.52f, 0.40f, 0.38f),
                new Color(0.64f, 0.50f, 0.46f));
        }
        // original：苍白肉粉。
        return new PlantPalette(
            new Color(0.21f, 0.12f, 0.11f),
            new Color(0.50f, 0.30f, 0.27f),
            new Color(0.62f, 0.40f, 0.35f));
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root, srgbVertexColors: true);

        // 灯泡是唯一刚性件：暖白 + 微 emission（用户要求白球即可，不必真发光）。
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 10 };
        var bulbMat = new StandardMaterial3D
        {
            AlbedoColor = _pal.Bulb,
            EmissionEnabled = true,
            Emission = _pal.Bulb,
            EmissionEnergyMultiplier = 1.1f,
            Roughness = 0.35f,
        };
        _bulb = new MeshInstance3D
        {
            Mesh = sphere,
            MaterialOverride = bulbMat,
            // 自发光球不该遮自己的探照灯。
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _root.AddChild(_bulb);

        // 探照灯 = 锁定锥的可视化（设定：灯泡发光并检测反射变化）。挂 _root 而非
        // _bulb——灯泡 basis 含非均匀缩放，子节点会继承畸变。伪装态 fwd 已被
        // UpdateMouthFrame 对齐 Outward → 灯自动朝下变吸顶灯，零额外分支。
        // 开阴影：墙挡光 = 感知视线遮挡的视觉诚实。
        _spot = new SpotLight3D
        {
            LightColor = _pal.Bulb,
            LightEnergy = 0.9f,
            SpotAngle = _spotAngleDeg,
            SpotRange = _spotRange,
            SpotAngleAttenuation = 1.4f,
            SpotAttenuation = 1.5f,
            ShadowEnabled = true,
        };
        _root.AddChild(_spot);

        SeedGenes();
        _frameInitialized = false;
        _neckState = default;
        _neck = default;
        _prevAttackSerial = _c.AttackSerial;
        _snapDelay = 0f;
        _snapTimer = 0f;
    }

    /// <summary>
    /// 宿主喂的光束朝向（化妆覆写）：探头/交战期链身有余量、末段在重力下上翘，
    /// 链末段推导的嘴 forward 不再代表"照哪"——按宿主权威方向（= 感知锁定锥轴）
    /// 混合嘴与探照灯朝向。weight=0 完全回落链推导，不调用的场景零影响。
    /// </summary>
    public void SetBeamAim(Vector3 direction, float weight)
    {
        if (direction.LengthSquared() > 1e-10f)
        {
            _beamAimDir = direction.Normalized();
        }
        _beamAimWeight = Mathf.Clamp(weight, 0f, 1f);
    }

    /// <summary>
    /// 宿主化妆配置面：让探照灯锥角/射程与宿主感知锁定锥对齐（可视化=设定本身）。
    /// 不调用则用默认值（沙盒即如此）。SpotAngle 语义 = 轴到边的半角。
    /// </summary>
    public void ConfigureSearchlight(float coneHalfAngleDegrees, float rangeMeters)
    {
        _spotAngleDeg = coneHalfAngleDegrees;
        _spotRange = rangeMeters;
        if (_spot is not null)
        {
            _spot.SpotAngle = _spotAngleDeg;
            _spot.SpotRange = _spotRange;
        }
    }

    /// <summary>
    /// 宿主化妆配置面：头颈关节极限转角（度；180 = 不限制，颈部只做进枕对齐）与
    /// "够不着"时允许的颈骨化妆拉伸倍率（≥1；1 = 绝不拉伸，改由头颈角放开）。
    /// 不调用则用默认 45°/1.0（沙盒从不喂光束，权重恒 0，本机制不生效）。
    /// </summary>
    public void ConfigureNeck(float atlasDegrees, float stretchMax)
    {
        _neckAtlasDegrees = Mathf.Clamp(atlasDegrees, 0f, 180f);
        _neckStretchMax = MathF.Max(1f, stretchMax);
    }

    /// <summary>重画后的头颈角（度）：枕点←前枕点 与嘴 forward 的夹角；HUD 读数用。</summary>
    public float NeckHeadAngleDegrees => _neck.HeadNeckDeg;

    /// <summary>物理链末方向与嘴 forward 的夹角（度）：未重画时的头颈角。</summary>
    public float NeckOmegaDegrees => _neck.OmegaDeg;

    /// <summary>颈骨当前化妆拉伸倍率（1 = 无）。</summary>
    public float NeckStretch => _neck.Stretch;

    /// <summary>形状基因一次抽签（seed 冻结）：头径/颌长/张角档/牙排。运行时抖动只用
    /// 相位与时间，绝不再抽随机。</summary>
    private void SeedGenes()
    {
        var rng = new Random(_seed);
        float R() => (float)rng.NextDouble();

        _headSize = Mathf.Lerp(1.25f, 1.45f, R());
        // rest ≥6°：上下两根颌管永不真正贴合（防闭合 z-fight，RatFiend jawRest 同理）；
        // gape 上限 172°：真 180° 时颌背贴上颈管，留余量。
        _jawRestDeg = Mathf.Lerp(6f, 9f, R());
        _jawGapeDeg = Mathf.Lerp(160f, 172f, R());
        _jawLenMult = Mathf.Lerp(2.0f, 2.4f, R());
        _jawBow = Mathf.Lerp(0.08f, 0.14f, R());
        float fangMult = Mathf.Lerp(1.3f, 1.7f, R());

        // 尖牙很多：上颌每侧 8~12、下颌每侧 7~10，10%/颗缺牙，最前两颗做犬齿。
        Tooth[] SeedRow(int min, int max, float lenScale)
        {
            int count = rng.Next(min, max + 1);
            var teeth = new List<Tooth>(count);
            for (int i = 0; i < count; i++)
            {
                if (R() < 0.10f)
                {
                    continue; // 缺牙
                }
                float t = Mathf.Lerp(0.06f, 0.94f, (i + 0.5f) / count);
                float len = Mathf.Lerp(0.42f, 0.18f, t) * lenScale *
                    Mathf.Lerp(0.9f, 1.15f, R());
                if (i < 2)
                {
                    len *= fangMult;
                }
                teeth.Add(new Tooth(t, len, Mathf.Lerp(0.055f, 0.09f, R())));
            }
            return teeth.ToArray();
        }

        _upperTeeth = SeedRow(8, 12, 1f);
        _lowerTeeth = SeedRow(7, 10, 0.9f);
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _bulb = null;
        _spot = null;
        _frameInitialized = false;
        _neckState = default;
    }

    public void SetVisible(bool visible)
    {
        if (_root is not null)
        {
            _root.Visible = visible;
        }
    }

    public void Draw(float alpha, float dt)
    {
        if (_root is null || _bulb is null)
        {
            return;
        }
        _time += dt;

        // 伪装/探头缓动：内核标量本身慢升快降，渲染低通只吃 40Hz 台阶。
        float kDisguise = 1f - Mathf.Exp(-12f * dt);
        _disguiseEase += (_c.DisguiseAmount - _disguiseEase) * kDisguise;
        _probeEase += (_c.ProbeAmount - _probeEase) * kDisguise;

        UpdateMouthOpen(dt);
        UpdateMouthFrame(alpha, dt);

        // 伪装下沉是纯化妆位移；突刺/咬合帧强制交还物理位，
        // 否则嘴会"从天花板瞬移到扑击点"。
        bool strikingNow = _c.Phase == TentaclePlantPhase.Striking
            || _snapDelay > 0f || _snapTimer > 0f;

        float headR = _c.Params.HandVisualRadius * _headSize;
        Vector3 handDraw = _c.Hand.LerpPos(alpha);
        // 下沉还必须等物理链真正收拢（按手端到挂点的距离连续门控）：出生/重置后的
        // 首次入伪装，DisguiseAmount 两秒到满而链还垂在半空——下沉偏移把管体中段
        // 上提、头 lerp 进天花板，滞后的链段仍在低处，管尾会被拉成一根从大张的嘴
        // 中央穿出的肉锥（用户实测穿帮）。门限 = 蜷缩静置包络（2L×Fraction+0.15，
        // 与 smoke quietTip 断言同源），包络外 gateRamp 处归零；吞食后的回伪装
        // 手端本就在包络内，门恒为 1、逐帧不变。
        float coilEnvelope = 2f * _c.Params.Length *
            _c.Params.DisguiseExtensionFraction + 0.15f;
        float gateRamp = Mathf.Max(0.5f, _c.Params.Length * 0.25f);
        float sinkGate = Mathf.Clamp(
            1f - ((handDraw - _c.Mount.Point).Length() - coilEnvelope) / gateRamp,
            0f, 1f);
        float sink = strikingNow
            ? 0f
            : Mathf.SmoothStep(0f, 1f, _disguiseEase) * sinkGate;
        // 埋深 1.35 headR：全伪装时全张的双颌与牙尖（含 fangMult 加长的犬齿）全部
        // 藏进安装面内（允许穿模；1.1 时 hunter/lurker 的门牙尖会冒头——评审精算），
        // 灯泡再由 DrawMouth 的 sink 项单独推出走面下——几乎只露一个灯泡。
        Vector3 buriedHome = _c.Mount.Point - _c.Outward * (headR * 1.35f);
        Vector3 mouthPos = handDraw.Lerp(buriedHome, sink);
        if (_snapTimer > 0f)
        {
            // 咬合顿挫：sin 包络前突，回程由包络自然收干净。
            float env = Mathf.Sin(_snapTimer / SnapSeconds * Mathf.Pi);
            mouthPos += _mouthFwd * (0.07f * env);
        }

        // 下沉深度必须盖住全伪装的蜷缩包络（tip 硬拴 2L×fraction + 视觉半径），
        // 管体才能连续沉进安装面消失——旧版 sink>0.9 一刀切停画会让收链过程中
        // 仍悬在面外 ~1m 的残段单帧消失（评审实测），揭露方向有扑击爆发掩护、
        // 收拢方向没有。
        float sinkDepth = _c.Params.Length * 2f * _c.Params.DisguiseExtensionFraction +
            headR * 1.2f;
        Vector3 sinkOffset = -_c.Outward * (sinkDepth * sink);
        UpdateNeckPose(alpha, dt, sinkOffset, handDraw, mouthPos, headR);

        _tube.BeginFrame();
        DrawBody(alpha, sinkOffset, mouthPos, headR);
        DrawMouth(mouthPos, headR, sink);
        _tube.EndFrame();
    }

    /// <summary>颈部姿态（见 <see cref="TentaclePlantNeckPose"/>）：输入全取绘制空间
    /// （插值 + 下沉偏移后的物理点；枕点按旧式表达式 mouthPos − fwd×0.35headR 在这里算好
    /// 喂入，权重 0 时管体末点逐位等于改动前）。接管权重 = 光束权重 ease ×
    /// (1 − 伪装缓动×1.25) × 蜷缩门（平均链节 ≤ 2 倍枕点后退量时为 0、≥ 4 倍时为 1——
    /// 蜷缩链上没有"颈"可言，出伪装首帧不许从它起算）。n&lt;4 的预设（现无）不重画。</summary>
    private void UpdateNeckPose(
        float alpha, float dt, Vector3 sinkOffset, Vector3 handDraw, Vector3 mouthPos, float headR)
    {
        IReadOnlyList<TentacleSegmentState> segments = _c.Segments;
        int n = segments.Count;
        Vector3 physHead = handDraw + sinkOffset;
        if (n < 4)
        {
            _neck = new TentaclePlantNeckPose.Output
            {
                Elbow = segments[Math.Max(0, n - 3)].LerpPos(alpha) + sinkOffset,
                PreOcciput = segments[Math.Max(0, n - 2)].LerpPos(alpha) + sinkOffset,
                Occiput = mouthPos - _mouthFwd * (headR * TentaclePlantNeckPose.OcciputHeadR),
                Stretch = 1f,
                Feasible = true,
            };
            return;
        }
        Vector3 anchor = segments[n - 4].LerpPos(alpha) + sinkOffset;
        Vector3 physMid = segments[n - 3].LerpPos(alpha) + sinkOffset;
        Vector3 physPre = segments[n - 2].LerpPos(alpha) + sinkOffset;
        float meanLink = (physMid.DistanceTo(anchor) + physPre.DistanceTo(physMid) +
            physHead.DistanceTo(physPre)) / 3f;
        float occiput = headR * TentaclePlantNeckPose.OcciputHeadR;
        float coilGate = Mathf.Clamp((meanLink - 2f * occiput) / (2f * occiput), 0f, 1f);
        float weight = _beamEase * (1f - Mathf.Min(1f, _disguiseEase * 1.25f)) * coilGate;
        Vector3 occiputPoint = mouthPos - _mouthFwd * (headR * TentaclePlantNeckPose.OcciputHeadR);
        var input = new TentaclePlantNeckPose.Input(
            anchor, physMid, physPre, physHead, occiputPoint, _mouthFwd, _chainDir,
            _c.Outward, _c.Mount.Point, headR, weight, dt);
        _neck = TentaclePlantNeckPose.Solve(in input, _neckAtlasDegrees, _neckStretchMax,
            ref _neckState);
    }

    /// <summary>嘴开度合成：出手沿 → 飞行保持窗全张扑脸 → 到时/抓到才咬合（快合）；
    /// 抓持 → 0；否则 max(蓄力斜坡, 伪装, 闲置微呼吸)。
    /// 非对称低通：开慢（蓄力是"慢慢张开"）合快（咬合要一口咬死）。</summary>
    private void UpdateMouthOpen(float dt)
    {
        // 出手沿：AttackSerial 单调序号的增沿（阶跃量，错帧不丢事件）。
        // 不立即咬合——先记飞行保持窗，嘴张着飞完全程，到脸才咬。
        if (_c.AttackSerial != _prevAttackSerial)
        {
            _prevAttackSerial = _c.AttackSerial;
            _snapDelay = SnapDelaySeconds;
        }
        if (_snapDelay > 0f)
        {
            _snapDelay -= dt;
            // 到时或内核真抓到猎物（可抓路线的接触帧）→ 触发咬合。
            if (_snapDelay <= 0f || _c.HeldTargetId is not null)
            {
                _snapDelay = 0f;
                _snapTimer = SnapSeconds;
            }
        }
        _snapTimer = Mathf.Max(0f, _snapTimer - dt);

        float target;
        if (_snapDelay > 0f)
        {
            target = 1f; // 飞行保持：全张扑向猎物
        }
        else if (_snapTimer > 0f ||
            _c.Phase is TentaclePlantPhase.Striking or TentaclePlantPhase.Holding)
        {
            target = 0f; // 咬紧猎物
        }
        else
        {
            float charge = Mathf.Clamp(_c.AttackCharge, 0f, 1f);
            float windup = Mathf.InverseLerp(_c.Params.WindupStart, 1f, charge);
            // 探头张紧也张嘴：颌是探照灯的准直器，搜索时全程大张。
            target = Mathf.Max(
                Mathf.Clamp(windup, 0f, 1f),
                Mathf.Max(_disguiseEase, _probeEase));
            // 闲置微呼吸颌 + 负偏置双正弦微颤（只朝闭合脉动，分频防拍频；
            // 幅度随开度升档——闭着的嘴不打颤）。微颤随伪装归零：伪装态开度
            // 下探会让颌/牙周期性冒出安装面（评审确认的穿帮）。
            target += 0.045f * (0.5f + 0.5f * Mathf.Sin(_time * 1.1f)) * (1f - target);
            float flutter = 0.5f * Mathf.Sin(_time * 2.3f)
                + 0.5f * Mathf.Sin(_time * 5.3f + 1.4f) - 0.85f;
            target += 0.06f * flutter * (0.25f + 0.75f * Mathf.Clamp(target, 0f, 1f)) *
                (1f - _disguiseEase);
            target = Mathf.Clamp(target, 0f, 1f);
        }

        float kOpen = 1f - Mathf.Exp(-7f * dt);
        float kClose = 1f - Mathf.Exp(-28f * dt);
        _mouthOpen += (target - _mouthOpen) * (target > _mouthOpen ? kOpen : kClose);
    }

    /// <summary>嘴帧：forward = 末端多段混合（单差分抖）→ 宿主光束覆写（照哪由宿主
    /// 权威方向决定，权重淡入淡出）→ Striking 混 40% 突刺速度方向、伪装混向 Outward
    /// （缩链后末端差分退化）→ 低通；up 逐帧平行传输延续（roll 自由跟随触手，
    /// 不做世界对齐），近共线逐级回退。帧每 Draw 只算一次。</summary>
    private void UpdateMouthFrame(float alpha, float dt)
    {
        IReadOnlyList<TentacleSegmentState> segments = _c.Segments;
        int n = segments.Count;
        Vector3 a = segments[n - 1].LerpPos(alpha) - segments[n - 2].LerpPos(alpha);
        Vector3 b = n >= 3
            ? segments[n - 2].LerpPos(alpha) - segments[n - 3].LerpPos(alpha)
            : a;
        Vector3 fwdRaw = SafeDirection(a * 0.6f + b * 0.4f, _c.Outward);
        // 光束覆写前的链末方向：颈部姿态的 Ω 基准（重画绝不比它更差）。
        _chainDir = fwdRaw;
        // 宿主光束覆写：探头/交战期悬停的链身有余量、末段上翘，链推导的 fwd
        // 不代表"照哪"；权重经 ease 淡入淡出，突刺速度混合与伪装 lerp 仍在其上。
        _beamEase += (_beamAimWeight - _beamEase) * (1f - Mathf.Exp(-6f * dt));
        if (_beamEase > 1e-3f)
        {
            fwdRaw = SafeDirection(fwdRaw.Lerp(_beamAimDir, _beamEase), fwdRaw);
        }
        if (_c.Phase == TentaclePlantPhase.Striking &&
            _c.Hand.Vel.LengthSquared() > 1e-6f)
        {
            fwdRaw = SafeDirection(
                fwdRaw * 0.6f + _c.Hand.Vel.Normalized() * 0.4f, fwdRaw);
        }
        // 伪装满时完全对齐 Outward：蜷缩链的末端差分噪声会留下持续 ~14° 倾斜，
        // 最坏滚转对齐时牙尖会斜着穿出安装面（评审实测）；0.8 处即混满。
        fwdRaw = SafeDirection(
            fwdRaw.Lerp(_c.Outward, Mathf.Min(1f, _disguiseEase * 1.25f)), fwdRaw);

        if (!_frameInitialized)
        {
            _mouthFwd = fwdRaw;
            _mouthUp = SafeDirection(_c.Tangent - fwdRaw * _c.Tangent.Dot(fwdRaw),
                OrthoFallback(fwdRaw));
            _bodyUp = _c.Tangent;
            _frameInitialized = true;
        }

        float k = 1f - Mathf.Exp(-10f * dt);
        _mouthFwd = SafeDirection(_mouthFwd.Lerp(fwdRaw, k), fwdRaw);
        Vector3 carried = _mouthUp - _mouthFwd * _mouthUp.Dot(_mouthFwd);
        _mouthUp = carried.LengthSquared() > 1e-6f
            ? carried.Normalized()
            : OrthoFallback(_mouthFwd);

        // 管体 frame 种子 up：投影去首段切向后延续（Daddy _tentUps 同款）。
        Vector3 rootPos = _c.Root.LerpPos(alpha);
        Vector3 firstDir = segments[0].LerpPos(alpha) - rootPos;
        if (firstDir.LengthSquared() > 1e-8f)
        {
            firstDir = firstDir.Normalized();
            Vector3 bodyCarried = _bodyUp - firstDir * _bodyUp.Dot(firstDir);
            if (bodyCarried.LengthSquared() > 1e-6f)
            {
                _bodyUp = bodyCarried.Normalized();
            }
        }
    }

    /// <summary>肉质管体：埋墙根喇叭 → 根 → 各段 → 颈（肘/前枕点，颈部姿态重画）→ 枕点
    /// （头组件接管）。反锥度视觉剖面 + 双频行波蠕动（突刺抑制、伪装归零）。sink 高时整链
    /// 埋进安装面（只剩灯泡）。控制点总数与物理段数同（n+2），剖面/蠕动的索引参数不变。</summary>
    private void DrawBody(float alpha, Vector3 sinkOffset, Vector3 mouthPos, float headR)
    {
        IReadOnlyList<TentacleSegmentState> segments = _c.Segments;
        Vector3 rootPos = _c.Root.LerpPos(alpha);

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        int count = segments.Count + 2;
        _pts.Add(rootPos - _c.Outward * 0.06f + sinkOffset);
        _pts.Add(rootPos + sinkOffset);
        int neckStart = segments.Count - 3;
        for (int i = 0; i < segments.Count; i++)
        {
            // 最后三点由颈部姿态给出（末点 = 枕点：头画位后退 0.35 headR，含下沉/
            // 前突偏移——管头同步，不与嘴拉丝；权重 0 时肘/前枕点逐位等于物理点）。
            Vector3 p = i < neckStart
                ? segments[i].LerpPos(alpha) + sinkOffset
                : i == segments.Count - 1
                    ? _neck.Occiput
                    : i == segments.Count - 2
                        ? _neck.PreOcciput
                        : _neck.Elbow;
            _pts.Add(p);
        }

        float pulseGate = (_c.Phase == TentaclePlantPhase.Striking ? 0.15f : 1f) *
            (1f - _disguiseEase);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            // 反锥度：肉根粗 → 颈细 → 向头膨出（原作视觉剖面自由的 3D 形态）。
            float baseR = Mathf.Lerp(
                _c.Params.RootRadius * 1.15f,
                _c.Params.RootRadius * 0.38f,
                Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, t * 1.35f)));
            float swell = Mathf.Pow(Mathf.Clamp(Mathf.InverseLerp(0.62f, 1f, t), 0f, 1f), 2f);
            float r = Mathf.Max(baseR, swell * headR * 0.8f);
            // 双频行波蠕动（分频防拍频），根部不动。
            float wave = 0.05f * Mathf.Sin(Mathf.Tau * 0.9f * _time - 1.1f * i)
                + 0.025f * Mathf.Sin(Mathf.Tau * 1.7f * _time + 0.7f * i);
            r *= 1f + wave * pulseGate * Mathf.Min(1f, t * 2f);
            _radii.Add(Mathf.Max(0.02f, r));

            Color c = t < 0.5f
                ? _pal.Root.Lerp(_pal.Neck, Mathf.SmoothStep(0f, 1f, t * 2f))
                : _pal.Neck.Lerp(_pal.Head, Mathf.SmoothStep(0f, 1f, (t - 0.5f) * 2f));
            _colors.Add(c);
        }

        SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
        _tube.AddTube(_stations, _bodyUp, 10);
    }

    /// <summary>蛇式双颌大嘴：上下两根长吻锥管绕 mouthRight 轴对开各承担 theta/2
    /// （长吻双颌对开时嘴缝中线天然保持在触手轴向上），喉部沿平分线的宽根短暗红锥
    /// （闭嘴缩没），灯泡挂在喉心、随开度探出/缩回。</summary>
    private void DrawMouth(Vector3 mouthPos, float headR, float sink)
    {
        Vector3 fwd = _mouthFwd;
        Vector3 up = _mouthUp;
        Vector3 right = fwd.Cross(up).Normalized();

        Vector3 jawPivot = mouthPos;
        float theta = Mathf.DegToRad(
            _jawRestDeg + (_jawGapeDeg - _jawRestDeg) * _mouthOpen);
        float jawLen = headR * _jawLenMult;

        DrawJaw(jawPivot, fwd, up, right, theta * 0.5f, jawLen, headR,
            _upperTeeth, radiusScale: 1f, toothShift: 0f);
        DrawJaw(jawPivot, fwd, up, right, -theta * 0.5f, jawLen, headR,
            _lowerTeeth, radiusScale: 0.94f, toothShift: 0.06f);

        // 铰点填缝：后侧肉球封住两根颌管根与颈的侧向缺口（闭嘴时被颌管完全包住），
        // 前侧暗红"口底"球把张开的楔口填成暗色口腔。
        _tube.AddKnob(jawPivot - fwd * (headR * 0.40f), headR * 0.72f,
            _pal.Head.Darkened(0.08f));
        _tube.AddKnob(jawPivot + fwd * (headR * 0.18f), headR * 0.62f, _pal.Maw);

        // 伪装外推量：只在 sink 后半程（头接近埋进安装面）才渐进启动——早启会把
        // 灯泡沿嘴轴推进"半张/近闭"的双颌里，从颌管壁与唇缝穿模透出一圈白
        // （用户竞技场实测穿帮：牙齿剪影衬在自发光灯泡前的锯齿白带）。
        // 前半程灯泡留在喉心（0.18 + 0.50×开度——攻击蓄力已验证的安全位置）；
        // sink=1 终态推满 1.20，与旧值一致。
        float bulbPush = 1.20f * Mathf.SmoothStep(0.5f, 1f, sink);

        // 喉部：宽根锥沿平分线（= fwd）伸向嘴口，闭嘴时随 mawScale 缩进颌内不可见
        // （RatFiend R18"细长暗锥远看像鼻子"教训：宽根 + 由深到浅）。
        // 喉锥必须跟随灯泡的伪装外推：灯泡被推走而锥留在原位时，嘴底与灯泡
        // 之间会露出一截"连着灯泡的锥"（竞技场暖灯照下呈肉色——用户实测穿帮）；
        // 同推保持锥-灯相对几何与 sink=0 一致，锥始终被灯泡吞没；半径再随 sink
        // 轻微收缩——锥根 0.55 与灯泡横向半径同宽，深伪装时会在椭球端部露一圈边。
        float mawScale = Mathf.Clamp(_mouthOpen * 1.6f, 0.15f, 1f)
            * (1f - 0.35f * sink);
        Vector3 sinkPush = fwd * (headR * bulbPush);
        _stations.Clear();
        _stations.Add(new TubeStation(jawPivot + sinkPush + fwd * (headR * 0.30f),
            headR * 0.55f * mawScale, _pal.Maw.Darkened(0.35f)));
        _stations.Add(new TubeStation(jawPivot + sinkPush + fwd * (headR * 0.80f),
            headR * 0.40f * mawScale, _pal.Maw));
        _stations.Add(new TubeStation(jawPivot + sinkPush + fwd * (headR * 1.15f),
            headR * 0.07f * mawScale, _pal.Maw.Darkened(0.2f)));
        _tube.AddTube(_stations, up, 8);

        // 灯泡：开度越大越向外探；闭嘴时缩回喉内被两根闭合颌管完全包住；
        // 伪装下沉后段头埋进安装面、灯泡单独再向外推（bulbPush，前半程为零）
        // ——只有它探出走面当"吸顶灯"。
        float bulbR = headR * 0.55f;
        Vector3 bulbPos = jawPivot + fwd *
            (headR * (0.18f + 0.50f * _mouthOpen + bulbPush));
        _bulb!.GlobalTransform = new Transform3D(
            new Basis(right * bulbR, fwd * (bulbR * 1.25f), up * bulbR),
            bulbPos);

        // 探照灯摆位与能量：right = fwd×up ⇒ right×up = −fwd，右手正交且
        // −Z ≡ 嘴 forward（SpotLight3D 沿本地 −Z 照射）；灯芯推出球面防埋颌管。
        // 能量曲线只读内核标量：伪装 = 弱光池（吸顶灯脚下那圈），揭露/探头 =
        // 搜索光束；蓄力/突刺窗叠一段闪耀。与竞技场挂点 Omni（disguise↑Omni↑）
        // 互补——那盏演"灯亮着"，这盏演"锁定锥"。
        if (_spot is not null)
        {
            _spot.GlobalTransform = new Transform3D(
                new Basis(right, up, -fwd),
                bulbPos + fwd * (bulbR * 0.6f));
            float hunt = MathF.Max(_probeEase, 1f - _disguiseEase);
            float charge = Mathf.Clamp(_c.AttackCharge, 0f, 1f);
            float windup = Mathf.Clamp(
                Mathf.InverseLerp(_c.Params.WindupStart, 1f, charge), 0f, 1f);
            bool flare = _c.Phase == TentaclePlantPhase.Striking ||
                _snapDelay > 0f || _snapTimer > 0f;
            _spot.LightEnergy = Mathf.Lerp(0.9f, 2.6f, hunt) +
                1.6f * MathF.Max(windup, flare ? 1f : 0f);
        }
    }

    /// <summary>单颌：4 点扫锥管（根埋进颈部，沿自身外侧微弓成相扣吻面）+ 沿唇缘两列
    /// 尖牙（朝对颌 + 15% 向铰点回勾的蛇式后弯；牙 = (帧, 张角) 纯函数零累积）。</summary>
    private void DrawJaw(Vector3 pivot, Vector3 fwd, Vector3 up, Vector3 right,
        float halfAngle, float jawLen, float headR,
        Tooth[] teeth, float radiusScale, float toothShift)
    {
        Vector3 jawDir = fwd.Rotated(right, halfAngle);
        Vector3 jawUp = SafeDirection(right.Cross(jawDir), up);
        // 内侧（朝对颌）方向：上颌 halfAngle>0 时内侧朝 -jawUp…… 取决于 right 定向，
        // 统一由几何决定：内侧 = 指向平分线（fwd）的那一侧。
        Vector3 inner = SafeDirection(fwd - jawDir * fwd.Dot(jawDir), -jawUp * MathF.Sign(halfAngle));
        Vector3 outer = -inner;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(pivot - jawDir * (headR * 0.35f));
        _radii.Add(headR * 0.85f * radiusScale);
        _colors.Add(_pal.Head);
        _pts.Add(pivot + jawDir * (headR * 0.55f) + outer * (headR * _jawBow));
        _radii.Add(headR * 0.60f * radiusScale);
        _colors.Add(_pal.Head.Darkened(0.05f));
        _pts.Add(pivot + jawDir * (headR * 1.5f) + outer * (headR * _jawBow * 0.7f));
        _radii.Add(headR * 0.33f * radiusScale);
        _colors.Add(_pal.Head.Darkened(0.12f));
        _pts.Add(pivot + jawDir * jawLen);
        _radii.Add(headR * 0.11f * radiusScale);
        _colors.Add(_pal.Head.Darkened(0.25f));
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, jawUp, 8);

        // 牙：沿颌管唇缘（内侧缘 ± 半径向两列），根色暗牙龈、尖色脏白；
        // 上下错半齿位（toothShift）防闭合 z-fight；咬合顿挫瞬间提亮。
        Color gum = _pal.Head.Lerp(_pal.Maw, 0.4f);
        Color toothCol = _snapTimer > 0f
            ? _pal.Teeth.Lightened(0.25f * (_snapTimer / SnapSeconds))
            : _pal.Teeth;
        foreach (Tooth tooth in teeth)
        {
            float t = Mathf.Min(0.96f, tooth.T + toothShift);
            Vector3 p = pivot + jawDir * (jawLen * Mathf.Lerp(0.22f, 0.97f, t));
            float r = Mathf.Lerp(headR * 0.55f, headR * 0.11f, t) * radiusScale;
            foreach (float side in Sides)
            {
                Vector3 root = p + inner * (r * 0.72f) + right * (side * r * 0.45f);
                Vector3 dir = SafeDirection(
                    inner * 0.85f - jawDir * 0.15f + right * (side * 0.18f), inner);
                Vector3 bladeRoot = root - inner * (r * 0.15f);
                Vector3 bladeTip = root + dir * (tooth.Length * headR);
                // 十字双刀片：单面刀片侧视近乎消失，大张的嘴是主视觉，
                // 交叉一枚沿颌轴的刀片让牙从任意角度都可读。
                _tube.AddBlade(bladeRoot, bladeTip, right,
                    tooth.Width * headR, 0.38f, gum, toothCol);
                _tube.AddBlade(bladeRoot, bladeTip, jawDir,
                    tooth.Width * headR * 0.8f, 0.38f, gum, toothCol);
            }
        }
    }

    private static readonly float[] Sides = { -1f, 1f };

    private static Vector3 OrthoFallback(Vector3 fwd)
    {
        Vector3 up = fwd.Cross(Vector3.Right);
        if (up.LengthSquared() < 1e-8f)
        {
            up = fwd.Cross(Vector3.Up);
        }
        return up.Normalized();
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }
}
