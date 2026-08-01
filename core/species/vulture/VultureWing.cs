using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Vulture;

/// <summary>
/// 一只翅膀 = 一条追「拍动行波目标点」的粒子链（≙ RW VultureTentacle : Tentacle 的
/// chunk 触手本体；tile 触手层不移植——其职责【视线合法性/绕障/穿墙自救】由射线验证与
/// 卡链释放类机制承接，见研究文档 §12「不重造格子」）。
///
/// 不复用 <see cref="Limb"/>：Limb 是 plant-and-trail 步态状态机（迈步阈值/对角协调/
/// 换步僵局），全部是地面步态概念且对蜥蜴控制器有硬引用；翅膀要的是「按拍动相位追
/// 周期目标点的链」+「抓地悬挂」，共享的只有 SphereTerrain 碰撞语义。
///
/// 两模式（≙ VultureTentacle.Mode）：
/// · Flap（≙ Fly）：每节沿翼展排开 + 法向 sin 行波偏移（翼尖滞后半周期）+ 饱和伺服。
///   升力**不在这里**——链对身体零回传（≙ pullAtConnectionChunk=0），升力由控制器
///   按同一相位注入后脊柱（≙ RW 升力写在翅膀类里但纯由相位驱动、不含链状态）。
/// · Grab（≙ Climb）：射线找抓点 → 翅尖吸附硬钉 → 各节贴墙吸附计支撑；
///   身体超出翼长时悬挂拉力把锚 chunk 拽向翅尖（唯一的翅→身体真实约束，只拉不推）。
///
/// 链约束 = 只抗拉伸的绳（≙ tProps.stiff=false）：根节↔锚全部修正落在翅节
/// （零回传），节间 50/50 分摊（≙ massDeteriorationPerChunk=0.5），Pos/Vel 同步修正。
/// 确定性：固定顺序、零随机（RW 的随机杆子搜索/stun 抖动不移植）。
/// </summary>
public sealed class VultureWing
{
    /// <summary>翅膀模式（≙ VultureTentacle.Mode：Climb/Fly）。切换由控制器统一决策。</summary>
    public enum WingMode
    {
        Flap,
        Grab,
    }

    /// <summary>翅节粒子（语义同 BodyChunk：Vel = 米/tick，LastPos 仅渲染插值/碰撞扫掠）。
    /// 不进 Body.Chunks——独立受力点，会碰地形但不参与 chunk 间物理（≙ TentacleChunk）。</summary>
    public sealed class WingSegment
    {
        public Vector3 Pos;
        public Vector3 LastPos;
        public Vector3 Vel;
        public readonly float Radius;
        public bool TerrainContact;
        public Vector3 ContactNormal = Vector3.Up;

        public WingSegment(Vector3 pos, float radius)
        {
            Pos = pos;
            LastPos = pos;
            Radius = radius;
        }

        public Vector3 LerpPos(float t) => LastPos.Lerp(Pos, t);
    }

    public WingMode Mode { get; private set; } = WingMode.Grab; // 出生栖息态（≙ 构造默认 Climb）

    public readonly WingSegment[] Segments;

    /// <summary>本翅锚定的肩 chunk（≙ connectedChunk：bodyChunks[2]/[3]）。</summary>
    public readonly BodyChunk Anchor;

    /// <summary>对侧肩 chunk（翼展方向参照：本肩背离对肩，≙ RW 目标点公式）。</summary>
    public readonly BodyChunk OppositeAnchor;

    /// <summary>横向符号：-1 左 / +1 右。</summary>
    public readonly int Side;

    /// <summary>同对的另一翅（模式协调用），构造后由工厂互联。</summary>
    public VultureWing? Pair;

    /// <summary>第二对翅膀的翼展后倾混合量（四翼拓扑；前对恒 0）。</summary>
    public float SpanTilt;

    /// <summary>收拢/展开翼长（米，≙ idealLength = Lerp(140,220,flyingMode)px）。</summary>
    public float FoldedLength = 3.5f;
    public float FlyLength = 5.5f;

    /// <summary>展开度 ∈[0,1]（≙ flyingMode）：Flap +0.05/tick，Grab −0.025/tick。
    /// 驱动翼长与（渲染层）羽毛展开。</summary>
    public float FlyingMode { get; private set; }

    public float IdealLength => Mathf.Lerp(FoldedLength, FlyLength, FlyingMode);

    // —— Grab 态 ——
    /// <summary>当前抓点（FindGrab 命中时有效）。</summary>
    public Vector3 GripPos { get; private set; }

    /// <summary>抓握面法线。</summary>
    public Vector3 GripNormal { get; private set; } = Vector3.Up;

    /// <summary>GripPos 是本次射线搜索背书的真实地形点（≙ Limb.HasGrip 的语义教训：
    /// 只有真落点才计支撑，否则悬空追空气也算抓稳）。</summary>
    public bool HasGrip { get; private set; }

    /// <summary>翅尖已吸附硬钉在抓点上（≙ attachedAtTip）。</summary>
    public bool Attached { get; private set; }

    /// <summary>连续吸附 tick 数；≥ GripDelay 才算「抓稳」。</summary>
    public int GripCounter { get; private set; }

    public int GripDelay = 4;

    /// <summary>松手冷却（≙ grabDelay=10：两翅错开重抓）。</summary>
    public int GrabDelay { get; private set; }

    /// <summary>连续找不到抓点/未吸附的 tick 数（≙ framesWithoutReaching：>60 → 改飞）。</summary>
    public int FramesWithoutGrip { get; private set; }

    /// <summary>Flap 中翅节持续撞地形的迟滞计数（撞+1/无撞−1 钳 0..30，≙
    /// framesOfHittingTerrain：满 30 → 控制器切 Grab）。</summary>
    public int TerrainHitFrames { get; private set; }

    /// <summary>当前模式已持续 tick 数（模式协调判据）。</summary>
    public int ModeTicks { get; private set; }

    /// <summary>本 tick 贴附在地形上的翅节数（≙ segmentsGrippingTerrain）。</summary>
    public int GrippingSegments { get; private set; }

    /// <summary>抓稳中：翅尖钉住且计满延迟。</summary>
    public bool Gripping => Attached && GripCounter >= GripDelay;

    /// <summary>支撑度 ∈[0,1]（≙ VultureTentacle.Support()）：Grab = 0.5×吸附 + 贴附节占比；
    /// Flap 恒 0.5——展开的翅膀本身算半个支撑。控制器求和后开方喂给全部身体力。</summary>
    public float Support() => Mode == WingMode.Flap
        ? 0.5f
        : Mathf.Clamp((Attached ? 0.5f : 0f)
            + (float)GrippingSegments / Segments.Length, 0f, 1f);

    // —— 参数（工厂从 VultureBreedParams 灌入）——
    /// <summary>翅尖吸附捕获半径（≙ 40px = 1.0m）。</summary>
    public float TipSnapRadius = 1.0f;

    /// <summary>期望抓点距离占翼长比（≙ 0.7×idealLength）。</summary>
    public float GrabReachFrac = 0.7f;

    /// <summary>翅节速度上限（≙ chunkVelocityCap 10px = 0.25）。</summary>
    public float SegmentVelocityCap = 0.25f;

    /// <summary>伺服每 tick 注入上限（≙ 5px；×Lerp(0.2,1,amp)）。</summary>
    public float ServoStrength = 0.125f;

    /// <summary>拍动扫掠幅（米；控制器每 tick 按振幅算好传入 TickFlap）。</summary>

    private const float SpreadForce = 0.015f;      // ≙ 0.6px 侧向展开恒力
    private const float FlapDamping = 0.95f;       // ≙ Fly 模式 vel*=0.95
    private const float RootDrive0 = 0.05f;        // ≙ 翼根 k=0 硬驱动 2px
    private const float RootDrive1 = 0.03f;        // ≙ 翼根 k=1 硬驱动 1.2px
    private const float GripSegmentDamping = 0.25f; // ≙ GripTerrain vel*=0.25
    private const float GripPressForce = 0.02f;    // ≙ 朝墙吸 0.8px
    private const float FloatLift = 0.0025f;       // ≙ 未附着节上浮 0.1px
    private const float BodyVelFollow = 0.1f;      // ≙ 未附着节随体 ×0.1
    private const float GrabServoMid = 0.03f;      // ≙ goalAttractionSpeed 1.2px
    private const float GrabServoTip = 0.005f;     // ≙ goalAttractionSpeedTip 0.2px
    private const float HangPullGain = 0.2f;       // ≙ 悬挂拉体 (0.9L−d)×0.2
    private const int ReleaseCooldown = 10;        // ≙ grabDelay
    private const float GrabProbeUp = 0.35f;
    private const float GrabProbeDown = 0.85f;

    /// <summary>FindGrab 期望点沿水平展向的采样偏移（米）——固定顺序保确定性。</summary>
    private static readonly float[] GrabSamples = { 0f, -0.25f, 0.25f, -0.5f, 0.5f };

    public VultureWing(BodyChunk anchor, BodyChunk oppositeAnchor, int side, int segments,
        Vector3 spawnDir, float segmentRadiusScale)
    {
        Anchor = anchor;
        OppositeAnchor = oppositeAnchor;
        Side = side;
        Segments = new WingSegment[segments];
        // 出生收拢：沿出生展向紧凑排开（确定性相位种子；落地后被约束/碰撞自然铺开）。
        float linkLen = FoldedLength / segments * 0.5f;
        for (int m = 0; m < segments; m++)
        {
            float tPos = (m + 1) / (float)segments;
            // 翼型轮廓（≙ TentacleContour：根细、中最粗 0.16m、尖收窄）。
            float radius = (0.06f + 0.10f * Mathf.Sin(Mathf.Pi * Mathf.Min(tPos, 1f)))
                * segmentRadiusScale;
            Segments[m] = new WingSegment(anchor.Pos + spawnDir * (linkLen * (m + 1)), radius);
        }
    }

    /// <summary>模式切换（控制器统一调用，≙ SwitchMode）：切 Flap 清抓点（≙ 清
    /// floatGrabDest），切 Grab 清撞地计数。同模式无操作。</summary>
    public void SwitchMode(WingMode mode)
    {
        if (Mode == mode)
        {
            return;
        }
        Mode = mode;
        ModeTicks = 0;
        TerrainHitFrames = 0;
        FramesWithoutGrip = 0;
        if (mode == WingMode.Flap)
        {
            DropGrip(cooldown: false);
        }
    }

    /// <summary>强制松手（Launch/Teleport/悬挂看门狗用）：丢抓点 + 松手冷却。</summary>
    public void ForceRelease()
    {
        DropGrip(cooldown: true);
        FramesWithoutGrip = 0;
    }

    private void DropGrip(bool cooldown)
    {
        Attached = false;
        HasGrip = false;
        GripCounter = 0;
        GrippingSegments = 0;
        if (cooldown)
        {
            GrabDelay = ReleaseCooldown;
        }
    }

    /// <summary>整体平移（控制器 Shift 的翅膀部分）：位置、插值历史、抓点同步移动。</summary>
    public void Shift(Vector3 delta)
    {
        foreach (WingSegment seg in Segments)
        {
            seg.Pos += delta;
            seg.LastPos += delta;
        }
        GripPos += delta;
    }

    /// <summary>
    /// Flap（≙ VultureTentacle Fly 分支）：phase/amplitude 由控制器统一推进（全翅共相位）；
    /// spanDir = 展向（本肩背离对肩与水平外侧的 50/50 混合，控制器算好传入）；
    /// flapUp = 拍动法向（机体上方向——RW 2D 的 perp 在 3D 里必须显式选定拍动轴）。
    /// 每节目标点 = 沿展向排开 + 法向 sin 行波偏移（翼尖滞后半周期）→ 饱和伺服追。
    /// </summary>
    public void TickFlap(in TickContext ctx, float phase, float amplitude, float sweep,
        Vector3 spanDir, Vector3 spanHorizontal, Vector3 flapUp)
    {
        ModeTicks++;
        FlyingMode = Mathf.Min(1f, FlyingMode + 0.05f);
        if (GrabDelay > 0)
        {
            GrabDelay--;
        }

        DriveRoots(spanDir);

        float idealLength = IdealLength;
        float servo = ServoStrength * Mathf.Lerp(0.2f, 1f, amplitude);
        for (int m = 0; m < Segments.Length; m++)
        {
            WingSegment seg = Segments[m];
            float tPos = (m + 1) / (float)Segments.Length;
            seg.Vel *= FlapDamping;
            seg.Vel += spanHorizontal * SpreadForce;
            // 目标点：展向直翼位形 + 法向行波（≙ VT L377-380；扫掠幅故意远超可达，
            // 伺服长期饱和——有效机制是方向指令+加速度上限，不是真实振幅）。
            Vector3 target = Anchor.Pos + spanDir * (idealLength * tPos)
                + flapUp * (Mathf.Sin(Mathf.Tau * (phase - tPos * 0.5f)) * sweep);
            seg.Vel += (target - seg.Pos).LimitLength(0.75f) / 0.75f * servo;
        }

        RelaxChain(idealLength);
        IntegrateAndCollide(ctx);

        TerrainHitFrames = Mathf.Clamp(TerrainHitFrames + (AnySegmentContact() ? 1 : -1), 0, 30);
    }

    /// <summary>
    /// Grab（≙ VultureTentacle Climb 分支）：找抓点 → 翅尖吸附 → 各节贴附计支撑 →
    /// 悬挂拉体。desiredDir = 期望抓点方向（侧下 + 移动意图混合，控制器算好）；
    /// support = 上 tick 支撑度（阻尼档位）；bodyVel = 锚 chunk 速度（未附着节随体）。
    /// </summary>
    public void TickGrab(in TickContext ctx, Vector3 desiredDir, Vector3 spanDir,
        float support, Vector3 bodyVel)
    {
        ModeTicks++;
        FlyingMode = Mathf.Max(0f, FlyingMode - 0.025f);
        if (GrabDelay > 0)
        {
            GrabDelay--;
        }

        DriveRoots(spanDir);

        float idealLength = IdealLength;
        Vector3 desired = Anchor.Pos + desiredDir * (idealLength * GrabReachFrac);
        if (!Attached && GrabDelay <= 0)
        {
            FindGrab(ctx, desiredDir, desired, idealLength);
        }

        WingSegment tip = Segments[^1];
        if (!Attached && HasGrip && (tip.Pos - GripPos).Length() < TipSnapRadius)
        {
            Attached = true; // ≙ 尖端进入 40px 捕获圈：硬钉（下方吸附段执行）
        }

        float damping = Mathf.Lerp(0.95f, 0.85f, support);
        int gripping = 0;
        for (int m = 0; m < Segments.Length; m++)
        {
            WingSegment seg = Segments[m];
            seg.Vel *= damping;
            if (Attached && seg.TerrainContact)
            {
                // 贴附节（≙ GripTerrain）：强阻尼 + 朝墙里吸——身体靠这些节挂在面上。
                seg.Vel *= GripSegmentDamping;
                seg.Vel -= seg.ContactNormal * GripPressForce;
                gripping++;
            }
            else
            {
                // 未附着节：上浮 + 随体 + 朝目标伸展（尖端弱、中段强，≙ tProps 反直觉取值）。
                seg.Vel += Vector3.Up * FloatLift + bodyVel * BodyVelFollow;
                Vector3 goal = HasGrip ? GripPos : desired;
                float strength = m == Segments.Length - 1 ? GrabServoTip : GrabServoMid;
                seg.Vel += (goal - seg.Pos).LimitLength(0.5f) / 0.5f * strength;
            }
        }
        GrippingSegments = gripping;

        RelaxChain(idealLength);
        IntegrateAndCollide(ctx);

        if (Attached)
        {
            // 翅尖硬钉在抓点（≙ tip.pos = floatGrabDest; vel = 0）。
            tip.Pos = GripPos;
            tip.Vel = Vector3.Zero;
            GripCounter++;
            FramesWithoutGrip = 0;

            // 悬挂拉体（≙ VT L314-338）：身体超出翼长时把锚 chunk 拽向翅尖，
            // Pos/Vel 同步、只拉不推——翅→身体唯一的真实约束。
            float dist = (Anchor.Pos - GripPos).Length();
            if (dist > idealLength && dist > 1e-6f)
            {
                Vector3 corr = (Anchor.Pos - GripPos) / dist * ((idealLength * 0.9f - dist) * HangPullGain);
                Anchor.Pos += corr;
                Anchor.Vel += corr;
            }
        }
        else
        {
            GripCounter = 0;
            FramesWithoutGrip++;
        }
    }

    /// <summary>翼根两节硬驱动（≙ VT L247-250：vel 直接赋值非加力）：沿肩轴向外，
    /// 保证两翼各占一侧、根部不缠绕。3D 化 = 绑定在肩部展向上。</summary>
    private void DriveRoots(Vector3 spanDir)
    {
        Segments[0].Vel = spanDir * RootDrive0;
        if (Segments.Length > 1)
        {
            Segments[1].Vel = spanDir * RootDrive1;
        }
    }

    /// <summary>链绳约束（≙ Tentacle.cs 链式距离约束）：只抗拉伸（stiff=false 的绳语义），
    /// 根↔锚全部修正落在翅节（≙ pullAtConnectionChunk=0 零回传身体），
    /// 节间 50/50 分摊（≙ massDeteriorationPerChunk=0.5），Pos/Vel 同步修正。</summary>
    private void RelaxChain(float idealLength)
    {
        float linkLen = idealLength / Segments.Length;

        WingSegment root = Segments[0];
        Vector3 delta = root.Pos - Anchor.Pos;
        float dist = delta.Length();
        if (dist > linkLen && dist > 1e-6f)
        {
            Vector3 corr = delta / dist * (dist - linkLen);
            root.Pos -= corr;
            root.Vel -= corr;
        }

        for (int m = 1; m < Segments.Length; m++)
        {
            WingSegment prev = Segments[m - 1];
            WingSegment seg = Segments[m];
            delta = seg.Pos - prev.Pos;
            dist = delta.Length();
            if (dist > linkLen && dist > 1e-6f)
            {
                Vector3 corr = delta / dist * ((dist - linkLen) * 0.5f);
                seg.Pos -= corr;
                seg.Vel -= corr;
                prev.Pos += corr;
                prev.Vel += corr;
            }
        }
    }

    /// <summary>逐节积分 + 地形碰撞（运动扫掠射线 + 球体 MTD，同 chunk/Limb 语义）。</summary>
    private void IntegrateAndCollide(in TickContext ctx)
    {
        foreach (WingSegment seg in Segments)
        {
            seg.Vel = seg.Vel.LimitLength(SegmentVelocityCap);
            seg.LastPos = seg.Pos;
            seg.Pos += seg.Vel;
            seg.TerrainContact = false;

            Vector3 motion = seg.Pos - seg.LastPos;
            float motionLen = motion.Length();
            if (motionLen > 1e-6f)
            {
                Vector3 dir = motion / motionLen;
                if (ctx.Terrain.Raycast(seg.LastPos, seg.Pos + dir * seg.Radius, out TerrainHit hit)
                    && SphereTerrain.Resolve(hit, seg.Radius, 0.35f, ref seg.Pos, ref seg.Vel,
                        out Vector3 n))
                {
                    seg.TerrainContact = true;
                    seg.ContactNormal = n;
                }
            }

            if (ctx.Terrain.SpherePenetration(seg.Pos, seg.Radius, out Vector3 pushDir, out float depth))
            {
                seg.Pos += pushDir * depth;
                SphereTerrain.RespondVelocity(pushDir, 0.35f, ref seg.Vel);
                seg.TerrainContact = true;
                seg.ContactNormal = pushDir;
            }
        }
    }

    private bool AnySegmentContact()
    {
        foreach (WingSegment seg in Segments)
        {
            if (seg.TerrainContact)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 找抓点（≙ FindGrabPos 的射线版：格子螺旋搜索 + 格子射线 → 锚点直射 + 投影射线，
    /// 与 Limb.FindGrip 同构）：① 锚点沿期望方向直射——面前的墙/顶面率先够到；
    /// ② 期望点沿水平展向排开的竖直投影射线（栖息面）。统一选「离期望点最近且够得着」。
    /// 天花板底面**允许**（≙ 秃鹫倒挂天花板的抓握——与 Limb 不落悬垂面相反）。
    /// 没找到 → HasGrip 摘掉（语义同 Limb：HasGrip = 本次搜索背书的真实地形点）。
    /// </summary>
    private void FindGrab(in TickContext ctx, Vector3 desiredDir, Vector3 desired,
        float idealLength)
    {
        float maxReach = idealLength * 0.9f;
        Vector3 alongSpan = desiredDir - Vector3.Up * desiredDir.Dot(Vector3.Up);
        alongSpan = alongSpan.LengthSquared() < 1e-8f ? Vector3.Zero : alongSpan.Normalized();

        bool found = false;
        Vector3 best = default;
        Vector3 bestNormal = Vector3.Up;
        float bestDistSq = float.MaxValue;

        void Consider(in TerrainHit hit)
        {
            if (hit.Normal.LengthSquared() < 1e-12f)
            {
                return; // 零法线 = 射线起点已陷入地形
            }
            if ((hit.Point - Anchor.Pos).Length() > maxReach)
            {
                return;
            }
            float distSq = (hit.Point - desired).LengthSquared();
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = hit.Point;
                bestNormal = hit.Normal;
                found = true;
            }
        }

        if (ctx.Terrain.Raycast(Anchor.Pos, Anchor.Pos + desiredDir * (maxReach + 0.2f),
                out TerrainHit direct))
        {
            Consider(direct);
        }
        foreach (float offset in GrabSamples)
        {
            Vector3 probe = desired + alongSpan * offset;
            if (ctx.Terrain.Raycast(probe + Vector3.Up * GrabProbeUp,
                    probe - Vector3.Up * GrabProbeDown, out TerrainHit hit))
            {
                Consider(hit);
            }
        }

        if (found)
        {
            GripPos = best;
            GripNormal = bestNormal;
            HasGrip = true;
        }
        else
        {
            HasGrip = false;
        }
    }
}
