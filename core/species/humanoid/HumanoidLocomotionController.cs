using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Species.Lizard;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Humanoid;

/// <summary>
/// 人形运动控制器 = Body（胸/髋/头三 chunk）+ 双 Limb 腿 + 双 Arm 手臂 + 站立力偶。
/// 与 <see cref="LizardLocomotionController"/> 并列的第二类运动后端（≙ core/README「人形等应在
/// 共享层之上增加并列控制器」），蓝本 = 反编译 Scavenger（拾荒者）。与蜥蜴的本质分野：
/// · 重力开关的判据是「清醒且近地」而不是「腿抓稳」（≙ Scavenger.Act L2207 对三 chunk
///   `vel.y += gravity` **抵消** chunk 级重力——清醒的地面拾荒者是失重的伺服木偶，
///   站姿由下面三条伺服雕出来；昏迷时 Act 不跑 → 重力回归 → 瘫倒涌现，零 knockdown 状态机）。
///   没有 SupportNormal/爬墙涌现——地面双足生物，撞墙只水平滑墙不上爬；
/// · 站立 = 姿态误差力偶（≙ WeightedPush(胸,髋,up, LerpMap(dot,-1,1,5.5,0.3))）：失重下它
///   永远赢，从任意姿态回正；倒得越狠推得越猛、正立时几乎不施力（不抖）；
/// · 腿脚不承重（≙ ScavengerLeg 纯图形层）：踩地的是髋 chunk + 髋高度伺服，腿是 plant-and-trail 装饰；
/// · 手臂走两条独立通道：物理层 KnucklePos（射线撑手点驱动的腾空俯仰泵，指关节行走的推进辅助）
///   + 肢体层 Arm 粒子（扇扫锁点/摆动纯可视化，不回传力）——RW 的分层原样移植；
/// · 手臂驱动 = 一条扁平优先级链（昏迷→投掷→蓄力→指向→持物→撑地→闲置），链尾统一臂长
///   约束与防穿模（≙ ScavengerHand.Update 的 if-else 链——加新动作 = 插一条分支，不影响移动）。
/// 确定性：RW 的随机抖动（蓄力 shake/指向神经质）全部换成 sin(TickIndex) 相位——零随机零墙钟。
/// </summary>
public sealed class HumanoidLocomotionController
{
	public readonly Body Body;

	/// <summary>胸 = 主根（最重，≙ mainBodyChunk）；髋 = 贴地承重点；头 = 极轻的观察端。</summary>
	public readonly BodyChunk Chest;
	public readonly BodyChunk Hips;
	public readonly BodyChunk Head;

	/// <summary>双腿（复用蜥蜴 Limb，anchor=髋；up 恒传世界上——爬墙分支静态死路）。</summary>
	public readonly List<Limb> Legs = new();

	/// <summary>双臂（[0]=主手 ≙ RW grasp 0 所在的手，[1]=副手）。</summary>
	public readonly List<Arm> Arms = new();

	// —— 输入面（与蜥蜴同三旋钮 + 人形专属开关）——
	/// <summary>移动意图方向（世界系，可不平整——内核自行压平到水平面）。
	/// <see cref="MoveTarget"/> 非 null 时改由内核每 tick 导出。</summary>
	public Vector3 MoveDir;

	/// <summary>移动意图强度 ∈ [0,1]。死区语义与蜥蜴共用同一常量。</summary>
	public float RunSpeed;

	/// <summary>可选第三旋钮：路径点直喂（语义同蜥蜴——喂邻近可达路径点，到点换点归宿主）。
	/// 人形是地面生物，到点判定用水平距离。Shift 随世界平移，Teleport 作废清 null。</summary>
	public Vector3? MoveTarget;

	/// <summary>到点判定半径（米，水平距离）。</summary>
	public float MoveTargetArriveRadius = 0.4f;

	/// <summary>MoveTarget 模式的到达信号（MoveTarget 为 null 时恒 false）。</summary>
	public bool AtMoveTarget { get; private set; }

	/// <summary>移动意图死区——与蜥蜴/腿层共用同一数值真相源（Limb 内部也引用它）。</summary>
	public const float MoveIntentDeadzone = LizardLocomotionController.MoveIntentDeadzone;

	public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
		&& (MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

	/// <summary>意识开关（≙ RW !Consious ⇒ Act() 不跑）：false 时站立力偶/伺服/推进/手臂链全停、
	/// 手臂垂落、腿收拢——身体只剩重力与约束，自然瘫倒；恢复 true 后力偶自动把身体撑起来。
	/// 眩晕/死亡表现全靠这一个开关，没有 knockdown 状态机。</summary>
	public bool Conscious = true;

	/// <summary>指向目标（世界点，≙ RW PointingAnimation）：非 null 且清醒时主手伸向它
	/// （带 sin 伸缩）。宿主写入/清除；Shift 随世界平移，Teleport 作废清 null。</summary>
	public Vector3? PointTarget;

	/// <summary>持物姿态开关（≙ RW grasp 0——只有主手真正"拿在手上"）：true 时主手切到躯干系
	/// 携带位。被持物本体归宿主：读 <see cref="MainHandPos"/>/<see cref="MainHandDir"/> 硬钉即可
	/// （≙ RW GraphicsModuleUpdated 对 grasp 0 的 MoveFromOutsideMyUpdate + vel*0）。</summary>
	public bool Carrying;

	// —— 调参（工厂从 HumanoidParams 拷入；语义与换算见 HumanoidParams 注释）——
	public float BaseSpeed = 0.06f;
	public float MaxMoveSpeed = 0.08f;
	public float StandCoupleMax = 0.1375f;
	public float StandCoupleMin = 0.0075f;
	public float HeadPush = 0.0075f;
	public float LeanPush = 0.00125f;
	public float HeadLean = 0.7f;
	public float NeckLength = 0.55f;
	public float HipRideHeight = 0.25f;
	public float HipLiftGain = 0.1f;
	public float HipVelYDamp = 0.5f;
	public float ChestDamping = 0.9f;
	public float HipsDamping = 0.8f;
	public float HeadDamping = 0.8f;
	public float ArmLength = 1.25f;
	public float KnuckleBehindWindow = 0.5f;
	public float KnuckleAheadWindow = 3.75f;
	public float KnucklePumpFar = 1.0f;
	public float KnucklePumpNear = 0.25f;
	public float KnucklePumpMin = 0.0125f;
	public float KnucklePumpMax = 0.0875f;

	/// <summary>步进方向中移动意图对身体朝向的混合权重（同蜥蜴 SteerBlend）。</summary>
	public float SteerBlend = 0.4f;

	/// <summary>按 gripCounter 协调双腿错开抬脚。</summary>
	public bool SmoothGait = true;

	/// <summary>头部位置伺服：把头拉向「胸 + 躯干轴×脖长」（≙ RW L1957 的 WeightedPush(2,0,
	/// ClampMagnitude(target-pos,5px), 0.8/5px)——增益 0.16/tick）。没有它头会吊在脖长的
	/// 球面下缘（头 chunk 有重力、HeadPush 单独顶不住）。钳制 Range 取 RW 5px=0.125 的 2 倍
	/// （掉头响应轮的有意偏离）：满速巡航所需修正恰好 ≈0.125，旧钳制下伺服恒饱和、掉头零
	/// 牵引余量（头就位要 110 tick，用户白盒实测暴露）；放宽只翻倍远场牵引，近场刚度
	/// （力 ∝ 距离×增益）与站姿稳定性不变——就位 14 tick、扫描全组合零振铃。</summary>
	public float HeadServoGain = 0.16f;
	public float HeadServoRange = 0.25f;

	/// <summary>连续贴地/近地这么多 tick 视为站稳（重力开关 + 摩擦切档 + 髋伺服的共同门控）。</summary>
	public int GroundedTicks = 5;

	/// <summary>髋向下探地射线在 HipRideHeight 之外的冗余长度（米）。</summary>
	public float GroundProbeSlack = 0.3f;

	/// <summary>探地射线在髋上方的起点抬升（米）：行进中先看见台阶顶面，伺服目标随之抬高，
	/// 失重的髋直接滑上台阶（RW 靠寻路格伺服到更高格中心，射线版等价物）。</summary>
	private const float HipProbeRise = 0.5f;

	/// <summary>行进中探地点的前置量（米）：在台阶/坡变前一步看到新地面高度。</summary>
	private const float HipProbeLead = 0.3f;

	public float FootedSurfaceFriction = 0.5f;
	public float AirborneSurfaceFriction = 0.3f;

	/// <summary>出手速度（米/tick，≙ RW Throw 的 vel = throwDir * max(20px,…) = 0.5）。</summary>
	public float ThrowSpeed = 0.5f;

	/// <summary>出手后头沿投掷方向的甩速注入（前 10 tick 逐 tick，≙ ThrowAnimation 的
	/// chunk2.vel += DirVec·3px 换算后按体格调低）。</summary>
	public float ThrowHeadFling = 0.03f;

	/// <summary>出手动画时长（tick，≙ ThrowAnimation Continue => age &lt; 20）。</summary>
	public const int ThrowAnimLength = 20;

	// —— 观测面（全部只读）——
	/// <summary>躯干轴与上方向的点积 ∈ [-1,1]：1=正立、0=横躺、-1=倒立（站立力偶的误差源）。</summary>
	public float Uprightness { get; private set; } = 1f;

	/// <summary>连续贴地/近地 tick 数（封顶 100）。</summary>
	public int GroundedCounter { get; private set; }

	public bool Grounded => GroundedCounter >= GroundedTicks;

	/// <summary>true = 重力在拽（坠落/昏迷）；false = 清醒且近地，重力 0（≙ Act 的抵消块）。
	/// 与蜥蜴的同名观测语义一致（渲染按它换色）。</summary>
	public bool ApplyGravity { get; private set; } = true;

	/// <summary>水平朝向（单位向量）：有移动意图时 = 压平后的意图方向，无意图时保持上次值。</summary>
	public Vector3 Facing { get; private set; } = Vector3.Right;

	/// <summary>指关节撑点（物理层抽象量，≙ Scavenger.knucklePos）：行走中从胸前探地射线取得、
	/// 落到合法窗外即失效。腾空俯仰泵与手的扇扫都以它为参考——手臂对移动的"作用"里，
	/// 真物理的一半在这里，手粒子只是它的可视化。</summary>
	public Vector3? KnucklePos { get; private set; }

	/// <summary>手扇扫锁定撑点的累计次数（回归指标：行走场景应持续增长）。</summary>
	public long KnucklePlants { get; private set; }

	/// <summary>当前抓地腿数（腿更新后重算；人形只用于观测与俯仰泵门控，不缩放推进力）。</summary>
	public int LegsGripping { get; private set; }

	/// <summary>投掷蓄力已持续 tick（0 = 未蓄力；蓄力期强制停驶 ≙ RW 瞄准时 moving=false）。</summary>
	public int ThrowChargeTicks { get; private set; }

	/// <summary>出手动画剩余 tick（前 5 tick 主手瞬移感、前 10 tick 头部甩速）。</summary>
	public int ThrowAnimTicks { get; private set; }

	/// <summary>当前投掷方向（蓄力/出手动画期间有效）。</summary>
	public Vector3 ThrowDir { get; private set; } = Vector3.Right;

	/// <summary>主手位置（≙ RW ItemPosition(0)——被持物/武器硬钉到这里）。</summary>
	public Vector3 MainHandPos { get; private set; }

	/// <summary>主手持物朝向（≙ RW WeaponDir()）：蓄力/出手 = 投掷方向，指向 = 指向方向，其余 = 朝向。</summary>
	public Vector3 MainHandDir { get; private set; } = Vector3.Right;

	/// <summary>撑点重扫距离：现有撑点离 KnucklePos 超过它且轮到本手扇扫时换点（≙ RW 40px=1.0m）。</summary>
	private const float KnuckleRegrabDist = 1.0f;

	/// <summary>撑点探地射线的向下深度（从胸高探到地面，三预设最深的胸位仍有富余）。</summary>
	private const float KnuckleProbeDepth = 1.5f;

	/// <summary>撑点探地在胸前的水平超前量 = 臂长×此系数（撑点落在手够得着的前方）。</summary>
	private const float KnuckleLeadFac = 0.7f;

	/// <summary>手扇扫的俯仰偏角序列（度，固定顺序保确定性；≙ RW CheckForGrabPos 的 0..45° 步 5°
	/// 左右交替扫，正=朝上、负=朝下，首个合法命中即取）。</summary>
	private static readonly float[] KnuckleScanDegrees = { 0f, 5f, -5f, 10f, -10f, 15f, -15f, 20f, -20f, 25f };

	/// <summary>两手撑点目标的侧向半距：扇扫中轴瞄 kp + right·Side·此值。分离必须错开**目标点**——
	/// 朝单一 kp 汇聚的射线会把起点的侧向错开在命中处精确抵消，两手锁到同一世界点。</summary>
	private const float KnucklePlantSpread = 0.2f;

	/// <summary>每手扫描俯仰偏置（度；≙ RW CheckForGrabPos 的 limbNumber==0?-4°:+4°，两手落点
	/// 沿行进方向前后交错——2D 原版唯一的分离维度，3D 里与上面的侧向错开叠加）。</summary>
	private const float KnuckleScanBiasDeg = 4f;

	public HumanoidLocomotionController(Body body, BodyChunk chest, BodyChunk hips, BodyChunk head)
	{
		Body = body;
		Chest = chest;
		Hips = hips;
		Head = head;
		MainHandPos = chest.Pos;
	}

	/// <summary>rebase：身体、腿、手臂、撑点、指向/直喂目标整体平移，速度/撑地/蓄力状态原样保留。
	/// 只适用于「地形随你一起平移」的场景；地形不动的瞬移用 <see cref="Teleport"/>。</summary>
	public void Shift(Vector3 delta)
	{
		Body.Shift(delta);
		foreach (Limb leg in Legs)
		{
			leg.Shift(delta);
		}
		foreach (Arm arm in Arms)
		{
			arm.Shift(delta);
		}
		if (KnucklePos is { } kp)
		{
			KnucklePos = kp + delta;
		}
		if (PointTarget is { } pt)
		{
			PointTarget = pt + delta;
		}
		if (MoveTarget is { } fed)
		{
			MoveTarget = fed + delta;
		}
		// MainHandPos 是世界坐标观测（宿主用它钉被持物）：它每 tick 末重算，但 Shift 到
		// 下个 Tick 之间读到旧位置会把持物钉回原点（评审 P 完备性）。
		MainHandPos += delta;
	}

	/// <summary>teleport：平移并作废一切位置态记忆——腿手全松、撑点/指向/直喂目标/蓄力全清，
	/// 落地后由站立力偶与优先级链重建姿态。</summary>
	public void Teleport(Vector3 delta)
	{
		Shift(delta);
		foreach (Limb leg in Legs)
		{
			leg.ForceRelease();
		}
		foreach (Arm arm in Arms)
		{
			arm.ForceRelease();
		}
		GroundedCounter = 0;
		KnucklePos = null;
		PointTarget = null;
		MoveTarget = null;
		AtMoveTarget = false;
		ThrowChargeTicks = 0;
		ThrowAnimTicks = 0;
	}

	/// <summary>宿主冲量注入（跳跃/击飞）：全 chunk 加同一速度增量，腿手松开、撑地清零，
	/// 身体进弹道；落地后站立力偶自动爬起（--yank 回归验证的恢复路径）。</summary>
	public void Launch(Vector3 velocityPerTick)
	{
		foreach (BodyChunk c in Body.Chunks)
		{
			c.Vel += velocityPerTick;
		}
		foreach (Limb leg in Legs)
		{
			leg.ForceRelease();
		}
		foreach (Arm arm in Arms)
		{
			arm.ForceRelease();
		}
		GroundedCounter = 0;
		KnucklePos = null;
	}

	/// <summary>开始/更新投掷蓄力（清醒才生效；方向零向量拒绝）。蓄力期身体强制停驶、
	/// 主手拉到身后蓄力位抖动（≙ ThrowChargeAnimation）。重复调用只更新方向。</summary>
	public bool StartThrowCharge(Vector3 dir)
	{
		if (!Conscious || dir.LengthSquared() < 1e-12f)
		{
			return false;
		}
		ThrowDir = dir.Normalized();
		if (ThrowChargeTicks == 0)
		{
			ThrowChargeTicks = 1;
		}
		ThrowAnimTicks = 0;
		return true;
	}

	/// <summary>出手：返回被投物应获得的速度（米/tick，≙ Weapon.Thrown 的 throwDir*20px），
	/// 物体本体归宿主（从 <see cref="MainHandPos"/> 起飞）。未蓄力返回 null。
	/// 出手动画（主手甩向掷向 + 头部甩速）随后自动播放。</summary>
	public Vector3? ReleaseThrow()
	{
		if (!Conscious)
		{
			// 昏迷即弃掷（契约 §4.2）：宿主在同一帧先置昏再释放也不能拿到出手向量——
			// 只靠下个 Tick 开头的清理会留一帧「昏迷仍能投掷」的窗口。
			ThrowChargeTicks = 0;
			return null;
		}
		if (ThrowChargeTicks == 0)
		{
			return null;
		}
		ThrowChargeTicks = 0;
		ThrowAnimTicks = ThrowAnimLength;
		return ThrowDir * ThrowSpeed;
	}

	/// <summary>
	/// 唯一入口。固定驱动序（人形的确定性契约）：
	/// 直喂导出 → 撑地判定/摩擦切档 → 意图压平/滑墙 → 站立力偶+头伺服+髋伺服 → 推进 →
	/// 撑点更新+俯仰泵 → 投掷状态 → 逐 chunk 阻尼 → Body.Tick → 腿 → 手臂优先级链 → 观测量。
	/// </summary>
	public void Tick(in TickContext ctx)
	{
		Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;

		if (!Conscious)
		{
			// 昏迷即弃掷（≙ RW Stun 掉主手武器 + Act 不跑）：蓄力与出手动画都不该在瘫倒时继续。
			ThrowChargeTicks = 0;
			ThrowAnimTicks = 0;
		}

		bool derivedMove = MoveTarget is not null;
		if (MoveTarget is { } fed)
		{
			DeriveMoveFromTarget(fed, up);
		}
		else
		{
			AtMoveTarget = false;
		}

		Uprightness = ComputeUprightness(up);
		UpdateGrounded(ctx, up, out bool groundHit, out Vector3 groundPoint);

		Vector3 effMove = Conscious && HasMoveIntent && ThrowChargeTicks == 0
			? SlideAlongWalls(FlattenToGround(MoveDir, up), up)
			: Vector3.Zero;
		if (effMove != Vector3.Zero)
		{
			Facing = effMove;
		}

		if (Conscious)
		{
			ApplyStandCouple(up);
			ApplyWalkLean(effMove);
			ApplyHeadServo(effMove);
			ApplyHipServo(up, groundHit, groundPoint);
			ApplyLocomotionForce(effMove);
			UpdateKnuckle(ctx, up, effMove);
		}
		else
		{
			KnucklePos = null;
		}
		UpdateThrowState();
		ApplyChunkDamping();
		Body.Tick(ctx);
		TickLegs(ctx, up, effMove);
		TickArms(ctx, up, effMove);
		Uprightness = ComputeUprightness(up);
		UpdateHandObservables();

		if (derivedMove)
		{
			// 派生 MoveDir 不跨 tick 残留（同蜥蜴约定）：宿主只清 MoveTarget 不写 MoveDir 就该停。
			MoveDir = Vector3.Zero;
		}
	}

	/// <summary>直喂旋钮的意图导出：地面生物用水平距离判到点、水平方向当意图
	/// （喂点贴地、身体有站高——3D 距离会把「站在点正上方」误判成永远不到）。</summary>
	private void DeriveMoveFromTarget(Vector3 target, Vector3 up)
	{
		Vector3 d = target - Chest.Pos;
		Vector3 dh = d - up * d.Dot(up);
		AtMoveTarget = dh.Length() <= MoveTargetArriveRadius;
		MoveDir = AtMoveTarget || dh.LengthSquared() < 1e-12f ? Vector3.Zero : dh.Normalized();
	}

	/// <summary>撑地判定 + 重力开关 + 摩擦切档。近地探针与髋伺服共用一根射线：行进中探地点
	/// 前置（先看见台阶/坡变）、起点抬升（射线从髋上方往下打，台阶顶面在可视范围内）；
	/// 前置探针打空/被拒（顶墙时起点陷入墙体给零法线、崖边探出悬空）再补一根原位竖直射线——
	/// 没有它，站在崖边瞄准或正面顶墙时撑地判定逐 tick 翻转、永不收敛（评审复现）。
	/// 重力开关 ≙ Scavenger.Act 的抵消块：清醒且近地 → 失重（站姿全靠伺服雕），
	/// 坠远/昏迷 → 重力回归。判据用近地探针而非腿抓握——人形的腿不承重，抓地计数骗不了它。
	/// 「地面」的法线门槛与墙分类同一条线（≥0.5 = 60° 以内）：曾用 0.3 时 0.3~0.5 争议带
	/// （60°~72.5° 陡面）被重力开关当地面、被滑墙当墙，人形能全速爬上 65° 陡壁永久悬挂
	/// （评审复现 49m）；chunk 接触同样按接触法线过滤——贴竖墙的胸接触不是撑地，
	/// 否则击飞撞墙会在半空反复失重成粘滞滑降。</summary>
	private void UpdateGrounded(in TickContext ctx, Vector3 up, out bool groundHit, out Vector3 groundPoint)
	{
		groundHit = false;
		groundPoint = default;
		// 蓄力瞄准强制停驶（effMove 会被清零），探地不前置——身体静止时前置探针在崖边打空
		// 只会让重力开关空抖（评审复现：200 tick 翻转 30 次）。
		bool leading = Conscious && HasMoveIntent && ThrowChargeTicks == 0;
		Vector3 origin = Hips.Pos + (leading ? Facing * HipProbeLead : Vector3.Zero);
		if (ctx.Terrain.Raycast(origin + up * HipProbeRise,
				origin - up * (HipRideHeight + GroundProbeSlack), out TerrainHit hit)
			&& IsGroundNormal(hit.Normal, up))
		{
			groundHit = true;
			groundPoint = hit.Point;
		}
		else if (leading
			&& ctx.Terrain.Raycast(Hips.Pos + up * HipProbeRise,
				Hips.Pos - up * (HipRideHeight + GroundProbeSlack), out TerrainHit fallback)
			&& IsGroundNormal(fallback.Normal, up))
		{
			groundHit = true;
			groundPoint = fallback.Point;
		}

		bool contact = groundHit
			|| (Chest.TerrainContact && IsGroundNormal(Chest.ContactNormal, up))
			|| (Hips.TerrainContact && IsGroundNormal(Hips.ContactNormal, up));
		GroundedCounter = contact ? Mathf.Min(GroundedCounter + 1, 100) : 0;
		ApplyGravity = !(Conscious && Grounded);
		Body.GravityScale = ApplyGravity ? 1f : 0f;
		Body.SurfaceFriction = Grounded ? FootedSurfaceFriction : AirborneSurfaceFriction;
	}

	/// <summary>「可站立地面」的统一判据：非零法线且与上方向夹角 ≤60°（与 SlideAlongWalls 的
	/// 墙分类、knuckle 撑面门槛同一条线——分类之间不留争议带）。</summary>
	private static bool IsGroundNormal(Vector3 normal, Vector3 up) =>
		normal.LengthSquared() > 1e-12f && normal.Dot(up) >= 0.5f;

	/// <summary>站立力偶（≙ WeightedPush(0,1,up, LerpMap(dot,-1,1,5.5px,0.3px))）：姿态误差驱动的
	/// 非线性伺服。体重由髋贴地 + 胸髋刚性杆承担，这里只抗倾覆——正立时几乎不施力（不抖），
	/// 倒立时 18 倍力矩（自动爬起）。昏迷时整段跳过 = 瘫倒涌现。</summary>
	private void ApplyStandCouple(Vector3 up)
	{
		float couple = Mathf.Remap(Uprightness, -1f, 1f, StandCoupleMax, StandCoupleMin);
		WeightedPush(Chest, Hips, up, couple);
	}

	/// <summary>胸朝移动方向前压（≙ RW L1956 WeightedPush(0,1,HeadLookDir,0.05px)）：站立力偶
	/// 恒指正上，此力偶叠加其上。满油门巡航时被 ApplyLocomotionForce 的余量钳制完全吸收
	/// （消融实证零净效果）——巡航弓背来自 Chest/Hips 阻尼差，此项只作用于低油门/加速段。
	/// 停驶/蓄力时 effMove 已为零，自动归零。</summary>
	private void ApplyWalkLean(Vector3 effMove)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		WeightedPush(Chest, Hips, effMove, LeanPush * RunSpeed);
	}

	/// <summary>头部双伺服（≙ RW L1803 轴外顶 + L1957 位置伺服）：站姿头待在躯干轴延长线上的
	/// 脖长处——头 chunk 极轻(0.05)，两条伺服合力恰好抵重力，脖子既不塌也不飘。行走时位置
	/// 伺服目标向移动方向混合（≙ L1957 头目标 = 胸 + HeadLookDir×脖长——行进中头探在胸前方，
	/// 弓背剪影的主要来源），轴外顶保持沿躯干轴不变（≙ L1803 无视视线）。</summary>
	private void ApplyHeadServo(Vector3 effMove)
	{
		Vector3 axis = Dir(Hips.Pos, Chest.Pos);
		WeightedPush(Head, Chest, axis, HeadPush);
		Vector3 aim = axis;
		if (effMove != Vector3.Zero)
		{
			Vector3 blended = axis.Lerp(effMove, HeadLean * RunSpeed);
			if (blended.LengthSquared() > 1e-8f)
			{
				aim = blended.Normalized();
			}
		}
		Vector3 target = Chest.Pos + aim * NeckLength;
		Vector3 pull = (target - Head.Pos).LimitLength(HeadServoRange);
		WeightedPush(Head, Chest, pull, HeadServoGain);
	}

	/// <summary>髋高度伺服（≙ chunk1.vel.y *= 0.5; += (格中心y-髋y)*0.1）：把髋轻拉向地面上方
	/// HipRideHeight 处——目标略高于物理静息位，伺服只给上提趋势，重量仍由地面扛。</summary>
	private void ApplyHipServo(Vector3 up, bool groundHit, Vector3 groundPoint)
	{
		if (!groundHit || !Grounded)
		{
			return;
		}
		float velUp = Hips.Vel.Dot(up);
		Hips.Vel += up * (velUp * (HipVelYDamp - 1f));
		float err = (groundPoint + up * HipRideHeight - Hips.Pos).Dot(up);
		Hips.Vel += up * (err * HipLiftGain);
	}

	/// <summary>推进（≙ 对胸+髋沿路径方向施力 0.8*MovementSpeed）：两 chunk 各按 MaxMoveSpeed
	/// 余量钳制注入。头不参与推进（RW 同）。不按抓地腿数缩放——腿不是人形的引擎。</summary>
	private void ApplyLocomotionForce(Vector3 effMove)
	{
		if (effMove == Vector3.Zero)
		{
			return;
		}
		float frameSpeed = BaseSpeed * RunSpeed;
		float chestRoom = MaxMoveSpeed - Chest.Vel.Dot(effMove);
		if (chestRoom > 0f)
		{
			Chest.Vel += effMove * Mathf.Min(frameSpeed, chestRoom);
		}
		float hipsRoom = MaxMoveSpeed - Hips.Vel.Dot(effMove);
		if (hipsRoom > 0f)
		{
			Hips.Vel += effMove * Mathf.Min(frameSpeed, hipsRoom);
		}
	}

	/// <summary>撑点维护 + 腾空俯仰泵（≙ Scavenger knucklePos 块 + L2312 的 WeightedPush(1,0,…)）：
	/// 撑点 = 胸前探地射线的命中点，落到合法窗外即失效重找（plant-and-trail 的手版——
	/// 撑点踩住不动，身体走过去，落后了再往前找新点）；胸髋双双腾空（奔跑腾跃相）时按
	/// 「撑点还有多远」在髋/胸间泵水平力偶：远 → 髋后仰伸手够，近 → 髋前甩蹬地过身。</summary>
	private void UpdateKnuckle(in TickContext ctx, Vector3 up, Vector3 effMove)
	{
		if (effMove == Vector3.Zero)
		{
			KnucklePos = null;
			return;
		}

		if (KnucklePos is { } existing)
		{
			float along = (existing - Chest.Pos).Dot(Facing);
			if (along < -KnuckleBehindWindow || along > KnuckleAheadWindow)
			{
				KnucklePos = null;
			}
		}
		if (KnucklePos is null)
		{
			Vector3 probe = Chest.Pos + Facing * (ArmLength * KnuckleLeadFac);
			if (ctx.Terrain.Raycast(probe + up * 0.35f, probe - up * KnuckleProbeDepth, out TerrainHit hit)
				&& hit.Normal.LengthSquared() > 1e-12f
				&& hit.Normal.Dot(up) > 0.5f)
			{
				KnucklePos = hit.Point;
			}
		}

		// 腾空俯仰泵：RW 原门是「胸髋双双无地面接触」= 真弹道腾空（走路不点火，跳跃/被击飞才有）。
		// 曾错用「双脚都没抓稳」当等价物——双足步态每步都有双摆相，泵在正常行走中 60% 时间点火，
		// 且撑点一落到胸后（无符号距离≈近端 → t≈1）就恒输出「髋前甩/胸后压」，把行走姿态整体
		// 掀成 ~40° 后仰 + 21 tick 周期摆（用户白盒实测暴露）。本内核「有地面支撑」的语义载体
		// 是近地探针（Grounded），不是 chunk 接触——门直接用 !Grounded。
		if (KnucklePos is { } kp && !Grounded)
		{
			Vector3 d = kp - Chest.Pos;
			Vector3 dh = d - up * d.Dot(up);
			float t = SaturatedInverseLerp(KnucklePumpFar, KnucklePumpNear, dh.Length());
			float fwdSpeed = (Chest.Vel + Hips.Vel).Dot(Facing) * 0.5f;
			float pump = Mathf.Lerp(KnucklePumpMin, KnucklePumpMax,
				SaturatedInverseLerp(0f, MaxMoveSpeed, fwdSpeed));
			WeightedPush(Hips, Chest, Facing * Mathf.Lerp(-1f, 1f, t), pump);
		}
	}

	/// <summary>蓄力/出手计时推进 + 出手头甩（≙ ThrowAnimation 前 10 帧 chunk2.vel += DirVec·3px）。</summary>
	private void UpdateThrowState()
	{
		if (ThrowChargeTicks > 0)
		{
			ThrowChargeTicks++;
		}
		if (ThrowAnimTicks > 0)
		{
			if (Conscious && ThrowAnimTicks > ThrowAnimLength - 10)
			{
				Head.Vel += ThrowDir * ThrowHeadFling;
			}
			ThrowAnimTicks--;
		}
	}

	/// <summary>逐 chunk 各向异性阻尼（≙ Act L2203：胸 0.9 惯性最大 / 髋头 0.8）。昏迷时跳过——
	/// 只剩 Body.AirFriction(0.999) 的近零风阻，瘫倒的身体像死物一样滑。</summary>
	private void ApplyChunkDamping()
	{
		if (!Conscious)
		{
			return;
		}
		Chest.Vel *= ChestDamping;
		Hips.Vel *= HipsDamping;
		Head.Vel *= HeadDamping;
	}

	/// <summary>固定顺序更新双腿并重算抓地数。stepDir = 水平朝向与意图的混合（人形躯干轴竖直，
	/// 不能像蜥蜴那样拿脊柱 Rotation 当步向）；up 恒传世界上——Limb 的爬墙分支静态死路。
	/// 蓄力瞄准（effMove 被清零）时按无意图驱动：身体停驶，腿也不该原地倒腾。</summary>
	private void TickLegs(in TickContext ctx, Vector3 up, Vector3 effMove)
	{
		Vector3 aim = effMove == Vector3.Zero ? Facing : effMove;
		Vector3 stepDir = Facing.Lerp(aim, SteerBlend);
		stepDir = stepDir.LengthSquared() < 1e-8f ? Facing : stepDir.Normalized();
		float runSpeed = Conscious && effMove != Vector3.Zero ? RunSpeed : 0f;

		foreach (Limb leg in Legs)
		{
			leg.Tick(ctx, stepDir, up, Legs, SmoothGait, runSpeed);
		}

		int gripping = 0;
		foreach (Limb leg in Legs)
		{
			if (leg.Gripping)
			{
				gripping++;
			}
		}
		LegsGripping = gripping;
	}

	/// <summary>
	/// 手臂优先级链（≙ ScavengerHand.Update 的扁平 if-else 链）：对每只手按 [主手, 副手] 固定序，
	/// 从高到低取第一条命中的分支写 Mode/HuntPos（及单帧速度覆盖），然后 Arm.Tick 统一做
	/// 臂长约束/腋窝排斥/出地形。加新动作 = 插一条分支，不触碰移动逻辑。
	/// </summary>
	private void TickArms(in TickContext ctx, Vector3 up, Vector3 effMove)
	{
		Vector3 right = Facing.Cross(up);
		right = right.LengthSquared() < 1e-8f ? Vector3.Right : right.Normalized();

		for (int i = 0; i < Arms.Count; i++)
		{
			Arm arm = Arms[i];
			bool mainHand = i == 0;

			if (!Conscious)
			{
				// 昏迷：双臂垂落（≙ !Consious → Dangle）。
				arm.GrabPos = null;
				arm.Mode = Arm.ArmMode.Dangle;
			}
			else if (mainHand && ThrowAnimTicks > 0)
			{
				// 出手：主手甩向掷向满伸展；前 5 tick 大步长 = 吸附瞬移感（≙ age<5 直接 pos=target）。
				arm.GrabPos = null;
				arm.Mode = Arm.ArmMode.HuntAbsolutePosition;
				arm.HuntPos = Chest.Pos + ThrowDir * arm.ArmLength;
				if (ThrowAnimTicks > ThrowAnimLength - 5)
				{
					arm.HuntSpeed = arm.ArmLength;
				}
			}
			else if (mainHand && ThrowChargeTicks > 0)
			{
				// 蓄力：主手拉到「反掷向与躯干上方向的球面插值」的身后高位 + sin 蓄力抖动
				// （≙ 胸 + Slerp2(-投掷方向, 躯干上, 0.6)·35px + sin(π/20·cycle) 抖动，抖动相位
				// 用蓄力计数驱动——确定性替代 RW 的随机 shake）。
				arm.GrabPos = null;
				arm.Mode = Arm.ArmMode.HuntAbsolutePosition;
				Vector3 back = ThrowDir.Cross(up).LengthSquared() < 1e-8f
					? up // 掷向与上共线的退化：直接举过头顶，固定回退保确定性
					: (-ThrowDir).Slerp(up, 0.6f);
				float wobble = Mathf.Sin(Mathf.Pi / 20f * ThrowChargeTicks) * 0.06f;
				arm.HuntPos = Chest.Pos + back * (arm.ArmLength * 0.7f) + up * wobble;
			}
			else if (mainHand && PointTarget is { } pt)
			{
				// 指向：主手沿「胸→目标」伸出，臂长 sin 伸缩（≙ 50+10·sin(cycle·0.3)；
				// RW 的神经质随机抖动换成 TickIndex 相位）。超出臂长的部分由链尾钳回。
				// TickIndex 先对 62832（≈3000×2π/0.3，long 取模精确）取模再转 float：
				// 直接转 float 在 ~7e6 tick（40tps 两天）后相位步长被 ulp 量化成台阶、
				// 2^24 tick 起相邻 tick 冻结同相位——长驻宿主的指向手会从呼吸变抽搐。
				arm.GrabPos = null;
				arm.Mode = Arm.ArmMode.HuntAbsolutePosition;
				float reach = arm.ArmLength
					* (1f + 0.2f * Mathf.Sin((float)(ctx.TickIndex % 62832L) * 0.3f));
				arm.HuntPos = Chest.Pos + Dir(Chest.Pos, pt) * reach;
			}
			else if (mainHand && Carrying)
			{
				// 持物：主手到躯干系携带位（≙ Slugcat 持物 relativeHuntPos (±20,-12)px 的 3D 化：
				// 本侧外撇 + 身前 + 微垂）。被持物由宿主硬钉到 MainHandPos。
				arm.GrabPos = null;
				arm.Mode = Arm.ArmMode.HuntRelativePosition;
				arm.HuntPos = Chest.Pos + right * (arm.Side * 0.35f) + Facing * 0.2f - up * 0.25f;
			}
			else if (effMove != Vector3.Zero)
			{
				// 行走 → 指关节撑地（≙ Run 分支）：有合法撑点就锁世界坐标（身体从上方走过），
				// 没有就垂摆 + 轮到本手时扇扫找点。每 tick 只许一只手扇扫（tick 奇偶交替）——
				// 射线预算与确定性双保。
				bool myTurn = (int)(ctx.TickIndex & 1) == i;
				if (arm.GrabPos is { } gp && !GrabPosLegal(gp, arm))
				{
					arm.GrabPos = null; // 超出可及圈（含速度预测）：丢弃（≙ !GrabPosLegal）
				}
				if (arm.GrabPos is { } gp2 && myTurn && KnucklePos is { } kp
					&& (gp2 - kp).Length() > KnuckleRegrabDist)
				{
					arm.GrabPos = null; // 撑点已远离行进参考 → 本手轮次重扫换点（≙ >40px 重找）
				}
				if (arm.GrabPos is { } lockPos)
				{
					arm.Mode = Arm.ArmMode.HuntAbsolutePosition;
					arm.HuntPos = lockPos;
				}
				else
				{
					arm.Mode = Arm.ArmMode.Dangle;
					if (myTurn)
					{
						TryFindGrabPos(ctx, up, right, arm);
					}
				}
			}
			else
			{
				// 闲置：慢速垂到身侧（≙ StandStill 的 huntSpeed*0.2 垂身侧探地——不打射线，
				// 休息位取幾何垂位；手够低时 PushOutOfTerrain 自然让指尖搭地）。
				arm.GrabPos = null;
				arm.HuntSpeed = arm.DefaultHuntSpeed * 0.2f;
				arm.Mode = Arm.ArmMode.HuntRelativePosition;
				arm.HuntPos = Chest.Pos + right * (arm.Side * (Chest.Radius + arm.Radius))
					+ Facing * 0.1f - up * (arm.ArmLength * 0.55f);
			}

			arm.Tick(ctx);
		}
	}

	/// <summary>撑点合法性（≙ GrabPosLegal）：手够得着（armLength×1.5），或 5 tick 速度预测后
	/// 够得着——高速跑动时允许手先伸向「将要到达」的位置。</summary>
	private bool GrabPosLegal(Vector3 p, Arm arm)
	{
		float reach = arm.ArmLength * 1.5f;
		return (p - Chest.Pos).Length() < reach
			|| (p - (Chest.Pos + Chest.Vel * 5f)).Length() < reach;
	}

	/// <summary>手的扇扫找点（≙ CheckForGrabPos）：以「胸→本手侧向错开的撑点目标」为中轴，
	/// 绕侧轴俯仰 ±25° 内固定序扫射线，首个合法命中即锁定。两手分离 = 目标点侧向错开
	/// （KnucklePlantSpread，左右分开）+ 每手 ±4° 俯仰偏置（KnuckleScanBiasDeg，前后交错）。</summary>
	private void TryFindGrabPos(in TickContext ctx, Vector3 up, Vector3 right, Arm arm)
	{
		if (KnucklePos is not { } kp)
		{
			return;
		}
		Vector3 origin = Chest.Pos + right * (arm.Side * Chest.Radius * 0.5f);
		Vector3 center = Dir(origin, kp + right * (arm.Side * KnucklePlantSpread));
		float reach = arm.ArmLength * 1.5f + Chest.Vel.Length() * 5f;
		foreach (float deg in KnuckleScanDegrees)
		{
			Vector3 dir = center.Rotated(right, Mathf.DegToRad(deg + arm.Side * KnuckleScanBiasDeg));
			if (!ctx.Terrain.Raycast(origin, origin + dir * reach, out TerrainHit hit))
			{
				continue;
			}
			// 零法线 = 起点已在地形内；朝下的悬垂底面不当撑面。
			if (hit.Normal.LengthSquared() < 1e-12f || hit.Normal.Dot(up) < -0.3f)
			{
				continue;
			}
			if (!GrabPosLegal(hit.Point, arm))
			{
				continue;
			}
			arm.GrabPos = hit.Point;
			KnucklePlants++;
			return;
		}
	}

	/// <summary>意图压平到水平面（人形只在地面走，竖直分量丢弃）。</summary>
	private static Vector3 FlattenToGround(Vector3 move, Vector3 up)
	{
		Vector3 flat = move - up * move.Dot(up);
		return flat.LengthSquared() < 1e-8f ? Vector3.Zero : flat.Normalized();
	}

	/// <summary>撞墙滑移：胸接触陡面时去掉意图里推入墙的分量（沿墙水平滑行）。
	/// 与蜥蜴 RedirectMove 的本质区别：不把受阻分量重定向成沿面上爬——人形不爬墙，
	/// 正对墙推就是停下（顶墙空转的刹车）。</summary>
	private Vector3 SlideAlongWalls(Vector3 move, Vector3 up)
	{
		if (move == Vector3.Zero
			|| (!Chest.TerrainContact && !Chest.HadContactLastTick)
			|| Chest.ContactNormal.LengthSquared() < 1e-12f)
		{
			return move;
		}
		Vector3 n = Chest.ContactNormal;
		if (Mathf.Abs(n.Dot(up)) >= 0.5f)
		{
			return move; // 地面/缓坡接触不算墙
		}
		Vector3 wall = n - up * n.Dot(up);
		if (wall.LengthSquared() < 1e-8f)
		{
			return move;
		}
		wall = wall.Normalized();
		float into = -move.Dot(wall);
		if (into <= 0.01f)
		{
			return move;
		}
		Vector3 slid = move + wall * into;
		return slid.LengthSquared() < 1e-6f ? Vector3.Zero : slid.Normalized();
	}

	private float ComputeUprightness(Vector3 up)
	{
		Vector3 axis = Chest.Pos - Hips.Pos;
		return axis.LengthSquared() < 1e-10f ? 0f : axis.Normalized().Dot(up);
	}

	private void UpdateHandObservables()
	{
		MainHandPos = Arms.Count > 0 ? Arms[0].Pos : Chest.Pos;
		if (ThrowChargeTicks > 0 || ThrowAnimTicks > 0)
		{
			MainHandDir = ThrowDir;
		}
		else if (PointTarget is { } pt)
		{
			MainHandDir = Dir(Chest.Pos, pt);
		}
		else
		{
			MainHandDir = Facing;
		}
	}

	/// <summary>按质量分配的等量反向力偶（≙ PhysicalObject.WeightedPush）：
	/// a 得 dir·frc·(mB/(mA+mB))，b 得反向余量——总动量不变，轻的一端动得多。
	/// dir 允许非单位向量（RW 的位置伺服就传钳制后的位移差当 dir）。</summary>
	private static void WeightedPush(BodyChunk a, BodyChunk b, Vector3 dir, float force)
	{
		float total = a.Mass + b.Mass;
		if (total <= 0f)
		{
			return;
		}
		float shareA = b.Mass / total;
		a.Vel += dir * (force * shareA);
		b.Vel -= dir * (force * (1f - shareA));
	}

	private static float SaturatedInverseLerp(float from, float to, float value) =>
		Mathf.Clamp(Mathf.InverseLerp(from, to, value), 0f, 1f);

	private static Vector3 Dir(Vector3 from, Vector3 to)
	{
		Vector3 d = to - from;
		// 两点重合时方向未定义——固定回退方向保确定性（与内核既有约定一致）。
		return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
	}
}
