using LumeStore.Services;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
static string Table(string[] columns, params string[][] rows)
{
    var widths = columns.Select((c, i) => Math.Max(c.Length, rows.Max(r => r[i].Length)) + 3).ToArray();
    string Line(string[] row) => string.Concat(row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
    return Line(columns) + "\n" + new string('-', widths.Sum()) + "\n" + string.Join("\n", rows.Select(Line));
}
var english = WinGet.Parse(Table(["Name", "Id", "Version", "Match", "Source"], ["Visual Studio Code", "Microsoft.VisualStudioCode", "1.100.1", "Tag: editor", "winget"], ["VLC media player", "VideoLAN.VLC", "3.0.21", "", "winget"]));
Check(english.Count == 2 && english[0].Id == "Microsoft.VisualStudioCode" && english[1].Name == "VLC media player", "Nomes com espaços e pesquisa em inglês");
var portuguese = WinGet.Parse(Table(["Nome", "ID", "Versão", "Disponível", "Origem"], ["Aplicativo de teste", "Vendor.Test", "1.0", "2.0", "winget"]));
Check(portuguese.Single().Available == "2.0" && portuguese.Single().Version == "1.0", "Atualizações com cabeçalhos em português");
Check(WinGet.ParseOrThrow(new(unchecked((int)0x8A150014), "nenhum resultado")).Count == 0, "Nenhum resultado não vira erro de rede");
try { WinGet.ParseOrThrow(new(1, "rede indisponível")); throw new Exception("Erro deveria ter sido propagado"); } catch (InvalidOperationException) { Console.WriteLine("PASS Erro de execução preservado"); }
Check(WinGet.Summary("Descrição: Um aplicativo de teste\nEditor: Teste") == "Um aplicativo de teste", "Descrição em português");
Check(WinGet.Summary("Description: A test application") == "A test application", "Descrição em inglês");
try { WinGet.Parse("formato desconhecido"); throw new Exception("Formato deveria ter sido rejeitado"); } catch (InvalidOperationException) { Console.WriteLine("PASS Formato incompatível é rejeitado"); }

if (OperatingSystem.IsWindows())
{
    var missing = Path.Combine(AppContext.BaseDirectory, "missing-winget-" + Guid.NewGuid().ToString("N") + ".exe");
    var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    // Only a harmless echo is executed; no installed packages or sources are changed.
    var runner = new WinGet(() => [missing, shell]);
    var probe = await runner.Run(["/d", "/c", "echo", "LUME_FALLBACK_OK"]);
    Check(probe.Code == 0 && probe.Output.Contains("LUME_FALLBACK_OK"), "Falha de início tenta o próximo executável");
    var directory = await runner.Run(["/d", "/c", "echo", "%CD%"]);
    Check(directory.Output.Contains(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Diretório de execução acompanha o aplicativo");
    try
    {
        await new WinGet(() => [missing]).Run(["--version"]);
        throw new Exception("Falha total deveria ter sido informada");
    }
    catch (InvalidOperationException ex)
    {
        Check(ex.Message.Contains(missing) && ex.Message.Contains("Win32"), "Falha total preserva caminho e código nativo");
    }
}

var manifest = "PackageName: 'Example App'\nPublisher: Example\nPackageVersion: 2.0\nShortDescription: 'A short: summary'\nDescription: |-\n  First line.\n  Second line.\nPackageUrl: https://example.org/app\nLicense: MIT\nIcons:\n- IconUrl: https://example.org/icon.png\n  IconFileType: png\n";
var info = AppPresentation.FromManifest(manifest);
Check(info.Name == "Example App" && info.ShortDescription == "A short: summary", "Manifesto mantém espaços e dois-pontos em textos");
Check(info.Description == "First line.\nSecond line." && info.Homepage == "https://example.org/app", "Descrição multilinha termina no próximo campo");
Check(info.Icon == "https://example.org/icon.png", "Ícone declarado em lista no manifesto");
Check(AppPresentation.YamlValue("Description: >-\n  First line\n  second line\nLicense: MIT", "Description") == "First line second line", "Descrição YAML dobrada");
var cliInfo = AppPresentation.FromCli("Version: 2.0\nPublisher: Example\nDescription:\n  First paragraph.\n\n  Second paragraph.\nHomepage: https://example.org\nLicense: MIT\nInstaller:\n  Installer Url: https://example.org/setup.exe");
Check(cliInfo.Description.Contains("Second paragraph") && !cliInfo.Description.Contains("Homepage"), "Descrição do CLI não engloba os próximos campos");
var ptInfo = AppPresentation.FromCli("Versão: 1.0\nPublicador: Exemplo\nDescrição: Um aplicativo.\nPágina inicial: https://example.org\nLicença: MIT\nURL da licença: https://example.org/license");
Check(ptInfo.Publisher == "Exemplo" && ptInfo.Description == "Um aplicativo." && ptInfo.LicenseUrl.EndsWith("/license"), "Metadados estruturados em português");
var siteInfo = AppPresentation.ParseSite("<link href='/icon.svg' rel='icon'><meta content='/social.png' property='og:image'>", new Uri("https://example.org/app"));
Check(siteInfo.Icon == "https://example.org/icon.svg" && siteInfo.Artwork == "https://example.org/social.png", "Imagens do site com atributos em qualquer ordem");
Check(!AppPresentation.SafeUrl("file:///C:/secret") && !AppPresentation.SafeUrl("https://127.0.0.1/a") && !AppPresentation.SafeUrl("javascript:alert(1)"), "Links de imagens rejeitam caminhos locais e protocolos executáveis");
var store = WinGet.Parse(Table(["Name", "Id", "Version", "Source"],
    ["Spotify Music", "9NCBCSZSJRSB", "Unknown", "msstore"]));
Check(store.Single().Id == "9NCBCSZSJRSB", "Busca aceita IDs e versões desconhecidas da Microsoft Store");
var localized = AppPresentation.FromManifest("PackageLocale: pt-BR\r\nTags:\r\n- multimedia\r\n- player\r\nManifestType: locale");
Check(localized.Locale == "pt-BR" && localized.Tags.Contains("multimedia") && !localized.Tags.Contains("ManifestType"),
    "Manifesto identifica idioma e categorias para apresentação");
