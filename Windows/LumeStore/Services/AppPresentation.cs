using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LumeStore.Services;

public sealed record AppInfo(string Name = "", string Publisher = "", string Version = "", string Description = "", string ShortDescription = "", string Homepage = "", string License = "", string LicenseUrl = "", string Icon = "", string Artwork = "", string Origin = "", string Locale = "", string Tags = "");

public sealed class AppPresentation
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8), DefaultRequestHeaders = { { "User-Agent", "LumeStore/1.1" } } };
    private readonly SemaphoreSlim requests = new(3);
    private readonly ConcurrentDictionary<string, AppInfo> cache = new();
    private readonly ConcurrentDictionary<string, string> iconCache = new();
    private readonly ConcurrentDictionary<string, AppInfo> siteCache = new();
    private static readonly Dictionary<string, string> websites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualStudioCode"] = "https://code.visualstudio.com/",
        ["Microsoft.PowerToys"] = "https://learn.microsoft.com/windows/powertoys/",
        ["VideoLAN.VLC"] = "https://www.videolan.org/vlc/"
    };
    public async Task<AppInfo> Get(Package package, CancellationToken token)
    {
        var version = string.IsNullOrWhiteSpace(package.Available) ? package.Version : package.Available;
        var key = "v2/" + package.Source + "/" + package.Id + "/" + version;
        if (cache.TryGetValue(key, out var value)) return value;
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(key, out value)) return value;
            var directory = Path.Combine(WinGet.DataDirectory, "metadata");
            var filename = Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
            try
            {
                if (File.Exists(filename) && DateTime.UtcNow - File.GetLastWriteTimeUtc(filename) < TimeSpan.FromDays(3))
                {
                    value = JsonSerializer.Deserialize<AppInfo>(await File.ReadAllTextAsync(filename, token).ConfigureAwait(false));
                    if (value is not null) return cache[key] = value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            value = new AppInfo(Name: package.Name, Version: version, Homepage: websites.GetValueOrDefault(package.Id, ""));
            if (package.Source == "winget" && Regex.IsMatch(package.Id, @"^[A-Za-z0-9][A-Za-z0-9._+-]+$") && !string.IsNullOrEmpty(version) && !version.Contains('…'))
            {
                var path = $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{char.ToLowerInvariant(package.Id[0])}/{string.Join('/', package.Id.Split('.').Select(Uri.EscapeDataString))}/{Uri.EscapeDataString(version)}/";
                var yaml = await OptionalText(path + package.Id + ".locale.pt-BR.yaml", token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(yaml)) yaml = await OptionalText(path + package.Id + ".locale.en-US.yaml", token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(yaml))
                {
                    var manifest = await OptionalText(path + package.Id + ".yaml", token).ConfigureAwait(false);
                    var locale = YamlValue(manifest, "DefaultLocale");
                    yaml = !string.IsNullOrEmpty(locale) && Regex.IsMatch(locale, @"^[a-zA-Z0-9-]+$") ? await OptionalText(path + package.Id + ".locale." + locale + ".yaml", token).ConfigureAwait(false) : manifest;
                }
                if (!string.IsNullOrEmpty(yaml))
                {
                    value = FromManifest(yaml) with { Version = version, Origin = "Manifesto do repositório WinGet" };
                    if (string.IsNullOrWhiteSpace(value.Name)) value = value with { Name = package.Name };
                    if (string.IsNullOrWhiteSpace(value.Homepage)) value = value with { Homepage = websites.GetValueOrDefault(package.Id, "") };
                }
            }
            token.ThrowIfCancellationRequested();
            cache[key] = value;
            if (!string.IsNullOrEmpty(value.Origin))
            {
                try { Directory.CreateDirectory(directory); await File.WriteAllTextAsync(filename, JsonSerializer.Serialize(value), token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return value;
        }
        finally { requests.Release(); }
    }
    public async Task<AppInfo> Site(string url, CancellationToken token)
    {
        if (!SafeUrl(url)) return new();
        if (siteCache.TryGetValue(url, out var cached)) return cached;
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (siteCache.TryGetValue(url, out cached)) return cached;
            var html = await OptionalText(url, token).ConfigureAwait(false);
            return siteCache[url] = ParseSite(html, new Uri(url));
        }
        finally { requests.Release(); }
    }
    public async Task<string> ImageFile(string url, CancellationToken token)
    {
        if (!SafeUrl(url)) return "";
        if (iconCache.TryGetValue(url, out var existing) && File.Exists(existing)) return existing;
        var directory = Path.Combine(WinGet.DataDirectory, "images");
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is not (".svg" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".ico")) extension = ".png";
        var file = Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url))) + extension);
        if (File.Exists(file)) { iconCache[url] = file; return file; }
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 8 * 1024 * 1024) return "";
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > 8 * 1024 * 1024) return "";
                await data.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(file, data.ToArray(), token).ConfigureAwait(false);
            iconCache[url] = file;
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException) { return ""; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
        finally { requests.Release(); }
    }
    private static async Task<string> OptionalText(string url, CancellationToken token)
    {
        if (!SafeUrl(url)) return "";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) return "";
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new char[8192]; var builder = new StringBuilder(); int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (builder.Length + read > 1024 * 1024) break;
                builder.Append(buffer, 0, read);
            }
            return builder.ToString();
        }
        catch (HttpRequestException) { return ""; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
    }
    public static bool SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !uri.IsLoopback && uri.Host.Contains('.') && !IPAddress.TryParse(uri.Host, out _);
    public static AppInfo FromManifest(string yaml) => new(Name: YamlValue(yaml, "PackageName"), Publisher: YamlValue(yaml, "Publisher"), Version: YamlValue(yaml, "PackageVersion"), Description: YamlValue(yaml, "Description"), ShortDescription: YamlValue(yaml, "ShortDescription"), Homepage: YamlValue(yaml, "PackageUrl"), License: YamlValue(yaml, "License"), LicenseUrl: YamlValue(yaml, "LicenseUrl"), Icon: YamlValue(yaml, "IconUrl"), Locale: YamlValue(yaml, "PackageLocale"), Tags: ManifestTags(yaml));
    private static string ManifestTags(string yaml)
    {
        var match = Regex.Match(yaml, @"(?m)^Tags:[ \t]*\r?\n((?:[ \t]*-[ \t]+[^\r\n]+\r?\n?)+)");
        return match.Groups[1].Value;
    }
    // Reads only display scalars from the known WinGet schema; never interprets YAML objects or tags.
    public static string YamlValue(string yaml, string key)
    {
        var lines = yaml.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Regex.Match(line, @"^(\s*)(?:-\s*)?" + Regex.Escape(key) + @":\s*(.*)$");
            if (!match.Success) continue;
            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith('|') || value.StartsWith('>'))
            {
                var chunks = new List<string>(); var indent = match.Groups[1].Length;
                while (++i < lines.Length && (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TakeWhile(char.IsWhiteSpace).Count() > indent)) chunks.Add(lines[i].Trim());
                return string.Join(value.StartsWith('>') ? " " : "\n", chunks).Trim();
            }
            if (value.StartsWith('"') && value.EndsWith('"')) { try { return JsonSerializer.Deserialize<string>(value) ?? ""; } catch (JsonException) { return value[1..^1]; } }
            if (value.StartsWith('\'') && value.EndsWith('\'')) return value[1..^1].Replace("''", "'");
            return Regex.Replace(value, @"\s+#.*$", "").Trim();
        }
        return "";
    }
    public static AppInfo FromCli(string text)
    {
        var fields = new Dictionary<string, string>();
        string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant).ToArray());
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(\s*)([^:]+):[ \t]*(.*)$");
            if (!m.Success) continue;
            var key = Normalize(m.Groups[2].Value);
            var value = new StringBuilder(m.Groups[3].Value.Trim());
            var indent = m.Groups[1].Length;
            while (i + 1 < lines.Length && (string.IsNullOrWhiteSpace(lines[i + 1]) || lines[i + 1].TakeWhile(char.IsWhiteSpace).Count() > indent)) { value.AppendLine(); value.Append(lines[++i].Trim()); }
            fields.TryAdd(key, value.ToString().Trim());
        }
        string Get(params string[] keys) => keys.Select(Normalize).Select(k => fields.GetValueOrDefault(k, "")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        return new(Publisher: Get("Publisher", "Editor", "Publicador"), Version: Get("Version", "Versão"), Description: Get("Description", "Descrição"), ShortDescription: Get("Short Description", "Descrição curta"), Homepage: Get("Homepage", "Página inicial", "Package Url", "URL do pacote", "Publisher Url", "URL do editor", "URL do publicador"), License: Get("License", "Licença"), LicenseUrl: Get("License Url", "URL da licença"), Origin: "WinGet");
    }
    public static AppInfo ParseSite(string html, Uri origin)
    {
        string Attr(string tag, string name)
        {
            var match = Regex.Match(tag, "\\b" + Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase);
            return WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        }
        string Absolute(string value) => Uri.TryCreate(origin, value, out var uri) && SafeUrl(uri.AbsoluteUri) ? uri.AbsoluteUri : "";
        var links = Regex.Matches(html, @"<link\b[^>]{0,4096}>", RegexOptions.IgnoreCase).Select(m => m.Value).Where(t => Attr(t, "rel").Split(' ').Contains("icon", StringComparer.OrdinalIgnoreCase)).ToArray();
        var icon = links.Select(t => Absolute(Attr(t, "href"))).FirstOrDefault(u => Regex.IsMatch(u, @"\.(png|svg|jpg|webp)(\?|$)", RegexOptions.IgnoreCase)) ?? "";
        var art = Regex.Matches(html, @"<meta\b[^>]{0,4096}>", RegexOptions.IgnoreCase).Select(m => m.Value).Where(t => Attr(t, "property").Equals("og:image", StringComparison.OrdinalIgnoreCase) || Attr(t, "name").Equals("twitter:image", StringComparison.OrdinalIgnoreCase)).Select(t => Absolute(Attr(t, "content"))).FirstOrDefault() ?? "";
        return new(Icon: icon, Artwork: art);
    }
}
