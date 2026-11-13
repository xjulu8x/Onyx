using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopOverlay;

public class StartApp
{
    public string Name;
    public string Launch;
    public ImageSource Icon;
    public string Glyph;   // used instead of Icon for built-in entries
}

/// <summary>Own Start menu (opened by the Windows key or the start button): search, pinned apps, all apps and power options.</summary>
public partial class StartMenuWindow : Window
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private List<StartApp> _all = new();
    private List<StartApp> _pinned = new();
    private DateTime _loadedAt = DateTime.Now;
    private bool _closing;

    public StartMenuWindow()
    {
        InitializeComponent();
        UserName.Text = Environment.UserName;
        SourceInitialized += (_, _) =>
        {
            // Hidden from Alt+Tab, but still focusable so the search box can take typing.
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };
        LoadAppsInBackground();
    }

    // ---------- open / close ----------

    public void Toggle(bool activate = true)
    {
        if (IsVisible && !_closing) { CloseMenu(); return; }
        // Pick up newly installed apps: refresh the list in the background when it is a few minutes old.
        if ((DateTime.Now - _loadedAt).TotalMinutes > 3) { _loadedAt = DateTime.Now; LoadAppsInBackground(); }
        _closing = false;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.WorkArea.Bottom - Height + 4;
        Search.Text = "";
        Build();
        bool fromHidden = !IsVisible;
        ShowActivated = activate;
        Show();
        if (activate)
        {
            Win32.Activate(new WindowInteropHelper(this).Handle);
            Search.Focus();
            Keyboard.Focus(Search);
        }
        Theme.Enter(Panel, Zoom, Slide, 22, fromHidden); // up out of the start button, growing from the bottom edge
    }

    private void CloseMenu()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        var fade = Theme.Leave(Panel, Zoom, Slide, 22);
        // The rows are rebuilt on every open, so they don't have to stay in memory while closed.
        fade.Completed += (_, _) => { if (_closing) { Hide(); _closing = false; Sections.Children.Clear(); } };
    }

    private void Window_Deactivated(object sender, EventArgs e) => CloseMenu();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseMenu();
        else if (e.Key == Key.Enter && Filtered().FirstOrDefault() is { } first) Launch(first);
    }

    // ---------- content ----------

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Build();
    }

    private IEnumerable<StartApp> Filtered()
    {
        var q = Search.Text.Trim();
        return q.Length == 0 ? _all : _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(a => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    private void Build()
    {
        Sections.Children.Clear();
        if (Search.Text.Trim().Length > 0)
        {
            AddSection("Ergebnisse", Filtered().Take(30));
            return;
        }
        if (_pinned.Count > 0) AddSection("Angeheftet", _pinned);
        AddSection("Alle Apps", _all);
    }

    private void AddSection(string title, IEnumerable<StartApp> apps)
    {
        Sections.Children.Add(new TextBlock { Text = "+ " + title.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(8, 6, 0, 8) });
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var app in apps) wrap.Children.Add(Tile(app));
        if (wrap.Children.Count == 0)
            wrap.Children.Add(new TextBlock { Text = "Keine Treffer", Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(8, 0, 0, 0) });
        Sections.Children.Add(wrap);
    }

    private Button Tile(StartApp app)
    {
        var panel = new StackPanel();
        if (app.Icon != null)
        {
            var img = new Image { Source = app.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 8) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            panel.Children.Add(img);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = app.Glyph ?? "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = app.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, HorizontalAlignment = HorizontalAlignment.Center,
        });
        var b = new Button { Style = (Style)FindResource("Tile"), Content = panel, ToolTip = app.Name };
        b.Click += (_, _) => Launch(app);
        return b;
    }

    private void Launch(StartApp app)
    {
        try { Process.Start(new ProcessStartInfo(app.Launch) { UseShellExecute = true }); } catch { }
        CloseMenu();
    }

    // ---------- footer ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => Launch(new StartApp { Launch = "ms-settings:" });

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PowerBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string glyph, string header, string exe, string args)
        {
            var item = new MenuItem { Header = header, Icon = glyph };
            item.Click += (_, _) => { CloseMenu(); Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); };
            menu.Items.Add(item);
        }
        Add("", "Sperren", "rundll32.exe", "user32.dll,LockWorkStation");
        Add("", "Energie sparen", "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
        Add("", "Neu starten", "shutdown.exe", "/r /t 0");
        Add("", "Herunterfahren", "shutdown.exe", "/s /t 0");
        menu.IsOpen = true;
    }

    // ---------- app discovery ----------

    private void LoadAppsInBackground()
    {
        var t = new Thread(() =>
        {
            var apps = DiscoverApps();
            var pinned = TaskbarApps.Pinned()
                .Select(p => new StartApp { Name = p.Name, Launch = p.LaunchPath, Icon = p.Icon }).ToList();
            Dispatcher.BeginInvoke(() => { _all = apps; _pinned = pinned; if (IsVisible) Build(); });
        });
        t.SetApartmentState(ApartmentState.STA); // WScript.Shell is a COM object
        t.IsBackground = true;
        t.Start();
    }

    private static List<StartApp> DiscoverApps()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };
        var skip = new[] { "uninstall", "deinstall", "readme", "help", "hilfe", "website", "manual" };
        var byName = new Dictionary<string, StartApp>(StringComparer.OrdinalIgnoreCase)
        {
            ["Einstellungen"] = new() { Name = "Einstellungen", Launch = "ms-settings:", Glyph = "" },
            ["Rechner"] = new() { Name = "Rechner", Launch = "calc.exe", Glyph = "" },
            ["Microsoft Store"] = new() { Name = "Microsoft Store", Launch = "ms-windows-store:", Glyph = "" },
        };
        dynamic shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { }

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); } catch { continue; }
            foreach (var lnk in files)
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (byName.ContainsKey(name) || skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                ImageSource icon = null;
                try
                {
                    var sc = shell?.CreateShortcut(lnk);
                    string loc = Environment.ExpandEnvironmentVariables((string)(sc?.IconLocation ?? ""));
                    string target = Environment.ExpandEnvironmentVariables((string)(sc?.TargetPath ?? ""));
                    int comma = loc.LastIndexOf(',');
                    if (comma > 0 && File.Exists(loc[..comma])) icon = TaskbarApps.IconFor(loc[..comma], int.TryParse(loc[(comma + 1)..], out var i) ? i : 0, 64);
                    if (icon == null && File.Exists(target)) icon = TaskbarApps.IconFor(target, 0, 64);
                }
                catch { }
                icon ??= TaskbarApps.ShellIcon(lnk);
                byName[name] = new StartApp { Name = name, Launch = lnk, Icon = icon };
            }
        }

        // Everything else Windows knows (Store apps, games, programs without a shortcut in the Start Menu folders).
        foreach (var (id, name) in TaskbarApps.AllApps())
        {
            if (byName.ContainsKey(name) || skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            // Plain URLs/documents listed in Start are not apps.
            if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                || id.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)) continue;
            byName[name] = new StartApp { Name = name, Launch = @"shell:AppsFolder\" + id, Icon = TaskbarApps.AppIcon(id, 64) };
        }
        return byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
