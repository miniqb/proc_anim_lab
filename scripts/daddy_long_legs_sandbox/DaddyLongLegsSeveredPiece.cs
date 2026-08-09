using System;
using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Species.DaddyLongLegs;
using ProcAnimLab.Render;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// 被枪击打断的触手远端段：宿主侧独立 Verlet 短绳，与内核完全脱钩。
/// 生命周期：Falling（重力 + 地板/墙碰撞 + 落地强摩擦）→ Resting（完全睡眠，
/// 「完全掉在地上之后不再动弹」）→ Traction（接手牵引：断口端被无形力量拽向
/// 残肢尖端，允许整条被拽离地面）→ 牵引中断则跌回 Falling；接回成功由宿主移除。
/// 点序固定：index 0 = 断口端（朝向残肢），末位 = 原触手末梢。
/// 渲染复用正式层 TubeMeshBuilder（剪影黑扫管），白盒/正式视图都可见。
/// </summary>
public sealed class DaddyLongLegsSeveredPiece
{
	public enum PieceState
	{
		Falling,
		Resting,
		Traction,
	}

	private const int ConstraintIterations = 4;
	private const float AirDamping = 0.985f;
	private const float GroundBounce = 0.22f;
	private const float GroundFriction = 0.55f;
	// 睡眠判定：全点近地 + 近静止连续这么多 tick 才冻结，避免弹跳间隙误睡。
	private const int RestRequiredStillTicks = 20;
	private const float RestVelocityThreshold = 0.004f;
	private const float RestGroundSlack = 0.03f;
	// 牵引：断口端的 capped 伺服速度（米/tick），链身按索引衰减跟随，其余交给约束。
	private const float TractionServoSpeed = 0.075f;
	private const float TractionFollowFalloff = 0.55f;

	private readonly Vector3[] _pos;
	private readonly Vector3[] _lastPos;
	private readonly Vector3[] _vel;
	private readonly float _radius;
	private readonly Vector3 _boundsMin;
	private readonly Vector3 _boundsMax;
	private Vector3 _tractionTarget;
	private int _stillTicks;

	private TubeMeshBuilder? _tube;
	private readonly List<Vector3> _pts = new();
	private readonly List<float> _radii = new();
	private readonly List<Color> _colors = new();
	private readonly List<TubeStation> _stations = new();
	private Color _bodyColor = new(0.058f, 0.049f, 0.038f);

	public int TentacleIndex { get; }
	public float LinkLength { get; }
	public PieceState State { get; private set; } = PieceState.Falling;
	public int PointCount => _pos.Length;
	public Vector3 BreakEndPosition => _pos[0];

	/// <summary>从 SeverTentacle 返回的孤儿段状态播种；LastPos/Vel 原样保留，
	/// 断落瞬间的渲染插值与弹道都无缝衔接。</summary>
	public DaddyLongLegsSeveredPiece(
		int tentacleIndex,
		float linkLength,
		IReadOnlyList<DaddyTentacleSegmentState> removedSegments,
		Vector3 boundsMin,
		Vector3 boundsMax)
	{
		ArgumentNullException.ThrowIfNull(removedSegments);
		if (removedSegments.Count < 1)
			throw new ArgumentException("Severed piece needs at least one segment.");
		TentacleIndex = tentacleIndex;
		LinkLength = linkLength;
		_pos = new Vector3[removedSegments.Count];
		_lastPos = new Vector3[removedSegments.Count];
		_vel = new Vector3[removedSegments.Count];
		_radius = MathF.Max(0.03f, removedSegments[0].Radius);
		_boundsMin = boundsMin;
		_boundsMax = boundsMax;
		for (int i = 0; i < removedSegments.Count; i++)
		{
			DaddyTentacleSegmentState segment = removedSegments[i];
			_pos[i] = segment.Pos;
			_lastPos[i] = segment.LastPos;
			_vel[i] = segment.Vel;
		}
	}

	/// <summary>把当前点坐标（断口→末梢顺序）拷进 RestoreTentacle 所需的缓冲。</summary>
	public void CopyPositions(Vector3[] buffer)
	{
		Array.Copy(_pos, buffer, _pos.Length);
	}

	public Vector3 AverageVelocity()
	{
		Vector3 sum = Vector3.Zero;
		foreach (Vector3 velocity in _vel)
			sum += velocity;
		return sum / _pos.Length;
	}

	/// <summary>接手牵引开始：Resting 也会被无形力量重新唤醒。</summary>
	public void BeginTraction(Vector3 stumpTip)
	{
		State = PieceState.Traction;
		_tractionTarget = stumpTip;
		_stillTicks = 0;
	}

	public void UpdateTractionTarget(Vector3 stumpTip) => _tractionTarget = stumpTip;

	/// <summary>接手失败/中断：跌回 Falling，重新走落地睡眠。</summary>
	public void EndTraction()
	{
		if (State == PieceState.Traction)
			State = PieceState.Falling;
		_stillTicks = 0;
	}

	public void Tick(Vector3 gravityPerTick)
	{
		if (State == PieceState.Resting)
			return;

		for (int i = 0; i < _pos.Length; i++)
		{
			_lastPos[i] = _pos[i];
			_vel[i] *= AirDamping;
			_vel[i] += gravityPerTick;
		}
		if (State == PieceState.Traction)
		{
			// 断口端满速伺服，链身衰减跟随（其余长度由距离约束带动），
			// 视觉上像整条被从断口提起来。
			float follow = 1f;
			for (int i = 0; i < _pos.Length; i++)
			{
				Vector3 target = _tractionTarget
					+ (_pos[i] - _pos[0]).LimitLength(LinkLength * i);
				Vector3 delta = target - _pos[i];
				float distance = delta.Length();
				if (distance > 1e-6f)
				{
					_vel[i] += delta / distance
						* MathF.Min(TractionServoSpeed * follow, distance);
				}
				follow *= TractionFollowFalloff;
			}
		}
		for (int i = 0; i < _pos.Length; i++)
			_pos[i] += _vel[i];

		SolveConstraints();
		CollideBounds();

		if (State == PieceState.Falling)
			UpdateRest();
	}

	private void SolveConstraints()
	{
		for (int iteration = 0; iteration < ConstraintIterations; iteration++)
		{
			for (int i = 1; i < _pos.Length; i++)
			{
				Vector3 delta = _pos[i] - _pos[i - 1];
				float distance = delta.Length();
				if (distance <= 1e-6f)
					continue;
				// 双向精确距离约束：断绳是一串刚性小节，不是橡皮筋。
				Vector3 correction = delta / distance * ((distance - LinkLength) * 0.5f);
				_pos[i - 1] += correction;
				_vel[i - 1] += correction;
				_pos[i] -= correction;
				_vel[i] -= correction;
			}
		}
	}

	private void CollideBounds()
	{
		for (int i = 0; i < _pos.Length; i++)
		{
			Vector3 position = _pos[i];
			Vector3 velocity = _vel[i];
			if (position.Y < _boundsMin.Y + _radius)
			{
				position.Y = _boundsMin.Y + _radius;
				if (velocity.Y < 0f)
					velocity.Y = -velocity.Y * GroundBounce;
				// 微弹直接吸收：不然逐 tick 的重力→反弹会持续注入 ~0.005 的竖直速度，
				// 恰好卡在入睡阈值上方，断落段永远睡不着。
				if (MathF.Abs(velocity.Y) < 0.01f)
					velocity.Y = 0f;
				velocity.X *= GroundFriction;
				velocity.Z *= GroundFriction;
			}
			if (position.Y > _boundsMax.Y - _radius)
			{
				position.Y = _boundsMax.Y - _radius;
				velocity.Y = MathF.Min(velocity.Y, 0f);
			}
			if (position.X < _boundsMin.X + _radius)
			{
				position.X = _boundsMin.X + _radius;
				velocity.X = MathF.Max(velocity.X, 0f);
			}
			else if (position.X > _boundsMax.X - _radius)
			{
				position.X = _boundsMax.X - _radius;
				velocity.X = MathF.Min(velocity.X, 0f);
			}
			if (position.Z < _boundsMin.Z + _radius)
			{
				position.Z = _boundsMin.Z + _radius;
				velocity.Z = MathF.Max(velocity.Z, 0f);
			}
			else if (position.Z > _boundsMax.Z - _radius)
			{
				position.Z = _boundsMax.Z - _radius;
				velocity.Z = MathF.Min(velocity.Z, 0f);
			}
			_pos[i] = position;
			_vel[i] = velocity;
		}
	}

	private void UpdateRest()
	{
		bool still = true;
		for (int i = 0; i < _pos.Length; i++)
		{
			if (_pos[i].Y > _boundsMin.Y + _radius + RestGroundSlack
				|| _vel[i].Length() > RestVelocityThreshold)
			{
				still = false;
				break;
			}
		}
		_stillTicks = still ? _stillTicks + 1 : 0;
		if (_stillTicks < RestRequiredStillTicks)
			return;
		State = PieceState.Resting;
		for (int i = 0; i < _pos.Length; i++)
		{
			_vel[i] = Vector3.Zero;
			_lastPos[i] = _pos[i];
		}
	}

	/// <summary>建一根独立剪影黑扫管；颜色取当前预设的 Body 剪影色。</summary>
	public void BuildVisual(Node3D parent, string stableId)
	{
		_bodyColor = DaddyRenderPalette.ForStableId(stableId).Body;
		_tube = new TubeMeshBuilder();
		_tube.Build(parent, srgbVertexColors: true);
	}

	public void ClearVisual()
	{
		_tube?.Clear();
		_tube = null;
	}

	public void Render(float interpolation)
	{
		if (_tube is null)
			return;
		_tube.BeginFrame();
		_pts.Clear();
		_radii.Clear();
		_colors.Clear();
		// 断口端略粗、末梢收细，对齐正式渲染件的触手管径轮廓。
		for (int i = 0; i < _pos.Length; i++)
		{
			float t = _pos.Length > 1 ? (float)i / (_pos.Length - 1) : 1f;
			_pts.Add(_lastPos[i].Lerp(_pos[i], interpolation));
			_radii.Add(Mathf.Lerp(0.062f, 0.018f, t));
			_colors.Add(_bodyColor);
		}
		SplineSampler.Sample(_pts, _radii, _colors, 4, _stations);
		if (_stations.Count >= 2)
			_tube.AddTube(_stations, Vector3.Up, 7);
		_tube.EndFrame();
	}
}
