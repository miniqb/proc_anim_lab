namespace ProcAnim.Core.Species.DropBug;

/// <summary>
/// DropBug（掉落虫）的纯出生配置。全部默认值直接换算自反编译 DropBug.cs / DropBugAI.cs
/// （1px = 0.025m、力与速度单位 = 米/tick），有意偏离原作的字段在注释里写明
/// "原作 X → 本项目 Y，理由"；完整依据见 docs/dropbug_controller.md。
/// 控制器出生时冻结浅拷贝，运行时不回读、不按预设名分支。
/// </summary>
public sealed class DropBugParams
{
    /// <summary>稳定预设 ID（dropbug/original 等）。</summary>
    public string Id = "dropbug/original";

    // —— 三节身体（≙ DropBug 构造：radius 6/8/6px，mass 0.8×0.4/0.4/0.2）——
    public float HeadRadius = 0.15f;
    public float MidRadius = 0.20f;
    public float TailRadius = 0.15f;
    public float HeadMass = 0.32f;
    public float MidMass = 0.32f;
    public float TailMass = 0.16f;

    // —— 连接（≙ bodyChunkConnections：Normal 12px / Normal 14px / Push 8px）——
    public float HeadMidLength = 0.30f;
    public float MidTailLength = 0.35f;
    /// <summary>头↔尾 PushOnly 防对折距离（只防过近、不限制拉开）。</summary>
    public float AntiFoldLength = 0.20f;

    // —— 悬挂态连接静息长度（≙ Update 里 Lerp(12,5)/Lerp(14,2)/Lerp(8,0) 的终点）——
    public float HangHeadMidLength = 0.125f;
    public float HangMidTailLength = 0.05f;
    public float HangAntiFoldLength = 0f;

    // —— 介质（≙ airFriction 0.999 / surfaceFriction 0.4；bounce 0.1 不移植：
    //     Body 共享碰撞语义为无反弹，掉落虫的手感不依赖弹跳）——
    public float AirFriction = 0.999f;
    public float SurfaceFriction = 0.4f;

    // —— 每 tick 恒定自撑力（≙ Update：head += DirVec(tail→head)*0.5px、tail 反向 1px）——
    public float HeadExtension = 0.0125f;
    public float TailExtension = 0.025f;

    // —— 站稳计数（≙ footingCounter：>10 判稳、失支撑 -3/tick、宽限上限 35、悬挂钉 20）——
    public int FootingThreshold = 10;
    public int FootingLossDecay = 3;
    public int FootingGraceCap = 35;
    public int HangFootingPin = 20;
    /// <summary>3D 支撑探针：从身体节沿重力向打 radius+此深度的射线（原作为 AI 图
    /// tile 可达性查询，3D 无格子，用射线等价）。</summary>
    public float FootingProbeDepth = 0.15f;
    /// <summary>可站立面法线门（与 Humanoid IsGroundNormal 同一条线：≥0.5 才算地面）。</summary>
    public float MinGroundDot = 0.5f;

    // —— 站稳时的前后不对称重力（≙ Footing 块：前两节 vel*=0.8 + 全额抵消，
    //     尾节只抵消 Lerp(0.5,1,stuck) 且无阻尼）——
    public float FrontFootingDamping = 0.8f;
    public float TailGravityCancelMin = 0.5f;

    // —— 行进力（≙ MoveTowards：前进 head 4.5px / mid -0.45 / tail -0.2；
    //     倒退 tail 7.5px / mid +0.2 / head -0.45；失稳 ×0.3）——
    public float MoveForceHead = 0.1125f;
    public float MoveForceMidBack = 0.01125f;
    public float MoveForceTailBack = 0.005f;
    public float BackwardForceTail = 0.1875f;
    public float BackwardForceMid = 0.005f;
    public float BackwardForceHead = 0.01125f;
    public float NoFootingMoveFactor = 0.3f;
    /// <summary>3D 追加：连续胡萝卜没有 RW 瞄格中心的天然限速，显式封顶（同蜥蜴
    /// MaxMoveSpeed 论证）。只在站稳且非弹道时钳制。</summary>
    public float MaxMoveSpeed = 0.13f;

    // —— 携带负重（≙ CarryObject → MoveTowards 的 LerpMap(mass,0,4,1,0.2,0.7)）——
    public float CarryMassFull = 4f;
    public float CarryForceFloor = 0.2f;
    public float CarryCurvePower = 0.7f;

    // —— 越障抬升（≙ MoveTowards 中段：headLag 5px、横向 1.3px、mid 0.5px、
    //     上跳 3.2px、头顶探空 20px）——
    public float HopHeadLag = 0.125f;
    public float HopLateralForce = 0.0325f;
    public float HopMidForward = 0.0125f;
    public float HopRise = 0.08f;
    public float HopCeilingProbe = 0.5f;

    // —— 倒退行走（≙ MoveBackwards：原作 walkBackwardsDist 为 0.005/tick 重掷的
    //     0..20 tile（0..10m）随机值 → 确定性常量 3.5m，取原作区间中低段；
    //     stuck 时抑制照抄）——
    public float BackwardsApproachDistance = 3.5f;

    // —— 卡住抖动（≙ stuckShake：LerpAndTick 升 (0.07, 1/70) / 降 (0.07, 0.05)，
    //     幅度 5px，行进力 ×Lerp(1,1.5,shake)；StuckTracker 的历史位置比对
    //     → 确定性等价：30 tick 窗口的中心净位移低于阈值时累计——净位移对抖动
    //     自身的随机游走天然钝感，不会自己扑灭卡住信号）——
    public int StuckWindowTicks = 30;
    public float StuckSpeedThreshold = 0.01f;
    public int StuckRampStart = 40;
    public int StuckRampFull = 120;
    public float StuckShakeAmplitude = 0.125f;
    public float StuckForceBoost = 1.5f;

    // —— 悬挂点判定（原作 ValidCeilingSpot：空 tile + 上方连续 2 实心 + 下方空 +
    //     floorAltitude ≥ 6 tile。3D 等价：法线朝下门 + 实体厚度探针 + 身体净空 +
    //     竖直落差射线；narrowSpace/相机/出口可达性归宿主）——
    /// <summary>悬挂面法线的向下分量下限（3D 取舍：只接受 ≤45° 倾斜的倒悬面，
    /// 原作只有水平天花板；理由见文档）。</summary>
    public float MinCeilingDot = 0.707f;
    /// <summary>实体厚度探针深度（原作 2 tile = 1m → 默认 0.3m：3D collider 厚度
    /// 由关卡作者掌控，0.3 已能排除薄板，可调）。</summary>
    public float SolidProbeDepth = 0.3f;
    /// <summary>锚点正下方沿重力向的最小落差（≙ floorAltitude ≥ 6 tile = 3m）。</summary>
    public float MinDropClearance = 3f;
    /// <summary>沿法线方向的身体净空（原作下方 1 空 tile ≈ 0.5m + 富余）。</summary>
    public float BodyClearance = 0.6f;

    // —— 悬挂进入（≙ SittingInCeiling 块：mid 距锚 40px 内开始、factor +0.025/tick、
    //     三节各自 Lerp 到 tile 中心 ±(-5/9/10)px·f、速率 0.05/0.4/0.5·f、
    //     vel ×(1-f)；approach assist = AI 侧 50px 内 LOS 时 mid.pos += 1px）——
    public float HangEngageDistance = 1.0f;
    public float HangApproachDistance = 1.25f;
    public float HangApproachMin = 0.125f;
    public float HangApproachStep = 0.025f;
    public float HangEnterRate = 0.025f;
    public float HangSurfaceInset = 0.25f;
    public float HangHeadExtra = 0.125f;
    /// <summary>原作 9px=0.225：mid 停在面下 1px、头悬静息长 5px 时头顶恰与面相切，
    /// 定态会与球形碰撞每 tick 推挤（RW 格子碰撞对 6px 球的嵌入不敏感）。
    /// 3D 改 8px=0.20 给头部留出半径余量，悬挂定态零穿透。</summary>
    public float HangMidRise = 0.20f;
    public float HangTailRise = 0.25f;
    public float HangLerpHead = 0.05f;
    public float HangLerpMid = 0.4f;
    public float HangLerpTail = 0.5f;
    /// <summary>mid/tail 停止地形碰撞的 factor 门槛。原作 0.5：tile 碰撞对贴附初期
    /// 目标点已进面内的浅嵌入不敏感；3D 球形碰撞会与吸附伺服在 [目标越面, 0.5) 窗口
    /// 逐 tick 推挤、留 ~1cm 定态残余 → 改 0.05（开始贴附即允许嵌入），收放全程零穿透。</summary>
    public float HangCollisionToggle = 0.05f;

    // —— 俯冲（≙ JumpFromCeiling + Jump + jumping 块：冲量 21/16px、先削速 ×0.5、
    //     方向上扬度 LerpMap(-1..1 → 0.7..1.2, 1.1)、空中转向 1.2/0.4px、
    //     高于目标 250px 且距 350px 内水平修正 3px、触地冷却 20 tick）——
    public float DiveImpulseHead = 0.525f;
    public float DiveImpulseMid = 0.4f;
    public float DiveVelocityCut = 0.5f;
    public float JumpPowerDown = 0.7f;
    public float JumpPowerUp = 1.2f;
    public float JumpPowerExp = 1.1f;
    public float DiveSteerHead = 0.03f;
    public float DiveSteerBack = 0.01f;
    public float DiveHighAbove = 6.25f;
    public float DiveCorrectionRange = 8.75f;
    public float DiveHorizontalCorrection = 0.075f;
    public int AttackCooldownTicks = 20;
    /// <summary>脱悬提前量（≙ AI SitUpdate 的 pos + ClampMagnitude(vel,6px) ×
    /// LerpMap(dist,40,500,0,30,0.8)：按距离最多预判 30 tick）。</summary>
    public float DiveLeadVelocityClamp = 0.15f;
    public float DiveLeadNear = 1f;
    public float DiveLeadFar = 12.5f;
    public float DiveLeadMaxTicks = 30f;
    public float DiveLeadPower = 0.8f;

    // —— 地面蓄力扑击（≙ charging：+1/15 每 tick、head += dir·charging²px、
    //     mid -= dir·4·charging px；可及 = LerpMap(dot(扑向,身体轴), -0.1, 0.8,
    //     0, 300px, 0.4)；目标上方无实心时按距离抬高瞄点 ≤20px；自己头/中上方
    //     无实心时按距离 Slerp 向上 0.05..0.2）——
    public float PounceChargeRate = 1f / 15f;
    public float PounceChargeStart = 0.01f;
    public float PounceHeadForce = 0.025f;
    public float PounceMidBackForce = 0.1f;
    public float PounceReachMax = 7.5f;
    public float ReachDotLow = -0.1f;
    public float ReachDotHigh = 0.8f;
    public float ReachCurvePower = 0.4f;
    public float AimLiftMax = 0.5f;
    public float AimLiftNear = 1f;
    public float AimLiftFar = 5f;
    public float TiltUpNear = 0.05f;
    public float TiltUpFar = 0.2f;
    public float CeilingProbeHeight = 0.5f;

    // —— 表现腿（≙ DropBugGraphics：Limb[2,2] 挂 chunk0、legLength 45px、相位步进
    //     0.25、失稳 dangle 30 tick、可达 1.2×legLength×(1-lift)^0.2；腿不回传力）——
    public int LegPairs = 2;
    public float LegLength = 1.125f;
    public float LegPhaseStep = 0.25f;
    public int LegDangleTicks = 30;
    public float LegReachRatio = 1.2f;
    public float LegReachShrinkPower = 0.2f;
    public float LegIdealScale = 0.85f;
    public float LegFanNearDeg = 40f;
    public float LegFanFarDeg = 160f;
    /// <summary>步频驱动：原作为头位移 ≥2px 的 tick 固定 +0.125 → 本项目改为按位移
    /// 比例（每 0.05m 进 0.125 周期、上限 2 倍），静止严格为 0（任务要求的驱动关系）。</summary>
    public float RunCycleStride = 0.05f;
    public float RunCycleRate = 0.125f;
    public float RunCycleMaxFactor = 2f;
    public float RunCycleDeadband = 0.002f;

    // —— 宿主直喂目标点 ——
    public float MoveTargetArriveRadius = 0.35f;
    public float MoveTargetResumeRadius = 0.7f;

    // —— travelDir（≙ Vector2.Lerp(travelDir, dir, 0.4)、衰减 0.9995 / sitting 0.5）——
    public float TravelDirLerp = 0.4f;
    public float TravelDirDecay = 0.9995f;
    public float TravelDirSitDecay = 0.5f;

    /// <summary>工厂出生冻结浅拷贝；字段全为值类型，不与调用侧共享可变状态。</summary>
    internal DropBugParams Snapshot() => (DropBugParams)MemberwiseClone();
}
