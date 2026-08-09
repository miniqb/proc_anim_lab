using Godot;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// 抓取竞技场 HUD：左上状态行（F1 显隐）、中央大字提示、下方「挣脱进度 + 倒计时」双条、
/// 吞没全屏渐暗层。纯展示——不持有任何玩法状态，世界脚本每帧推送。
///
/// 沿用仓库 HUD 纪律：代码构建、无 Theme 资源、逐控件覆盖；本类只用 Label/ColorRect/
/// PanelContainer（天然不可聚焦）且全部 <c>MouseFilter = Ignore</c>，保证连打键与鼠标
/// 捕获永远不会被 UI 吞掉。
/// </summary>
public sealed class DaddyLongLegsGrabHud
{
    private const float BarWidth = 420f;
    private const float BarHeight = 16f;
    private const float TimeBarHeight = 7f;

    private CanvasLayer _layer = null!;
    private PanelContainer _statusPanel = null!;
    private Label _status = null!;
    private Label _prompt = null!;
    private Label _promptDetail = null!;
    private Control _barsRoot = null!;
    private ColorRect _escapeFill = null!;
    private ColorRect _timeFill = null!;
    private ColorRect _fade = null!;
    private Control _crosshair = null!;
    private ColorRect _crosshairH = null!;
    private ColorRect _crosshairV = null!;

    private static readonly Color CrosshairIdle = new(0.95f, 0.95f, 0.92f, 0.85f);
    private static readonly Color CrosshairHit = new(1.00f, 0.42f, 0.22f, 0.95f);

    public void Build(Node parent)
    {
        _layer = new CanvasLayer { Name = "GrabHud" };
        parent.AddChild(_layer);

        var root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(root);

        // 渐暗层最先加（垫底），alpha 0 时完全不可见。
        _fade = new ColorRect
        {
            Name = "DevourFade",
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_fade);

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

        _barsRoot = new Control
        {
            Name = "Bars",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _barsRoot.AnchorLeft = 0.5f;
        _barsRoot.AnchorRight = 0.5f;
        _barsRoot.AnchorTop = 0.66f;
        _barsRoot.AnchorBottom = 0.66f;
        _barsRoot.OffsetLeft = -BarWidth * 0.5f;
        _barsRoot.OffsetRight = BarWidth * 0.5f;
        _barsRoot.OffsetTop = 0f;
        _barsRoot.OffsetBottom = BarHeight + TimeBarHeight + 10f;
        root.AddChild(_barsRoot);

        AddBar(_barsRoot, new Vector2(0f, 0f), new Vector2(BarWidth, BarHeight),
            new Color(0.06f, 0.08f, 0.10f, 0.82f),
            new Color(0.35f, 0.95f, 0.45f), out _escapeFill);
        AddBar(_barsRoot, new Vector2(0f, BarHeight + 6f), new Vector2(BarWidth, TimeBarHeight),
            new Color(0.06f, 0.08f, 0.10f, 0.82f),
            new Color(0.95f, 0.30f, 0.22f), out _timeFill);

        // 屏幕中央准心：两条细矩形组成的十字，命中瞬间整体变橙。
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

    /// <summary>中央提示：主行 + 小字副行；传空串隐藏对应行。</summary>
    public void SetPrompt(string main, string detail = "")
    {
        _prompt.Text = main;
        _prompt.Visible = main.Length > 0;
        _promptDetail.Text = detail;
        _promptDetail.Visible = detail.Length > 0;
    }

    /// <summary>挣脱双条：escapeFraction 是已填进度、timeFraction 是**剩余**时间占比。</summary>
    public void SetBars(bool visible, float escapeFraction, float timeFraction)
    {
        _barsRoot.Visible = visible;
        if (!visible)
            return;
        _escapeFill.Size = new Vector2(
            BarWidth * Mathf.Clamp(escapeFraction, 0f, 1f), BarHeight);
        _timeFill.Size = new Vector2(
            BarWidth * Mathf.Clamp(timeFraction, 0f, 1f), TimeBarHeight);
    }

    public void SetFadeAlpha(float alpha)
    {
        _fade.Color = new Color(0f, 0f, 0f, Mathf.Clamp(alpha, 0f, 1f));
    }

    /// <summary>F1：只收左上状态行；玩法反馈（提示/双条/渐暗）不受影响。</summary>
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

    private static void AddBar(
        Control parent,
        Vector2 position,
        Vector2 size,
        Color background,
        Color fill,
        out ColorRect fillRect)
    {
        var bg = new ColorRect
        {
            Position = position,
            Size = size,
            Color = background,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(bg);
        fillRect = new ColorRect
        {
            Position = Vector2.Zero,
            Size = new Vector2(0f, size.Y),
            Color = fill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bg.AddChild(fillRect);
    }
}
