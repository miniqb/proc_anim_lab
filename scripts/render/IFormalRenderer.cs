using Godot;

namespace ProcAnimLab.Render;

/// <summary>
/// 正式（美化）渲染器的沙盒接口。与 debug 白盒渲染（BodyRenderer）并存，V 键切换。
/// 契约（≙ 渲染研究 docs/rainworld_render_research.md §3.2）：对内核**只读**——
/// 渲染层持有的一切化妆状态（呼吸相位、bend pole 低通、稳定 up）都是渲染侧私有，
/// 不写回控制器、不进确定性哈希；alpha 是物理插值分数，dt 是渲染帧真实时长（化妆状态推进用）。
/// </summary>
internal interface IFormalRenderer
{
    void Build(Node3D parent);
    void Clear();
    void SetVisible(bool visible);
    void Draw(float alpha, float dt);
}
