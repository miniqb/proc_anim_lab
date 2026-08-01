namespace ProcAnim.Core.Host;

/// <summary>
/// 并列 locomotion 后端本 tick 推进目标（调试“胡萝卜”）的来源。
/// 主要供宿主与通用调试绘制读取；具体后端可在同 tick 内以局部分支结果调节推进。
/// </summary>
public enum MoveTargetKind
{
    /// <summary>本 tick 未施加推进（无移动意图或位于死区），目标点无效。</summary>
    None,

    /// <summary>沿当前支撑方向投影并命中的表面目标。</summary>
    Support,

    /// <summary>世界向下补充探测命中的棱线后表面；目前仅蜥蜴后端使用。</summary>
    Crest,

    /// <summary>所有推进探测打空后的相对空中目标。</summary>
    Fallback,

    /// <summary>宿主通过 MoveTarget 直喂的邻近可达路径点。</summary>
    External,
}
