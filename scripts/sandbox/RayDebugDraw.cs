using System.Collections.Generic;
using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 射线可视化调试：包装 ITerrainQuery，把一个物理 tick 内经过接缝的所有射线
/// （腿的落脚/翻越采样带、脚与身体的碰撞扫掠、推进目标投影、站稳探测）记录下来，
/// 每帧用 ImmediateMesh 重画——洋红=命中（画到命中点），灰蓝=打空（画完整段）。
/// 纯观测：转发不改变任何查询结果，开关只影响记录与绘制，确定性哈希不受影响。
/// </summary>
public sealed class RayDebugDraw : ITerrainQuery
{
    private readonly ITerrainQuery _inner;
    private readonly List<(Vector3 From, Vector3 End, bool DidHit)> _rays = new();
    private ImmediateMesh? _mesh;

    /// <summary>记录/绘制开关（F3 切换）。关闭时本类只是纯转发。</summary>
    public bool Enabled;

    private static readonly Color HitColor = new(0.95f, 0.25f, 0.85f);
    private static readonly Color MissColor = new(0.45f, 0.5f, 0.6f, 0.5f);

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
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = true, // 调试线透视：穿进墙里的射线段也要能看见
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        parent.AddChild(node);
    }

    public void Draw()
    {
        if (_mesh is null)
        {
            return;
        }
        _mesh.ClearSurfaces();
        if (!Enabled || _rays.Count == 0)
        {
            return;
        }
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach ((Vector3 from, Vector3 end, bool didHit) in _rays)
        {
            Color c = didHit ? HitColor : MissColor;
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(from);
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(end);
        }
        _mesh.SurfaceEnd();
    }
}
