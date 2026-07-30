namespace ProcAnim.Core;

/// <summary>
/// 秃鹫品种参数表（≙ RW Vulture 构造硬编码 + IsKing/IsMiros 分支的参数化收拢）。
/// 与 <see cref="BreedParams"/>（蜥蜴表）平行——不往那张表塞飞行字段：它镜像
/// LizardBreedParams，塞飞行参数会污染四个既有品种的表意。
/// 纯出生配置：工厂读表装配 Body/VultureFlightController/VultureWing，内核运行时不回读。
/// 单位全部本项目制：米 / 米每tick，1 RW tile (20px) = 0.5 m，1px = 0.025 m。
/// 数值出处标注反编译行号（Vulture.cs / VultureTentacle.cs，本机参考副本，不入库）。
/// </summary>
public sealed class VultureBreedParams
{
    public string Name = "vulture";

    // —— 身体（≙ Vulture.cs L419-445 的风筝刚架）——
    /// <summary>chunk 半径/连接长度整体缩放。躯干基准半径 0.2375m（9.5px）。</summary>
    public float BodySizeFac = 1f;

    /// <summary>质量缩放（≙ IsKing ×1.4）。当前内核不读质量（约束走显式权重），存备回迁。</summary>
    public float BodyMassFac = 1f;

    /// <summary>肩宽（chunk2↔3，≙ 40px = 1.0m）。</summary>
    public float ShoulderWidth = 1.0f;

    /// <summary>前脊柱在肩线之前的距离（≙ 16px = 0.4m）。</summary>
    public float SpineFront = 0.40f;

    /// <summary>后脊柱在肩线之后的距离（≙ 10px = 0.25m）。脊柱全长 = 前+后 = 0.65m（26px）。</summary>
    public float SpineRear = 0.25f;

    /// <summary>头（喙）chunk 半径（≙ 6.5px = 0.1625m）。</summary>
    public float HeadRadius = 0.1625f;

    /// <summary>头部伺服静息位在前脊柱之前的距离（≙ 脖长 100px = 2.5m 的收缩——本项目
    /// 不做物理脖链，头由拴绳+伺服悬持，渲染层画脖曲线，故取更贴身的 1.0m）。</summary>
    public float NeckLength = 1.0f;

    /// <summary>头拴绳松弛长（PullOnly RestLength，≙ 60px = 1.5m，King 70px）。</summary>
    public float HeadLeash = 1.5f;

    // —— 翅膀（≙ VultureTentacle）——
    /// <summary>翅膀数（普通/King 2，≙ Miros 4）。成对装配：偶数索引左、奇数右。</summary>
    public int Wings = 2;

    /// <summary>每翅节数（≙ tChunks 8，King 10）。</summary>
    public int WingSegments = 8;

    /// <summary>收拢翼长（≙ idealLength@flyingMode=0：140px = 3.5m，King 180px）。</summary>
    public float WingFoldedLength = 3.5f;

    /// <summary>展开翼长（≙ idealLength@flyingMode=1：220px = 5.5m，King 270px）。</summary>
    public float WingFlyLength = 5.5f;

    /// <summary>第二对翅膀（Wings≥3 时）翼展方向向后倾的混合量（四翼拓扑的 3D 参数，
    /// RW Miros 四翼同锚双肩的近似）。前对恒 0。</summary>
    public float RearWingSpanTilt = 0.35f;

    // —— 拍翅节奏（≙ Vulture.cs L789-800 wingFlap）——
    /// <summary>快半程相位速率（φ&lt;0.5 动力下拍，≙ 1/30：15 tick）。</summary>
    public float FlapFastRate = 1f / 30f;

    /// <summary>慢半程相位速率（φ≥0.5 回收上挥，≙ 0.02：25 tick；周期共 40 tick = 1 s）。</summary>
    public float FlapSlowRate = 0.02f;

    /// <summary>下降滑翔的相位倒拨速率（≙ L1152 wingFlap -= 1/70：慢半程净速率趋零 =
    /// 相位冻结在低升力半区，「不施向下力、靠减升力下降」的实现）。</summary>
    public float FlapGlideRate = 1f / 70f;

    /// <summary>拍动目标点法向扫掠幅（米，≙ Lerp(200,600,amp)px = 5~15m）。
    /// 故意远超可达范围——有效机制是「方向指令 + 伺服饱和截断」，不是真实振幅。</summary>
    public float FlapSweepMin = 5f;
    public float FlapSweepMax = 15f;

    /// <summary>翅节伺服每 tick 速度注入上限（≙ 5px×Lerp(0.2,1,amp) = 0.125）。</summary>
    public float WingServoStrength = 0.125f;

    // —— 升力/推进（≙ VultureTentacle L387-393 / Vulture.cs L1119-1191）——
    /// <summary>每翅升力脉冲系数（≙ 5.6px = 0.14）：注入后脊柱单 chunk，
    /// 重力却摊在全部 4 个躯干 chunk 上——约束松弛把脉冲摊分 ≈ ÷4，
    /// 周期均值恰与重力平衡（RW 的悬停颠簸即源于此）。</summary>
    public float LiftForce = 0.14f;

    /// <summary>脉冲基底份额（≙ num4：普通 0.5，Miros 1.4/翅数）：
    /// (share + share·sin2πφ)² 的 share。多翅时调低保持总升力量级。</summary>
    public float LiftShare = 0.5f;

    /// <summary>拍翅横向反冲系数（≙ 2.6px = 0.065）：沿本翅水平展向反方向注入后脊柱，
    /// 双翅健康时逐 tick 近抵消，残差 = 有机侧摆。</summary>
    public float LateralRecoil = 0.065f;

    /// <summary>飞行巡航推进（每躯干 chunk 每 tick，≙ 0.6px = 0.015，×support）。</summary>
    public float FlightPropulsion = 0.015f;

    /// <summary>贴地爬行推进（≙ 1.2px = 0.03，King 1.9px = 0.0475）。</summary>
    public float CrawlPropulsion = 0.03f;

    /// <summary>爬升脉冲（目标在上方时注入后脊柱，≙ 3.5px = 0.0875）。</summary>
    public float ClimbPulse = 0.0875f;

    /// <summary>飞行极速（米/tick）：沿推进方向的 headroom 钳制上限。RW 靠「瞄下一路径格 +
    /// 乘性阻尼」天然限速；我们的胡萝卜连续跟随，必须显式封顶（与蜥蜴 MaxMoveSpeed 同理）。</summary>
    public float MaxFlySpeed = 0.12f;

    /// <summary>竖直上升极速（米/tick）：升力/爬升脉冲注入的 headroom 上限（对躯干平均竖速）。</summary>
    public float MaxRiseSpeed = 0.12f;

    /// <summary>悬停锚点弹簧的每 chunk 注入上限（≙ 0.6px = 0.015，10px 内线性）。</summary>
    public float HoverSpring = 0.015f;

    /// <summary>俯仰配平力（≙ 1.9px = 0.0475）：尾抬头压 ∝ 速度，速度→俯仰角的全部实现。</summary>
    public float PitchTrim = 0.0475f;

    // —— 渲染提示（图形层读取，不进物理）——
    /// <summary>每翅羽毛数（≙ 普通 13~19 / King 15~24 / Miros 6~7 的确定性定值）。</summary>
    public int FeathersPerWing = 16;
}
