using Godot;

namespace ProcAnimLab.Sandbox;

/// <summary>
/// 沙盒调试 UI 的焦点策略。调试下拉框只接受鼠标选择；关闭 Popup 后立即把键盘归还场景。
/// </summary>
public static class SandboxUiFocus
{
    public static void MakePointerOnly(OptionButton picker)
    {
        picker.FocusMode = Control.FocusModeEnum.None;
        picker.GetPopup().PopupHide += () => picker.GetViewport().GuiReleaseFocus();
    }
}
