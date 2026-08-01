using System.Collections.Generic;
using Godot;
using ProcAnim.Core.Physics;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 鼠标拖拽：相机射线对 chunk 做纯数学射线-球求交（chunk 不是 collider，不走物理查询），
/// 拖拽时在受力阶段施加 clamp 过的弹簧力——不直接写 Pos，保软体感（身体被拖着晃、尾巴甩尾）。
/// 输入只在 tick 的外力相位进入系统，determinism 模式整体跳过即可，两条路径互不污染。
/// </summary>
public sealed class DragController
{
    /// <summary>弹簧系数（每 tick）。</summary>
    public float Spring = 0.2f;

    /// <summary>阻尼系数（对 chunk 当前 Vel）。</summary>
    public float Damping = 0.3f;

    /// <summary>单 tick 力上限（米/tick），防甩爆。</summary>
    public float MaxForce = 0.5f;

    private BodyChunk? _grabbed;
    private float _grabDepth;
    private Vector3 _target;

    public bool IsDragging => _grabbed is not null;

    /// <summary>外部强制释放（身体被品种重生替换时调用，防止悬挂旧 chunk 引用）。</summary>
    public void Release() => _grabbed = null;

    /// <summary>每 tick 开头采样鼠标状态：按下→拾取最近 chunk；按住→更新定深平面目标点；松开→释放。</summary>
    public void SampleInput(Camera3D camera, IReadOnlyList<Body> bodies)
    {
        bool pressed = Input.IsMouseButtonPressed(MouseButton.Left);
        if (!pressed)
        {
            _grabbed = null;
            return;
        }

        Vector2 mouse = camera.GetViewport().GetMousePosition();
        Vector3 rayOrigin = camera.ProjectRayOrigin(mouse);
        Vector3 rayDir = camera.ProjectRayNormal(mouse);

        if (_grabbed is null)
        {
            float best = float.MaxValue;
            foreach (Body body in bodies)
            {
                foreach (BodyChunk chunk in body.Chunks)
                {
                    if (RaySphere(rayOrigin, rayDir, chunk.Pos, chunk.Radius, out float dist) && dist < best)
                    {
                        best = dist;
                        _grabbed = chunk;
                    }
                }
            }
            if (_grabbed is null)
            {
                return;
            }
            _grabDepth = best;
        }

        // 目标点 = 垂直视线的定深平面上鼠标投影。
        _target = rayOrigin + rayDir * _grabDepth;
    }

    /// <summary>外力阶段：对被抓 chunk 施加弹簧力（与重力同相位，所有外力都发生在受力阶段）。</summary>
    public void ApplyDragForce()
    {
        if (_grabbed is null)
        {
            return;
        }
        Vector3 force = (_target - _grabbed.Pos) * Spring - _grabbed.Vel * Damping;
        if (force.Length() > MaxForce)
        {
            force = force.Normalized() * MaxForce;
        }
        _grabbed.Vel += force;
    }

    private static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float dist)
    {
        dist = 0f;
        Vector3 oc = center - origin;
        float proj = oc.Dot(dir);
        if (proj < 0f)
        {
            return false;
        }
        float perpSq = oc.LengthSquared() - proj * proj;
        float rSq = radius * radius;
        if (perpSq > rSq)
        {
            return false;
        }
        dist = proj - Mathf.Sqrt(rSq - perpSq);
        return true;
    }
}
