using System;
using Godot;

namespace ProcAnimLab.Render;

/// <summary>
/// 拟态草渲染期颈部姿态（纯静态数学，无引擎依赖——RatFiendJointMath / TwoBoneIk 同款，
/// 无引擎 harness 可直接编译调用）。物理链是只抗拉的绳，末段常从"错误的一侧"进头
/// （探头悬停有余量时链身垂到头下方再翘上来；过冲后猎物在脑后），而嘴 forward 被宿主
/// 光束覆写——嘴便张向自己的脖子。解法照搬蛇/巨蜥：**头颈关节（寰枕）有限转角，
/// 其余转角由多节颈椎吸收**——嘴 forward 分毫不动（指哪看哪优先级更高），只重画
/// 最后三节链的走向：
/// <list type="number">
/// <item>颈入方向 N：从嘴 forward F 朝物理链末方向 C 转，最多 AtlasDegrees（头颈关节
/// 极限）；再往后转只在"三节颈够不着"时发生（闭式可达性解，在圆周上取满足绳长的最小
/// 转角，Atan2 割线两侧一并检验，分支处处连续），且不超过 Ω=angle(F,C)——寰枕/可达
/// 解绝不比物理链更差；唯一的例外是第 3 条的挂点半空间钳（净空优先于头颈角）。</item>
/// <item>弯曲平面向量 G 逐帧续接（同侧化 + 近反平行时向上帧混合，永不在两反向单位向量
/// 之间 lerp）；F 越过 C 的反向、弯曲侧真的换边时解是镜像跳变，由输出侧的偏差限速
/// （4 m/s）滑过去——限速转角会让过渡帧穿过颈够不着的方向，反而更糟。</item>
/// <item>末节钉死沿 N 进枕：P2 = O − N·l3（Catmull-Rom 端点单边切线 = 该节，管子
/// 精确沿 N 进头）；前两节由 TwoBoneIk 从锚点 A（s[n−4]）解到 P2，极向量取物理
/// 中点的偏侧 + 进枕侧偏置 + 上帧极向量低通（防翻侧）；N 与肘都不得钻进安装面。</item>
/// <item>权重 0 逐位回落物理链（枕点由调用方按旧式表达式给出，本类不重算）。</item>
/// </list>
/// 骨长逐帧取自插值后的物理链（突刺 ×1.7 拉伸、回收缩链自动跟随）；StretchMax 允许的
/// 化妆拉伸只在"够不着"的帧动用，弦短到会折成发夹时才等比压缩。
/// </summary>
internal static class TentaclePlantNeckPose
{
    /// <summary>挂点半空间安全边距（×headR）：肘/前枕点离安装面至少这么远。</summary>
    public const float SurfaceMarginHeadR = 0.4f;
    /// <summary>枕点后退量（×headR）= 管体末点在嘴铰点后方的距离（沿用旧值 0.35）。
    /// 调用方按 <c>mouthPos − mouthFwd × (headR × OcciputHeadR)</c> 算出枕点喂入。</summary>
    public const float OcciputHeadR = 0.35f;
    /// <summary>硬拉伸上限（×StretchMax）：越界时把前枕点拉回锚点方向，放弃精确进枕。</summary>
    public const float StretchHardRatio = 1.25f;
    /// <summary>进枕转角上限（度）：骨 2 到 P2 的转折超过它就把 N 朝来向回转（一次修正）。</summary>
    public const float MaxEntryTurnDegrees = 100f;
    /// <summary>肘折叠上限：骨长不超过弦长×此比（对称两骨 → 肘转折 ≤ 110°）。链身有余量时
    /// 锚→前枕弦很短，定长两骨会折成发夹（弯曲半径 ≈ 管半径）；此时让颈骨等比缩短——
    /// 化妆管长损失一点，换圆顺的天鹅颈。够着才拉伸、折叠才压缩，两者不同时发生。</summary>
    public const float FoldCapRatio = 0.87f;
    /// <summary>化妆偏差（肘/前枕点相对物理点）的限速（米/秒）：解的拓扑跳变（换边镜像、
    /// 修正分支切换）在输出侧滑过去，约 0.1 m/tick；正常帧偏差变化远低于此。</summary>
    public const float DeviationSpeedLimit = 4f;
    /// <summary>末节最短长度（×l3）：够不着时先缩短末节而不是折弯它——缩到零会让端点切线退化。</summary>
    public const float TerminalMinFraction = 0.25f;
    /// <summary>末节最长长度（×l3）：只有更长的末节才够得着时允许它伸长一点（余量进末节）。</summary>
    public const float TerminalMaxFraction = 1.5f;
    private const float PoleLambda = 8f;
    // 偏差低通只压单帧数值抖动：解本身处处连续，λ 取大（≈1 tick）避免快速甩头时
    // 画出的前枕点落后于真解、头颈角瞬时超过物理原状。
    private const float DeviationLambda = 40f;
    private const float PoleEntryWeight = 0.35f;
    private const float PolePrevWeight = 0.5f;

    /// <summary>渲染侧私有平滑状态（不进物理与哈希）；Build/Clear 清零。</summary>
    public struct State
    {
        public Vector3 Pole;
        public Vector3 BendDir;
        public Vector3 ElbowDeviation;
        public Vector3 PreDeviation;
        public bool Initialized;
    }

    /// <summary>全部位置取**绘制空间**（插值 + 下沉偏移后的物理点；Occiput = 管体末点画位）。</summary>
    public readonly struct Input
    {
        public readonly Vector3 Anchor;    // A = s[n-4]
        public readonly Vector3 PhysMid;   // s[n-3]
        public readonly Vector3 PhysPre;   // s[n-2]
        public readonly Vector3 PhysHead;  // s[n-1]
        public readonly Vector3 Occiput;   // O = 嘴铰点画位 − forward × OcciputHeadR × headR
        public readonly Vector3 Forward;   // 嘴 forward（只读，不改）
        public readonly Vector3 ChainDir;  // 链末混合方向（光束覆写前）
        public readonly Vector3 Outward;
        public readonly Vector3 MountPoint;
        public readonly float HeadR;
        public readonly float Weight;      // 0 = 逐位物理链
        public readonly float Dt;

        public Input(
            Vector3 anchor, Vector3 physMid, Vector3 physPre, Vector3 physHead,
            Vector3 occiput, Vector3 forward, Vector3 chainDir,
            Vector3 outward, Vector3 mountPoint, float headR, float weight, float dt)
        {
            Anchor = anchor;
            PhysMid = physMid;
            PhysPre = physPre;
            PhysHead = physHead;
            Occiput = occiput;
            Forward = forward;
            ChainDir = chainDir;
            Outward = outward;
            MountPoint = mountPoint;
            HeadR = headR;
            Weight = weight;
            Dt = dt;
        }
    }

    public struct Output
    {
        public Vector3 Elbow;       // 替换 s[n-3]
        public Vector3 PreOcciput;  // 替换 s[n-2]
        public Vector3 Occiput;     // 管体末点（替换 s[n-1]）
        public float OmegaDeg;      // angle(链末方向, 嘴 forward)：物理原状
        public float HeadNeckDeg;   // angle(枕点 − 前枕点, 嘴 forward)：重画后的头颈角
        public float Stretch;       // 前两骨的化妆拉伸倍率（1 = 无）
        public bool Feasible;       // 不拉伸即可满足头颈角上限
    }

    public static Output Solve(in Input i, float atlasDegrees, float stretchMax, ref State st)
    {
        Vector3 F = Safe(i.Forward, i.ChainDir);
        Vector3 C = Safe(i.ChainDir, F);
        float cosOmega = Mathf.Clamp(F.Dot(C), -1f, 1f);
        float omega = MathF.Acos(cosOmega);
        Vector3 O = i.Occiput;

        float l1 = i.PhysMid.DistanceTo(i.Anchor);
        float l2 = i.PhysPre.DistanceTo(i.PhysMid);
        float l3 = i.PhysHead.DistanceTo(i.PhysPre);
        float weight = Mathf.Clamp(i.Weight, 0f, 1f);
        bool degenerate = !(l1 > 1e-3f && l2 > 1e-3f && l3 > 1e-3f) ||
            !Finite(O) || !Finite(i.Anchor) || !Finite(i.PhysMid) || !Finite(i.PhysPre);
        if (weight <= 1e-3f || degenerate)
        {
            // 逐位物理链（沙盒从不喂光束、伏击态权重归零、蜷缩退化帧）：偏差与转角状态
            // 清零，下次接管从零偏差起步；极向量/弯曲面保留作续接种子。
            st.ElbowDeviation = Vector3.Zero;
            st.PreDeviation = Vector3.Zero;
            st.Initialized = st.Initialized && Finite(st.Pole) && st.Pole.LengthSquared() > 0.5f;
            return new Output
            {
                Elbow = i.PhysMid,
                PreOcciput = i.PhysPre,
                Occiput = O,
                OmegaDeg = Mathf.RadToDeg(omega),
                HeadNeckDeg = Mathf.RadToDeg(Angle(O - i.PhysPre, F)),
                Stretch = 1f,
                Feasible = true,
            };
        }

        // 弯曲平面向量 G（F 法平面内、朝链那一侧）逐帧续接：上帧 G 投影到当前 F 法平面；
        // 本帧几何解 rawG 与它反向则取 −rawG（side=−1，弯曲侧真的换边了）；近反平行
        // （Ω→180°）时几何解在法平面内随机打转，再向上帧 G 混合钉住——两者已同侧，
        // lerp 不会过零。
        Vector3 prevG = Vector3.Zero;
        bool hasPrev = false;
        if (st.Initialized)
        {
            prevG = st.BendDir - F * st.BendDir.Dot(F);
            if (prevG.LengthSquared() > 1e-8f)
            {
                prevG = prevG.Normalized();
                hasPrev = true;
            }
        }
        Vector3 rawG = C - F * cosOmega;
        rawG = rawG.LengthSquared() >= 1e-8f
            ? rawG.Normalized()
            : hasPrev ? prevG : Ortho(F);
        float side = hasPrev && rawG.Dot(prevG) < 0f ? -1f : 1f;
        Vector3 G = rawG * side;
        if (hasPrev)
        {
            float anti = Mathf.SmoothStep(Mathf.DegToRad(150f), Mathf.DegToRad(176f), omega);
            if (anti > 0f)
            {
                Vector3 blended = G.Lerp(prevG, anti);
                if (blended.LengthSquared() > 1e-8f)
                {
                    G = blended.Normalized();
                }
            }
        }
        st.BendDir = G;

        // 闭式寰枕解（在朝链的真实弯曲面 G·side 内、无符号 θ ∈ [θlo, Ω]）：满足
        // |O − N(θ)·l3 − A| ≤ R 的最小 θ（N(θ) = F cosθ + G' sinθ；
        // |u − l3 N| ≤ R ⇔ ρ cos(θ − φ) ≥ γ）。
        float thetaLo = MathF.Min(omega, Mathf.DegToRad(Mathf.Clamp(atlasDegrees, 0f, 180f)));
        float reach = (l1 + l2) * MathF.Max(1f, stretchMax);
        Vector3 u = O - i.Anchor;
        float theta = SolveAtlas(
            thetaLo, omega, u.Dot(F), u.Dot(G) * side, u.LengthSquared(), l3, reach,
            out bool feasible);
        // 带符号转角（在续接的 G 帧里）：权重 0 → 物理链方向 side·Ω。
        Vector3 N = Direction(F, G, side * Mathf.Lerp(omega, theta, weight));

        float margin = SurfaceMarginHeadR * i.HeadR;
        N = ClampIntoHalfSpace(N, O, i.MountPoint, i.Outward, margin, l3, G);
        Vector3 P2 = O - N * l3;

        Vector3 pole = PoleDirection(in i, P2, N, G, st.Initialized ? st.Pole : Vector3.Zero);
        if (st.Initialized)
        {
            Vector3 blended = st.Pole.Lerp(pole, 1f - MathF.Exp(-PoleLambda * i.Dt));
            st.Pole = blended.LengthSquared() > 1e-8f ? blended.Normalized() : pole;
        }
        else
        {
            st.Pole = pole;
        }
        st.Initialized = true;

        float stretch;
        Vector3 E = SolveCrook(i.Anchor, O, N, ref P2, l1, l2, l3, stretchMax, st.Pole, out stretch);

        // 进枕转折修正：骨 2 到 P2 的来向与 N 夹角过大时（肘被极向量/可达性拽到
        // 另一侧），把 N 朝来向回转到上限——用几度头颈角换一个不打折的进枕。先过
        // 半空间钳再比较：修正后（含钳）的头颈角不得比当前 N 的更差。
        Vector3 arrive = Safe(P2 - E, N);
        float turn = Angle(arrive, N);
        float maxTurn = Mathf.DegToRad(MaxEntryTurnDegrees);
        if (turn > maxTurn)
        {
            Vector3 rotAxis = arrive.Cross(N);
            if (rotAxis.LengthSquared() > 1e-10f)
            {
                Vector3 rotated = ClampIntoHalfSpace(
                    arrive.Rotated(rotAxis.Normalized(), maxTurn).Normalized(),
                    O, i.MountPoint, i.Outward, margin, l3, G);
                if (Angle(rotated, F) <= Angle(N, F) + 1e-4f)
                {
                    N = rotated;
                    P2 = O - N * l3;
                    E = SolveCrook(i.Anchor, O, N, ref P2, l1, l2, l3, stretchMax, st.Pole, out stretch);
                }
            }
        }

        // 肘的挂点净空（一侧钳）。
        float elbowDepth = (E - i.MountPoint).Dot(i.Outward);
        if (elbowDepth < margin)
        {
            E += i.Outward * (margin - elbowDepth);
        }

        // 权重混合 + 对物理点的偏差限速与低通：物理点本身随插值连续，只平滑化妆偏差
        // （限速兜住解的拓扑跳变，低通压单帧抖动）；权重 0 走上面的逐位分支，这里的
        // 偏差状态从零起步。
        float dt = MathF.Max(0f, i.Dt);
        float k = 1f - MathF.Exp(-DeviationLambda * dt);
        float maxStep = DeviationSpeedLimit * dt;
        st.ElbowDeviation += ((E - i.PhysMid) * weight - st.ElbowDeviation).LimitLength(maxStep) * k;
        st.PreDeviation += ((P2 - i.PhysPre) * weight - st.PreDeviation).LimitLength(maxStep) * k;
        Vector3 elbowOut = i.PhysMid + st.ElbowDeviation;
        Vector3 preOut = i.PhysPre + st.PreDeviation;

        return new Output
        {
            Elbow = elbowOut,
            PreOcciput = preOut,
            Occiput = O,
            OmegaDeg = Mathf.RadToDeg(omega),
            HeadNeckDeg = Mathf.RadToDeg(Angle(O - preOut, F)),
            Stretch = stretch,
            Feasible = feasible,
        };
    }

    /// <summary>无符号寰枕解：θ ∈ [θlo, Ω] 内满足 ρ cos(θ − φ) ≥ γ 的最小值。可行集在
    /// 圆周上是 [φ−α, φ+α] mod 2π，φ = Atan2 ∈ (−π, π]，定义域 ⊂ [0, π]——只有 k=0/1
    /// 两个平移副本可能相交，两个都查（φ 越过割线时结果不变）；不可行时取定义域内
    /// 与 φ 在圆周上最近的端点（cos 差比较，同样跨割线连续）。</summary>
    private static float SolveAtlas(
        float thetaLo, float omega, float uf, float ug, float uLengthSquared,
        float l3, float reach, out bool feasible)
    {
        float gamma = (uLengthSquared + l3 * l3 - reach * reach) / (2f * l3);
        float rho = MathF.Sqrt(uf * uf + ug * ug);
        if (rho < 1e-6f)
        {
            feasible = gamma <= 0f;
            return thetaLo;
        }
        float phi = MathF.Atan2(ug, uf);
        float c = gamma / rho;
        if (c <= -1f)
        {
            feasible = true;
            return thetaLo;
        }
        if (c < 1f)
        {
            float alpha = MathF.Acos(c);
            for (int k = 0; k <= 1; k++)
            {
                float lo = phi - alpha + k * MathF.Tau;
                float hi = phi + alpha + k * MathF.Tau;
                float candidate = MathF.Max(thetaLo, lo);
                if (candidate <= MathF.Min(omega, hi))
                {
                    feasible = true;
                    return candidate;
                }
            }
        }
        feasible = false;
        return NearestOnCircle(phi, thetaLo, omega);
    }

    /// <summary>[lo, hi] ⊂ [0, π] 内使 cos(θ − φ) 最大的 θ：φ 落在区间内取 φ，否则取圆周上
    /// 更近的端点。</summary>
    private static float NearestOnCircle(float phi, float lo, float hi)
    {
        if (phi >= lo && phi <= hi)
        {
            return phi;
        }
        return MathF.Cos(lo - phi) >= MathF.Cos(hi - phi) ? lo : hi;
    }

    /// <summary>前两骨：TwoBoneIk（余弦钳 [0.2,0.98]：不伸直不对折）；够不着时两骨等比
    /// 拉伸到 StretchMax×硬比，再远就沿 N **缩短末节**（进枕方向不变、管长有界；缩到
    /// 最短仍不够就任由两骨再伸——极少）；只有更长的末节才够着时允许末节伸长一点；
    /// 弦短到会折成发夹时等比压缩（FoldCapRatio）。</summary>
    private static Vector3 SolveCrook(
        Vector3 anchor, Vector3 occiput, Vector3 N, ref Vector3 preOcciput,
        float l1, float l2, float l3, float stretchMax, Vector3 pole, out float stretch)
    {
        float span = l1 + l2;
        float hard = span * MathF.Max(1f, stretchMax) * StretchHardRatio;
        Vector3 toPre = preOcciput - anchor;
        float d = toPre.Length();
        if (d > hard)
        {
            // |O − A − N t| ≤ hard 的 t 区间 [t−, t+]：取 ≤ l3 的最长可达末节；区间全在 l3 之上
            // 就伸长到 t−（封顶）；无解取最近点。各分支在边界处连续。
            Vector3 u = occiput - anchor;
            float minLeg = l3 * TerminalMinFraction;
            float un = u.Dot(N);
            float disc = un * un - (u.LengthSquared() - hard * hard);
            float t;
            if (disc < 0f)
            {
                t = Mathf.Clamp(un, minLeg, l3);
            }
            else
            {
                float r = MathF.Sqrt(disc);
                float tMinus = un - r;
                float tPlus = un + r;
                t = tPlus < minLeg
                    ? minLeg
                    : tMinus > l3
                        ? MathF.Min(tMinus, l3 * TerminalMaxFraction)
                        : Mathf.Clamp(tPlus, minLeg, l3);
            }
            preOcciput = occiput - N * t;
            d = preOcciput.DistanceTo(anchor);
        }
        stretch = MathF.Max(1f, d / span);
        float compress = MathF.Min(1f, d * FoldCapRatio / MathF.Max(l1, l2));
        float scale = stretch * compress;
        return TwoBoneIk.Solve(anchor, preOcciput, l1 * scale, l2 * scale, pole);
    }

    /// <summary>极向量（肘弯向哪一侧）：物理中点相对 锚→前枕 轴的偏侧（米，松弛时主导、
    /// 保物理形状）+ 进枕侧偏置（肘须在 −N 一侧，骨 2 才顺着 N 进枕）+ 上帧极向量
    /// （防同轴歧义翻面）；永不弯进安装面。</summary>
    private static Vector3 PoleDirection(
        in Input i, Vector3 preOcciput, Vector3 N, Vector3 fallback, Vector3 prevPole)
    {
        Vector3 axis = Safe(preOcciput - i.Anchor, i.Outward);
        Vector3 rel = i.PhysMid - i.Anchor;
        Vector3 phys = rel - axis * rel.Dot(axis);
        Vector3 entry = -(N - axis * N.Dot(axis));
        Vector3 raw = phys + entry * PoleEntryWeight + prevPole * PolePrevWeight;
        float intoSurface = raw.Dot(i.Outward);
        if (intoSurface < 0f)
        {
            raw -= i.Outward * intoSurface;
        }
        return Safe(raw, fallback);
    }

    /// <summary>把颈入方向 N 钳进挂点半空间：前枕点 O − N·l3 距安装面 ≥ margin
    /// （净空优先于头颈角——这是 θ ≤ Ω 的唯一例外）。</summary>
    private static Vector3 ClampIntoHalfSpace(
        Vector3 N, Vector3 occiput, Vector3 mountPoint, Vector3 outward,
        float margin, float l3, Vector3 perpHint)
    {
        float cMax = Mathf.Clamp(((occiput - mountPoint).Dot(outward) - margin) / l3, -1f, 1f);
        float along = N.Dot(outward);
        if (along <= cMax)
        {
            return N;
        }
        Vector3 perp = N - outward * along;
        if (perp.LengthSquared() < 1e-8f)
        {
            perp = perpHint - outward * perpHint.Dot(outward);
            if (perp.LengthSquared() < 1e-8f)
            {
                perp = Ortho(outward);
            }
        }
        perp = perp.Normalized();
        return (outward * cMax + perp * MathF.Sqrt(MathF.Max(0f, 1f - cMax * cMax))).Normalized();
    }

    private static Vector3 Direction(Vector3 f, Vector3 g, float theta) =>
        Safe(f * MathF.Cos(theta) + g * MathF.Sin(theta), f);

    private static float Angle(Vector3 a, Vector3 b)
    {
        float denominator = a.Length() * b.Length();
        if (denominator < 1e-12f)
        {
            return 0f;
        }
        return MathF.Acos(Mathf.Clamp(a.Dot(b) / denominator, -1f, 1f));
    }

    private static bool Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Vector3 Ortho(Vector3 f)
    {
        Vector3 up = f.Cross(Vector3.Right);
        if (up.LengthSquared() < 1e-8f)
        {
            up = f.Cross(Vector3.Up);
        }
        return up.Normalized();
    }

    private static Vector3 Safe(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 1e-10f)
        {
            return value.Normalized();
        }
        return fallback.LengthSquared() > 1e-10f ? fallback.Normalized() : Vector3.Up;
    }
}
