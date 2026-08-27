using Godot;

namespace ProcAnimLab.TentaclePlantSandbox;

/// <summary>
/// 拟态草伏击竞技场 HUD：左上状态行（F1 显隐）+ 中央大字（BITTEN ×n）+ toast +
/// 底部常驻小字（R = RESTART）。无准心——本场景玩家不开枪，只用身体试探那盏灯。
/// 纯展示，不持有玩法状态（RatArenaHud 骨架裁剪版：代码构建、无 Theme 资源，
/// 全部控件 MouseFilter = Ignore，鼠标捕获与按键永远不被 UI 吞掉）。
/// </summary>
public sealed class TentaclePlantArenaHud
{
    private CanvasLayer _layer = null!;
    private PanelContainer _statusPanel = null!;
    private Label _status = null!;
    private Label _prompt = null!;
    private Label _toast = null!;
    private Label _restartHint = null!;

    public void Build(Node parent)
    {
        _layer = new CanvasLayer { Name = "TentaclePlantArenaHud" };
        parent.AddChild(_layer);

        var root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(root);

        _statusPanel = new PanelContainer
        {
            Name = "StatusPanel",
            Position = new Vector2(14f, 14f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _status = new Label
        {
            Text = "",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _statusPanel.AddChild(_status);
        root.AddChild(_statusPanel);

        _prompt = MakeCenteredLabel(root, "Prompt", fontSize: 30, anchorY: 0.38f, height: 44f);
        _toast = MakeCenteredLabel(root, "BiteToast", fontSize: 18, anchorY: 0.58f, height: 28f);
        _toast.AddThemeColorOverride("font_color", new Color(1.00f, 0.78f, 0.55f));

        _restartHint = MakeCenteredLabel(root, "RestartHint", fontSize: 13, anchorY: 0.94f, height: 20f);
        _restartHint.AddThemeColorOverride("font_color", new Color(0.85f, 0.83f, 0.78f, 0.75f));
        _restartHint.Text = "R = RESTART";
        _restartHint.Visible = true;
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>中央提示；传空串隐藏。</summary>
    public void SetPrompt(string main)
    {
        _prompt.Text = main;
        _prompt.Visible = main.Length > 0;
    }

    /// <summary>toast（TTL 由世界脚本管理，过期传空串隐藏）。</summary>
    public void SetToast(string text)
    {
        _toast.Text = text;
        _toast.Visible = text.Length > 0;
    }

    /// <summary>F1：只收左上状态行；玩法反馈不受影响。</summary>
    public void ToggleStatusVisibility() => _statusPanel.Visible = !_statusPanel.Visible;

    private static Label MakeCenteredLabel(
        Control parent, string name, int fontSize, float anchorY, float height)
    {
        var label = new Label
        {
            Name = name,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        label.AnchorLeft = 0.5f;
        label.AnchorRight = 0.5f;
        label.AnchorTop = anchorY;
        label.AnchorBottom = anchorY;
        label.OffsetLeft = -400f;
        label.OffsetRight = 400f;
        label.OffsetTop = 0f;
        label.OffsetBottom = height;
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(0.96f, 0.93f, 0.88f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        label.AddThemeConstantOverride("outline_size", 5);
        parent.AddChild(label);
        return label;
    }
}
