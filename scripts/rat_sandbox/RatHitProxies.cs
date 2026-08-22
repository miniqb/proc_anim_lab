using Godot;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Species.RatFiend;
using ProcAnimLab.Render;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.RatArena;

/// <summary>命中部位（枪击裁决的宿主枚举；内核只认 RatFiendLimbId）。</summary>
public enum RatBodyPart
{
    Head,
    Torso,
    Arm,
    Leg,
}

/// <summary>单发命中裁决结果：部位 + 侧别（-1 左 / +1 右 / 0 无侧）+ 命中距离 + 是否已断。</summary>
public readonly struct RatHitReport
{
    public readonly RatBodyPart Part;
    public readonly int Side;
    public readonly float Distance;
    public readonly bool SeveredAlready;

    public RatHitReport(RatBodyPart part, int side, float distance, bool severedAlready)
    {
        Part = part;
        Side = side;
        Distance = distance;
        SeveredAlready = severedAlready;
    }
}

/// <summary>
/// 鼠煞枪击命中判定（宿主数学，不建物理形体；全走 <see cref="RayHitMath"/>）。
/// 测试体清单：头球（HeadRadius+headAssist）；躯干三件 = 胸球 + 髋球（chunk 半径
/// +torsoAssist）+ 胸髋胶囊（r=0.16+torsoAssist）；每臂完好 = 肩→肘、肘→手两段胶囊
/// （关节走 <see cref="RatFiendJointMath"/> 单一真相源；臂管视觉半径 ≈0.035，判定半径
/// 0.035+limbAssist）、已断 = 肩→残肢粒子单胶囊；每腿同构（髋关节→膝、膝→脚，
/// 半径 0.05+limbAssist；已断 = 髋关节→残肢粒子）。
/// 裁决 = **最近 t**：每个命中候选记 part/side/t/severedAlready，取 t 最小且 ≤ maxDistance。
/// </summary>
internal static class RatHitProxies
{
    /// <summary>臂管视觉半径（≈ 渲染件 thick 0.030×档位，取管面外缘）。</summary>
    private const float ArmVisualRadius = 0.035f;

    /// <summary>腿管视觉半径（膝鼓部外缘）。</summary>
    private const float LegVisualRadius = 0.05f;

    /// <summary>胸髋胶囊的基础半径（躯干管腰部）。</summary>
    private const float TorsoCapsuleRadius = 0.16f;

    public static RatHitReport? ResolveShot(RatFiendLocomotionController rat,
        Vector3 from, Vector3 dir, float maxDistance,
        float headAssist, float torsoAssist, float limbAssist)
    {
        RatBodyPart bestPart = RatBodyPart.Head;
        int bestSide = 0;
        bool bestSevered = false;
        float bestT = float.PositiveInfinity;

        void Consider(RatBodyPart part, int side, bool severedAlready, bool hit, float t)
        {
            if (hit && t <= maxDistance && t < bestT)
            {
                bestPart = part;
                bestSide = side;
                bestSevered = severedAlready;
                bestT = t;
            }
        }

        // 头球。
        Consider(RatBodyPart.Head, 0, false,
            RayHitMath.RayHitsSphere(from, dir, rat.Head.Pos,
                rat.Head.Radius + headAssist, out float tHead), tHead);

        // 躯干三件：胸球 + 髋球 + 胸髋胶囊。
        Consider(RatBodyPart.Torso, 0, false,
            RayHitMath.RayHitsSphere(from, dir, rat.Chest.Pos,
                rat.Chest.Radius + torsoAssist, out float tChest), tChest);
        Consider(RatBodyPart.Torso, 0, false,
            RayHitMath.RayHitsSphere(from, dir, rat.Hips.Pos,
                rat.Hips.Radius + torsoAssist, out float tHips), tHips);
        Consider(RatBodyPart.Torso, 0, false,
            RayHitMath.RayHitsCapsule(from, dir, rat.Chest.Pos, rat.Hips.Pos,
                TorsoCapsuleRadius + torsoAssist, out float tTorso), tTorso);

        // 关节几何的共享帧（JointMath 确定性近似——无渲染低通，见其类头）。
        Vector3 spineUp = RatFiendJointMath.SpineUp(rat);
        Vector3 facing = rat.Facing;
        Vector3 right = RatFiendJointMath.Right(facing, spineUp);
        Vector3 dorsal = RatFiendJointMath.Dorsal(facing, spineUp, rat.CrawlFactor);
        float runN = rat.RunBlend;
        float crawl = rat.CrawlFactor;

        // 双臂：完好 = 肩→肘→手两段；已断 = 肩→残肢粒子（肘端）单段。
        float armRadius = ArmVisualRadius + limbAssist;
        for (int i = 0; i < rat.Arms.Count; i++)
        {
            RatArm arm = rat.Arms[i];
            Vector3 shoulder = RatFiendJointMath.Shoulder(
                rat.Chest.Pos, right, spineUp, dorsal, arm.Side, rat.Chest.Radius);
            if (arm.Severed)
            {
                Consider(RatBodyPart.Arm, arm.Side, true,
                    RayHitMath.RayHitsCapsule(from, dir, shoulder, arm.Pos,
                        armRadius, out float tStump), tStump);
                continue;
            }
            Vector3 pole = RatFiendJointMath.ArmPole(right, spineUp, facing, arm.Side, runN, crawl);
            Vector3 elbow = RatFiendJointMath.Elbow(shoulder, arm.Pos,
                RatFiendJointMath.ArmBone(arm.ArmLength), pole);
            Consider(RatBodyPart.Arm, arm.Side, false,
                RayHitMath.RayHitsCapsule(from, dir, shoulder, elbow,
                    armRadius, out float tUpper), tUpper);
            Consider(RatBodyPart.Arm, arm.Side, false,
                RayHitMath.RayHitsCapsule(from, dir, elbow, arm.Pos,
                    armRadius, out float tLower), tLower);
        }

        // 双腿：完好 = 髋关节→膝→脚两段；已断 = 髋关节→残肢粒子（膝端）单段。
        float legRadius = LegVisualRadius + limbAssist;
        for (int i = 0; i < rat.Legs.Count; i++)
        {
            Limb leg = rat.Legs[i];
            bool severed = rat.IsSevered(i == 0 ? RatFiendLimbId.LegLeft : RatFiendLimbId.LegRight);
            Vector3 hipJoint = RatFiendJointMath.HipJoint(
                rat.Hips.Pos, right, leg.Side, rat.Hips.Radius);
            if (severed)
            {
                Consider(RatBodyPart.Leg, leg.Side, true,
                    RayHitMath.RayHitsCapsule(from, dir, hipJoint, leg.Pos,
                        legRadius, out float tStump), tStump);
                continue;
            }
            Vector3 pole = RatFiendJointMath.LegPole(facing, right, spineUp, leg.Side, crawl);
            Vector3 knee = RatFiendJointMath.Knee(hipJoint, leg.Pos,
                RatFiendJointMath.LegBone(leg.JointDist), pole);
            Consider(RatBodyPart.Leg, leg.Side, false,
                RayHitMath.RayHitsCapsule(from, dir, hipJoint, knee,
                    legRadius, out float tUpper), tUpper);
            Consider(RatBodyPart.Leg, leg.Side, false,
                RayHitMath.RayHitsCapsule(from, dir, knee, leg.Pos,
                    legRadius, out float tLower), tLower);
        }

        return float.IsPositiveInfinity(bestT)
            ? null
            : new RatHitReport(bestPart, bestSide, bestT, bestSevered);
    }
}
