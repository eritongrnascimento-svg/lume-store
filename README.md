# Lume Store

Loja de aplicativos para **Windows** e **Linux**. Código-fonte sob a licença [GPL-3.0-only](LICENSE).

## Instalação

Baixe o arquivo do seu sistema na página de [Releases](https://github.com/eritongrnascimento-svg/lume-store/releases).

- **Windows x64.** Execute `Lume-Store-Windows-x64.exe`. O inicializador extrai o aplicativo para `%LOCALAPPDATA%\Programs\Lume Store` e abre a loja. O catálogo requer o **Instalador de Aplicativo**, que fornece o WinGet. A edição inclui Microsoft Store, ícones do catálogo e tradução opcional via Azure Translator.
- **Debian, Ubuntu e derivados com APT.** Instale com `sudo apt install ./lume-store_0.2.0_all.deb`. Abra **Lume Store** pelo menu de aplicativos. A opção **APT** consulta os índices da distribuição já configurados. A opção **Flatpak** consulta os remotos configurados para o usuário e o sistema. As instalações APT usam PackageKit e a autorização da sessão quando exigida. A edição Linux oferece busca e instalação.

## Compilar

No Windows x64, no PowerShell com .NET SDK e os componentes WinUI disponíveis:

```powershell
./Windows/build.ps1
dotnet run --project ./Windows/LumeStore.Tests
```

Para produzir o pacote Debian, execute `python3 Linux/build-deb.py`. Os dois scripts salvam os artefatos em `dist/`.

As imagens e marcas dos aplicativos de terceiros pertencem aos respectivos titulares. Os runtimes e dependências seguem suas próprias licenças.
