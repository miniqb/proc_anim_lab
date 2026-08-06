using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Humanoid;
using ProcAnim.Core.Species.Lizard;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.Render;

/// <summary>人形（拾荒者）渲染调色板 + 性格档案。RW 的外观由 6 元性格 + seed 基因驱动
/// （ScavengerGraphics.IndividualVariations / GenerateColors）；本项目预设只有三个固定
/// 品种，颜色改为按 RW 管线的**硬规则**手工定档（头永不亮过身体、眼 = 对比色相满饱和、
/// 手向头色渐变），形状基因仍走 seed 冻结普查。性格旋钮沿 RW 语义：
/// dominance→鹿角须尺寸/背刺、aggression→牙长、sympathy→眼大小/瞳孔、nervous→眨眼频率
/// 与待机抖动、energy→睁眼速度、bravery（本项目无恐惧观测，仅保留档案位）。</summary>
internal readonly struct ScavRenderProfile
{
    public readonly Color Body;
    public readonly Color Head;       // 近黑（RW light 0.05~0.2；硬规则：不亮过身体）
    public readonly Color Eye;        // 对比色相、满饱和（黑影里两粒彩灯）
    public readonly Color Decoration; // 角尖/背饰亮色
    public readonly float Dominance;
    public readonly float Sympathy;
    public readonly float Aggression;
    public readonly float Nervous;
    public readonly float Energy;

    public ScavRenderProfile(Color body, Color head, Color eye, Color decoration,
        float dominance, float sympathy, float aggression, float nervous, float energy)
    {
        Body = body;
        Head = head;
        Eye = eye;
        Decoration = decoration;
        Dominance = dominance;
        Sympathy = sympathy;
        Aggression = aggression;
        Nervous = nervous;
        Energy = energy;
    }

    public static ScavRenderProfile ForBreed(string breedName) => breedName switch
    {
        // brute：暗色重型——支配/好斗拉满 → 大角长牙缝眼（参考图三的暗影气质）。
        "brute" => new ScavRenderProfile(
            new Color(0.22f, 0.16f, 0.13f), new Color(0.050f, 0.045f, 0.040f),
            new Color(1.00f, 0.35f, 0.12f), new Color(0.72f, 0.64f, 0.50f),
            dominance: 0.92f, sympathy: 0.15f, aggression: 0.85f,
            nervous: 0.15f, energy: 0.35f),
        // waif：灰绿瘦小——心善神经质 → 大眼真瞳孔、勤眨眼、待机微颤。
        "waif" => new ScavRenderProfile(
            new Color(0.52f, 0.54f, 0.46f), new Color(0.058f, 0.060f, 0.068f),
            new Color(0.55f, 0.90f, 1.00f), new Color(0.72f, 0.58f, 0.88f),
            dominance: 0.12f, sympathy: 0.85f, aggression: 0.30f,
            nervous: 0.90f, energy: 0.75f),
        // scavenger：土黄基准（参考图二的沙褐身体 + 黑脸）。
        _ => new ScavRenderProfile(
            new Color(0.60f, 0.48f, 0.32f), new Color(0.075f, 0.055f, 0.045f),
            new Color(0.85f, 0.93f, 0.42f), new Color(0.95f, 0.65f, 0.25f),
            dominance: 0.50f, sympathy: 0.50f, aggression: 0.50f,
            nervous: 0.45f, energy: 0.55f),
    };
}

/// <summary>
/// 人形正式渲染器（技术验证件六号，≙ ScavengerGraphics 的 3D 移植；真相源 =
/// scratchpad spider_scav_render/scavenger_graphics.md 逐行取证）。
/// 躯干 = 五点脊柱（头/脖/胸/**背凸腰点**/髋）Catmull-Rom 变径扫管——驼背「?」形剪影的
/// 几何本体是腰点沿背侧法线外推（≙ drawPositions[3] − Perp×Lerp(5,15,narrowWaist)）；
/// 头 = 独立近黑竖长椭球（≙ Circle20 16×22px，light 0.05~0.2 生而近黑）+ 三件怪脸：
/// 牙 = **头色**刀片从脸下缘辐射刺出头轮廓（≙ TeethSprite——不是白牙，是颅骨下颚的
/// 锯齿剪影，20%/颗 缺牙）、眼 = 微小满饱和对比色亮点（unshaded，斜吊向共同汇聚点，
/// 眨眼直接抹零、昏迷半睁死鱼眼 ≙ eyesOpen→0.5）、鹿角须 eartlers = seed 冻结的
/// 2~4 对分支模板（主支上弯 + 分叉 + 鬓角 + 后枕支，dominance 定尺寸、角尖染饰色）；
/// 脖管顶点色做体→头渐变（黑头不突兀的全部秘密）；手臂/腿 = TwoBoneIk 肘膝 + 细锥管
/// （臂半宽 ≙ 0.75~2.75px 细竿），手向头色渐变（≙ handsHeadColor 深色手套）；
/// 背饰 = 脊背 UV 上的鳞片刀片阵（≙ LizardScale 复用 + dominance 定大小）；
/// 尾 = 渲染侧 verlet 短锥链（≙ TailSegment 0~4 节）。持物画长矛钉 MainHandPos/Dir
/// （≙ RW grasp 0 硬钉），交互投掷的飞行道具同款矛（方向 = 渲染侧前帧差分）。
/// 有意偏离：RW 的 flip/xSqueeze/眼贴边归零全是 2D 透视伪装——3D 真旋转自带自遮挡，
/// 一律不移植；图形层 UnityEngine.Random 换 seed 普查 + 渲染侧私有 PRNG/sin 相位。
/// 对内核只读；顶点色 sRGB 语义（srgbVertexColors:true，与头/眼椭球 AlbedoColor 同空间）。
/// </summary>
internal sealed class HumanoidFormalRenderer : IFormalRenderer
{
    private readonly HumanoidLocomotionController _c;
    private readonly ScavRenderProfile _p;
    private readonly HumanoidSandboxDriver _driver;
    private readonly int _seed;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private MeshInstance3D? _head;
    private readonly MeshInstance3D?[] _eyes = new MeshInstance3D?[2];
    private readonly MeshInstance3D?[] _pupils = new MeshInstance3D?[2];

    // —— seed 冻结的形状基因（≙ IndividualVariations）——
    private float _headSize;
    private float _fatness;
    private float _narrowWaist;
    private float _neckThickness;
    private float _eyeSize;
    private float _narrowEyes;
    private float _eyesAngle;
    private bool _hasPupils;
    private float _armThickness;
    private float _legsSize;
    private bool _coloredEartlerTips;
    private float _wideTeeth;
    private int _tailSegs;

    private readonly struct Tooth
    {
        public readonly float T;      // 沿下颌弧 −1..1
        public readonly float Length; // 头半径倍数
        public readonly float Width;

        public Tooth(float t, float length, float width)
        {
            T = t;
            Length = length;
            Width = width;
        }
    }

    private Tooth[] _teeth = Array.Empty<Tooth>();

    private readonly struct EartlerVertex
    {
        public readonly Vector2 Pos;  // 头局部 (lateral, up) 平面，头半径为单位
        public readonly float Rad;

        public EartlerVertex(Vector2 pos, float rad)
        {
            Pos = pos;
            Rad = rad;
        }
    }

    private readonly List<EartlerVertex[]> _eartlerBranches = new();
    private float[] _eartlerFwdOffsets = Array.Empty<float>();
    private float _eartlerScale;

    private readonly struct TuftSpec
    {
        public readonly float SpineT;    // 沿脊柱 0(头端)..1(髋端)
        public readonly float Lateral;   // −1..1
        public readonly float Length;
        public readonly float SwayPhase;
        public readonly bool Colored;

        public TuftSpec(float spineT, float lateral, float length, float swayPhase, bool colored)
        {
            SpineT = spineT;
            Lateral = lateral;
            Length = length;
            SwayPhase = swayPhase;
            Colored = colored;
        }
    }

    private TuftSpec[] _tufts = Array.Empty<TuftSpec>();

    // —— 渲染侧化妆状态（私有，不写回内核、不进哈希）——
    private Vector3 _spineUp = Vector3.Up;
    private Vector3 _headFwd = Vector3.Right;
    private float _eyesOpen = 1f;
    private float _blinkTimer = 2f;
    private float _blinkHold;
    private readonly Random _blinkRng;  // 渲染侧私有节律源（≙ blink 随机重掷，不进哈希）
    private float _time;
    private Vector3[] _tailPos = Array.Empty<Vector3>();
    private Vector3[] _tailLast = Array.Empty<Vector3>();
    private bool _tailInitialized;
    private Vector3 _prevPropPos;
    private Vector3 _propDir = Vector3.Right;
    private bool _hadProp;

    private readonly List<TubeStation> _stations = new();
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly Vector3[] _spinePts = new Vector3[5];
    private readonly float[] _spineRadii = new float[5];

    public HumanoidFormalRenderer(HumanoidLocomotionController controller, string breedName,
        HumanoidSandboxDriver driver)
    {
        _c = controller;
        _p = ScavRenderProfile.ForBreed(breedName);
        _driver = driver;
        uint h = 2166136261u;
        foreach (char ch in breedName)
        {
            h = (h ^ ch) * 16777619u;
        }
        _seed = unchecked((int)h);
        _blinkRng = new Random(_seed ^ 0x5f3759df);
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root, srgbVertexColors: true);

        // 头椭球 + 眼 + 瞳孔：AlbedoColor（sRGB）与顶点色同空间——脖管末端与头同色融合。
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 };
        _head = new MeshInstance3D
        {
            Mesh = sphere,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = _p.Head,
            },
        };
        _root.AddChild(_head);
        var eyeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _p.Eye,
        };
        var pupilMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.03f, 0.02f, 0.02f),
        };
        for (int i = 0; i < 2; i++)
        {
            _eyes[i] = new MeshInstance3D { Mesh = sphere, MaterialOverride = eyeMat };
            _root.AddChild(_eyes[i]);
            _pupils[i] = new MeshInstance3D
            {
                Mesh = sphere,
                MaterialOverride = pupilMat,
                Visible = false,
            };
            _root.AddChild(_pupils[i]);
        }

        SeedGenes();
    }

    /// <summary>形状基因普查（seed 冻结，≙ Random.InitState(ID.RandomSeed) 的 iVars 段）：
    /// 体型、脸、牙、鹿角须、背饰、尾全部出生定死；性格旋钮按 RW 公式参与。</summary>
    private void SeedGenes()
    {
        var rng = new Random(_seed);
        float R() => (float)rng.NextDouble();

        _headSize = Mathf.Lerp(0.88f, 1.10f, R());
        _fatness = Mathf.Lerp(0.25f, 0.9f, Mathf.Lerp(R(), _p.Dominance, 0.6f));
        _narrowWaist = Mathf.Lerp(0.35f, 0.95f, Mathf.Lerp(R(), 1f - _fatness, 0.5f));
        _neckThickness = Mathf.Lerp(0.5f, 1.5f, Mathf.Lerp(R(), 1f - _fatness, 0.4f));
        // 心善眼大（≙ eyeSize pow(…, Lerp(0.95,0.55,sympathy))）；好斗/支配 → 缝眼。
        _eyeSize = Mathf.Lerp(0.35f, 1.0f, Mathf.Lerp(R(), _p.Sympathy, 0.7f));
        _narrowEyes = _p.Sympathy > 0.6f ? 0f
            : Mathf.Pow(R(), Mathf.Lerp(1.5f, 0.5f, _p.Aggression));
        _eyesAngle = Mathf.Lerp(0.45f, 0.95f, R());  // 普遍斜吊（≙ rand^~0.5）
        _hasPupils = _p.Sympathy > 0.55f;            // ≙ rand < sympathy^1.5×0.8 的定档化
        _armThickness = Mathf.Lerp(0.7f, 1.3f, Mathf.Lerp(R(), _fatness, 0.5f));
        _legsSize = Mathf.Lerp(1f, 1.4f, R());
        _coloredEartlerTips = R() < 0.7f;
        _wideTeeth = R();
        _tailSegs = _p.Dominance > 0.8f ? 0 : rng.Next(2, 5);  // ≙ 50% 无尾的定档化

        // 牙：4/6/8 颗，中间长两端短，20%/颗 缺牙（scruffy 恒 1），好斗 → 长。
        int toothPairs = rng.Next(2, 5);
        int toothCount = toothPairs * 2;
        float lenBase = Mathf.Lerp(0.5f, 1.5f,
            Mathf.Pow(R(), 1.5f - _p.Aggression));
        var teeth = new List<Tooth>(toothCount);
        for (int i = 0; i < toothCount; i++)
        {
            if (R() < 0.2f)
            {
                continue;  // 缺牙
            }
            float t = toothCount <= 1 ? 0f : -1f + 2f * i / (toothCount - 1);
            float arc = Mathf.Sin((i + 0.5f) / toothCount * Mathf.Pi);
            teeth.Add(new Tooth(t,
                Mathf.Lerp(0.45f, 0.95f, arc * lenBase) * Mathf.Lerp(0.9f, 1.1f, R()),
                Mathf.Lerp(0.07f, 0.14f, R())));
        }
        _teeth = teeth.ToArray();

        SeedEartlers(rng);

        // 背饰：沿脊背散布的鳞片刀片（≙ WobblyBackTufts 位置普查；强者刺大）。
        int tuftCount = rng.Next(7, 13);
        _tufts = new TuftSpec[tuftCount];
        for (int i = 0; i < tuftCount; i++)
        {
            _tufts[i] = new TuftSpec(
                Mathf.Lerp(0.25f, 0.95f, R()),
                Mathf.Lerp(-0.7f, 0.7f, R()),
                Mathf.Lerp(0.05f, 0.13f, R()) * Mathf.Lerp(0.8f, 1.6f, _p.Dominance),
                R() * Mathf.Tau,
                R() < 0.4f);
        }

        _tailPos = new Vector3[Math.Max(2, _tailSegs + 1)];
        _tailLast = new Vector3[_tailPos.Length];
        _tailInitialized = false;
    }

    /// <summary>鹿角须分支模板（≙ Eartlers.GenerateSegments——**不是随机游走**，是
    /// 「主支上弯 + 分叉 + 可选鬓角 + 后枕支」的手工折线族 + 随机参数）。2D 平面 =
    /// 头局部 (lateral, up)，每支镜像成左右一对，另存逐支前后偏移补 3D 体积。</summary>
    private void SeedEartlers(Random rng)
    {
        float R() => (float)rng.NextDouble();
        _eartlerBranches.Clear();
        var fwdOffsets = new List<float>();
        _eartlerScale = Mathf.Lerp(1.4f, 3.1f, _p.Dominance);  // ≙ Lerp(15,35,dominance)px

        Vector2 Polar(float deg) =>
            new(Mathf.Cos(Mathf.DegToRad(deg)), Mathf.Sin(Mathf.DegToRad(deg)));

        // 主支：先竖起再外弯，尖端加宽（≙ r 1→1→1.5→2）。角度压低一档换取左右张开
        /// ——竖直丛读作「胡萝卜头」（v1 实测），鹿角感要靠横向发散。
        Vector2 p0 = Vector2.Zero;
        Vector2 p1 = p0 + Polar(Mathf.Lerp(42f, 75f, R())) * 0.42f;
        Vector2 p2 = p1 + Polar(Mathf.Lerp(22f, 52f, R())) * 0.34f;
        Vector2 p3 = p2 + Polar(Mathf.Lerp(5f, 38f, R())) * 0.38f;
        _eartlerBranches.Add(new[]
        {
            new EartlerVertex(p0, 1f), new EartlerVertex(p1, 1f),
            new EartlerVertex(p2, 1.5f), new EartlerVertex(p3, 2f),
        });
        fwdOffsets.Add(0f);

        // 分叉支：从主支第 1 点沿末段方向平移 + 抖动（≙ 宽 1.2→1.75）。
        Vector2 forkDir = (p2 - p1).Normalized();
        Vector2 f0 = p1;
        Vector2 f1 = f0 + forkDir * 0.22f
            + Polar(R() * 360f) * 0.10f;
        Vector2 f2 = f1 + (forkDir + Polar(Mathf.Lerp(60f, 100f, R())) * 0.6f).Normalized() * 0.24f;
        _eartlerBranches.Add(new[]
        {
            new EartlerVertex(f0, 1.2f), new EartlerVertex(f1, 1.4f),
            new EartlerVertex(f2, 1.75f),
        });
        fwdOffsets.Add(0.10f);

        // 鬓角支（50%）：脸侧短刺，可再向后下折一段。
        if (R() < 0.5f)
        {
            Vector2 s0 = new(0.08f, -0.05f);
            Vector2 s1 = s0 + Polar(Mathf.Lerp(70f, 110f, R())) * Mathf.Lerp(0.2f, 0.4f, R());
            if (R() < 0.5f)
            {
                Vector2 s2 = s1 + Polar(Mathf.Lerp(120f, 170f, R())) * 0.18f;
                _eartlerBranches.Add(new[]
                {
                    new EartlerVertex(s0, 0.9f), new EartlerVertex(s1, 1.1f),
                    new EartlerVertex(s2, 1.3f),
                });
            }
            else
            {
                _eartlerBranches.Add(new[]
                {
                    new EartlerVertex(s0, 0.9f), new EartlerVertex(s1, 1.2f),
                });
            }
            fwdOffsets.Add(0.22f);
        }

        // 后枕支（75%）：向后上方 2~3 段，宽度可放大。
        if (R() < 0.75f)
        {
            Vector2 b0 = new(-0.06f, -0.02f);
            Vector2 b1 = b0 + Polar(Mathf.Lerp(95f, 135f, R())) * 0.28f;
            Vector2 b2 = b1 + Polar(Mathf.Lerp(100f, 150f, R())) * 0.24f;
            _eartlerBranches.Add(new[]
            {
                new EartlerVertex(b0, 1f), new EartlerVertex(b1, 1.3f),
                new EartlerVertex(b2, 1f + 1.5f * R()),
            });
            fwdOffsets.Add(-0.18f);
        }
        _eartlerFwdOffsets = fwdOffsets.ToArray();
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _head = null;
        for (int i = 0; i < 2; i++)
        {
            _eyes[i] = null;
            _pupils[i] = null;
        }
        _tailInitialized = false;
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
        if (_root is null)
        {
            return;
        }
        _time += dt;
        float h = Mathf.Clamp(dt, 0f, 1f / 30f);

        Vector3 chest = _c.Chest.LerpPos(alpha);
        Vector3 hips = _c.Hips.LerpPos(alpha);
        Vector3 headPos = _c.Head.LerpPos(alpha);

        Vector3 axis = chest - hips;
        axis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
        float k = 1f - Mathf.Exp(-8f * dt);
        _spineUp = _spineUp.Lerp(axis, k).Normalized();

        Vector3 facing = _c.Facing;
        Vector3 right = facing.Cross(Vector3.Up);
        right = right.LengthSquared() > 1e-6f ? right.Normalized()
            : _spineUp.Cross(Vector3.Right).Normalized();
        // 背侧法线：−facing 去脊柱轴分量——驼背腰点沿它外凸。
        Vector3 dorsal = -facing - _spineUp * (-facing).Dot(_spineUp);
        dorsal = dorsal.LengthSquared() > 1e-6f ? dorsal.Normalized() : -facing;

        UpdateExpression(dt);

        _tube.BeginFrame();
        DrawTorso(chest, hips, headPos, dorsal, right, out Vector3 waistPoint);
        DrawNeck(chest, headPos, dorsal);
        DrawHeadAndFace(chest, headPos, facing, right, alpha);
        for (int i = 0; i < _c.Arms.Count; i++)
        {
            DrawArm(_c.Arms[i], chest, facing, right, alpha);
        }
        foreach (Limb leg in _c.Legs)
        {
            DrawLeg(leg, hips, facing, right, alpha);
        }
        DrawTail(hips, dorsal, h);
        DrawProps(alpha);
        _tube.EndFrame();
    }

    // ———————————————————————— 表情标量 ————————————————————————

    /// <summary>≙ blink/eyesOpen 状态机：随机重掷倒计时（nervous → 眨得勤）、闭眼两帧、
    /// 睁眼速度吃 energy；昏迷收敛到 0.5 半睁死鱼眼（≙ !Consious → eyesOpen→0.5）。
    /// 节律源是渲染侧私有 PRNG——不进 tick/哈希。</summary>
    private void UpdateExpression(float dt)
    {
        if (!_c.Conscious)
        {
            _eyesOpen = Mathf.Lerp(_eyesOpen, 0.5f, Mathf.Min(1f, 4f * dt));
            return;
        }
        if (_blinkHold > 0f)
        {
            _blinkHold -= dt;
            _eyesOpen = Mathf.Max(0f, _eyesOpen - 12f * dt);
        }
        else
        {
            _blinkTimer -= dt;
            if (_blinkTimer <= 0f)
            {
                _blinkHold = 0.09f;
                // ≙ blink = Random(4, rand<0.5 ? 12 : Lerp(800,200,nervous)) 的秒化。
                _blinkTimer = _blinkRng.NextDouble() < 0.5
                    ? 0.3f + (float)_blinkRng.NextDouble() * 0.5f
                    : Mathf.Lerp(8f, 2f, _p.Nervous) * (0.5f + (float)_blinkRng.NextDouble());
            }
            _eyesOpen = Mathf.Min(1f,
                _eyesOpen + (0.05f + 0.95f * _p.Energy) * 8f * dt);
        }
    }

    // ———————————————————————— 躯干 ————————————————————————

    /// <summary>五点脊柱扫管（≙ drawPositions[5,2]）：头→脖→胸→腰→髋。驼背的几何本体 =
    /// 腰点沿背侧法线外推 Lerp(0.10,0.28,narrowWaist)×胸径（≙ Perp×Lerp(5,15,narrowWaist)），
    /// 脖点沿「腰→胸」切线外推——脊线自然弯成「?」。宽度剖面 ≙ RW 宽度表：
    /// 脖细、胸最宽（吃 fatness）、腰掐（沙漏）、髋中等。</summary>
    private void DrawTorso(Vector3 chest, Vector3 hips, Vector3 headPos,
        Vector3 dorsal, Vector3 right, out Vector3 waistPoint)
    {
        float chestR = _c.Chest.Radius;
        float hipsR = _c.Hips.Radius;
        waistPoint = chest.Lerp(hips, 0.55f)
            + dorsal * (chestR * Mathf.Lerp(0.20f, 0.44f, _narrowWaist));
        Vector3 neckDir = chest - waistPoint;
        neckDir = neckDir.LengthSquared() > 1e-8f ? neckDir.Normalized() : _spineUp;
        Vector3 neckPoint = chest + neckDir * (chestR * 0.55f);

        _spinePts[0] = neckPoint + neckDir * (chestR * 0.25f);
        _spinePts[1] = neckPoint;
        _spinePts[2] = chest;
        _spinePts[3] = waistPoint;
        _spinePts[4] = hips - _spineUp * (hipsR * 0.35f);
        _spineRadii[0] = chestR * 0.36f * _neckThickness;
        _spineRadii[1] = chestR * Mathf.Lerp(0.74f, 0.98f, _fatness);
        _spineRadii[2] = chestR * Mathf.Lerp(1.00f, 1.35f, _fatness);
        _spineRadii[3] = hipsR * Mathf.Lerp(0.90f, 0.50f, _narrowWaist);
        _spineRadii[4] = hipsR * 0.85f;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        for (int i = 0; i < 5; i++)
        {
            _pts.Add(_spinePts[i]);
            _radii.Add(_spineRadii[i]);
            _colors.Add(_p.Body);
        }
        SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
        _tube.AddTube(_stations, dorsal, 10);

        DrawBackTufts(dorsal, right);
    }

    /// <summary>背饰鳞片（≙ BackDecals/WobblyBackTufts：LizardScale 复用 + 强者刺大）：
    /// 刀片钉在脊背管表面，指向背侧偏后，sin 相位微摆（verlet 版留作后续）。</summary>
    private void DrawBackTufts(Vector3 dorsal, Vector3 right)
    {
        foreach (TuftSpec tuft in _tufts)
        {
            // 脊柱 t → 分段线性求位置与半径（普查冻结的 UV，≙ OnSpinePos）。
            float ft = tuft.SpineT * 4f;
            int seg = Mathf.Clamp((int)ft, 0, 3);
            float local = ft - seg;
            Vector3 basePos = _spinePts[seg].Lerp(_spinePts[seg + 1], local);
            float baseR = Mathf.Lerp(_spineRadii[seg], _spineRadii[seg + 1], local);

            Vector3 outDir = (dorsal + right * (tuft.Lateral * 0.8f)).Normalized();
            Vector3 root = basePos + outDir * (baseR * 0.92f);
            float sway = Mathf.Sin(_time * 1.3f + tuft.SwayPhase) * 0.12f;
            Vector3 dir = (outDir + _spineUp * (0.35f + sway) + right * (sway * 0.5f))
                .Normalized();
            Color tipCol = tuft.Colored
                ? _p.Body.Lerp(_p.Decoration, 0.55f)
                : _p.Body.Lerp(_p.Head, 0.4f);
            _tube.AddBlade(root, root + dir * tuft.Length, right, tuft.Length * 0.18f,
                0.45f, _p.Body, tipCol);
        }
    }

    /// <summary>脖管：胸上缘 → 头心，根粗头细、中段掐一口（≙ Lerp(7,3,t) − 2sin(tπ)×
    /// neckThickness），顶点色逐段体→头渐变（≙ 脖 customColor mesh——近黑的头从彩色身体
    /// 上长出来不突兀的全部秘密）。两端各沉进胸/头内部融根。</summary>
    private void DrawNeck(Vector3 chest, Vector3 headPos, Vector3 dorsal)
    {
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        const int neckPoints = 5;
        float headR = _c.Head.Radius * _headSize;
        Vector3 start = chest + (headPos - chest).Normalized() * (_c.Chest.Radius * 0.35f);
        for (int i = 0; i < neckPoints; i++)
        {
            float t = i / (float)(neckPoints - 1);
            Vector3 pos = start.Lerp(headPos, t);
            // 中段向背侧微弯（驼背脖子从背侧绕上来）。
            pos += dorsal * (Mathf.Sin(t * Mathf.Pi) * headR * 0.18f);
            float r = Mathf.Lerp(_c.Chest.Radius * 0.32f, headR * 0.40f, t)
                - Mathf.Sin(t * Mathf.Pi) * headR * 0.18f * _neckThickness;
            _pts.Add(pos);
            _radii.Add(Mathf.Max(headR * 0.14f, r));
            _colors.Add(_p.Body.Lerp(_p.Head, Mathf.Pow(t, 1.3f)));
        }
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, dorsal, 8);
    }

    // ———————————————————————— 头 + 脸 ————————————————————————

    /// <summary>头 = 竖长近黑椭球（≙ Circle20 16×22px、抬头变扁不移植——3D 真旋转）；
    /// 朝向 ≙ HeadDir：主要由头相对胸的物理偏移决定（0.8），视线/Facing 只占小头（0.2）
    /// ——真身体先转、脸再跟。牙从脸下缘辐射刺出轮廓（头色锯齿颅骨），眼斜吊向共同
    /// 汇聚点（eyesAngle），nervous 高的个体脸部微颤（≙ 神经质待机抖）。</summary>
    private void DrawHeadAndFace(Vector3 chest, Vector3 headPos, Vector3 facing,
        Vector3 right, float alpha)
    {
        float headR = _c.Head.Radius * _headSize;
        Vector3 offset = headPos - chest;
        // ≙ HeadDir：头相对胸的物理偏移为主、视线为辅——但本项目头挂在胸上方 0.55m
        // （RW 2D 头在前），照抄权重会让脸仰天、牙横长（v2 实测）。压掉大部分竖直分量、
        // 再给一点向下俯视——弓背个体的凝视线天然朝前下。
        Vector3 headOffsetDir = offset.LengthSquared() > 1e-8f ? offset.Normalized() : facing;
        Vector3 gaze = facing + headOffsetDir * 0.40f - _spineUp * 0.18f;
        gaze = gaze.Normalized();
        _headFwd = _headFwd.Lerp(gaze, 0.35f);
        Vector3 headFwd = _headFwd.LengthSquared() > 1e-8f
            ? _headFwd.Normalized() : facing;
        Vector3 headUp = _spineUp - headFwd * _spineUp.Dot(headFwd);
        headUp = headUp.LengthSquared() > 1e-6f ? headUp.Normalized() : Vector3.Up;
        Vector3 headRight = headFwd.Cross(headUp).Normalized();

        // 神经质待机微颤（≙ nervous>0.8 的胸位抖动——挪到头上更醒目，量级 1.5px）。
        Vector3 jitter = Vector3.Zero;
        if (_p.Nervous > 0.7f && _c.Conscious)
        {
            float amp = 0.006f * (_p.Nervous - 0.7f) / 0.3f;
            jitter = new Vector3(
                Mathf.Sin(_time * 43f), Mathf.Sin(_time * 51f + 1.3f),
                Mathf.Sin(_time * 47f + 2.6f)) * amp;
        }
        Vector3 drawPos = headPos + jitter;

        if (_head is not null)
        {
            // 竖长椭球：长轴沿头 up（16×22px 比例 ≙ 0.73:1，再拉长一档强化颅骨感）。
            _head.Transform = new Transform3D(
                new Basis(
                    headRight * (headR * 0.72f),
                    headUp * (headR * 1.28f),
                    headFwd * (headR * 0.72f)),
                drawPos);
        }

        Vector3 faceCenter = drawPos + headFwd * (headR * 0.52f) - headUp * (headR * 0.10f);

        // 牙：脸下缘辐射刀片，**头色**——伸出头椭球轮廓的部分读作颅骨锯齿（≙ 牙=
        // blendedHeadColor 画在头之上；白牙是错的，RW 的怪异感恰恰来自「不是嘴」）。
        foreach (Tooth tooth in _teeth)
        {
            Vector3 baseP = faceCenter
                + headRight * (tooth.T * headR * 0.52f * Mathf.Lerp(0.6f, 1.2f, _wideTeeth))
                - headUp * (headR * 0.30f);
            Vector3 dir = (headFwd * 0.35f - headUp * 0.9f
                + headRight * (tooth.T * 0.45f)).Normalized();
            Vector3 tip = baseP + dir * (tooth.Length * headR * 1.6f);
            _tube.AddBlade(baseP - headUp * (headR * 0.05f), tip, headRight,
                tooth.Width * headR, 0.4f, _p.Head, _p.Head);
        }

        DrawEyes(faceCenter, headFwd, headUp, headRight, headR);
        DrawEartlers(drawPos, headFwd, headUp, headRight, headR);
    }

    /// <summary>眼：微小满饱和亮点，双眼旋向共同汇聚点（≙ float19 = 脸锚 + 脸向×
    /// Lerp(−5,60,eyesAngle)——汇聚点远在脸前 → 倒八字斜吊）。眨眼把竖向 scale 抹零、
    /// narrowEyes 压横向成缝、瞳孔（sympathy 个体）追足端下方视线目标的简化：追 Facing。</summary>
    private void DrawEyes(Vector3 faceCenter, Vector3 headFwd,
        Vector3 headUp, Vector3 headRight, float headR)
    {
        Vector3 converge = faceCenter
            + headFwd * (headR * Mathf.Lerp(-0.3f, 3.5f, _eyesAngle))
            - headUp * (headR * 0.4f);
        float eyeW = headR * Mathf.Lerp(0.10f, 0.30f, _eyeSize)
            * Mathf.Lerp(1f, 0.35f, _narrowEyes);
        float eyeH = headR * Mathf.Lerp(0.14f, 0.34f, _eyeSize)
            * Mathf.Clamp(_eyesOpen, 0f, 1f);
        eyeH = Mathf.Max(eyeH, headR * 0.015f);

        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 center = faceCenter
                + headRight * (side * headR * 0.40f)
                + headUp * (headR * 0.08f)
                + headFwd * (headR * 0.16f);
            // 斜吊：眼长轴指向汇聚点（投影到脸平面）。
            Vector3 toConverge = converge - center;
            Vector3 slant = toConverge - headFwd * toConverge.Dot(headFwd);
            slant = slant.LengthSquared() > 1e-8f ? slant.Normalized() : headUp;

            if (_eyes[i] is { } eye)
            {
                Vector3 across = headFwd.Cross(slant).Normalized();
                eye.Transform = new Transform3D(
                    new Basis(across * eyeW, slant * eyeH, headFwd * (headR * 0.05f)),
                    center);
            }
            if (_pupils[i] is { } pupil)
            {
                pupil.Visible = _hasPupils;
                if (_hasPupils)
                {
                    // 瞳孔沿视线在眼面滑动（≙ DirVec(眼→lookPoint)×sympathy 加权的简化）。
                    Vector3 look = _c.Facing;
                    Vector3 slide = look - headFwd * look.Dot(headFwd);
                    Vector3 pupilPos = center + headFwd * (headR * 0.055f)
                        + slide * (eyeW * 0.3f);
                    float pr = Mathf.Min(eyeW, eyeH) * 0.5f;
                    pupil.Transform = new Transform3D(
                        new Basis(
                            headRight * pr, headUp * (pr * Mathf.Min(1f, _eyesOpen * 2f)),
                            headFwd * (headR * 0.03f)),
                        pupilPos);
                }
            }
        }
    }

    /// <summary>鹿角须：模板折线族按头局部系 (lateral=headRight, up=headUp) 展开，左右
    /// 镜像 + 逐支前后偏移；宽度 = 逐点 rad × eartlerWidth，末端 ÷4 收尖；角尖染饰色
    /// （≙ coloredEartlerTips 最后 2 顶点 = decorationColor）。dominance 定整体尺寸
    /// （≙ Lerp(15,35,dominance)px——强者大角）。根部沉进头椭球融根。</summary>
    private void DrawEartlers(Vector3 headDrawPos, Vector3 headFwd, Vector3 headUp,
        Vector3 headRight, float headR)
    {
        Vector3 crown = headDrawPos + headUp * (headR * 0.62f);
        float scale = headR * _eartlerScale;

        for (int b = 0; b < _eartlerBranches.Count; b++)
        {
            EartlerVertex[] branch = _eartlerBranches[b];
            float fwdOff = _eartlerFwdOffsets[b] * headR;
            for (int mirror = 0; mirror < 2; mirror++)
            {
                float side = mirror == 0 ? -1f : 1f;
                _pts.Clear();
                _radii.Clear();
                _colors.Clear();
                for (int v = 0; v < branch.Length; v++)
                {
                    EartlerVertex vert = branch[v];
                    Vector3 pos = crown
                        + headRight * (vert.Pos.X * scale * side)
                        + headUp * (vert.Pos.Y * scale)
                        + headFwd * fwdOff;
                    bool isTip = v == branch.Length - 1;
                    float r = 0.012f * Mathf.Lerp(0.8f, 1.6f, _eartlerScale / 2.6f)
                        * vert.Rad * (isTip ? 0.25f : 1f);
                    Color col = isTip && _coloredEartlerTips
                        ? _p.Decoration
                        : _p.Head;
                    _pts.Add(pos);
                    _radii.Add(r);
                    _colors.Add(col);
                }
                SplineSampler.Sample(_pts, _radii, _colors, 2, _stations);
                _tube.AddTube(_stations, headFwd, 5);
            }
        }
    }

    // ———————————————————————— 手臂 / 腿 ————————————————————————

    /// <summary>臂：肩（胸侧）→ 肘（TwoBoneIk，两骨等长）→ 腕 → 手瘤。细竿半径 ≙
    /// 0.75~2.75px×armThickness；手与臂末端向头色渐变（≙ handsHeadColor 深色手套）；
    /// 抓握态（GrabPos 锁点/持物）手瘤放大压扁的简化 = 双瘤（≙ ScavengerHandB）。</summary>
    private void DrawArm(Arm arm, Vector3 chest, Vector3 facing, Vector3 right, float alpha)
    {
        Vector3 hand = arm.LerpPos(alpha);
        Vector3 shoulder = chest
            + right * (arm.Side * _c.Chest.Radius * 0.72f)
            + _spineUp * (_c.Chest.Radius * 0.30f);
        float bone = arm.ArmLength * 0.52f;
        // 肘弯向：外侧偏后上（弓背指关节行走的肘外撇）。
        Vector3 pole = (right * arm.Side * 0.7f + _spineUp * 0.5f - facing * 0.45f)
            .Normalized();
        Vector3 elbow = TwoBoneIk.Solve(shoulder, hand, bone, bone, pole);
        Vector3 wrist = hand + (elbow - hand).Normalized() * 0.05f;

        // 臂比腿厚一档且末端压暗——不然细竿贴着同色腿在剪影里融掉（v1 实测）。
        float thick = 0.040f * _armThickness;
        Color handCol = _p.Body.Lerp(_p.Head, 0.78f);

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(shoulder.Lerp(chest, 0.35f));
        _radii.Add(thick * 1.5f);
        _colors.Add(_p.Body);
        _pts.Add(elbow);
        _radii.Add(thick * 0.95f);
        _colors.Add(_p.Body.Lerp(handCol, 0.35f));
        _pts.Add(wrist);
        _radii.Add(thick * 0.68f);
        _colors.Add(_p.Body.Lerp(handCol, 0.8f));
        _pts.Add(hand);
        _radii.Add(thick * 0.6f);
        _colors.Add(handCol);
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, pole, 6);

        bool grabbing = arm.GrabPos is not null
            || (_c.Carrying && _c.Arms.Count > 0 && ReferenceEquals(arm, _c.Arms[0]));
        _tube.AddKnob(hand, arm.Radius * (grabbing ? 1.5f : 1.2f), handCol);
        if (grabbing)
        {
            // 抓握二态：多一粒指瘤（≙ HandB 抓握贴图）。
            Vector3 toGround = arm.GrabPos is { } gp ? (gp - hand).Normalized() : -_spineUp;
            _tube.AddKnob(hand + toGround * (arm.Radius * 0.8f), arm.Radius * 0.8f, handCol);
        }
    }

    /// <summary>腿：髋侧关节 → 膝（TwoBoneIk，pole 朝前 = 蹲屈膝）→ 踝 → 脚板小管。
    /// 半径剖面 ≙ RW 髋端 2、膝 3、踝 0.8（大腿窄、膝盖鼓、脚踝细的昆虫感细腿）。</summary>
    private void DrawLeg(Limb leg, Vector3 hips, Vector3 facing, Vector3 right, float alpha)
    {
        Vector3 foot = leg.LerpPos(alpha);
        Vector3 hipJoint = hips + right * (leg.Side * _c.Hips.Radius * 0.55f);
        float bone = leg.JointDist * 0.58f * _legsSize;
        Vector3 pole = (facing + right * (leg.Side * 0.25f)).Normalized();
        Vector3 knee = TwoBoneIk.Solve(hipJoint, foot, bone, bone, pole);
        Vector3 ankle = foot + (knee - foot).Normalized() * (leg.Radius * 0.8f);

        // 腿粗随体格：重型的庞躯配牙签腿会脱节（brute v1 实测）。
        float thick = Mathf.Lerp(0.014f, 0.024f, _fatness) * _legsSize;
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(hipJoint);
        _radii.Add(thick * 1.6f);
        _colors.Add(_p.Body);
        _pts.Add(knee);
        _radii.Add(thick * 2.1f);
        _colors.Add(_p.Body);
        _pts.Add(ankle);
        _radii.Add(thick * 0.75f);
        _colors.Add(_p.Body);
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, pole, 6);

        // 脚板：踝沿地向前探出的扁短管（≙ 顶点 8~11 的小脚板）。方向混入本腿姿态——
        // 摆动腿拖在身后时脚板不能还朝行进向硬指（v3 实测悬空外飘成小旗）。
        Vector3 legDir = foot - knee;
        legDir = legDir.LengthSquared() > 1e-8f ? legDir.Normalized() : -_spineUp;
        Vector3 toeDir = facing * 0.55f + legDir * 0.45f;
        toeDir -= _spineUp * toeDir.Dot(_spineUp) * 0.7f;
        toeDir = toeDir.LengthSquared() > 1e-8f ? toeDir.Normalized() : facing;
        _stations.Clear();
        _stations.Add(new TubeStation(ankle, leg.Radius * 0.8f, _p.Body));
        _stations.Add(new TubeStation(foot + toeDir * (leg.Radius * 1.5f),
            leg.Radius * 0.35f, _p.Body));
        _tube.AddTube(_stations, _spineUp, 5);
    }

    // ———————————————————————— 尾 / 道具 ————————————————————————

    /// <summary>尾：髋后下的渲染侧 verlet 短锥链（≙ TailSegment 0~4 节 + pointyTip），
    /// 半重力下垂 + 距离约束；50% 个体无尾在预设档案里定死。</summary>
    private void DrawTail(Vector3 hips, Vector3 dorsal, float h)
    {
        if (_tailSegs <= 0)
        {
            return;
        }
        int n = _tailPos.Length;
        Vector3 pin = hips + dorsal * (_c.Hips.Radius * 0.7f)
            - _spineUp * (_c.Hips.Radius * 0.45f);
        float segLen = 0.09f;
        if (!_tailInitialized)
        {
            for (int i = 0; i < n; i++)
            {
                _tailPos[i] = pin + dorsal * (segLen * i);
                _tailLast[i] = _tailPos[i];
            }
            _tailInitialized = true;
        }
        _tailPos[0] = pin;
        _tailLast[0] = pin;
        float dt40 = h * 40f;
        for (int i = 1; i < n; i++)
        {
            Vector3 vel = (_tailPos[i] - _tailLast[i]) * 0.90f;
            _tailLast[i] = _tailPos[i];
            vel += Vector3.Down * (0.011f * dt40);
            vel += dorsal * (0.004f * dt40);
            _tailPos[i] += vel;
        }
        for (int i = 1; i < n; i++)
        {
            Vector3 delta = _tailPos[i] - _tailPos[i - 1];
            float dist = delta.Length();
            if (dist > 1e-6f)
            {
                _tailPos[i] = _tailPos[i - 1] + delta * (segLen / dist);
            }
        }

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            _pts.Add(_tailPos[i]);
            _radii.Add(Mathf.Lerp(_c.Hips.Radius * 0.34f, 0.008f, t));
            _colors.Add(_p.Body);
        }
        SplineSampler.Sample(_pts, _radii, _colors, 2, _stations);
        _tube.AddTube(_stations, _spineUp, 6);
    }

    /// <summary>持物/投掷道具画成长矛（拾荒者的标志物）：持物钉 MainHandPos 沿 MainHandDir
    /// （≙ RW grasp 0 spearPosAdd 硬钉）；飞行道具位置来自宿主驱动器（纯视觉弹道），
    /// 朝向 = 渲染侧前帧差分（宿主只发布位置）。矛 = 木杆管 + 尖端刀片。</summary>
    private void DrawProps(float alpha)
    {
        var wood = new Color(0.32f, 0.24f, 0.15f);
        var tipCol = new Color(0.62f, 0.60f, 0.55f);
        if (_c.Carrying)
        {
            DrawSpear(_c.MainHandPos, _c.MainHandDir, wood, tipCol);
        }
        if (_driver.ThrownProp is { } prop)
        {
            if (_hadProp)
            {
                Vector3 delta = prop - _prevPropPos;
                if (delta.LengthSquared() > 1e-8f)
                {
                    _propDir = delta.Normalized();
                }
            }
            DrawSpear(prop, _propDir, wood, tipCol);
            _prevPropPos = prop;
            _hadProp = true;
        }
        else
        {
            _hadProp = false;
        }
    }

    private void DrawSpear(Vector3 center, Vector3 dir, Color wood, Color tipCol)
    {
        if (dir.LengthSquared() < 1e-8f)
        {
            dir = Vector3.Right;
        }
        dir = dir.Normalized();
        const float halfLen = 0.62f;
        Vector3 tail = center - dir * halfLen;
        Vector3 headBase = center + dir * (halfLen * 0.88f);
        Vector3 tip = center + dir * (halfLen * 1.12f);
        _stations.Clear();
        _stations.Add(new TubeStation(tail, 0.014f, wood));
        _stations.Add(new TubeStation(headBase, 0.017f, wood));
        _tube.AddTube(_stations, _spineUp, 5);
        Vector3 side = dir.Cross(_spineUp);
        side = side.LengthSquared() > 1e-6f
            ? side.Normalized() : dir.Cross(Vector3.Right).Normalized();
        _tube.AddBlade(headBase, tip, side, 0.030f, 0.35f, tipCol, tipCol);
    }
}
