using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MacPartsFinder;

/// <summary>Temporary API key entry for testing. Closes with true when the keys were saved.</summary>
public partial class KeysWindow : Window
{
    private readonly AppSettings _settings;

    // Needed by the XAML loader / previewer.
    public KeysWindow() : this(new AppSettings())
    {
    }

    public KeysWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        txtMouser.Text = settings.MouserApiKey;
        txtDigiKeyId.Text = settings.DigiKeyClientId;
        txtDigiKeySecret.Text = settings.DigiKeyClientSecret;
        txtElement14.Text = settings.Element14ApiKey;
        txtTmeToken.Text = settings.TmeToken;
        txtTmeSecret.Text = settings.TmeSecret;
    }

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        _settings.MouserApiKey = (txtMouser.Text ?? "").Trim();
        _settings.DigiKeyClientId = (txtDigiKeyId.Text ?? "").Trim();
        _settings.DigiKeyClientSecret = (txtDigiKeySecret.Text ?? "").Trim();
        _settings.Element14ApiKey = (txtElement14.Text ?? "").Trim();
        _settings.TmeToken = (txtTmeToken.Text ?? "").Trim();
        _settings.TmeSecret = (txtTmeSecret.Text ?? "").Trim();
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not save the keys: " + ex.Message, Title ?? "");
            return;
        }
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
