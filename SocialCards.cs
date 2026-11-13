using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopOverlay;

/// <summary>A row of the friends cards: status dot, avatar, name and what the person is doing.</summary>
internal static class FriendRow
{
    public static readonly Color Playing = Color.FromRgb(0x30, 0xD1, 0x58), Online = Color.FromRgb(0x4F, 0xA8, 0xFF), Away = Color.FromRgb(0xFF, 0xB3, 0x40),
        Busy = Color.FromRgb(0xFF, 0x45, 0x3A), Offline = Color.FromRgb(0x55, 0x55, 0x55);

    public static FrameworkElement Make(Color state, string avatar, string name, string detail, string badge = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        var photo = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) };
        if (!string.IsNullOrEmpty(avatar))
            try { photo.Background = new ImageBrush(new BitmapImage(new Uri(avatar)) { CacheOption = BitmapCacheOption.OnLoad, DecodePixelWidth = 44 }); } catch { }
        face.Children.Add(photo);
        face.Children.Add(new Rectangle
        {
            Width = 7, Height = 7, Fill = new SolidColorBrush(state), Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)), StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2),
        });
        row.Children.Add(face);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 8.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (badge != null)
        {
            var b = new TextBlock { Text = badge, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(b, 2);
            row.Children.Add(b);
        }
        return row;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = "+ " + text.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 9, Opacity = 0.55, Margin = new Thickness(0, 4, 0, 6),
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text, FontFamily = Theme.Mono, FontSize = 9.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };
}

/// <summary>
/// Steam friends who are online and what they play. No key needed: the account comes from the Steam app on this PC,
/// the friends from the public friends page. Asked every 90 seconds while the card can be seen.
/// </summary>
public sealed class SteamCard : Card
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Theme.Tag("");
    private DateTime _fetched;

    public SteamCard() : base("Steam", "+ Steam / Freunde", 300, 300)
    {
        Meta.Children.Add(_count);
        SetBody(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false });
        Every(TimeSpan.FromSeconds(90), Fetch);
        Loaded += (_, _) => Fetch();
    }

    protected override void OnResume() { if ((DateTime.Now - _fetched).TotalSeconds > 55) Fetch(); }

    public void Refresh() => Fetch();

    private async void Fetch()
    {
        // No key needed: the account comes from the Steam app on this PC (or a profile link from the settings),
        // the friends from your public friends page on steamcommunity.com.
        string profile = ProfileUrl();
        if (profile == null)
        {
            Show("STEAM NICHT GEFUNDEN.\nSTEAM STARTEN UND ANMELDEN\nODER PROFIL-LINK IN DEN EINSTELLUNGEN.");
            return;
        }
        _fetched = DateTime.Now;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, profile + "/friends/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.AcceptLanguage.ParseAdd("en");
            using var response = await Http.SendAsync(request);
            string html = await response.Content.ReadAsStringAsync();
            if (!html.Contains("friend_block_v2"))
            {
                Show(html.Contains("private") || html.Contains("profile_private")
                    ? "FREUNDESLISTE IST PRIVAT.\nIN STEAM: PROFIL > PRIVATSPHÄRE >\nFREUNDESLISTE: ÖFFENTLICH."
                    : "KEINE FREUNDE GEFUNDEN.");
                return;
            }
            var people = new List<(int Order, Color State, string Avatar, string Name, string Detail)>();
            int total = 0;
            foreach (var block in html.Split("class=\"selectable friend_block_v2 persona ").Skip(1))
            {
                total++;
                string state = block[..Math.Max(0, block.IndexOf('"'))].Trim();
                if (state.StartsWith("offline")) continue;
                string name = System.Net.WebUtility.HtmlDecode(Between(block, "friend_block_content\">", "<br") ?? "?").Trim();
                string avatar = Between(block, "<img src=\"", "\"");
                string game = Between(block, "friend_game_link\">", "<");
                bool playing = state.StartsWith("in-game") && game != null;
                var color = playing ? FriendRow.Playing : state.Contains("away") || state.Contains("snooze") ? FriendRow.Away : FriendRow.Online;
                people.Add((playing ? 0 : 1, color, avatar, name, playing ? "spielt " + System.Net.WebUtility.HtmlDecode(game).Trim() : "online"));
            }
            _list.Children.Clear();
            _count.Text = $"{people.Count}/{total} ONLINE";
            foreach (var p in people.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(9))
                _list.Children.Add(FriendRow.Make(p.State, p.Avatar, p.Name, p.Detail));
            if (people.Count == 0) _list.Children.Add(FriendRow.Note("GERADE IST NIEMAND ONLINE."));
            if (people.Count > 9) _list.Children.Add(FriendRow.Note($"+ {people.Count - 9} WEITERE"));
        }
        catch
        {
            Show("KEINE VERBINDUNG ZU STEAM");
            _fetched = DateTime.Now.AddSeconds(-40);
        }
    }

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return null;
        a += start.Length;
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? null : text[a..b];
    }

    private void Show(string note)
    {
        _list.Children.Clear();
        _count.Text = "";
        _list.Children.Add(FriendRow.Note(note));
    }

    /// <summary>
    /// Your profile: a link from the settings if given, otherwise the account signed in to Steam on this PC
    /// (the running Steam app, or the last login it remembers).
    /// </summary>
    public static string ProfileUrl()
    {
        var own = Settings.Current.SteamId?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(own))
        {
            if (own.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return own.Replace("/friends", "");
            return own.Length == 17 && own.All(char.IsDigit) ? "https://steamcommunity.com/profiles/" + own : "https://steamcommunity.com/id/" + own;
        }
        return LocalAccount() is { } id ? "https://steamcommunity.com/profiles/" + id : null;
    }

    public static string LocalAccount()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                if (key?.GetValue("ActiveUser") is int account && account != 0) return (76561197960265728L + (uint)account).ToString();
            using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string path)
            {
                var file = System.IO.Path.Combine(path.Replace('/', '\\'), "config", "loginusers.vdf");
                if (System.IO.File.Exists(file))
                {
                    // The user marked "MostRecent" "1", or simply the first one.
                    string text = System.IO.File.ReadAllText(file), best = null;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}"))
                    {
                        best ??= m.Groups[1].Value;
                        if (m.Groups[2].Value.Contains("\"MostRecent\"\t\t\"1\"") || System.Text.RegularExpressions.Regex.IsMatch(m.Groups[2].Value, "\"MostRecent\"\\s*\"1\"")) return m.Groups[1].Value;
                    }
                    return best;
                }
            }
        }
        catch { }
        return null;
    }

    protected override Point DefaultPlace(Rect w) => new(w.Left + 16, w.Top + 636);
}

/// <summary>
/// Your Discord server through your own bot: who is online, what they play and who sits in which voice channel.
/// Discord only gives friend lists and private calls to apps it approved, so a bot on a shared server is the way.
/// The bot connects to the Discord gateway and gets every change pushed, so nothing is polled.
/// </summary>
public sealed class DiscordCard : Card
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Theme.Tag("");
    private ClientWebSocket _socket;
    private CancellationTokenSource _stop;
    private string _note = "";

    // What the gateway told us about the (first) server.
    private string _guild = "";
    private readonly Dictionary<string, string> _names = new(), _avatars = new(), _status = new(), _activity = new(), _channels = new();
    private readonly Dictionary<string, (string Channel, bool Mute, bool Deaf, bool Video)> _voice = new();

    public DiscordCard() : base("Discord", "+ Discord", 300, 320)
    {
        Meta.Children.Add(_count);
        SetBody(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false });
        Loaded += (_, _) => Connect();
        Closed += (_, _) => _stop?.Cancel();
        Every(TimeSpan.FromSeconds(5), Draw); // redraws what the gateway changed meanwhile, at most every few seconds
    }

    public void Refresh() { _stop?.Cancel(); Connect(); }

    /// <summary>The bot's application id: the first part of a bot token is its user id in base64. Null when it is no token.</summary>
    public static string AppId(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var first = token.Trim().Split('.');
        if (first.Length != 3) return null;
        try
        {
            string b64 = first[0].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            string id = Encoding.ASCII.GetString(Convert.FromBase64String(b64));
            return id.Length >= 16 && id.All(char.IsDigit) ? id : null;
        }
        catch { return null; }
    }

    private async void Connect()
    {
        _stop?.Cancel();
        var token = Settings.Current.DiscordToken?.Trim();
        if (string.IsNullOrEmpty(token)) { _note = "DISCORD-BOT VERBINDEN:\nEINSTELLUNGEN > WIDGETS > FREUNDE."; Draw(); return; }
        var stop = _stop = new CancellationTokenSource();
        int wait = 3;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                _note = "VERBINDE …";
                Draw();
                await Run(token, stop.Token);
                wait = 3;
            }
            catch (WebSocketException) { _note = "VERBINDUNG UNTERBROCHEN"; }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { _note = e.Message.ToUpperInvariant(); }
            Draw();
            try { await Task.Delay(TimeSpan.FromSeconds(wait), stop.Token); } catch { return; }
            wait = Math.Min(wait * 2, 120);
        }
    }

    private async Task Run(string token, CancellationToken stop)
    {
        using var socket = _socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), stop);
        int? seq = null;
        Task beat = null;
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            using var message = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, stop);
                if (result.MessageType == WebSocketMessageType.Close) throw new Exception(Close(socket.CloseStatus, socket.CloseStatusDescription));
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            var root = doc.RootElement;
            if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) seq = s.GetInt32();
            switch (root.GetProperty("op").GetInt32())
            {
                case 10: // hello: start the heartbeat, then log in with the intents we need
                    int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                    beat = Heartbeat(socket, interval, () => seq, stop);
                    const int intents = 1 | 2 | 128 | 256; // guilds, members, voice states, presences
                    await Send(socket, new { op = 2, d = new { token, intents, properties = new { os = "windows", browser = "onyx", device = "onyx" } } }, stop);
                    break;
                case 0:
                    Handle(root.GetProperty("t").GetString(), root.GetProperty("d"));
                    break;
                case 7: case 9: throw new Exception("NEU VERBINDEN");
            }
        }
        _ = beat;
    }

    private static string Close(WebSocketCloseStatus? status, string text) => (int?)status switch
    {
        4004 => "TOKEN UNGÜLTIG",
        4014 => "IM DEVELOPER-PORTAL PRESENCE- UND MEMBERS-INTENT EINSCHALTEN",
        _ => "GETRENNT " + text,
    };

    private static async Task Heartbeat(ClientWebSocket socket, int interval, Func<int?> seq, CancellationToken stop)
    {
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            await Task.Delay(interval, stop);
            await Send(socket, new { op = 1, d = seq() }, stop);
        }
    }

    private static readonly SemaphoreSlim SendLock = new(1, 1);

    private static async Task Send(ClientWebSocket socket, object payload, CancellationToken stop)
    {
        await SendLock.WaitAsync(stop);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text, true, stop); }
        finally { SendLock.Release(); }
    }

    private void Handle(string type, JsonElement d)
    {
        lock (_names)
        {
            switch (type)
            {
                case "READY":
                    _note = d.GetProperty("guilds").GetArrayLength() == 0 ? "BOT IST AUF KEINEM SERVER" : "";
                    break;
                case "GUILD_CREATE":
                    if (_guild.Length > 0 && _guild != d.GetProperty("name").GetString()) return; // the first server is shown
                    _guild = d.GetProperty("name").GetString();
                    foreach (var c in d.GetProperty("channels").EnumerateArray())
                        if (c.GetProperty("type").GetInt32() is 2 or 13) _channels[c.GetProperty("id").GetString()] = c.GetProperty("name").GetString();
                    if (d.TryGetProperty("members", out var members)) foreach (var m in members.EnumerateArray()) Member(m);
                    if (d.TryGetProperty("presences", out var presences)) foreach (var p in presences.EnumerateArray()) Presence(p);
                    if (d.TryGetProperty("voice_states", out var voice)) foreach (var v in voice.EnumerateArray()) Voice(v);
                    _note = "";
                    break;
                case "PRESENCE_UPDATE": Presence(d); break;
                case "VOICE_STATE_UPDATE": Voice(d); break;
                case "GUILD_MEMBER_ADD": case "GUILD_MEMBER_UPDATE": Member(d); break;
            }
        }
    }

    private void Member(JsonElement m)
    {
        var user = m.GetProperty("user");
        if (user.TryGetProperty("bot", out var bot) && bot.GetBoolean()) return;
        string id = user.GetProperty("id").GetString();
        string name = m.TryGetProperty("nick", out var nick) && nick.ValueKind == JsonValueKind.String ? nick.GetString()
            : user.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : user.GetProperty("username").GetString();
        _names[id] = name;
        if (user.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.String) _avatars[id] = $"https://cdn.discordapp.com/avatars/{id}/{a.GetString()}.png?size=64";
    }

    private void Presence(JsonElement p)
    {
        string id = p.GetProperty("user").GetProperty("id").GetString();
        _status[id] = p.GetProperty("status").GetString();
        _activity.Remove(id);
        if (p.TryGetProperty("activities", out var acts))
            foreach (var a in acts.EnumerateArray())
            {
                int kind = a.GetProperty("type").GetInt32();
                string name = a.GetProperty("name").GetString();
                string text = kind switch { 0 => "spielt " + name, 1 => "streamt " + name, 2 => "hört " + (a.TryGetProperty("details", out var det) ? det.GetString() : name), 3 => "schaut " + name, 5 => "tritt an in " + name, _ => null };
                if (text != null) { _activity[id] = text; break; }
            }
    }

    private void Voice(JsonElement v)
    {
        string id = v.GetProperty("user_id").GetString();
        if (v.TryGetProperty("member", out var m)) Member(m);
        var channel = v.GetProperty("channel_id");
        if (channel.ValueKind != JsonValueKind.String) { _voice.Remove(id); return; }
        bool Flag(string name) => v.TryGetProperty(name, out var f) && f.GetBoolean();
        _voice[id] = (channel.GetString(), Flag("self_mute") || Flag("mute"), Flag("self_deaf") || Flag("deaf"), Flag("self_video"));
    }

    private string _drawn;

    private void Draw()
    {
        string snapshot;
        List<(string Channel, List<(string Id, bool Mute, bool Deaf, bool Video)> People)> calls;
        List<(string Id, string Status, string Activity)> online;
        lock (_names)
        {
            calls = _voice.GroupBy(v => v.Value.Channel).Select(g => (_channels.GetValueOrDefault(g.Key, "Sprachkanal"), g.Select(x => (x.Key, x.Value.Mute, x.Value.Deaf, x.Value.Video)).ToList())).ToList();
            online = _status.Where(s => s.Value != "offline" && _names.ContainsKey(s.Key)).Select(s => (s.Key, s.Value, _activity.GetValueOrDefault(s.Key))).ToList();
            snapshot = _note + _guild + string.Join(",", calls.SelectMany(c => c.People.Select(p => c.Channel + p.Id + p.Mute + p.Deaf))) + string.Join(",", online.Select(o => o.Id + o.Status + o.Activity));
        }
        if (snapshot == _drawn) return; // nothing changed since the last picture
        _drawn = snapshot;
        _list.Children.Clear();
        Caption.Text = _guild.Length > 0 ? "+ DISCORD / " + _guild.ToUpperInvariant() : "+ DISCORD";
        if (_note.Length > 0) { _count.Text = ""; _list.Children.Add(FriendRow.Note(_note)); if (_guild.Length == 0) return; }
        _count.Text = $"{online.Count} ONLINE";
        foreach (var (channel, people) in calls)
        {
            _list.Children.Add(FriendRow.Heading("Sprache / " + channel));
            foreach (var p in people)
                _list.Children.Add(FriendRow.Make(FriendRow.Playing, _avatars.GetValueOrDefault(p.Id), _names.GetValueOrDefault(p.Id, "?"), p.Video ? "kamera an" : _activity.GetValueOrDefault(p.Id) ?? "im Gespräch",
                    p.Deaf ? "" : p.Mute ? "" : null));
        }
        var rest = online.Where(o => calls.All(c => c.People.All(p => p.Id != o.Id))).OrderBy(o => o.Activity == null).ThenBy(o => _names[o.Id], StringComparer.CurrentCultureIgnoreCase).ToList();
        if (rest.Count > 0) _list.Children.Add(FriendRow.Heading("Online"));
        foreach (var o in rest.Take(8))
        {
            var color = o.Activity != null ? FriendRow.Playing : o.Status switch { "idle" => FriendRow.Away, "dnd" => FriendRow.Busy, _ => FriendRow.Online };
            _list.Children.Add(FriendRow.Make(color, _avatars.GetValueOrDefault(o.Id), _names[o.Id], o.Activity ?? (o.Status == "idle" ? "abwesend" : o.Status == "dnd" ? "bitte nicht stören" : "online")));
        }
        if (rest.Count > 8) _list.Children.Add(FriendRow.Note($"+ {rest.Count - 8} WEITERE"));
        if (calls.Count == 0 && rest.Count == 0) _list.Children.Add(FriendRow.Note("GERADE IST NIEMAND ONLINE."));
    }

    protected override Point DefaultPlace(Rect w) => new(w.Left + 388, w.Top + 486);
}
