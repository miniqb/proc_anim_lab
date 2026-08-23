namespace ProcAnim.Core.Species.RatFiend;

/// <summary>
/// 鼠煞（RatFiend）出生参数表：驼背鼠头人形怪的"手感"全部收拢在这一张表上。
/// 工厂读它装配 Body/RatFiendLocomotionController/Limb/RatArm，内核运行时只读快照——
/// 纯出生配置，零行为分支（DropBugParams 模式）。
/// 机制蓝本 = Humanoid（Scavenger 伺服木偶）+ 本物种新造的三块：倾斜站立力偶（常态驼背）、
/// 断肢（固定断肘/膝）、断腿后爬行（推进 ∝ 抓地肢体数）。数值基线对照 HumanoidParams
/// 的 scavenger 档，修长感靠「躯干长 + 腿长臂长 + 半径小」表达，不碰抓地循环可行域
/// （CLAUDE.md §6.5：腿速度 ≥0.12、步幅 ≤0.75）。
/// </summary>
public sealed class RatFiendParams
{
	/// <summary>稳定预设 ID（ratfiend/gaunt 等；未知 ID 由工厂快速失败）。</summary>
	public string Id = "ratfiend/gaunt";

	// —— 体格（对照 scavenger 0.24/0.175/0.125：更瘦、躯干更长）——
	public float ChestRadius = 0.21f;
	public float HipsRadius = 0.16f;
	public float HeadRadius = 0.13f;
	public float ChestMass = 0.45f;
	public float HipsMass = 0.28f;
	public float HeadMass = 0.06f;

	/// <summary>胸↔髋刚性杆长（修长躯干；scavenger 0.45）。</summary>
	public float ChestHipsDist = 0.55f;

	/// <summary>胸↔头 PullOnly 脖长（允许压缩——「耷拉」姿态靠头伺服把头压进脖长球面内）。
	/// 0.5→0.38→0.30（2026-08-23 短脖轮两刀）：0.5 是 Scavenger 式长脖弧，鼠头应贴肩。
	/// 0.38 只是把球面露出段压到 0.04，但渲染脖根在背峰顶、伺服沿 42° 耷拉向挂头，
	/// 目检仍长；0.30 让头球嵌进胸球 0.04（PullOnly 可压缩、无自碰撞，嵌入合法）。</summary>
	public float NeckLength = 0.30f;

	// —— 姿态（本物种核心新参数）——
	/// <summary>驼背角（度）：站立力偶的目标姿态轴从世界上方向朝 Facing 倾斜这么多。
	/// 失重伺服体制下平衡态被整体倾斜——静止、走、跑全程驼背（显式姿态轴，不靠阻尼差：
	/// 阻尼差只在推进注入速度时产生前倾，静止站立时零输出）。</summary>
	public float HunchAngleDegrees = 25f;

	/// <summary>站立力偶端点（同 Humanoid 语义，误差改对倾斜后的姿态轴度量）。</summary>
	public float StandCoupleMax = 0.1375f;
	public float StandCoupleMin = 0.0075f;

	/// <summary>头沿 aim 方向外顶的力（Humanoid HeadPush 的 aim 版——顶向与伺服目标同向，
	/// 低头时顶向前下而不是沿躯干轴，防脖塌与姿态打架）。</summary>
	public float HeadPush = 0.0075f;

	/// <summary>胸朝移动方向前压力偶（≙ Humanoid LeanPush；满油门被推进余量钳制吸收，保留作加速段质感）。</summary>
	public float LeanPush = 0.00125f;

	/// <summary>头伺服增益/钳制半径/阻尼（Humanoid 掉头响应轮的定型值原样沿用）。</summary>
	public float HeadServoGain = 0.16f;
	public float HeadServoRange = 0.25f;

	/// <summary>头耷拉 aim 的向下分量（droopAim = Facing − up·此值，≈42° 前下——头垂在胸前下方）。</summary>
	public float HeadDroopDown = 0.9f;

	/// <summary>跑步 aim 的向上分量（runAim = Facing + up·此值——直视前方微仰）。</summary>
	public float HeadRunUp = 0.08f;

	/// <summary>爬行时的抬头分量（aim = Facing + up·此值——爬行抬头看路）。</summary>
	public float HeadCrawlUp = 0.2f;

	/// <summary>髋高度伺服（站高 ≈0.83×腿长 → 站立膝微屈的人样直立；R10 站姿修复轮之前
	/// 是 0.6×腿长，两骨 IK 被塞成深蹲马步）。抬高受 Limb.FindGrip 探针几何约束：
	/// goal 高度 = 站高 − 腿长×(0.3×FeetDown/|dir|) 必须落在 (−0.35, 0.55] 内
	/// （探针带上扫 0.35 / 下扫 0.55；超上界探不到地、破下界探针起点陷入地面）——
	/// 调站高必须与 FeetDown 联动核这条（R11 后 goal ≈ 地上 0.06m，两侧余量都足）。</summary>
	public float HipRideHeight = 0.66f;
	public float HipLiftGain = 0.1f;
	public float HipVelYDamp = 0.5f;

	/// <summary>逐 chunk 阻尼。胸/髋**刻意相等**：弓背深度全权交给 HunchAngleDegrees。
	/// 不等阻尼会把动量中性的倾斜站立力偶整流成静立净前漂（0.9/0.86 时实测 0.65m/300tick
	/// ——每 tick 力偶注入动量守恒，但衰减系数不同的两端稳态速度不再抵消）；拉平后
	/// 稳态速度对 = 动量零，漂移消失。Humanoid 靠阻尼差做巡航弓背，本物种不需要。</summary>
	public float ChestDamping = 0.9f;
	public float HipsDamping = 0.9f;
	public float HeadDamping = 0.88f;

	// —— 走↔跑连续混合（Gait 平滑标量，进哈希）——
	/// <summary>Gait 追 RunSpeed 的 LerpAndTick 系数（比例项 / 线性步进项）。</summary>
	public float GaitLerp = 0.06f;
	public float GaitTickStep = 0.01f;

	/// <summary>走→跑姿态权重 r 的映射区间：Gait ≤ Start 全走姿、≥ End 全跑姿。
	/// End 取 0.95（不是 0.8）：RunSpeed 0.8 与 1.0 两档的稳态姿态要可区分
	/// （smoke 的姿态单调断言），0.8 封顶会让两档饱和同值。</summary>
	public float RunBlendStart = 0.35f;
	public float RunBlendEnd = 0.95f;

	/// <summary>嘴开度 = clamp(基础微张 + r·跑步增量 + 宿主 MouthDrive)。常态 0.15 微张、满跑 0.7。</summary>
	public float MouthBase = 0.15f;
	public float MouthRunGain = 0.55f;

	/// <summary>CrawlFactor（0..1 连续爬行标量，渲染 dorsal/pole 混合用）的平滑系数。</summary>
	public float CrawlFactorLerp = 0.08f;
	public float CrawlFactorTickStep = 0.02f;

	// —— 推进（直立）——
	public float BaseSpeed = 0.065f;
	public float MaxMoveSpeed = 0.095f;

	/// <summary>行走驼背的差分通道：胸的推进天花板 ×(1+bias)、髋 ×(1−bias)。
	/// 巡航时 BaseSpeed ≫ MaxMoveSpeed×(1−damping)，胸髋双双被钉在天花板上——任何
	/// 力偶/注入型的前倾通道都会被余量钳制吸收（人形 LeanPush 消融实证的同一现象，
	/// 阻尼差通道又会带来静立净漂移）；唯有天花板本身不对称，持续速度差才穿得过钳制，
	/// 刚性杆把它转成行进前倾。静立时推进不跑，此项零输出——静立驼背由倾斜站立力偶承载。</summary>
	public float HunchSpeedBias = 0.02f;

	/// <summary>步进方向中移动意图对身体朝向的混合权重（同 Humanoid SteerBlend）。</summary>
	public float SteerBlend = 0.4f;

	/// <summary>直立调头限速（弧度/tick，R13）：意图反向时 Facing 画弧回转（0.22 ≈ 12.6°/tick，
	/// 180° ≈ 0.36s），推进/走倾/头伺服/驼背姿态轴全程跟随——此前直立瞬时置向，头伺服把头
	/// 直接从躯干里拽到另一侧、渲染 dorsal（−Facing）低通打转（爬行 R8 的同一病灶）。
	/// 夹角 ≤ 限速时 SlewFacing 直接取目标：直线与小角度转向零影响。转弯半径上界 = 巡航速/
	/// 此值（满油门 ≈0.39m、走档 ≈0.14m；实际更紧——回转中推进反向对旧速度是刹车），
	/// 仍低于到点判定半径 0.4m，无绕圈风险。初版取 0.12（2×CrawlTurnRatePerTick），
	/// 用户实测嫌弧大，提到 0.22 换干脆紧凑的原地掉头感；再往上逼近瞬时置向的头穿躯干带
	/// （爬行消融红灯 ③ 的病灶），不宜超 ~0.3。</summary>
	public float StandTurnRatePerTick = 0.22f;

	/// <summary>掉头主动刹车（R13b）：踩地且朝向-意图对齐度 < TurnAlignGate 时，胸髋
	/// 水平速度每 tick 乘此保留系数——纯 chunk 阻尼（0.9/tick）放速度太慢，冲刺掉头滑出
	/// ≈1m 横向弧（探针逐 tick 实测）。与推进零注入（同一道门）配合：滑步刹停 → 低速
	/// 拧身 → 对齐后按本种「弹射起步」体质（BaseSpeed ≫ 天花板，2 tick 到巡航）沿新方向
	/// 干净加速。只砍水平分量（竖直留给重力/髋伺服），空中不刹（击飞弹道不受染指）。</summary>
	public float TurnBrakePerTick = 0.8f;

	/// <summary>转体门（R13b，双态）：Facing·意图 < 此值 = 「还在转体」——推进零注入 +
	/// 主动刹车；≥ 此值恢复全油门。线性节流（注入 ∝ 对齐度）被证伪：BaseSpeed(0.065) ≫
	/// 天花板余量，min() 钳制下部分对齐即近全额注入，沿旋转中的 Facing 逐 tick 矢量叠加
	/// 把出弯速度弹到 4.6 m/s 超巡航、尾段侧向残速再横漂 0.5m。0.9 ≈ 偏差 26° 内放行。
	/// 直线行走对齐度恒 1 永不触发；缓弯目标漂移被 slew 逐 tick 追平（对齐度 ≥
	/// cos(12.6°) ≈ 0.976，如竞技场绕行玩家）同样不触发——只有 >26° 的急转吃这道门。</summary>
	public float TurnAlignGate = 0.9f;

	// —— 腿（复用 Lizard Limb，anchor=髋；LookaheadTicks=3 由工厂硬性写入）——
	// R10 站姿修复轮：LegLateral 0.3→0.14（马步外撇 → 脚落髋正下）。
	// R11 步态修复轮：FeetDown 0.75→4.5。FeetDown 决定 FindGrip 步向的下压角
	// （dir = stepDir − up×0.3×FeetDown），落点前距 = 腿长/|dir|。0.75 时下压角仅 ~13°，
	// 落点甩在髋前 0.94×腿长（探针几乎够不到、脚被钳制拖行）；4.5 → 下压角 ≈54°，
	// 落点收到髋前 ≈0.46m——高站位双足的髋→落点方向本来就该近乎垂下（人迈步的
	// 髋-落点连线 ≈55°）。副作用是探针带整体贴地：下行落差余量从 0.06m 放宽到 ≈0.5m。
	public float LegLength = 0.8f;
	public float FootRadius = 0.06f;
	public float LegSpeed = 0.3f;
	public float LegQuickness = 0.7f;
	public int LegGripDelay = 3;
	public float StepLength = 0.6f;
	public float LiftFeet = 0.2f;
	public float FeetDown = 4.5f;
	public float LegLateral = 0.14f;

	/// <summary>反相自举的最小前后间距（米）：两脚同时踩稳且沿步向间距塌到此值内、居后脚
	/// 已过髋时，居后脚被提前释放重迈（见 RatFiendLocomotionController.TickLegs R11 注释）。
	/// 取 ≈0.26×步幅跨距：远小于自稳后的半步差（≈0.35m），收敛后规则静默。</summary>
	public float LegPhaseMinGap = 0.2f;
	public bool SmoothenLegMovement = true;

	/// <summary>走姿慢摆（R15）：摆动期 HuntSpeed 的走端缩放系数（跑端恒 1，对标手臂
	/// ArmSwingSpeedFactor 先例）。步频由前瞻释放循环决定、基本不受影响，变慢的是单步
	/// 抬脚→落下的腾空过程（此前 ~1.8×腿长/LegSpeed ≈ 3 tick 摆完，40Hz 下像瞬移）。
	/// 可行域下界：LegSpeed×此值 ≥ 0.12（CLAUDE.md §6.5），否则脚追不上身体。</summary>
	public float LegSwingSpeedFactor = 0.45f;

	/// <summary>慢摆的混合窗（对 Gait）：≤Start 全额减速、≥End 恢复全速。窗口刻意比
	/// RunBlend 姿态窗（0.35~0.95）更靠后——walk 档（RunSpeed 0.6 → Gait≈0.6）必须落在
	/// 全额减速带内；若复用姿态窗，r≈0.42 会把减速稀释掉近一半。</summary>
	public float LegSwingBlendStart = 0.62f;
	public float LegSwingBlendEnd = 0.95f;

	// —— 手臂（RatArm）——
	public float ArmLength = 1.15f;
	public float HandRadius = 0.06f;
	public float ArmSpeed = 0.6f;
	public float ArmQuickness = 0.95f;
	public float ArmAdaptVel = 0.4f;
	public float ArmExaggerate = 0.1f;

	/// <summary>走路摆臂：目标点沿 Facing 的摆幅（×对侧腿相位，右臂随左腿天然反相）。
	/// R10 站姿修复轮 0.25→0.35：站高抬升后腿的归一化相位变小，等幅补回可见摆幅。</summary>
	public float ArmSwingAmplitude = 0.35f;

	/// <summary>走路摆臂目标点的固定前偏（米）。</summary>
	public float ArmWalkForwardBias = 0.08f;

	/// <summary>走路时手垂位深度（×臂长——手垂在胸侧下方这么深）。</summary>
	public float ArmWalkDangleDrop = 0.62f;

	/// <summary>走路摆臂的追猎速度系数（慢摆；跑步前伸线性升回 1.0 = 急伸）。</summary>
	public float ArmSwingSpeedFactor = 0.25f;

	/// <summary>跑步前伸目标：前向/侧向/上抬分量（前两者 ×臂长、侧向为绝对米数）。</summary>
	public float RunReachForward = 0.8f;
	public float RunReachLateral = 0.22f;
	public float RunReachUp = 0.05f;

	/// <summary>凝视备抓抬臂（R16b）：LookTarget 非空时走/跑手臂混合权重的下限——低速逼近
	/// 猎物手也保持前伸备抓（举着），不随 Gait 落回垂摆。仅在 LookTarget 设置时生效
	/// （opt-in 派生，哈希中立同 LookTarget）。</summary>
	public float LookArmRaise = 0.85f;

	// —— 攻击接缝 ——
	/// <summary>手到 GrabTarget 的「抓住」判定半径（米，HandsOnTarget 观测）。</summary>
	public float GrabContactRadius = 0.25f;

	// —— 断肢 ——
	/// <summary>断口比例：断肢后残肢可及长度 = 原长 × 此值（固定断肘/膝 = 对半）。</summary>
	public float SeveredLengthFactor = 0.5f;

	/// <summary>断腿残肢（膝端粒子）的手动积分参数：速度阻尼 / 垂位弹簧增益。</summary>
	public float StumpDamping = 0.85f;
	public float StumpSpring = 0.1f;

	// —— 爬行（Crawling = 清醒且断了任一条腿）——
	/// <summary>每条抓地肢体的推进力（米/tick）：force = 此值 × 抓地肢体数 × RunSpeed——
	/// 「前进动力 ∝ 抓地肢体数」的涌现核心（Vulture CrawlPropulsion 的推广）。</summary>
	public float CrawlForcePerLimb = 0.011f;

	/// <summary>爬行速度上限（逐 chunk headroom 钳制，≈半步行速）。</summary>
	public float CrawlMaxSpeed = 0.045f;

	/// <summary>爬行 Facing 回转限速（弧度/tick）。爬行没有腿把身体真转过来——目标在身后时
	/// 瞬时置向会让头伺服把头从裆下拽过去（头髋穿插）；限速后身体拖着自己画弧调头，
	/// 推进沿 Facing 跟随。0.06 ≈ 180° 用 52 tick（1.3 秒）。站立置向不受此限。</summary>
	public float CrawlTurnRatePerTick = 0.06f;

	/// <summary>有支撑时的微小抬鼻力偶（吻部离地；消融可验证非必需）。</summary>
	public float CrawlNoseLift = 0.003f;

	/// <summary>手撑地探针：起点抬升 / 前伸系数（×有效臂长）/ 侧向错开 / 向下深度。</summary>
	public float CrawlProbeRise = 0.3f;
	public float CrawlProbeForward = 0.55f;
	public float CrawlProbeSide = 0.28f;
	public float CrawlProbeDepth = 1.2f;

	/// <summary>撑点合法窗：身后上限（米）/ 身前上限（×有效臂长）/ 距离上限（×有效臂长）。</summary>
	public float CrawlBehindWindow = 0.25f;
	public float CrawlAheadWindowFactor = 1.1f;
	public float CrawlReachFactor = 1.4f;

	/// <summary>无撑点时手的前伸摸地目标：前向（×有效臂长）/ 下沉（米）。</summary>
	public float CrawlHandTargetForward = 0.7f;
	public float CrawlHandTargetDown = 0.15f;

	/// <summary>手「撑稳」判定的吸附余量（米，加在手半径上）。</summary>
	public float ArmPlantPad = 0.05f;

	// —— 爬台阶（手拉体升）——
	/// <summary>撑稳的手高出 chunk 底面（死区外）时把该 chunk 朝上拽的增益（米/tick 每米高差，
	/// headroom 钳制）。平地撑点与 chunk 底面同高 → 恒为零：平地爬行逐位不变。可爬高度无
	/// 显式参数——高过「探针举高 + 胸高」的台面探不到（起点陷入固体 → 零法线拒收），
	/// 涌现上限 ≈ CrawlProbeRise + 胸径。</summary>
	public float CrawlClimbGain = 0.5f;

	/// <summary>爬台阶上升速度天花板（米/tick）。要压过重力（0.0225/tick）净上升。</summary>
	public float CrawlClimbMaxRise = 0.035f;

	/// <summary>爬台阶死区（米）：撑点高差低于此不触发拽升、也不豁免滑墙（防平地微噪声）。</summary>
	public float CrawlClimbDeadzone = 0.04f;

	// —— 蠕动（四肢全断 + 有移动意图）——
	/// <summary>周期性内力偶幅度（动量守恒内力，靠摩擦整流出近零位移的挣扎感）。</summary>
	public float WrigglePulse = 0.002f;

	/// <summary>蠕动周期（tick）。</summary>
	public int WrigglePeriodTicks = 60;

	// —— 撑地/摩擦（Humanoid 同款）——
	public int GroundedTicks = 5;
	public float GroundProbeSlack = 0.3f;
	public float FootedSurfaceFriction = 0.5f;
	public float AirborneSurfaceFriction = 0.3f;

	internal RatFiendParams Snapshot() => (RatFiendParams)MemberwiseClone();
}
