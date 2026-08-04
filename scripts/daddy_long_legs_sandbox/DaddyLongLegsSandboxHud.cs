using System;
using Godot;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.DaddyLongLegsSandbox;

/// <summary>
/// DaddyLongLegs 独立白盒 HUD。这里只显示核心公开观测量并转发宿主操作，
/// 不持有任何运动、职责或外部目标状态。
/// </summary>
public sealed class DaddyLongLegsSandboxHud
{
    private const float CompactWidth = 430f;
    private const float FullWidth = 510f;

    private PanelContainer _panel = null!;
    private Button _restoreButton = null!;
    private Button _densityButton = null!;
    private OptionButton _presetPicker = null!;
    private Label _status = null!;
    private Label _help = null!;
    private string _summaryText = string.Empty;
    private string _tentacleStates = string.Empty;
    private bool _compact = true;

    public void Build(
        Node parent,
        string[] presetIds,
        Action<int> onPresetSelected,
        Action<int> onSeedDelta,
        Action onAssignTarget,
        Action onTogglePull)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        _panel = new PanelContainer
        {
            Position = new Vector2(14f, 14f),
            CustomMinimumSize = new Vector2(CompactWidth, 0f),
        };
        layer.AddChild(_panel);

        _restoreButton = new Button
        {
            Text = "Show debug UI (F1)",
            Position = new Vector2(14f, 14f),
            FocusMode = Control.FocusModeEnum.None,
            Visible = false,
        };
        _restoreButton.Pressed += ToggleVisibility;
        layer.AddChild(_restoreButton);

        var column = new VBoxContainer();
        _panel.AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        header.AddChild(new Label
        {
            Text = "DADDY LONG LEGS — ISOTROPIC TENTACLE LAB",
            ThemeTypeVariation = "HeaderSmall",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        _densityButton = AddButton(header, "Full (F2)", ToggleCompact);
        AddButton(header, "Hide (F1)", ToggleVisibility);

        var presetRow = new HBoxContainer();
        column.AddChild(presetRow);
        presetRow.AddChild(new Label { Text = "Preset:" });
        _presetPicker = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        SandboxUiFocus.MakePointerOnly(_presetPicker);
        foreach (string id in presetIds)
            _presetPicker.AddItem(id);
        _presetPicker.ItemSelected += index => onPresetSelected((int)index);
        presetRow.AddChild(_presetPicker);

        Button previousSeed = AddButton(presetRow, "Seed −", () => onSeedDelta(-1));
        previousSeed.TooltipText = "Regenerate this preset with the previous stable seed";
        Button nextSeed = AddButton(presetRow, "Seed +", () => onSeedDelta(1));
        nextSeed.TooltipText = "Regenerate this preset with the next stable seed";

        var targetRow = new HBoxContainer();
        column.AddChild(targetRow);
        AddButton(targetRow, "Assign idle tentacle", onAssignTarget);
        AddButton(targetRow, "Toggle target pull", onTogglePull);

        _status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(_status);
        column.AddChild(new HSeparator());
        _help = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _help.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(_help);
        RefreshDensity();
    }

    public void ToggleVisibility()
    {
        bool show = !_panel.Visible;
        _panel.Visible = show;
        _restoreButton.Visible = !show;
    }

    public void ToggleCompact()
    {
        _compact = !_compact;
        RefreshDensity();
    }

    public void SyncPreset(int index)
    {
        if (index >= 0 && index < _presetPicker.ItemCount)
            _presetPicker.Select(index);
    }

    public void UpdateStatus(
        string presetId,
        ulong seed,
        int bodyChunks,
        int tentacles,
        int totalSegments,
        float rawSupport,
        float effectiveSupport,
        float gravityCancellation,
        float directionalSupport,
        float driveScale,
        int locomotion,
        int arrived,
        int idle,
        int external,
        int stunned,
        int contacts,
        int passiveContacts,
        int activeGrips,
        int totalContactableSegments,
        int planted,
        int peeling,
        int reaching,
        int stuckCounter,
        float stuckAmount,
        bool stuckDetourActive,
        Vector3 stuckDetourDirection,
        int stuckEpisodeSerial,
        int movementEpisodeSerial,
        int startReplantSerial,
        int activeStartReplant,
        int movementGraceTicks,
        int tickQueries,
        int peakQueries,
        int queryBudget,
        bool atMoveTarget,
        int externalTentacle,
        bool pullTarget,
        string tentacleStates)
    {
        _summaryText =
            $"type  {presetId}    seed  {seed}\n" +
            $"morphology  body {bodyChunks} / tentacles {tentacles} / segments {totalSegments}\n" +
            $"support  raw {rawSupport,6:F3}  filtered {effectiveSupport,6:F3}  " +
            $"gravity cancel {gravityCancellation,6:P0}\n" +
            $"directional {directionalSupport,6:F3}  drive {driveScale,6:F3}  " +
            $"at target {atMoveTarget}\n" +
            $"duties  locomotion {locomotion} (arrived {arrived}) / idle {idle} / " +
            $"external {external} / stunned {stunned}\n" +
            $"segments  touch {contacts}/{totalContactableSegments}  " +
            $"passive {passiveContacts}  active grip {activeGrips}\n" +
            $"replant  planted {planted} / peeling {peeling} / reaching {reaching}    " +
            $"stuck {stuckCounter} ({stuckAmount:P0})\n" +
            $"detour  {stuckDetourActive}  episode {stuckEpisodeSerial}  " +
            $"side ({stuckDetourDirection.X:F2},{stuckDetourDirection.Y:F2}," +
            $"{stuckDetourDirection.Z:F2})\n" +
            $"move episode {movementEpisodeSerial}  grace {movementGraceTicks}  " +
            $"start replant {startReplantSerial} / active T{activeStartReplant}\n" +
            $"queries  {tickQueries}/{queryBudget}  peak {peakQueries}    " +
            $"debug target T{externalTentacle} pull:{pullTarget}";
        _tentacleStates = tentacleStates;
        RefreshStatus();
    }

    private void RefreshDensity()
    {
        _panel.CustomMinimumSize = new Vector2(_compact ? CompactWidth : FullWidth, 0f);
        _densityButton.Text = _compact ? "Full (F2)" : "Compact (F2)";
        _help.Text = _compact
            ? "F1 hide/show UI    F2 full/compact    WASD+Q/E move    RMB camera"
            : "F1  hide/show UI    F2  full/compact\n" +
              "WASD + Q/E  move in 3D    Shift + RMB  feed MoveTarget\n" +
              "Alt + 0..9 (Alt + -/= for 10/11)  stun by index    1 / 2 / 3  preset\n" +
              "B / N  previous / next seed    Space  Launch\n" +
              "T  Teleport    H  Shift    X  assign/clear debug target\n" +
              "P  toggle pull    I/J/K/L + U/O  move debug target\n" +
              "LMB  drag body chunk    F3  terrain queries\n" +
              "Hold RMB (without Shift)  free camera";
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_status is null)
            return;
        _status.Text = _compact || string.IsNullOrEmpty(_tentacleStates)
            ? _summaryText
            : _summaryText + "\ntentacles  " + _tentacleStates;
    }

    private static Button AddButton(Container parent, string text, Action callback)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
        };
        button.Pressed += callback;
        parent.AddChild(button);
        return button;
    }
}
