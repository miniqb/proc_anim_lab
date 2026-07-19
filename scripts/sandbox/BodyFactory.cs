using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

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
        BaseSpeed = 0.07f,
        MaxMoveSpeed = 0.085f,
        NoGripSpeed = 0.05f,
        LimbSize = 1.35f,
        LimbSpeed = 0.10f,
        LimbQuickness = 0.35f,
        LimbGripDelay = 5,
        StepLength = 0.85f,
        LiftFeet = 0.4f,
        FeetDown = 0.8f,
        LegPairDisplacement = 0.7f,
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

    /// <summary>六足（本项目扩展，RW 无对照）：3 节脊柱 3 腿对，抓地冗余高、推进平顺。</summary>
    public static BreedParams Hexapod() => new()
    {
        Name = "hexapod",
        SpineSegments = 3,
        BodyMassFac = 1.3f,
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

    public static BreedParams ByName(string name)
    {
        foreach (BreedParams p in AllBreeds())
        {
            if (p.Name == name)
            {
                return p;
            }
        }
        GD.PushWarning($"[FACTORY] unknown breed '{name}', falling back to default");
        return Default();
    }

    public static Walker CreateWalker(Vector3 origin) => CreateWalker(origin, Default());

    /// <summary>
    /// 按品种参数装配行走体。出生姿态：脊柱竖叠（头在上）、尾巴沿 +X 伸展、
    /// 脚按对角步态错开（步态错开逻辑用 gripCounter 严格比较打破平局，
    /// 完全对称落地会同抬同落，出生错位给它一个确定性的相位种子）。
    /// </summary>
    public static Walker CreateWalker(Vector3 origin, BreedParams p)
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

        var walker = new Walker(body, head, hips)
        {
            SpineLength = linkLen * (spine - 1),
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
                walker.Limbs.Add(limb);
            }
            Limb left = walker.Limbs[^2];
            Limb right = walker.Limbs[^1];
            left.Pair = right;
            right.Pair = left;
        }
        return walker;
    }
}
