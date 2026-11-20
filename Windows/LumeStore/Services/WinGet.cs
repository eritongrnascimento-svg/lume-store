using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LumeStore.Services;

public sealed record Package(string Name, string Id, string Version, string Available = "", string Source = "winget", string Category = "");
public sealed record CommandResult(int Code, string Output);

public sealed class WinGet
{
    private readonly Func<IReadOnlyList<string>> executableCandidates;
    public WinGet() : this(WinGetExecutable.Candidates) { }
    internal WinGet(Func<IReadOnlyList<string>> executableCandidates) => this.executableCandidates = executableCandidates;
    private readonly SemaphoreSlim readers = new(2);
    private readonly Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeStore");
    public async Task<CommandResult> Run(IEnumerable<string> arguments, Action<string>? output = null, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--disable-interactivity");
        using var process = new Process { StartInfo = start };
        var failures = new List<string>();
        var started = false;
        foreach (var executable in await Task.Run(executableCandidates, cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            start.FileName = executable;
            try { started = process.Start(); if (started) break; }
            catch (System.ComponentModel.Win32Exception ex) { failures.Add($"{executable}: {ex.Message} (Win32 {ex.NativeErrorCode})"); }
        }
        if (!started) throw new InvalidOperationException("O Windows não permitiu iniciar o WinGet pelos caminhos disponíveis. Verifique o Instalador de Aplicativo e o alias winget.exe em Configurações > Aplicativos > Configurações avançadas de aplicativos > Aliases de execução de aplicativo. Se winget --version funciona no Terminal, envie os detalhes abaixo.\n" + string.Join("\n", failures));
        var buffer = new StringBuilder();
        var gate = new object();
        async Task Pump(StreamReader stream)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
                lock (gate) buffer.AppendLine(line);
                output?.Invoke(line);
            }
        }
        // Cancellation stops read-only queries; installation is deliberately not force-killed.
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch { } });
        await Task.WhenAll(Task.Run(() => Pump(process.StandardOutput)), Task.Run(() => Pump(process.StandardError)), process.WaitForExitAsync()).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new(process.ExitCode, buffer.ToString());
    }
    public async Task<List<Package>> Catalog(string query, string? tag, CancellationToken token, string source = "winget")
    {
        var args = new List<string> { "search", "--source", source, "--accept-source-agreements" };
        if (tag is not null) { args.Add("--tag"); args.Add(tag); }
        else { args.Add("--query"); args.Add(query); }
        var result = await Run(args, cancellation: token).ConfigureAwait(false);
        return await Task.Run(() => ParseOrThrow(result).Select(package => package with { Source = source, Category = tag ?? "" }).ToList(), token).ConfigureAwait(false);
    }
    public async Task<List<Package>> Installed(bool updates, CancellationToken token)
    {
        var args = new List<string> { "list", "--source", "winget", "--accept-source-agreements" };
        if (updates) args.Add("--upgrade-available");
        var result = await Run(args, cancellation: token).ConfigureAwait(false);
        return await Task.Run(() => ParseOrThrow(result), token).ConfigureAwait(false);
    }
    public async Task<string> Details(string id, CancellationToken token = default, string source = "winget")
    {
        var key = source + "/" + id;
        lock (metadata) if (metadata.TryGetValue(key, out var cached)) return cached;
        await readers.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (metadata) if (metadata.TryGetValue(key, out var cached)) return cached;
            var result = await Run(["show", "--id", id, "--exact", "--source", source, "--accept-source-agreements"], cancellation: token).ConfigureAwait(false);
            if (result.Code != 0) throw new InvalidOperationException(result.Output);
            lock (metadata) metadata[key] = result.Output;
            return result.Output;
        }
        finally { readers.Release(); }
    }
    public static List<Package> ParseOrThrow(CommandResult result)
    {
        // APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND is an empty result, not a transport error.
        if (result.Code == unchecked((int)0x8A150014)) return [];
        if (result.Code != 0) throw new InvalidOperationException(result.Output);
        return Parse(result.Output);
    }
    public static List<Package> Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var index = Array.FindIndex(lines, l => Regex.IsMatch(l.Trim(), "^-{5,}$"));
        if (index < 1) throw new InvalidOperationException("O formato de resposta do WinGet não foi reconhecido. Atualize o WinGet e tente novamente.\n" + text);
        var headers = Regex.Matches(lines[index - 1], @"\S.*?(?=\s{2,}|$)").Select(m => (m.Value.Trim(), m.Index)).ToArray();
        if (headers.Length < 3) throw new InvalidOperationException("Não foi possível reconhecer as colunas do WinGet.");
        string Cell(string line, int column) => column >= headers.Length || line.Length <= headers[column].Index ? "" : line.Substring(headers[column].Index, Math.Min(line.Length, column + 1 < headers.Length ? headers[column + 1].Index : line.Length) - headers[column].Index).Trim();
        var available = Array.FindIndex(headers, h => h.Item1.Equals("Available", StringComparison.OrdinalIgnoreCase) || h.Item1.Equals("Disponível", StringComparison.OrdinalIgnoreCase));
        return lines.Skip(index + 1).Where(l => l.Length > headers[2].Index).Select(l => new Package(Cell(l, 0), Cell(l, 1), Cell(l, 2), available >= 0 ? Cell(l, available) : "")).Where(p => !string.IsNullOrWhiteSpace(p.Id) && !p.Id.Contains(' ') && !p.Id.Contains('…')).DistinctBy(p => p.Id).ToList();
    }
    public static string Summary(string metadata)
    {
        var match = Regex.Match(metadata, @"(?im)^\s*(Description|Descrição|Short Description|Descrição curta):\s*(.+)$");
        return match.Success ? match.Groups[2].Value : "Consulte os metadados completos publicados no WinGet.";
    }
}
