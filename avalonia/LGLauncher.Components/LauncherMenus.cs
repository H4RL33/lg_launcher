using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LGLauncher.Components;

public static class LauncherMenus
{
    public static ContextMenu Create(PlacementMode placement = PlacementMode.Pointer) =>
        new FrostedContextMenu { Placement = placement, Classes = { "lg-menu" } };

    public static MenuItem AddAction(this ContextMenu menu, string label, Action action, string? iconKey = null, bool destructive = false)
    {
        var item = new MenuItem { Header = label, Classes = { "lg-menu-item" } };
        if (iconKey != null && Application.Current?.FindResource(iconKey) is Geometry geometry)
            item.Icon = new SymbolIcon { Data = geometry, Width = 16, Height = 16 };
        if (destructive) item.Classes.Add("destructive");
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    public static void AddSeparator(this ContextMenu menu) =>
        menu.Items.Add(new Separator { Classes = { "lg-menu-separator" } });

    public static void UseTextEditingMenu(this TextBox field)
    {
        var menu = Create();
        AddEdit("Cut", field.Cut, "LG.Icon.Cut", TextBox.CutGesture, TextBox.CanCutProperty);
        AddEdit("Copy", field.Copy, "LG.Icon.Copy", TextBox.CopyGesture, TextBox.CanCopyProperty);
        AddEdit("Paste", field.Paste, "LG.Icon.Paste", TextBox.PasteGesture, TextBox.CanPasteProperty);
        field.ContextFlyout = null;
        field.ContextMenu = menu;

        void AddEdit(string label, Action command, string icon, KeyGesture? gesture, AvaloniaProperty<bool> enabled)
        {
            var item = menu.AddAction(label, command, icon);
            item.InputGesture = gesture;
            item.Bind(InputElement.IsEnabledProperty, field.GetObservable(enabled));
        }
    }
}
