using Godot;

namespace ProcAnim.Core;

/// <summary>单个 chunk 在一个 tick 内按 R1→R2→R3→S4 顺序收集的固定容量接触法线。
/// 零分配、按点积去重；供碰撞后结构修正把候选位移投影到接触可行锥。速度响应暂时仍保持
/// 既有逐命中语义，避免把 heavy 的表面阻尼行为与本次姿态修复捆在一起。</summary>
internal struct ContactManifold3D
{
    private Vector3 _n0;
    private Vector3 _n1;
    private Vector3 _n2;
    private Vector3 _n3;

    public int Count { get; private set; }

    public void Clear()
    {
        Count = 0;
        _n0 = _n1 = _n2 = _n3 = Vector3.Zero;
    }

    public void Add(Vector3 normal)
    {
        if (normal.LengthSquared() < 1e-12f)
        {
            return;
        }
        normal = normal.Normalized();
        for (int i = 0; i < Count; i++)
        {
            if (Get(i).Dot(normal) > 0.999f)
            {
                return;
            }
        }
        if (Count >= 4)
        {
            return;
        }
        switch (Count++)
        {
            case 0: _n0 = normal; break;
            case 1: _n1 = normal; break;
            case 2: _n2 = normal; break;
            case 3: _n3 = normal; break;
        }
    }

    /// <summary>去掉会把 chunk 推入任一已知接触面的位移分量；沿面与离面分量保留。
    /// 非正交接触面须迭代：单遍顺序投影会让后一个法线重新引入对前一面的穿入分量。
    /// 固定 8 遍后仍不满足全部半空间时保守返回零，保持确定性且绝不写回地形内。</summary>
    public Vector3 ProjectDisplacement(Vector3 displacement)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            bool adjusted = false;
            for (int i = 0; i < Count; i++)
            {
                Vector3 n = Get(i);
                float into = displacement.Dot(n);
                if (into < -1e-6f)
                {
                    displacement -= n * into;
                    adjusted = true;
                }
            }
            if (!adjusted)
            {
                break;
            }
        }
        for (int i = 0; i < Count; i++)
        {
            if (displacement.Dot(Get(i)) < -1e-5f)
            {
                return Vector3.Zero;
            }
        }
        return displacement;
    }

    private Vector3 Get(int index) => index switch
    {
        0 => _n0,
        1 => _n1,
        2 => _n2,
        _ => _n3,
    };

}
