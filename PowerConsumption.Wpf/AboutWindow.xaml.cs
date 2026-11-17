using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Velopack;
using Velopack.Sources;

namespace PowerConsumption.Wpf;

/// <summary>Version, build time, copyright and credits. The texts are in the XAML; only the version is filled in here.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // An .ico holds several sizes and WPF would pick a small one; take the largest so it is sharp at 64 px.
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AppIcon.Source = decoder.Frames.MaxBy(f => f.PixelWidth);

        VersionText.Text = BuildInfo.Display;
        CopyrightText.Text = BuildInfo.Copyright;
    }

    private const string RepoUrl = "https://github.com/kreaton/PowerConsumption";

    /// <summary>Checks GitHub releases through Velopack and offers to install a newer version.</summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        var updates = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        if (!updates.IsInstalled)
        {
            UpdateStatus.Text = "Updates only work in the installed app.";
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var newVersion = await updates.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                UpdateStatus.Text = "You have the latest version.";
                return;
            }

            var version = newVersion.TargetFullRelease.Version;
            var answer = MessageBox.Show(this,
                $"Version {version} is available. Download and install it now? The app restarts afterwards.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                UpdateStatus.Text = $"Version {version} is available.";
                return;
            }

            UpdateStatus.Text = $"Downloading {version}…";
            await updates.DownloadUpdatesAsync(newVersion);
            updates.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Update failed: " + ex.Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Opens a credit link in the default browser (WPF hyperlinks do nothing on their own outside a Frame).</summary>
    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
