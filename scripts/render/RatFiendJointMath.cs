using Godot;
using ProcAnim.Core.Species.RatFiend;

namespace ProcAnimLab.Render;

/// <summary>
/// 鼠煞肘/膝/肩/髋关节公式的**单一真相源**（命中判定侧的消费入口）。
///
/// ⚠️ **双向同步义务**：本类的全部常量与 <see cref="RatFiendFormalRenderer"/> 内联的同名
/// 公式**必须逐字一致**（肩挂点 0.78/0.35/0.18、臂骨 0.52、三态 pole、髋关节 0.55、
/// 腿 pole 0.25/0.5）——改渲染件的关节公式必须同步这里，反之亦然。腿骨长渲染件已改为
/// 直接调 <see cref="LegBone"/>（R10 起 _legGene 只调粗细不掺骨长），膝位两侧逐字同源。
/// 刻意的确定性近似（命中侧用内核标量，不复制渲染件的化妆状态）：
/// · 忽略 pole/spineUp/dorsal 的逐帧低通——
///   由命中判定的瞄准冗余（LimbAimAssist 0.22m ≫ 低通差）冗余覆盖误差；
/// · SpineUp/Dorsal 直接从内核当 tick 状态推导（无历史、无 RNG），断肢流当 tick 可解。
/// </summary>
internal static class RatFiendJointMath
{
    /// <summary>脊柱上轴：胸→髋轴的瞬时值（渲染件另有低通，见类头注释）。</summary>
    public static Vector3 SpineUp(RatFiendLocomotionController controller)
    {
        Vector3 axis = controller.Chest.Pos - controller.Hips.Pos;
        return axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
    }

    /// <summary>身体右向（渲染件同款：facing × up，退化回退 spineUp × Right）。</summary>
    public static Vector3 Right(Vector3 facing, Vector3 spineUp)
    {
        Vector3 right = facing.Cross(Vector3.Up);
        return right.LengthSquared() > 1e-6f
            ? right.Normalized()
            : spineUp.Cross(Vector3.Right).Normalized();
    }

    /// <summary>背侧法线：站姿（−facing 去脊柱轴分量）与爬行（世界上去脊柱轴分量）按内核
    /// CrawlFactor slerp——渲染件的逐帧低通不复制（无低通，确定性近似）。</summary>
    public static Vector3 Dorsal(Vector3 facing, Vector3 spineUp, float crawlFactor)
    {
        Vector3 dorsalStand = -facing - spineUp * (-facing).Dot(spineUp);
        dorsalStand = dorsalStand.LengthSquared() > 1e-6f ? dorsalStand.Normalized() : -facing;
        Vector3 dorsalCrawl = Vector3.Up - spineUp * Vector3.Up.Dot(spineUp);
        Vector3 crawlDir = dorsalCrawl.LengthSquared() > 1e-6f
            ? dorsalCrawl.Normalized()
            : dorsalStand;
        // 反平行退化守卫（评审修复轮）：脊轴倒置（摔倒翻滚瞬间）且三向量近共面时两源
        // 反平行，Slerp 在共线 cross 下回退 Lerp、weight=0.5 精确返回零向量——回退取站姿源。
        Vector3 blended = dorsalStand.Slerp(crawlDir, Mathf.Clamp(crawlFactor, 0f, 1f));
        return blended.LengthSquared() > 1e-6f ? blended.Normalized() : dorsalStand;
    }

    /// <summary>肩挂点：驼峰前坡（耸肩）——胸心 + 右 0.78 + 脊上 0.35 + 背侧 0.18（×胸半径）。</summary>
    public static Vector3 Shoulder(Vector3 chest, Vector3 right, Vector3 spineUp,
        Vector3 dorsal, float side, float chestRadius)
    {
        return chest
            + right * (side * chestRadius * 0.78f)
            + spineUp * (chestRadius * 0.35f)
            + dorsal * (chestRadius * 0.18f);
    }

    /// <summary>臂骨长（上/下臂等长）。</summary>
    public static float ArmBone(float armLength) => armLength * 0.52f;

    /// <summary>肘 pole 三态混合（垂手肘朝后外 / 前伸肘外压 / 爬行肘朝上），无低通。</summary>
    public static Vector3 ArmPole(Vector3 right, Vector3 spineUp, Vector3 facing,
        float side, float runBlend, float crawlFactor)
    {
        Vector3 poleIdle = (right * (side * 0.55f) + spineUp * 0.35f - facing * 0.60f)
            .Normalized();
        Vector3 poleRun = (right * (side * 0.90f) - spineUp * 0.25f + facing * 0.10f)
            .Normalized();
        Vector3 poleCrawl = (spineUp * 0.85f + right * (side * 0.45f)).Normalized();
        Vector3 blended = poleIdle.Lerp(poleRun, runBlend).Lerp(poleCrawl, crawlFactor);
        return blended.LengthSquared() > 1e-8f ? blended.Normalized() : poleIdle;
    }

    /// <summary>肘 = 两骨 IK（上/下臂等长）。</summary>
    public static Vector3 Elbow(Vector3 shoulder, Vector3 hand, float bone, Vector3 pole) =>
        TwoBoneIk.Solve(shoulder, hand, bone, bone, pole);

    /// <summary>髋侧关节：髋心 + 右 0.55（×髋半径）。</summary>
    public static Vector3 HipJoint(Vector3 hips, Vector3 right, float side, float hipsRadius) =>
        hips + right * (side * hipsRadius * 0.55f);

    /// <summary>腿骨长（渲染件同调此函数，膝位逐字同源）。0.44 × 双骨 ≈ 0.88×JointDist，
    /// 对 HipRideHeight ≈0.83×JointDist 的站高给出站立膝微屈（R10 站姿修复轮；此前 0.60）。</summary>
    public static float LegBone(float jointDist) => jointDist * 0.44f;

    /// <summary>膝 pole：前向 + 侧偏 + 爬行抬升。</summary>
    public static Vector3 LegPole(Vector3 facing, Vector3 right, Vector3 spineUp,
        float side, float crawlFactor)
    {
        Vector3 pole = facing + right * (side * 0.25f) + spineUp * (crawlFactor * 0.5f);
        return pole.LengthSquared() > 1e-8f ? pole.Normalized() : facing;
    }

    /// <summary>膝 = 两骨 IK（上/下腿等长）。</summary>
    public static Vector3 Knee(Vector3 hipJoint, Vector3 foot, float bone, Vector3 pole) =>
        TwoBoneIk.Solve(hipJoint, foot, bone, bone, pole);
}
