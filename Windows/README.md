# Lume Store para Windows

Loja nativa em C# e WinUI 3. A edição Windows usa o WinGet para encontrar e instalar aplicativos.

## Abrir a loja

1. Confirme que `winget --version` funciona no Terminal. Se necessário, atualize o **Instalador de Aplicativo** pela Microsoft Store.
2. Extraia o ZIP inteiro e abra `Aplicativo\LumeStore.exe`. O aplicativo portátil precisa das DLLs e dos recursos que ficam ao lado do executável.
3. Aceite os contratos da fonte WinGet. Abra **Microsoft Store** na barra lateral para ver a seleção inicial e pesquisar na fonte oficial `msstore`. A disponibilidade dos aplicativos depende da região e da conta.

Os cartões usam o ícone declarado no manifesto do WinGet, quando disponível, ou tentam o ícone do site do publicador. As categorias e tags geram um símbolo local quando não há imagem utilizável. Pacotes sem categoria recebem um símbolo genérico. As telas **Instalados** e **Atualizações** continuam consultando a fonte comunitária do WinGet.

## Tradução automática

A loja dá preferência à descrição oficial em português do Brasil. Para traduzir as demais, configure um recurso Azure Translator. O serviço recebe o texto público da descrição e pode consumir a cota da sua conta. Em **Ver mais**, a descrição original fica disponível abaixo do texto traduzido. Quando a tradução está desativada ou falha, a loja exibe a descrição disponível.

Em um PowerShell aberto na raiz do pacote, informe a chave e, se necessário, a região do seu recurso. A chave permanece no ambiente desse terminal e não é gravada pela loja.

```powershell
$env:LUME_TRANSLATOR_KEY = Read-Host 'Chave do Azure Translator' -MaskInput
$env:LUME_TRANSLATOR_REGION = 'região-do-recurso'
& .\Aplicativo\LumeStore.exe
```

Para um recurso global que dispensa o cabeçalho de região, omita a segunda linha. **Configurações** da loja cobre a aparência e o consentimento do WinGet.

## Compilar

Em Windows x64 com SDK .NET 8 ou superior, na pasta **Codigo**:

```powershell
dotnet publish .\LumeStore\LumeStore.csproj -c Release -p:Platform=x64 --self-contained true -o .\Aplicativo
dotnet run --project .\LumeStore.Tests
```

## Licença proposta para publicação

A escolha preparada para o código autoral é **GPL-3.0-only**. O arquivo LICENSE contém o texto integral. Ao distribuir uma versão modificada, a GPLv3 preserva os avisos e exige que o código-fonte correspondente seja disponibilizado sob a mesma licença. A seção 7 permite tratar atribuição, identificação de versões modificadas e direitos de marca em termos compatíveis com a GPL.

Antes da publicação, confirme a titularidade e os avisos de copyright do código existente. Registre a política de uso do nome Lume Store e da logo separadamente. Imagens e marcas de terceiros continuam sujeitas aos termos dos seus titulares. Dependências e runtimes distribuídos retêm suas próprias licenças.

Esta entrega é para Windows. A interface WinUI e as operações WinGet são específicas do Windows. A edição Linux precisará de uma interface compatível e de um backend para os repositórios Linux, podendo compartilhar a identidade visual e a lógica de tradução.

- [GPLv3 e marcas, guia da GNU](https://www.gnu.org/licenses/quick-guide-gplv3.html)
- [Fontes oficiais do WinGet](https://learn.microsoft.com/windows/package-manager/winget/source)
- [Referência do Azure Translator](https://learn.microsoft.com/azure/ai-services/translator/text-translation/reference/v3/translate)
