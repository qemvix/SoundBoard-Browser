using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundboardBrowser;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "SoundboardBrowser";
    const string SettingsKey = @"Software\SoundboardBrowser";
    const string MicIdValue = "MicrophoneId";

    readonly MMDeviceEnumerator _enum = new();
    List<MMDevice> _mics = new(), _outs = new();
    AudioEngine _engine;
    bool _ready;

    public MainWindow()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var result = MessageBox.Show(
                "Microsoft Edge WebView2 Runtime is required to run this app. Open the download page?",
                "WebView2 Runtime missing",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
            {
                Process.Start(new ProcessStartInfo(
                    "https://developer.microsoft.com/microsoft-edge/webview2/")
                {
                    UseShellExecute = true
                });
            }

            Application.Current.Shutdown();
            return;
        }

        InitializeComponent();

        if (Environment.GetCommandLineArgs().Contains("--minimized"))
            WindowState = WindowState.Minimized;

        Loaded += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        _mics = _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        _outs = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        MicBox.ItemsSource = _mics.Select(d => d.FriendlyName).ToList();
        OutBox.ItemsSource = _outs.Select(d => d.FriendlyName).ToList();

        // Restore the saved microphone if it is still connected.
        string savedMicId = null;

        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            savedMicId = key?.GetValue(MicIdValue) as string;

        int savedIndex = savedMicId == null
            ? -1
            : _mics.FindIndex(d => d.ID == savedMicId);

        if (savedIndex >= 0)
        {
            MicBox.SelectedIndex = savedIndex;
        }
        else
        {
            // If no saved mic is available, use the Windows communications default.
            try
            {
                var def = _enum.GetDefaultAudioEndpoint(
                    DataFlow.Capture, Role.Communications);

                MicBox.SelectedIndex = _mics.FindIndex(d => d.ID == def.ID);
            }
            catch
            {
                // No default microphone is available.
            }
        }

        OutBox.SelectedIndex = _outs.FindIndex(d =>
            d.FriendlyName.Contains(
                "CABLE Input", StringComparison.OrdinalIgnoreCase));

        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            AutoStart.IsChecked = k?.GetValue(RunName) != null;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundboardBrowser");

        var env = await CoreWebView2Environment.CreateAsync(
            null,
            dataDir,
            new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required"));

        await Web.EnsureCoreWebView2Async(env);

        Web.CoreWebView2.SourceChanged += (_, _) =>
            UrlBox.Text = Web.CoreWebView2.Source;

        Web.CoreWebView2.Navigate("https://www.google.com");

        _ready = true;
        StartEngine();
    }

    void SaveSelectedMic()
    {
        if (MicBox.SelectedIndex < 0 ||
            MicBox.SelectedIndex >= _mics.Count)
            return;

        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key?.SetValue(MicIdValue, _mics[MicBox.SelectedIndex].ID);
    }

    void StartEngine()
    {
        if (!_ready)
            return;

        _engine?.Dispose();
        _engine = null;

        if (MicBox.SelectedIndex < 0 || OutBox.SelectedIndex < 0)
        {
            Status.Text =
                "Choose your mic and the virtual mic output. If you don't see " +
                "\"CABLE Input\", install VB-Cable and restart this app.";
            return;
        }

        try
        {
            _engine = new AudioEngine(
                _mics[MicBox.SelectedIndex],
                _outs[OutBox.SelectedIndex],
                Web.CoreWebView2.BrowserProcessId);

            _engine.Failed += msg =>
                Dispatcher.Invoke(() =>
                    Status.Text = "Browser audio capture failed: " + msg);

            ApplyVolumes();
            _engine.Start();

            Status.Text =
                "Live. In Discord, set your input device to \"CABLE Output\".";
        }
        catch (Exception ex)
        {
            Status.Text = "Audio error: " + ex.Message;
        }
    }

    void ApplyVolumes()
    {
        if (_engine == null)
            return;

        _engine.MicGain.Volume =
            MuteMic.IsChecked == true ? 0f : (float)MicSlider.Value;

        _engine.BrowserGain.Volume = (float)BrowserSlider.Value;
    }

    void Device_Changed(object s, SelectionChangedEventArgs e)
    {
        // Ignore selection events that occur while the device lists are being set up.
        if (s == MicBox)
            SaveSelectedMic();

        StartEngine();
    }

    void Vol_Changed(object s, RoutedEventArgs e) => ApplyVolumes();

    void AutoStart_Click(object s, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);

        if (AutoStart.IsChecked == true)
            k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            k.DeleteValue(RunName, false);
    }

    void Back_Click(object s, RoutedEventArgs e)
    {
        if (Web.CanGoBack)
            Web.GoBack();
    }

    void Forward_Click(object s, RoutedEventArgs e)
    {
        if (Web.CanGoForward)
            Web.GoForward();
    }

    void Reload_Click(object s, RoutedEventArgs e) => Web.Reload();

    void UrlBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Web.CoreWebView2 == null)
            return;

        var t = UrlBox.Text.Trim();
        if (t.Length == 0)
            return;

        if (!t.Contains("://"))
        {
            t = t.Contains('.') && !t.Contains(' ')
                ? "https://" + t
                : "https://www.google.com/search?q=" +
                  Uri.EscapeDataString(t);
        }

        Web.CoreWebView2.Navigate(t);
    }

    void Window_Closing(object s, System.ComponentModel.CancelEventArgs e)
    {
        _engine?.Dispose();
    }
}