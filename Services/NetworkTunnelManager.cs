using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GVPN.Services
{
    public sealed class NetworkTunnelManager
    {
        private const string TunnelName = "GVPN_Tunnel";
        private string? _wireGuardExe;
        private string? _wireSockExe;
        private Process? _wireSockProcess;
        private int _intentionalWireSockExit;
        public bool IsConnected { get; private set; }
        public bool HasWireSock => FindWireSockExecutable() is not null;
        public event EventHandler<string>? ConnectionLost;

        public async Task<(bool Success, string Message)> InstallWireSockSdkAsync()
        {
            _wireSockExe = FindWireSockExecutable();
            if (_wireSockExe is not null) return (true, "WireSock SDK já está instalado.");

            var start = new ProcessStartInfo
            {
                FileName = "winget.exe",
                UseShellExecute = false,
                CreateNoWindow = false
            };
            foreach (string argument in new[]
            {
                "install", "--id", "NTKERNEL.WireSockVPNClientCLI", "--exact"
            })
                start.ArgumentList.Add(argument);

            try
            {
                using var process = new Process { StartInfo = start };
                process.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (false, "A instalação do WireSock pelo WinGet excedeu 20 minutos.");
                }

                if (process.ExitCode != 0)
                    return (false, $"O WinGet encerrou com código {process.ExitCode}. Confira a janela do instalador e os termos do WireSock.");

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    _wireSockExe = FindWireSockExecutable();
                    if (_wireSockExe is not null)
                        return (true, "WireSock SDK instalado e encontrado.");
                    await Task.Delay(1000);
                }

                return (false, "O WinGet concluiu, mas o executável wiresock-client.exe não foi encontrado. Confira a instalação do WireSock SDK.");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                return (false, "O WinGet não está disponível neste Windows. Instale o WireSock SDK pelo site oficial e tente novamente.");
            }
            catch (Exception ex)
            {
                return (false, $"Não foi possível instalar o WireSock SDK pelo WinGet: {ex.Message}");
            }
        }

        public async Task<(bool Success, string StatusMessage)> ConnectAsync(string configPath, IReadOnlyList<string>? tunneledApps = null)
        {
            if (IsConnected) return (true, "Túnel WireGuard ativo.");
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return (false, "Selecione um perfil WireGuard (.conf) válido.");

            try
            {
                ValidateConfig(await File.ReadAllTextAsync(configPath));
                var probe = await CaptureProbeTargetAsync();
                if (probe is null)
                    return (false, "Não foi possível confirmar o acesso à internet antes da VPN. Verifique a conexão e tente novamente.");

                _wireSockExe = FindWireSockExecutable();
                if (_wireSockExe != null)
                {
                    var result = await ConnectWithWireSockAsync(configPath, tunneledApps ?? Array.Empty<string>());
                    if (!result.Success) return result;
                    var verification = await VerifyTunnelAsync(probe);
                    if (verification.Success) return (true, $"{result.StatusMessage} {verification.Message}");
                    await DisconnectAsync(configPath);
                    return (false, $"{verification.Message} O túnel foi encerrado para restaurar a conexão normal.");
                }

                if (tunneledApps is { Count: > 0 })
                    return (false, "Split Tunneling por aplicativo precisa do WireSock SDK. Instale o WireSock Secure Connect e tente novamente.");
                _wireGuardExe = FindWireGuardExecutable();
                if (_wireGuardExe == null)
                    return (false, "WireGuard para Windows não foi encontrado. Instale o cliente oficial do WireGuard e tente novamente.");

                var install = await RunProcessAsync(_wireGuardExe, $"/installtunnelservice \"{configPath}\"", 30000);
                if (install.ExitCode != 0)
                    return (false, ExplainInstallError(install, TunnelName));

                string serviceName = "WireGuardTunnel$" + Path.GetFileNameWithoutExtension(configPath);
                for (int i = 0; i < 20; i++)
                {
                    var query = await RunProcessAsync("sc.exe", $"query \"{serviceName}\"", 5000);
                    if (query.ExitCode == 0 && query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) &&
                        IsTunnelAdapterUp(Path.GetFileNameWithoutExtension(configPath)))
                    {
                        IsConnected = true;
                        var verification = await VerifyTunnelAsync(probe);
                        if (verification.Success)
                            return (true, $"Serviço e adaptador virtual {Path.GetFileNameWithoutExtension(configPath)} ativos. {verification.Message}");
                        await DisconnectAsync(configPath);
                        return (false, $"{verification.Message} O túnel foi encerrado para restaurar a conexão normal.");
                    }
                    await Task.Delay(500);
                }

                await RunProcessAsync(_wireGuardExe, $"/uninstalltunnelservice \"{Path.GetFileNameWithoutExtension(configPath)}\"", 10000);
                return (false, $"O serviço {serviceName} não entrou em execução. Confira o perfil e o log do WireGuard.");
            }
            catch (Exception ex)
            {
                return (false, $"Falha ao iniciar WireGuard: {ex.Message}");
            }
        }

        public async Task DisconnectAsync(string configPath)
        {
            if (_wireSockProcess is { } wsProcess)
            {
                Interlocked.Exchange(ref _intentionalWireSockExit, 1);
                try
                {
                    if (!wsProcess.HasExited) wsProcess.Kill(entireProcessTree: true);
                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await wsProcess.WaitForExitAsync(timeout.Token);
                }
                catch (InvalidOperationException) { }
                catch (System.Threading.Tasks.TaskCanceledException) { }
                finally { wsProcess.Dispose(); _wireSockProcess = null; Interlocked.Exchange(ref _intentionalWireSockExit, 0); }
                TryDelete(Path.Combine(Path.GetDirectoryName(configPath)!, "GVPN_Tunnel_Split.conf"));
                IsConnected = false;
                return;
            }
            string name = Path.GetFileNameWithoutExtension(configPath);
            string? exe = _wireGuardExe ?? FindWireGuardExecutable();
            if (exe != null && !string.IsNullOrWhiteSpace(name))
            {
                await RunProcessAsync(exe, $"/uninstalltunnelservice \"{name}\"", 15000);
                string service = "WireGuardTunnel$" + name;
                for (int i = 0; i < 10; i++)
                {
                    var result = await RunProcessAsync("sc.exe", $"query \"{service}\"", 5000);
                    if (result.ExitCode != 0 || result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) break;
                    await Task.Delay(300);
                }
            }
            IsConnected = false;
        }

        private async Task<(bool Success, string StatusMessage)> ConnectWithWireSockAsync(string configPath, IReadOnlyList<string> tunneledApps)
        {
            string effectiveConfig = configPath;
            if (tunneledApps.Count > 0)
            {
                effectiveConfig = Path.Combine(Path.GetDirectoryName(configPath)!, "GVPN_Tunnel_Split.conf");
                string config = await File.ReadAllTextAsync(configPath);
                config = ApplySplitTunnelApps(config, tunneledApps.Append(GetCurrentExecutablePath()).ToArray());
                await File.WriteAllTextAsync(effectiveConfig, config, new UTF8Encoding(false));
            }

            var start = new ProcessStartInfo
            {
                FileName = _wireSockExe!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(effectiveConfig)!
            };
            foreach (string arg in new[] { "run", "-config", effectiveConfig, "-log-level", "info", "-network-lock", "disabled" })
                start.ArgumentList.Add(arg);

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += WireSockProcess_Exited;
            process.Start();
            _wireSockProcess = process;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (process.HasExited)
            {
                int exitCode = process.ExitCode;
                process.Dispose();
                return (false, $"WireSock encerrou durante a conexão (código {exitCode}). Confira se o driver do WireSock está instalado e ativo.");
            }
            if (process.HasExited)
            {
                int exitCode = process.ExitCode;
                _wireSockProcess = null;
                IsConnected = false;
                process.Dispose();
                return (false, $"WireSock encerrou durante a inicialização (código {exitCode}). Confira se o driver do WireSock está instalado e ativo.");
            }
            IsConnected = true;
            if (process.HasExited)
            {
                int exitCode = process.ExitCode;
                IsConnected = false;
                _wireSockProcess = null;
                process.Dispose();
                return (false, $"WireSock encerrou durante a inicialização (código {exitCode}). Confira se o driver do WireSock está instalado e ativo.");
            }
            return (true, tunneledApps.Count > 0
                ? $"WireSock conectado. Split Tunneling ativo para {tunneledApps.Count} programa(s); IPv6 segue pelo perfil Proton e nenhuma configuração IPv6 do Windows foi alterada."
                : "WireSock conectado em túnel completo; IPv6 segue pelo perfil Proton e nenhuma configuração IPv6 do Windows foi alterada.");
        }

        private sealed record TunnelProbeTarget(IPAddress Address, string BaselineIp);

        private static async Task<TunnelProbeTarget?> CaptureProbeTargetAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var addresses = await Dns.GetHostAddressesAsync("api.ipify.org", timeout.Token);
                foreach (var address in addresses.OrderByDescending(address => address.AddressFamily == AddressFamily.InterNetwork))
                {
                    string? publicIp = await RequestPublicIpAsync(address, timeout.Token);
                    if (publicIp is not null) return new TunnelProbeTarget(address, publicIp);
                }
            }
            catch { }
            return null;
        }

        private static async Task<string?> RequestPublicIpAsync(IPAddress address, CancellationToken cancellationToken)
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4)
            };
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("https://api.ipify.org/", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            string value = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == address.AddressFamily
                ? parsed.ToString()
                : null;
        }

        private async Task<(bool Success, string Message)> VerifyTunnelAsync(TunnelProbeTarget probe)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!IsConnected) return (false, "O processo WireSock ou o adaptador encerrou antes de validar o tráfego.");
                try
                {
                    string? currentIp = await RequestPublicIpAsync(probe.Address, CancellationToken.None);
                    if (currentIp is not null && !string.Equals(currentIp, probe.BaselineIp, StringComparison.Ordinal))
                        return (true, $"Tráfego verificado pelo túnel (IP de saída {currentIp}).");
                }
                catch { }
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return (false, "O túnel iniciou, mas a verificação não confirmou saída de internet pela VPN ou o IP de saída não mudou.");
        }

        private static string GetCurrentExecutablePath()
        {
            string? executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable)) return executable;
            return Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Não foi possível identificar o executável do GVPN para validar o Split Tunneling.");
        }

        private void WireSockProcess_Exited(object? sender, EventArgs e)
        {
            if (sender is not Process process || Volatile.Read(ref _intentionalWireSockExit) != 0 || !ReferenceEquals(_wireSockProcess, process)) return;
            int exitCode;
            try { exitCode = process.ExitCode; } catch { exitCode = -1; }
            _wireSockProcess = null;
            IsConnected = false;
            ConnectionLost?.Invoke(this, $"O processo WireSock encerrou inesperadamente (código {exitCode}); o túnel não está mais ativo.");
        }

        private static string ApplySplitTunnelApps(string config, IReadOnlyList<string> apps)
        {
            var unique = apps.Select(app => app.Trim()).Where(app => app.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unique.Length == 0) return config;
            if (unique.Any(app => app.Contains(',') || app.Contains('\r') || app.Contains('\n')))
                throw new InvalidDataException("O caminho escolhido contém caracteres não aceitos pelo WireSock.");

            string allowedApps = string.Join(", ", unique);
            var lines = config.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => !Regex.IsMatch(line, @"^\s*DNS\s*=", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(line, @"^\s*(?:#@ws:)?AllowedApps\s*=", RegexOptions.IgnoreCase))
                .ToList();
            lines.Add($"#@ws:AllowedApps = {allowedApps}");
            return string.Join("\r\n", lines);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void ValidateConfig(string config)
        {
            string[] Required(string section, string key) => Regex.Matches(config,
                $@"(?im)^\s*{Regex.Escape(key)}\s*=\s*(.*?)\s*$")
                .Select(m => m.Groups[1].Value.Trim()).ToArray();

            string? privateKey = Required("Interface", "PrivateKey").FirstOrDefault();
            string? publicKey = Required("Peer", "PublicKey").FirstOrDefault();
            string? endpoint = Required("Peer", "Endpoint").FirstOrDefault();
            string? allowedIps = Required("Peer", "AllowedIPs").FirstOrDefault();
            if (!Regex.IsMatch(config, @"(?im)^\s*\[Interface\]\s*$") ||
                !Regex.IsMatch(config, @"(?im)^\s*\[Peer\]\s*$"))
                throw new InvalidDataException("O perfil precisa conter as seções [Interface] e [Peer].");
            ValidateKey(privateKey, "PrivateKey");
            ValidateKey(publicKey, "PublicKey");
            if (string.IsNullOrWhiteSpace(endpoint) || !Regex.IsMatch(endpoint, @"^[^\s:]+:\d{1,5}$"))
                throw new InvalidDataException("Endpoint ausente ou inválido no perfil.");
            int port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..]);
            if (port is < 1 or > 65535) throw new InvalidDataException("A porta do Endpoint é inválida.");
            if (string.IsNullOrWhiteSpace(allowedIps)) throw new InvalidDataException("AllowedIPs está ausente no perfil.");

            static void ValidateKey(string? key, string name)
            {
                try
                {
                    if (key == null || Convert.FromBase64String(key).Length != 32)
                        throw new FormatException();
                }
                catch (FormatException) { throw new InvalidDataException($"{name} ausente ou inválida (esperada chave WireGuard de 32 bytes em Base64)."); }
            }
        }

        private static string? FindWireGuardExecutable()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidates = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wireguard.exe"),
                Path.Combine(programFiles, "WireGuard", "wireguard.exe"),
                Path.Combine(programFilesX86, "WireGuard", "wireguard.exe"),
                Path.Combine(programFiles, "Proton", "VPN", "wireguard.exe"),
                Path.Combine(programFiles, "Proton", "VPN", "current", "wireguard.exe"),
                Path.Combine(programFiles, "ProtonVPN", "vwclient", "wireguard.exe"),
                Path.Combine(programFilesX86, "ProtonVPN", "vwclient", "wireguard.exe")
            };
            string protonRoot = Path.Combine(programFiles, "Proton", "VPN");
            try
            {
                if (Directory.Exists(protonRoot))
                    candidates.AddRange(Directory.EnumerateDirectories(protonRoot)
                        .Select(directory => Path.Combine(directory, "wireguard.exe")));
            }
            catch { }
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string? FindWireSockExecutable()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var roots = new[]
            {
                Path.Combine(programFiles, "WireSock Secure Connect"),
                Path.Combine(programFiles, "WireSock Secure Connect SDK"),
                Path.Combine(programFiles, "WireSock VPN Client"),
                Path.Combine(programFiles, "WireSock"),
                Path.Combine(programFilesX86, "WireSock Secure Connect"),
                Path.Combine(programFilesX86, "WireSock Secure Connect SDK"),
                Path.Combine(programFilesX86, "WireSock VPN Client"),
                Path.Combine(programFilesX86, "WireSock")
            };
            var searchRoots = roots.Concat(GetWireSockInstallLocations()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string root in searchRoots)
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(root, "sdk", "wiresock-client.exe"),
                    Path.Combine(root, "wiresock-client.exe"),
                    Path.Combine(root, "bin", "wiresock-client.exe")
                })
                    if (File.Exists(candidate)) return candidate;

                try
                {
                    if (Directory.Exists(root))
                    {
                        string? nested = Directory.EnumerateFiles(root, "wiresock-client.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (nested is not null) return nested;
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        private static IReadOnlyList<string> GetWireSockInstallLocations()
        {
            var locations = new List<string>();
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wiresock-client.exe");
                    if (appPaths?.GetValue(null) is string executable && Path.IsPathFullyQualified(executable))
                        locations.Add(Path.GetDirectoryName(executable)!);

                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;
                    foreach (string subkeyName in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(subkeyName);
                        string? displayName = entry?.GetValue("DisplayName") as string;
                        string? installLocation = entry?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Contains("WireSock", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(installLocation) && Path.IsPathFullyQualified(installLocation))
                            locations.Add(installLocation);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                finally { baseKey?.Dispose(); }
            }
            return locations;
        }

        private static bool IsTunnelAdapterUp(string tunnelName)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    (adapter.Name.Contains(tunnelName, StringComparison.OrdinalIgnoreCase) ||
                     adapter.Description.Contains(tunnelName, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }

        private static async Task<(int ExitCode, string Output)> RunProcessAsync(string file, string args, int timeoutMs)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }};
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(timeoutMs);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException($"{Path.GetFileName(file)} excedeu o tempo limite."); }
            return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
        }

        private static string ExplainInstallError((int ExitCode, string Output) result, string name)
        {
            string text = result.Output.Trim();
            if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return $"Já existe um serviço WireGuard para este perfil ({name}). Desconecte a sessão anterior ou remova o serviço antigo.";
            return $"WireGuard recusou o perfil (código {result.ExitCode}). {text}";
        }
    }
}
