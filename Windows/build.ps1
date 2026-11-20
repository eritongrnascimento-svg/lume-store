param([string]$Output = 'dist')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$outputDir = [IO.Path]::GetFullPath($Output)
$stage = Join-Path ([IO.Path]::GetTempPath()) ('LumeBuild-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage,$outputDir -Force | Out-Null
    $payload = Join-Path $stage 'payload'
    dotnet publish (Join-Path $PSScriptRoot 'LumeStore/LumeStore.csproj') -c Release -p:Platform=x64 --self-contained true -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o app' }
    $zip = Join-Path $stage 'payload.zip'
    tar.exe -a -cf $zip -C $payload -- @(Get-ChildItem -LiteralPath $payload -Name -Force)
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compactar o app' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $icon = Join-Path $PSScriptRoot 'LumeStore/Assets/Lume.ico'
    $launcher = Join-Path $PSScriptRoot 'Launcher.cs'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/resource:$zip,LumePayload" "/out:$outputDir/Lume-Store-Windows-x64.exe" /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $launcher
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o inicializador' }
} finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = (Resolve-Path -LiteralPath $stage).Path
        if ((Split-Path -Parent $resolved) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and (Split-Path -Leaf $resolved) -match '^LumeBuild-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse
        }
    }
}
