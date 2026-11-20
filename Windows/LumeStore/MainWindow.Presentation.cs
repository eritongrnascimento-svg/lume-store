using LumeStore.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LumeStore;

public sealed partial class MainWindow
{
    private readonly AppPresentation presentation = new();
    private readonly DescriptionTranslator translator = new();
    private CancellationTokenSource? detailQuery;
    private readonly CancellationTokenSource lifetime = new();
    private void HideDetails()
    {
        detailQuery?.Cancel();
        DetailsPage.Visibility = Visibility.Collapsed;
        HomeScroll.Visibility = Visibility.Visible;
    }
    private async Task LoadPicture(Border target, string url, bool artwork, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var file = await presentation.ImageFile(url, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(file)) return;
            var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            ImageSource source = file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? new SvgImageSource(new Uri(file)) : new BitmapImage(new Uri(file)) { DecodePixelWidth = artwork ? 1000 : 128 };
            var previous = target.Child;
            target.Child = null;
            var layers = new Grid();
            if (previous is not null) layers.Children.Add(previous);
            image.Opacity = 0;
            layers.Children.Add(image);
            target.Child = layers;
            void Reveal() { if (token.IsCancellationRequested) return; image.Opacity = 1; if (previous is not null) previous.Visibility = Visibility.Collapsed; target.Visibility = Visibility.Visible; }
            image.ImageOpened += (_, _) => Reveal();
            if (source is SvgImageSource svg) svg.Opened += (_, _) => Reveal();
            image.Source = source;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { }
    }
    private async Task FillIcon(Border target, AppInfo info, CancellationToken token)
    {
        var icon = info.Icon;
        if (string.IsNullOrEmpty(icon) && !string.IsNullOrEmpty(info.Homepage)) icon = (await presentation.Site(info.Homepage, token)).Icon;
        await LoadPicture(target, icon, false, token);
    }
    private Border RichCard(Package package)
    {
        var installed = page == "Instalados";
        var update = page == "Atualizações";
        var cardQuery = CancellationTokenSource.CreateLinkedTokenSource(query?.Token ?? lifetime.Token);
        var token = cardQuery.Token;
        var stack = new StackPanel { Spacing = 10 };
        var icon = Badge(package); icon.HorizontalAlignment = HorizontalAlignment.Left;
        var header = new Grid(); header.Children.Add(icon);
        var version = Text(package.Source == "msstore" ? "Microsoft Store" : package.Version, 11); version.HorizontalAlignment = HorizontalAlignment.Right; version.Foreground = Brush(143, 164, 190); header.Children.Add(version);
        stack.Children.Add(header);
        var name = Text(package.Name, 15, true); name.MaxLines = 1; name.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(name);
        var description = Text("Carregando descrição…", 12); description.Height = 34; description.MaxLines = 2; description.TextTrimming = TextTrimming.CharacterEllipsis; description.Foreground = Brush(160, 179, 204); stack.Children.Add(description);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Button(installed ? "Desinstalar" : update ? "Atualizar" : "Instalar", async (_, _) => await ConfirmOperation(package, installed ? "uninstall" : update ? "upgrade" : "install"), !installed));
        buttons.Children.Add(Button("Ver mais", async (_, _) => await Details(package)));
        stack.Children.Add(buttons);
        var card = new Border { Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = Brush(75, 100, 137, 65), Background = Brush(43, 43, 80, 185), Child = stack };
        var started = false;
        async Task Fetch()
        {
            if (started || !agreed || token.IsCancellationRequested) return;
            started = true;
            try
            {
                var info = package.Source == "msstore"
                    ? AppPresentation.FromCli(await winget.Details(package.Id, token, package.Source))
                    : await presentation.Get(package, token);
                token.ThrowIfCancellationRequested();
                description.Text = !string.IsNullOrWhiteSpace(info.ShortDescription) ? info.ShortDescription : !string.IsNullOrWhiteSpace(info.Description) ? info.Description : "Descrição indisponível. Abra os detalhes para consultar o WinGet.";
                if (info.Tags.Length > 0) ApplyBadge(icon, package, info.Tags);
                var original = info.ShortDescription.Length > 0 ? info.ShortDescription : info.Description;
                await FillIcon(icon, info, token);
                var translated = await translator.Translate(original, info.Locale, token);
                token.ThrowIfCancellationRequested();
                if (translated != original)
                {
                    description.Text = translated;
                    ToolTipService.SetToolTip(description, "Tradução automática para português");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!token.IsCancellationRequested) description.Text = "Não foi possível carregar os dados. Tente abrir os detalhes."; }
        }
        card.Unloaded += (_, _) => { cardQuery.Cancel(); cardQuery.Dispose(); };
        card.EffectiveViewportChanged += async (_, args) => { if (args.EffectiveViewport.Width > 0 && args.EffectiveViewport.Height > 0) await Fetch(); };
        card.PointerEntered += (_, _) => card.Background = Brush(62, 56, 108, 230);
        card.PointerExited += (_, _) => card.Background = Brush(43, 43, 80, 185);
        card.GotFocus += async (_, _) => await Fetch();
        return card;
    }
    private async Task Details(Package package)
    {
        if (!agreed) { await Consent(); if (!agreed) return; }
        detailQuery?.Cancel();
        var current = detailQuery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = current.Token;
        var wasInstalled = page == "Instalados";
        var wasUpdate = page == "Atualizações";
        HomeScroll.Visibility = Visibility.Collapsed;
        DetailsPage.Visibility = Visibility.Visible;
        DetailsContent.Children.Clear();
        DetailsContent.Children.Add(Button("← Voltar", (_, _) => HideDetails()));
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        var icon = Badge(package, 76); header.Children.Add(icon);
        var title = new StackPanel { Spacing = 6 }; title.Children.Add(Text(package.Name, 28, true)); title.Children.Add(Text(package.Id, 12)); header.Children.Add(title); DetailsContent.Children.Add(header);
        var loading = new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left }; DetailsContent.Children.Add(loading);
        try
        {
            var info = await presentation.Get(package, token);
            string raw = ""; string warning = "";
            try { raw = await winget.Details(package.Id, token, package.Source); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { warning = "O WinGet não respondeu. Exibindo os metadados disponíveis do repositório."; }
            token.ThrowIfCancellationRequested();
            var cli = AppPresentation.FromCli(raw);
            string Pick(string primary, string fallback) => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
            var descriptionLocale = info.Description.Length > 0 || (cli.Description.Length == 0 && info.ShortDescription.Length > 0)
                ? info.Locale : cli.Locale;
            info = info with { Description = Pick(info.Description, cli.Description), ShortDescription = Pick(info.ShortDescription, cli.ShortDescription), Publisher = Pick(info.Publisher, cli.Publisher), Version = Pick(cli.Version, info.Version), Homepage = Pick(info.Homepage, cli.Homepage), License = Pick(info.License, cli.License), LicenseUrl = Pick(info.LicenseUrl, cli.LicenseUrl) };
            loading.IsActive = false; loading.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(warning)) DetailsContent.Children.Add(Text(warning, 12));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            actions.Children.Add(Button(wasInstalled ? "Desinstalar" : wasUpdate ? "Atualizar" : "Instalar", async (_, _) => await ConfirmOperation(package, wasInstalled ? "uninstall" : wasUpdate ? "upgrade" : "install"), !wasInstalled));
            void Link(string label, string url)
            {
                if (!AppPresentation.SafeUrl(url)) return;
                actions.Children.Add(new HyperlinkButton { Content = label, NavigateUri = new Uri(url) });
            }
            Link("Site oficial ↗", info.Homepage); Link("Licença ↗", info.LicenseUrl); DetailsContent.Children.Add(actions);
            DetailsContent.Children.Add(Text("Sobre este aplicativo", 20, true));
            var about = Pick(info.Description, info.ShortDescription);
            var aboutText = Text(string.IsNullOrWhiteSpace(about) ? "O publicador não disponibilizou uma descrição nesta consulta." : about, 14); aboutText.IsTextSelectionEnabled = true; DetailsContent.Children.Add(aboutText);
            var facts = new StackPanel { Spacing = 10 };
            foreach (var (label, value) in new[] { ("Publicador", info.Publisher), ("Versão", info.Version), ("Licença", info.License), ("Origem", package.Source == "msstore" ? StorePage : "WinGet · repositório da comunidade") })
                if (!string.IsNullOrWhiteSpace(value)) facts.Children.Add(Text(label + ": " + value, 13));
            DetailsContent.Children.Add(new Border { Padding = new Thickness(20), CornerRadius = new CornerRadius(12), Background = Brush(43, 43, 80, 185), Child = facts });
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var technical = Text(raw, 11); technical.IsTextSelectionEnabled = true;
                DetailsContent.Children.Add(new Expander { Header = "Metadados técnicos do WinGet", Content = technical, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
            var artwork = new Border { Height = 280, CornerRadius = new CornerRadius(14), Background = Brush(23, 36, 57), Visibility = Visibility.Collapsed };
            var caption = Text("Imagem de divulgação do site oficial", 11); caption.Visibility = Visibility.Collapsed;
            DetailsContent.Children.Add(artwork); DetailsContent.Children.Add(caption);
            if (info.Tags.Length > 0) ApplyBadge(icon, package, info.Tags);
            var translated = await translator.Translate(about, descriptionLocale, token);
            token.ThrowIfCancellationRequested();
            if (translated != about)
            {
                aboutText.Text = translated;
                var original = Text(about, 13);
                original.IsTextSelectionEnabled = true;
                DetailsContent.Children.Insert(DetailsContent.Children.IndexOf(aboutText) + 1,
                    new Expander { Header = "Tradução automática · ver texto original", Content = original });
            }
            var site = await presentation.Site(info.Homepage, token);
            token.ThrowIfCancellationRequested();
            await LoadPicture(icon, Pick(info.Icon, site.Icon), false, token);
            if (!string.IsNullOrEmpty(site.Artwork))
            {
                await LoadPicture(artwork, site.Artwork, true, token);
                caption.Visibility = Visibility.Visible;
            }
            Status.Text = "Detalhes do aplicativo";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) DetailsContent.Children.Add(Text("Não foi possível carregar os detalhes. " + ex.Message, 13)); }
        finally { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
    }
}
