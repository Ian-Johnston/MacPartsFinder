using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MacPartsFinder;

public partial class AboutWindow : Window
{
    private const string DonateUrl =
        "https://www.paypal.com/donate/?business=ian%40ianj.net&no_recurring=0&item_name=Support+MacPartsFinder&currency_code=GBP";

    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url }) WebHelper.OpenInBrowser(url);
    }

    private void BtnDonate_Click(object? sender, RoutedEventArgs e)
    {
        WebHelper.OpenInBrowser(DonateUrl);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
