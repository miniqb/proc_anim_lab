using System;
using Godot;
using ProcAnimLab.Sandbox;

namespace ProcAnimLab.RatFiendSandbox;

/// <summary>鼠煞沙盒的轻量交互面板：预设选择 + 调试状态显示，不拥有控制器状态。</summary>
public sealed class RatFiendSandboxHud
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
            CustomMinimumSize = new Vector2(380f, 0f),
        };
        layer.AddChild(panel);

        var column = new VBoxContainer();
        panel.AddChild(column);

        column.AddChild(new Label
        {
            Text = "RATFIEND LAB",
            ThemeTypeVariation = "HeaderSmall",
        });

        var presetRow = new HBoxContainer();
        column.AddChild(presetRow);
        presetRow.AddChild(new Label { Text = "Preset:" });
        _presetPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        SandboxUiFocus.MakePointerOnly(_presetPicker);
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
                "WASD  run（Shift = 慢走看驼背摆臂）\n" +
                "K  sever next limb（腿→臂 演示序）    R  respawn 复原\n" +
                "T  set grab target（鼠标点地）    Y  clear target\n" +
                "M  toggle mouth drive    B  launch (knockback)\n" +
                "Hold RMB  free camera    1/2/3/4  presets",
        });
    }

    public void SyncPreset(int index)
    {
        if (index >= 0 && index < _presetPicker.ItemCount)
        {
            _presetPicker.Select(index);
        }
    }

    public void UpdateStatus(string text) => _status.Text = text;
}
