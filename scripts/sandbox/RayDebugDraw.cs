using System.Collections.Generic;
using Godot;
using ProcAnim.Core;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 射线可视化调试：包装 ITerrainQuery，把一个物理 tick 内经过接缝的所有射线
/// （腿的落脚/翻越采样带、脚与身体的碰撞扫掠、推进目标投影、站稳探测）记录下来，
/// 每帧用 ImmediateMesh 重画。GL 线图元恒为 1px 看不清，这里画成朝向相机的条带
/// （有实际宽度），命中点再加菱形标记——洋红=命中（画到命中点），灰蓝=打空（完整段）。
/// 另画推进目标（胡萝卜）：头→目标的条带 + 大菱形，按来源分支着色——
/// 绿=钉在支撑面、橙=翻越顶面、紫=宿主直喂（MoveTarget）、红=空中退化目标
/// （红长期出现 = 身体在追悬空胡萝卜）。
/// 纯观测：转发不改变任何查询结果，开关只影响记录与绘制，确定性哈希不受影响。
/// </summary>
public sealed class RayDebugDraw : ITerrainQuery
{
    private readonly ITerrainQuery _inner;
    private readonly List<(Vector3 From, Vector3 End, bool DidHit)> _rays = new();
    private ImmediateMesh? _mesh;

    /// <summary>记录/绘制开关（F3 切换）。关闭时本类只是纯转发。</summary>
    public bool Enabled;

    /// <summary>射线条带半宽（米）：总宽 2cm，生物尺度（腿长 0.55m）下清晰可辨。</summary>
    private const float HalfWidth = 0.01f;

    /// <summary>命中点菱形标记的半径（米）。</summary>
    private const float MarkerSize = 0.035f;

    private static readonly Color HitColor = new(1f, 0.2f, 0.9f);
    private static readonly Color MarkerColor = new(1f, 0.85f, 1f);
    private static readonly Color MissColor = new(0.55f, 0.65f, 0.85f, 0.75f);

    /// <summary>胡萝卜标记比射线命中菱形大一圈，一眼区分「目标」与「落点」。</summary>
    private const float CarrotMarkerSize = 0.07f;

    private static readonly Color CarrotSupportColor = new(0.25f, 0.95f, 0.35f);
    private static readonly Color CarrotCrestColor = new(1f, 0.65f, 0.15f);
    private static readonly Color CarrotFallbackColor = new(1f, 0.18f, 0.18f);
    private static readonly Color CarrotExternalColor = new(0.7f, 0.35f, 1f);

    public RayDebugDraw(ITerrainQuery inner)
    {
        _inner = inner;
    }

    public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
    {
        bool didHit = _inner.Raycast(from, to, out hit);
        if (Enabled)
        {
            _rays.Add((from, didHit ? hit.Point : to, didHit));
        }
        return didHit;
    }

    public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
    {
        bool overlapped = _inner.SpherePenetration(center, radius, out pushDir, out depth);
        if (Enabled && overlapped)
        {
            // 去穿透画成从球心沿 MTD 的短条带（未重叠的查询每 tick 量大，不记）。
            _rays.Add((center, center + pushDir * (depth + 0.05f), true));
        }
        return overlapped;
    }

    /// <summary>每个物理 tick 开头调用：清掉上一 tick 的记录（画面始终显示最近一个 tick）。</summary>
    public void BeginTick()
    {
        _rays.Clear();
    }

    public void Build(Node3D parent)
    {
        _mesh = new ImmediateMesh();
        var node = new MeshInstance3D
        {
            Mesh = _mesh,
            TopLevel = true, // 顶点是世界坐标，节点钉在世界原点

            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = true, // 调试线透视：穿进墙里的射线段也要能看见
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        parent.AddChild(node);
    }

    public void Draw(Camera3D camera, LizardLocomotionController controller)
    {
        if (_mesh is null)
        {
            return;
        }
        _mesh.ClearSurfaces();
        bool hasCarrot = controller.LastMoveTargetKind != MoveTargetKind.None;
        if (!Enabled || (_rays.Count == 0 && !hasCarrot))
        {
            return;
        }
        Vector3 camPos = camera.GlobalPosition;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach ((Vector3 from, Vector3 end, bool didHit) in _rays)
        {
            AddRibbon(from, end, didHit ? HitColor : MissColor, camPos);
            if (didHit)
            {
                AddMarker(end, camPos, MarkerColor, MarkerSize);
            }
        }
        if (hasCarrot)
        {
            Color c = controller.LastMoveTargetKind switch
            {
                MoveTargetKind.Support => CarrotSupportColor,
                MoveTargetKind.Crest => CarrotCrestColor,
                MoveTargetKind.External => CarrotExternalColor,
                _ => CarrotFallbackColor,
            };
            AddRibbon(controller.Head.Pos, controller.LastMoveTarget, c, camPos);
            AddMarker(controller.LastMoveTarget, camPos, c, CarrotMarkerSize);
        }
        _mesh.SurfaceEnd();
    }

    /// <summary>一根射线 = 一条朝向相机的条带（两三角形），比 1px 线图元醒目得多。</summary>
    private void AddRibbon(Vector3 from, Vector3 end, Color c, Vector3 camPos)
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
            side = dir.Cross(Vector3.Up); // 射线正对相机的退化情形
            if (side.LengthSquared() < 1e-8f)
            {
                side = Vector3.Right;
            }
        }
        side = side.Normalized() * HalfWidth;

        Quad(from - side, from + side, end + side, end - side, c);
    }

    /// <summary>指定点画一个朝向相机的菱形（射线命中点/胡萝卜共用），一眼锁定位置。</summary>
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
