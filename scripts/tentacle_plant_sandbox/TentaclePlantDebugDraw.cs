using Godot;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 竞技场调试覆盖层（纯观测）。两组信息：
///
/// ① **感知两锥**（覆盖层开着就一直画，感知本来每相位都在跑）：以头端为顶点、
///    以感知用的 tick 域头 forward 为轴，画锁定锥（青）与察觉锥（紫、更淡）。
///    两者都是**球扇形**（判定 = 距离 ≤ 半径 且 cos ≥ cos半角），所以边界圆环画在
///    球冠上（深 = R·cos半角、半径 = R·sin半角），不是平底盖——16° 的锁定锥两者
///    几乎一样，75° 的察觉锥差得很远，画平底会严重虚报覆盖范围。
/// ② **当前探测点**（绿菱形）与「探测点 ↔ 头端」连线（半透明绿条带，长度 = 头端
///    伺服滞后）+ 头端小橙菱形；只在"宿主本 tick 确实把探测点喂给了内核"时画。
///
/// 绘制手法沿用 <see cref="ProcAnimLab.Sandbox.RayDebugDraw"/>：GL 线图元恒为 1px
/// 看不清，改画朝向相机的条带/菱形；Unshaded + NoDepthTest 让触手或墙挡住时也
/// 看得见。只读宿主已有的观测量，不进物理、不进哈希（本场景亦不进矩阵）。
/// </summary>
public sealed class TentaclePlantDebugDraw
{
	/// <summary>探测连线条带半宽（米）：总宽 2.4cm，触手尺度（链长 ≈3m）下清晰可辨。</summary>
	private const float LineHalfWidth = 0.012f;

	/// <summary>锥体线框半宽（米）：比探测连线细，6.5m 的察觉锥不至于糊住视野。</summary>
	private const float ConeHalfWidth = 0.007f;

	/// <summary>探测点菱形半径（米）：比头部（HandVisualRadius ≈0.2m）小一圈。</summary>
	private const float TargetMarkerSize = 0.11f;

	/// <summary>头端菱形半径（米）：只用来钉住连线另一端，不糊住嘴。</summary>
	private const float HeadMarkerSize = 0.05f;

	// 锥体线框密度：察觉锥又宽又大，多给几根母线/环段才看得出是个锥。
	private const int LockSpokes = 8;
	private const int LockRimSegments = 24;
	private const int AwareSpokes = 12;
	private const int AwareRimSegments = 32;

	private static readonly Color TargetColor = new(0.25f, 1f, 0.55f);
	private static readonly Color LineColor = new(0.25f, 1f, 0.55f, 0.55f);
	private static readonly Color HeadColor = new(1f, 0.85f, 0.35f);
	private static readonly Color LockConeColor = new(0.35f, 0.95f, 1f, 0.85f);
	private static readonly Color AwareConeColor = new(0.75f, 0.45f, 1f, 0.40f);

	private ImmediateMesh? _mesh;

	private float _lockHalfAngle;
	private float _lockLength;
	private float _awareHalfAngle;
	private float _awareRadius;

	/// <summary>绘制总开关（Inspector 初值 + F3 切换）。关闭时只清面。</summary>
	public bool Enabled;

	public void Build(Node3D parent)
	{
		_mesh = new ImmediateMesh();
		var node = new MeshInstance3D
		{
			Name = "PlantDebugDraw",
			Mesh = _mesh,
			TopLevel = true, // 顶点是世界坐标，节点钉在世界原点
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				VertexColorUseAsAlbedo = true,
				NoDepthTest = true,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			},
		};
		parent.AddChild(node);
	}

	/// <summary>
	/// 钉入两锥的几何（≙ 渲染件 ConfigureSearchlight 的一次性配置面）：宿主的
	/// 感知 Export 是 _Ready 期常量，画的锥与感知器吃的是同一组数。
	/// </summary>
	public void ConfigureCones(
		float lockHalfAngleDegrees, float lockLength,
		float awareHalfAngleDegrees, float awareRadius)
	{
		_lockHalfAngle = Mathf.DegToRad(lockHalfAngleDegrees);
		_lockLength = lockLength;
		_awareHalfAngle = Mathf.DegToRad(awareHalfAngleDegrees);
		_awareRadius = awareRadius;
	}

	/// <summary>
	/// 每渲染帧调一次。两锥恒画（感知不分相位）；探测点只在
	/// <paramref name="probeActive"/>（宿主本 tick 喂了探测点）时画——画面不会留下
	/// 上一轮的鬼影。<paramref name="head"/> 与 <paramref name="probePoint"/> 传插值
	/// 后的位置（与正式渲染件同一 alpha），不让 40Hz 逻辑抖画面；
	/// <paramref name="headForward"/> 传**感知用的 tick 域 forward**（无渲染低通），
	/// 画出来的锥才是判定真正用的那个。
	/// </summary>
	public void Draw(bool probeActive, Vector3 head, Vector3 headForward,
		Vector3 probePoint, Vector3 camPos)
	{
		if (_mesh is null)
		{
			return;
		}
		_mesh.ClearSurfaces();
		if (!Enabled)
		{
			return;
		}
		_mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);

		// 察觉锥先画（大而淡），锁定锥压在上面。
		AddCone(head, headForward, _awareHalfAngle, _awareRadius,
			AwareConeColor, camPos, AwareSpokes, AwareRimSegments);
		AddCone(head, headForward, _lockHalfAngle, _lockLength,
			LockConeColor, camPos, LockSpokes, LockRimSegments);
		// 锥轴（= 探照灯指向）：只画一次，两锥同轴。
		AddRibbon(head, head + headForward * _lockLength,
			LockConeColor, camPos, ConeHalfWidth);

		if (probeActive)
		{
			AddRibbon(head, probePoint, LineColor, camPos, LineHalfWidth);
			AddMarker(probePoint, camPos, TargetColor, TargetMarkerSize);
			AddMarker(head, camPos, HeadColor, HeadMarkerSize);
		}
		_mesh.SurfaceEnd();
	}

	/// <summary>
	/// 球扇形线框：apex→球冠边界的母线（每根都是精确的半径）+ 球冠上的边界圆环。
	/// 画的是**判定边界本身**，不是近似的锥面。
	/// </summary>
	private void AddCone(Vector3 apex, Vector3 forward, float halfAngle, float radius,
		Color color, Vector3 camPos, int spokes, int rimSegments)
	{
		if (radius <= 1e-4f || halfAngle <= 1e-4f)
		{
			return;
		}
		Vector3 axis = forward.LengthSquared() > 1e-10f
			? forward.Normalized()
			: Vector3.Down;
		Vector3 right = axis.Cross(Vector3.Up);
		if (right.LengthSquared() < 1e-8f)
		{
			right = axis.Cross(Vector3.Right); // 轴与世界 up 近共线（伏击朝下即此情形）
		}
		right = right.Normalized();
		Vector3 up = right.Cross(axis).Normalized();

		Vector3 rimCenter = apex + axis * (radius * Mathf.Cos(halfAngle));
		float rimRadius = radius * Mathf.Sin(halfAngle);

		for (int i = 0; i < spokes; i++)
		{
			AddRibbon(apex, RimPoint(i, spokes), color, camPos, ConeHalfWidth);
		}
		Vector3 previous = RimPoint(0, rimSegments);
		for (int i = 1; i <= rimSegments; i++)
		{
			Vector3 current = RimPoint(i, rimSegments);
			AddRibbon(previous, current, color, camPos, ConeHalfWidth);
			previous = current;
		}

		Vector3 RimPoint(int index, int count)
		{
			float angle = Mathf.Tau * index / count;
			return rimCenter +
				(right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * rimRadius;
		}
	}

	/// <summary>一段连线 = 一条朝向相机的条带（两三角形）。</summary>
	private void AddRibbon(Vector3 from, Vector3 end, Color color, Vector3 camPos,
		float halfWidth)
	{
		Vector3 seg = end - from;
		if (seg.LengthSquared() < 1e-10f)
		{
			return;
		}
		Vector3 dir = seg.Normalized();
		Vector3 toCam = camPos - (from + end) * 0.5f;
		Vector3 side = dir.Cross(toCam);
		if (side.LengthSquared() < 1e-8f)
		{
			side = dir.Cross(Vector3.Up); // 连线正对相机的退化情形
			if (side.LengthSquared() < 1e-8f)
			{
				side = Vector3.Right;
			}
		}
		side = side.Normalized() * halfWidth;
		Quad(from - side, from + side, end + side, end - side, color);
	}

	/// <summary>指定点画一个朝向相机的菱形，一眼锁定位置。</summary>
	private void AddMarker(Vector3 pos, Vector3 camPos, Color color, float size)
	{
		Vector3 toCam = (camPos - pos).LengthSquared() < 1e-8f
			? Vector3.Back
			: (camPos - pos).Normalized();
		Vector3 right = toCam.Cross(Vector3.Up);
		right = right.LengthSquared() < 1e-8f ? Vector3.Right : right.Normalized();
		Vector3 up = right.Cross(toCam);

		Quad(pos - right * size, pos + up * size,
			pos + right * size, pos - up * size, color);
	}

	private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
	{
		_mesh!.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(a);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(b);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(c);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(a);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(c);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(d);
	}
}
