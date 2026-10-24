using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MacPartsFinder;

/// <summary>A plain message box with an OK button (Avalonia doesn't have one built in).</summary>
public static class MessageBox
{
    public static Task Show(Window owner, string text, string title)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80,
                              HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 520,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 16,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok }
            }
        };
        ok.Click += (_, _) => w.Close();
        return w.ShowDialog(owner);
    }
}
