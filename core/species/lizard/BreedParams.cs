namespace ProcAnim.Core.Species.Lizard;

/// <summary>
/// 品种参数表（≙ LizardBreedParams 的运动子集）：一个生物的"手感"全部收拢在这一张表上，
/// 工厂读它装配 Body/LizardLocomotionController/Limb，内核运行时不再回读——纯出生配置，零行为分支。
/// 字段名尽量镜像 RW 便于对照调参（反编译 LizardBreeds 各品种的取值差异就是手感差异）；
/// 单位全部本项目制：米 / 米每tick，1 RW tile = 0.5 m。
/// SpineSegments/LegPairs 的任意数量参数化是本项目扩展；RW 常见蜥蜴为四腿，
/// Caramel/SpitLizard 已有 chunk 0/1/2 三锚六腿的拓扑先例。
/// </summary>
public sealed class BreedParams
{
    public string Name = "default";

    // —— 体型（≙ bodyMass / bodySizeFac / bodyLengthFac）——
    /// <summary>脊柱 chunk 数 ≥2（头…中段…髋）。腿对沿脊柱均匀分布锚定。</summary>
    public int SpineSegments = 2;

    /// <summary>chunk 半径缩放（头基准 0.20m / 髋 0.25m，中段插值）。</summary>
    public float BodySizeFac = 1f;

    /// <summary>chunk 质量缩放（头基准 0.4 / 髋 0.6）。当前内核不读质量（约束走显式权重），存备回迁。</summary>
    public float BodyMassFac = 1f;

    /// <summary>脊柱相邻 chunk 连接长度缩放（基准 0.3m/节）。</summary>
    public float BodyLengthFac = 1f;

    /// <summary>弯曲刚度 ∈[0,1]（≙ bodyStiffnes；RW 粉蜥 0.2/绿蜥 0.5/蓝蜥 0）：脊柱每对隔一节
    /// chunk 间的 PushOnly 防折叠支柱，RestLength = 节长×(1+此值)——越大允许的折角越小。
    /// 伸直时隔节距离 = 2 节长，支柱永不触发；只在链条折起来时软推撑开。2 节脊柱无隔节对，无效。</summary>
    public float BodyStiffness = 0.2f;

    // —— 推进（LizardLocomotionController，≙ baseSpeed / noGripSpeed）——
    /// <summary>满抓地满速每 tick 注入速度（≙ baseSpeed；RW 粉蜥 4.1 ↔ 0.06）。</summary>
    public float BaseSpeed = 0.06f;

    /// <summary>引擎极速（米/tick）：沿推进方向已有此速不再注入（贴墙滑升的唯一刹车）。</summary>
    public float MaxMoveSpeed = 0.08f;

    /// <summary>零腿抓地保留的推进比例（≙ noGripSpeed）。</summary>
    public float NoGripSpeed = 0.15f;

    // —— 腿（≙ limbSize / limbSpeed / limbQuickness / limbGripDelay / stepLength / liftFeet / feetDown / legPairDisplacement / smoothenLegMovement）——
    /// <summary>腿对数 ≥1。对 p 锚定脊柱位 round(p·(Spine-1)/(Pairs-1))；单对锚髋。</summary>
    public int LegPairs = 2;

    /// <summary>腿长与脚半径缩放（腿长基准 JointDist 0.55m，脚 0.06m）。</summary>
    public float LimbSize = 1f;

    /// <summary>脚每 tick 最大逼近速度（≙ limbSpeed；RW 粉蜥 5 ↔ 0.15）。</summary>
    public float LimbSpeed = 0.15f;

    /// <summary>脚速度插值急促度 ∈(0,1]（≙ limbQuickness）。</summary>
    public float LimbQuickness = 0.6f;

    /// <summary>判定「抓稳」所需连续 tick（≙ limbGripDelay）。</summary>
    public int LimbGripDelay = 4;

    /// <summary>迈步阈值 ∈[0,1]（≙ stepLength）：越大步幅越大、换步越少。</summary>
    public float StepLength = 0.7f;

    /// <summary>摆动期目标向髋收拢程度（≙ liftFeet，抬脚感）。</summary>
    public float LiftFeet = 0.2f;

    /// <summary>落点方向向下偏置（≙ feetDown，脚更贴地）。</summary>
    public float FeetDown = 0.5f;

    /// <summary>左右腿落点外撇量（≙ legPairDisplacement 的 3D 化，Limb.PairLateral）。</summary>
    public float LegPairDisplacement = 0.45f;

    /// <summary>按 gripCounter 协调多腿错开抬脚（≙ smoothenLegMovement）。</summary>
    public bool SmoothenLegMovement = true;

    // —— 尾巴（≙ tailSegments / tailStiffness(+Decline 的端点式) / tailLengthFactor）——
    /// <summary>尾链节数（0 = 无尾）。</summary>
    public int TailSegments = 6;

    /// <summary>每节长度缩放（基准 0.15m/节；≙ tailLengthFactor）。</summary>
    public float TailLengthFactor = 1f;

    /// <summary>链首约束权重 WeightA（≙ tailStiffness：越大尾根越"硬"、越能带动身体）。</summary>
    public float TailStiffness = 0.35f;

    /// <summary>尾梢约束权重（≙ tailStiffness·(1−tailStiffnessDecline) 的直接端点表达）。</summary>
    public float TailTipStiffness = 0.05f;
}
