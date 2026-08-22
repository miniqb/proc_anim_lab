using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.RatFiend;

namespace ProcAnimLab.Render;

/// <summary>鼠煞渲染调色板 + 性格档案。用户定档：苍白灰皮为主（参考图的恐怖谷瘦长鬼怪——
/// 暗鼻/暗爪尖/暗眼窝把剪影感拉回来），dusk 预设走项目一致的近黑身 + 骨白鼠颅
/// （Vulture 骨白头先例）。牙**有意偏离** Humanoid 的「头色颅骨锯齿」哲学——本怪是真嘴
/// （可开闭下颌 + 脏白牙 + 暗红口腔），参考图的恐怖来自嘴，不是「不是嘴」。</summary>
internal readonly struct RatFiendRenderProfile
{
    public readonly Color Body;
    public readonly Color Head;
    public readonly Color Teeth;    // 脏白（偏离 Humanoid：这是真牙）
    public readonly Color Maw;      // 口腔/喉内暗红
    public readonly Color Eye;      // 白浊（无瞳孔）
    public readonly Color Socket;   // 眼窝暗环（苍白头上白眼必须垫暗底才读得出）
    public readonly Color Wound;    // 残肢断口暗红
    public readonly Color EarInner;
    public readonly float Gauntness;  // 瘦削度 → 掐腰/椎骨凸
    public readonly float Hunch;      // 驼背度 → 背凸量基准
    public readonly float Aggression; // → 牙长/眼小
    public readonly float Nervous;    // → 眨眼频率/耳颤
    public readonly float Energy;     // → 睁眼速度/挣扎摆幅

    public RatFiendRenderProfile(Color body, Color head, Color teeth, Color maw,
        Color eye, Color socket, Color wound, Color earInner,
        float gauntness, float hunch, float aggression, float nervous, float energy)
    {
        Body = body;
        Head = head;
        Teeth = teeth;
        Maw = maw;
        Eye = eye;
        Socket = socket;
        Wound = wound;
        EarInner = earInner;
        Gauntness = gauntness;
        Hunch = hunch;
        Aggression = aggression;
        Nervous = nervous;
        Energy = energy;
    }

    public static RatFiendRenderProfile ForBreed(string breedName) => breedName switch
    {
        // dusk：暗剪影变体——近黑暖灰身 + 骨白鼠颅浮在上面（最「雨世界」的读法）。
        "dusk" => new RatFiendRenderProfile(
            new Color(0.105f, 0.095f, 0.090f), new Color(0.62f, 0.58f, 0.52f),
            new Color(0.85f, 0.82f, 0.72f), new Color(0.13f, 0.055f, 0.055f),
            new Color(0.88f, 0.89f, 0.85f), new Color(0.07f, 0.06f, 0.06f),
            new Color(0.24f, 0.09f, 0.08f), new Color(0.14f, 0.10f, 0.10f),
            gauntness: 0.85f, hunch: 0.7f, aggression: 0.8f, nervous: 0.35f, energy: 0.45f),
        // broad：重型——更沉的土灰、驼得浅一点、牙更长、迟钝少眨眼。
        "broad" => new RatFiendRenderProfile(
            new Color(0.50f, 0.47f, 0.44f), new Color(0.58f, 0.54f, 0.50f),
            new Color(0.82f, 0.78f, 0.66f), new Color(0.14f, 0.055f, 0.055f),
            new Color(0.85f, 0.85f, 0.80f), new Color(0.10f, 0.09f, 0.09f),
            new Color(0.24f, 0.09f, 0.08f), new Color(0.34f, 0.26f, 0.26f),
            gauntness: 0.6f, hunch: 0.6f, aggression: 0.9f, nervous: 0.15f, energy: 0.3f),
        // whelp：幼体——更苍白、神经质勤眨眼、牙短。
        "whelp" => new RatFiendRenderProfile(
            new Color(0.60f, 0.58f, 0.56f), new Color(0.66f, 0.63f, 0.60f),
            new Color(0.86f, 0.83f, 0.74f), new Color(0.13f, 0.055f, 0.055f),
            new Color(0.90f, 0.91f, 0.87f), new Color(0.12f, 0.11f, 0.11f),
            new Color(0.26f, 0.10f, 0.09f), new Color(0.42f, 0.34f, 0.34f),
            gauntness: 0.9f, hunch: 0.8f, aggression: 0.5f, nervous: 0.8f, energy: 0.8f),
        // gaunt：基线——参考图的苍白灰皮修长鬼怪。
        _ => new RatFiendRenderProfile(
            new Color(0.55f, 0.53f, 0.50f), new Color(0.63f, 0.60f, 0.56f),
            new Color(0.85f, 0.82f, 0.72f), new Color(0.13f, 0.055f, 0.055f),
            new Color(0.88f, 0.89f, 0.85f), new Color(0.10f, 0.09f, 0.09f),
            new Color(0.24f, 0.09f, 0.08f), new Color(0.38f, 0.30f, 0.30f),
            gauntness: 0.85f, hunch: 0.7f, aggression: 0.7f, nervous: 0.35f, energy: 0.45f),
    };
}

/// <summary>
/// 鼠煞正式渲染器（渲染件七号，本项目**首个可动颌**渲染件）。
/// 头 = **颅吻一体变径扫管**（楔形颅 → 细长吻，鼻尖渐暗）——刻意不用共享 SphereMesh 头：
/// 顶点色烘焙的包裹漫反射（TubeMeshBuilder.ShadeAmount）与平色球在苍白色下会在拼缝处
/// 露出 ~20% 亮度断层（近黑的 Humanoid 头看不见这个坑）。
/// 下颌 = 绕耳下铰点旋转的短管，张角由内核 MouthOpen 驱动（lerp(LastMouthOpen, MouthOpen,
/// alpha) 插值 + 渲染侧非对称低通——开快合稍慢）；口腔 = 沿两颌角平分线的暗红喉管，
/// 闭嘴时半径缩进颌内不可见。抗抖四件套：头帧每帧只算一次、张角单独低通、
/// 牙 = (头帧, 张角) 纯函数零累积、jawRest ≥3° 永不真正咬死（TwoBoneIk「永不绷直」的颌版）。
/// 躯干 = 七点脊柱**长弧**（颈根→肩坡→驼峰弧顶→中背→掐腰→髋→尾基，R12：隆起摊在
/// 三个控制点上沿轴单调推进，弧顶仍是全身最宽——背读成弓不读成球；躯干管终点即脖管
/// 起点，同点同半径无拼缝）+ 掐腰瘦削 + 椎骨凸；爬行时 dorsal 双源 slerp（站姿 −facing 系 /
/// 爬行 up 系，按内核 CrawlFactor 连续混合）——防脊轴转水平后驼峰方向乱转。
/// 四肢 = TwoBoneIk 肘膝（pole 三态混合：垂手肘朝后外 / 前伸肘外压 / **爬行肘朝上**）+
/// 钩爪（逐指两枚刀片折出钩形）；R14 一体化走 Lizard 路线：根点埋进躯干/骨盆内部、
/// 肥根快收锥（三角肌/臀肌），大腿肉取代膝球、掌/脚掌并进肢管一根扫到底、两骨中段
/// 沿 pole 微弓——关节位置与 RatFiendJointMath 逐字同源不动，只改管剖面。
/// 断肢画 同款埋根 → 内核残肢粒子的短残段 + 暗红断口瘤 + 碎肉刀片。
/// 尾 = 渲染侧 verlet 短锥链（环纹裸鼠尾）。
/// 化妆状态（嘴平滑/咬合前突/断肢抽搐/爬行挣扎/眨眼/呼吸/耳颤）全部渲染侧私有，
/// 不写回内核、不进哈希。对内核只读；srgbVertexColors:true（苍白调色在所见空间定档）。
/// </summary>
internal sealed class RatFiendFormalRenderer : IFormalRenderer
{
    private readonly RatFiendLocomotionController _c;
    private readonly RatFiendRenderProfile _p;
    private readonly int _seed;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private readonly MeshInstance3D?[] _eyes = new MeshInstance3D?[2];

    // —— seed 冻结的形状基因 ——
    private float _headSize;
    private float _snoutLen;      // ×headR
    private float _snoutDroop;    // ×headR
    private float _jawRestDeg;
    private float _jawGapeDeg;
    private float _fangMult;
    private float _earLen;        // ×headR
    private float _earSplay;
    private float _earAsym;       // 左右耳 ±比例差
    private float _humpGene;
    private float _gauntWaist;
    private float _armThin;
    private float _fingerLen;
    private float _fingerCurlDeg;
    private bool _hasThumbClaw;
    private float _legGene;
    private float _kneeBulge;
    private int _tailSegs;
    private float _eyeSize;

    private readonly struct Tooth
    {
        public readonly float T;       // 沿唇线 0(吻根)..1(吻尖)
        public readonly float Length;  // ×headR
        public readonly float Width;

        public Tooth(float t, float length, float width)
        {
            T = t;
            Length = length;
            Width = width;
        }
    }

    private Tooth[] _upperTeeth = Array.Empty<Tooth>();
    private Tooth[] _lowerTeeth = Array.Empty<Tooth>();

    private readonly struct VertebraSpec
    {
        public readonly float SpineT;
        public readonly float Size;

        public VertebraSpec(float spineT, float size)
        {
            SpineT = spineT;
            Size = size;
        }
    }

    private VertebraSpec[] _vertebrae = Array.Empty<VertebraSpec>();

    private readonly struct WoundBladeSpec
    {
        public readonly float Angle;
        public readonly float Length;

        public WoundBladeSpec(float angle, float length)
        {
            Angle = angle;
            Length = length;
        }
    }

    private WoundBladeSpec[] _woundBlades = Array.Empty<WoundBladeSpec>();

    // —— 渲染侧化妆状态（私有，不写回内核、不进哈希）——
    private Vector3 _spineUp = Vector3.Up;
    private Vector3 _headFwd = Vector3.Right;
    private Vector3 _dorsalDraw = Vector3.Back;
    private bool _dorsalInitialized;
    private readonly Vector3[] _armPole = new Vector3[2];
    private readonly bool[] _armPoleInit = new bool[2];
    private float _mouthSmoothed = 0.15f;
    private float _prevMouthSmoothed = 0.15f;
    private float _biteLunge;         // 咬合前突剩余秒
    private float _twitchTimer;       // 断肢抽搐剩余秒
    private int _prevSeverSerial;
    private float _eyesOpen = 1f;
    private float _blinkTimer = 2f;
    private float _blinkHold;
    private float _earFlick;
    private float _earFlickTimer = 3f;
    private readonly Random _blinkRng;  // 渲染侧私有节律源
    private float _time;
    private Vector3[] _tailPos = Array.Empty<Vector3>();
    private Vector3[] _tailLast = Array.Empty<Vector3>();
    private bool _tailInitialized;

    private readonly List<TubeStation> _stations = new();
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly Vector3[] _spinePts = new Vector3[7];
    private readonly float[] _spineRadii = new float[7];
    private readonly Vector3[] _snoutPts = new Vector3[5];
    private readonly float[] _snoutRadii = new float[5];

    public RatFiendFormalRenderer(RatFiendLocomotionController controller, string breedName)
    {
        _c = controller;
        _p = RatFiendRenderProfile.ForBreed(breedName);
        uint h = 2166136261u;
        foreach (char ch in breedName)
        {
            h = (h ^ ch) * 16777619u;
        }
        _seed = unchecked((int)h);
        _blinkRng = new Random(_seed ^ 0x5f3759df);
        _prevSeverSerial = controller.SeverSerial;
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root, srgbVertexColors: true);

        // 眼球是仅存的刚性件（小 + 垫了暗眼窝瘤，管/球拼缝不可见）。
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 16, Rings = 8 };
        var eyeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _p.Eye,
        };
        for (int i = 0; i < 2; i++)
        {
            _eyes[i] = new MeshInstance3D { Mesh = sphere, MaterialOverride = eyeMat };
            _root.AddChild(_eyes[i]);
        }

        SeedGenes();
    }

    /// <summary>形状基因普查（seed 冻结）：颅吻/颌/牙/耳/驼峰/掐腰/爪指/腿/尾出生定死。</summary>
    private void SeedGenes()
    {
        var rng = new Random(_seed);
        float R() => (float)rng.NextDouble();

        _headSize = Mathf.Lerp(0.90f, 1.10f, R());
        _snoutLen = Mathf.Lerp(1.5f, 1.95f, R());
        _snoutDroop = Mathf.Lerp(0f, 0.15f, R());
        _jawRestDeg = Mathf.Lerp(3f, 8f, R());
        _jawGapeDeg = Mathf.Lerp(45f, 55f, R());
        _fangMult = Mathf.Lerp(1.2f, 1.6f, R());
        _earLen = Mathf.Lerp(0.9f, 1.3f, R());
        _earSplay = Mathf.Lerp(0.25f, 0.5f, R());
        _earAsym = Mathf.Lerp(-0.08f, 0.08f, R());
        _humpGene = Mathf.Lerp(0.45f, 0.75f, Mathf.Lerp(R(), _p.Hunch, 0.6f));
        _gauntWaist = Mathf.Lerp(0.5f, 0.9f, Mathf.Lerp(R(), _p.Gauntness, 0.6f));
        _armThin = Mathf.Lerp(0.85f, 1.15f, R());
        _fingerLen = Mathf.Lerp(0.10f, 0.16f, R());
        _fingerCurlDeg = Mathf.Lerp(30f, 55f, R());
        _hasThumbClaw = R() < 0.4f;
        _legGene = Mathf.Lerp(1.0f, 1.25f, R());
        _kneeBulge = Mathf.Lerp(2.2f, 2.6f, R());
        _tailSegs = rng.Next(3, 5);
        _eyeSize = Mathf.Lerp(0.08f, 0.12f, Mathf.Lerp(R(), 1f - _p.Aggression, 0.5f));

        // 牙：上 5~8 / 下 4~7 每侧，12%/颗 缺牙，前犬齿加长（好斗 → 整体拉长）。
        Tooth[] SeedRow(int min, int max, float lenScale)
        {
            int count = rng.Next(min, max + 1);
            var teeth = new List<Tooth>(count);
            for (int i = 0; i < count; i++)
            {
                if (R() < 0.12f)
                {
                    continue; // 缺牙
                }
                float t = Mathf.Lerp(0.08f, 0.92f, (i + 0.5f) / count);
                float len = Mathf.Lerp(0.30f, 0.15f, t) * lenScale
                    * Mathf.Lerp(0.85f, 1.25f, Mathf.Lerp(R(), _p.Aggression, 0.5f));
                if (i == 0)
                {
                    len *= _fangMult; // 最前犬齿
                }
                teeth.Add(new Tooth(t, len, Mathf.Lerp(0.05f, 0.09f, R())));
            }
            return teeth.ToArray();
        }

        _upperTeeth = SeedRow(5, 8, 1f);
        _lowerTeeth = SeedRow(4, 7, 0.85f);

        // 椎骨凸：4~7 颗沿驼峰脊线（区间对准七点脊柱的肩坡→中背段 = 弧顶两侧）。
        int vertCount = rng.Next(4, 8);
        _vertebrae = new VertebraSpec[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            _vertebrae[i] = new VertebraSpec(
                Mathf.Lerp(0.14f, 0.52f, (i + 0.5f) / vertCount) + R() * 0.03f,
                Mathf.Lerp(0.11f, 0.16f, R()) * Mathf.Lerp(0.8f, 1.3f, _p.Gauntness));
        }

        // 断口碎肉刀片：2~3 枚，seed 角度冻结（左右残肢共用模板）。
        int bladeCount = rng.Next(2, 4);
        _woundBlades = new WoundBladeSpec[bladeCount];
        for (int i = 0; i < bladeCount; i++)
        {
            _woundBlades[i] = new WoundBladeSpec(
                R() * Mathf.Tau, Mathf.Lerp(0.02f, 0.045f, R()));
        }

        _tailPos = new Vector3[_tailSegs + 1];
        _tailLast = new Vector3[_tailPos.Length];
        _tailInitialized = false;
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        for (int i = 0; i < 2; i++)
        {
            _eyes[i] = null;
        }
        _tailInitialized = false;
        _dorsalInitialized = false;
        _armPoleInit[0] = false;
        _armPoleInit[1] = false;
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
        float runN = _c.RunBlend;
        float crawl = Mathf.Lerp(_c.LastCrawlFactor, _c.CrawlFactor, alpha);
        bool struggling = crawl > 0.5f && _c.HasMoveIntent && _c.Conscious;

        Vector3 axis = chest - hips;
        axis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
        float k = 1f - Mathf.Exp(-8f * dt);
        _spineUp = _spineUp.Lerp(axis, k).Normalized();

        Vector3 facing = _c.Facing;
        Vector3 right = facing.Cross(Vector3.Up);
        right = right.LengthSquared() > 1e-6f ? right.Normalized()
            : _spineUp.Cross(Vector3.Right).Normalized();

        UpdateDorsal(facing, crawl, dt);
        UpdateMouth(alpha, dt, runN, struggling);
        UpdateExpression(dt, struggling);
        UpdateTwitch(dt);

        _tube.BeginFrame();
        DrawTorso(chest, hips, right, crawl, runN, struggling, out Vector3 neckRoot);
        DrawNeck(chest, headPos, neckRoot, runN);
        DrawHead(chest, headPos, facing, runN, crawl, dt);
        for (int i = 0; i < _c.Arms.Count; i++)
        {
            DrawArm(_c.Arms[i], i, chest, facing, right, runN, crawl, alpha);
        }
        for (int i = 0; i < _c.Legs.Count; i++)
        {
            DrawLeg(_c.Legs[i], i, hips, facing, right, crawl, alpha);
        }
        DrawTail(hips, facing, crawl, h);
        _tube.EndFrame();
    }

    // ———————————————————————— 化妆标量 ————————————————————————

    /// <summary>背侧法线双源混合：站姿 = −facing 去脊柱轴分量（驼峰朝后），爬行 = 世界上
    /// 去脊柱轴分量（脊轴转水平后「背」朝上）——按内核 CrawlFactor 连续 slerp，终值再低通，
    /// 退化取上帧值（CLAUDE.md §6.2 惯例）。没有这层，断腿摔倒瞬间驼峰方向会绕轴乱转。</summary>
    private void UpdateDorsal(Vector3 facing, float crawl, float dt)
    {
        Vector3 dorsalStand = -facing - _spineUp * (-facing).Dot(_spineUp);
        dorsalStand = dorsalStand.LengthSquared() > 1e-6f ? dorsalStand.Normalized() : -facing;
        Vector3 dorsalCrawl = Vector3.Up - _spineUp * Vector3.Up.Dot(_spineUp);
        dorsalCrawl = dorsalCrawl.LengthSquared() > 1e-6f
            ? dorsalCrawl.Normalized()
            : dorsalStand;
        Vector3 blended = dorsalStand.Slerp(dorsalCrawl, Mathf.Clamp(crawl, 0f, 1f));
        if (!_dorsalInitialized)
        {
            _dorsalDraw = blended;
            _dorsalInitialized = true;
        }
        float k = 1f - Mathf.Exp(-6f * dt);
        Vector3 next = _dorsalDraw.Lerp(blended, k);
        if (next.LengthSquared() > 1e-6f)
        {
            _dorsalDraw = next.Normalized();
        }
    }

    /// <summary>嘴开度平滑：target = 内核插值开度（含 Gait 派生 + 宿主咬合驱动）+ 渲染附加
    /// （闲置微呼吸颌 / 跑步喘 / 挣扎加张 / 抽搐尖叫）。非对称低通（开 25 / 合 18）——
    /// 咬合尖峰不能被糊掉，低通只吃 40Hz 台阶。下行沿（咬合闭口）触发 0.12s 头前突。</summary>
    private void UpdateMouth(float alpha, float dt, float runN, bool struggling)
    {
        float target = Mathf.Lerp(_c.LastMouthOpen, _c.MouthOpen, alpha);
        target += 0.04f * Mathf.Sin(_time * 1.3f) * (1f - runN);   // 闲置微呼吸颌
        target += 0.05f * Mathf.Sin(_time * 7f) * runN;            // 跑步喘
        if (struggling)
        {
            target += 0.15f;
        }
        if (_twitchTimer > 0f)
        {
            target += 0.6f * (_twitchTimer / 0.6f);                // 断肢尖叫
        }
        target = Mathf.Clamp(target, 0f, 1f);

        _prevMouthSmoothed = _mouthSmoothed;
        float kOpen = 1f - Mathf.Exp(-25f * dt);
        float kClose = 1f - Mathf.Exp(-18f * dt);
        _mouthSmoothed += (target - _mouthSmoothed)
            * (target > _mouthSmoothed ? kOpen : kClose);

        // 咬合冲击：快速闭口且下穿 0.5 → 头前突 + 牙短暂提亮。
        if (dt > 1e-5f)
        {
            float rate = (_prevMouthSmoothed - _mouthSmoothed) / dt;
            if (rate > 3f && _mouthSmoothed < 0.5f && _prevMouthSmoothed >= 0.5f)
            {
                _biteLunge = 0.12f;
            }
        }
        _biteLunge = Mathf.Max(0f, _biteLunge - dt);
    }

    /// <summary>眨眼（白浊死鱼眼：Nervous 低 → 少而慢的眨更瘆人；挣扎瞪眼抑制眨）+
    /// 耳颤（Nervous 驱动的偶发 flick）。节律源是渲染侧私有 PRNG。</summary>
    private void UpdateExpression(float dt, bool struggling)
    {
        if (!_c.Conscious)
        {
            _eyesOpen = Mathf.Lerp(_eyesOpen, 0.5f, Mathf.Min(1f, 4f * dt));
        }
        else if (struggling)
        {
            _eyesOpen = Mathf.Min(1f, _eyesOpen + 8f * dt); // 瞪眼
        }
        else if (_blinkHold > 0f)
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
                _blinkTimer = _blinkRng.NextDouble() < 0.3
                    ? 0.4f + (float)_blinkRng.NextDouble() * 0.6f
                    : Mathf.Lerp(10f, 3f, _p.Nervous) * (0.5f + (float)_blinkRng.NextDouble());
            }
            _eyesOpen = Mathf.Min(1f, _eyesOpen + (0.05f + 0.95f * _p.Energy) * 8f * dt);
        }

        _earFlickTimer -= dt;
        if (_earFlickTimer <= 0f)
        {
            _earFlick = 1f;
            _earFlickTimer = Mathf.Lerp(6f, 1.5f, _p.Nervous)
                * (0.5f + (float)_blinkRng.NextDouble());
        }
        _earFlick = Mathf.Max(0f, _earFlick - 10f * dt);
    }

    /// <summary>断肢抽搐：内核 SeverSerial 跳变（按沿触发，不轮询 bool——错帧不丢事件）→
    /// 0.6s 全身 jitter + 残肢甩动 + 嘴尖叫（在 UpdateMouth 里叠加）。</summary>
    private void UpdateTwitch(float dt)
    {
        if (_c.SeverSerial != _prevSeverSerial)
        {
            _prevSeverSerial = _c.SeverSerial;
            _twitchTimer = 0.6f;
        }
        _twitchTimer = Mathf.Max(0f, _twitchTimer - dt);
    }

    private Vector3 TwitchJitter(float amp)
    {
        if (_twitchTimer <= 0f)
        {
            return Vector3.Zero;
        }
        float envelope = _twitchTimer / 0.6f;
        return new Vector3(
            Mathf.Sin(_time * 41f), Mathf.Sin(_time * 53f + 1.1f),
            Mathf.Sin(_time * 47f + 2.4f)) * (amp * envelope);
    }

    // ———————————————————————— 躯干 ————————————————————————

    /// <summary>七点脊柱长弧：颈根 → 肩坡 → **驼峰弧顶**（全身最宽）→ 中背 → 掐腰 → 髋 →
    /// 尾基。R12 背部重塑：旧版驼峰是单点极宽剖面（0.90×胸半径），颈基紧贴其上 0.2×胸半径
    /// 处半径却砍半——近半球端盖，读成「背了颗肉球」；且控制点序列在胸处折返
    /// （颈基→驼峰→胸 是回头路径），背侧剪影在驼峰后方塌出一道凹槽。现改沿躯干轴单调推进
    /// 的长弧（负 lerp 因子 = 越过胸向头侧延伸），隆起摊在肩坡→弧顶→中背三点、峰值半径降档，
    /// 背读成弓不读成球。呼吸 = 弧顶/中背半径 ±5%（停驶才明显，Lizard 门控；爬行时频率 ×2.7
    /// 幅度 ×0.6 = 急促浅喘）；挣扎 = 弧顶侧向蠕摆。椎骨凸沿弧顶脊线撒 AddKnob。</summary>
    private void DrawTorso(Vector3 chest, Vector3 hips, Vector3 right, float crawl,
        float runN, bool struggling, out Vector3 neckRoot)
    {
        float chestR = _c.Chest.Radius;
        float hipsR = _c.Hips.Radius;

        float breathRate = Mathf.Lerp(0.45f, 1.2f, crawl) * Mathf.Tau;
        float breathGate = Mathf.Pow(1f - runN, 2f) * Mathf.Lerp(1f, 0.6f, crawl);
        float breathPulse = (Mathf.Sin(_time * breathRate) + 1f) * 0.5f * breathGate;
        float breathScale = 1f + 0.05f * breathPulse;

        float humpLift = chestR * Mathf.Lerp(0.40f, 0.62f, _humpGene)
            * Mathf.Lerp(1f, 0.75f, crawl);
        neckRoot = chest.Lerp(hips, -0.18f) + _dorsalDraw * (humpLift * 0.50f);
        Vector3 shoulderSlope = chest.Lerp(hips, -0.02f) + _dorsalDraw * (humpLift * 0.88f);
        Vector3 humpPeak = chest.Lerp(hips, 0.20f) + _dorsalDraw * humpLift;
        if (struggling)
        {
            humpPeak += right * (Mathf.Sin(_time * 2.2f) * 0.015f * crawl);
        }
        Vector3 midBack = chest.Lerp(hips, 0.42f) + _dorsalDraw * (humpLift * 0.52f);
        Vector3 waistPoint = chest.Lerp(hips, 0.62f) + _dorsalDraw * (chestR * 0.12f);

        _spinePts[0] = neckRoot;
        _spinePts[1] = shoulderSlope;
        _spinePts[2] = humpPeak;
        _spinePts[3] = midBack;
        _spinePts[4] = waistPoint;
        _spinePts[5] = hips;
        _spinePts[6] = hips - _spineUp * (hipsR * 0.35f * (1f - 0.7f * crawl));
        // 半径梯度全程 ≲0.7×（旧颈基段 ≈2.4×——半球端盖的根因）；[0] 与脖管首站同半径，
        // 躯干管终点直接交棒给脖管。
        _spineRadii[0] = chestR * 0.30f;
        _spineRadii[1] = chestR * 0.62f;
        _spineRadii[2] = chestR * 0.78f * breathScale;
        _spineRadii[3] = chestR * 0.70f * breathScale;
        _spineRadii[4] = hipsR * Mathf.Lerp(0.85f, 0.45f, _gauntWaist);
        _spineRadii[5] = hipsR * 0.74f;
        _spineRadii[6] = hipsR * 0.50f;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        for (int i = 0; i < 7; i++)
        {
            _pts.Add(_spinePts[i]);
            _radii.Add(_spineRadii[i]);
            _colors.Add(_p.Body);
        }
        SplineSampler.Sample(_pts, _radii, _colors, 5, _stations);
        _tube.AddTube(_stations, _dorsalDraw, 12);

        // 椎骨凸：露脊椎的瘦削恐怖。挂点在**采样后的站点**间插值——控制点连线在驼峰
        // 前坡与样条曲面偏差最大，直接用控制点会让瘤浮出表面读成「粘珠子」；
        // 下沉到只露 ~0.45 瘤半径的尖，读成皮下顶出的骨节。
        Color vertColor = _p.Body.Darkened(0.12f);
        foreach (VertebraSpec vert in _vertebrae)
        {
            float fs = Mathf.Clamp(vert.SpineT, 0f, 1f) * (_stations.Count - 1);
            int s0 = Mathf.Clamp((int)fs, 0, _stations.Count - 2);
            float local = fs - s0;
            Vector3 basePos = _stations[s0].Pos.Lerp(_stations[s0 + 1].Pos, local);
            float surfR = Mathf.Lerp(_stations[s0].Radius, _stations[s0 + 1].Radius, local);
            // dorsal 在驼峰弯折处与管切线不垂直——投影到切线法平面才是真实表面法向，
            // 否则沿 dorsal 偏移 surfR 已越出椭圆剖面（残余浮凸的根因）。
            Vector3 tangent = _stations[s0 + 1].Pos - _stations[s0].Pos;
            Vector3 normal = _dorsalDraw
                - tangent * (_dorsalDraw.Dot(tangent) / Mathf.Max(tangent.LengthSquared(), 1e-8f));
            normal = normal.LengthSquared() > 1e-8f ? normal.Normalized() : _dorsalDraw;
            float knobR = surfR * vert.Size;
            _tube.AddKnob(basePos + normal * (surfR - knobR * 0.55f), knobR, vertColor);
        }
    }

    /// <summary>脖管：起点 = 躯干管终点（颈根，**同点同半径**——R12 前两根管在驼峰顶各自
    /// 收口，端点与粗细都不重合，大驼峰盖与细脖根之间留一道褶缝读成凹陷），再沿脖向反向
    /// 内埋一小段把接头藏进躯干管里。正弦上拱 + 前凸、中段掐细，顶点色体→头渐变
    /// （Humanoid 秘诀原样搬）。头耷拉时 occiput 低于起点 → 自然读成「先拱起再垂落」的
    /// 秃鹫式问号脖；跑步抬头时同一公式自动读成前伸直脖。</summary>
    private void DrawNeck(Vector3 chest, Vector3 headPos, Vector3 neckRoot, float runN)
    {
        float chestR = _c.Chest.Radius;
        float headR = _c.Head.Radius * _headSize;
        Vector3 occiput = headPos - _headFwd * (headR * 0.55f);
        Vector3 inward = occiput - neckRoot;
        inward = inward.LengthSquared() > 1e-8f ? inward.Normalized() : _spineUp;
        Vector3 neckStart = neckRoot - inward * (chestR * 0.12f);
        float droop = Mathf.Clamp(-_headFwd.Dot(_spineUp), 0f, 1f);
        Vector3 facing = _c.Facing;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        const int neckPoints = 6;
        for (int i = 0; i < neckPoints; i++)
        {
            float t = i / (float)(neckPoints - 1);
            Vector3 pos = neckStart.Lerp(occiput, t)
                + facing * (Mathf.Sin(t * Mathf.Pi) * chestR * 0.28f)
                + _spineUp * (Mathf.Sin(t * Mathf.Pi) * chestR * 0.35f * (1f - 0.6f * droop));
            float r = Mathf.Lerp(chestR * 0.30f, headR * 0.38f, t)
                - Mathf.Sin(t * Mathf.Pi) * headR * 0.10f;
            _pts.Add(pos);
            _radii.Add(Mathf.Max(headR * 0.12f, r));
            _colors.Add(_p.Body.Lerp(_p.Head, Mathf.Pow(t, 1.2f)));
        }
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, _dorsalDraw, 8);
    }

    // ———————————————————————— 头（颅吻 + 可动颌）————————————————————————

    /// <summary>头帧每帧只算一次（抗抖第一件）：gaze 主要由头相对胸的物理偏移决定，
    /// 竖直分量按 runN 调制（闲置重度耷拉 → 跑步平视微抬——「头朝向必须压竖直分量」的
    /// Humanoid 实测教训吸收进公式）。</summary>
    private void DrawHead(Vector3 chest, Vector3 headPos, Vector3 facing,
        float runN, float crawl, float dt)
    {
        float headR = _c.Head.Radius * _headSize;
        Vector3 offset = headPos - chest;
        Vector3 headOffsetDir = offset.LengthSquared() > 1e-8f ? offset.Normalized() : facing;
        float downBias = Mathf.Lerp(0.50f, -0.05f, runN) * (1f - 0.6f * crawl);
        Vector3 gaze = facing * 0.55f + headOffsetDir * 0.35f - _spineUp * downBias;
        gaze = gaze.LengthSquared() > 1e-8f ? gaze.Normalized() : facing;
        float k = 1f - Mathf.Exp(-10f * dt);
        _headFwd = _headFwd.Lerp(gaze, k);
        Vector3 headFwd = _headFwd.LengthSquared() > 1e-8f ? _headFwd.Normalized() : facing;
        Vector3 headUp = _spineUp - headFwd * _spineUp.Dot(headFwd);
        headUp = headUp.LengthSquared() > 1e-6f ? headUp.Normalized() : Vector3.Up;
        Vector3 headRight = headFwd.Cross(headUp).Normalized();

        Vector3 drawPos = headPos + TwitchJitter(0.02f);
        if (_biteLunge > 0f)
        {
            drawPos += headFwd * (0.02f * (_biteLunge / 0.12f)); // 咬合前突
        }

        // 颅吻一体扫管（楔形颅 → 细长吻，鼻尖渐暗；无共享球 → 无拼缝亮度断层）。
        _snoutPts[0] = drawPos - headFwd * (headR * 0.55f);
        _snoutPts[1] = drawPos + headFwd * (headR * 0.10f) + headUp * (headR * 0.08f);
        _snoutPts[2] = drawPos + headFwd * (headR * 0.70f);
        _snoutPts[3] = drawPos + headFwd * (headR * (0.7f + _snoutLen * 0.45f))
            - headUp * (headR * _snoutDroop * 0.5f);
        _snoutPts[4] = drawPos + headFwd * (headR * (0.7f + _snoutLen))
            - headUp * (headR * _snoutDroop);
        _snoutRadii[0] = headR * 0.62f;
        _snoutRadii[1] = headR * 0.58f;
        _snoutRadii[2] = headR * 0.40f;
        _snoutRadii[3] = headR * 0.25f;
        _snoutRadii[4] = headR * 0.13f;

        Color snoutDark = _p.Head.Lerp(new Color(0.30f, 0.27f, 0.25f), 0.75f);
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        for (int i = 0; i < 5; i++)
        {
            float t = i / 4f;
            _pts.Add(_snoutPts[i]);
            _radii.Add(_snoutRadii[i]);
            _colors.Add(_p.Head.Lerp(snoutDark, Mathf.Pow(t, 2.2f)));
        }
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, headUp, 12);

        // 可动下颌：绕耳下铰点旋转（张角单独低通在 _mouthSmoothed 里；jawRest ≥3° 永不咬死）。
        Vector3 jawPivot = drawPos - headFwd * (headR * 0.15f) - headUp * (headR * 0.32f);
        float theta = Mathf.DegToRad(
            _jawRestDeg + (_jawGapeDeg - _jawRestDeg) * _mouthSmoothed);
        Vector3 jawDir = headFwd.Rotated(headRight, -theta);
        Vector3 jawUp = headRight.Cross(jawDir).Normalized();
        float jawLen = headR * (0.6f + _snoutLen * 0.55f);

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(jawPivot);
        _radii.Add(headR * 0.30f);
        _colors.Add(_p.Head.Darkened(0.06f));
        _pts.Add(jawPivot + jawDir * (headR * 0.95f));
        _radii.Add(headR * 0.19f);
        _colors.Add(_p.Head.Darkened(0.10f));
        _pts.Add(jawPivot + jawDir * jawLen);
        _radii.Add(headR * 0.09f);
        _colors.Add(_p.Head.Lerp(snoutDark, 0.6f));
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, jawUp, 8);

        // 口腔暗喉：沿两颌角平分线，半径 × clamp(mouth·1.6)——闭嘴缩进颌内不可见。
        Vector3 snoutAxisDir = (_snoutPts[4] - _snoutPts[2]).Normalized();
        Vector3 bisector = (snoutAxisDir + jawDir).Normalized();
        float mawScale = Mathf.Clamp(_mouthSmoothed * 1.6f, 0.15f, 1f);
        _stations.Clear();
        _stations.Add(new TubeStation(jawPivot + bisector * (headR * 0.05f),
            headR * 0.26f * mawScale, _p.Maw));
        _stations.Add(new TubeStation(jawPivot + bisector * (headR * 1.05f),
            headR * 0.05f * mawScale, _p.Maw.Darkened(0.3f)));
        _tube.AddTube(_stations, jawUp, 8);

        DrawTeeth(headFwd, headUp, headRight, jawPivot, jawDir, jawUp, jawLen, headR);
        DrawEyes(drawPos, headFwd, headUp, headRight, headR);
        DrawEars(drawPos, headFwd, headUp, headRight, headR, runN);
    }

    /// <summary>牙 = (头帧, 张角) 的纯函数（抗抖第三件：零累积状态）。上牙挂上吻管下缘
    /// 唇线朝下微外撇，下牙从下颌管上缘朝上，上下错半齿位防闭合 z-fight；根色 = 暗牙龈
    /// （苍白头上分离牙），尖色 = 脏白；咬合冲击瞬间牙提亮。</summary>
    private void DrawTeeth(Vector3 headFwd, Vector3 headUp, Vector3 headRight,
        Vector3 jawPivot, Vector3 jawDir, Vector3 jawUp, float jawLen, float headR)
    {
        Color gum = _p.Head.Lerp(_p.Maw, 0.35f);
        Color toothCol = _biteLunge > 0f
            ? _p.Teeth.Lightened(0.25f * (_biteLunge / 0.12f))
            : _p.Teeth;

        // 上牙：沿吻段（station 2→4）分段线性取唇线位置与半径。
        foreach (Tooth tooth in _upperTeeth)
        {
            float ft = tooth.T * 2f;
            int seg = Mathf.Clamp((int)ft, 0, 1) + 2;
            float local = ft - (seg - 2);
            Vector3 p = _snoutPts[seg].Lerp(_snoutPts[seg + 1], local);
            float r = Mathf.Lerp(_snoutRadii[seg], _snoutRadii[seg + 1], local);
            foreach (float side in Sides)
            {
                Vector3 root = p - headUp * (r * 0.80f) + headRight * (side * r * 0.55f);
                Vector3 dir = (-headUp * 0.85f + headFwd * 0.10f
                    + headRight * (side * 0.25f)).Normalized();
                float len = tooth.Length * headR;
                _tube.AddBlade(root + headUp * (r * 0.15f), root + dir * len, headRight,
                    tooth.Width * headR, 0.38f, gum, toothCol);
            }
        }

        // 下牙：错半齿位（T + 0.06），从颌管上缘朝上。
        foreach (Tooth tooth in _lowerTeeth)
        {
            float t = Mathf.Min(0.95f, tooth.T + 0.06f);
            Vector3 p = jawPivot + jawDir * (jawLen * Mathf.Lerp(0.35f, 0.98f, t));
            float r = Mathf.Lerp(headR * 0.19f, headR * 0.09f, t);
            foreach (float side in Sides)
            {
                Vector3 root = p + jawUp * (r * 0.75f) + headRight * (side * r * 0.5f);
                Vector3 dir = (jawUp * 0.9f + jawDir * 0.15f
                    + headRight * (side * 0.2f)).Normalized();
                float len = tooth.Length * headR;
                _tube.AddBlade(root - jawUp * (r * 0.15f), root + dir * len, headRight,
                    tooth.Width * headR, 0.38f, gum, toothCol);
            }
        }
    }

    private static readonly float[] Sides = { -1f, 1f };

    /// <summary>眼：白浊小椭球（无瞳孔——参考图的浑浊盲眼），先 AddKnob 暗眼窝垫底
    /// （苍白头上白眼必须有暗环才读得出）；眨眼抹竖向 scale，昏迷半睁死鱼眼。</summary>
    private void DrawEyes(Vector3 drawPos, Vector3 headFwd, Vector3 headUp,
        Vector3 headRight, float headR)
    {
        float eyeR = headR * _eyeSize / 0.10f * 0.10f;
        eyeR = headR * _eyeSize;
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 center = drawPos + headFwd * (headR * 0.45f)
                + headUp * (headR * 0.14f) + headRight * (side * headR * 0.44f);
            _tube.AddKnob(center - headFwd * (eyeR * 0.15f), eyeR * 1.6f, _p.Socket);
            if (_eyes[i] is { } eye)
            {
                float eyeH = Mathf.Max(eyeR * Mathf.Clamp(_eyesOpen, 0f, 1f), eyeR * 0.12f);
                eye.Transform = new Transform3D(
                    new Basis(headRight * eyeR, headUp * eyeH, headFwd * (eyeR * 0.7f)),
                    center + headFwd * (eyeR * 0.35f));
            }
        }
    }

    /// <summary>大立耳：外层大刀片 + 内层暗色小刀片凹面（前移一皮防 z-fight），
    /// 左右 seed 不对称；跑步/受创耳后折，Nervous 偶发 flick。</summary>
    private void DrawEars(Vector3 drawPos, Vector3 headFwd, Vector3 headUp,
        Vector3 headRight, float headR, float runN)
    {
        float fold = 0.15f + 0.35f * runN + 0.5f * (_twitchTimer > 0f ? 1f : 0f);
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            float lenScale = _earLen * (1f + side * _earAsym);
            Vector3 root = drawPos + headUp * (headR * 0.55f)
                - headFwd * (headR * 0.20f) + headRight * (side * headR * 0.42f);
            float flick = _earFlick * Mathf.Sin(_time * 30f) * 0.12f * (i == 0 ? 1f : 0.4f);
            Vector3 dir = (headUp + headRight * (side * (_earSplay + flick))
                - headFwd * fold).Normalized();
            Vector3 hw = headFwd.Cross(dir);
            hw = hw.LengthSquared() > 1e-8f ? hw.Normalized() : headRight;
            Vector3 tip = root + dir * (headR * lenScale);
            _tube.AddBlade(root, tip, hw, headR * 0.36f, 0.35f,
                _p.Head, _p.Head.Darkened(0.18f));
            // 内耳凹面：缩小 + 沿耳面法线前移一皮。
            Vector3 faceN = dir.Cross(hw).Normalized();
            Vector3 innerRoot = root + dir * (headR * 0.08f) + faceN * 0.008f;
            _tube.AddBlade(innerRoot, innerRoot + dir * (headR * lenScale * 0.66f), hw,
                headR * 0.20f, 0.35f, _p.EarInner, _p.EarInner.Darkened(0.2f));
        }
    }

    // ———————————————————————— 四肢 ————————————————————————

    /// <summary>臂：肩挂驼峰前坡（耸肩）→ TwoBoneIk 肘 → 腕 → 掌。pole 三态混合
    /// （垂手肘朝后外 / 前伸肘外压 / 爬行肘朝上=蜘蛛式撑地），逐臂低通防跳变。
    /// R14 一体化（Lizard 路线）：根点埋到躯干**内部**、根半径放大成三角肌锥
    /// （旧版细棍从肥躯干表面穿出，交线读成「管子怼上去」——苍白皮不像近黑 Humanoid
    /// 能靠剪影藏缝）；两骨中段沿 pole 微弓打散直圆柱感；掌隆起并进臂管本体
    /// （旧版手 = 大瘤球粘细腕上）。肩/肘**位置**与 RatFiendJointMath 逐字同源不动，
    /// 改的全是管控制点与半径剖面。断臂 → 同款埋根 → 残肢粒子的短残段
    /// （内核真粒子，插值无缝）+ 暗红断口瘤 + 碎肉刀片。</summary>
    private void DrawArm(RatArm arm, int index, Vector3 chest, Vector3 facing,
        Vector3 right, float runN, float crawl, float alpha)
    {
        float chestR = _c.Chest.Radius;
        Vector3 shoulder = chest
            + right * (arm.Side * chestR * 0.78f)
            + _spineUp * (chestR * 0.35f)
            + _dorsalDraw * (chestR * 0.18f);
        float thick = 0.030f * _armThin;
        Color handCol = _p.Body.Lerp(_p.Head.Darkened(0.35f), 0.55f);
        Vector3 armRootDeep = shoulder.Lerp(chest, 0.62f);
        float armRootR = chestR * 0.38f;

        if (arm.Severed)
        {
            DrawStump(armRootDeep, armRootR, shoulder, arm.LerpPos(alpha), thick * 1.5f);
            return;
        }

        Vector3 hand = arm.LerpPos(alpha) + TwitchJitter(0.015f);
        Vector3 poleIdle = (right * (arm.Side * 0.55f) + _spineUp * 0.35f - facing * 0.60f)
            .Normalized();
        Vector3 poleRun = (right * (arm.Side * 0.90f) - _spineUp * 0.25f + facing * 0.10f)
            .Normalized();
        Vector3 poleCrawl = (_spineUp * 0.85f + right * (arm.Side * 0.45f)).Normalized();
        Vector3 poleTarget = poleIdle.Lerp(poleRun, runN).Lerp(poleCrawl, crawl);
        if (!_armPoleInit[index])
        {
            _armPole[index] = poleTarget;
            _armPoleInit[index] = true;
        }
        _armPole[index] = _armPole[index].Lerp(poleTarget, 0.25f);
        Vector3 pole = _armPole[index].LengthSquared() > 1e-8f
            ? _armPole[index].Normalized() : poleIdle;

        float bone = arm.ArmLength * 0.52f;
        Vector3 elbow = TwoBoneIk.Solve(shoulder, hand, bone, bone, pole);
        Vector3 forearmDir = hand - elbow;
        forearmDir = forearmDir.LengthSquared() > 1e-8f ? forearmDir.Normalized() : facing;
        Vector3 wrist = hand - forearmDir * 0.05f;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        // 埋根三角肌锥：起点在躯干内部，肥根快收锥（半径剖面 smoothstep 无过冲），
        // 出膛角变浅 → 剪影里臂从肩坡自然长出来而不是穿出来。
        _pts.Add(armRootDeep);
        _radii.Add(armRootR);
        _colors.Add(_p.Body);
        _pts.Add(shoulder);
        _radii.Add(thick * 2.2f);
        _colors.Add(_p.Body);
        _pts.Add(shoulder.Lerp(elbow, 0.55f) + pole * (bone * 0.10f));
        _radii.Add(thick * 1.25f);
        _colors.Add(_p.Body.Lerp(handCol, 0.18f));
        _pts.Add(elbow);
        _radii.Add(thick * 0.95f);
        _colors.Add(_p.Body.Lerp(handCol, 0.35f));
        _pts.Add(elbow.Lerp(hand, 0.5f) + pole * (bone * 0.05f));
        _radii.Add(thick * 1.0f);
        _colors.Add(_p.Body.Lerp(handCol, 0.55f));
        _pts.Add(wrist);
        _radii.Add(thick * 0.62f);
        _colors.Add(_p.Body.Lerp(handCol, 0.8f));
        // 掌并进臂管：腕细 → 掌背隆起 → 掌缘收锥，一根管扫到底（旧版大瘤球粘细腕）。
        _pts.Add(hand);
        _radii.Add(thick * 1.10f);
        _colors.Add(handCol);
        _pts.Add(hand + forearmDir * (arm.Radius * 0.7f));
        _radii.Add(thick * 0.45f);
        _colors.Add(handCol.Darkened(0.08f));
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, pole, 10);

        DrawClaws(arm, hand, elbow, facing, right, handCol);
    }

    /// <summary>钩爪：每手 3 指（40% 个体 + 拇指爪），每指两枚刀片折出钩形
    /// （近节沿指向、远节向掌心折 _fingerCurl）；撑地时指向抓面摊开扒地。爪尖染暗
    /// （苍白身上的暗爪尖，参考图读法）。R14 起手瘤删除——掌隆起在臂管剖面里，
    /// 刀片根埋在掌管内，无球-管拼缝。</summary>
    private void DrawClaws(RatArm arm, Vector3 hand, Vector3 elbow, Vector3 facing,
        Vector3 right, Color handCol)
    {
        Vector3 forearmDir = (hand - elbow).Normalized();
        bool planted = arm.GrabPos is not null && arm.TerrainContact;
        Vector3 palmN = planted ? Vector3.Up : -facing * 0.3f + forearmDir;
        palmN = palmN.LengthSquared() > 1e-8f ? palmN.Normalized() : Vector3.Up;
        Vector3 fingerAxis = right;
        Color clawTip = _p.Head.Lerp(new Color(0.16f, 0.13f, 0.12f), 0.8f);

        int fingers = _hasThumbClaw ? 4 : 3;
        for (int f = 0; f < fingers; f++)
        {
            float spread = fingers <= 1 ? 0f : -1f + 2f * f / (fingers - 1);
            bool thumb = _hasThumbClaw && f == fingers - 1;
            Vector3 dir1 = (forearmDir + fingerAxis * (spread * (planted ? 0.55f : 0.35f))
                - palmN * (planted ? 0.5f : 0.2f)).Normalized();
            if (thumb)
            {
                dir1 = (fingerAxis * (arm.Side * 0.8f) + forearmDir * 0.4f).Normalized();
            }
            float len1 = _fingerLen * (thumb ? 0.6f : 1f);
            Vector3 mid = hand + dir1 * (len1 * 0.6f);
            Vector3 dir2 = dir1.Rotated(fingerAxis.LengthSquared() > 1e-8f
                    ? fingerAxis.Normalized() : Vector3.Up,
                Mathf.DegToRad(-_fingerCurlDeg));
            Vector3 tip = mid + dir2 * (len1 * 0.5f);
            _tube.AddBlade(hand, mid, fingerAxis, len1 * 0.16f, 0.5f, handCol, clawTip);
            _tube.AddBlade(mid, tip, fingerAxis, len1 * 0.10f, 0.4f, clawTip, clawTip);
        }
    }

    /// <summary>腿：髋侧关节 → TwoBoneIk 膝 → 踝 → 脚掌 → 趾尖，**一根管扫到底** + 趾刀片。
    /// R14 一体化（Lizard 路线）：根点埋进骨盆内部成臀肌锥（旧版细棍从髋球表面出发）；
    /// _kneeBulge 基因改驱大腿肉（旧版单点鼓在膝上 = 机械球关节），膝收成骨感窄点、
    /// 小腿肚微凸；脚掌并进腿管（旧版独立脚板管在踝处半径跳 3× = 楔子粘牙签）。
    /// 髋/膝**位置**与 RatFiendJointMath 逐字同源不动。
    /// 断腿 → 同款埋根 → 残肢粒子（膝端）的短残段 + 断口。</summary>
    private void DrawLeg(Limb leg, int index, Vector3 hips, Vector3 facing,
        Vector3 right, float crawl, float alpha)
    {
        float hipsR = _c.Hips.Radius;
        Vector3 hipJoint = hips + right * (leg.Side * hipsR * 0.55f);
        float thick = Mathf.Lerp(0.016f, 0.022f, 1f - _p.Gauntness) * _legGene;
        Vector3 legRootDeep = hipJoint.Lerp(hips, 0.75f);
        float legRootR = hipsR * 0.52f;

        bool severed = _c.IsSevered(index == 0
            ? RatFiendLimbId.LegLeft : RatFiendLimbId.LegRight);
        if (severed)
        {
            DrawStump(legRootDeep, legRootR, hipJoint, leg.LerpPos(alpha), thick * 1.7f);
            return;
        }

        Vector3 foot = leg.LerpPos(alpha);
        // 骨长不吃 _legGene（基因只调粗细）：站立膝角由「站高/双骨长」唯一决定，基因掺进
        // 骨长会把同一预设的膝角在 141°~97° 之间乱抽。系数调 JointMath 单一真相源
        // （R10 站姿修复轮 0.60→0.44，命中胶囊与视觉膝位逐字同源）。
        float bone = RatFiendJointMath.LegBone(leg.JointDist);
        Vector3 pole = (facing + right * (leg.Side * 0.25f) + _spineUp * (crawl * 0.5f))
            .Normalized();
        Vector3 knee = TwoBoneIk.Solve(hipJoint, foot, bone, bone, pole);
        Vector3 ankle = foot + (knee - foot).Normalized() * (leg.Radius * 0.8f);

        // 趾向混入本腿姿态——摆动腿拖在身后时脚不能还朝行进向硬指（悬空外飘教训照抄）。
        Vector3 legDir = foot - knee;
        legDir = legDir.LengthSquared() > 1e-8f ? legDir.Normalized() : -_spineUp;
        Vector3 toeDir = facing * 0.55f + legDir * 0.45f;
        toeDir -= _spineUp * toeDir.Dot(_spineUp) * 0.7f;
        toeDir = toeDir.LengthSquared() > 1e-8f ? toeDir.Normalized() : facing;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(legRootDeep);
        _radii.Add(legRootR);
        _colors.Add(_p.Body);
        _pts.Add(hipJoint.Lerp(knee, 0.16f));
        _radii.Add(thick * _kneeBulge);
        _colors.Add(_p.Body);
        _pts.Add(hipJoint.Lerp(knee, 0.62f) + pole * (bone * 0.07f));
        _radii.Add(thick * 1.5f);
        _colors.Add(_p.Body);
        _pts.Add(knee);
        _radii.Add(thick * 1.05f);
        _colors.Add(_p.Body);
        _pts.Add(knee.Lerp(ankle, 0.4f) - pole * (bone * 0.05f));
        _radii.Add(thick * 1.2f);
        _colors.Add(_p.Body.Darkened(0.05f));
        _pts.Add(ankle);
        _radii.Add(thick * 0.78f);
        _colors.Add(_p.Body.Darkened(0.1f));
        _pts.Add(foot);
        _radii.Add(leg.Radius * 0.5f);
        _colors.Add(_p.Body.Darkened(0.12f));
        _pts.Add(foot + toeDir * (leg.Radius * 1.5f));
        _radii.Add(leg.Radius * 0.22f);
        _colors.Add(_p.Body.Darkened(0.2f));
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, pole, 10);
        Color clawTip = _p.Head.Lerp(new Color(0.16f, 0.13f, 0.12f), 0.8f);
        foreach (float side in Sides)
        {
            Vector3 toeRoot = foot + toeDir * (leg.Radius * 1.2f)
                + right * (side * leg.Radius * 0.5f);
            _tube.AddBlade(toeRoot, toeRoot + toeDir * (leg.Radius * 1.1f), right,
                leg.Radius * 0.25f, 0.4f, _p.Body.Darkened(0.2f), clawTip);
        }
    }

    /// <summary>残肢：埋根锥（与完好肢同款——断肢不能反而露出根部拼缝）→ 锚关节 →
    /// 内核残肢粒子的短管（末段不收锥——AddTube 锥帽读作钝圆残端），
    /// 端头暗红断口瘤沉出创面 + 碎肉小刀片（seed 角度冻结）。断肢瞬间抽搐由
    /// TwitchJitter 叠在残端上。</summary>
    private void DrawStump(Vector3 rootDeep, float rootDeepR, Vector3 anchor,
        Vector3 stumpEnd, float rootThick)
    {
        Vector3 end = stumpEnd + TwitchJitter(0.03f);
        Vector3 dir = end - anchor;
        float len = dir.Length();
        if (len < 1e-5f)
        {
            return;
        }
        dir /= len;

        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        _pts.Add(rootDeep);
        _radii.Add(rootDeepR);
        _colors.Add(_p.Body);
        _pts.Add(anchor);
        _radii.Add(rootThick * 1.3f);
        _colors.Add(_p.Body);
        _pts.Add(anchor.Lerp(end, 0.6f));
        _radii.Add(rootThick * 0.8f);
        _colors.Add(_p.Body.Darkened(0.08f));
        _pts.Add(end);
        _radii.Add(rootThick * 0.7f);
        _colors.Add(_p.Body.Lerp(_p.Wound, 0.45f));
        SplineSampler.Sample(_pts, _radii, _colors, 2, _stations);
        _tube.AddTube(_stations, _spineUp, 8);

        // 断口创面 + 碎肉。
        _tube.AddKnob(end + dir * 0.004f, rootThick * 0.62f, _p.Wound);
        Vector3 across = dir.Cross(Vector3.Up);
        across = across.LengthSquared() > 1e-8f
            ? across.Normalized() : dir.Cross(Vector3.Right).Normalized();
        Vector3 up2 = dir.Cross(across).Normalized();
        foreach (WoundBladeSpec blade in _woundBlades)
        {
            Vector3 radial = across * Mathf.Cos(blade.Angle) + up2 * Mathf.Sin(blade.Angle);
            Vector3 root = end + radial * (rootThick * 0.4f);
            Vector3 tip = root + (radial * 0.5f + dir * 0.85f).Normalized() * blade.Length;
            _tube.AddBlade(root, tip, across, blade.Length * 0.3f, 0.4f,
                _p.Wound, _p.Wound.Lerp(_p.Body, 0.4f));
        }
    }

    // ———————————————————————— 尾 ————————————————————————

    /// <summary>小尾巴：渲染侧 verlet 短锥链（Humanoid 尾参数下调），环纹裸鼠尾 =
    /// 控制点颜色交替明暗，零额外几何。尾根方向不能直接用 dorsal——爬行时 dorsal = 世界上，
    /// 尾巴会直挺挺戳向天（首轮截图实测）；按 crawl 混向水平的「身后」方向并加重下垂。</summary>
    private void DrawTail(Vector3 hips, Vector3 facing, float crawl, float h)
    {
        int n = _tailPos.Length;
        float hipsR = _c.Hips.Radius;
        Vector3 backFlat = -facing;
        backFlat -= Vector3.Up * backFlat.Dot(Vector3.Up);
        backFlat = backFlat.LengthSquared() > 1e-6f ? backFlat.Normalized() : -facing;
        Vector3 tailDir = _dorsalDraw.Slerp(backFlat, Mathf.Clamp(crawl, 0f, 1f) * 0.85f);
        tailDir = tailDir.LengthSquared() > 1e-6f ? tailDir.Normalized() : backFlat;
        Vector3 pin = hips + tailDir * (hipsR * 0.55f) - _spineUp * (hipsR * 0.45f);
        const float segLen = 0.06f;
        if (!_tailInitialized)
        {
            for (int i = 0; i < n; i++)
            {
                _tailPos[i] = pin + tailDir * (segLen * i);
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
            vel += Vector3.Down * (0.018f * dt40);
            vel += tailDir * (0.004f * dt40);
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
            _radii.Add(Mathf.Lerp(hipsR * 0.22f, 0.005f, t));
            // 环纹：交替明暗（裸鼠尾）。
            _colors.Add(i % 2 == 0 ? _p.Body.Darkened(0.06f) : _p.Body.Lightened(0.06f));
        }
        SplineSampler.Sample(_pts, _radii, _colors, 2, _stations);
        _tube.AddTube(_stations, _spineUp, 6);
    }
}
