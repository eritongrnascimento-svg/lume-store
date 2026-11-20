using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using LumeStore.Services;
using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics;

namespace LumeStore;

public sealed partial class MainWindow : Window
{
    private readonly WinGet winget = new();
    private readonly DispatcherTimer slideshow = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly SemaphoreSlim operations = new(1);
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Transfer> history = [];
    private List<Package> packages = [];
    private CancellationTokenSource? query;
    private string page = "Descobrir";
    private int shown;
    private bool agreed;
    private bool storeAgreed;
    private string searchSource = "winget";
    private const string StorePage = "Microsoft Store";
    private bool closing;
    private bool motionEnabled = true;
    private readonly string settingsPath = Path.Combine(WinGet.DataDirectory, "settings.json");
    private sealed record Nav(string Label, string Glyph, string? Tag = null);
    private sealed class Transfer(Package package, string action)
    {
        public Package Package = package;
        public string Action = action;
        public string State = "Na fila";
        public string Log = "";
        public bool Active = true;
    }
    private readonly Nav[] navigation = [new("Descobrir", "\uE80F"), new("Todos os apps", "\uE71D"), new(StorePage, "\uE719"), new("Produtividade", "\uE9CE", "productivity"), new("Desenvolvimento", "\uE943", "developer"), new("Navegadores", "\uE774", "browser"), new("Multimídia", "\uE714", "multimedia"), new("Comunicação", "\uE8F2", "chat"), new("Jogos", "\uE7FC", "game"), new("Utilitários", "\uE90F", "utility"), new("Instalados", "\uE73E"), new("Atualizações", "\uE895"), new("Downloads", "\uE896")];
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lume.ico"));
        AppWindow.Resize(new SizeInt32(1440, 960));
        foreach (var entry in navigation)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13, Margin = new Thickness(8, 10, 0, 10) };
            row.Children.Add(new FontIcon { Glyph = entry.Glyph, FontSize = 17 });
            row.Children.Add(new TextBlock { Text = entry.Label, FontSize = 13 });
            Navigation.Items.Add(new ListViewItem { Content = row, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        try { agreed = File.Exists(settingsPath) && JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(settingsPath))?.GetValueOrDefault("agreed") == true; } catch { }
        Root.Loaded += async (_, _) => { await Consent(); Navigation.SelectedIndex = 0; };
        slideshow.Tick += (_, _) => { if (Featured.Items.Count > 1) Featured.SelectedIndex = (Featured.SelectedIndex + 1) % Featured.Items.Count; };
        Featured.PointerEntered += (_, _) => slideshow.Stop();
        Featured.PointerExited += (_, _) => { if (motionEnabled) slideshow.Start(); };
        Root.SizeChanged += (_, _) => Search.Width = Math.Max(120, Math.Min(480, Root.ActualWidth - 380));
        Closed += (_, _) => { closing = true; query?.Cancel(); lifetime.Cancel(); detailQuery?.Cancel(); slideshow.Stop(); };
        AppWindow.Closing += async (sender, args) =>
        {
            if (!history.Any(t => t.Active)) return;
            args.Cancel = true;
            await Dialog("Operações em andamento", "Aguarde a conclusão das instalações ou desinstalações antes de fechar a loja.");
        };
        BuildFeatured();
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(ColorHelper.FromArgb(a, r, g, b));
    private static TextBlock Text(string text, double size = 14, bool strong = false) => new() { Text = text, FontSize = size, FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label, RoutedEventHandler action, bool accent = false)
    {
        var button = new Button { Content = label };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += action;
        return button;
    }
    private void Inform(string text, bool error = false)
    {
        Notice.Title = error ? "Não foi possível concluir" : "Lume Store";
        Notice.Message = text;
        Notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        Notice.IsOpen = true;
    }
    private async Task Consent()
    {
        if (agreed) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Conectar ao repositório WinGet", Content = "Para pesquisar, a loja consultará a fonte winget e aceitará seus contratos de origem. Cada instalação terá uma confirmação separada, incluindo os termos do pacote. Continuar?", PrimaryButtonText = "Conectar", CloseButtonText = "Agora não", DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            agreed = true;
            try { Directory.CreateDirectory(WinGet.DataDirectory); File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { agreed })); } catch (Exception ex) { Inform("Não foi possível salvar a preferência: " + ex.Message); }
        }
    }
    private async void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListViewItem { Tag: Nav nav }) return;
        page = nav.Label;
        Search.Text = "";
        Search.PlaceholderText = nav.Label == StorePage ? "Pesquisar na Microsoft Store" : "Pesquisar aplicativos no WinGet";
        await Load(nav.Tag);
    }
    private async void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.QueryText)) return;
        searchSource = page == StorePage || (page == "Pesquisa" && searchSource == "msstore") ? "msstore" : "winget";
        page = "Pesquisa";
        await Load(null, args.QueryText.Trim());
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await Load(navigation.FirstOrDefault(n => n.Label == page)?.Tag, page == "Pesquisa" ? Search.Text.Trim() : "");
    private async Task Load(string? tag = null, string search = "")
    {
        var isStore = page == StorePage || (page == "Pesquisa" && searchSource == "msstore");
        var source = isStore ? "msstore" : "winget";
        HideDetails();
        query?.Cancel();
        var current = query = new CancellationTokenSource();
        Busy.IsActive = false;
        Notice.IsOpen = false;
        Heading.Text = page == "Descobrir" ? "Descubra seu próximo favorito." : page == "Pesquisa" ? "Resultados da pesquisa" : page;
        Subheading.Text = page switch { "Instalados" => "Gerencie os aplicativos reconhecidos pelo WinGet neste computador.", "Atualizações" => "Novas versões para manter tudo em dia.", "Downloads" => "Acompanhe a fila, as instalações e o histórico desta sessão.", StorePage => "Seleção de aplicativos da Microsoft Store. Use a busca para explorar o catálogo.", "Pesquisa" => $"“{search}” · {(isStore ? StorePage : "WinGet")}", _ => "Ferramentas incríveis para fazer mais com o seu Windows." };
        FeaturedSection.Visibility = page == "Descobrir" ? Visibility.Visible : Visibility.Collapsed;
        Cards.Items.Clear(); packages = []; shown = 0;
        Transfers.Visibility = page == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        GridHeading.Visibility = page == "Downloads" ? Visibility.Collapsed : Visibility.Visible;
        Previous.Visibility = More.Visibility = EmptyLabel.Visibility = Visibility.Collapsed;
        CountLabel.Text = "";
        if (page == "Downloads") { RenderTransfers(); return; }
        if (!agreed) { EmptyLabel.Text = "Conecte ao WinGet em Configurações para carregar os aplicativos."; EmptyLabel.Visibility = Visibility.Visible; return; }
        Busy.IsActive = true;
        Status.Text = "Consultando o WinGet…";
        try
        {
            if (isStore && !storeAgreed)
            {
                var consent = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = "Conectar à Microsoft Store",
                    Content = "O WinGet pode enviar a região configurada no Windows à Microsoft e aceitar os contratos da fonte Microsoft Store.",
                    PrimaryButtonText = "Conectar", CloseButtonText = "Cancelar"
                };
                storeAgreed = await consent.ShowAsync() == ContentDialogResult.Primary;
                if (current.IsCancellationRequested) return;
                if (!storeAgreed)
                {
                    EmptyLabel.Text = "Atualize esta página para conectar à Microsoft Store.";
                    EmptyLabel.Visibility = Visibility.Visible;
                    return;
                }
            }
            var list = isStore && search.Length == 0 ? StoreHighlights() : page is "Instalados" or "Atualizações" ? await winget.Installed(page == "Atualizações", current.Token) : await winget.Catalog(search, tag, current.Token, source);
            if (current.IsCancellationRequested) return;
            packages = list;
            SectionTitle.Text = page == "Descobrir" ? "Explore os aplicativos" : "Aplicativos";
            CountLabel.Text = $"{list.Count:N0} aplicativos";
            EmptyLabel.Text = page == "Atualizações" ? "Tudo em dia. Nenhuma atualização encontrada pelo WinGet." : "Nenhum aplicativo encontrado. Tente outro nome ou categoria.";
            EmptyLabel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddPage();
            Status.Text = $"Fonte: {(isStore ? StorePage : "WinGet")} · atualizado às " + DateTime.Now.ToString("HH:mm");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!current.IsCancellationRequested) { Inform(ex.Message, true); Status.Text = "WinGet indisponível · tente novamente"; EmptyLabel.Text = "Não foi possível carregar os aplicativos. Verifique o Instalador de Aplicativo e sua conexão."; EmptyLabel.Visibility = Visibility.Visible; } }
        finally { if (query == current) Busy.IsActive = false; }
    }
    private static List<Package> StoreHighlights() =>
    [
        new("Spotify", "9NCBCSZSJRSB", "", Source: "msstore", Category: "multimedia"),
        new("WhatsApp", "9NKSQGP7F2NH", "", Source: "msstore", Category: "chat"),
        new("Netflix", "9WZDNCRFJ3TJ", "", Source: "msstore", Category: "multimedia"),
        new("Microsoft To Do", "9NBLGGH5R558", "", Source: "msstore", Category: "productivity"),
        new("Disney+", "9NXQXXLFST89", "", Source: "msstore", Category: "multimedia"),
        new("Telegram Desktop", "9NZTWSQNTD0S", "", Source: "msstore", Category: "chat")
    ];
    private void AddPage()
    {
        Cards.Items.Clear();
        var start = shown;
        foreach (var package in packages.Skip(start).Take(24)) Cards.Items.Add(RichCard(package));
        shown = Math.Min(start + 24, packages.Count);
        Previous.Visibility = start > 0 ? Visibility.Visible : Visibility.Collapsed;
        More.Visibility = shown < packages.Count ? Visibility.Visible : Visibility.Collapsed;
        CountLabel.Text = packages.Count == 0 ? "0 aplicativos" : $"{start + 1}–{shown} de {packages.Count:N0} aplicativos";
        HomeScroll.ChangeView(null, 0, null, true);
    }
    private void MoreClicked(object sender, RoutedEventArgs e) => AddPage();
    private void PreviousClicked(object sender, RoutedEventArgs e) { shown = Math.Max(0, shown - ((shown - 1) % 24 + 1) - 24); AddPage(); }
    private Border Badge(Package p, double size = 48)
    {
        var badge = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4) };
        ApplyBadge(badge, p);
        return badge;
    }
    private void ApplyBadge(Border badge, Package p, string tags = "")
    {
        var (glyph, color) = (p.Category + " " + tags).ToLowerInvariant() switch
        {
            var category when category.Contains("developer") || category.Contains("development") => ("\uE943", Brush(78, 58, 143)),
            var category when category.Contains("browser") => ("\uE774", Brush(27, 92, 144)),
            var category when category.Contains("chat") || category.Contains("communication") => ("\uE8F2", Brush(33, 103, 101)),
            var category when category.Contains("multimedia") || category.Contains("audio") || category.Contains("video") => ("\uE714", Brush(135, 52, 99)),
            var category when category.Contains("game") => ("\uE7FC", Brush(88, 66, 149)),
            var category when category.Contains("productivity") => ("\uE9CE", Brush(37, 100, 118)),
            var category when category.Contains("utility") => ("\uE90F", Brush(121, 73, 35)),
            _ => ("\uE74C", Brush(54, 73, 109))
        };
        badge.Background = color;
        badge.Child = new FontIcon { Glyph = glyph, FontSize = badge.Width * 0.42, Foreground = Brush(255, 255, 255) };
    }
    private void BuildFeatured()
    {
        var features = new[] {
            (new Package("Visual Studio Code", "Microsoft.VisualStudioCode", "", Category: "developer"), "CRIE ALGO EXTRAORDINÁRIO", "Suas ideias.\nSeu próximo projeto.", "Um espaço para código, criatividade e novas possibilidades.", "\uE943", Brush(45, 49, 116)),
            (new Package("PowerToys", "Microsoft.PowerToys", "", Category: "utility"), "SEU WINDOWS, TURBINADO", "Pequenas ferramentas.\nGrandes possibilidades.", "Descubra novos jeitos de trabalhar com seu computador.", "\uE945", Brush(74, 43, 113)),
            (new Package("VLC media player", "VideoLAN.VLC", "", Category: "multimedia"), "DÊ PLAY NO SEU DIA", "Todos os seus momentos.\nEm um só player.", "Explore o clássico player de mídia de código aberto.", "\uE714", Brush(21, 83, 91)) };
        foreach (var (p, label, title, description, glyph, color) in features)
        {
            var grid = new Grid { Padding = new Thickness(30, 24, 30, 20) };
            var decoration = new FontIcon { Glyph = glyph, FontSize = 110, Opacity = 0.17, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 35, 0) }; grid.Children.Add(decoration);
            var featureLogo = Badge(p, 104); featureLogo.HorizontalAlignment = HorizontalAlignment.Right; featureLogo.Margin = new Thickness(0, 0, 45, 0); featureLogo.Opacity = 0.9; grid.Children.Add(featureLogo);
            var featureLoaded = false;
            featureLogo.EffectiveViewportChanged += async (_, args) =>
            {
                if (featureLoaded || args.EffectiveViewport.Width <= 0 || !agreed) return;
                featureLoaded = true;
                try { var info = await presentation.Get(p, lifetime.Token); await FillIcon(featureLogo, info, lifetime.Token); } catch (OperationCanceledException) { } catch (Exception) { }
            };
            var stack = new StackPanel { Spacing = 9, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
            var eyebrow = Text(label, 10, true); eyebrow.CharacterSpacing = 180; eyebrow.Foreground = Brush(162, 200, 242); stack.Children.Add(eyebrow);
            var heading = Text(title, 28, true); stack.Children.Add(heading);
            stack.Children.Add(Text(description, 12));
            stack.Children.Add(Button("Conhecer " + p.Name + "  ↗", async (_, _) => await Details(p), true)); grid.Children.Add(stack);
            Featured.Items.Add(new Border { Background = color, CornerRadius = new CornerRadius(18), Child = grid });
        }
        Featured.SelectedIndex = 0;
        slideshow.Start();
    }
    private void FeatureChanged(object sender, SelectionChangedEventArgs e) { if (SlideLabel != null) SlideLabel.Text = $"{Featured.SelectedIndex + 1:00} / 03"; }
    private async Task Dialog(string title, string message) => await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new ScrollViewer { MaxHeight = 430, Content = Text(message, 13) }, CloseButtonText = "Fechar", RequestedTheme = ElementTheme.Dark }.ShowAsync();
    private async Task ConfirmOperation(Package p, string action)
    {
        if (!agreed) { await Consent(); if (!agreed) return; }
        if (pending.Contains(p.Id)) { Inform("Este aplicativo já está na fila."); return; }
        var verb = action == "uninstall" ? "Desinstalar" : action == "upgrade" ? "Atualizar" : "Instalar";
        var stack = new StackPanel { Spacing = 14 };
        var metadataBox = Text("", 11);
        stack.Children.Add(Text(p.Id + "\nOrigem: " + (p.Source == "msstore" ? StorePage : "WinGet"), 13));
        var cleanup = new CheckBox { Content = "Remover resíduos de pacotes portáteis (--purge)", IsChecked = false };
        if (action == "uninstall")
        {
            stack.Children.Add(Text("A desinstalação pode remover dados do aplicativo. Para pacotes portáteis, você pode também remover o diretório gerenciado pelo WinGet. Pastas pessoais e resíduos de outros instaladores não são apagados automaticamente.", 13));
            stack.Children.Add(cleanup);
        }
        else
        {
            stack.Children.Add(Text("Consulte os metadados e a licença antes de continuar. Ao confirmar, você aceita os contratos do pacote. O instalador pode solicitar permissão do Windows.", 13));
            stack.Children.Add(Button("Ver metadados e licença", async (_, _) => { try { var raw = await winget.Details(p.Id, source: p.Source); metadataBox.Text = raw; } catch (Exception ex) { metadataBox.Text = ex.Message; } }));
        }
        stack.Children.Add(new ScrollViewer { MaxHeight = 180, Content = metadataBox });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = verb + " " + p.Name + "?", Content = stack, PrimaryButtonText = verb, CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { metadataBox.Text = ""; return; }
        metadataBox.Text = "";
        var purge = cleanup.IsChecked == true;
        if (!pending.Add(p.Id)) return;
        var transfer = new Transfer(p, verb); history.Insert(0, transfer); RenderTransfers();
        _ = Execute(transfer, action, purge);
    }
    private async Task Execute(Transfer transfer, string action, bool purge)
    {
        await operations.WaitAsync();
        try
        {
            transfer.State = "Em andamento"; RenderTransfers();
            var args = new List<string> { action, "--id", transfer.Package.Id, "--exact", "--source", transfer.Package.Source, "--accept-source-agreements" };
            if (action != "uninstall") args.Add("--accept-package-agreements");
            if (purge) args.Add("--purge");
            var result = await winget.Run(args, line => DispatcherQueue.TryEnqueue(() => { transfer.Log += line + "\n"; Status.Text = transfer.Action + ": " + transfer.Package.Name; }));
            transfer.Log = result.Output;
            transfer.State = result.Code == 0 ? "Concluído" : $"Falhou · código 0x{result.Code:X8}";
            Directory.CreateDirectory(WinGet.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(WinGet.DataDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log"), transfer.Package.Id + "\n" + transfer.State + "\n" + result.Output);
        }
        catch (Exception ex) { transfer.State = "Falhou"; transfer.Log += "\n" + ex.Message; }
        finally
        {
            transfer.Active = false; pending.Remove(transfer.Package.Id); operations.Release();
            if (!closing) { RenderTransfers(); Inform(transfer.Package.Name + " · " + transfer.State, transfer.State.StartsWith("Falhou")); }
        }
    }
    private void RenderTransfers()
    {
        var item = Navigation.Items.OfType<ListViewItem>().LastOrDefault();
        if (item?.Content is StackPanel row && row.Children[1] is TextBlock text) text.Text = pending.Count > 0 ? $"Downloads ({pending.Count})" : "Downloads";
        if (page != "Downloads") return;
        Transfers.Children.Clear();
        EmptyLabel.Text = "Nenhum download nesta sessão. Explore o catálogo para instalar um aplicativo.";
        EmptyLabel.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var transfer in history)
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(Text(transfer.Package.Name, 17, true)); stack.Children.Add(Text(transfer.Action + " · " + transfer.State, 12));
            if (transfer.Active) stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
            stack.Children.Add(Button("Ver registro", async (_, _) => await Dialog(transfer.Package.Name, string.IsNullOrEmpty(transfer.Log) ? "Aguardando saída do WinGet. O progresso é indeterminado até a conclusão da operação." : transfer.Log)));
            Transfers.Children.Add(new Border { Child = stack, Padding = new Thickness(20), CornerRadius = new CornerRadius(12), Background = Brush(35, 52, 77, 170) });
        }
    }
    private async void SettingsClicked(object sender, RoutedEventArgs e)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Text("Aparência", 18, true));
        stack.Children.Add(Text("Tema azul escuro · Mica nativo · destaque do Windows. A disponibilidade do Mica depende do Windows e das opções de transparência.", 13));
        var motion = new ToggleSwitch { Header = "Destaques em movimento", IsOn = motionEnabled };
        motion.Toggled += (_, _) => { motionEnabled = motion.IsOn; if (motionEnabled) slideshow.Start(); else slideshow.Stop(); };
        stack.Children.Add(motion);
        stack.Children.Add(Text("Fonte de aplicativos", 18, true));
        stack.Children.Add(Text("winget · Repositório da comunidade Microsoft. As categorias usam as tags dos manifestos; nem todo pacote possui tags ou ícones. Ícones e descrições são carregados sob demanda do repositório e dos sites oficiais. Alguns pacotes não fornecem imagens.", 13));
        stack.Children.Add(Text("Limpeza: --purge remove o diretório de pacotes portáteis. Não existe remoção genérica garantida de resíduos para todos os aplicativos.", 12));
        var reconnect = new CheckBox { Content = "Aceito os contratos da fonte winget", IsChecked = agreed };
        stack.Children.Add(reconnect);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Configurações", Content = stack, PrimaryButtonText = "Salvar", CloseButtonText = "Fechar", RequestedTheme = ElementTheme.Dark };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            agreed = reconnect.IsChecked == true;
            try { Directory.CreateDirectory(WinGet.DataDirectory); File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { agreed })); } catch (Exception ex) { Inform(ex.Message, true); }
            await Load(navigation.FirstOrDefault(n => n.Label == page)?.Tag);
        }
    }
}
