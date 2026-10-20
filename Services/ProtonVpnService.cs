using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace GVPN.Services
{
    public sealed record ProtonServer(string Name, string Country, string City, string Tier, int Load, double Score, int? PingMs)
    {
        public string PingDisplay => PingMs is > 0 ? $"{PingMs} ms" : "—";
        public string RegionDisplay
        {
            get
            {
                try { return $"{City}, {new System.Globalization.RegionInfo(Country).DisplayName}"; }
                catch (ArgumentException) { return $"{City}, {Country}"; }
            }
        }
    }

    public sealed record ProtonResult(bool Success, string? Code = null, string? Error = null, string? CaptchaUrl = null);

    public sealed class ProtonVpnService
    {
        private readonly string _appData;
        private readonly string _sessionFile;
        private readonly string _profileFile;
        private readonly string _helperPath;
        private readonly bool _extractBundledHelper;

        public ProtonVpnService()
        {
            _appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVPN");
            Directory.CreateDirectory(_appData);
            _sessionFile = Path.Combine(_appData, "proton-session.enc");
            _profileFile = Path.Combine(_appData, "GVPN_Tunnel.conf");
            string besideApp = Path.Combine(AppContext.BaseDirectory, "proton-confgen.exe");
            string developmentPath = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "proton-confgen", "build", "proton-confgen.exe");
            string bundledPath = Path.Combine(_appData, "bin", "proton-confgen.exe");
            if (File.Exists(besideApp)) _helperPath = besideApp;
            else if (File.Exists(developmentPath)) _helperPath = developmentPath;
            else
            {
                _helperPath = bundledPath;
                _extractBundledHelper = true;
            }
        }

        public string ProfileFile => _profileFile;
        public bool HasSession => File.Exists(_sessionFile);

        public async Task<string?> GetStoredUsernameAsync()
        {
            if (!HasSession) return null;
            await EnsureHelperAsync();
            var result = await RunAsync(new[] { "-session-file", _sessionFile, "-session-username", "-json" });
            try
            {
                using var doc = ParseJson(result.Stdout);
                return ReadBool(doc.RootElement, "success") ? ReadString(doc.RootElement, "username") : null;
            }
            catch (JsonException) { return null; }
        }

        public async Task<ProtonResult> LoginAsync(string username, string password, string? twoFactorCode = null, string? humanVerificationToken = null)
        {
            await EnsureHelperAsync();
            if (!File.Exists(_helperPath))
                return new(false, "MISSING_EXECUTABLE", "O componente ProtonVPN do GVPN não foi encontrado.");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return new(false, "INVALID_INPUT", "Informe usuário e senha da conta Proton.");

            // The user supplied a password explicitly, so authenticate afresh instead of
            // letting a stale cached session short-circuit the login attempt.
            var result = await RunAsync(new[] { "-username", username.Trim(), "-session-file", _sessionFile, "-clear-session", "-login-only", "-stdin-secrets", "-json" },
                new { password, twoFactorCode = twoFactorCode ?? "", humanVerificationToken = humanVerificationToken ?? "" });
            return ParseResult(result.Stdout, result.Stderr, result.ExitCode);
        }

        public async Task<(bool Success, string Message, bool IsFree, List<ProtonServer> Servers)> LoadServersAsync(string username)
        {
            var plan = await RunAsync(new[] { "-username", username, "-session-file", _sessionFile, "-check-plan", "-json" });
            using var planDoc = ParseJson(plan.Stdout);
            var planRoot = planDoc.RootElement;
            if (plan.ExitCode != 0 || !ReadBool(planRoot, "success"))
                return (false, ReadString(planRoot, "error") ?? "Não foi possível confirmar a assinatura Proton.", false, new());

            string status = ReadString(planRoot, "status") ?? "unknown";
            if (status is not ("free" or "premium"))
                return (false, "A Proton não confirmou o plano desta conta. Nenhum servidor foi liberado.", false, new());

            bool isFree = status == "free";
            var args = new List<string> { "-username", username, "-session-file", _sessionFile, "-route-catalog", "-auto-ping", "-p2p-only=false", "-json" };
            if (isFree) args.Add("-free-only");
            var response = await RunAsync(args);
            if (response.ExitCode != 0)
                return (false, ExtractError(response.Stdout, response.Stderr), isFree, new());

            using var serverDoc = ParseJson(response.Stdout);
            if (!ReadBool(serverDoc.RootElement, "success") || !serverDoc.RootElement.TryGetProperty("routes", out var routesElement) || routesElement.ValueKind != JsonValueKind.Array)
                return (false, "A Proton não retornou um catálogo de servidores válido.", isFree, new());

            var servers = routesElement.EnumerateArray().Select(item => new ProtonServer(
                ReadString(item, "server") ?? "",
                ReadString(item, "country") ?? "",
                ReadString(item, "city") ?? "",
                ReadString(item, "tier") ?? "",
                ReadInt(item, "load"),
                ReadDouble(item, "score"),
                ReadInt(item, "pingMs") > 0 ? ReadInt(item, "pingMs") : (int?)null))
                .Where(server => !string.IsNullOrWhiteSpace(server.Name))
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return servers.Count == 0
                ? (false, "A Proton não retornou servidores disponíveis para esta assinatura.", isFree, servers)
                : (true, $"Plano {ReadString(planRoot, "planTitle") ?? status}. {servers.Count} servidores carregados.", isFree, servers);
        }

        public async Task<(bool Success, string Message)> GenerateProfileAsync(string username, ProtonServer server)
        {
            var result = await RunAsync(new[]
            {
                "-username", username, "-session-file", _sessionFile,
                "-server", server.Name, "-no-save", "-ipv6", "-output", _profileFile, "-json"
            });
            var parsed = ParseResult(result.Stdout, result.Stderr, result.ExitCode);
            if (!parsed.Success) return (false, parsed.Error ?? "A Proton não conseguiu gerar o perfil WireGuard.");
            if (!File.Exists(_profileFile)) return (false, "O helper não gerou o perfil WireGuard esperado.");
            return (true, $"Perfil Proton gerado para {server.Name} ({server.Country}).");
        }

        public void ClearSession()
        {
            if (File.Exists(_sessionFile)) File.Delete(_sessionFile);
            if (File.Exists(_profileFile)) File.Delete(_profileFile);
        }

        private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IReadOnlyList<string> args, object? secrets = null)
        {
            await EnsureHelperAsync();
            if (!File.Exists(_helperPath)) throw new FileNotFoundException("proton-confgen.exe não foi encontrado.", _helperPath);
            var start = new ProcessStartInfo { FileName = _helperPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = secrets != null,
                WorkingDirectory = _appData };
            foreach (string arg in args) start.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = start };
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (secrets != null)
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(secrets));
                process.StandardInput.Close();
            }
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("A solicitação ao serviço Proton excedeu o tempo limite.");
            }
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }

        private Task EnsureHelperAsync() => _extractBundledHelper
            ? EmbeddedResources.ExtractFileAsync("GVPN.Embedded.proton-confgen.exe", _helperPath)
            : Task.CompletedTask;

        private static ProtonResult ParseResult(string stdout, string stderr, int exitCode)
        {
            try
            {
                using var doc = ParseJson(stdout);
                var root = doc.RootElement;
                return new ProtonResult(ReadBool(root, "success"), ReadString(root, "code"),
                    ReadString(root, "error") ?? ReadString(root, "message"), ReadString(root, "captchaUrl"));
            }
            catch (JsonException)
            {
                return new ProtonResult(false, "HELPER_ERROR", ExtractError(stdout, stderr, exitCode));
            }
        }

        private static string ExtractError(string stdout, string stderr, int? exitCode = null)
        {
            foreach (string text in new[] { stdout, stderr })
            {
                try
                {
                    using var doc = ParseJson(text);
                    string? error = ReadString(doc.RootElement, "error");
                    if (!string.IsNullOrWhiteSpace(error)) return error;
                }
                catch (JsonException) { }
            }
            string detail = string.Join(" ", new[] { stdout, stderr }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            if (detail.Length > 900) detail = detail[..900];
            return string.IsNullOrWhiteSpace(detail) ? $"O componente Proton falhou (código {exitCode})." : detail;
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static bool ReadBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : 0;
        private static double ReadDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetDouble(out double result) ? result : 0;

        // The helper may emit status lines (for example, saved-session expiry) before JSON.
        // Parse the first complete JSON object/array instead of requiring stdout to be JSON-only.
        private static JsonDocument ParseJson(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'{' && bytes[i] != (byte)'[') continue;
                try
                {
                    var reader = new Utf8JsonReader(bytes.AsSpan(i));
                    return JsonDocument.ParseValue(ref reader);
                }
                catch (JsonException) { }
            }
            throw new JsonException("Nenhuma resposta JSON válida foi encontrada na saída do helper.");
        }
    }

    public sealed class SavedProtonCredentials
    {
        private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVPN", "proton-credentials.dpapi");

        public (string Username, string Password)? Load()
        {
            if (!File.Exists(_path)) return null;
            try
            {
                byte[] encrypted = File.ReadAllBytes(_path);
                byte[] plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                using var document = JsonDocument.Parse(plaintext);
                string? username = document.RootElement.GetProperty("username").GetString();
                string? password = document.RootElement.GetProperty("password").GetString();
                return string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) ? null : (username, password);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or KeyNotFoundException)
            {
                return null;
            }
        }

        public void Save(string username, string password)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new { username, password });
            byte[] encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        public void Delete()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        public bool Exists => File.Exists(_path);
    }
}
