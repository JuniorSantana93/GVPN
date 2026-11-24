using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GVPN.Services
{
    public sealed class NetworkTunnelManager
    {
        private const string TunnelName = "GVPN_Tunnel";
        private string? _wireGuardExe;
        private string? _wireSockExe;
        private Process? _wireSockProcess;
        private SafeJobHandle? _wireSockJob;
        private bool _usingWireSock;
        private int _intentionalWireSockExit;
        private int _recoveringTunnel;
        private CancellationTokenSource? _healthMonitorCancellation;
        private TunnelProbeTarget? _activeProbe;
        private string _activeConfigPath = "";
        private readonly SemaphoreSlim _disconnectGate = new(1, 1);
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
                _usingWireSock = _wireSockExe is not null;
                if (_wireSockExe != null)
                {
                    _activeConfigPath = configPath;
                    _activeProbe = probe;
                    var result = await ConnectWithWireSockAsync(configPath, tunneledApps ?? Array.Empty<string>());
                    if (!result.Success)
                    {
                        await DisconnectAsync(configPath);
                        bool restoredAfterStartFailure = await WaitForNormalInternetAsync(probe);
                        return (false, result.StatusMessage + " " + (restoredAfterStartFailure
                            ? "A conexão normal com a internet foi restaurada."
                            : "Não foi possível confirmar a restauração da internet; reinicie a conexão de rede do Windows."));
                    }
                    var verification = await VerifyTunnelAsync(probe);
                    if (verification.Success)
                    {
                        StartConnectionHealthMonitor(configPath, probe);
                        return (true, $"{result.StatusMessage} {verification.Message}");
                    }
                    await DisconnectAsync(configPath);
                    bool restored = await WaitForNormalInternetAsync(probe);
                    return (false, $"{verification.Message} O túnel foi encerrado. " + (restored
                        ? "A conexão normal com a internet foi restaurada."
                        : "Não foi possível confirmar a restauração da internet; reinicie a conexão de rede do Windows."));
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
                if (_usingWireSock)
                {
                    try { await DisconnectAsync(configPath); } catch { }
                }
                return (false, $"Falha ao iniciar WireGuard: {ex.Message}");
            }
        }

        public async Task DisconnectAsync(string configPath)
        {
            await _disconnectGate.WaitAsync();
            try
            {
                StopConnectionHealthMonitor();
                if (_usingWireSock || _wireSockProcess is not null || _wireSockJob is not null)
                {
                    if (_wireSockProcess is { } wsProcess)
                    {
                        Interlocked.Exchange(ref _intentionalWireSockExit, 1);
                        try
                        {
                            if (!wsProcess.HasExited) wsProcess.Kill(entireProcessTree: true);
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await wsProcess.WaitForExitAsync(timeout.Token);
                        }
                        catch (InvalidOperationException) { }
                        catch (TaskCanceledException) { }
                        finally
                        {
                            wsProcess.Dispose();
                            _wireSockProcess = null;
                            Interlocked.Exchange(ref _intentionalWireSockExit, 0);
                        }
                    }
                    CloseWireSockJob();
                    _usingWireSock = false;
                    _activeProbe = null;
                    _activeConfigPath = "";
                    IsConnected = false;
                    TryDelete(Path.Combine(Path.GetDirectoryName(configPath)!, "GVPN_Tunnel_Split.conf"));
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
            }
            finally
            {
                IsConnected = false;
                _disconnectGate.Release();
            }
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
            _wireSockProcess = process;
            try
            {
                _wireSockJob = CreateKillOnCloseJob();
                process.Start();
            }
            catch
            {
                _wireSockProcess = null;
                process.Dispose();
                CloseWireSockJob();
                throw;
            }
            try
            {
                if (!AssignProcessToJobObject(_wireSockJob!, process.Handle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível vincular o WireSock ao ciclo de vida do GVPN.");
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                _wireSockProcess = null;
                process.Dispose();
                CloseWireSockJob();
                throw;
            }
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (process.HasExited)
            {
                int exitCode = process.ExitCode;
                _wireSockProcess = null;
                IsConnected = false;
                process.Dispose();
                CloseWireSockJob();
                return (false, $"WireSock encerrou durante a conexão (código {exitCode}). Confira se o driver do WireSock está instalado e ativo.");
            }
            IsConnected = true;
            return (true, tunneledApps.Count > 0
                ? $"WireSock conectado. Split Tunneling ativo para {tunneledApps.Count} programa(s); IPv6 segue pelo perfil Proton e nenhuma configuração IPv6 do Windows foi alterada."
                : "WireSock conectado em túnel completo; IPv6 segue pelo perfil Proton e nenhuma configuração IPv6 do Windows foi alterada.");
        }

        private sealed record TunnelProbeTarget(IPAddress Address, string BaselineIp);

        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static SafeJobHandle CreateKillOnCloseJob()
        {
            IntPtr rawHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (rawHandle == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível criar o controle de ciclo de vida do WireSock.");

            var job = new SafeJobHandle(rawHandle);
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new System.ComponentModel.Win32Exception(error, "Não foi possível configurar o encerramento automático do WireSock.");
            }
            return job;
        }

        private void CloseWireSockJob()
        {
            Interlocked.Exchange(ref _wireSockJob, null)?.Dispose();
        }

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

        private void StartConnectionHealthMonitor(string configPath, TunnelProbeTarget probe)
        {
            StopConnectionHealthMonitor();
            var cancellation = new CancellationTokenSource();
            _healthMonitorCancellation = cancellation;
            _ = MonitorConnectionHealthAsync(configPath, probe, cancellation);
        }

        private void StopConnectionHealthMonitor()
        {
            var cancellation = _healthMonitorCancellation;
            _healthMonitorCancellation = null;
            if (cancellation is null) return;
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task MonitorConnectionHealthAsync(string configPath, TunnelProbeTarget probe, CancellationTokenSource cancellation)
        {
            int failedProbes = 0;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(12), cancellation.Token);
                    if (!IsConnected) return;
                    var process = _wireSockProcess;
                    if (process is null) return;
                    try { if (process.HasExited) return; }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }

                    string? currentIp = null;
                    try { currentIp = await RequestPublicIpAsync(probe.Address, cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                    catch { }

                    if (currentIp is not null && !string.Equals(currentIp, probe.BaselineIp, StringComparison.Ordinal))
                    {
                        failedProbes = 0;
                        continue;
                    }

                    failedProbes++;
                    if (failedProbes < 3) continue;
                    if (Interlocked.Exchange(ref _recoveringTunnel, 1) != 0) return;

                    try
                    {
                        await DisconnectAsync(configPath);
                        bool restored = await WaitForNormalInternetAsync(probe);
                        ConnectionLost?.Invoke(this, restored
                            ? "O túnel perdeu acesso à internet em três verificações seguidas. O WireSock foi encerrado e a conexão normal foi restaurada."
                            : "O túnel perdeu acesso à internet em três verificações seguidas. O WireSock foi encerrado, mas não foi possível confirmar a restauração. Reinicie a conexão de rede do Windows.");
                    }
                    catch (Exception ex)
                    {
                        ConnectionLost?.Invoke(this, $"O túnel perdeu acesso à internet e a recuperação automática falhou: {ex.Message}");
                    }
                    finally { Interlocked.Exchange(ref _recoveringTunnel, 0); }
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_healthMonitorCancellation, cancellation)) _healthMonitorCancellation = null;
                cancellation.Dispose();
            }
        }

        private static async Task<bool> WaitForNormalInternetAsync(TunnelProbeTarget probe)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                try
                {
                    string? currentIp = await RequestPublicIpAsync(probe.Address, CancellationToken.None);
                    if (string.Equals(currentIp, probe.BaselineIp, StringComparison.Ordinal)) return true;
                }
                catch { }
            }
            return false;
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
            bool wasConnected = IsConnected;
            _wireSockProcess = null;
            CloseWireSockJob();
            IsConnected = false;
            StopConnectionHealthMonitor();
            TryDelete(Path.Combine(Path.GetDirectoryName(_activeConfigPath) ?? "", "GVPN_Tunnel_Split.conf"));
            var probe = _activeProbe;
            ConnectionLost?.Invoke(this, wasConnected
                ? $"O processo WireSock encerrou inesperadamente (código {exitCode}); verificando a recuperação da internet."
                : $"O processo WireSock encerrou durante a conexão (código {exitCode}).");
            if (wasConnected && probe is not null) _ = ReportUnexpectedWireSockExitAsync(probe, exitCode);
        }

        private async Task ReportUnexpectedWireSockExitAsync(TunnelProbeTarget probe, int exitCode)
        {
            bool restored = await WaitForNormalInternetAsync(probe);
            ConnectionLost?.Invoke(this, restored
                ? $"O WireSock encerrou inesperadamente (código {exitCode}), mas a conexão normal com a internet foi restaurada."
                : $"O WireSock encerrou inesperadamente (código {exitCode}) e a internet não voltou. Reinicie a conexão de rede do Windows.");
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

