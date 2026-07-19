using System.Collections.Generic;
using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 会走路的生物 = Body（chunk 物理）+ 若干 Limb（plant-and-trail 腿）+ 推进力。
/// 镜像 RW Lizard 的移动块：腿的抓握既是锚也是引擎——推进力 ∝ 抓地腿数
/// （frameSpeed = BaseSpeed · gripFac · RunSpeed），没腿抓地就几乎使不上劲。
///
/// M3 核心：走/爬无模式分支，全部由「支撑系」涌现（≙ RW §11.6b 重力开关）——
/// · SupportNormal = 抓地腿抓握面法线的平滑平均：平地=上，墙面=墙法线；
///   腿的落脚射线沿 -SupportNormal 打（走=朝下、爬=朝墙，同一条代码）。
/// · 站稳（FootingCounter 足够 且 有腿抓地）→ 重力关成 0：没有吸墙力，
///   是重力本身被开关——这就是身体不从墙上掉下来的全部原因。
/// · 移动意图被支撑面挡住的分量沿面内上坡方向重定向：推着墙走自然变成往上爬。
/// 无状态机：MoveDir/RunSpeed 是唯一输入，「抓住/没抓住」是唯一开关。
/// </summary>
public sealed class Walker
{
	public readonly Body Body;
	public readonly List<Limb> Limbs = new();

	/// <summary>头 = 施力主点（脊柱首）；髋 = 脊柱末。M4 起脊柱可多节，由工厂显式指定两端。</summary>
	public readonly BodyChunk Head;
	public readonly BodyChunk Hips;

	/// <summary>脊柱总长（米，= 头到髋各连接 RestLength 之和）：推进拖尾点在目标身后这个距离。</summary>
	public float SpineLength = 0.3f;

	/// <summary>移动意图方向（单位向量或零向量）。由输入/AI 每 tick 写入。</summary>
	public Vector3 MoveDir;

	/// <summary>移动意图强度 ∈ [0,1]（≙ AI.runSpeed）。低于 <see cref="MoveIntentDeadzone"/>
	/// 一律视为零输入（见 HasMoveIntent）。</summary>
	public float RunSpeed;

	/// <summary>移动意图死区：RunSpeed ≤ 此值时推进/步态/顶死检测/闲置退出全部视为无输入。
	/// 曾经推进层用 &gt;0、腿层用 &gt;0.1 —— 0.05 的合法输入会推着一具收着腿（IdlePose
	/// 退不出来）的身体滑行（外部评审 P1-5）。唯一死区，所有层共用。</summary>
	public const float MoveIntentDeadzone = 0.1f;

	/// <summary>本 tick 是否存在有效移动意图（死区之上且方向非零）——推进与步态的唯一开关。</summary>
	public bool HasMoveIntent => RunSpeed > MoveIntentDeadzone && MoveDir != Vector3.Zero;

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

	public Walker(Body body, BodyChunk head, BodyChunk hips)
	{
		Body = body;
		Head = head;
		Hips = hips;
	}

	/// <summary>宿主 tether 契约的 rebase/teleport 入口：身体、腿、腿的追逐目标整体平移，
	/// 速度/抓握/站稳状态原样保留。权威根瞬移或复位时调用——没有它，视觉身体只能以
	/// MaxMoveSpeed 横穿场景慢慢追根，或继续抓在原地（评审 P1-7）。</summary>
	public void Shift(Vector3 delta)
	{
		Body.Shift(delta);
		foreach (Limb limb in Limbs)
		{
			limb.Shift(delta);
		}
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

		UpdateFooting(ctx);
		Vector3 effMove = HasMoveIntent ? RedirectMove(up) : Vector3.Zero;
		ApplyLocomotionForce(ctx, effMove, up);
		Body.Tick(ctx);
		StallTicks = HasMoveIntent && Head.Vel.Length() < StallSpeed ? StallTicks + 1 : 0;
		TickLimbs(ctx, effMove);
		UpdateSupportNormal(up);
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
					 Hips.Pos - SupportNormal * (Hips.Radius + NearTerrainRange), out _))
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
	/// 推进力（≙ Lizard.FollowConnection）：头朝目标点、髋朝「目标点身后一节」，
	/// 双点施力让身体沿移动方向拉直。力按抓地腿数缩放——腿是引擎。
	/// </summary>
	private void ApplyLocomotionForce(in TickContext ctx, Vector3 effMove, Vector3 up)
	{
		if (RunSpeed <= MoveIntentDeadzone || effMove == Vector3.Zero)
		{
			return;
		}

		float gripFac = Limbs.Count == 0
			? 1f
			: (float)LegsGripping / Limbs.Count * (1f - NoGripSpeed) + NoGripSpeed;
		float frameSpeed = BaseSpeed * gripFac * RunSpeed;

		Vector3 target = FindMoveTarget(ctx, effMove, up, out bool crest);
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

		Vector3 trail = target + Dir(target, Head.Pos) * SpineLength;
		Vector3 hipsDir = Dir(Hips.Pos, trail);
		// 身体折叠（髋看目标与看拖尾点方向相反）时衰减髋部力，防止两点对拉（≙ RW 的 dot LerpMap）。
		float fold = Mathf.Remap(Dir(Hips.Pos, target).Dot(hipsDir), -1f, 1f, 0.5f, 1f);
		float hipsHeadroom = MaxMoveSpeed - Hips.Vel.Dot(hipsDir);
		if (hipsHeadroom > 0f)
		{
			Hips.Vel += hipsDir * Mathf.Min(frameSpeed * fold, hipsHeadroom);
		}

		// 拉直（≙ RW straightenOut）：身体轴线背对目标（正面撞墙翻倒、头折叠到髋后）时，
		// 头沿「髋→目标」强拉、髋反向推，把身体甩回朝向目标——卡越久（StallTicks）力越大。
		// 没有它，翻倒姿态会让 stepDir 被反向身体轴污染，腿全部背着目标迈步，永久瘫死。
		float mis = Mathf.InverseLerp(0f, -1f, Dir(Head.Pos, target).Dot(Dir(Hips.Pos, Head.Pos)));
		if (mis > 0f)
		{
			mis *= Mathf.Max(0.2f, Mathf.InverseLerp(5f, 20f, StallTicks));
			Vector3 straight = Dir(Hips.Pos, target);
			Head.Vel += straight * (mis * 2f * frameSpeed);
			Hips.Vel -= straight * (mis * frameSpeed);
		}
	}

	/// <summary>
	/// 推进目标钉在支撑面上（≙ RW 瞄路径格中心——格中心天然贴着地形）：
	/// 头前 LookAhead 处沿 -SupportNormal 投影到面、抬 RideHeight。
	/// 攀爬中支撑向打空（头已越过棱线）→ 补世界向下探测，目标钉在顶面上
	/// （≙ RW 瞄墙顶那格）：头部力变成朝顶面的伺服，靠近自动减速——
	/// 否则退回相对胡萝卜会在支撑系旋转跟上之前把身体抛过墙顶（快速爬墙必摔的根因）。
	/// 两射线都打空/探进墙里（零法线）/朝下悬垂面 → 退回 M2 的头前相对目标。
	/// </summary>
	private Vector3 FindMoveTarget(in TickContext ctx, Vector3 effMove, Vector3 up, out bool crest)
	{
		crest = false;
		Vector3 n = SupportNormal;
		Vector3 ahead = Head.Pos + effMove * LookAhead;
		if (ctx.Terrain.Raycast(ahead + n * 0.35f, ahead - n * 0.65f, out TerrainHit hit)
			&& hit.Normal.LengthSquared() > 1e-12f
			&& hit.Normal.Dot(up) > -0.3f)
		{
			return hit.Point + n * RideHeight;
		}
		if (n.Dot(up) < 0.95f
			&& ctx.Terrain.Raycast(ahead + up * 0.35f, ahead - up * CrestProbeDepth, out TerrainHit topHit)
			&& topHit.Normal.LengthSquared() > 1e-12f
			&& topHit.Normal.Dot(up) > -0.3f)
		{
			crest = true;
			return topHit.Point + up * RideHeight;
		}
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
		Vector3 stepDir = forward.Lerp(aim, SteerBlend);
		stepDir = stepDir.LengthSquared() < 1e-8f ? forward : stepDir.Normalized();

		foreach (Limb limb in Limbs)
		{
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
