using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Host;
using ProcAnim.Core.Physics;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Species.Lizard;

/// <summary>
/// 蜥蜴式运动控制器 = Body（chunk 物理）+ 若干 Limb（plant-and-trail 腿）+ 推进力。
/// 这是 Lizard 专属 locomotion backend，不是所有生物共用的通用行走控制器；未来蜈蚣、人形等
/// 应在共享 Body/地形原语之上实现各自并列的运动控制器。
/// 镜像 RW Lizard 的移动块：腿的抓握既是锚也是引擎——推进力 ∝ 抓地腿数
/// （frameSpeed = BaseSpeed · gripFac · RunSpeed），没腿抓地就几乎使不上劲。
///
/// M3 核心：走/爬无模式分支，全部由「支撑系」涌现（≙ RW §11.6b 重力开关）——
/// · SupportNormal = 抓地腿抓握面法线的平滑平均：平地=上，墙面=墙法线；
///   腿的落脚射线沿 -SupportNormal 打（走=朝下、爬=朝墙，同一条代码）。
/// · 站稳（FootingCounter 足够 且 有腿抓地）→ 重力关成 0：没有吸墙力，
///   是重力本身被开关——这就是身体不从墙上掉下来的全部原因。
/// · 移动意图被支撑面挡住的分量沿面内上坡方向重定向：推着墙走自然变成往上爬。
/// 无状态机：MoveDir/RunSpeed（或可选 MoveTarget）是输入，「抓住/没抓住」是唯一开关。
/// </summary>
public sealed class LizardLocomotionController
{
	public readonly Body Body;
	public readonly List<Limb> Limbs = new();

	/// <summary>头 = 施力主点（脊柱首）；髋 = 脊柱末。M4 起脊柱可多节，由工厂显式指定两端。</summary>
	public readonly BodyChunk Head;
	public readonly BodyChunk Hips;

	/// <summary>脊柱头后紧邻一节（≙ RW bodyChunks[1]）：与头共同承担推进追踪力的唯一另一点。
	/// 链尾（Hips）永远不直接追目标，只靠连接约束被动拖行（≙ RW bodyChunks[2] 从不直接追
	/// FollowConnection 目标）——多节脊柱下要是反过来直接拖 Hips，中间节没有任何驱动力，
	/// 两条独立刚性连接会在「头到髋」欠约束的自由度上折成 V 形（M4 遗留 bug，2026-07 修，
	/// 反编译 Lizard.cs 核实：p = vector2 + DirVec(vector2,bodyChunks[0].pos) *
	/// bodyChunkConnections[0].distance 驱动的是 bodyChunks[1]，偏移量是单节长度）。
	/// spine=2 时与 Hips 是同一个 chunk，退化为原双点驱动，行为不变。</summary>
	public readonly BodyChunk SpineFollower;

	/// <summary>头到 <see cref="SpineFollower"/> 的连接静止长度（米，≙ RW
	/// bodyChunkConnections[0].distance）：只算头到紧邻下一节的单节长度，不是脊柱全长。
	/// 推进拖尾点取头后（背离目标侧）此长度之半——见 ApplyLocomotionForce 内注释。</summary>
	public float HeadLinkLength = 0.3f;

	/// <summary>移动意图方向（单位向量或零向量）。由输入/AI 每 tick 写入；
	/// <see cref="MoveTarget"/> 非 null 时改由内核每 tick 导出（宿主写入被覆盖）。</summary>
	public Vector3 MoveDir;

	/// <summary>移动意图强度 ∈ [0,1]（≙ AI.runSpeed）。低于 <see cref="MoveIntentDeadzone"/>
	/// 一律视为零输入（见 HasMoveIntent）。</summary>
	public float RunSpeed;

	/// <summary>可选第三旋钮：宿主直喂的移动目标点（世界系，≙ RW 寻路器给 FollowConnection 的
	/// 下一路径格中心——RW 的原始形态，MoveDir 抽象是本项目的收缩）。非 null 时 MoveDir 每 tick
	/// 由内核从该点导出（宿主写入被覆盖；导出值 tick 末清零不跨 tick 残留——只清 null 不写
	/// MoveDir 即停，派生方向不冒充宿主意图）。到点基准 = 喂点沿 SupportNormal 抬
	/// RideHeight；实际推进胡萝卜跳过射线构造，并在意图顶住支撑面时沿面重定向为爬，
	/// 因而重定向窗口里不一定与到点基准重合。External 模式始终没有 Fallback 空中退化分支。
	/// 喂点与换点节奏归宿主（≙ RW 寻路器逐格递进）：**点必须是邻近的可达路径点**（导航网格/
	/// 路径采样，与头之间无墙阻隔——隔墙远点是契约违规，RW 同样走不过去）；
	/// <see cref="AtMoveTarget"/> 是到达信号——到点即视为无意图（停下/闲置涌现），
	/// 宿主应换下一点或清 null。RunSpeed 仍由宿主控制（油门不变）。
	/// 世界坐标语义：<see cref="Shift"/> 随世界平移它，<see cref="Teleport"/> 作废它（清 null）。</summary>
	public Vector3? MoveTarget;

	/// <summary>到点判定半径（米）：头到到点基准「喂点 + SupportNormal·RideHeight」的
	/// 3D 距离 ≤ 此值即到达。推进发生支撑系重定向时，实际胡萝卜可能暂时不与该基准重合。</summary>
	public float MoveTargetArriveRadius = 0.4f;

	/// <summary>MoveTarget 模式的到达信号（每 tick 更新；MoveTarget 为 null 时恒 false）。</summary>
	public bool AtMoveTarget { get; private set; }

	/// <summary>移动意图死区：RunSpeed ≤ 此值时推进/步态/顶死检测/闲置退出全部视为无输入。
	/// 曾经推进层用 &gt;0、腿层用 &gt;0.1 —— 0.05 的合法输入会推着一具收着腿（IdlePose
	/// 退不出来）的身体滑行（外部评审 P1-5）。唯一死区，所有层共用。</summary>
	public const float MoveIntentDeadzone = 0.1f;

	/// <summary>本 tick 是否存在有效移动意图——推进与步态的唯一开关。
	/// 方向驱动读宿主的 MoveDir；直喂驱动读「尚未到点」，不受 tick 末清掉派生 MoveDir 影响，
	/// 因此外部在 Tick 后读取时仍能得到真实的直喂运动状态。</summary>
	public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone
		&& (MoveTarget is not null ? !AtMoveTarget : MoveDir != Vector3.Zero);

	/// <summary>满抓地满速时每 tick 注入的速度（米/tick，≙ lizardParams.baseSpeed）。</summary>
	public float BaseSpeed = 0.06f;

	/// <summary>引擎极速（米/tick）：沿推进方向已有这个速度就不再注入。
	/// RW 瞄「下一个路径格」的伺服天然限速；我们的目标点是随头移动的胡萝卜，必须显式封顶——
	/// 平地上碰撞/腿阻先饱和（实测 ~0.03），贴墙无阻滑升时这是唯一的刹车。</summary>
	public float MaxMoveSpeed = 0.08f;

	/// <summary>零腿抓地时仍保留的推进比例（≙ noGripSpeed，防止完全瘫死）。</summary>
	public float NoGripSpeed = 0.15f;

	/// <summary>推进目标点在头前方的距离（米，≙ 瞄下一个路径格）。</summary>
	public float LookAhead = 0.5f;

	/// <summary>推进目标离支撑面的高度（米，≙ 路径格中心在地表上方半格 10px = 0.25m）。
	/// 目标钉在支撑面上：身体飘离面时头部力自动带回中分量——RW「不飘离墙」的真正来源。</summary>
	public float RideHeight = 0.25f;

	/// <summary>支撑面射线打空（悬崖边/探进墙里）时相对目标的上抬量（米，M2 的 floorLeverage 回退）。</summary>
	public float FloorLeverage = 0.15f;

	/// <summary>翻越窗口的比例回中增益（每 tick，≙ RW 攀爬时 vel -= (pos-格中心)*0.25 的回中力）：
	/// 头部越过目标点后力自动反向 = 刹车，把弹道过顶压成贴着棱线的弧线。只在翻越窗口生效。</summary>
	public float CrestCentering = 0.15f;

	/// <summary>翻越探测的向下射线深度（米）。必须显著大于普通支撑探测（0.65）：
	/// 头部冲过墙顶后这根射线是唯一还能看见顶面的目标源——太浅会过早退回
	/// 「头前+向上」的飞行胡萝卜，在支撑系旋转跟上之前把身体持续往天上推。</summary>
	public float CrestProbeDepth = 1.5f;

	/// <summary>链尾静息位回摆增益（每 tick 比例；仅 spine≥3 生效——spine=2 时链尾就是吃
	/// 拖尾点驱动的 SpineFollower，结构上排除，基线不动）。修的是 3D 化新增的自由度：
	/// RW 2D 侧视里「垂直于墙面伸出去」的方向不存在，链尾无驱动也不会悬臂；3D 里上墙
	/// 交接期髋悬在墙外 0.5~0.8m、重力已关、后腿超出可及圈、拉直力 &gt;120° 全休——悬臂
	/// 成了力中性姿态，只能靠爬行拖拽以 τ≈25 tick 被动衰减（heavy 弓起最长 1.5s 的主因）。
	/// 静息位 = 沿「头→SpineFollower」轴再延长到链尾当前半径处（纯姿态几何，零地形查询），
	/// 施力前显式去掉径向分量——只驱动绕 SpineFollower 的回摆，不压缩/拉伸链条。
	/// 两个被实测否决的方案：沿移动/目标轴直接拖链尾（重引多节 V 形，SpineFollower 修复轮）；
	/// 沿 -SupportNormal 朝最近墙面吸（停驶时无推进力抵消，把停在近直姿态的身体主动
	/// 折进墙角——夹角 174°→135° 锁死，2026-07 用户实测回归）。坠落态交还重力，不造抓地。</summary>
	public float RearBraceGain = 0.15f;

	/// <summary>Heavy 类「三节脊柱、无中段腿」上墙交接时的前段沿面回摆增益。
	/// Head 腿的射线落点或身体真接触先给出未经混合的局部墙法线，避开 SupportNormal
	/// 仍混着地面的滞后；SpineFollower 绕 Head 纯切向预转，Head 同步沿面走，绝不沿
	/// 法线吸墙或直接驱动 Hips。预摆在中铰到 120° 时停止追加伺服，前段进入沿墙 30°、全局支撑
	/// 接管、后段踩到同面或进入 Crest 后都会熄火；速度按 RunSpeed 缩放并以目标速度补差，
	/// 不逐 tick 累积冲量。中段有腿的拓扑会自行反馈局部支撑面，结构上排除。</summary>
	public float FrontMountGain = 0.35f;

	/// <summary>步进方向中移动意图对身体朝向的混合权重（≙ LizardLimb 的 0.4）。</summary>
	public float SteerBlend = 0.4f;

	/// <summary>是否按 gripCounter 协调多腿错开抬脚（≙ smoothenLegMovement）。</summary>
	public bool SmoothGait = true;

	// —— 站稳/重力开关参数（≙ Lizard applyGravity 块，数值直接取 RW）——
	/// <summary>坠落后需连续站稳这么多 tick 重力才重新关闭（≙ regainFootingCounter）。</summary>
	public int RegainFootingTicks = 15;

	/// <summary>连续这么多 tick 没有任何腿抓地 → 重力恢复（≙ NoGripCounter > 10）。</summary>
	public int LoseGripTicks = 10;

	public float FootedAirFriction = 0.8f;
	public float FootedSurfaceFriction = 0.5f;
	public float AirborneAirFriction = 0.999f;
	public float AirborneSurfaceFriction = 0.3f;

	/// <summary>支撑法线每 tick 向目标法线的插值系数（换面过渡的平滑度）。</summary>
	public float SupportBlend = 0.15f;

	/// <summary>悬空但没坠远时「支撑面就在附近」的探测余量（米，≙ RW 查相邻可达格）。</summary>
	public float NearTerrainRange = 0.4f;

	// —— 顶死解锁（≙ RW timeSpentTryingThisMove 的极简确定性版）——
	// 正面顶墙时身体静止 → 腿的 trail 松脚条件永远不满足 → 抓着矮处不换步 → 僵局。
	// RW 靠随机抖动+卡住升级打破；确定性内核没有随机逃生口，用显式超时换步替代。
	/// <summary>推着走却几乎不动，连续这么多 tick 后强制换一条腿。</summary>
	public int StallReleaseTicks = 20;

	/// <summary>头部速度低于此值（米/tick）视为「顶死」。行走均速 ~0.03。</summary>
	public float StallSpeed = 0.008f;

	/// <summary>顶死持续 tick 数（推着走且头部几乎不动）。</summary>
	public int StallTicks { get; private set; }

	/// <summary>持续拉直需求 ∈[0,1]（≙ RW Lizard.straightenOutNeeded）：深度误朝向会逐 tick
	/// 累积，姿态短暂越过阈值也不会立即丢失恢复力；无需求时平滑衰减。</summary>
	public float StraightenOutNeeded { get; private set; }

	/// <summary>局部脊柱卡角计数：不看仍在移动的头，而看髋部接触、低速与弯折的组合。
	/// 在后续 tick 提升拉直力，并驱动只缩地形接触半径的 terrainSqueeze 逃生口。</summary>
	public int SpineCornerStuckTicks { get; private set; }
	public int MaxSpineCornerStuckTicks { get; private set; }

	/// <summary>最近一次 180° 输入反转触发的确定性侧向转身剩余 tick。只绕支撑法线施力，
	/// 不引入显式走/爬状态。</summary>
	public int TurnAssistTicks { get; private set; }

	private Vector3 _lastIntentDir;
	private float _turnSign = 1f;

	/// <summary>当前抓地腿数（≙ legsGrabbing，每 tick 腿更新后重算）。</summary>
	public int LegsGripping { get; private set; }

	/// <summary>连续无任何腿抓地的 tick 数（≙ LizardGraphics.noGripCounter）。</summary>
	public int NoGripCounter { get; private set; }

	/// <summary>连续「站在/贴着可停留地形」的 tick 数，封顶 100（≙ inAllowedTerrainCounter）。</summary>
	public int FootingCounter { get; private set; }

	/// <summary>true = 重力在拽（坠落态摩擦）；false = 抓稳，重力 0（≙ applyGravity）。</summary>
	public bool ApplyGravity { get; private set; } = true;

	/// <summary>支撑系的「上」：抓地腿抓握面法线的平滑平均。平地=世界上，爬墙=墙法线。</summary>
	public Vector3 SupportNormal { get; private set; } = Vector3.Up;

	/// <summary>本 tick 推进目标（胡萝卜）的来源分支；None = 本 tick 没有推进。</summary>
	public MoveTargetKind LastMoveTargetKind { get; private set; }

	/// <summary>本 tick 的推进目标点（仅 <see cref="LastMoveTargetKind"/> ≠ None 时有效）。</summary>
	public Vector3 LastMoveTarget { get; private set; }

	public LizardLocomotionController(Body body, BodyChunk head, BodyChunk hips, BodyChunk spineFollower)
	{
		Body = body;
		Head = head;
		Hips = hips;
		SpineFollower = spineFollower;
	}

	/// <summary>rebase：身体、腿、腿的追逐目标、直喂目标整体平移，速度/抓握/站稳状态
	/// **原样保留**。只适用于「地形随你一起平移」的场景（浮点原点重置、整个世界搬家）——
	/// 地形不动的瞬移要用 <see cref="Teleport"/>：保留的抓握点会留在旧位置的空气里，
	/// 身体悬空关重力永久漂浮（终审 C8）。</summary>
	public void Shift(Vector3 delta)
	{
		Body.Shift(delta);
		foreach (Limb limb in Limbs)
		{
			limb.Shift(delta);
		}
		// MoveTarget/LastMoveTarget 是世界坐标：漏平移会让下 tick 朝旧原点里的目标走（评审 P1）。
		if (MoveTarget is { } fed)
		{
			MoveTarget = fed + delta;
		}
		LastMoveTarget += delta;
	}

	/// <summary>teleport：平移到新位置并作废一切位置态记忆——全腿强制松手（旧抓握点
	/// 对新位置无效）、站稳计数清零、直喂目标清 null（旧路径点对新位置同样无效，
	/// 宿主须按新位置重喂），落地后按常规 plant-and-trail 重建步态。
	/// 权威根瞬移/复位/换房用这个（评审 P1-7 + 终审 C8 的区分）。</summary>
	public void Teleport(Vector3 delta)
	{
		Shift(delta);
		foreach (Limb limb in Limbs)
		{
			limb.ForceRelease();
		}
		FootingCounter = 0;
		NoGripCounter = LoseGripTicks + 1;
		ResetRecoveryState();
		MoveTarget = null;
		AtMoveTarget = false;
		LastMoveTargetKind = MoveTargetKind.None;
	}

	/// <summary>宿主冲量注入（跳跃/击飞/弹射，≙ RW 被抛掷）：全 chunk 加同一速度增量
	/// （米/tick），全腿强制松手、站稳计数清零——重力当 tick 回归，身体进入弹道，
	/// 落地后按常规 plant-and-trail 恢复步态（--yank 回归验证的正是这条恢复路径）。</summary>
	public void Launch(Vector3 velocityPerTick)
	{
		foreach (BodyChunk c in Body.Chunks)
		{
			c.Vel += velocityPerTick;
		}
		foreach (Limb limb in Limbs)
		{
			limb.ForceRelease();
		}
		FootingCounter = 0;
		NoGripCounter = LoseGripTicks + 1;
		ResetRecoveryState();
	}

	/// <summary>
	/// 唯一入口：站稳判定 → 推进力 → 身体物理 → 腿 → 支撑系更新
	/// （腿在图形层语义上晚于物理，与 RW 帧序一致；支撑法线滞后一 tick 使用）。
	/// </summary>
	public void Tick(in TickContext ctx)
	{
		Vector3 up = ctx.GravityPerTick.LengthSquared() > 1e-12f
			? -ctx.GravityPerTick.Normalized()
			: Vector3.Up;

		bool derivedMove = MoveTarget is not null;
		if (MoveTarget is { } fed)
		{
			DeriveMoveFromTarget(fed);
		}
		else
		{
			AtMoveTarget = false;
		}
		UpdateFooting(ctx);
		UpdateTurnAssist();
		Vector3 effMove = HasMoveIntent ? RedirectMove(up) : Vector3.Zero;
		ApplyLocomotionForce(ctx, effMove, up);
		ApplyFrontMountAssist(up);
		ApplyRearBrace();
		UpdateTerrainSqueeze();
		Body.EnablePostCollisionStructureRecovery = SpineCornerStuckTicks >= 10;
		Body.Tick(ctx);
		StallTicks = HasMoveIntent && Head.Vel.Length() < StallSpeed ? StallTicks + 1 : 0;
		UpdateSpineCornerStuck();
		TickLimbs(ctx, effMove);
		UpdateSupportNormal(up);
		StraightenOutNeeded = LerpAndTick(StraightenOutNeeded, 0f, 0.1f, 0.05f);

		if (derivedMove)
		{
			// 派生 MoveDir 不跨 tick 残留：宿主只清 MoveTarget 不写 MoveDir 就该停——
			// 残留值会冒充宿主意图继续推着身体走。下个直喂 tick 会重新导出；
			// HasMoveIntent 在直喂模式读 AtMoveTarget，因此 tick 后观测仍如实。
			MoveDir = Vector3.Zero;
		}
	}

	/// <summary>MoveTarget 旋钮的意图导出（每 tick 覆盖 MoveDir，≙ RW FollowConnection 的
	/// DirVec(chunk, 格中心)——完整方向不压平，局部合法性由「喂邻近点」的契约保证）：
	/// 方向 = 头 → 到点基准（喂点 + SupportNormal·RideHeight）。指向墙内的分量仍由
	/// RedirectMove 重定向为沿面上爬——撞墙、上坡、翻越等下游涌现全部复用 MoveDir 通路。
	/// 到达 → 意图清零（停下涌现），AtMoveTarget 供宿主换点。
	/// 支撑法线沿用上 tick 终值（与腿/推进同一「滞后一 tick」约定）。</summary>
	private void DeriveMoveFromTarget(Vector3 target)
	{
		Vector3 carrot = target + SupportNormal * RideHeight;
		AtMoveTarget = (carrot - Head.Pos).Length() <= MoveTargetArriveRadius;
		MoveDir = AtMoveTarget ? Vector3.Zero : Dir(Head.Pos, carrot);
	}

	/// <summary>
	/// 站稳计数与重力开关（≙ Lizard ~1778 行）：
	/// applyGravity = 站稳不足 || 抓空过久。重力关时摩擦切「贴地档」（阻尼大，力快速到平衡，
	/// 身体不因失重漂移）；开时切「坠落档」（近乎无风阻）。
	/// </summary>
	private void UpdateFooting(in TickContext ctx)
	{
		bool anyGrip = false;
		bool anyContact = false;
		foreach (Limb limb in Limbs)
		{
			// 用真抓握（GripCounter ≥ GripDelay）而非「碰过一下」：棱边抖动时几条腿轮流
			// 只接触 1 tick 也能把 NoGripCounter 摁在 0——零真抓地却关重力（外部评审 P1-6）。
			anyGrip |= limb.Gripping;
			anyContact |= limb.TerrainContact;
		}
		foreach (BodyChunk c in Body.Chunks)
		{
			anyContact |= c.TerrainContact;
		}

		NoGripCounter = anyGrip ? 0 : NoGripCounter + 1;

		if (anyGrip || anyContact)
		{
			FootingCounter = Mathf.Min(FootingCounter + 1, 100);
		}
		else if (ctx.Terrain.Raycast(Hips.Pos,
					 Hips.Pos - SupportNormal * (Hips.TerrainRadius + NearTerrainRange), out _))
		{
			// 悬空但支撑面就在附近（迈步腾空/小颠簸）：缓扣不清零，免得重力闪开又闪关。
			FootingCounter = Mathf.Max(0, FootingCounter - 10);
		}
		else
		{
			FootingCounter = 0;
		}

		ApplyGravity = FootingCounter < RegainFootingTicks || NoGripCounter > LoseGripTicks;

		if (ApplyGravity)
		{
			Body.GravityScale = 1f;
			Body.AirFriction = AirborneAirFriction;
			Body.SurfaceFriction = AirborneSurfaceFriction;
		}
		else
		{
			Body.GravityScale = 0f;
			Body.AirFriction = FootedAirFriction;
			Body.SurfaceFriction = FootedSurfaceFriction;
		}
	}

	/// <summary>
	/// 把移动意图里被支撑面挡住的分量沿面内「上坡」方向重定向：
	/// 平地（意图不顶面）原样通过；斜坡上顶坡的分量变成沿坡上行；推着墙走变成竖直向上爬。
	/// 一条公式覆盖走/爬，没有模式分支（RW 靠寻路格给出同样的「往上」，这里用几何重定向替代）。
	/// 意图背离支撑面时不干预——身体被拉离墙、抓空、重力回归，「松手掉落」自然涌现。
	/// </summary>
	private Vector3 RedirectMove(Vector3 up)
	{
		if (MoveDir == Vector3.Zero)
		{
			return Vector3.Zero;
		}
		Vector3 n = SupportNormal;
		float into = -MoveDir.Dot(n);
		if (into <= 0.01f)
		{
			return MoveDir;
		}
		Vector3 upOnPlane = up - n * up.Dot(n);
		if (upOnPlane.LengthSquared() < 1e-6f)
		{
			return MoveDir; // 支撑面近乎水平：没有「坡上」可言
		}
		Vector3 redirected = MoveDir + n * into + upOnPlane.Normalized() * into;
		return redirected.LengthSquared() < 1e-6f ? MoveDir : redirected.Normalized();
	}

	/// <summary>
	/// 推进力（≙ Lizard.FollowConnection）：头朝目标点、SpineFollower（脊柱头后紧邻一节）朝
	/// 「头后（背离目标侧）半节长」的拖尾点，双点施力让身体前段沿移动方向拉直；
	/// 链尾（Hips）不直接受力，
	/// 靠连接约束被动拖行（≙ RW bodyChunks[2] 从不直接追 FollowConnection 目标——多节脊柱下
	/// 若改成直接拖链尾，中间节无驱动力，两条独立刚性连接会在欠约束的自由度上折成 V 形）。
	/// 力按抓地腿数缩放——腿是引擎。
	/// </summary>
	private void ApplyLocomotionForce(in TickContext ctx, Vector3 effMove, Vector3 up)
	{
		LastMoveTargetKind = MoveTargetKind.None;
		if (RunSpeed <= MoveIntentDeadzone || effMove == Vector3.Zero)
		{
			return;
		}

		float gripFac = Limbs.Count == 0
			? 1f
			: (float)LegsGripping / Limbs.Count * (1f - NoGripSpeed) + NoGripSpeed;
		float frameSpeed = BaseSpeed * gripFac * RunSpeed;

		Vector3 target = FindMoveTarget(ctx, effMove, up, out MoveTargetKind targetKind);
		LastMoveTarget = target;
		LastMoveTargetKind = targetKind;
		bool crest = targetKind == MoveTargetKind.Crest;
		Vector3 headDir = Dir(Head.Pos, target);
		// 注入量按剩余空间钳制：MaxMoveSpeed 是推进通道的真上限，不是「低于才加力」的
		// 软闸（旧版初速 0.079 + 满注入可到 0.139——名字承诺的上限形同虚设）。
		float headroom = MaxMoveSpeed - Head.Vel.Dot(headDir);
		if (headroom > 0f)
		{
			Head.Vel += headDir * Mathf.Min(frameSpeed, headroom);
		}
		if (crest)
		{
			// 翻越窗口：朝顶面目标的比例伺服（过冲即反向刹车），弹道过顶的唯一解药。
			Head.Vel += (target - Head.Pos) * CrestCentering;
		}

		// 拖尾点必须在头的「背离目标」一侧：follower 的注入是方向性定幅（不随距离缩放），
		// 且刚性连接把 follower 锁在头周围一节长的球面上——吸引子落在球面内哪一侧，哪一侧
		// 的极点就是稳定平衡。旧式 target + Dir(target→Head)·节长 在 LookAhead(0.5) > 节长(0.3)
		// 时恒在头前 ~0.2m：稳定位变成「髋在头前」，我们要的拖尾位是反平衡点，只靠头的运动
		// 拖行（~v/L）压制；头一顶死（墙角/顶死换步窗口）失稳泵独大，1cm 侧偏 40 tick 指数
		// 摆到 ~80° 并锁死成横向滑移步态（wall_pose 失稳，2026-07）。挪到头后半节长：顶死时
		// 髋自己垂回拖尾位，正常拖尾姿态下 followerDir 几乎不变。系数不取 1.0——那会让拖尾
		// 点恰落在 follower 静息位上，两点重合时 Dir 退化回固定 Up。
		Vector3 trail = Head.Pos + Dir(target, Head.Pos) * (HeadLinkLength * 0.5f);
		Vector3 followerDir = Dir(SpineFollower.Pos, trail);
		// 身体折叠（follower 看目标与看拖尾点方向相反）时衰减力，防止两点对拉（≙ RW 的 dot LerpMap）。
		float fold = Mathf.Remap(Dir(SpineFollower.Pos, target).Dot(followerDir), -1f, 1f, 0.5f, 1f);
		float followerHeadroom = MaxMoveSpeed - SpineFollower.Vel.Dot(followerDir);
		if (followerHeadroom > 0f)
		{
			SpineFollower.Vel += followerDir * Mathf.Min(frameSpeed * fold, followerHeadroom);
		}

		// 拉直（≙ RW Lizard.cs:2283-2293）：目标误朝向负责把身体前段转回目标；局部 V 折叠
		// 另沿 Head-Hips 弦向撑开，不能也沿目标轴推——180° 掉头时后者会让头、髋试图从彼此
		// 中间穿过去。StraightenOutNeeded 保留跨 tick 的恢复需求；SpineCornerStuckTicks 即使头仍
		// 在墙上移动，也能按局部髋部卡角持续升级，而不是被只看 Head.Vel 的 StallTicks 清零。
		float misTarget = SaturatedInverseLerp(0f, -1f,
			Dir(Head.Pos, target).Dot(Dir(SpineFollower.Pos, Head.Pos)));
		if (Hips == SpineFollower)
		{
			// 双点脊柱没有局部 V 折叠自由度，严格保留既有 RW 对照路径与确定性基线。
			float legacyMis = misTarget * Mathf.Max(0.2f,
				SaturatedInverseLerp(5f, 20f, StallTicks));
			if (legacyMis > 0f)
			{
				Vector3 straight = Dir(SpineFollower.Pos, target);
				Head.Vel += straight * (legacyMis * 2f * frameSpeed);
				SpineFollower.Vel -= straight * (legacyMis * frameSpeed);
			}
			return;
		}

		// 本地折叠触发：从 SpineFollower 看 Head 与 Hips 是否落到同侧（<120° 开始介入）。
		// RW 的六足 Caramel/SpitLizard 同样把三对腿锚在 chunk 0/1/2；腿粒子本身不向身体回传
		// 反力，因此这里处理的是身体局部几何，不是假设「第三腿对把髋钉住」。
		float misLocal = 0f;
		if (Hips != SpineFollower)
		{
			float side = Dir(SpineFollower.Pos, Head.Pos).Dot(Dir(SpineFollower.Pos, Hips.Pos));
			if (side > -0.5f)
			{
				misLocal = SaturatedInverseLerp(-0.5f, 1f, side);
			}
		}

		float rawMis = Mathf.Max(misTarget, misLocal);
		if (rawMis > 0.5f)
		{
			StraightenOutNeeded = LerpAndTick(StraightenOutNeeded, 1f, 0.1f, 0.1f);
		}

		int attemptTicks = Mathf.Max(StallTicks, SpineCornerStuckTicks);
		float escalation = SaturatedInverseLerp(5f, 20f, attemptTicks);
		float forceFull = SaturatedInverseLerp(20f, 40f, attemptTicks);
		float gain = Mathf.Max(0.2f, Mathf.Max(escalation, StraightenOutNeeded));
		// 正常推进仍由腿当引擎；姿态恢复随持续需求逐步摆脱抓地衰减，否则尾链松开后正是
		// 最缺抓地的窗口，frameSpeed 只有正常值约十分之一，永远来不及在再次撞墙前展开。
		float recoverySpeed = Mathf.Lerp(frameSpeed, BaseSpeed * RunSpeed,
			Mathf.Max(StraightenOutNeeded, escalation));

		float targetMis = Mathf.Lerp(misTarget, 1f, forceFull) * gain;
		if (targetMis > 0f)
		{
			Vector3 straight = Dir(SpineFollower.Pos, target);
			Head.Vel += straight * (targetMis * 2f * recoverySpeed);
			SpineFollower.Vel -= straight * (targetMis * recoverySpeed);
			// spine=2 时 SpineFollower 与 Hips 是同一 chunk：再减一次会把修正力翻倍、
			// 偏离 spine=2 的既有基线——只在链尾是独立第三节时才追加这一推（≙ RW bodyChunks[2]）。
			if (Hips != SpineFollower)
			{
				Hips.Vel -= straight * (targetMis * recoverySpeed);
			}
		}

		float localForce = Mathf.Lerp(misLocal, 1f, forceFull) * gain;
		if (localForce > 0f && Hips != SpineFollower)
		{
			Vector3 chord = Head.Pos - Hips.Pos;
			Vector3 open = chord.LengthSquared() > 1e-10f
				? chord.Normalized()
				: StableTurnTangent(up, effMove);
			Head.Vel += open * (localForce * recoverySpeed);
			Hips.Vel -= open * (localForce * recoverySpeed);
		}

		Vector3 turnUp = SupportNormal.LengthSquared() > 1e-10f
			? SupportNormal.Normalized()
			: up;
		ApplyTurnAssist(effMove, turnUp, recoverySpeed);
	}

	/// <summary>链尾静息位回摆（见 <see cref="RearBraceGain"/>）：抓稳期把链尾朝
	/// 「SpineFollower + 头→SpineFollower 轴 × 链尾当前半径」的直脊柱静息位回摆。
	/// 力先去径向（绕 SpineFollower 的纯切向）——径向分量要么被刚性连接吃掉、要么
	/// 变成压链折叠压力，都不产生回摆；对齐后 delta≈0 自然熄火。悬臂态它给出爬行
	/// 拖拽本来要等 τ≈25 tick 才给的下摆；上墙后停驶时前段轴指哪就摆到哪，不会
	/// 无中生有把身体拉向墙。注入沿用 MaxMoveSpeed 余量钳制。零地形查询、零射线。</summary>
	private void ApplyRearBrace()
	{
		if (Hips == SpineFollower || ApplyGravity)
		{
			return;
		}
		// 注：曾试过翻越窗口（Crest）豁免——直觉是「翻越期脊柱本该绕棱线弯，回摆在对抗它」，
		// 实测反向：回摆把链尾持续送向棱线，正是翻越吞吐的助力（豁免后 wall-heavy 翻越
		// 8→6、heavy 巡逻 8→6，巡逻路线含翻越段所以平地配置一并受损）。不设窗口分支。
		Vector3 axisRaw = SpineFollower.Pos - Head.Pos;
		if (axisRaw.LengthSquared() < 1e-8f)
		{
			return; // 头与中段近重合：轴未定义，本 tick 不施力（约束松弛会先拉开）
		}
		Vector3 rest = SpineFollower.Pos
			+ axisRaw.Normalized() * (Hips.Pos - SpineFollower.Pos).Length();
		Vector3 delta = rest - Hips.Pos;
		Vector3 radial = Dir(SpineFollower.Pos, Hips.Pos);
		delta -= radial * delta.Dot(radial);
		float mag = delta.Length();
		if (mag < 1e-6f)
		{
			return;
		}
		Vector3 dir = delta / mag;
		float headroom = MaxMoveSpeed - Hips.Vel.Dot(dir);
		if (headroom > 0f)
		{
			Hips.Vel += dir * Mathf.Min(mag * RearBraceGain, headroom);
		}
	}

	/// <summary>上墙交接的前段局部姿态伺服（见 <see cref="FrontMountGain"/>）。
	/// 完全由本 tick 的射线落点/接触拓扑与几何门派生，不新增 locomotion 状态机：
	/// Head 正在伸向陡面且原始意图正推入该面时预转，真接触后可继续；局部法线绕开
	/// SupportNormal 的地/墙混合低通，后段/全局支撑接管后立即结束。</summary>
	private void ApplyFrontMountAssist(Vector3 worldUp)
	{
		if (FrontMountGain <= 0f || Hips == SpineFollower || !HasMoveIntent
			|| LastMoveTargetKind == MoveTargetKind.Crest || MoveDir.LengthSquared() < 1e-10f)
		{
			return;
		}
		foreach (Limb limb in Limbs)
		{
			if (limb.Anchor == SpineFollower)
			{
				return; // 中段有腿的拓扑会自行把局部支撑面喂给 SupportNormal，不属于 Heavy 空窗
			}
		}

		Vector3 headNormalSum = Vector3.Zero;
		int headSurfaceCount = 0;
		bool headHasRealSurfaceContact = false;
		foreach (Limb limb in Limbs)
		{
			bool backedWallTarget = limb.GripCounter > 0
				|| (limb.ReachingForTerrain && limb.HasGrip);
			if (limb.Anchor != Head || !backedWallTarget
				|| limb.GripNormal.LengthSquared() < 1e-10f)
			{
				continue;
			}
			Vector3 candidate = limb.GripNormal.Normalized();
			float upDot = candidate.Dot(worldUp);
			// 只接陡墙/陡坡：排除地面与悬垂底面。HasGrip 是本 tick 射线背书的真实地形
			// 落点；允许脚尚在伸过去时预转姿态，但不改 GripCounter/Gripping，不造抓地。
			if (upDot < -0.3f || upDot >= 0.5f)
			{
				continue;
			}
			headNormalSum += candidate;
			headSurfaceCount++;
			headHasRealSurfaceContact |= limb.GripCounter > 0;
		}
		// 正撞墙时两条 Head 腿可能同 tick 换步；只靠 GripCounter 会在最需要转向的几 tick
		// 断电。身体球的碰撞法线是同样的真实地形证据，可在腿接触暂缺时补位；必须与
		// TerrainContact 配套读取，绝不缓存上一段墙面的过期法线。
		if (headSurfaceCount == 0 && Head.TerrainContact
			&& Head.ContactNormal.LengthSquared() >= 1e-10f)
		{
			Vector3 candidate = Head.ContactNormal.Normalized();
			float upDot = candidate.Dot(worldUp);
			if (upDot >= -0.3f && upDot < 0.5f)
			{
				headNormalSum = candidate;
				headSurfaceCount = 1;
				headHasRealSurfaceContact = true;
			}
		}
		if (headSurfaceCount == 0 || headNormalSum.LengthSquared() < 1e-8f)
		{
			return;
		}
		Vector3 localNormal = headNormalSum.Normalized();
		if (!headHasRealSurfaceContact && Head.TerrainContact
			&& Head.ContactNormal.LengthSquared() >= 1e-10f)
		{
			Vector3 bodyNormal = Head.ContactNormal.Normalized();
			float bodyUpDot = bodyNormal.Dot(worldUp);
			headHasRealSurfaceContact = bodyUpDot >= -0.3f && bodyUpDot < 0.5f
				&& bodyNormal.Dot(localNormal) > 0.85f;
		}
		if (SupportNormal.LengthSquared() > 1e-10f
			&& SupportNormal.Normalized().Dot(localNormal) >= 0.8f)
		{
			return; // 全局支撑已接管该墙面：成熟爬墙换步不得重新点火
		}

		Vector3 rawMove = MoveDir.Normalized();
		float into = -rawMove.Dot(localNormal);
		if (into <= 0.1f)
		{
			return; // 已沿面行走/掉头不属于「正面推墙上墙」交接
		}

		foreach (Limb limb in Limbs)
		{
			if (limb.Anchor == Head || limb.GripCounter <= 0
				|| limb.GripNormal.LengthSquared() < 1e-10f)
			{
				continue;
			}
			float alignment = limb.GripNormal.Normalized().Dot(localNormal);
			if (alignment > 0.85f)
			{
				return; // 后段已踩到同一面：全局支撑与 RearBrace 足以接管
			}
		}

		Vector3 climb = rawMove + localNormal * into;
		Vector3 upOnFace = worldUp - localNormal * worldUp.Dot(localNormal);
		if (upOnFace.LengthSquared() > 1e-8f)
		{
			climb += upOnFace.Normalized() * into;
		}
		climb -= localNormal * climb.Dot(localNormal);
		if (climb.LengthSquared() < 1e-8f)
		{
			return;
		}
		climb = climb.Normalized();

		Vector3 radial = SpineFollower.Pos - Head.Pos;
		float radius = radial.Length();
		if (radius < 1e-6f)
		{
			return;
		}
		radial /= radius;
		if (!headHasRealSurfaceContact)
		{
			Vector3 toHips = Hips.Pos - SpineFollower.Pos;
			if (toHips.LengthSquared() >= 1e-10f
				&& (-radial).Dot(toHips.Normalized()) >= -0.5f)
			{
				return; // 预摆最多到 120°；真正踩墙前不能先把中铰压成深 L
			}
		}
		// 目的只是把前段从墙法向转入沿面趋势；达到 30° 后立即熄火。这样成熟爬墙时即使
		// 后腿同步抬起，也不会把正常的沿墙前段重复当成交接姿态施力。
		if (-radial.Dot(climb) >= 0.8660254f)
		{
			return;
		}
		Vector3 rest = Head.Pos - climb * radius;
		Vector3 delta = rest - SpineFollower.Pos;
		delta -= radial * delta.Dot(radial); // 只绕 Head 回摆，不压缩/拉伸第一节
		float mag = delta.Length();
		if (mag < 1e-6f)
		{
			return;
		}

		Vector3 followerDir = delta / mag;
		float throttle = Mathf.Clamp(RunSpeed, 0f, 1f);
		float correction = Mathf.Min(mag * FrontMountGain, MaxMoveSpeed) * throttle;
		// 这是目标方向速度，不是每 tick 固定加速度：只补当前缺口可防止交接末端角速度累积过冲。
		const float Share = 0.75f;
		float desiredShareSpeed = correction * Share;
		float followerHeadroom = MaxMoveSpeed - SpineFollower.Vel.Dot(followerDir);
		float followerMissing = desiredShareSpeed - SpineFollower.Vel.Dot(followerDir);
		if (followerHeadroom > 0f && followerMissing > 0f)
		{
			SpineFollower.Vel += followerDir * Mathf.Min(followerMissing, followerHeadroom);
		}
		float headHeadroom = MaxMoveSpeed - Head.Vel.Dot(climb);
		float headMissing = desiredShareSpeed - Head.Vel.Dot(climb);
		if (headHeadroom > 0f && headMissing > 0f)
		{
			Head.Vel += climb * Mathf.Min(headMissing, headHeadroom);
		}
	}

	private void UpdateTurnAssist()
	{
		if (!HasMoveIntent || Hips == SpineFollower || MoveDir.LengthSquared() < 1e-10f)
		{
			TurnAssistTicks = 0;
			_lastIntentDir = Vector3.Zero;
			return;
		}

		Vector3 intent = MoveDir.Normalized();
		if (_lastIntentDir.LengthSquared() > 1e-10f && intent.Dot(_lastIntentDir) < -0.8f)
		{
			// 180° 时左右两条转弯路径完全等价；固定交替手性打破平面对称，既确定又不长期偏一侧。
			_turnSign = -_turnSign;
			TurnAssistTicks = 16;
		}
		_lastIntentDir = intent;
	}

	private void ApplyTurnAssist(Vector3 desired, Vector3 up, float recoverySpeed)
	{
		if (TurnAssistTicks <= 0 || desired.LengthSquared() < 1e-10f)
		{
			return;
		}

		Vector3 forward = Head.Pos - Hips.Pos;
		forward -= up * forward.Dot(up);
		Vector3 target = desired - up * desired.Dot(up);
		if (forward.LengthSquared() < 1e-10f || target.LengthSquared() < 1e-10f)
		{
			TurnAssistTicks--;
			return;
		}
		forward = forward.Normalized();
		target = target.Normalized();

		float signed = up.Dot(forward.Cross(target));
		if (Mathf.Abs(signed) > 0.02f)
		{
			_turnSign = Mathf.Sign(signed);
		}
		Vector3 turn = up.Cross(forward) * _turnSign;
		if (turn.LengthSquared() < 1e-10f)
		{
			turn = StableTurnTangent(up, target);
		}
		else
		{
			turn = turn.Normalized();
		}

		float turnSpeed = Mathf.Max(recoverySpeed * 0.35f, BaseSpeed * RunSpeed * 0.25f);
		Head.Vel += turn * turnSpeed;
		SpineFollower.Vel -= turn * (turnSpeed * 0.5f);
		if (Hips != SpineFollower)
		{
			Hips.Vel -= turn * (turnSpeed * 0.5f);
		}
		TurnAssistTicks--;
	}

	private Vector3 StableTurnTangent(Vector3 up, Vector3 desired)
	{
		Vector3 basis = desired - up * desired.Dot(up);
		if (basis.LengthSquared() < 1e-10f)
		{
			Vector3 fallback = Mathf.Abs(up.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
			basis = fallback - up * fallback.Dot(up);
		}
		Vector3 tangent = up.Cross(basis.Normalized());
		return tangent.LengthSquared() < 1e-10f
			? Vector3.Right * _turnSign
			: tangent.Normalized() * _turnSign;
	}

	private void UpdateSpineCornerStuck()
	{
		if (!HasMoveIntent || Hips == SpineFollower)
		{
			SpineCornerStuckTicks = 0;
			return;
		}

		Vector3 toHead = Head.Pos - SpineFollower.Pos;
		Vector3 toHips = Hips.Pos - SpineFollower.Pos;
		if (toHead.LengthSquared() < 1e-10f || toHips.LengthSquared() < 1e-10f)
		{
			SpineCornerStuckTicks = Mathf.Max(0, SpineCornerStuckTicks - 2);
			return;
		}

		float side = toHead.Normalized().Dot(toHips.Normalized());
		float bendThreshold = SpineCornerStuckTicks > 0 ? -0.5f : -0.17364818f; // 120° / 100°
		bool contact = Hips.TerrainContact || Hips.HadContactLastTick;
		bool pinned = Hips.Vel.Length() < StallSpeed;
		if (contact && pinned && side > bendThreshold)
		{
			SpineCornerStuckTicks = Mathf.Min(600, SpineCornerStuckTicks + 1);
			MaxSpineCornerStuckTicks = Mathf.Max(MaxSpineCornerStuckTicks, SpineCornerStuckTicks);
		}
		else
		{
			SpineCornerStuckTicks = Mathf.Max(0, SpineCornerStuckTicks - 2);
		}
	}

	private void UpdateTerrainSqueeze()
	{
		if (Hips == SpineFollower)
		{
			return;
		}
		if (!HasMoveIntent)
		{
			SpineCornerStuckTicks = 0;
		}
		Hips.TerrainSqueeze = Mathf.Lerp(1f, 0.05f,
			SaturatedInverseLerp(10f, 30f, SpineCornerStuckTicks));
	}

	private void ResetRecoveryState()
	{
		StallTicks = 0;
		StraightenOutNeeded = 0f;
		SpineCornerStuckTicks = 0;
		MaxSpineCornerStuckTicks = 0;
		Hips.TerrainSqueeze = 1f;
		Body.EnablePostCollisionStructureRecovery = false;
		TurnAssistTicks = 0;
		_lastIntentDir = Vector3.Zero;
		_turnSign = 1f;
	}

	/// <summary>Godot Mathf.InverseLerp 不会自行钳位；恢复计数可持续到 600 tick，所有用它
	/// 驱动的强度都必须显式限制在 [0,1]，否则 Lerp 会外推并随卡住时长放大。</summary>
	internal static float SaturatedInverseLerp(float from, float to, float value) =>
		Mathf.Clamp(Mathf.InverseLerp(from, to, value), 0f, 1f);

	private static float LerpAndTick(float value, float target, float lerp, float tick) =>
		Mathf.MoveToward(Mathf.Lerp(value, target, lerp), target, tick);

	/// <summary>
	/// 推进目标钉在支撑面上（≙ RW 瞄路径格中心——格中心天然贴着地形）：
	/// 头前 LookAhead 处沿 -SupportNormal 投影到面、抬 RideHeight。
	/// 攀爬中支撑向打空（头已越过棱线）→ 补世界向下探测，目标钉在顶面上
	/// （≙ RW 瞄墙顶那格）：头部力变成朝顶面的伺服，靠近自动减速——
	/// 否则退回相对胡萝卜会在支撑系旋转跟上之前把身体抛过墙顶（快速爬墙必摔的根因）。
	/// 两射线都打空/探进墙里（零法线）/朝下悬垂面 → 退回 M2 的头前相对目标。
	/// </summary>
	private Vector3 FindMoveTarget(in TickContext ctx, Vector3 effMove, Vector3 up, out MoveTargetKind kind)
	{
		if (MoveTarget is { } fed)
		{
			// 宿主直喂（≙ RW 瞄路径格中心）：到点基准 = 喂点沿支撑法线抬 RideHeight，零射线，
			// 因此 External 模式没有 Fallback 分支。实际胡萝卜沿 effMove 摆在「头到到点基准」
			// 的同距离处：意图未被支撑面挡时 effMove 就是头→基准方向，两者恒等；
			// 被挡时（喂点在墙根、身体已上墙）
			// 身体施力跟着重定向走——否则「撞墙涌现为爬」只传给腿，头髋仍朝墙里顶（评审 P2）。
			kind = MoveTargetKind.External;
			Vector3 fedCarrot = fed + SupportNormal * RideHeight;
			return Head.Pos + effMove * (fedCarrot - Head.Pos).Length();
		}
		Vector3 n = SupportNormal;
		Vector3 ahead = Head.Pos + effMove * LookAhead;
		if (ctx.Terrain.Raycast(ahead + n * 0.35f, ahead - n * 0.65f, out TerrainHit hit)
			&& hit.Normal.LengthSquared() > 1e-12f
			&& hit.Normal.Dot(up) > -0.3f)
		{
			kind = MoveTargetKind.Support;
			return hit.Point + n * RideHeight;
		}
		if (n.Dot(up) < 0.95f
			&& ctx.Terrain.Raycast(ahead + up * 0.35f, ahead - up * CrestProbeDepth, out TerrainHit topHit)
			&& topHit.Normal.LengthSquared() > 1e-12f
			&& topHit.Normal.Dot(up) > -0.3f)
		{
			kind = MoveTargetKind.Crest;
			return topHit.Point + up * RideHeight;
		}
		kind = MoveTargetKind.Fallback;
		return ahead + up * FloorLeverage;
	}

	/// <summary>固定顺序更新每条腿，然后重算抓地数（供下 tick 推进力与外部读取）。</summary>
	private void TickLimbs(in TickContext ctx, Vector3 effMove)
	{
		// 顶死解锁：强制抓得最久的那条腿松开重迈步（并列取列表序靠前者，保确定性）。
		// 新落点由上倾的 stepDir 引向更高处——正面顶墙从僵局变成棘轮式上爬。
		if (StallTicks >= StallReleaseTicks)
		{
			Limb? oldest = null;
			foreach (Limb limb in Limbs)
			{
				if (limb.GripCounter > (oldest?.GripCounter ?? 0))
				{
					oldest = limb;
				}
			}
			oldest?.ForceRelease();
			StallTicks = 0;
		}

		Vector3 forward = Dir(Hips.Pos, Head.Pos);
		Vector3 aim = effMove == Vector3.Zero ? forward : effMove;

		foreach (Limb limb in Limbs)
		{
			// 每锚点步进方向（≙ LizardLimb：a = DirVec(rotationChunk→connection)，再与目标
			// Lerp 0.4）：头/髋锚 = 脊柱长基线轴（与旧全局 stepDir 逐位一致——负号与除法在
			// IEEE 下可交换；重合退化时 Rotation 给零向量，下方回退 forward 与旧 Dir 的
			// 退化分支同源，翻转不会把回退值翻成朝下），中段锚（hexapod 中腿对）= 后段轴——
			// 脊柱弯着走时中段腿跟本段、不跟头髋弦。髋的 Rotation 指向后方，翻转回前
			// （≙ RW LizardLimb 的 connection.index==2 时 a *= -1，按锚点判定不写死索引）。
			Vector3 axis = limb.Anchor == Hips ? -limb.Anchor.Rotation : limb.Anchor.Rotation;
			if (axis.LengthSquared() < 1e-8f)
			{
				axis = forward;
			}
			Vector3 stepDir = axis.Lerp(aim, SteerBlend);
			stepDir = stepDir.LengthSquared() < 1e-8f ? forward : stepDir.Normalized();
			limb.Tick(ctx, stepDir, SupportNormal, Limbs, SmoothGait, RunSpeed);
		}

		int gripping = 0;
		foreach (Limb limb in Limbs)
		{
			if (limb.Gripping)
			{
				gripping++;
			}
		}
		LegsGripping = gripping;
	}

	/// <summary>
	/// 支撑法线 = 抓地腿抓握面法线的平滑平均。抓着墙 → 转向墙法线（腿的射线随之朝墙打）；
	/// 抓空过久 → 衰减回世界上方向（坠落后落脚射线自动恢复朝下）。
	/// 短暂全腿腾空维持现值——换步瞬间支撑系不抖。
	/// </summary>
	private void UpdateSupportNormal(Vector3 up)
	{
		Vector3 sum = Vector3.Zero;
		foreach (Limb limb in Limbs)
		{
			if (limb.GripCounter > 0)
			{
				sum += limb.GripNormal;
			}
		}

		Vector3 target;
		if (sum.LengthSquared() > 1e-6f)
		{
			target = sum.Normalized();
		}
		else if (NoGripCounter > LoseGripTicks)
		{
			target = up;
		}
		else
		{
			return;
		}

		Vector3 blended = SupportNormal.Lerp(target, SupportBlend);
		// 目标与现值几乎反向时插值可能过零——固定回退到目标本身保确定性。
		SupportNormal = blended.LengthSquared() < 1e-6f ? target : blended.Normalized();
	}

	private static Vector3 Dir(Vector3 from, Vector3 to)
	{
		Vector3 d = to - from;
		// 两点重合时方向未定义——固定回退方向保确定性。
		return d.LengthSquared() < 1e-12f ? Vector3.Up : d.Normalized();
	}
}
