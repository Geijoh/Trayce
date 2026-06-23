using System.Windows.Forms;

namespace Trayce;

internal static class A11y
{
    public static void Button(Control control, string name, string? description = null)
    {
        control.TabStop = true;
        control.AccessibleRole = AccessibleRole.PushButton;
        control.AccessibleName = name;
        if (!string.IsNullOrWhiteSpace(description)) control.AccessibleDescription = description;
    }

    public static void CheckBox(Control control, string name, string? description = null)
    {
        control.TabStop = true;
        control.AccessibleRole = AccessibleRole.CheckButton;
        control.AccessibleName = name;
        if (!string.IsNullOrWhiteSpace(description)) control.AccessibleDescription = description;
    }

    public static void Slider(Control control, string name, string? description = null)
    {
        control.TabStop = true;
        control.AccessibleRole = AccessibleRole.Slider;
        control.AccessibleName = name;
        if (!string.IsNullOrWhiteSpace(description)) control.AccessibleDescription = description;
    }

    public static void InvokeOnEnterOrSpace(KeyEventArgs e, Action invoke)
    {
        if (e.KeyCode is not (Keys.Enter or Keys.Space)) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        invoke();
    }
}
