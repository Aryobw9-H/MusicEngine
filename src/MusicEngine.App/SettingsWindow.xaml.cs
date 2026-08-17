namespace MusicEngine.App;

using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Documents;
using System.Windows.Media;
using Configuration;
using Models;

public partial class SettingsWindow : Window
{
    private string _selectedAccent = "green";

    public SettingsWindow()
    {
        InitializeComponent();

        var cfg = AppConfig.Load();
        OutputBox.Text = cfg.OutputDirectory;
        ProxyBox.Text = cfg.ProxyUrl ?? "";
        CookiesBox.Text = cfg.CookiesBrowser ?? "";
        PersianIndexBox.IsChecked = cfg.EnablePersianIndex;
        ToastsBox.IsChecked = cfg.DownloadToasts;
        TrayBox.IsChecked = cfg.MinimizeToTray;
        ClipboardBox.IsChecked = cfg.ClipboardMonitor;
        ParallelBox.SelectedIndex = Math.Clamp(cfg.MaxParallelDownloads, 1, 6) - 1;
        BitrateBox.SelectedIndex = cfg.BitrateKbps switch { 128 => 0, 192 => 1, _ => 2 };
        TemplateBox.SelectedIndex = (int)cfg.FilenameTemplate;

        SrcItunes.IsChecked = cfg.IsSourceEnabled(ProviderId.ITunes);
        SrcDeezer.IsChecked = cfg.IsSourceEnabled(ProviderId.Deezer);
        SrcYoutube.IsChecked = cfg.IsSourceEnabled(ProviderId.YouTube);
        SrcSoundcloud.IsChecked = cfg.IsSourceEnabled(ProviderId.SoundCloud);
        SrcRadioJavan.IsChecked = cfg.IsSourceEnabled(ProviderId.RadioJavan);
        SrcNex1.IsChecked = cfg.IsSourceEnabled(ProviderId.Nex1Music);
        SrcPersianSites.IsChecked = cfg.IsSourceEnabled(ProviderId.PersianSites);
        SrcPersianIndex.IsChecked = cfg.IsSourceEnabled(ProviderId.PersianIndex);

        _selectedAccent = cfg.Accent;
        BuildAccentPicker(cfg.Accent);
    }

    private void BuildAccentPicker(string selected)
    {
        foreach (var (key, _, hex) in AccentTheme.Presets)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            var btn = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = key,
                ToolTip = key,
            };
            btn.Template = MakeAccentTemplate(brush, key == selected);
            btn.Content = key == selected ? "✓" : null;
            btn.Click += (_, _) =>
            {
                _selectedAccent = key;
                foreach (var child in AccentPanel.Children.OfType<Button>())
                {
                    var childKey = (string)child.Tag;
                    child.Content = childKey == key ? "✓" : null;
                    child.Template = MakeAccentTemplate((SolidColorBrush)child.Background, childKey == key);
                }
            };
            btn.Background = brush; // kept as the color carrier for re-templating
            AccentPanel.Children.Add(btn);
        }
    }

    private static ControlTemplate MakeAccentTemplate(SolidColorBrush brush, bool selected)
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        factory.SetValue(Border.BackgroundProperty, brush);
        factory.SetValue(Border.BorderBrushProperty, Brushes.White);
        factory.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2 : 0));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(TextElement.ForegroundProperty, Brushes.Black);
        cp.SetValue(TextElement.FontWeightProperty, FontWeights.Bold);
        factory.AppendChild(cp);
        return new ControlTemplate(typeof(Button)) { VisualTree = factory };
    }

    public string OutputDirectory => OutputBox.Text.Trim();
    public string ProxyUrl => ProxyBox.Text.Trim();
    public string CookiesBrowser => CookiesBox.Text.Trim();
    public bool EnablePersianIndex => PersianIndexBox.IsChecked == true;
    public int ParallelDownloads => ParallelBox.SelectedIndex + 1;
    public int Bitrate => BitrateBox.SelectedIndex switch { 0 => 128, 1 => 192, _ => 320 };
    public FilenameTemplate Template => (FilenameTemplate)TemplateBox.SelectedIndex;
    public string Accent => _selectedAccent;
    public bool ClipboardMonitor => ClipboardBox.IsChecked == true;
    public bool MinimizeToTray => TrayBox.IsChecked == true;
    public bool DownloadToasts => ToastsBox.IsChecked == true;

    public IReadOnlyList<string> DisabledSources
    {
        get
        {
            var list = new List<string>();
            if (SrcItunes.IsChecked != true) list.Add(ProviderId.ITunes.ToString());
            if (SrcDeezer.IsChecked != true) list.Add(ProviderId.Deezer.ToString());
            if (SrcYoutube.IsChecked != true) list.Add(ProviderId.YouTube.ToString());
            if (SrcSoundcloud.IsChecked != true) list.Add(ProviderId.SoundCloud.ToString());
            if (SrcRadioJavan.IsChecked != true) list.Add(ProviderId.RadioJavan.ToString());
            if (SrcNex1.IsChecked != true) list.Add(ProviderId.Nex1Music.ToString());
            if (SrcPersianSites.IsChecked != true) list.Add(ProviderId.PersianSites.ToString());
            if (SrcPersianIndex.IsChecked != true) list.Add(ProviderId.PersianIndex.ToString());
            return list;
        }
    }

    public IReadOnlyList<string> EnabledSources
    {
        get
        {
            var list = new List<string>();
            if (SrcItunes.IsChecked == true) list.Add(ProviderId.ITunes.ToString());
            if (SrcDeezer.IsChecked == true) list.Add(ProviderId.Deezer.ToString());
            if (SrcYoutube.IsChecked == true) list.Add(ProviderId.YouTube.ToString());
            if (SrcSoundcloud.IsChecked == true) list.Add(ProviderId.SoundCloud.ToString());
            if (SrcRadioJavan.IsChecked == true) list.Add(ProviderId.RadioJavan.ToString());
            if (SrcNex1.IsChecked == true) list.Add(ProviderId.Nex1Music.ToString());
            if (SrcPersianSites.IsChecked == true) list.Add(ProviderId.PersianSites.ToString());
            if (SrcPersianIndex.IsChecked == true) list.Add(ProviderId.PersianIndex.ToString());
            return list;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the music folder",
            InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : null,
        };
        if (dialog.ShowDialog(this) == true)
            OutputBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
