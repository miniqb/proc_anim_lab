using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core.Species.Spider;

/// <summary>
/// 蜘蛛身体链的一节。列表顺序就是拓扑顺序；第 0 节没有前驱，所以其连接字段不参与装配。
/// 后续每节只连到前一节，刻意不表达分支或环，保证装配与固定 tick 求解顺序唯一。
/// </summary>
public sealed class BodySegmentSpec
{
    /// <summary>身体球半径（米）。</summary>
    public float Radius = 0.18f;

    /// <summary>身体球质量。当前约束以显式权重求解，质量同时作为宿主观测与后续回迁参数保留。</summary>
    public float Mass = 0.5f;

    /// <summary>本节到前一节的目标距离（米）；第 0 节忽略。</summary>
    public float ConnectionLength = 0.28f;

    /// <summary>距离修正分配给前一节的比例 ∈[0,1]；第 0 节忽略。</summary>
    public float ConnectionWeight = 0.5f;

    /// <summary>刚性距离约束之外、每 tick 施加一次的软恢复系数 ∈[0,1]；第 0 节忽略。</summary>
    public float ConnectionElasticity = 0.2f;
}

/// <summary>
/// 一对左右蜘蛛腿的出生配置。每对显式指定身体锚点，因此同一套控制器既能表达
/// “所有腿在第一节”的 BigSpider 式拓扑，也能表达多节、多锚点的实验体。
/// </summary>
public sealed class LegPairSpec
{
    /// <summary>腿对挂载的 <see cref="SpiderBreedParams.BodySegments"/> 索引。</summary>
    public int AnchorSegmentIndex;

    /// <summary>
    /// 腿根相对锚点的局部偏移：X 沿身体前向、Y 沿支撑法线、Z 沿左右轴。
    /// Z 是腿对中心偏移；左右分离另由 <see cref="RootLateralOffset"/> 给出。
    /// </summary>
    public Vector3 RootOffset;

    /// <summary>左右腿根各自离腿对中心的横向距离（米）。</summary>
    public float RootLateralOffset = 0.08f;

    /// <summary>腿相对身体横轴向前/后扇开的角度（度）；正值朝前、负值朝后。</summary>
    public float FanAngleDegrees;

    /// <summary>左腿的确定性步态相位种子，只允许 0/1，不读取随机数。</summary>
    public int PhaseGroup;

    /// <summary>
    /// true 时右腿使用与左腿相反的相位；相邻腿对继续用交替的 PhaseGroup，
    /// 即得到 L1/R2/L3/R4 与 R1/L2/R3/L4 两组对角四足步态。
    /// false 保留“同一腿对左右同步”的自定义拓扑表达能力。
    /// </summary>
    public bool OpposeSidePhase;

    /// <summary>上腿（Anchor→Knee）长度（米），至少 1e-4。</summary>
    public float UpperLength = 0.28f;

    /// <summary>下腿（Knee→Foot）长度（米），至少 1e-4。</summary>
    public float LowerLength = 0.32f;

    /// <summary>脚端碰撞/显示半径（米）。</summary>
    public float FootRadius = 0.035f;

    /// <summary>脚端每 tick 最大追逐速度（米/tick），至少 0.001。</summary>
    public float HuntSpeed = 0.16f;

    /// <summary>脚端速度向目标速度插值的急促度 ∈(0,1]。</summary>
    public float Quickness = 0.65f;

    /// <summary>命中真实地形后成为稳定抓地所需的连续 tick，必须至少为 1。</summary>
    public int GripDelay = 3;

    /// <summary>射线候选连续未接触其目标面多久后作废并排除旧点重搜。</summary>
    public int AcquisitionTimeoutTicks = 30;

    /// <summary>plant-and-trail 的换步阈值 ∈[0,1]，按最大可达距离计。</summary>
    public float StepLength = 0.62f;

    /// <summary>
    /// 足端落后腿根达到多少最大腿长后主动换步；-1 沿用由 StepLength 推导的旧曲线。
    /// 独立字段让长腿品种能提前抬腿，而不缩短下一步的名义前伸距离。
    /// </summary>
    public float TrailReleaseRatio = -1f;

    /// <summary>
    /// 下一 AEP 在抬腿瞬间相对腿根的局部前向坐标，占最大腿长的比例。
    /// 允许负值：后腿可在身体后向工作区内完成“由后向前”的完整一步，而不必跨到腿根前方。
    /// 仅当 <see cref="UseExplicitTouchdownLead"/> 为 true 时生效。
    /// 足端的横向/支撑向分量仍沿用本腿当前工作区，因此后腿不会被改造成朝前的前腿。
    /// </summary>
    public float TouchdownLeadRatio;

    /// <summary>
    /// 是否冻结由 <see cref="TouchdownLeadRatio"/> 定义的世界 AEP。false 保留旧式
    /// “由扇形名义目标逐 tick 推导”的自定义配置语义。
    /// </summary>
    public bool UseExplicitTouchdownLead;

    /// <summary>
    /// 静态工作区中心额外沿身体前向偏置的无量纲权重；0 保留各腿扇形的自然前后分工。
    /// 正式摆动的 AEP 前缘由 <see cref="TouchdownLeadRatio"/> 独立决定。
    /// </summary>
    public float ForwardStepBias;

    /// <summary>摆动期脚端向腿根收拢程度。</summary>
    public float LiftFeet = 0.2f;

    /// <summary>候选落点沿支撑方向的偏置。</summary>
    public float FeetDown = 0.45f;

    /// <summary>左右期望落点方向的无量纲横向权重（与现有 Limb.PairLateral 同语义）。</summary>
    public float PairLateral = 0.42f;

    /// <summary>落脚射线在两段腿总长以外的少量探测冗余（米），命中后仍会做真实可达性钳制。</summary>
    public float ProbeReach = 0.08f;
}

/// <summary>
/// 蜘蛛出生参数表。身体是至少两节的有序线性链；腿对可显式挂在任意身体节上。
/// 工厂只在出生时读取此表，运行时控制器持有展开后的身体和腿数据，不回读品种配置。
/// </summary>
public sealed class SpiderBreedParams
{
    public string Name = "spider-small";

    /// <summary>按头/胸到腹部/尾端排列的身体节；必须至少有两项。</summary>
    public readonly List<BodySegmentSpec> BodySegments = new();

    /// <summary>显式腿对列表；允许任意节挂任意多对，顺序同时作为稳定的腿槽位顺序。</summary>
    public readonly List<LegPairSpec> LegPairs = new();

    // —— 身体推进与抓地反馈 ——
    /// <summary>全腿抓稳、满输入时每 tick 的推进速度注入。</summary>
    public float BaseSpeed = 0.055f;

    /// <summary>沿表面推进的速度上限（米/tick）。</summary>
    public float MaxMoveSpeed = 0.085f;

    /// <summary>完全失去抓地时仍保留的推进比例。</summary>
    public float NoGripSpeed = 0.08f;

    /// <summary>至少这么多条腿稳定抓地后关闭重力。</summary>
    public int MinGroundedLegs = 2;

    /// <summary>达到抓地门槛持续这么多 tick 后关闭重力。</summary>
    public int RegainFootingTicks = 2;

    /// <summary>低于抓地门槛持续这么多 tick 后恢复重力，避免换步瞬间抖动。</summary>
    public int LoseGripTicks = 6;

    /// <summary>抓地法线低通系数 ∈(0,1]；越小，墙角换面越柔和。</summary>
    public float SupportBlend = 0.22f;

    /// <summary>每个确定性步态相位持续的 tick 数。</summary>
    public int GaitPhaseTicks = 10;

    /// <summary>换步时至少保留的稳定抓地腿数。</summary>
    public int MinPlantedLegs = 2;

    /// <summary>后续身体节追随前一节拖尾点的姿态速度增益。</summary>
    public float TrailingGain = 0.18f;

    /// <summary>推进目标在主身体节前方的默认预看距离（米）。</summary>
    public float LookAhead = 0.48f;

    /// <summary>主身体节离被足端汇总出的支撑面的期望高度（米）。</summary>
    public float RideHeight = 0.2f;

    /// <summary>有移动意图却几乎不动多久后释放最老抓地腿，打破确定性顶死。</summary>
    public int StallReleaseTicks = 18;

    /// <summary>身体每 tick 的刚性距离约束松弛次数。</summary>
    public int ConstraintIterations = 4;
}
