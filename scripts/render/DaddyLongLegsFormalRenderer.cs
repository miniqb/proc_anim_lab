using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.DaddyLongLegs;

namespace ProcAnimLab.Render;

/// <summary>长腿爸爸渲染调色板（≙ DaddyGraphics 三色制：剪影黑 / effectColor / 眼色）。
/// 参考图为黄色个体：daddy 预设按参考图取黄橙 X 眼 + 橄榄黄管梢；terror 保留 RW 大型
/// 蓝色正统（effectColor=(0,0,1) 系）；brother 取小型 plain 的橄榄+棕橙眼（≙ L221-257）。</summary>
internal readonly struct DaddyRenderPalette
{
    public readonly Color Body;   // 球团/管根的剪影黑（微暖，≙ palette.blackColor）
    public readonly Color Effect; // 管梢渐染色（最多只到 40%，≙ OnTubeEffectColorFac）
    public readonly Color Eye;    // X 眼记号

    public DaddyRenderPalette(Color body, Color effect, Color eye)
    {
        Body = body;
        Effect = effect;
        Eye = eye;
    }

    private static readonly Color SilhouetteBlack = new(0.058f, 0.049f, 0.038f);

    public static DaddyRenderPalette ForStableId(string stableId) => stableId switch
    {
        DaddyLongLegsFactory.BrotherId => new DaddyRenderPalette(
            SilhouetteBlack, new Color(0.55f, 0.55f, 0.30f), new Color(0.85f, 0.50f, 0.06f)),
        DaddyLongLegsFactory.TerrorId => new DaddyRenderPalette(
            SilhouetteBlack, new Color(0.25f, 0.40f, 1.00f), new Color(0.55f, 0.76f, 1.00f)),
        _ => new DaddyRenderPalette(
            SilhouetteBlack, new Color(0.60f, 0.50f, 0.16f), new Color(1.00f, 0.68f, 0.10f)),
    };
}

/// <summary>
/// 长腿爸爸正式渲染器（技术验证件四号，≙ DaddyGraphics 的 3D 移植；真相源 =
/// 渲染研究文档 + rw_render_research/agent2 逐行分析）。
/// 球团 = 逐球共享同一张纯平黑材质的球体，显示半径 ×1.1 加深互穿——RW「同色无描边 →
/// 球间棱线不可见」的融合手法在 3D unshaded 下原样成立，不需要 metaball；
/// X 眼 = 每球一个朝质心外向的四臂刀片十字（≙ Eye 的双 slit 90° 交叉），seed 定相位角 +
/// 亮度慢闪，被邻球吞没的眼按突出度自动隐藏（≙ slit 随 chunk 突出度缩放）；
/// 触手 = 从锚球中心出发的 Catmull-Rom 扫管（root melding：前 30% 弧长纯剪影黑，之后
/// pow^1.5×0.4 渐染 effect，≙ OnTubeEffectColorFac），BacktrackFrom 处断管不跨墙平滑；
/// 疣珠 = seed 冻结的离轴小瘤沿管散布（径向偏移大于管径 → 疣状轮廓，≙ OnTubePos.x）；
/// 垂索/死腿 = 纯渲染侧 verlet 垂坠索（重力减半 + i±2 拉直 + 双程距离约束，
/// ≙ DaddyDangleTube / DaddyDeadLeg），两端钉在球或触手上。对内核只读，零写回；
/// 装饰普查用 Morphology.StableSeed 冻结（≙ graphicsSeed），运行时抖动只用渲染侧相位。
/// </summary>
internal sealed class DaddyLongLegsFormalRenderer : IFormalRenderer
{
    private readonly DaddyLongLegsLocomotionController _c;
    private readonly DaddyRenderPalette _pal;

    private readonly TubeMeshBuilder _tube = new();
    private Node3D? _root;
    private readonly List<MeshInstance3D> _balls = new();
    private SphereMesh? _ballMesh;

    private const float TubeRadius = 0.05f;      // ≙ RW 2px；细，量感来自条数不是粗细
    private const float DanglerRadius = 0.036f;

    // —— seed 冻结的装饰普查（Build 时生成，≙ Random.InitState(graphicsSeed) 的整段普查）——
    private readonly struct BumpSpec
    {
        public readonly float T;        // 沿链弧长分数
        public readonly float Angle;    // 环向角
        public readonly float Radial;   // 径向偏移（管径倍数）
        public readonly float Size;     // 瘤半径（米）
        public readonly bool Glow;

        public BumpSpec(float t, float angle, float radial, float size, bool glow)
        {
            T = t;
            Angle = angle;
            Radial = radial;
            Size = size;
            Glow = glow;
        }
    }

    private readonly struct EyeSpec
    {
        public readonly float Twist;       // X 的常量旋转相位（≙ 每眼随机常量角）
        public readonly float FlickPhase;
        public readonly float FlickSpeed;

        public EyeSpec(float twist, float flickPhase, float flickSpeed)
        {
            Twist = twist;
            FlickPhase = flickPhase;
            FlickSpeed = flickSpeed;
        }
    }

    /// <summary>垂索端点：TentacleIndex &lt; 0 → 钉在身体球上，否则钉在该触手的段上。</summary>
    private readonly struct StrandEnd
    {
        public readonly int ChunkIndex;
        public readonly int TentacleIndex;
        public readonly int SegmentIndex;
        public readonly float ChainT; // 触手端的链分数（决定端点颜色渐染）

        public StrandEnd(int chunkIndex, int tentacleIndex, int segmentIndex, float chainT)
        {
            ChunkIndex = chunkIndex;
            TentacleIndex = tentacleIndex;
            SegmentIndex = segmentIndex;
            ChainT = chainT;
        }
    }

    private sealed class Strand
    {
        public StrandEnd EndA;
        public StrandEnd EndB;          // 死腿不用 EndB（PinnedOnly = true）
        public bool PinnedOnly;         // true = 死腿：只钉 EndA，整条下垂
        public float Deadness;          // 死腿的暗淡度（出生随机，≙ RW deadness）
        public float SegLength;         // 死腿固定；垂索每帧按端距重算
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Vector3[] LastPos = Array.Empty<Vector3>();
        public BumpSpec[] Bumps = Array.Empty<BumpSpec>();
        public bool Initialized;
    }

    private EyeSpec[] _eyes = Array.Empty<EyeSpec>();
    private BumpSpec[][] _legBumps = Array.Empty<BumpSpec[]>();
    private Vector3[] _tentUps = Array.Empty<Vector3>();
    private readonly List<Strand> _strands = new();
    private float _time;

    private readonly List<Vector3> _pts = new();
    private readonly List<float> _radii = new();
    private readonly List<Color> _colors = new();
    private readonly List<TubeStation> _stations = new();
    private readonly List<Vector3> _frameSide = new();
    private readonly List<Vector3> _frameUp = new();

    public DaddyLongLegsFormalRenderer(DaddyLongLegsLocomotionController controller)
    {
        _c = controller;
        _pal = DaddyRenderPalette.ForStableId(controller.Morphology.StableId);
    }

    public void Build(Node3D parent)
    {
        _root = new Node3D { TopLevel = true };
        parent.AddChild(_root);
        _tube.Build(_root, srgbVertexColors: true); // 管根须与球体 AlbedoColor 同空间同色

        // 球团：共享一张纯平黑材质——interior 球间棱线因同色不可见（RW 融合手法核心）。
        _ballMesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 };
        var ballMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = _pal.Body,
        };
        foreach (BodyChunk _ in _c.Body.Chunks)
        {
            var ball = new MeshInstance3D { Mesh = _ballMesh, MaterialOverride = ballMat };
            _root.AddChild(ball);
            _balls.Add(ball);
        }

        SeedDecorations();
    }

    /// <summary>装饰普查：眼相位、逐触手疣珠、垂索/死腿拓扑全部由 StableSeed 冻结
    /// （≙ DaddyGraphics ctor 的 Random.InitState(graphicsSeed) 普查段）。渲染侧私有，
    /// 不触碰内核任何状态。</summary>
    private void SeedDecorations()
    {
        ulong seed = _c.Morphology.StableSeed;
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32) ^ 0x9E3779B9UL)));
        int chunkCount = _c.Body.Chunks.Count;
        int tentacleCount = _c.Tentacles.Count;

        _eyes = new EyeSpec[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            _eyes[i] = new EyeSpec(
                (float)rng.NextDouble() * Mathf.Tau,
                (float)rng.NextDouble() * Mathf.Tau,
                Mathf.Lerp(0.5f, 1.4f, (float)rng.NextDouble()));
        }

        _legBumps = new BumpSpec[tentacleCount][];
        _tentUps = new Vector3[tentacleCount];
        for (int i = 0; i < tentacleCount; i++)
        {
            _tentUps[i] = Vector3.Up;
            int segCount = _c.Tentacles[i].Segments.Count;
            int bumpCount = segCount / 2 + rng.Next(5, 9); // ≙ segments/2 + Random(5,8)
            var bumps = new BumpSpec[bumpCount];
            for (int b = 0; b < bumpCount; b++)
            {
                bumps[b] = new BumpSpec(
                    Mathf.Lerp(0.08f, 1f, (float)rng.NextDouble()),
                    (float)rng.NextDouble() * Mathf.Tau,
                    Mathf.Lerp(0.4f, 1.8f, (float)rng.NextDouble()),
                    Mathf.Lerp(0.03f, 0.075f, Mathf.Pow((float)rng.NextDouble(), 1.4f)),
                    rng.NextDouble() < 0.35);
            }
            _legBumps[i] = bumps;
        }

        // 垂索（≙ DaddyDangleTube：两个 Connection 之间的下垂缆）+ 死腿（≙ DaddyDeadLeg）。
        _strands.Clear();
        int danglerCount = rng.Next(5, Math.Min(15, 6 + chunkCount));
        for (int i = 0; i < danglerCount; i++)
        {
            var strand = new Strand
            {
                EndA = RandomStrandEnd(rng, chunkCount),
                EndB = RandomStrandEnd(rng, chunkCount),
                PinnedOnly = false,
                SegLength = 0.2f,
            };
            InitStrandParts(strand, rng.Next(6, 15), rng, danglerBumps: true);
            _strands.Add(strand);
        }
        int deadLegCount = rng.Next(1, 3);
        for (int i = 0; i < deadLegCount; i++)
        {
            int parts = rng.Next(7, 15);
            var strand = new Strand
            {
                EndA = new StrandEnd(rng.Next(chunkCount), -1, 0, 0f),
                PinnedOnly = true,
                Deadness = (float)rng.NextDouble(),
                SegLength = Mathf.Lerp(1.6f, 3.2f, (float)rng.NextDouble()) / (parts - 1),
            };
            InitStrandParts(strand, parts, rng, danglerBumps: true);
            _strands.Add(strand);
        }
    }

    private StrandEnd RandomStrandEnd(Random rng, int chunkCount)
    {
        // ≙ RW Connection：一半概率钉身体球，一半钉某条腿 legPos ≤ 0.7 的近根位置。
        if (rng.NextDouble() < 0.5 || _c.Tentacles.Count == 0)
        {
            return new StrandEnd(rng.Next(chunkCount), -1, 0, 0f);
        }
        int tent = rng.Next(_c.Tentacles.Count);
        int segCount = _c.Tentacles[tent].Segments.Count;
        int seg = rng.Next(Math.Max(1, (int)(segCount * 0.7f)));
        return new StrandEnd(0, tent, seg, (seg + 1) / (float)segCount);
    }

    private void InitStrandParts(Strand strand, int parts, Random rng, bool danglerBumps)
    {
        strand.Pos = new Vector3[parts];
        strand.LastPos = new Vector3[parts];
        int bumpCount = danglerBumps ? rng.Next(5, 13) : 0;
        var bumps = new BumpSpec[bumpCount];
        for (int b = 0; b < bumpCount; b++)
        {
            bumps[b] = new BumpSpec(
                Mathf.Lerp(0.1f, 0.9f, (float)rng.NextDouble()),
                (float)rng.NextDouble() * Mathf.Tau,
                Mathf.Lerp(0.2f, 0.8f, (float)rng.NextDouble()),
                Mathf.Lerp(0.028f, 0.06f, Mathf.Pow((float)rng.NextDouble(), 1.3f)),
                rng.NextDouble() < 0.2);
        }
        strand.Bumps = bumps;
    }

    public void Clear()
    {
        _tube.Clear();
        _root?.QueueFree();
        _root = null;
        _balls.Clear();
        _ballMesh = null;
        _strands.Clear();
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
        Vector3 center = InterpolatedMassCenter(alpha);

        var chunks = _c.Body.Chunks;
        for (int i = 0; i < _balls.Count && i < chunks.Count; i++)
        {
            BodyChunk chunk = chunks[i];
            float r = BallRadius(chunk); // ≙ RW rad*1.1+2px：比例+加法双放大，加深互穿融合
            _balls[i].Transform = new Transform3D(Basis.Identity.Scaled(new Vector3(r, r, r)),
                chunk.LerpPos(alpha));
        }

        _tube.BeginFrame();
        for (int i = 0; i < _c.Tentacles.Count; i++)
        {
            DrawTentacle(_c.Tentacles[i], i, alpha);
        }
        DrawStrands(alpha, dt, center);
        DrawEyes(alpha, center);
        _tube.EndFrame();
    }

    private static float BallRadius(BodyChunk chunk) => chunk.Radius * 1.1f + 0.05f;

    private Vector3 InterpolatedMassCenter(float alpha)
    {
        // controller.BodyCenter 是上一 tick 缓存——渲染要平滑必须自己按插值位重算。
        Vector3 sum = Vector3.Zero;
        float mass = 0f;
        foreach (BodyChunk chunk in _c.Body.Chunks)
        {
            sum += chunk.LerpPos(alpha) * chunk.Mass;
            mass += chunk.Mass;
        }
        return mass > 0f ? sum / mass : Vector3.Zero;
    }

    // ———————————————————————— 触手 ————————————————————————

    /// <summary>触手扫管：锚球中心起笔（root melding——管从球内长出来，前 30% 纯剪影黑，
    /// 融根不需要任何圆角几何），BacktrackFrom 处断成两段，绝不跨阻断边平滑
    /// （否则管会横穿墙体，测绘 map_1 的硬约束）。</summary>
    private void DrawTentacle(DaddyTentacle tentacle, int index, float alpha)
    {
        var segments = tentacle.Segments;
        int n = segments.Count;
        if (n == 0)
        {
            return;
        }

        float jitterAmp = tentacle.StunTicks > 0
            ? Mathf.Min(1f, tentacle.StunTicks / 12f) * 0.05f
            : 0f; // ≙ stun 时喂点抖动 RNV()*InverseLerp(4,14,stun)*4px（渲染侧噪声）

        // frame 种子 up：投影去切向后低通保持，疣珠环向角不随帧跳变。
        Vector3 rootPos = tentacle.Anchor.LerpPos(alpha);
        Vector3 firstDir = segments[0].LerpPos(alpha) - rootPos;
        if (firstDir.LengthSquared() > 1e-8f)
        {
            firstDir = firstDir.Normalized();
            Vector3 carried = _tentUps[index] - firstDir * _tentUps[index].Dot(firstDir);
            if (carried.LengthSquared() > 1e-6f)
            {
                _tentUps[index] = carried.Normalized();
            }
        }

        int split = tentacle.BacktrackFrom; // -1 = 整链连通
        int chainPoints = n + 1;            // 锚 + n 段
        if (split < 0)
        {
            DrawTentaclePiece(tentacle, index, alpha, 0, n, 0f, 1f, jitterAmp, tipPiece: true);
        }
        else
        {
            // 前缀：锚..split-1；后缀：split..n-1（独立断片，颜色保持原链分数）。
            if (split > 0)
            {
                DrawTentaclePiece(tentacle, index, alpha, 0, split,
                    0f, split / (float)(chainPoints - 1), jitterAmp, tipPiece: false);
            }
            if (split < n)
            {
                DrawTentaclePiece(tentacle, index, alpha, split, n,
                    (split + 1) / (float)(chainPoints - 1), 1f, jitterAmp, tipPiece: true);
            }
        }
    }

    /// <summary>画 [segStart, segEnd) 的连续段片。segStart==0 时含锚点起笔。t0/t1 =
    /// 该片在整链上的弧长分数范围（决定渐染颜色与疣珠归属）。</summary>
    private void DrawTentaclePiece(DaddyTentacle tentacle, int index, float alpha,
        int segStart, int segEnd, float t0, float t1, float jitterAmp, bool tipPiece)
    {
        var segments = tentacle.Segments;
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();

        int count = segEnd - segStart + (segStart == 0 ? 1 : 0);
        if (count < 2)
        {
            return;
        }

        int k = 0;
        if (segStart == 0)
        {
            _pts.Add(Jitter(tentacle.Anchor.LerpPos(alpha), index, k, jitterAmp));
            _radii.Add(0.085f); // 根部微喇叭：从球内长出的过渡
            _colors.Add(TubeColorAt(0f));
            k++;
        }
        for (int i = segStart; i < segEnd; i++)
        {
            float t = Mathf.Lerp(t0, t1, count <= 1 ? 0f : k / (float)(count - 1));
            _pts.Add(Jitter(segments[i].LerpPos(alpha), index, k, jitterAmp));
            _radii.Add(segStart == 0 && k == 1 ? 0.062f : TubeRadius);
            _colors.Add(TubeColorAt(t));
            k++;
        }
        if (tipPiece && _pts.Count >= 2)
        {
            // 尖端外推（≙ RW tip +1px），半径收细盖锥帽。
            Vector3 tipDir = _pts[^1] - _pts[^2];
            if (tipDir.LengthSquared() > 1e-8f)
            {
                _pts.Add(_pts[^1] + tipDir.Normalized() * (tentacle.LinkLength * 0.35f));
                _radii.Add(0.018f);
                _colors.Add(TubeColorAt(1f));
            }
        }

        SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
        _tube.AddTube(_stations, _tentUps[index], 7);

        DrawLegBumps(index, t0, t1);
    }

    private Vector3 Jitter(Vector3 pos, int tentacleIndex, int pointIndex, float amp)
    {
        if (amp <= 0f)
        {
            return pos;
        }
        float s = tentacleIndex * 7.3f + pointIndex * 1.7f;
        return pos + new Vector3(
            Mathf.Sin(s + _time * 37f),
            Mathf.Sin(s * 1.31f + _time * 41f + 1.7f),
            Mathf.Sin(s * 1.77f + _time * 43f + 3.1f)) * amp;
    }

    /// <summary>管体渐染（≙ OnTubeEffectColorFac + ApplyPalette：前段纯剪影黑 + pow 渐升，
    /// 管根消失在球团里的全部秘密）。RW 的 0.3/0.4 参数在 3D 亮色下整条腿发黄（首轮截图
    /// 实证），收紧为 45% 起染、梢端至多 22%——参考图的腿近乎全黑，黄只属于 X 眼。</summary>
    private Color TubeColorAt(float t)
    {
        float f = Mathf.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        return _pal.Body.Lerp(_pal.Effect, Mathf.Pow(f, 1.8f) * 0.22f);
    }

    /// <summary>疣珠：径向偏移可超过管径（≙ OnTubePos.x = ±2..3px > 管径 2px）——瘤挂在
    /// 管缘外侧，把光滑管轮廓搅成有机疣状。用刚采样的 _stations + 平行传输 frame 定位。</summary>
    private void DrawLegBumps(int tentacleIndex, float t0, float t1)
    {
        BumpSpec[] bumps = _legBumps[tentacleIndex];
        if (bumps.Length == 0 || _stations.Count < 2)
        {
            return;
        }
        ComputeFrames(_stations, _tentUps[tentacleIndex]);
        foreach (BumpSpec bump in bumps)
        {
            if (bump.T < t0 || bump.T > t1)
            {
                continue;
            }
            float local = t1 <= t0 ? 0f : (bump.T - t0) / (t1 - t0);
            int idx = Mathf.Clamp(Mathf.RoundToInt(local * (_stations.Count - 1)),
                0, _stations.Count - 1);
            TubeStation st = _stations[idx];
            Vector3 offset = _frameSide[idx] * Mathf.Cos(bump.Angle)
                + _frameUp[idx] * Mathf.Sin(bump.Angle);
            Vector3 pos = st.Pos + offset * (st.Radius * bump.Radial);
            _tube.AddKnob(pos, bump.Size, st.Color);
            if (bump.Glow)
            {
                // ≙ eyeSize 疣上的发光点：更小的暗亮色瘤压在外缘（保持整腿近黑的克制）。
                _tube.AddKnob(pos + offset * (bump.Size * 0.55f), bump.Size * 0.45f,
                    _pal.Body.Lerp(_pal.Effect, 0.4f));
            }
        }
    }

    /// <summary>对刚采样的管站序列重算平行传输 frame（side/up 逐站），供疣珠环向定位。
    /// 与 TubeMeshBuilder.AddTube 内部同一算法；管本体照常由 AddTube 自算。</summary>
    private void ComputeFrames(List<TubeStation> stations, Vector3 upSeed)
    {
        _frameSide.Clear();
        _frameUp.Clear();
        int n = stations.Count;
        Vector3 up = upSeed;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = stations[Math.Max(0, i - 1)].Pos;
            Vector3 b = stations[Math.Min(n - 1, i + 1)].Pos;
            Vector3 tangent = b - a;
            tangent = tangent.LengthSquared() > 1e-10f ? tangent.Normalized() : Vector3.Right;
            Vector3 carried = up - tangent * up.Dot(tangent);
            if (carried.LengthSquared() > 1e-8f)
            {
                up = carried.Normalized();
            }
            else
            {
                up = tangent.Cross(Vector3.Right);
                up = up.LengthSquared() > 1e-8f ? up.Normalized()
                    : tangent.Cross(Vector3.Up).Normalized();
            }
            _frameSide.Add(tangent.Cross(up).Normalized());
            _frameUp.Add(up);
        }
    }

    // ———————————————————————— 垂索 / 死腿 ————————————————————————

    private void DrawStrands(float alpha, float dt, Vector3 center)
    {
        // 渲染侧 verlet：帧长钳制防大 dt 爆炸；纯化妆状态，不进任何 tick/哈希。
        float h = Mathf.Clamp(dt, 0f, 1f / 30f);
        foreach (Strand strand in _strands)
        {
            (Vector3 pinA, Color colA) = ResolveStrandEnd(strand.EndA, alpha);
            Vector3 pinB = pinA;
            Color colB = colA;
            if (!strand.PinnedOnly)
            {
                (pinB, colB) = ResolveStrandEnd(strand.EndB, alpha);
            }
            if (strand.Deadness > 0f)
            {
                // ≙ RW deadness：死腿渐染整体压暗。
                colA = _pal.Body;
                colB = _pal.Body.Lerp(_pal.Effect, Mathf.Lerp(0.2f, 0.05f, strand.Deadness));
            }

            StepStrand(strand, pinA, pinB, center, h);
            DrawStrandTube(strand, colA, colB);
        }
    }

    private (Vector3, Color) ResolveStrandEnd(StrandEnd end, float alpha)
    {
        if (end.TentacleIndex >= 0 && end.TentacleIndex < _c.Tentacles.Count)
        {
            DaddyTentacle tent = _c.Tentacles[end.TentacleIndex];
            int seg = Mathf.Clamp(end.SegmentIndex, 0, tent.Segments.Count - 1);
            // 端点颜色 = 该腿链分数处的渐染值（≙ RW 垂索两端取腿渐变的对应值）。
            return (tent.Segments[seg].LerpPos(alpha), TubeColorAt(end.ChainT));
        }
        var chunks = _c.Body.Chunks;
        int idx = Mathf.Clamp(end.ChunkIndex, 0, chunks.Count - 1);
        return (chunks[idx].LerpPos(alpha), _pal.Body);
    }

    /// <summary>≙ DaddyDangleTube.Update：半重力 + i±2 拉直弹簧 + 双程距离约束（端点每程
    /// 重钉）。垂索自然段长 = max(下限, 端距/n)——端点靠近时富余长度垂成深环，正是参考图
    /// 身下的下垂缆。死腿（PinnedOnly）另加近根径向外推（≙ 从身体中心向外压出再下垂）。</summary>
    private void StepStrand(Strand strand, Vector3 pinA, Vector3 pinB, Vector3 center, float h)
    {
        Vector3[] pos = strand.Pos;
        Vector3[] last = strand.LastPos;
        int n = pos.Length;
        if (!strand.Initialized)
        {
            for (int i = 0; i < n; i++)
            {
                float t = n <= 1 ? 0f : i / (float)(n - 1);
                pos[i] = strand.PinnedOnly
                    ? pinA + Vector3.Down * (strand.SegLength * i)
                    : pinA.Lerp(pinB, t);
                last[i] = pos[i];
            }
            strand.Initialized = true;
        }

        float segLen = strand.PinnedOnly
            ? strand.SegLength
            : Mathf.Max(0.18f, pinA.DistanceTo(pinB) / Math.Max(1, n - 1));
        float gravity = 18f * h * h; // ≙ 重力 ×0.5（渲染帧步长）

        for (int i = 0; i < n; i++)
        {
            bool pinned = i == 0 || (!strand.PinnedOnly && i == n - 1);
            if (pinned)
            {
                continue;
            }
            Vector3 vel = (pos[i] - last[i]) * 0.94f;
            last[i] = pos[i];
            vel += Vector3.Down * gravity;
            // i±2 拉直弹簧：抵抗尖折，垂出平滑悬链线。
            if (i >= 2)
            {
                vel += DirTo(pos[i], pos[i - 2]) * (0.010f * (h * 40f));
            }
            if (i + 2 < n)
            {
                vel += DirTo(pos[i], pos[i + 2]) * (0.010f * (h * 40f));
            }
            if (strand.PinnedOnly && i < 5)
            {
                // ≙ DaddyDeadLeg：近根 5 节沿「身体中心→根」方向外推后交给重力。
                Vector3 outward = pinA - center;
                if (outward.LengthSquared() > 1e-6f)
                {
                    vel += outward.Normalized() * (0.02f * (1f - i / 5f) * (h * 40f));
                }
            }
            pos[i] += vel;
        }

        // 双程距离约束（A→B 再 B→A），每程重钉端点。
        for (int pass = 0; pass < 2; pass++)
        {
            pos[0] = pinA;
            if (!strand.PinnedOnly)
            {
                pos[n - 1] = pinB;
            }
            if (pass == 0)
            {
                for (int i = 1; i < n; i++)
                {
                    SolveStrandLink(pos, i - 1, i, segLen, pinFirst: i - 1 == 0);
                }
            }
            else
            {
                for (int i = n - 1; i >= 1; i--)
                {
                    SolveStrandLink(pos, i, i - 1,
                        segLen, pinFirst: !strand.PinnedOnly && i == n - 1);
                }
            }
        }
        pos[0] = pinA;
        if (!strand.PinnedOnly)
        {
            pos[n - 1] = pinB;
        }
    }

    private static void SolveStrandLink(Vector3[] pos, int a, int b, float segLen, bool pinFirst)
    {
        Vector3 delta = pos[b] - pos[a];
        float dist = delta.Length();
        if (dist < 1e-6f)
        {
            return;
        }
        Vector3 correction = delta * ((dist - segLen) / dist);
        if (pinFirst)
        {
            pos[b] -= correction;
        }
        else
        {
            pos[a] += correction * 0.5f;
            pos[b] -= correction * 0.5f;
        }
    }

    private static Vector3 DirTo(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        return d.LengthSquared() > 1e-8f ? d.Normalized() : Vector3.Zero;
    }

    private void DrawStrandTube(Strand strand, Color colA, Color colB)
    {
        Vector3[] pos = strand.Pos;
        int n = pos.Length;
        if (n < 2)
        {
            return;
        }
        _pts.Clear();
        _radii.Clear();
        _colors.Clear();
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            _pts.Add(pos[i]);
            _radii.Add(strand.PinnedOnly && i == n - 1 ? 0.016f : DanglerRadius);
            _colors.Add(colA.Lerp(colB, t));
        }
        SplineSampler.Sample(_pts, _radii, _colors, 3, _stations);
        _tube.AddTube(_stations, Vector3.Up, 5);

        if (strand.Bumps.Length > 0)
        {
            ComputeFrames(_stations, Vector3.Up);
            foreach (BumpSpec bump in strand.Bumps)
            {
                int idx = Mathf.Clamp(Mathf.RoundToInt(bump.T * (_stations.Count - 1)),
                    0, _stations.Count - 1);
                TubeStation st = _stations[idx];
                Vector3 offset = _frameSide[idx] * Mathf.Cos(bump.Angle)
                    + _frameUp[idx] * Mathf.Sin(bump.Angle);
                _tube.AddKnob(st.Pos + offset * (st.Radius * bump.Radial), bump.Size, st.Color);
            }
        }
    }

    // ———————————————————————— X 眼 ————————————————————————

    /// <summary>X 眼记号（≙ Eye 的双 slit 90° 交叉 + BulgeVertex 球面包裹的简化）：四臂
    /// 尖头刀片贴在球面外向点上，臂梢沿球面收拢。被邻球吞没的眼整只隐藏（≙ slit 突出度
    /// 随 chunk 距质心距离缩放）；亮度慢闪 = 渲染侧相位（≙ light/blink）。</summary>
    private void DrawEyes(float alpha, Vector3 center)
    {
        var chunks = _c.Body.Chunks;
        for (int i = 0; i < chunks.Count; i++)
        {
            BodyChunk chunk = chunks[i];
            Vector3 chunkPos = chunk.LerpPos(alpha);
            Vector3 outward = chunkPos - center;
            if (outward.LengthSquared() < 1e-6f)
            {
                outward = _c.MaterialAxisY;
            }
            outward = outward.Normalized();
            float r = BallRadius(chunk);
            // apex/臂梢都抬到球面之上：直线刀片的弦会沉入球体（v4 实测只剩橙色碎点），
            // 抬高后整个 X 浮贴在球壳外侧，≙ RW slit 画在 ball sprite 之上的 z 序。
            Vector3 apex = chunkPos + outward * (r * 1.12f);

            // 遮埋测试半径必须略大于邻球**渲染**半径（首轮实测：按物理半径测会让 X 藏在
            // 邻球渲染面之下，≙ RW slit 突出度门的 3D 等价物）。
            bool buried = false;
            for (int j = 0; j < chunks.Count && !buried; j++)
            {
                if (j != i && apex.DistanceTo(chunks[j].LerpPos(alpha))
                    < BallRadius(chunks[j]) * 1.03f)
                {
                    buried = true;
                }
            }
            if (buried)
            {
                continue;
            }

            Vector3 tanU = _c.MaterialAxisX - outward * _c.MaterialAxisX.Dot(outward);
            if (tanU.LengthSquared() < 1e-6f)
            {
                tanU = _c.MaterialAxisZ - outward * _c.MaterialAxisZ.Dot(outward);
            }
            tanU = tanU.Normalized();
            Vector3 tanV = outward.Cross(tanU).Normalized();

            EyeSpec eye = _eyes[Math.Min(i, _eyes.Length - 1)];
            float flick = 0.86f + 0.14f * Mathf.Sin(_time * eye.FlickSpeed + eye.FlickPhase);
            var eyeCol = new Color(_pal.Eye.R * flick, _pal.Eye.G * flick, _pal.Eye.B * flick);

            // 两笔交叉笔画，每笔 = 中心宽底、两端收尖的双三角（宽底在 apex 处相连 →
            // 实心 X；四臂尖根方案会读成四角星，v6 实测否决）。
            float armLen = r * 0.74f;
            float halfWidth = r * 0.16f;
            for (int stroke = 0; stroke < 2; stroke++)
            {
                float ang = eye.Twist + stroke * (Mathf.Tau / 4f) + Mathf.Tau / 8f;
                Vector3 dir = tanU * Mathf.Cos(ang) + tanV * Mathf.Sin(ang);
                Vector3 widthDir = outward.Cross(dir).Normalized() * halfWidth;
                // 臂梢沿球面收拢：外向+切向合成后归一化回球壳外侧。
                Vector3 tipPlus = chunkPos
                    + (outward + dir * (armLen / r)).Normalized() * (r * 1.10f);
                Vector3 tipMinus = chunkPos
                    + (outward - dir * (armLen / r)).Normalized() * (r * 1.10f);
                _tube.AddFin(apex + widthDir, apex - widthDir, tipPlus, eyeCol);
                _tube.AddFin(apex + widthDir, apex - widthDir, tipMinus, eyeCol);
            }
        }
    }
}
