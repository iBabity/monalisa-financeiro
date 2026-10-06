param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\installer'),
    [string]$BuildName = '',
    [string]$BuildNumber = '',
    [string]$FileName = 'Monalisa-Financeiro-Setup.exe',
    [string]$LicenseServerUrl = 'https://licencas.monalisasistemas.com.br',
    [switch]$Showcase
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$flutter = 'C:\Tools\flutter\bin\flutter.bat'
$dotnet = 'C:\Tools\dotnet\dotnet.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$buildRoot = Join-Path $projectRoot 'tmp\installer-build'
$publishRoot = Join-Path $buildRoot 'publish'
$sourceRoot = Join-Path $buildRoot 'setup-source'
if ($Showcase -and $FileName -eq 'Monalisa-Financeiro-Setup.exe') {
    $FileName = 'Monalisa-Financeiro-Demonstracao-Setup.exe'
}
$setupPath = Join-Path $OutputDirectory $FileName
$redistCache = Join-Path $projectRoot '.tools\VC_redist.x64.exe'
$fullProjectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
$fullBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\')
if (-not $fullBuildRoot.StartsWith($fullProjectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A PASTA TEMPORÁRIA DO INSTALADOR PRECISA FICAR DENTRO DO PROJETO.'
}

foreach ($required in @($flutter, $dotnet, $csc)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "FERRAMENTA NÃO ENCONTRADA: $required" }
}

if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishRoot, $sourceRoot, $OutputDirectory -Force | Out-Null

Push-Location (Join-Path $projectRoot 'app')
try {
    $flutterArguments = @('build', 'windows', '--release')
    if ($BuildName) { $flutterArguments += @('--build-name', $BuildName) }
    if ($BuildNumber) { $flutterArguments += @('--build-number', $BuildNumber) }
    & $flutter @flutterArguments
    if ($LASTEXITCODE -ne 0) { throw 'A COMPILAÇÃO DO APLICATIVO WINDOWS FALHOU.' }
} finally { Pop-Location }

$windowsBundle = Join-Path $projectRoot 'app\build\windows\x64\runner\Release'
if (-not (Test-Path -LiteralPath (Join-Path $windowsBundle 'MonalisaFinanceiro.exe'))) { throw 'O EXECUTÁVEL WINDOWS NÃO FOI GERADO.' }
Copy-Item -Path (Join-Path $windowsBundle '*') -Destination $publishRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'configure-network.ps1') -Destination (Join-Path $publishRoot 'Configurar-Rede-Financeira.ps1') -Force
if ($Showcase) {
    Set-Content -LiteralPath (Join-Path $publishRoot 'showcase-edition.txt') -Value 'MONALISA FINANCEIRO - EDICAO DE DEMONSTRACAO' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publishRoot 'no-login.txt') -Value 'ACESSO LOCAL AUTOMATICO' -Encoding ascii
}

if (-not (Test-Path -LiteralPath $redistCache)) {
    New-Item -ItemType Directory -Path (Split-Path $redistCache -Parent) -Force | Out-Null
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redistCache
}
$redistSignature = Get-AuthenticodeSignature -LiteralPath $redistCache
if ($redistSignature.Status -ne 'Valid' -or $redistSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'O MICROSOFT VISUAL C++ RUNTIME BAIXADO NÃO POSSUI ASSINATURA VÁLIDA DA MICROSOFT.'
}
$redistPublish = Join-Path $publishRoot 'redist'
New-Item -ItemType Directory -Path $redistPublish -Force | Out-Null
Copy-Item -LiteralPath $redistCache -Destination (Join-Path $redistPublish 'VC_redist.x64.exe') -Force

$serverPublish = Join-Path $publishRoot 'server'
if (Test-Path -LiteralPath $serverPublish) {
    # A pasta de build do Flutter pode conter um servidor antigo de uma execução local.
    # Remova-o antes de publicar para nunca reutilizar banco, licença ou binários anteriores.
    Remove-Item -LiteralPath $serverPublish -Recurse -Force
}
New-Item -ItemType Directory -Path $serverPublish -Force | Out-Null
& $dotnet publish (Join-Path $projectRoot 'api\Monalisa.Financeiro.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $serverPublish
if ($LASTEXITCODE -ne 0) { throw 'A COMPILAÇÃO DO SERVIDOR LOCAL FALHOU.' }

# Leitor portátil usado somente para recuperar backups PostgreSQL do Gestores.
$postgresBin = 'C:\Program Files\PostgreSQL\12\bin'
$postgresTools = Join-Path $serverPublish 'tools\postgresql'
$postgresFiles = @(
    'pg_restore.exe', 'libpq.dll', 'libintl-9.dll', 'libiconv-2.dll', 'zlib1.dll',
    'libssl-1_1-x64.dll', 'libcrypto-1_1-x64.dll', 'libwinpthread-1.dll',
    'libxml2.dll', 'libxslt.dll'
)
foreach ($file in $postgresFiles) {
    $source = Join-Path $postgresBin $file
    if (-not (Test-Path -LiteralPath $source)) { throw "COMPONENTE DO POSTGRESQL NÃO ENCONTRADO: $source" }
}
New-Item -ItemType Directory -Path $postgresTools -Force | Out-Null
foreach ($file in $postgresFiles) { Copy-Item -LiteralPath (Join-Path $postgresBin $file) -Destination $postgresTools -Force }

Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Name -match '\.db($|-)' -or $_.Name -match '\.license\.json$'
} | ForEach-Object { throw "O PACOTE TENTOU INCLUIR DADOS LOCAIS: $($_.FullName)" }

$payloadZip = Join-Path $sourceRoot 'payload.zip'
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $payloadZip -CompressionLevel Optimal
$payloadHash = (Get-FileHash -LiteralPath $payloadZip -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $sourceRoot 'payload.sha256') -Value $payloadHash -Encoding ascii -NoNewline
$installScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Raw
$escapedServerUrl = $LicenseServerUrl.Replace("'", "''")
$installScript = [Regex]::Replace($installScript, "\[string\]\`$LicenseServerUrl = '[^']*'", "[string]`$LicenseServerUrl = '$escapedServerUrl'", 1)
[IO.File]::WriteAllText((Join-Path $sourceRoot 'install.ps1'), $installScript, [Text.UTF8Encoding]::new($true))
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }

$versionName = $BuildName
if (-not $versionName) {
    $pubspec = Get-Content -LiteralPath (Join-Path $projectRoot 'app\pubspec.yaml') -Raw
    $versionMatch = [Regex]::Match($pubspec, '(?m)^version:\s*(\d+\.\d+\.\d+)')
    if (-not $versionMatch.Success) { throw 'NÃO FOI POSSÍVEL IDENTIFICAR A VERSÃO DO APLICATIVO.' }
    $versionName = $versionMatch.Groups[1].Value
}
if ($versionName -notmatch '^\d+\.\d+\.\d+$') { throw "VERSÃO INVÁLIDA PARA O INSTALADOR: $versionName" }
$assemblyVersion = "$versionName.0"
$productName = if ($Showcase) { 'Monalisa Financeiro Demonstração' } else { 'Monalisa Financeiro' }
$assemblyInfo = @"
using System.Reflection;
[assembly: AssemblyTitle("$productName")]
[assembly: AssemblyDescription("Instalador do $productName")]
[assembly: AssemblyCompany("Monalisa Sistemas")]
[assembly: AssemblyProduct("$productName")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
"@
$assemblyInfoPath = Join-Path $sourceRoot 'setup-assembly-info.cs'
[IO.File]::WriteAllText($assemblyInfoPath, $assemblyInfo, [Text.UTF8Encoding]::new($true))

& $csc /nologo /target:winexe /platform:anycpu "/out:$setupPath" "/win32icon:$projectRoot\app\windows\runner\resources\app_icon.ico" "/win32manifest:$PSScriptRoot\installer.manifest" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$payloadZip,payload.zip" "/resource:$sourceRoot\payload.sha256,payload.sha256" "/resource:$sourceRoot\install.ps1,install.ps1" (Join-Path $PSScriptRoot 'bootstrapper.cs') $assemblyInfoPath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath)) { throw 'NÃO FOI POSSÍVEL GERAR O INSTALADOR.' }

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$hashName = [IO.Path]::GetFileNameWithoutExtension($FileName) + '.sha256.txt'
Set-Content -LiteralPath (Join-Path $OutputDirectory $hashName) -Value "$hash  $FileName" -Encoding ascii
Write-Host "INSTALADOR GERADO: $setupPath"
Write-Host "SHA256: $hash"
