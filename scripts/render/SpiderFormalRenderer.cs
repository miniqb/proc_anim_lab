using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Spider;

namespace ProcAnimLab.Render;

/// <summary>蜘蛛渲染调色板 + 品种档案（≙ BigSpiderGraphics 三品种共用一类：黄毛普通种 /
/// 红毛 Spitter / 小蜘蛛全黑）。RW 大蜘蛛是纯剪影生物：身体/头/腿全黑，唯一彩色 =
/// 腹毛链「黑根金尖」渐亮（ApplyPalette L701-708）与螯根微光。</summary>
internal readonly struct SpiderRenderPalette
{
    public readonly Color Body;    // 剪影黑（微暖，≙ palette.blackColor）
    public readonly Color Accent;  // 毛尖/螯根色（≙ yellowCol：普通黄 / Spitter 红）
    public readonly int HairCount; // 腹毛链数（≙ 普通 10~27 / Spitter 26~37 / 小蜘蛛 0）
    public readonly float HairLength;   // 单链总长上限（米）
    public readonly float BodyFat;      // 身体管峰值半径系数（≙ bodyThickness 档差）
    public readonly float LegThickness; // 腿管基准半径系数

    public SpiderRenderPalette(Color body, Color accent, int hairCount, float hairLength,
        float bodyFat, float legThickness)
    {
        Body = body;
        Accent = accent;
        HairCount = hairCount;
        HairLength = hairLength;
        BodyFat = bodyFat;
        LegThickness = legThickness;
    }

    private static readonly Color SilhouetteBlack = new(0.058f, 0.049f, 0.038f);

    public static SpiderRenderPalette ForBreed(string breedName) => breedName switch
    {
        // spider-large ≙ Spitter 系：红毛更多更长，体管略胖。毛量按 3D 球面密度换算
        //（RW 2D 侧视 10~37 链 ≈ 半圆弧密度，球面同密度需 ~2-3×——稀疏粗毛的单根
        // 远侧遮挡弧读作悬空逗号，密细毛读作绒毯）。BodyFat 是修长椭腹的**高度**系数
        //（轴向长度由 DrawBody 椭圆剖面 + StepTail 尾展决定）。
        "spider-large" => new SpiderRenderPalette(SilhouetteBlack,
            new Color(0.92f, 0.24f, 0.10f), 64, 0.24f, 0.95f, 1.12f),
        // spider-lean ≙ 群居小蜘蛛（SpiderGraphics）气质：近乎无毛、全黑、极细腿。
        "spider-lean" => new SpiderRenderPalette(SilhouetteBlack,
            new Color(0.62f, 0.55f, 0.38f), 14, 0.13f, 0.85f, 0.82f),
        // spider-small = BigSpider 黄毛基准。
        _ => new SpiderRenderPalette(SilhouetteBlack,
            new Color(1.00f, 0.78f, 0.28f), 48, 0.18f, 0.95f, 1.00f),
    };
}

/// <summary>
/// 蜘蛛正式渲染器（技术验证件五号，≙ BigSpiderGraphics 的 3D 移植；真相源 =
/// scratchpad spider_scav_render/bigspider_graphics.md 逐行取证）。
/// 身体 = 头前伸点→腹（双控制点）→渲染侧 tailEnd verlet 粒子的三点 Bezier 变径扫管
/// （≙ MakeLongMesh(7) + 半径 Lerp(2.5, 10+呼吸, Sin(Pow(f,0.75)π))·thickness——最宽截面
/// 偏向腹部的单峰剖面），头叶瓣以第二峰并进同一条剖面（RW 用独立头椭圆 sprite 的 3D 等价）；
/// 腿 = 内核两段 IK 正式姿态（Root/Knee/Foot 直接消费）画成股节/膝结/胫节 + 爪尖四件
/// （≙ 屏幕三段贴图 1.5×/1.2×/1.2× 粗细梯度 + 段间重叠；RW 的第三段本是"中点插定长节"
/// 戏法，我们的膝结小瘤承担同一职责），根部沉进体管融根；
/// 腹毛 = 渲染侧 verlet 短链（≙ scales 系统：根锚弹簧 + 外梳方向 + n−2 拉直 + 黑根亮尖
/// 逐段渐变），呼吸周期性鼓张（≙ ScaleDir 的 Sin(breath) 项）；
/// 螯肢 = 头前一对短管，闲置蠕动（≙ mandibles RNV 抖 → sin 相位化）。
/// 有意偏离：不移植 deadLeg（RW 腿是纯图形件可以装瘫；本项目腿真实承力，静止的"瘫腿"
/// 会和可见的真实迈步矛盾）；不移植 flip 侧倾/膝压平（2D 滚转伪装，3D 由真实姿态取代）；
/// 图形层 UnityEngine.Random 全部换 seed 冻结普查 + sin 相位（确定性守则）。
/// 对内核只读；顶点色走 sRGB 语义（srgbVertexColors:true——新渲染器按 Daddy 轮教训
/// 直接在所见空间调色）。
/// </summary>
internal sealed class SpiderFormalRenderer : IFormalRenderer
{
    private readonly SpiderLocomotionController _c;
    private readonly SpiderRenderPalette _pal;
    private readonly int _seed;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;

    // —— 渲染侧化妆状态（私有，不写回内核、不进哈希）——
    private Vector3 _bodyUp = Vector3.Up;      // SupportNormal 低通
    private Vector3 _tailPos;                  // tailEnd verlet 粒子（≙ GenericBodyPart）
    private Vector3 _tailLastPos;
    private bool _tailInitialized;
    private float _time;

    private readonly struct HairSpec
    {
        public readonly Vector3 LocalDir;   // 腹部局部系（forward/up/right）锚向
        public readonly int Nodes;
        public readonly float SegLength;
        public readonly float Brightness;   // 毛尖亮度系数（≙ scaleSpecs 0.3~0.9）

        public HairSpec(Vector3 localDir, int nodes, float segLength, float brightness)
        {
            LocalDir = localDir;
            Nodes = nodes;
            SegLength = segLength;
            Brightness = brightness;
        }
    }

    private sealed class Hair
    {
        public HairSpec Spec;
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Vector3[] LastPos = Array.Empty<Vector3>();
        public bool Initialized;
    }

    private readonly List<Hair> _hairs = new();
    private float[] _legThickness = Array.Empty<float>();  // 逐腿个体粗细（≙ 0.7~1.1）
    private float[] _mandiblePhase = Array.Empty<float>();

    private readonly List<TubeStation> _stations = new();
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    // 本帧身体剖面站链（站心+半径，DrawBody 填充）——腹毛锚定/排斥的表面真相源。
    private readonly List<(Vector3 Center, float Radius)> _bodyProfile = new();

    public SpiderFormalRenderer(SpiderLocomotionController controller, string breedName)
    {
        _c = controller;
        _pal = SpiderRenderPalette.ForBreed(breedName);
        // 品种名 FNV-1a 稳定种子（string.GetHashCode 逐进程随机化，不可用作冻结普查）。
        uint h = 2166136261u;
        foreach (char ch in breedName)
        {
            h = (h ^ ch) * 16777619u;
        }
        _seed = unchecked((int)h);
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root, srgbVertexColors: true);
        SeedDecorations();
    }

    /// <summary>装饰普查（seed 冻结，≙ BigSpiderGraphics ctor 的个体参数段）：逐腿粗细、
    /// 腹毛链拓扑、螯肢相位。</summary>
    private void SeedDecorations()
    {
        var rng = new Random(_seed);
        _legThickness = new float[_c.Legs.Count];
        for (int i = 0; i < _legThickness.Length; i++)
        {
            _legThickness[i] = Mathf.Lerp(0.78f, 1.1f, (float)rng.NextDouble());
        }

        _mandiblePhase = new float[2];
        _mandiblePhase[0] = (float)rng.NextDouble() * Mathf.Tau;
        _mandiblePhase[1] = (float)rng.NextDouble() * Mathf.Tau;

        _hairs.Clear();
        for (int i = 0; i < _pal.HairCount; i++)
        {
            // 锚向：整个腹背面 + 两侧铺开、后段略密（≙ scaleStuckPositions 全腹散布 +
            // 长毛偏腹后）；不留「一撮」——毛的职责是把整圈轮廓搅毛。
            // back 下限 −0.86：避开正后极——极点毛披过穹顶边缘后垂在近隐形的细尾管旁，
            // 读作散落悬空（后极的「尾梢毛边」由 −0.86~−0.6 的斜后毛自然覆盖）。
            float back = Mathf.Lerp(-0.86f, 0.55f, (float)rng.NextDouble());
            float up = Mathf.Lerp(-0.20f, 1.0f, (float)rng.NextDouble());
            float side = Mathf.Lerp(-1f, 1f, (float)rng.NextDouble());
            Vector3 local = new Vector3(back, up, side).Normalized();
            // 每第 3 条是长链（≙ 普通种长短混编 2~12 段）；短毛贴身、长毛出轮廓。
            bool longHair = i % 3 == 0;
            int nodes = longHair ? rng.Next(4, 7) : rng.Next(2, 4);
            float lenScale = longHair ? 1f : 0.55f;
            float segLen = _pal.HairLength * lenScale / Math.Max(2, nodes - 1)
                * Mathf.Lerp(0.7f, 1.05f, (float)rng.NextDouble());
            _hairs.Add(new Hair
            {
                Spec = new HairSpec(local, nodes, segLen,
                    Mathf.Lerp(0.3f, 0.9f, (float)rng.NextDouble())),
            });
        }
        foreach (Hair hair in _hairs)
        {
            hair.Pos = new Vector3[hair.Spec.Nodes];
            hair.LastPos = new Vector3[hair.Spec.Nodes];
        }
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _hairs.Clear();
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

        Vector3 head = _c.Primary.LerpPos(alpha);
        Vector3 abdomen = _c.Rear.LerpPos(alpha);
        Vector3 fwd = head - abdomen;
        fwd = fwd.LengthSquared() > 1e-8f ? fwd.Normalized() : Vector3.Right;

        // 稳定 up：SupportNormal 低通再去前向分量（≙ CLAUDE.md 3D 朝向边界）。
        float k = 1f - Mathf.Exp(-6f * dt);
        _bodyUp = _bodyUp.Lerp(_c.SupportNormal, k);
        Vector3 up = _bodyUp - fwd * _bodyUp.Dot(fwd);
        up = up.LengthSquared() > 1e-6f ? up.Normalized() : fwd.Cross(Vector3.Right).Normalized();
        Vector3 right = fwd.Cross(up).Normalized();

        StepTail(head, abdomen, fwd, h);

        _tube.BeginFrame();
        DrawBody(head, abdomen, fwd, up);
        for (int i = 0; i < _c.Legs.Count; i++)
        {
            DrawLeg(_c.Legs[i], i, alpha, head, abdomen);
        }
        DrawMandibles(head, fwd, up, right);
        DrawHairs(abdomen, fwd, up, right, h);
        _tube.EndFrame();
    }

    // ———————————————————————— tailEnd（化妆 verlet）————————————————————————

    /// <summary>≙ BigSpiderGraphics tailEnd：追「腹后 + 微垂 + 微漂」，身体网格第三控制点。
    /// 尾展 1.6×腹半径 = 修长椭腹的轴向长度来源（2026-08 用户示意图定型；旧 0.75× 是圆
    /// 栗子时代的取值）。追踪显式收硬：高阻尼 + 强回中 + 微幅漂移——软弹簧参数会让整个
    /// 后腹 Q 弹地晃（用户实测不适合蜘蛛），残余的微漂只保留「活物」底噪。</summary>
    private void StepTail(Vector3 head, Vector3 abdomen, Vector3 fwd, float h)
    {
        float tailLen = _c.Rear.Radius * 1.6f;
        Vector3 restTarget = abdomen - fwd * tailLen;
        if (!_tailInitialized)
        {
            _tailPos = restTarget;
            _tailLastPos = restTarget;
            _tailInitialized = true;
        }
        Vector3 vel = (_tailPos - _tailLastPos) * 0.55f;
        _tailLastPos = _tailPos;
        vel += (restTarget - _tailPos) * (0.55f * (h * 40f));   // 硬追静息位
        vel += Vector3.Down * (0.004f * (h * 40f));             // 微垂
        // 微幅呼吸漂移（≙ breathDir·0.7 的收硬版，RNV → 双 sin 合成的慢游走）。
        vel += new Vector3(
            Mathf.Sin(_time * 0.9f) * 0.4f,
            Mathf.Sin(_time * 1.3f + 1.7f) * 0.3f,
            Mathf.Sin(_time * 1.1f + 3.4f) * 0.4f) * (0.0012f * (h * 40f));
        _tailPos += vel;
        // 不离腹太远（绳约束，≙ ConnectToPoint rad 钳制）。
        Vector3 toTail = _tailPos - abdomen;
        float maxDist = tailLen * 1.12f;
        if (toTail.Length() > maxDist)
        {
            _tailPos = abdomen + toTail.Normalized() * maxDist;
        }
    }

    // ———————————————————————— 身体 ————————————————————————

    /// <summary>三点 Bezier 变径扫管：起点头前伸、双控制点钉腹、终点 tailEnd
    /// （≙ Custom.Bezier(头+头向·3, 腹, 尾, 腹, f)）。半径剖面 = 腹部**椭圆叶**
    /// （中心偏后、覆盖细腰后到尾梢——修长椭腹，2026-08 用户示意图定型；有意偏离原作
    /// Sin(Pow(f,0.75)π) 单峰：那个剖面在 3D 读作扁圆栗子）∨ 头叶瓣第二峰（RW 头椭圆
    /// sprite 的等价物）——双峰同管，头小腹大的双叶剪影一条剖面完成；细腰与尾收锥由
    /// 椭圆两端自然给出。微幅呼吸只调制腹峰（≙ 10+Sin(breath) 的收硬版——大幅呼吸
    /// 读作 Q 弹软体）。</summary>
    private void DrawBody(Vector3 head, Vector3 abdomen, Vector3 fwd, Vector3 up)
    {
        float headR = _c.Primary.Radius;
        float peakR = _c.Rear.Radius * _pal.BodyFat
            + 0.002f * Mathf.Sin(_time * 1.6f);
        Vector3 start = head + fwd * (headR * 0.85f);

        _stations.Clear();
        _bodyProfile.Clear();
        const int stationCount = 20;
        for (int i = 0; i < stationCount; i++)
        {
            float f = i / (float)(stationCount - 1);
            float u = 1f - f;
            Vector3 pos = start * (u * u * u)
                + abdomen * (3f * u * u * f + 3f * u * f * f)
                + _tailPos * (f * f * f);
            // 腹椭圆叶：f∈[0.26, 0.98]，峰值在 0.62——细腰谷（≈0.4×峰）与尾收锥
            // 是椭圆两端的自然结果，不再需要显式 pinch/快收。
            float en = (f - 0.62f) / 0.36f;
            float main = en > -1f && en < 1f ? peakR * Mathf.Sqrt(1f - en * en) : 0f;
            main = Mathf.Max(main, 0.014f);
            float headLobe = 0f;
            float hn = (f - 0.11f) / 0.20f;
            if (hn > -1f && hn < 1f)
            {
                headLobe = headR * 1.02f * Mathf.Sqrt(1f - hn * hn);
            }
            float r = Mathf.Max(main, headLobe);
            _stations.Add(new TubeStation(pos, r, _pal.Body));
            // 毛锚剖面只收「腹部肉身」站：细尾锥/细颈排除（半径小近隐形，毛锚上去读作
            // 悬在肥腹旁——爬墙俯视实测）；头叶瓣站也排除——前倾毛向的射线会打中头叶，
            // 毛垂在脸前（用户实测），RW 蜘蛛的鳞毛长在腹背不长头脸，排除后前倾毛自动
            // 改锚腹前坡。
            if (main >= peakR * 0.30f && main >= headLobe)
            {
                _bodyProfile.Add((pos, main));
            }
        }
        _tube.AddTube(_stations, up, 12);
    }

    /// <summary>p 相对本帧剖面**锥台链**（相邻站间线性插值半径）的有符号距离
    /// （负 = 体内）。必须用锥台链而不是站球并集：快收锥/细腰处，腹峰大球沿斜向的
    /// 球冠远超扫管真实轮廓，按球并集锚毛在腹后上方整圈悬空 5~7cm（用户两轮实测——
    /// 第一轮球面锚定、第二轮球并集射线求交都栽在这一处）。</summary>
    private float BodySignedDistance(Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < _bodyProfile.Count; i++)
        {
            (Vector3 a, float ra) = _bodyProfile[i];
            (Vector3 b, float rb) = _bodyProfile[i + 1];
            Vector3 ab = b - a;
            float len2 = ab.LengthSquared();
            float s = len2 > 1e-12f ? Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f) : 0f;
            Vector3 m = a + ab * s;
            float sd = (p - m).Length() - Mathf.Lerp(ra, rb, s);
            if (sd < best)
            {
                best = sd;
            }
        }
        return best;
    }

    /// <summary>沿 dir 从体内 origin 出发对锥台链表面二分求交（20 轮，亚毫米）。origin
    /// 不在体内或剖面缺失时回落 fallback。</summary>
    private float BodySurfaceDistance(Vector3 origin, Vector3 dir, float fallback)
    {
        if (_bodyProfile.Count < 2 || BodySignedDistance(origin) >= 0f)
        {
            return fallback;
        }
        float tHi = 0f;
        for (int i = 0; i < _bodyProfile.Count; i++)
        {
            (Vector3 c, float r) = _bodyProfile[i];
            tHi = Mathf.Max(tHi, (c - origin).Length() + r);
        }
        tHi += 0.01f;
        float tLo = 0f;
        for (int iter = 0; iter < 20; iter++)
        {
            float tMid = 0.5f * (tLo + tHi);
            if (BodySignedDistance(origin + dir * tMid) < 0f)
            {
                tLo = tMid;
            }
            else
            {
                tHi = tMid;
            }
        }
        return 0.5f * (tLo + tHi);
    }

    // ———————————————————————— 腿 ————————————————————————

    /// <summary>内核两段 IK 姿态（Root/Knee/Foot）画成四件：股节（粗）→ 膝结小瘤 →
    /// 胫节（细、收针）→ 爪尖延伸。粗细梯度 ≙ RW 三段贴图 1.5×/1.2×/1.2×·个体 0.7~1.1；
    /// 直线管 + 膝瘤保住锐利折角（Catmull-Rom 会把膝圆掉——节肢感的关键是硬关节）；
    /// 股节起点沉向体轴融根（≙ 全腿肩点收在「头后 30% 体轴」的聚拢画法）。</summary>
    private void DrawLeg(SpiderLeg leg, int index, float alpha, Vector3 head, Vector3 abdomen)
    {
        Vector3 root = leg.LerpRoot(alpha);
        Vector3 knee = leg.LerpKnee(alpha);
        Vector3 foot = leg.LerpPos(alpha);

        float thick = _legThickness[Math.Min(index, _legThickness.Length - 1)]
            * _pal.LegThickness;
        float baseR = leg.Radius * 0.62f * thick;

        // 融根点：root 沿「root→体轴 30% 点」内沉，腿从身体里长出来。
        Vector3 gather = head.Lerp(abdomen, 0.35f);
        Vector3 rootIn = root.Lerp(gather, 0.45f);

        Vector3 upSeed = leg.BendPole.LengthSquared() > 1e-8f ? leg.BendPole : Vector3.Up;

        // 股节：rootIn → knee（1.5× → 1.25×）。
        _stations.Clear();
        _stations.Add(new TubeStation(rootIn, baseR * 1.55f, _pal.Body));
        _stations.Add(new TubeStation(root.Lerp(knee, 0.5f), baseR * 1.4f, _pal.Body));
        _stations.Add(new TubeStation(knee, baseR * 1.22f, _pal.Body));
        _tube.AddTube(_stations, upSeed, 6);

        // 膝结：关节小瘤略粗于两侧管（硬关节读数，≙ 第三段贴图的居中膝节）。
        _tube.AddKnob(knee, baseR * 1.38f, _pal.Body);

        // 胫节 + 爪尖：knee → foot → 尖端延伸（针尖收细，≙ 贴图自带锥形 + 足尖出画）。
        Vector3 shinDir = foot - knee;
        Vector3 clawDir = shinDir.LengthSquared() > 1e-8f ? shinDir.Normalized() : Vector3.Down;
        Vector3 clawTip = foot + clawDir * (leg.LowerLength * 0.22f);
        _stations.Clear();
        _stations.Add(new TubeStation(knee, baseR * 1.15f, _pal.Body));
        _stations.Add(new TubeStation(knee.Lerp(foot, 0.55f), baseR * 0.85f, _pal.Body));
        _stations.Add(new TubeStation(foot, baseR * 0.48f, _pal.Body));
        _stations.Add(new TubeStation(clawTip, baseR * 0.10f, _pal.Body));
        _tube.AddTube(_stations, upSeed, 6);
    }

    // ———————————————————————— 螯肢 ————————————————————————

    /// <summary>头前一对短螯（≙ MandibleSprite 两段）：根在头前下侧，尖端内弯相向，
    /// 闲置蠕动 = sin 相位微摆（≙ mandibles.vel += RNV·rand）。根部染一点 accent
    /// （≙ 蓄力发光的常暗底色——本项目无咬击意图量，保留静态微光做面部读数）。</summary>
    private void DrawMandibles(Vector3 head, Vector3 fwd, Vector3 up, Vector3 right)
    {
        float headR = _c.Primary.Radius;
        for (int side = 0; side < 2; side++)
        {
            float s = side == 0 ? -1f : 1f;
            float wiggle = Mathf.Sin(_time * 2.6f + _mandiblePhase[side]) * 0.12f
                + Mathf.Sin(_time * 4.1f + _mandiblePhase[side] * 1.7f) * 0.05f;
            Vector3 root = head + fwd * (headR * 0.62f) + right * (s * headR * 0.42f)
                - up * (headR * 0.28f);
            Vector3 mid = root + fwd * (headR * 0.55f) + right * (s * headR * (0.18f + wiggle))
                - up * (headR * 0.18f);
            Vector3 tip = mid + fwd * (headR * 0.42f) - right * (s * headR * (0.30f - wiggle))
                - up * (headR * 0.10f);

            Color baseCol = _pal.Body.Lerp(_pal.Accent, 0.18f);
            _stations.Clear();
            _stations.Add(new TubeStation(root, headR * 0.20f, baseCol));
            _stations.Add(new TubeStation(mid, headR * 0.13f, _pal.Body));
            _stations.Add(new TubeStation(tip, headR * 0.045f, _pal.Body));
            _tube.AddTube(_stations, up, 5);
        }
    }

    // ———————————————————————— 腹毛 ————————————————————————

    /// <summary>≙ scales 系统的 3D 收缩版：每链根锚在本帧扫管表面（局部系冻结方向随
    /// 身体姿态旋转，锚距 = 剖面锥台链二分求交），verlet = 阻尼 0.9 + 弱重力 + 外梳力（锚向 × 呼吸鼓张，≙ ScaleDir 的
    /// Sin(breath) 项）+ n−2 拉直；颜色黑根亮尖（≙ Lerp(black, yellowCol, 链位 · 0.3~0.9)）。
    /// 毛链同时承担 JaggedSquare 毛边的职责——3D 轮廓的「不光滑」由它们盖出来。</summary>
    private void DrawHairs(Vector3 abdomen, Vector3 fwd, Vector3 up, Vector3 right, float h)
    {
        if (_hairs.Count == 0)
        {
            return;
        }
        // 锚在**本帧实际渲染表面**上（剖面锥台链二分求交，微沉半个根粗融根）——球面
        // 或球并集锚定在细腰/尾锥方向都会整根悬空（用户两轮实测，见 BodySignedDistance）。
        float abdR = _c.Rear.Radius * _pal.BodyFat * 0.8f;   // 求交失败的保守回落
        // 鼓张幅度 ±8%（曾 ±25%：毛随呼吸整片起伏读作 Q 弹软体——用户实测不适合蜘蛛）。
        float breathPuff = 1f + 0.08f * (0.5f + 0.5f * Mathf.Sin(_time * 1.6f));
        float dt40 = h * 40f;

        foreach (Hair hair in _hairs)
        {
            Vector3 dir = fwd * hair.Spec.LocalDir.X
                + up * hair.Spec.LocalDir.Y
                + right * hair.Spec.LocalDir.Z;
            float surf = BodySurfaceDistance(abdomen, dir, abdR);
            Vector3 anchor = abdomen + dir * Mathf.Max(0.02f, surf - 0.006f);
            // 外梳方向 = 40% 径向 + 60% 表面切向后方（≙ RW 鳞毛整体向后掠，不是径向
            // 直立）：后掠毛在远侧沿轮廓线方向伸出、投影连着轮廓；垂直支棱的远侧毛
            // 弧根被身体遮挡后读作悬空小点（第四轮实测残余）。
            Vector3 backTan = -fwd + dir * fwd.Dot(dir);
            backTan = backTan.LengthSquared() > 1e-8f ? backTan.Normalized() : Vector3.Zero;
            Vector3 combDir = dir * 0.4f + backTan * 0.6f;
            combDir = combDir.LengthSquared() > 1e-8f ? combDir.Normalized() : dir;
            int n = hair.Spec.Nodes;

            if (!hair.Initialized)
            {
                for (int i = 0; i < n; i++)
                {
                    hair.Pos[i] = anchor + dir * (hair.Spec.SegLength * i);
                    hair.LastPos[i] = hair.Pos[i];
                }
                hair.Initialized = true;
            }

            hair.Pos[0] = anchor;
            hair.LastPos[0] = anchor;
            for (int i = 1; i < n; i++)
            {
                // 阻尼 0.78（毛 = 硬刚毛不是软穗：低阻尼 + 强拉直把摆动压到微颤；大幅
                // 甩弧还会让远侧毛弧中段藏进身体后面、只露弧梢 = 悬空逗号）。
                Vector3 vel = (hair.Pos[i] - hair.LastPos[i]) * 0.78f;
                hair.LastPos[i] = hair.Pos[i];
                vel += Vector3.Down * (0.002f * dt40);
                // 外梳（根强梢弱）× 呼吸鼓张（≙ ScaleDir 根部外推 4px→0 + Sin(breath) 项）。
                float comb = 0.030f * (1f - (i - 1) / (float)Math.Max(1, n - 1));
                vel += combDir * (comb * breathPuff * dt40);
                if (i >= 2)
                {
                    // n−2 拉直：抵抗尖折（≙ 段 n 与 n−2 互推）。
                    Vector3 straighten = hair.Pos[i] - hair.Pos[i - 2];
                    if (straighten.LengthSquared() > 1e-8f)
                    {
                        vel += straighten.Normalized() * (0.024f * dt40);
                    }
                }
                hair.Pos[i] += vel;
            }
            for (int i = 1; i < n; i++)
            {
                Vector3 delta = hair.Pos[i] - hair.Pos[i - 1];
                float dist = delta.Length();
                if (dist > 1e-6f)
                {
                    hair.Pos[i] = hair.Pos[i - 1]
                        + delta * (hair.Spec.SegLength / dist);
                }
                // 体面排斥：自由节永不穿进渲染体管——毛贴体耷拉会读成花纹（v1 实测），
                // RW 毛链有根部外推 + 真地形碰撞，这里对锥台链找最近表面外推做最小等价
                // （球面/球并集两版旧排斥都在细腰/尾锥把链抬离真实表面 = 悬空毛）。
                float bestSd = float.MaxValue;
                Vector3 pushDir = Vector3.Zero;
                for (int s = 0; s + 1 < _bodyProfile.Count; s++)
                {
                    (Vector3 a, float ra) = _bodyProfile[s];
                    (Vector3 b, float rb) = _bodyProfile[s + 1];
                    Vector3 ab = b - a;
                    float len2 = ab.LengthSquared();
                    float u = len2 > 1e-12f
                        ? Mathf.Clamp((hair.Pos[i] - a).Dot(ab) / len2, 0f, 1f) : 0f;
                    Vector3 m = a + ab * u;
                    Vector3 radial = hair.Pos[i] - m;
                    float rDist = radial.Length();
                    float sd = rDist - Mathf.Lerp(ra, rb, u);
                    if (sd < bestSd && rDist > 1e-6f)
                    {
                        bestSd = sd;
                        pushDir = radial / rDist;
                    }
                }
                if (bestSd < 0.005f && bestSd < float.MaxValue)
                {
                    hair.Pos[i] += pushDir * (0.005f - bestSd);
                }
                else if (bestSd < float.MaxValue)
                {
                    // 外壳钳制：整链只在贴体薄壳内活动（根紧梢松）。远侧毛弓离体面太高
                    // 时，弧根/弧中段被身体遮挡、只露弧梢 = 悬空逗号（第三轮实测——毛根
                    // 全部真实扎根后残余的"悬空"读数全部来自这种遮挡弧）；壳内的可见段
                    // 离轮廓线不会远，披垂/外梳在壳内自由。
                    float tt = i / (float)Math.Max(1, n - 1);
                    float hMax = 0.02f + 0.06f * tt * tt;
                    if (bestSd > hMax)
                    {
                        hair.Pos[i] -= pushDir * ((bestSd - hMax) * 0.75f);
                    }
                }
            }

            _pts.Clear();
            _radii.Clear();
            _colors.Clear();
            for (int i = 0; i < n; i++)
            {
                float t = n <= 1 ? 0f : i / (float)(n - 1);
                _pts.Add(hair.Pos[i]);
                _radii.Add(Mathf.Lerp(0.008f, 0.0022f, Mathf.Pow(t, 0.6f)));
                Color tipCol = new(
                    _pal.Accent.R * hair.Spec.Brightness,
                    _pal.Accent.G * hair.Spec.Brightness,
                    _pal.Accent.B * hair.Spec.Brightness);
                // 线性渐亮（≙ RW ApplyPalette 原式）。曾用 pow(t,1.4) 压暗中段：毛中段
                // 横穿轮廓线时在灰背景上隐形，只剩亮梢 = 读作悬空（用户实测第三轮）。
                _colors.Add(_pal.Body.Lerp(tipCol, t));
            }
            SplineSampler.Sample(_pts, _radii, _colors, 2, _stations);
            _tube.AddTube(_stations, dir, 4);
        }
    }
}
