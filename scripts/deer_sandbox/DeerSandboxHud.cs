using System;
using Godot;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.DeerSandbox;

/// <summary>
/// Deer 白盒面板。只显示核心公开观测量并转发预设选择，不拥有运动状态。
/// </summary>
public sealed class DeerSandboxHud
{
    private OptionButton _presetPicker = null!;
    private Label _status = null!;

    public void Build(Node parent, string[] presetIds, Action<int> onPresetSelected)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        var panel = new PanelContainer
        {
            Position = new Vector2(14f, 14f),
            CustomMinimumSize = new Vector2(390f, 0f),
        };
        layer.AddChild(panel);

        var column = new VBoxContainer();
        panel.AddChild(column);
        column.AddChild(new Label
        {
            Text = "DEER MULTI-SEGMENT LEG LAB",
            ThemeTypeVariation = "HeaderSmall",
        });

        var presetRow = new HBoxContainer();
        column.AddChild(presetRow);
        presetRow.AddChild(new Label { Text = "Preset:" });
        _presetPicker = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        SandboxUiFocus.MakePointerOnly(_presetPicker);
        foreach (string presetId in presetIds)
        {
            _presetPicker.AddItem(presetId);
        }
        _presetPicker.ItemSelected += index => onPresetSelected((int)index);
        presetRow.AddChild(_presetPicker);

        _status = new Label();
        column.AddChild(_status);
        column.AddChild(new HSeparator());
        column.AddChild(new Label
        {
            Text =
                "WASD  move    Shift + RMB  feed MoveTarget\n" +
                "Space  Launch    T  Teleport test    H  Shift test\n" +
                "LMB  drag body chunk    F3  terrain rays\n" +
                "Hold RMB  free camera (WASD + E/Q, mouse look)\n" +
                "1 / 2 / 3  switch preset",
        });
    }

    public void SyncPreset(int index)
    {
        if (index >= 0 && index < _presetPicker.ItemCount)
        {
            _presetPicker.Select(index);
        }
    }

    public void UpdateStatus(
        string presetId,
        int plantedLegs,
        float totalSupport,
        float desiredHeight,
        float actualHeight,
        float hesitation,
        bool atTarget,
        string legState)
    {
        _status.Text =
            $"type  {presetId}\n" +
            $"planted  {plantedLegs}/4    support  {totalSupport,6:F3}\n" +
            $"height  actual {actualHeight,5:F2}m / target {desiredHeight,5:F2}m\n" +
            $"hesitation  {hesitation,6:P0}    at target  {atTarget}\n" +
            $"legs  {legState}";
    }
}
