using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Reflection;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GVPN.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GVPN;

public partial class MainWindow : Window
{
    private static readonly HttpClient UpdateHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string LatestReleaseApi = "https://api.github.com/repos/JuniorSantana93/GVPN/releases/latest";
    private readonly ProtonVpnService _proton = new();
    private readonly SavedProtonCredentials _savedCredentials = new();
    private readonly NetworkTunnelManager _tunnel = new();
    private readonly DispatcherTimer _connectionTimer;
    private readonly string _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVPN", "app-settings.json");
    private readonly string _splitAppsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVPN", "split-apps.json");
    private readonly List<string> _tunneledApps = new();
    private readonly Dictionary<string, string> _appIconCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ProtonServer> _servers = new();
    private AppSettings _settings = new();
    private string _username = "";
    private string _selectedServer = "";
    private string _sort = "Ordem alfabética";
    private string _region = "";
    private string _serverSummary = "";
    private string _loginError = "";
    private string _connectionStatus = "VPN DESCONECTADA";
    private bool _busy;
    private bool _waitingForTwoFactor;
    private bool _waitingForCaptcha;
    private bool _saveLogin;
    private bool _isFree = true;
    private bool _updateAvailable;
    private string _updateStatus = "Atualização automática ativada. Será verificada ao iniciar.";
    private string _latestVersion = "";
    private string _latestReleaseUrl = "https://github.com/JuniorSantana93/GVPN/releases/latest";
    private bool _allowExit;
    private ProtonServer? _connectedServer;
    private DateTime _connectedAt;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        LoadAppSettings();
        LoadSplitApps();
        if (_settings.CloseToTray) InitializeTrayIcon();
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectionTimer.Tick += (_, _) => _ = AppWebView?.ExecuteScriptAsync($"window.gvpn?.updateDuration({JsonSerializer.Serialize(ConnectionDuration)})");
        _tunnel.ConnectionLost += (_, message) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            _connectionTimer.Stop();
            _connectedServer = null;
            _connectionStatus = "FALHA NA CONEXÃO";
            _serverSummary = message;
            await PushWebStateAsync();
        }));
    }

    private string ConnectionDuration
    {
        get
        {
            if (!_tunnel.IsConnected) return "00:00:00";
            var elapsed = DateTime.Now - _connectedAt;
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    private void ApplyDarkTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        uint enabled = 1;
        SetDwmAttribute(handle, 20, ref enabled); // DWMWA_USE_IMMERSIVE_DARK_MODE

        // Windows 11 supports explicit non-client colors; older versions keep the dark-mode fallback above.
        uint captionColor = ToColorRef(0x0B, 0x11, 0x19);
        uint borderColor = ToColorRef(0x0B, 0x11, 0x19);
        uint textColor = ToColorRef(0xF2, 0xF6, 0xFB);
        SetDwmAttribute(handle, 35, ref captionColor); // DWMWA_CAPTION_COLOR
        SetDwmAttribute(handle, 34, ref borderColor);  // DWMWA_BORDER_COLOR
        SetDwmAttribute(handle, 36, ref textColor);    // DWMWA_TEXT_COLOR
    }

    private static uint ToColorRef(byte red, byte green, byte blue) => (uint)(red | (green << 8) | (blue << 16));

    private static void SetDwmAttribute(IntPtr handle, int attribute, ref uint value)
    {
        try { _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, uint valueSize);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebViewAsync();
            if (_settings.CheckUpdatesOnStartup) _ = CheckForUpdatesAsync(silent: true);
            var saved = _savedCredentials.Load();
            _saveLogin = saved.HasValue;
            string? sessionUser = await _proton.GetStoredUsernameAsync();
            if (!string.IsNullOrWhiteSpace(sessionUser))
            {
                _username = sessionUser;
                await LoadAccountAndServersAsync(sessionUser);
            }
            await PushWebStateAsync();
            ApplyStartMinimized();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível iniciar a interface WebView2. Verifique se o Microsoft Edge WebView2 Runtime está instalado.\n\n{ex.Message}", "GVPN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        string webViewDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVPN", "WebView2");
        Directory.CreateDirectory(webViewDataFolder);
        var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
        await AppWebView.EnsureCoreWebView2Async(webViewEnvironment);
        AppWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        AppWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        AppWebView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;
        string html = await EmbeddedResources.ReadTextAsync("GVPN.Embedded.Web.index.html");
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object? _, CoreWebView2NavigationCompletedEventArgs args)
        {
            AppWebView.NavigationCompleted -= OnLoaded;
            if (args.IsSuccess) loaded.TrySetResult();
            else loaded.TrySetException(new InvalidOperationException($"O WebView2 não carregou a interface (código {args.WebErrorStatus})."));
        }
        AppWebView.NavigationCompleted += OnLoaded;
        AppWebView.NavigateToString(html);
        await loaded.Task;
    }

    private async void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            string action = ReadString(root, "action");
            if (_busy && (action is "connect" or "disconnect" or "refresh" or "login")) return;
            switch (action)
            {
                case "login": await LoginAsync(root); break;
                case "select-server": await SelectServerAsync(ReadString(root, "name")); break;
                case "sort": _sort = ReadString(root, "value"); await PushWebStateAsync(); break;
                case "region": _region = ReadString(root, "value"); await PushWebStateAsync(); break;
                case "connect": case "disconnect": await ToggleConnectionAsync(); break;
                case "refresh": await RefreshServersAsync(); break;
                case "add-app": AddSplitApp(); await PushWebStateAsync(); break;
                case "remove-app": RemoveSplitApp(root); await PushWebStateAsync(); break;
                case "logout": await LogoutAsync(); break;
                case "save-settings": SaveSettings(root); await PushWebStateAsync(); break;
                case "check-updates": await CheckForUpdatesAsync(silent: false); break;
                case "open-update": OpenLatestRelease(); break;
                case "account": ShowAccount(); break;
                case "support": OpenSupport(); break;
                case "minimize": WindowState = WindowState.Minimized; break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            _loginError = ex.Message;
            await PushWebStateAsync();
        }
    }

    private async Task LoginAsync(JsonElement root)
    {
        string username = ReadString(root, "username").Trim();
        string password = ReadString(root, "password");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            _loginError = "Informe seu usuário e senha Proton.";
            await PushWebStateAsync();
            return;
        }
        _busy = true; _loginError = "";
        _saveLogin = ReadBool(root, "saveLogin");
        if (!_saveLogin) _savedCredentials.Delete();
        await PushWebStateAsync();
        try
        {
            var result = await _proton.LoginAsync(username, password,
                _waitingForTwoFactor ? ReadString(root, "twoFactor").Trim() : null,
                _waitingForCaptcha ? ReadString(root, "captchaToken").Trim() : null);
            if (!result.Success)
            {
                if (result.Code == "TWO_FACTOR_REQUIRED") _waitingForTwoFactor = true;
                if (result.CaptchaUrl != null || result.Code?.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _waitingForCaptcha = true;
                    OpenProtonCaptcha(result.CaptchaUrl);
                }
                _loginError = FriendlyLoginError(result.Error ?? "Não foi possível autenticar a conta Proton.");
                return;
            }

            if (_saveLogin) _savedCredentials.Save(username, password);
            else _savedCredentials.Delete();
            _waitingForTwoFactor = false; _waitingForCaptcha = false;
            _username = username;
            await LoadAccountAndServersAsync(username);
            ApplyStartMinimized();
        }
        catch (Exception ex) { _loginError = FriendlyLoginError(ex.Message); }
        finally { _busy = false; await PushWebStateAsync(); }
    }

    private async Task LoadAccountAndServersAsync(string username)
    {
        _busy = true;
        _serverSummary = "Carregando servidores da assinatura…";
        await PushWebStateAsync();
        try
        {
            var result = await _proton.LoadServersAsync(username);
            if (!result.Success) { _serverSummary = result.Message; _loginError = result.Message; return; }
            _username = username;
            _servers = result.Servers;
            _isFree = result.IsFree;
            if (!_servers.Any(s => s.Name == _selectedServer)) _selectedServer = _servers.FirstOrDefault()?.Name ?? "";
            _serverSummary = result.Message;
            _loginError = "";
            _connectedServer = null;
            _connectionStatus = _tunnel.IsConnected ? "VPN CONECTADA" : "VPN DESCONECTADA";
        }
        finally { _busy = false; await PushWebStateAsync(); }
    }

    private async Task RefreshServersAsync()
    {
        if (string.IsNullOrWhiteSpace(_username)) return;
        _busy = true; _serverSummary = "Atualizando servidores e latência…";
        await PushWebStateAsync();
        try
        {
            var result = await _proton.LoadServersAsync(_username);
            if (!result.Success) { _serverSummary = result.Message; return; }
            _servers = result.Servers; _isFree = result.IsFree; _serverSummary = result.Message;
            if (!_servers.Any(s => s.Name == _selectedServer)) _selectedServer = _servers.FirstOrDefault()?.Name ?? "";
        }
        catch (Exception ex) { _serverSummary = ex.Message; }
        finally { _busy = false; await PushWebStateAsync(); }
    }

    private async Task SelectServerAsync(string serverName)
    {
        if (_busy) return;
        var server = _servers.FirstOrDefault(s => s.Name == serverName);
        if (server is null) return;

        _selectedServer = server.Name;
        if (_tunnel.IsConnected && _connectedServer?.Name == server.Name)
        {
            await PushWebStateAsync();
            return;
        }

        if (_tunnel.IsConnected)
        {
            _busy = true;
            _connectionStatus = "TROCANDO SERVIDOR…";
            await PushWebStateAsync();
            try
            {
                await _tunnel.DisconnectAsync(_proton.ProfileFile);
                _connectedServer = null;
                _connectionTimer.Stop();
                _connectionStatus = "VPN DESCONECTADA";
            }
            catch (Exception ex)
            {
                _connectionStatus = $"Falha ao trocar servidor: {ex.Message}";
                _serverSummary = "A conexão atual foi mantida; tente novamente.";
                _busy = false;
                await PushWebStateAsync();
                return;
            }
            finally
            {
                if (_busy)
                {
                    _busy = false;
                    await PushWebStateAsync();
                }
            }
        }

        await ToggleConnectionAsync();
    }

    private async Task ToggleConnectionAsync()
    {
        if (_tunnel.IsConnected)
        {
            _busy = true; await PushWebStateAsync();
            try { await _tunnel.DisconnectAsync(_proton.ProfileFile); _connectedServer = null; _connectionStatus = "VPN DESCONECTADA"; _connectionTimer.Stop(); }
            catch (Exception ex) { _connectionStatus = $"Falha ao desconectar: {ex.Message}"; }
            finally { _busy = false; await PushWebStateAsync(); }
            return;
        }
        var server = _servers.FirstOrDefault(s => s.Name == _selectedServer);
        if (server is null) { _serverSummary = "Selecione um servidor da lista."; await PushWebStateAsync(); return; }
        _busy = true; _connectionStatus = "PREPARANDO CONEXÃO…"; _connectedServer = server; _serverSummary = "Solicitando o perfil WireGuard à Proton…";
        await PushWebStateAsync();
        try
        {
            RefreshDiscordSplitAppPaths();
            var generated = await _proton.GenerateProfileAsync(_username, server);
            if (!generated.Success) { _connectionStatus = "FALHA AO GERAR PERFIL"; _serverSummary = generated.Message; _connectedServer = null; return; }

            if (!_tunnel.HasWireSock)
            {
                var installChoice = MessageBox.Show(this,
                    "O WireSock SDK é necessário para o Split Tunneling e permite que o GVPN use o WireSock para a conexão completa.\n\n" +
                    "O GVPN vai abrir o WinGet oficial para baixar e instalar NTKERNEL.WireSockVPNClientCLI. Você poderá revisar e aceitar os termos do WinGet e do fornecedor na janela que será aberta.\n\n" +
                    "O WireSock é gratuito para uso pessoal, educacional e sem fins lucrativos; uso comercial exige licença Pro.\n\n" +
                    "Deseja instalar agora?",
                    "Instalar dependência WireSock", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (installChoice == MessageBoxResult.Yes)
                {
                    _connectionStatus = "INSTALANDO WIRESOCK…";
                    _serverSummary = "O WinGet está instalando o WireSock SDK. A janela do instalador pode solicitar sua confirmação.";
                    await PushWebStateAsync();
                    var installation = await _tunnel.InstallWireSockSdkAsync();
                    if (!installation.Success && _tunneledApps.Count > 0)
                    {
                        _connectionStatus = "FALHA NA CONEXÃO";
                        _serverSummary = installation.Message;
                        _connectedServer = null;
                        return;
                    }
                }
                else if (_tunneledApps.Count > 0)
                {
                    _connectionStatus = "FALHA NA CONEXÃO";
                    _serverSummary = "Split Tunneling precisa do WireSock SDK. Instale-o para usar a VPN com programas selecionados.";
                    _connectedServer = null;
                    return;
                }
            }

            _connectionStatus = "INICIANDO TÚNEL VPN…"; await PushWebStateAsync();
            var connected = await _tunnel.ConnectAsync(_proton.ProfileFile, _tunneledApps);
            if (!connected.Success) { _connectionStatus = "FALHA NA CONEXÃO"; _serverSummary = connected.StatusMessage; _connectedServer = null; return; }
            _connectionStatus = "VPN CONECTADA"; _connectedAt = DateTime.Now; _connectionTimer.Start();
            _serverSummary = connected.StatusMessage;
        }
        catch (Exception ex) { _connectionStatus = "FALHA NA CONEXÃO"; _serverSummary = ex.Message; _connectedServer = null; }
        finally { _busy = false; await PushWebStateAsync(); }
    }
    private async Task LogoutAsync()
    {
        _busy = true; await PushWebStateAsync();
        try
        {
            if (_tunnel.IsConnected) await _tunnel.DisconnectAsync(_proton.ProfileFile);
            _proton.ClearSession();
            _username = ""; _servers.Clear(); _selectedServer = ""; _connectedServer = null;
            _connectionStatus = "VPN DESCONECTADA"; _connectionTimer.Stop();
            _waitingForTwoFactor = false; _waitingForCaptcha = false; _loginError = ""; _serverSummary = "";
        }
        catch (Exception ex) { _loginError = $"Não foi possível encerrar a sessão com segurança: {ex.Message}"; }
        finally { _busy = false; await PushWebStateAsync(); }
    }

    private void AddSplitApp()
    {
        var picker = new OpenFileDialog { Title = "Selecione o programa que usará a VPN", Filter = "Programas (*.exe)|*.exe|Todos os arquivos (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true || _tunneledApps.Contains(picker.FileName, StringComparer.OrdinalIgnoreCase)) return;
        _tunneledApps.Add(picker.FileName); SaveSplitApps();
    }

    private void RemoveSplitApp(JsonElement root)
    {
        if (root.TryGetProperty("index", out var idx) && idx.TryGetInt32(out int index) && index >= 0 && index < _tunneledApps.Count)
        { _tunneledApps.RemoveAt(index); SaveSplitApps(); }
    }

    private void LoadSplitApps()
    {
        try
        {
            if (File.Exists(_splitAppsFile))
            {
                var configured = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_splitAppsFile)) ?? new();
                _tunneledApps.AddRange(configured.Select(ResolveDiscordExecutable).Distinct(StringComparer.OrdinalIgnoreCase));
                if (!_tunneledApps.SequenceEqual(configured, StringComparer.OrdinalIgnoreCase)) SaveSplitApps();
            }
        }
        catch { _tunneledApps.Clear(); }
    }

    private void RefreshDiscordSplitAppPaths()
    {
        bool changed = false;
        for (int i = 0; i < _tunneledApps.Count; i++)
        {
            string resolved = ResolveDiscordExecutable(_tunneledApps[i]);
            if (string.Equals(resolved, _tunneledApps[i], StringComparison.OrdinalIgnoreCase)) continue;
            _tunneledApps[i] = resolved;
            changed = true;
        }
        if (changed) SaveSplitApps();
    }

    private static string ResolveDiscordExecutable(string configuredPath)
    {
        string? fileName = Path.GetFileName(configuredPath);
        string? installFolder = fileName?.ToLowerInvariant() switch
        {
            "discord.exe" => "Discord",
            "discordptb.exe" => "DiscordPTB",
            "discordcanary.exe" => "DiscordCanary",
            _ => null
        };
        if (installFolder is null) return configuredPath;

        string installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), installFolder);
        if (!Directory.Exists(installRoot)) return configuredPath;
        try
        {
            var latest = Directory.EnumerateDirectories(installRoot, "app-*", SearchOption.TopDirectoryOnly)
                .Select(directory => new { Directory = directory, Executable = Path.Combine(directory, fileName!) })
                .Where(candidate => File.Exists(candidate.Executable))
                .Select(candidate => new
                {
                    candidate.Executable,
                    Version = Version.TryParse(Path.GetFileName(candidate.Directory).Substring("app-".Length), out var version) ? version : null,
                    Modified = Directory.GetLastWriteTimeUtc(candidate.Directory)
                })
                .OrderByDescending(candidate => candidate.Version is not null)
                .ThenByDescending(candidate => candidate.Version)
                .ThenByDescending(candidate => candidate.Modified)
                .FirstOrDefault();
            return latest?.Executable ?? configuredPath;
        }
        catch (IOException) { return configuredPath; }
        catch (UnauthorizedAccessException) { return configuredPath; }
    }

    private void SaveSplitApps()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_splitAppsFile)!);
        File.WriteAllText(_splitAppsFile, JsonSerializer.Serialize(_tunneledApps));
    }

    private string GetAppIcon(string executablePath)
    {
        if (_appIconCache.TryGetValue(executablePath, out string? cached)) return cached;
        string iconData = "";
        try
        {
            var info = new ShellFileInfo();
            if (SHGetFileInfo(executablePath, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x1) != IntPtr.Zero && info.Icon != IntPtr.Zero)
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(info.Icon);
                    using var bitmap = icon.ToBitmap();
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    iconData = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
                }
                finally { DestroyIcon(info.Icon); }
            }
        }
        catch { }
        _appIconCache[executablePath] = iconData;
        return iconData;
    }

    private static string GetFlagDataUri(string country)
    {
        string svg = country.ToUpperInvariant() switch
        {
            "US" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#fff' d='M0 0h60v40H0z'/><path fill='#b22234' d='M0 0h60v3H0zm0 6h60v3H0zm0 6h60v3H0zm0 6h60v3H0zm0 6h60v3H0zm0 6h60v3H0zm0 6h60v2H0z'/><path fill='#3c3b6e' d='M0 0h27v21H0z'/><g fill='#fff'><circle cx='4' cy='4' r='1'/><circle cx='10' cy='4' r='1'/><circle cx='16' cy='4' r='1'/><circle cx='22' cy='4' r='1'/><circle cx='7' cy='8' r='1'/><circle cx='13' cy='8' r='1'/><circle cx='19' cy='8' r='1'/><circle cx='4' cy='12' r='1'/><circle cx='10' cy='12' r='1'/><circle cx='16' cy='12' r='1'/><circle cx='22' cy='12' r='1'/><circle cx='7' cy='16' r='1'/><circle cx='13' cy='16' r='1'/><circle cx='19' cy='16' r='1'/></g></svg>",
            "CA" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#d80621' d='M0 0h60v40H0z'/><path fill='#fff' d='M14 0h32v40H14z'/><path fill='#d80621' d='m30 7 3 6 4-2-2 6 4 3-6 1 1 10-4-3-4 3 1-10-6-1 4-3-2-6 4 2z'/></svg>",
            "NL" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#ae1c28' d='M0 0h60v13.34H0z'/><path fill='#fff' d='M0 13.33h60v13.34H0z'/><path fill='#21468b' d='M0 26.66h60V40H0z'/></svg>",
            "DE" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path d='M0 0h60v13.34H0z'/><path fill='#d00' d='M0 13.33h60v13.34H0z'/><path fill='#ffce00' d='M0 26.66h60V40H0z'/></svg>",
            "FR" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#0055a4' d='M0 0h20v40H0z'/><path fill='#fff' d='M20 0h20v40H20z'/><path fill='#ef4135' d='M40 0h20v40H40z'/></svg>",
            "GB" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#012169' d='M0 0h60v40H0z'/><path stroke='#fff' stroke-width='9' d='m0 0 60 40M60 0 0 40'/><path stroke='#c8102e' stroke-width='4' d='m0 0 60 40M60 0 0 40'/><path stroke='#fff' stroke-width='14' d='M30 0v40M0 20h60'/><path stroke='#c8102e' stroke-width='7' d='M30 0v40M0 20h60'/></svg>",
            "JP" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#fff' d='M0 0h60v40H0z'/><circle cx='30' cy='20' r='10' fill='#bc002d'/></svg>",
            "CH" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40'><path fill='#d52b1e' d='M0 0h40v40H0z'/><path fill='#fff' d='M17 7h6v10h10v6H23v10h-6V23H7v-6h10z'/></svg>",
            "BR" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#009739' d='M0 0h60v40H0z'/><path fill='#ffdf00' d='m30 4 24 16-24 16L6 20z'/><circle cx='30' cy='20' r='9' fill='#002776'/></svg>",
            "ES" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#aa151b' d='M0 0h60v40H0z'/><path fill='#f1bf00' d='M0 10h60v20H0z'/></svg>",
            "IT" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#009246' d='M0 0h20v40H0z'/><path fill='#fff' d='M20 0h20v40H20z'/><path fill='#ce2b37' d='M40 0h20v40H40z'/></svg>",
            "SE" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#006aa7' d='M0 0h60v40H0z'/><path fill='#fecc00' d='M18 0h6v40h-6zM0 17h60v6H0z'/></svg>",
            "NO" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#ba0c2f' d='M0 0h60v40H0z'/><path fill='#fff' d='M16 0h12v40H16zM0 14h60v12H0z'/><path fill='#00205b' d='M19 0h6v40h-6zM0 17h60v6H0z'/></svg>",
            "FI" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#fff' d='M0 0h60v40H0z'/><path fill='#003580' d='M17 0h8v40h-8zM0 16h60v8H0z'/></svg>",
            "DK" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#c8102e' d='M0 0h60v40H0z'/><path fill='#fff' d='M17 0h6v40h-6zM0 17h60v6H0z'/></svg>",
            "PL" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#fff' d='M0 0h60v20H0z'/><path fill='#dc143c' d='M0 20h60v20H0z'/></svg>",
            "RO" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#002b7f' d='M0 0h20v40H0z'/><path fill='#fcd116' d='M20 0h20v40H20z'/><path fill='#ce1126' d='M40 0h20v40H40z'/></svg>",
            "SG" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#ed2939' d='M0 0h60v20H0z'/><path fill='#fff' d='M0 20h60v20H0z'/><path fill='#fff' d='M19 7a8 8 0 1 0 0 14 7 7 0 1 1 0-14'/><g fill='#fff'><circle cx='24' cy='7' r='1.3'/><circle cx='29' cy='11' r='1.3'/><circle cx='27' cy='17' r='1.3'/><circle cx='21' cy='17' r='1.3'/><circle cx='19' cy='11' r='1.3'/></g></svg>",
            "MX" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#006847' d='M0 0h20v40H0z'/><path fill='#fff' d='M20 0h20v40H20z'/><path fill='#ce1126' d='M40 0h20v40H40z'/><circle cx='30' cy='20' r='5.5' fill='#a87935'/><path d='M27 23c1-3 1-7 3-9l2 3 2-1-1 4 2 2-3 1-1 3-2-2z' fill='#79552d'/><path d='M24.5 23c1 4 3 6 5.5 6m5.5-6c-1 4-3 6-5.5 6' fill='none' stroke='#287447' stroke-width='1.4'/></svg>",
            "AU" => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><path fill='#012169' d='M0 0h60v40H0z'/><path stroke='#fff' stroke-width='7' d='m0 0 30 20M30 0 0 20'/><path stroke='#c8102e' stroke-width='3' d='m0 0 30 20M30 0 0 20'/><path stroke='#fff' stroke-width='9' d='M15 0v20M0 10h30'/><path stroke='#c8102e' stroke-width='4' d='M15 0v20M0 10h30'/><circle cx='44' cy='12' r='2' fill='#fff'/><circle cx='50' cy='24' r='2' fill='#fff'/><circle cx='39' cy='30' r='2' fill='#fff'/></svg>",
            _ => $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 40'><rect width='60' height='40' rx='4' fill='#26384b'/><path fill='#00d395' d='M0 0h60v7H0z'/><text x='30' y='27' text-anchor='middle' font-family='Arial' font-size='18' font-weight='700' fill='white'>{(country.Length == 2 ? country.ToUpperInvariant() : "??")}</text></svg>"
        };
        return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint fileInfoSize, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private void SaveSettings(JsonElement root)
    {
        _settings = new AppSettings { StartWithWindows = ReadBool(root, "startWithWindows"), StartMinimized = ReadBool(root, "startMinimized"), CloseToTray = ReadBool(root, "closeToTray"), CheckUpdatesOnStartup = ReadBool(root, "checkUpdatesOnStartup") };
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        File.WriteAllText(_settingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        SetStartWithWindows(_settings.StartWithWindows);
        if (_settings.CloseToTray) InitializeTrayIcon(); else { _trayIcon?.Dispose(); _trayIcon = null; }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        _updateStatus = "Verificando atualizações no GitHub…";
        if (!silent) await PushWebStateAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("GVPN-Updater/1.1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await UpdateHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _updateAvailable = false;
                _updateStatus = $"GitHub respondeu HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Tente novamente mais tarde.";
                Debug.WriteLine($"Falha ao verificar atualizações: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                await PushWebStateAsync();
                return;
            }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tag = json.RootElement.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("A resposta da API não contém a tag da release.");
            _latestReleaseUrl = json.RootElement.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() ?? "https://github.com/JuniorSantana93/GVPN/releases/latest" : "https://github.com/JuniorSantana93/GVPN/releases/latest";
            _latestVersion = tag.TrimStart('v', 'V');
            Version? current = Assembly.GetExecutingAssembly().GetName().Version;
            bool hasCurrent = Version.TryParse(current?.ToString(3), out var currentVersion);
            bool hasLatest = Version.TryParse(_latestVersion, out var latestVersion);
            if (!hasCurrent || !hasLatest) throw new InvalidDataException($"Não foi possível interpretar as versões (instalada: {current}; GitHub: {tag}).");
            _updateAvailable = hasCurrent && hasLatest && latestVersion! > currentVersion!;
            _updateStatus = _updateAvailable
                ? $"Nova versão {_latestVersion} disponível (instalada: {currentVersion})."
                : $"Você está usando a versão mais recente ({current?.ToString(3) ?? "desconhecida"}).";
        }
        catch (Exception ex)
        {
            _updateAvailable = false;
            _updateStatus = ex switch
            {
                TaskCanceledException => "A consulta ao GitHub expirou após 20 segundos. Confira sua conexão e tente novamente.",
                HttpRequestException { StatusCode: not null } httpError => $"Falha de comunicação com o GitHub (HTTP {(int)httpError.StatusCode}: {httpError.Message}).",
                HttpRequestException => $"Não foi possível alcançar a API do GitHub: {ex.Message}",
                JsonException => "O GitHub respondeu, mas os dados da release estavam inválidos.",
                _ => $"Não foi possível verificar atualizações: {ex.Message}"
            };
            Debug.WriteLine($"Falha ao verificar atualizações: {ex.Message}");
        }
        await PushWebStateAsync();
    }

    private void OpenLatestRelease()
    {
        if (!Uri.TryCreate(_latestReleaseUrl, UriKind.Absolute, out var uri) || uri.Host != "github.com") return;
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir a página da atualização: {ex.Message}", "GVPN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void LoadAppSettings()
    {
        try { if (File.Exists(_settingsFile)) _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new(); }
        catch { _settings = new(); }
        if (_settings.StartWithWindows) SetStartWithWindows(true);
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            const string valueName = "GVPN";
            if (enabled)
            {
                string minimized = _settings.StartMinimized ? " --minimized" : "";
                key.SetValue(valueName, $"\"{Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName}\"{minimized}");
            }
            else key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { MessageBox.Show($"Não foi possível atualizar a inicialização do Windows: {ex.Message}", "GVPN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyStartMinimized()
    {
        if (!_settings.StartMinimized && !Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase)) return;
        if (_settings.CloseToTray) HideToTray(); else WindowState = WindowState.Minimized;
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon != null) return;
        var appIcon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        _trayIcon = new System.Windows.Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "GVPN", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir GVPN", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void HideToTray() { if (_trayIcon == null) InitializeTrayIcon(); Hide(); }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ExitFromTray() { _allowExit = true; _trayIcon?.Dispose(); _trayIcon = null; Close(); }
    private void MainWindow_StateChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Minimized && _settings.CloseToTray) HideToTray(); }
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) { if (_settings.CloseToTray && !_allowExit) { e.Cancel = true; HideToTray(); } else { _trayIcon?.Dispose(); _trayIcon = null; } }

    private void ShowAccount() => MessageBox.Show($"Conta Proton: {_username}\n{(_isFree ? "Plano Free" : "Plano Premium")}\n{_serverSummary}", "Minha Conta", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OpenSupport() { try { Process.Start(new ProcessStartInfo("https://protonvpn.com/support/") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "GVPN", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private static void OpenProtonCaptcha(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("proton.me", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".proton.me", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("protonvpn.ch", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".protonvpn.ch", StringComparison.OrdinalIgnoreCase))) return;
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); } catch { }
    }

    private static string FriendlyLoginError(string value) => value.Contains("temporary Proton session operation failure", StringComparison.OrdinalIgnoreCase)
        ? "A Proton não respondeu neste momento. Isso não significa que sua senha esteja errada. Verifique a conexão e tente novamente em instantes."
        : value;
    private static string ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool ReadBool(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task PushWebStateAsync()
    {
        if (AppWebView?.CoreWebView2 == null) return;
        var saved = _savedCredentials.Load();
        var servers = _servers.Select(s =>
        {
            string countryName; try { countryName = new System.Globalization.RegionInfo(s.Country).DisplayName; } catch { countryName = s.Country; }
            string flag = GetFlagDataUri(s.Country);
            return new { name = s.Name, country = s.Country, countryName, city = s.City, tier = s.Tier, load = s.Load, pingMs = s.PingMs, pingDisplay = s.PingDisplay, flag };
        }).ToArray();
        var active = _connectedServer is null ? new { name = "", city = "", pingDisplay = "—" } : new { name = _connectedServer.Name, city = _connectedServer.City, pingDisplay = _connectedServer.PingDisplay };
        var state = new
        {
            authenticated = !string.IsNullOrWhiteSpace(_username), username = _username,
            saveLogin = _saveLogin || saved.HasValue, savedPassword = saved?.Password ?? "",
            error = _loginError, busy = _busy, twoFactor = _waitingForTwoFactor, captcha = _waitingForCaptcha,
            plan = _isFree ? "Plano Free" : "Plano Premium", connected = _tunnel.IsConnected,
            status = _connectionStatus, duration = ConnectionDuration, activeServer = active,
            servers, selected = _selectedServer, sort = _sort, region = _region,
            apps = _tunneledApps.Select(path => new { path, name = Path.GetFileName(path), icon = GetAppIcon(path) }).ToArray(),
            settings = new { startWithWindows = _settings.StartWithWindows, startMinimized = _settings.StartMinimized, closeToTray = _settings.CloseToTray, checkUpdatesOnStartup = _settings.CheckUpdatesOnStartup },
            currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—",
            checkUpdatesOnStartup = _settings.CheckUpdatesOnStartup, updateAvailable = _updateAvailable,
            updateStatus = _updateStatus, latestVersion = _latestVersion,
            statusText = _serverSummary
        };
        AppWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state }));
        await Task.CompletedTask;
    }

    private sealed class AppSettings
    {
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool CloseToTray { get; set; }
        public bool CheckUpdatesOnStartup { get; set; } = true;
    }
}
