using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 测试身体工厂（≙ RW LizardBreeds：品种预设 + 按参数表装配）。
/// 单位约定：1 RW tile (20px) = 0.5 m，即 1px = 0.025 m。
/// 装配规则：脊柱 = SpineSegments 个 chunk 的 Rigid 链（头…中段…髋，出生竖叠头在上）；
/// 腿对沿脊柱均匀分布锚定；尾巴 = 髋后渐细 PullOnly 链（WeightA 沿链递减 ≙ tailStiffnessDecline）。
/// 基准体（缩放因子全 1）= M1 的 slugcat：头 0.20m/髋 0.25m/连接 0.3m。
/// </summary>
public static class BodyFactory
{
    // —— 基准尺寸（缩放因子全 1 时的取值，与 M1~M3 的硬编码完全一致）——
    private const float HeadRadius = 0.20f;
    private const float HipsRadius = 0.25f;
    private const float HeadMass = 0.4f;
    private const float HipsMass = 0.6f;
    private const float SpineLink = 0.3f;
    private const float TailLink = 0.15f;
    private const float FootRadius = 0.06f;
    private const float LegJointDist = 0.55f;
    private const float LegStagger = 0.10f;
    private const float LegSpread = 0.25f;

    // —— 品种预设（手感差异全在参数表上；RW 对照见各注释）——

    /// <summary>基准四腿（≙ 粉蜥系）：M2~M3 全程调教的默认手感，回归基线品种。</summary>
    public static BreedParams Default() => new();

    /// <summary>重装（≙ 绿蜥系）：3 节脊柱大体格、长腿大步幅、腿慢而黏、硬长尾。</summary>
    public static BreedParams Heavy() => new()
    {
        Name = "heavy",
        SpineSegments = 3,
        BodySizeFac = 1.2f,
        BodyMassFac = 3f,
        BodyStiffness = 0.5f,
        BaseSpeed = 0.07f,
        MaxMoveSpeed = 0.085f,
        NoGripSpeed = 0.05f,
        LimbSize = 1.15f,
        LimbSpeed = 0.14f,
        LimbQuickness = 0.5f,
        LimbGripDelay = 4,
        StepLength = 0.7f,
        LiftFeet = 0.25f,
        FeetDown = 0.6f,
        LegPairDisplacement = 0.5f,
        SmoothenLegMovement = false,
        TailSegments = 8,
        TailLengthFactor = 1.1f,
        TailStiffness = 0.45f,
        TailTipStiffness = 0.12f,
    };

    /// <summary>轻捷（≙ 黄蜥系）：小体格快腿、步频高步幅小、短软尾。</summary>
    public static BreedParams Sprinter() => new()
    {
        Name = "sprinter",
        BodySizeFac = 0.85f,
        BodyMassFac = 0.7f,
        BodyLengthFac = 0.9f,
        BaseSpeed = 0.08f,
        MaxMoveSpeed = 0.105f,
        NoGripSpeed = 0.2f,
        LimbSize = 0.9f,
        LimbSpeed = 0.2f,
        LimbQuickness = 0.8f,
        LimbGripDelay = 3,
        StepLength = 0.6f,
        LiftFeet = 0.15f,
        FeetDown = 0.25f,
        LegPairDisplacement = 0.35f,
        TailSegments = 5,
        TailLengthFactor = 0.8f,
        TailStiffness = 0.4f,
        TailTipStiffness = 0.1f,
    };

    /// <summary>六足：3 节脊柱 3 腿对，抓地冗余高、推进平顺；三锚六腿拓扑在 RW
    /// Caramel/SpitLizard 有直接先例，本项目只扩展为 3D 落脚几何与参数化装配。</summary>
    public static BreedParams Hexapod() => new()
    {
        Name = "hexapod",
        SpineSegments = 3,
        BodyMassFac = 1.3f,
        BodyStiffness = 0.3f,
        BaseSpeed = 0.065f,
        MaxMoveSpeed = 0.085f,
        NoGripSpeed = 0.1f,
        LegPairs = 3,
        LimbSize = 0.95f,
        LimbSpeed = 0.16f,
        LegPairDisplacement = 0.5f,
        TailSegments = 8,
    };

    /// <summary>沙盒可切换的品种表（数字键 1~N 与 --breed= 共用此序）。</summary>
    public static BreedParams[] AllBreeds() => new[] { Default(), Heavy(), Sprinter(), Hexapod() };

    /// <summary>按名取预设；未知名回落 default（内核零日志——调用侧如需告警自行比对返回值的 Name）。</summary>
    public static BreedParams ByName(string name)
    {
        foreach (BreedParams p in AllBreeds())
        {
            if (p.Name == name)
            {
                return p;
            }
        }
        return Default();
    }

    public static LizardLocomotionController CreateLizardController(Vector3 origin) =>
        CreateLizardController(origin, Default());

    /// <summary>
    /// 按品种参数装配行走体。出生姿态：脊柱竖叠（头在上）、尾巴沿 +X 伸展、
    /// 脚按对角步态错开（步态错开逻辑用 gripCounter 严格比较打破平局，
    /// 完全对称落地会同抬同落，出生错位给它一个确定性的相位种子）。
    /// </summary>
    public static LizardLocomotionController CreateLizardController(Vector3 origin, BreedParams p)
    {
        var body = new Body();
        int spine = Mathf.Max(2, p.SpineSegments);

        // 脊柱链：spine[0]=头 … spine[^1]=髋，半径/质量沿链在头髋基准间插值（端点不插值保基准）。
        float linkLen = SpineLink * p.BodyLengthFac;
        var chunks = new BodyChunk[spine];
        for (int i = 0; i < spine; i++)
        {
            float f = spine <= 1 ? 0f : i / (float)(spine - 1);
            float radius = (i == 0 ? HeadRadius : i == spine - 1 ? HipsRadius : Mathf.Lerp(HeadRadius, HipsRadius, f))
                * p.BodySizeFac;
            float mass = (i == 0 ? HeadMass : i == spine - 1 ? HipsMass : Mathf.Lerp(HeadMass, HipsMass, f))
                * p.BodyMassFac;
            chunks[i] = new BodyChunk(origin + new Vector3(0f, linkLen * (spine - 1 - i), 0f), radius, mass);
            body.Chunks.Add(chunks[i]);
        }
        for (int i = 0; i < spine - 1; i++)
        {
            body.Connections.Add(new ChunkConnection(chunks[i], chunks[i + 1], linkLen, weightA: 0.5f)
            {
                ConstraintMode = ChunkConnection.Mode.Rigid,
                Elasticity = 0.25f,
            });
        }
        // 防折叠支柱（≙ RW Lizard bodyChunkConnections[2]：头↔第三节 Push-only）：
        // 隔一节的 chunk 间下限距离 = 节长×(1+BodyStiffness)，伸直（2 节长）永不触发，
        // 折叠到低于下限才软推撑开——等效于把最大折角参数化钳制。软推贴 RW 手感
        // （RW elasticity = 1-Lerp(0.9,0.5,stiffness)，绿蜥 0.3），不用硬约束免得转弯发僵。
        for (int i = 0; i + 2 < spine; i++)
        {
            body.Connections.Add(new ChunkConnection(chunks[i], chunks[i + 2],
                linkLen * (1f + p.BodyStiffness), weightA: 0.5f)
            {
                ConstraintMode = ChunkConnection.Mode.PushOnly,
                Elasticity = 1f - Mathf.Lerp(0.9f, 0.5f, p.BodyStiffness),
                SoftOnly = true,
                TerrainCoupled = true,
            });
        }

        BodyChunk head = chunks[0];
        BodyChunk hips = chunks[spine - 1];

        // 尾链：接髋，渐细渐轻，PullOnly（只防拉长）+ WeightA 沿链递减（≙ tailStiffnessDecline）。
        BodyChunk prev = hips;
        float tailLen = TailLink * p.TailLengthFactor;
        for (int i = 0; i < p.TailSegments; i++)
        {
            float f = p.TailSegments <= 1 ? 0f : i / (float)(p.TailSegments - 1);
            var seg = new BodyChunk(prev.Pos + new Vector3(tailLen, 0f, 0f),
                Mathf.Lerp(0.12f, 0.04f, f) * p.BodySizeFac,
                Mathf.Lerp(0.05f, 0.01f, f) * p.BodyMassFac);
            body.Chunks.Add(seg);
            body.Connections.Add(new ChunkConnection(prev, seg, tailLen,
                weightA: Mathf.Lerp(p.TailStiffness, p.TailTipStiffness, f))
            {
                ConstraintMode = ChunkConnection.Mode.PullOnly,
            });
            prev = seg;
        }

        // 朝向钉定（≙ RW Lizard 最终指向 头→髋/中→髋/髋→头；显式赋值仿 RW Deer 构造后
        // 重申指向的先例）：建连接互绑的「后建覆盖」会让髋参照尾根、指向随建链顺序漂——
        // 尾巴是软链，摆动会污染腿的步进方向。全部钉在刚性脊柱上：
        // · 头 → 髋：长基线 = 全身轴前向（≙ RW 头盾/嘴部判定拿头.Rotation 当全身朝向）；
        // · 中段 → 后一节：真·本段轴（3 节脊柱时后一节即髋，与 RW 中→髋逐一对应；
        //   四节以上仍是相邻段——统一指髋会退化成跨关节长弦，不再是「本段朝向」）；
        // · 髋 → 头：Rotation 指向后方，消费侧翻转（LizardLocomotionController.TickLimbs）。尾链保持互绑自然指向。
        head.RotationChunk = hips;
        for (int i = 1; i < spine - 1; i++)
        {
            chunks[i].RotationChunk = chunks[i + 1];
        }
        hips.RotationChunk = head;

        var controller = new LizardLocomotionController(body, head, hips, chunks[1])
        {
            HeadLinkLength = linkLen,
            BaseSpeed = p.BaseSpeed,
            MaxMoveSpeed = p.MaxMoveSpeed,
            NoGripSpeed = p.NoGripSpeed,
            SmoothGait = p.SmoothenLegMovement,
        };

        // 腿对沿脊柱均匀分布：对 p 锚定 spine[round(p·(spine-1)/(pairs-1))]；
        // 相邻对错位相反 → 对角步态相位种子（-X 为出生错位的“靠后”方向）。
        int pairs = Mathf.Max(1, p.LegPairs);
        for (int pair = 0; pair < pairs; pair++)
        {
            BodyChunk anchor = pairs == 1
                ? hips
                : chunks[Mathf.RoundToInt(pair * (spine - 1) / (float)(pairs - 1))];
            foreach (int side in new[] { -1, +1 })
            {
                float stagger = LegStagger * side * (pair % 2 == 0 ? -1f : 1f);
                Vector3 foot = anchor.Pos
                    + new Vector3(-0.15f + stagger, -anchor.Radius, side * LegSpread * p.LimbSize);
                var limb = new Limb(anchor, foot, FootRadius * p.LimbSize, side)
                {
                    JointDist = LegJointDist * p.LimbSize,
                    HuntSpeed = p.LimbSpeed,
                    Quickness = p.LimbQuickness,
                    GripDelay = p.LimbGripDelay,
                    StepLength = p.StepLength,
                    LiftFeet = p.LiftFeet,
                    FeetDown = p.FeetDown,
                    PairLateral = p.LegPairDisplacement,
                };
                controller.Limbs.Add(limb);
            }
            Limb left = controller.Limbs[^2];
            Limb right = controller.Limbs[^1];
            left.Pair = right;
            right.Pair = left;
        }
        return controller;
    }
}
