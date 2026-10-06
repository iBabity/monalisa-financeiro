$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot

function Resolve-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Fallback
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if (Test-Path -LiteralPath $Fallback) {
        return $Fallback
    }

    throw "Ferramenta obrigatoria nao encontrada: $Name"
}

$dotnet = Resolve-Tool -Name 'dotnet' -Fallback 'C:\Tools\dotnet\dotnet.exe'
$flutter = Resolve-Tool -Name 'flutter' -Fallback 'C:\Tools\flutter\bin\flutter.bat'

$installedSdks = & $dotnet --list-sdks 2>$null
if (-not $installedSdks) {
    $dotnetFallback = 'C:\Tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetFallback)) {
        throw 'O .NET foi encontrado, mas nenhum SDK esta instalado.'
    }
    $dotnet = $dotnetFallback
}

Write-Host '1/4 - Compilando a API...'
& $dotnet build (Join-Path $projectRoot 'api\Monalisa.Financeiro.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '2/4 - Analisando o aplicativo Flutter...'
Push-Location (Join-Path $projectRoot 'app')
try {
    & $flutter analyze
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host '3/4 - Executando os testes Flutter...'
    & $flutter test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

Write-Host '4/4 - Executando os testes da API...'
$dotnetDirectory = Split-Path -Parent $dotnet
$env:PATH = "$dotnetDirectory;$env:PATH"
& (Join-Path $projectRoot 'test-api.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'QUALIDADE VALIDADA: compilacao, analise e testes concluidos.' -ForegroundColor Green
