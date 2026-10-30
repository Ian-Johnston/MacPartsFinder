using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MacPartsFinder;

/// <summary>What the user clicked in <see cref="MessageBox.AskSave"/>.</summary>
public enum SaveChoice
{
    Save,
    DontSave,
    Cancel
}

/// <summary>Plain message boxes (Avalonia doesn't have them built in): OK, Yes / No, and Save / Don't Save / Cancel.</summary>
public static class MessageBox
{
    public static Task Show(Window owner, string text, string title)
    {
        return Ask(owner, text, title, ("OK", true, true));
    }

    /// <summary>Yes / No. Returns true for Yes; No, Esc or closing the box is false.</summary>
    public static async Task<bool> AskYesNo(Window owner, string text, string title)
    {
        return await Ask(owner, text, title, ("No", false, true), ("Yes", true, false)) == 1;
    }

    /// <summary>Save / Don't Save / Cancel, as Mac apps ask about unsaved changes. Esc or closing the box is Cancel.</summary>
    public static async Task<SaveChoice> AskSave(Window owner, string text, string title)
    {
        return await Ask(owner, text, title, ("Don't Save", false, false), ("Cancel", false, true), ("Save", true, false)) switch
        {
            0 => SaveChoice.DontSave,
            2 => SaveChoice.Save,
            _ => SaveChoice.Cancel
        };
    }

    /// <summary>Shows the text with these buttons (left to right) and returns the index of the one clicked, or -1 if closed.</summary>
    private static async Task<int> Ask(Window owner, string text, string title, params (string Text, bool IsDefault, bool IsCancel)[] buttons)
    {
        var chosen = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
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
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row }
            }
        };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, IsDefault = buttons[i].IsDefault, IsCancel = buttons[i].IsCancel, MinWidth = 80,
                                 HorizontalContentAlignment = HorizontalAlignment.Center };
            // (Esc clicks the IsCancel button, so it reports that button - e.g. No, or Cancel.)
            b.Click += (_, _) =>
            {
                chosen = index;
                w.Close();
            };
            row.Children.Add(b);
        }
        await w.ShowDialog(owner);
        return chosen;
    }
}
