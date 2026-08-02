using System;

namespace ProcAnim.Core.Species.Deer;

/// <summary>
/// 鹿躯干链的一节。第 0 节连到头，其余节连到数组中的前一节；顺序固定为头侧到尾侧。
/// 每节自己的权重让支撑力与推进力可以沿粗躯干不均匀分布。
/// </summary>
public sealed class DeerBodySegmentParams
{
    /// <summary>身体球半径（米）。</summary>
    public float Radius = 0.5f;

    /// <summary>身体球质量。</summary>
    public float Mass = 1f;

    /// <summary>到头或前一躯干节的静息中心距（米）；应小于两球半径之和以形成重叠粗躯干。</summary>
    public float ConnectionLengthToPrevious = 0.42f;

    /// <summary>本节分到的腿支撑力权重；控制器按全躯干权重和归一化。</summary>
    public float SupportWeight = 1f;

    /// <summary>本节分到的推进力权重；控制器按全躯干权重和归一化。</summary>
    public float DriveWeight = 1f;

    /// <summary>本节参与整体姿态恢复与防折力的权重。</summary>
    public float PostureWeight = 1f;

    public DeerBodySegmentParams Clone() => (DeerBodySegmentParams)MemberwiseClone();

    /// <summary>校验一节的有限值和局部范围；连接的重叠关系由整表校验。</summary>
    public DeerBodySegmentParams Validate(string path = "TrunkSegment")
    {
        DeerParamGuard.Positive(Radius, $"{path}.{nameof(Radius)}");
        DeerParamGuard.Positive(Mass, $"{path}.{nameof(Mass)}");
        DeerParamGuard.Positive(ConnectionLengthToPrevious, $"{path}.{nameof(ConnectionLengthToPrevious)}");
        DeerParamGuard.NonNegative(SupportWeight, $"{path}.{nameof(SupportWeight)}");
        DeerParamGuard.NonNegative(DriveWeight, $"{path}.{nameof(DriveWeight)}");
        DeerParamGuard.NonNegative(PostureWeight, $"{path}.{nameof(PostureWeight)}");
        return this;
    }
}

/// <summary>
/// 一条鹿腿的出生槽位。腿是独立的多节粒子链；<see cref="Side"/> 只给出解剖侧，
/// 每条腿仍以自己的前后与向外偏好形成互不相同的 3D 理想落点。
/// </summary>
public sealed class DeerLegSlotParams
{
    /// <summary>腿根所挂载的躯干节索引。</summary>
    public int AnchorTrunkIndex;

    /// <summary>腿对编号：0 为前对，1 为后对。</summary>
    public int PairIndex;

    /// <summary>解剖侧：-1 为左，+1 为右。</summary>
    public int Side = 1;

    /// <summary>腿链粒子段数，包含足端之前的所有腿段。</summary>
    public int SegmentCount = 4;

    /// <summary>每个腿段的碰撞/调试半径（米）。</summary>
    public float SegmentRadius = 0.075f;

    /// <summary>每个腿段的质量。</summary>
    public float SegmentMass = 0.06f;

    /// <summary>腿根到足端的最大可达长度（米）。</summary>
    public float MaxLength = 1.75f;

    /// <summary>
    /// 出生时段链的理想总长（米）。原作 DeerTentacle 构造值与运行期动态最大长度不是同一量。
    /// </summary>
    public float InitialLength = 1.35f;

    /// <summary>相对身体前向的固定偏好 ∈[-1,1]；正值朝头，负值朝尾。</summary>
    public float ForwardSplay;

    /// <summary>沿本腿解剖外侧的固定偏好 ∈[0,1]；控制器再乘 <see cref="Side"/>。</summary>
    public float OutwardSplay = 0.7f;

    /// <summary>足端碰撞与地形净空半径（米）。</summary>
    public float FootRadius = 0.09f;

    /// <summary>松脚后腿段追逐目标的最大速度（米/tick）。</summary>
    public float HuntSpeed = 0.16f;

    /// <summary>腿段速度向期望速度插值的急促度 ∈(0,1]。</summary>
    public float Quickness = 0.55f;

    /// <summary>主动或被动松脚后禁止重新确认落点的 tick 数。</summary>
    public int ReleaseCooldownTicks = 6;

    /// <summary>候选连续命中多少 tick 后才成为踩实落点。</summary>
    public int GripConfirmTicks = 3;

    /// <summary>新候选相对现有落点至少改善的比例 ∈[0,1]，用于抑制略优候选造成的抖动。</summary>
    public float CandidateHysteresisRatio = 0.12f;

    /// <summary>从名义落点继续沿查询方向搜索真实地形的距离（米）。</summary>
    public float ProbeDistance = 0.55f;

    /// <summary>固定 3D 探测扇相对最大腿长的横向展开比例 ∈[0,1]。</summary>
    public float ProbeSpreadRatio = 0.18f;

    /// <summary>腿长达到最大值的此比例后开始反向拖住身体。</summary>
    public float BodyDragStartRatio = 0.82f;

    /// <summary>腿长达到最大值的此比例后强制松脚，必须大于拖拽起点且不超过 1。</summary>
    public float ReachReleaseRatio = 0.98f;

    public DeerLegSlotParams Clone() => (DeerLegSlotParams)MemberwiseClone();

    /// <summary>校验槽位的有限值、tick 上界和无量纲比例。</summary>
    public DeerLegSlotParams Validate(string path = "LegSlot")
    {
        if (AnchorTrunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(path, AnchorTrunkIndex,
                $"{path}.{nameof(AnchorTrunkIndex)} must be non-negative.");
        }
        if (PairIndex is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(path, PairIndex,
                $"{path}.{nameof(PairIndex)} must be 0 (front) or 1 (rear).");
        }
        if (Side is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(path, Side,
                $"{path}.{nameof(Side)} must be -1 or +1.");
        }

        DeerParamGuard.IntegerRange(SegmentCount, 2, 12, $"{path}.{nameof(SegmentCount)}");
        DeerParamGuard.Positive(SegmentRadius, $"{path}.{nameof(SegmentRadius)}");
        DeerParamGuard.Positive(SegmentMass, $"{path}.{nameof(SegmentMass)}");
        DeerParamGuard.Positive(MaxLength, $"{path}.{nameof(MaxLength)}");
        DeerParamGuard.Positive(InitialLength, $"{path}.{nameof(InitialLength)}");
        DeerParamGuard.Range(ForwardSplay, -1f, 1f, $"{path}.{nameof(ForwardSplay)}");
        DeerParamGuard.Unit(OutwardSplay, $"{path}.{nameof(OutwardSplay)}");
        DeerParamGuard.Positive(FootRadius, $"{path}.{nameof(FootRadius)}");
        DeerParamGuard.Positive(HuntSpeed, $"{path}.{nameof(HuntSpeed)}");
        DeerParamGuard.UnitPositive(Quickness, $"{path}.{nameof(Quickness)}");
        DeerParamGuard.IntegerRange(ReleaseCooldownTicks, 0, 600, $"{path}.{nameof(ReleaseCooldownTicks)}");
        DeerParamGuard.IntegerRange(GripConfirmTicks, 1, 120, $"{path}.{nameof(GripConfirmTicks)}");
        DeerParamGuard.Unit(CandidateHysteresisRatio, $"{path}.{nameof(CandidateHysteresisRatio)}");
        DeerParamGuard.Positive(ProbeDistance, $"{path}.{nameof(ProbeDistance)}");
        DeerParamGuard.Unit(ProbeSpreadRatio, $"{path}.{nameof(ProbeSpreadRatio)}");
        DeerParamGuard.UnitPositive(BodyDragStartRatio, $"{path}.{nameof(BodyDragStartRatio)}");
        DeerParamGuard.UnitPositive(ReachReleaseRatio, $"{path}.{nameof(ReachReleaseRatio)}");
        if (BodyDragStartRatio >= ReachReleaseRatio)
        {
            throw new ArgumentException(
                $"{path}.{nameof(BodyDragStartRatio)} must be below {path}.{nameof(ReachReleaseRatio)}.", path);
        }
        if (FootRadius >= MaxLength)
        {
            throw new ArgumentException($"{path}.{nameof(FootRadius)} must be below {path}.{nameof(MaxLength)}.", path);
        }
        if (InitialLength > MaxLength)
        {
            throw new ArgumentException(
                $"{path}.{nameof(InitialLength)} must not exceed {path}.{nameof(MaxLength)}.", path);
        }
        return this;
    }
}

/// <summary>
/// 鹿控制器的纯出生配置。重力始终由身体层施加；这里的支撑参数只描述四条多节腿
/// 如何向身体回传支撑、推进、犹豫、目标高度与防倾覆力，不表达 locomotion 模式枚举。
/// </summary>
public sealed class DeerParams
{
    public string StableId = "deer/custom";

    // —— 头、重叠粗躯干与轻鹿角 ——
    public float HeadRadius = 0.42f;
    public float HeadMass = 0.8f;

    /// <summary>头侧到尾侧的躯干链；正式预设为四节，自定义配置允许 2..12 节。</summary>
    public DeerBodySegmentParams[] TrunkSegments = CreateDefaultTrunk();

    public float AntlerRadius = 0.72f;
    public float AntlerMass = 0.18f;

    /// <summary>头与鹿角球沿其连接轴互相压入的长度（米）。</summary>
    public float AntlerHeadOverlap = 0.24f;

    /// <summary>头相对首节躯干的向上姿态权重；与前向权重共同定义确定性 3D 目标轴。</summary>
    public float HeadPostureUpWeight = 0.85f;

    /// <summary>头相对首节躯干的前向姿态权重。</summary>
    public float HeadPostureForwardWeight = 0.60f;

    /// <summary>鹿角块相对头的向上姿态权重。</summary>
    public float AntlerPostureUpWeight = 1f;

    /// <summary>鹿角块相对头的前向姿态权重；原作多项受力合并后约为 0.18。</summary>
    public float AntlerPostureForwardWeight = 0.18f;

    /// <summary>鹿角目标轴的速度伺服增益；独立于粗躯干的较软姿态增益。</summary>
    public float AntlerPostureStrength = 0.55f;

    /// <summary>身体距离约束每 tick 的固定松弛次数。</summary>
    public int ConstraintIterations = 6;

    // —— 四条独立多节腿 ——
    public DeerLegSlotParams[] LegSlots = CreateDefaultLegSlots();

    /// <summary>理想落点中世界/支撑系“向下”分量的权重。</summary>
    public float FootTargetDownWeight = 1f;

    /// <summary>理想落点中移动方向分量的权重。</summary>
    public float FootTargetMoveWeight = 0.65f;

    /// <summary>理想落点中每腿固定外撇偏好的权重。</summary>
    public float FootTargetSplayWeight = 0.55f;

    /// <summary>理想落点占该腿最大可达长度的比例。</summary>
    public float IdealFootReachRatio = 0.72f;

    // —— 支撑与推进；均为连续量，不关闭重力 ——
    /// <summary>四腿满贡献时用于抵消重力并托起身体的最大速度注入（米/tick）。</summary>
    public float MaxSupportImpulse = 0.035f;

    /// <summary>腿越接近支撑法线方向，贡献增长越陡的指数。</summary>
    public float SupportStraightnessExponent = 2.2f;

    /// <summary>身体另一侧也有踩实腿时，单腿支撑的额外倍率。</summary>
    public float OppositeSideSupportBonus = 0.7f;

    /// <summary>支撑量沿 tick 低通的系数 ∈(0,1]。</summary>
    public float SupportBlend = 0.28f;

    /// <summary>把连续腿支撑映射为重力补偿的增益；零支撑仍严格产生零升力。</summary>
    public float SupportLiftGain = 1.75f;

    /// <summary>
    /// 腿支撑聚合后的强响应指数。原作最后使用 0.1 次方，把任意可靠非零支撑迅速抬高；
    /// 3D 连续法线保留稍软的指数，避免单脚擦边也近似满升力。
    /// </summary>
    public float SupportResponseExponent = 0.18f;

    /// <summary>满输入、满抓地与满支撑时的前向速度注入（米/tick）。</summary>
    public float BaseDrive = 0.024f;

    /// <summary>身体沿支撑面推进的速度上限（米/tick）。</summary>
    public float MaxMoveSpeed = 0.095f;

    /// <summary>推进强度中来自踩实腿数量的混合权重。</summary>
    public float GripCountDriveWeight = 0.45f;

    /// <summary>推进强度中来自总支撑量的混合权重。</summary>
    public float SupportDriveWeight = 0.55f;

    /// <summary>前方没有踩实腿时每 tick 增长的犹豫量。</summary>
    public float HesitationRisePerTick = 0.08f;

    /// <summary>前方重新有踩实腿时每 tick 消退的犹豫量。</summary>
    public float HesitationFallPerTick = 0.12f;

    /// <summary>完全犹豫时仍保留的推进比例。</summary>
    public float HesitationMinimumDrive = 0.12f;

    // —— plant-and-trail 松脚评分与确定性顶死恢复 ——
    /// <summary>踩实腿数大于此值时，控制器可以主动选择一条腿松开。</summary>
    public int ReleaseWhenPlantedAbove = 3;

    /// <summary>
    /// 腿已接近可及极限、必须在完整落脚窗口前预换步时使用的较低踩实腿数门。
    /// 仍受同对互锁、即时/剩余支撑量和最低踩实腿数约束，不是物理失效的旁路。
    /// </summary>
    public int ReachGuardReleaseWhenPlantedAbove = 2;

    /// <summary>无论评分如何都必须保留的踩实腿数；同一对双悬空另有硬约束。</summary>
    public int MinimumPlantedLegs = 2;

    public float ReleaseDistanceScoreWeight = 1f;
    public float ReleaseBehindScoreWeight = 0.8f;
    public float ReleaseAgeScoreWeight = 0.18f;

    /// <summary>支撑贡献越高越不应松脚，因此该项从总评分中扣除。</summary>
    public float ReleaseSupportPenaltyWeight = 0.65f;

    /// <summary>主动松脚前四腿即时支撑贡献之和的下限；物理失效松脚不受此门限制。</summary>
    public float MinimumRawSupportForRelease = 1.1f;

    public float ReleaseScoreThreshold = 0.5f;

    /// <summary>有输入却几乎不动多久后允许确定性地释放最高评分腿。</summary>
    public int StallReleaseTicks = 32;

    // —— 地形高度、休息和地形接缝 ——
    /// <summary>
    /// 活动时身体质量中心相对支撑面的 3D 目标高度（米）。这是本项目的连续伺服量，
    /// 不是原作名字相近、只用于腿长计算的 preferredHeight。
    /// </summary>
    public float PreferredBodyHeight = 1.28f;

    /// <summary>静止时的目标高度占行进高度的比例，连续低通过渡而非模式切换。</summary>
    public float RestHeightRatio = 0.78f;

    /// <summary>完全休息时的腿可达上限占活动上限的比例；原作为 1/3。</summary>
    public float RestLegReachRatio = 1f / 3f;

    /// <summary>活动→休息缩腿曲线的指数；原作为 5。</summary>
    public float RestLegReachExponent = 5f;

    /// <summary>
    /// 普通无输入连续多久后才开始降低；到达宿主直喂目标时可立即进入同一连续过程。
    /// 这替代原作 AI 的 stayStill/目的地资格，不是 locomotion 模式。
    /// </summary>
    public int RestDelayTicks = 160;

    public float HeightFollowGain = 0.09f;
    public float HeightDamping = 0.38f;
    public float HeightBlend = 0.16f;

    /// <summary>从身体向支撑系“下方”查找可站立表面的最大距离（米）。</summary>
    public float GroundProbeDistance = 2.4f;

    /// <summary>沿移动方向预看前方地形高度的距离（米）。</summary>
    public float ForwardHeightProbeDistance = 1.15f;

    /// <summary>足端与身体地形查询使用的额外净空（米）。</summary>
    public float TerrainClearance = 0.025f;

    /// <summary>移动输入低于该值时累计静止资格；达到休息延迟后才连续向休息高度收敛。</summary>
    public float RestIntentThreshold = 0.04f;

    /// <summary>移动方向输入小于该值视为零。</summary>
    public float MoveIntentDeadzone = 0.05f;

    // —— 整体姿态、防对折与翻滚限制 ——
    public float PostureStrength = 0.12f;
    public float PostureDamping = 0.3f;
    public float AntiFoldStrength = 0.2f;

    /// <summary>隔节中心距低于其展开跨度的此比例时开始施加防折相互作用。</summary>
    public float AntiFoldSpanRatio = 0.58f;

    /// <summary>
    /// 质量中心相对左右支点中心的最大等价侧倾角（度）。球链没有轴向自转自由度；
    /// 超过此门且 COM 横向越出真实支点边界时，平衡恢复力连续增强。
    /// </summary>
    public float MaxLeanDegrees = 48f;

    public float BalanceRecoveryStrength = 0.11f;
    public float BalanceDamping = 0.32f;

    /// <summary>可作为腿支撑面的最大坡角（度，0=水平，90=竖直）。</summary>
    public float MaxStandableSlopeDegrees = 52f;

    /// <summary>真实踩实腿法线汇总的每 tick 低通系数。</summary>
    public float SupportNormalBlend = 0.2f;

    /// <summary>宿主直喂目标点进入此半径即报告到达（米）。</summary>
    public float ArriveRadius = 0.42f;

    /// <summary>返回独立出生快照；躯干节和腿槽数组及其中对象全部深拷贝。</summary>
    public DeerParams Clone()
    {
        if (TrunkSegments is null)
        {
            throw new InvalidOperationException($"{nameof(TrunkSegments)} cannot be null when cloning.");
        }
        if (LegSlots is null)
        {
            throw new InvalidOperationException($"{nameof(LegSlots)} cannot be null when cloning.");
        }

        var clone = (DeerParams)MemberwiseClone();
        clone.TrunkSegments = new DeerBodySegmentParams[TrunkSegments.Length];
        for (int i = 0; i < TrunkSegments.Length; i++)
        {
            clone.TrunkSegments[i] = TrunkSegments[i]?.Clone()
                ?? throw new InvalidOperationException($"{nameof(TrunkSegments)}[{i}] cannot be null when cloning.");
        }

        clone.LegSlots = new DeerLegSlotParams[LegSlots.Length];
        for (int i = 0; i < LegSlots.Length; i++)
        {
            clone.LegSlots[i] = LegSlots[i]?.Clone()
                ?? throw new InvalidOperationException($"{nameof(LegSlots)}[{i}] cannot be null when cloning.");
        }
        return clone;
    }

    /// <summary>校验完整拓扑、所有有限值、tick 上界和比例范围；失败时异常包含字段路径。</summary>
    public DeerParams Validate()
    {
        if (string.IsNullOrWhiteSpace(StableId))
        {
            throw new ArgumentException($"{nameof(StableId)} must not be empty.", nameof(StableId));
        }

        DeerParamGuard.Positive(HeadRadius, nameof(HeadRadius));
        DeerParamGuard.Positive(HeadMass, nameof(HeadMass));
        DeerParamGuard.Positive(AntlerRadius, nameof(AntlerRadius));
        DeerParamGuard.Positive(AntlerMass, nameof(AntlerMass));
        DeerParamGuard.NonNegative(AntlerHeadOverlap, nameof(AntlerHeadOverlap));
        if (AntlerHeadOverlap >= HeadRadius + AntlerRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(AntlerHeadOverlap), AntlerHeadOverlap,
                $"{nameof(AntlerHeadOverlap)} must be below the sum of head and antler radii.");
        }
        DeerParamGuard.NonNegative(HeadPostureUpWeight, nameof(HeadPostureUpWeight));
        DeerParamGuard.NonNegative(HeadPostureForwardWeight, nameof(HeadPostureForwardWeight));
        DeerParamGuard.NonNegative(AntlerPostureUpWeight, nameof(AntlerPostureUpWeight));
        DeerParamGuard.NonNegative(AntlerPostureForwardWeight, nameof(AntlerPostureForwardWeight));
        DeerParamGuard.UnitPositive(AntlerPostureStrength, nameof(AntlerPostureStrength));
        if (HeadPostureUpWeight + HeadPostureForwardWeight <= 0f)
        {
            throw new ArgumentException("Head posture axis weights cannot both be zero.");
        }
        if (AntlerPostureUpWeight + AntlerPostureForwardWeight <= 0f)
        {
            throw new ArgumentException("Antler posture axis weights cannot both be zero.");
        }
        DeerParamGuard.IntegerRange(ConstraintIterations, 1, 32, nameof(ConstraintIterations));

        if (TrunkSegments is null)
        {
            throw new ArgumentNullException(nameof(TrunkSegments));
        }
        DeerParamGuard.IntegerRange(TrunkSegments.Length, 2, 12, $"{nameof(TrunkSegments)}.Length");
        float previousRadius = HeadRadius;
        float supportWeightSum = 0f;
        float driveWeightSum = 0f;
        float postureWeightSum = 0f;
        for (int i = 0; i < TrunkSegments.Length; i++)
        {
            DeerBodySegmentParams segment = TrunkSegments[i]
                ?? throw new ArgumentNullException($"{nameof(TrunkSegments)}[{i}]");
            string path = $"{nameof(TrunkSegments)}[{i}]";
            segment.Validate(path);
            if (segment.ConnectionLengthToPrevious >= previousRadius + segment.Radius)
            {
                throw new ArgumentOutOfRangeException(path, segment.ConnectionLengthToPrevious,
                    $"{path}.{nameof(DeerBodySegmentParams.ConnectionLengthToPrevious)} must keep adjacent body spheres overlapping.");
            }
            previousRadius = segment.Radius;
            supportWeightSum += segment.SupportWeight;
            driveWeightSum += segment.DriveWeight;
            postureWeightSum += segment.PostureWeight;
        }
        DeerParamGuard.Positive(supportWeightSum, "sum of trunk support weights");
        DeerParamGuard.Positive(driveWeightSum, "sum of trunk drive weights");
        DeerParamGuard.Positive(postureWeightSum, "sum of trunk posture weights");

        ValidateLegTopology();

        DeerParamGuard.NonNegative(FootTargetDownWeight, nameof(FootTargetDownWeight));
        DeerParamGuard.NonNegative(FootTargetMoveWeight, nameof(FootTargetMoveWeight));
        DeerParamGuard.NonNegative(FootTargetSplayWeight, nameof(FootTargetSplayWeight));
        DeerParamGuard.UnitPositive(IdealFootReachRatio, nameof(IdealFootReachRatio));
        if (FootTargetDownWeight + FootTargetMoveWeight + FootTargetSplayWeight <= 0f)
        {
            throw new ArgumentException("At least one foot-target weight must be positive.");
        }

        DeerParamGuard.Positive(MaxSupportImpulse, nameof(MaxSupportImpulse));
        DeerParamGuard.Range(SupportStraightnessExponent, 0.1f, 8f, nameof(SupportStraightnessExponent));
        DeerParamGuard.Range(OppositeSideSupportBonus, 0f, 4f, nameof(OppositeSideSupportBonus));
        DeerParamGuard.UnitPositive(SupportBlend, nameof(SupportBlend));
        DeerParamGuard.Range(SupportLiftGain, 1f, 4f, nameof(SupportLiftGain));
        DeerParamGuard.Range(SupportResponseExponent, 0.05f, 1f, nameof(SupportResponseExponent));
        DeerParamGuard.NonNegative(BaseDrive, nameof(BaseDrive));
        DeerParamGuard.Positive(MaxMoveSpeed, nameof(MaxMoveSpeed));
        DeerParamGuard.Unit(GripCountDriveWeight, nameof(GripCountDriveWeight));
        DeerParamGuard.Unit(SupportDriveWeight, nameof(SupportDriveWeight));
        if (GripCountDriveWeight + SupportDriveWeight <= 0f)
        {
            throw new ArgumentException("At least one drive contribution weight must be positive.");
        }
        DeerParamGuard.UnitPositive(HesitationRisePerTick, nameof(HesitationRisePerTick));
        DeerParamGuard.UnitPositive(HesitationFallPerTick, nameof(HesitationFallPerTick));
        DeerParamGuard.Unit(HesitationMinimumDrive, nameof(HesitationMinimumDrive));

        DeerParamGuard.IntegerRange(ReleaseWhenPlantedAbove, 2, 3, nameof(ReleaseWhenPlantedAbove));
        DeerParamGuard.IntegerRange(ReachGuardReleaseWhenPlantedAbove, 1, 3,
            nameof(ReachGuardReleaseWhenPlantedAbove));
        DeerParamGuard.IntegerRange(MinimumPlantedLegs, 1, 3, nameof(MinimumPlantedLegs));
        if (MinimumPlantedLegs > ReleaseWhenPlantedAbove)
        {
            throw new ArgumentException($"{nameof(MinimumPlantedLegs)} cannot exceed {nameof(ReleaseWhenPlantedAbove)}.");
        }
        if (MinimumPlantedLegs > ReachGuardReleaseWhenPlantedAbove)
        {
            throw new ArgumentException(
                $"{nameof(MinimumPlantedLegs)} cannot exceed {nameof(ReachGuardReleaseWhenPlantedAbove)}.");
        }
        DeerParamGuard.ScoreWeight(ReleaseDistanceScoreWeight, nameof(ReleaseDistanceScoreWeight));
        DeerParamGuard.ScoreWeight(ReleaseBehindScoreWeight, nameof(ReleaseBehindScoreWeight));
        DeerParamGuard.ScoreWeight(ReleaseAgeScoreWeight, nameof(ReleaseAgeScoreWeight));
        DeerParamGuard.ScoreWeight(ReleaseSupportPenaltyWeight, nameof(ReleaseSupportPenaltyWeight));
        DeerParamGuard.Range(MinimumRawSupportForRelease, 0f, 4f, nameof(MinimumRawSupportForRelease));
        DeerParamGuard.Range(ReleaseScoreThreshold, 0f, 10f, nameof(ReleaseScoreThreshold));
        DeerParamGuard.IntegerRange(StallReleaseTicks, 1, 600, nameof(StallReleaseTicks));

        DeerParamGuard.Positive(PreferredBodyHeight, nameof(PreferredBodyHeight));
        DeerParamGuard.UnitPositive(RestHeightRatio, nameof(RestHeightRatio));
        DeerParamGuard.UnitPositive(RestLegReachRatio, nameof(RestLegReachRatio));
        DeerParamGuard.Range(RestLegReachExponent, 1f, 12f, nameof(RestLegReachExponent));
        DeerParamGuard.IntegerRange(RestDelayTicks, 0, 36000, nameof(RestDelayTicks));
        DeerParamGuard.UnitPositive(HeightFollowGain, nameof(HeightFollowGain));
        DeerParamGuard.Unit(HeightDamping, nameof(HeightDamping));
        DeerParamGuard.UnitPositive(HeightBlend, nameof(HeightBlend));
        DeerParamGuard.Positive(GroundProbeDistance, nameof(GroundProbeDistance));
        DeerParamGuard.Positive(ForwardHeightProbeDistance, nameof(ForwardHeightProbeDistance));
        DeerParamGuard.NonNegative(TerrainClearance, nameof(TerrainClearance));
        DeerParamGuard.Unit(RestIntentThreshold, nameof(RestIntentThreshold));
        DeerParamGuard.Unit(MoveIntentDeadzone, nameof(MoveIntentDeadzone));
        if (RestIntentThreshold > MoveIntentDeadzone)
        {
            throw new ArgumentException($"{nameof(RestIntentThreshold)} cannot exceed {nameof(MoveIntentDeadzone)}.");
        }

        DeerParamGuard.Unit(PostureStrength, nameof(PostureStrength));
        DeerParamGuard.Unit(PostureDamping, nameof(PostureDamping));
        DeerParamGuard.Unit(AntiFoldStrength, nameof(AntiFoldStrength));
        DeerParamGuard.UnitPositive(AntiFoldSpanRatio, nameof(AntiFoldSpanRatio));
        DeerParamGuard.Range(MaxLeanDegrees, 1f, 89f, nameof(MaxLeanDegrees));
        DeerParamGuard.Unit(BalanceRecoveryStrength, nameof(BalanceRecoveryStrength));
        DeerParamGuard.Unit(BalanceDamping, nameof(BalanceDamping));
        DeerParamGuard.Range(MaxStandableSlopeDegrees, 1f, 89f, nameof(MaxStandableSlopeDegrees));
        DeerParamGuard.UnitPositive(SupportNormalBlend, nameof(SupportNormalBlend));
        DeerParamGuard.Positive(ArriveRadius, nameof(ArriveRadius));
        return this;
    }

    private void ValidateLegTopology()
    {
        if (LegSlots is null)
        {
            throw new ArgumentNullException(nameof(LegSlots));
        }
        if (LegSlots.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(LegSlots), LegSlots.Length,
                "A deer requires exactly four leg slots.");
        }

        var seenSide = new bool[2, 2];
        int[] pairAnchors = { -1, -1 };
        int[] pairCounts = new int[2];
        for (int i = 0; i < LegSlots.Length; i++)
        {
            DeerLegSlotParams slot = LegSlots[i]
                ?? throw new ArgumentNullException($"{nameof(LegSlots)}[{i}]");
            string path = $"{nameof(LegSlots)}[{i}]";
            slot.Validate(path);
            if ((uint)slot.AnchorTrunkIndex >= (uint)TrunkSegments.Length)
            {
                throw new ArgumentOutOfRangeException(path, slot.AnchorTrunkIndex,
                    $"{path}.{nameof(DeerLegSlotParams.AnchorTrunkIndex)} is outside {nameof(TrunkSegments)}.");
            }

            int sideIndex = slot.Side < 0 ? 0 : 1;
            if (seenSide[slot.PairIndex, sideIndex])
            {
                throw new ArgumentException($"Pair {slot.PairIndex} contains duplicate side {slot.Side}.", path);
            }
            seenSide[slot.PairIndex, sideIndex] = true;
            pairCounts[slot.PairIndex]++;
            if (pairAnchors[slot.PairIndex] < 0)
            {
                pairAnchors[slot.PairIndex] = slot.AnchorTrunkIndex;
            }
            else if (pairAnchors[slot.PairIndex] != slot.AnchorTrunkIndex)
            {
                throw new ArgumentException($"Both legs in pair {slot.PairIndex} must share one trunk anchor.", path);
            }
        }

        for (int pair = 0; pair < 2; pair++)
        {
            if (pairCounts[pair] != 2 || !seenSide[pair, 0] || !seenSide[pair, 1])
            {
                throw new ArgumentException($"Leg pair {pair} must contain exactly one left and one right leg.", nameof(LegSlots));
            }
        }
        if (pairAnchors[0] > pairAnchors[1])
        {
            throw new ArgumentException("Front leg pair must not be anchored behind the rear leg pair.", nameof(LegSlots));
        }
    }

    private static DeerBodySegmentParams[] CreateDefaultTrunk() =>
    [
        new() { Radius = 0.46f, Mass = 0.85f, ConnectionLengthToPrevious = 0.40f, SupportWeight = 1.20f, DriveWeight = 1.35f, PostureWeight = 0.90f },
        new() { Radius = 0.56f, Mass = 1.35f, ConnectionLengthToPrevious = 0.38f, SupportWeight = 1.40f, DriveWeight = 1.15f, PostureWeight = 1.20f },
        new() { Radius = 0.62f, Mass = 1.65f, ConnectionLengthToPrevious = 0.42f, SupportWeight = 1.05f, DriveWeight = 0.80f, PostureWeight = 1.35f },
        new() { Radius = 0.50f, Mass = 0.95f, ConnectionLengthToPrevious = 0.40f, SupportWeight = 0.65f, DriveWeight = 0.40f, PostureWeight = 1.00f },
    ];

    private static DeerLegSlotParams[] CreateDefaultLegSlots() =>
    [
        new() { AnchorTrunkIndex = 0, PairIndex = 0, Side = -1, ForwardSplay = 0.42f, OutwardSplay = 0.78f },
        new() { AnchorTrunkIndex = 0, PairIndex = 0, Side = 1, ForwardSplay = 0.34f, OutwardSplay = 0.72f },
        new() { AnchorTrunkIndex = 1, PairIndex = 1, Side = -1, ForwardSplay = -0.28f, OutwardSplay = 0.66f },
        new() { AnchorTrunkIndex = 1, PairIndex = 1, Side = 1, ForwardSplay = -0.38f, OutwardSplay = 0.74f },
    ];
}

internal static class DeerParamGuard
{
    internal static void Positive(float value, string path)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and greater than zero.");
        }
    }

    internal static void NonNegative(float value, string path)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and non-negative.");
        }
    }

    internal static void Unit(float value, string path) => Range(value, 0f, 1f, path);

    internal static void UnitPositive(float value, string path)
    {
        if (!float.IsFinite(value) || value <= 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be finite and in (0, 1].");
        }
    }

    internal static void ScoreWeight(float value, string path) => Range(value, 0f, 10f, path);

    internal static void Range(float value, float minimum, float maximum, string path)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(path, value,
                $"{path} must be finite and in [{minimum}, {maximum}].");
        }
    }

    internal static void IntegerRange(int value, int minimum, int maximum, string path)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(path, value, $"{path} must be in [{minimum}, {maximum}].");
        }
    }
}
