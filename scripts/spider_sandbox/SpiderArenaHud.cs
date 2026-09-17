using Godot;

namespace ProcAnimLab.SpiderSandbox;

/// <summary>
/// 蜘蛛跳跃攻击竞技场 HUD：准心十字（命中闪橙）+ 命中/被扑 toast + 左上状态行（F1 显隐）+
/// 中央大字提示（POUNCED ×n / KILLED）+ 底部常驻小字（按键表）。
/// 纯展示——不持有任何玩法状态，世界脚本每帧推送（toast TTL 也归世界脚本管）。
///
/// 沿用仓库 HUD 纪律（RatArenaHud 骨架）：代码构建、无 Theme 资源、逐控件覆盖；
/// 只用 Label/ColorRect/PanelContainer（天然不可聚焦）且全部 <c>MouseFilter = Ignore</c>，
/// 保证鼠标捕获与按键永远不会被 UI 吞掉。
/// </summary>
public sealed class SpiderArenaHud
{
    private CanvasLayer _layer = null!;
    private PanelContainer _statusPanel = null!;
    private Label _status = null!;
    private Label _prompt = null!;
    private Label _promptDetail = null!;
    private Label _toast = null!;
    private Label _keyHint = null!;
    private Control _crosshair = null!;
    private ColorRect _crosshairH = null!;
    private ColorRect _crosshairV = null!;

    private static readonly Color CrosshairIdle = new(0.95f, 0.95f, 0.92f, 0.85f);
    private static readonly Color CrosshairHit = new(1.00f, 0.42f, 0.22f, 0.95f);

    public void Build(Node parent)
    {
        _layer = new CanvasLayer { Name = "SpiderArenaHud" };
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
        _promptDetail = MakeCenteredLabel(root, "PromptDetail", fontSize: 16, anchorY: 0.45f, height: 26f);
        _toast = MakeCenteredLabel(root, "HitToast", fontSize: 18, anchorY: 0.58f, height: 28f);
        _toast.AddThemeColorOverride("font_color", new Color(1.00f, 0.78f, 0.55f));

        _keyHint = MakeCenteredLabel(root, "KeyHint", fontSize: 13, anchorY: 0.94f, height: 20f);
        _keyHint.AddThemeColorOverride("font_color", new Color(0.85f, 0.83f, 0.78f, 0.75f));
        _keyHint.Text = "[LMB] shoot   [1/2/3] spider preset   [R] restart   [F1] hud   [Esc] mouse";
        _keyHint.Visible = true;

        _crosshair = new Control
        {
            Name = "Crosshair",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _crosshair.AnchorLeft = 0.5f;
        _crosshair.AnchorRight = 0.5f;
        _crosshair.AnchorTop = 0.5f;
        _crosshair.AnchorBottom = 0.5f;
        root.AddChild(_crosshair);
        _crosshairH = new ColorRect
        {
            Position = new Vector2(-7f, -1f),
            Size = new Vector2(14f, 2f),
            Color = CrosshairIdle,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _crosshair.AddChild(_crosshairH);
        _crosshairV = new ColorRect
        {
            Position = new Vector2(-1f, -7f),
            Size = new Vector2(2f, 14f),
            Color = CrosshairIdle,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _crosshair.AddChild(_crosshairV);
    }

    /// <summary>准心显隐与命中闪色（世界脚本每帧推送）。</summary>
    public void SetCrosshair(bool visible, bool hitFlash)
    {
        _crosshair.Visible = visible;
        Color color = hitFlash ? CrosshairHit : CrosshairIdle;
        _crosshairH.Color = color;
        _crosshairV.Color = color;
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>整层显隐（事件截图时隐藏，免得文字压住主体）。</summary>
    public bool Visible
    {
        get => _layer.Visible;
        set => _layer.Visible = value;
    }

    /// <summary>中央提示：主行 + 小字副行；传空串隐藏对应行。</summary>
    public void SetPrompt(string main, string detail = "")
    {
        _prompt.Text = main;
        _prompt.Visible = main.Length > 0;
        _promptDetail.Text = detail;
        _promptDetail.Visible = detail.Length > 0;
    }

    /// <summary>toast（TTL 由世界脚本管理，过期传空串隐藏）。</summary>
    public void SetToast(string text)
    {
        _toast.Text = text;
        _toast.Visible = text.Length > 0;
    }

    /// <summary>F1：只收左上状态行；玩法反馈（提示/toast/准心）不受影响。</summary>
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
        label.OffsetLeft = -440f;
        label.OffsetRight = 440f;
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
