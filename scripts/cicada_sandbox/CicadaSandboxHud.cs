using System;
using Godot;

namespace ProcAnimLab.CicadaSandbox;

/// <summary>
/// 蝉沙盒的轻量交互面板。只负责预设选择和状态显示，不拥有控制器状态。
/// </summary>
public sealed class CicadaSandboxHud
{
    private OptionButton _presetPicker = null!;
    private Label _status = null!;

    public void Build(Node parent, string[] presetNames, Action<int> onPresetSelected)
    {
        var layer = new CanvasLayer();
        parent.AddChild(layer);

        var panel = new PanelContainer
        {
            Position = new Vector2(14f, 14f),
            CustomMinimumSize = new Vector2(330f, 0f),
        };
        layer.AddChild(panel);

        var column = new VBoxContainer();
        panel.AddChild(column);

        var title = new Label
        {
            Text = "CICADA FLIGHT LAB",
            ThemeTypeVariation = "HeaderSmall",
        };
        column.AddChild(title);

        var presetRow = new HBoxContainer();
        column.AddChild(presetRow);
        presetRow.AddChild(new Label { Text = "Preset:" });

        _presetPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string name in presetNames)
        {
            _presetPicker.AddItem(name);
        }
        _presetPicker.ItemSelected += index => onPresetSelected((int)index);
        presetRow.AddChild(_presetPicker);

        _status = new Label();
        column.AddChild(_status);

        column.AddChild(new HSeparator());
        column.AddChild(new Label
        {
            Text =
                "WASD  horizontal flight    E / Q  rise / dive\n" +
                "Space  charge (fly) / take off (perched)\n" +
                "Shift + RMB  select perch surface\n" +
                "Hold RMB  free camera (WASD + E/Q, mouse look)\n" +
                "1 / 2  switch light / dark",
        });
    }

    public void SyncPreset(int index)
    {
        if (index >= 0 && index < _presetPicker.ItemCount)
        {
            _presetPicker.Select(index);
        }
    }

    public void UpdateStatus(string preset, string mode, string charge, float flightPower,
        float bankDegrees, bool hasPerch)
    {
        _status.Text =
            $"type  {preset}\n" +
            $"mode  {mode,-9} charge  {charge}\n" +
            $"flight power  {flightPower,5:P0}    bank  {bankDegrees,6:F1}°\n" +
            $"perch target  {(hasPerch ? "locked" : "none")}";
    }
}
