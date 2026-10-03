param(
    [string]$PayloadZip = (Join-Path $PSScriptRoot 'payload.zip'),
    [string]$PreparedPayloadDirectory = '',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'Monalisa Financeiro'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'Monalisa Financeiro\data'),
    [string]$LicenseServerUrl = 'https://licencas.monalisasistemas.com.br',
    [switch]$NoShortcuts,
    [switch]$NoLaunch,
    [switch]$SkipRuntimeInstall
)

$ErrorActionPreference = 'Stop'
$defaultInstallRoot = Join-Path $env:ProgramFiles 'Monalisa Financeiro'
$defaultDataRoot = Join-Path $env:ProgramData 'Monalisa Financeiro\data'
$showcaseEdition = $PreparedPayloadDirectory -and (Test-Path -LiteralPath (Join-Path $PreparedPayloadDirectory 'showcase-edition.txt'))
if ($showcaseEdition) {
    if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -eq [IO.Path]::GetFullPath($defaultInstallRoot).TrimEnd('\')) {
        $InstallRoot = Join-Path $env:ProgramFiles 'Monalisa Financeiro Demonstracao'
    }
    if ([IO.Path]::GetFullPath($DataRoot).TrimEnd('\') -eq [IO.Path]::GetFullPath($defaultDataRoot).TrimEnd('\')) {
        $DataRoot = Join-Path $env:ProgramData 'Monalisa Financeiro Demonstracao\data'
    }
    $LicenseServerUrl = ''
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$DataRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
$protectedRoots = @(
    [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\'),
    [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'),
    [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\'),
    [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\')
)
if ($protectedRoots -contains $InstallRoot) { throw 'PASTA DE INSTALAÇÃO INSEGURA. ESCOLHA UMA SUBPASTA EXCLUSIVA DO MONALISA FINANCEIRO.' }

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Stop-MonalisaProcesses {
    # Monalisa.Financeiro.exe era o nome do servidor usado pelas versoes
    # anteriores, que abriam a interface no navegador. Ele tambem precisa ser
    # encerrado para que uma atualizacao consiga substituir a pasta instalada.
    $processNames = @(
        'MonalisaFinanceiro',
        'Monalisa.Financeiro.Server',
        'Monalisa.Financeiro'
    )

    Get-Process -Name $processNames -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    foreach ($imageName in @(
        'MonalisaFinanceiro.exe',
        'Monalisa.Financeiro.Server.exe',
        'Monalisa.Financeiro.exe'
    )) {
        $processName = [IO.Path]::GetFileNameWithoutExtension($imageName)
        if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
            Start-Process -FilePath "$env:WINDIR\System32\taskkill.exe" `
                -ArgumentList @('/F', '/T', '/IM', $imageName) `
                -Wait -WindowStyle Hidden | Out-Null
        }
    }
    # O taskkill devolve 128 quando a imagem ja nao existe. Isso significa que
    # nao ha nada para encerrar e nao deve marcar a instalacao como falha.
    $global:LASTEXITCODE = 0

    $limit = (Get-Date).AddSeconds(15)
    do {
        $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $limit)

    throw 'NAO FOI POSSIVEL FECHAR UMA VERSAO ANTERIOR DO MONALISA FINANCEIRO. FECHE O PROGRAMA E TENTE NOVAMENTE.'
}

function Clear-InstallDirectory([string]$Path) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $lastError = $null
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
                Remove-Item -Recurse -Force -ErrorAction Stop
            return
        } catch {
            $lastError = $_
            Start-Sleep -Milliseconds 500
        }
    }
    throw "NAO FOI POSSIVEL ATUALIZAR A PASTA DO MONALISA FINANCEIRO. FECHE O PROGRAMA E TENTE NOVAMENTE. DETALHE: $($lastError.Exception.Message)"
}

function Start-InstalledApplication([string]$ApplicationPath, [string]$WorkingDirectory) {
    # O instalador roda elevado para gravar em Program Files. Abrir o aplicativo
    # diretamente daqui pode deixá-lo atrás da janela do instalador ou na sessão
    # elevada. O Explorer encaminha a abertura para a sessão visível do usuário.
    $explorer = Join-Path $env:WINDIR 'explorer.exe'
    try {
        Start-Process -FilePath $explorer -ArgumentList @('"' + $ApplicationPath + '"') | Out-Null
    } catch {
        Start-Process -FilePath $ApplicationPath -WorkingDirectory $WorkingDirectory | Out-Null
    }
}

function Test-VisualCppRuntime {
    return (Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\MSVCP140.dll')) -and
        (Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\VCRUNTIME140.dll')) -and
        (Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\VCRUNTIME140_1.dll'))
}

function Install-VisualCppRuntime([string]$InstallerPath) {
    if (Test-VisualCppRuntime) { return $false }
    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        throw 'O INSTALADOR NÃO CONTÉM O MICROSOFT VISUAL C++ RUNTIME NECESSÁRIO.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw 'A ASSINATURA DO MICROSOFT VISUAL C++ RUNTIME É INVÁLIDA.'
    }
    $runtime = Start-Process -FilePath $InstallerPath `
        -ArgumentList @('/install', '/quiet', '/norestart') `
        -Wait -PassThru -WindowStyle Hidden
    if ($runtime.ExitCode -notin @(0, 1638, 3010)) {
        throw "NÃO FOI POSSÍVEL INSTALAR O MICROSOFT VISUAL C++ RUNTIME. CÓDIGO: $($runtime.ExitCode)."
    }
    if (-not (Test-VisualCppRuntime) -and $runtime.ExitCode -ne 3010) {
        throw 'O MICROSOFT VISUAL C++ RUNTIME NÃO FOI INSTALADO CORRETAMENTE.'
    }
    return $runtime.ExitCode -eq 3010
}

$usesProtectedFolders = $InstallRoot.StartsWith($env:ProgramFiles, [StringComparison]::OrdinalIgnoreCase) -or
    $DataRoot.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)

if ($usesProtectedFolders -and -not (Test-Administrator)) {
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath),
        '-PayloadZip', (Quote-Argument $PayloadZip),
        '-InstallRoot', (Quote-Argument $InstallRoot),
        '-DataRoot', (Quote-Argument $DataRoot)
    )
    if ($PreparedPayloadDirectory) { $arguments += @('-PreparedPayloadDirectory', (Quote-Argument $PreparedPayloadDirectory)) }
    if ($LicenseServerUrl) { $arguments += @('-LicenseServerUrl', (Quote-Argument $LicenseServerUrl)) }
    if ($NoShortcuts) { $arguments += '-NoShortcuts' }
    if ($NoLaunch) { $arguments += '-NoLaunch' }
    if ($SkipRuntimeInstall) { $arguments += '-SkipRuntimeInstall' }
    $process = Start-Process powershell.exe -Verb RunAs -ArgumentList ($arguments -join ' ') -Wait -PassThru
    exit $process.ExitCode
}

if (-not $PreparedPayloadDirectory -and -not (Test-Path -LiteralPath $PayloadZip)) {
    throw "PACOTE DO MONALISA FINANCEIRO NÃO ENCONTRADO: $PayloadZip"
}

if (-not $showcaseEdition) {
    if (-not $LicenseServerUrl) { throw 'O INSTALADOR NÃO POSSUI O ENDEREÇO DO PAINEL CENTRAL.' }
    $licenseUri = $null
    if (-not [Uri]::TryCreate($LicenseServerUrl, [UriKind]::Absolute, [ref]$licenseUri)) { throw 'ENDEREÇO DO PAINEL CENTRAL INVÁLIDO.' }
    $isLocal = $licenseUri.Host -in @('127.0.0.1', 'localhost')
    $ipAddress = $null
    $isPrivateNetwork = [Net.IPAddress]::TryParse($licenseUri.Host, [ref]$ipAddress) -and (
        $ipAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and (
            $ipAddress.GetAddressBytes()[0] -eq 10 -or
            ($ipAddress.GetAddressBytes()[0] -eq 172 -and $ipAddress.GetAddressBytes()[1] -ge 16 -and $ipAddress.GetAddressBytes()[1] -le 31) -or
            ($ipAddress.GetAddressBytes()[0] -eq 192 -and $ipAddress.GetAddressBytes()[1] -eq 168)
        )
    )
    if ($licenseUri.Scheme -ne 'https' -and -not $isLocal -and -not $isPrivateNetwork) { throw 'FORA DA REDE LOCAL, O PAINEL CENTRAL PRECISA USAR HTTPS.' }
    $LicenseServerUrl = $LicenseServerUrl.TrimEnd('/')
}

$staging = if ($PreparedPayloadDirectory) { [IO.Path]::GetFullPath($PreparedPayloadDirectory) } else { Join-Path $env:TEMP ('monalisa-install-' + [Guid]::NewGuid().ToString('N')) }
$removeStaging = -not [bool]$PreparedPayloadDirectory
try {
    if ($removeStaging) {
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        Expand-Archive -LiteralPath $PayloadZip -DestinationPath $staging -Force
    }
    $executable = Join-Path $staging 'MonalisaFinanceiro.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'O PACOTE NÃO CONTÉM O APLICATIVO WINDOWS DO MONALISA FINANCEIRO.' }
    $restartRequired = $false
    if (-not $SkipRuntimeInstall) {
        $restartRequired = Install-VisualCppRuntime (Join-Path $staging 'redist\VC_redist.x64.exe')
    }

    Stop-MonalisaProcesses
    $database = Join-Path $DataRoot 'financeiro.db'
    if (Test-Path -LiteralPath $database) {
        $backupRoot = Join-Path (Split-Path $DataRoot -Parent) 'backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $backupName = 'financeiro-antes-atualizacao-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.db'
        Copy-Item -LiteralPath $database -Destination (Join-Path $backupRoot $backupName) -Force
    }
    Clear-InstallDirectory $InstallRoot
    Copy-Item -Path (Join-Path $staging '*') -Destination $InstallRoot -Recurse -Force
    $installedRedist = Join-Path $InstallRoot 'redist'
    if (Test-Path -LiteralPath $installedRedist) { Remove-Item -LiteralPath $installedRedist -Recurse -Force }
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    if ($DataRoot.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)) {
        & "$env:WINDIR\System32\icacls.exe" $DataRoot /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'NÃO FOI POSSÍVEL LIBERAR A PASTA DE DADOS PARA OS USUÁRIOS DO WINDOWS.' }
    }

    $configurationRoot = Split-Path $DataRoot -Parent
    New-Item -ItemType Directory -Path $configurationRoot -Force | Out-Null
    if (-not $showcaseEdition) {
        Set-Content -LiteralPath (Join-Path $configurationRoot 'license-server.txt') -Value $LicenseServerUrl -Encoding ascii
    }

    $applicationPath = Join-Path $InstallRoot 'MonalisaFinanceiro.exe'
    if (-not (Test-Path -LiteralPath $applicationPath)) { throw 'O EXECUTÁVEL DO APLICATIVO WINDOWS NÃO FOI ENCONTRADO.' }

    if (-not $NoShortcuts) {
        $wsh = New-Object -ComObject WScript.Shell
        $productName = if ($showcaseEdition) { 'Monalisa Financeiro Demonstracao' } else { 'Monalisa Financeiro' }
        $shortcutTargets = @(
            (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) ($productName + '.lnk')),
            (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) ($productName + '.lnk'))
        )
        foreach ($shortcutPath in $shortcutTargets) {
            $shortcut = $wsh.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $applicationPath
            $shortcut.Arguments = ''
            $shortcut.WorkingDirectory = $InstallRoot
            $shortcut.IconLocation = $applicationPath + ',0'
            $shortcut.Description = if ($showcaseEdition) { 'MONALISA FINANCEIRO - DEMONSTRAÇÃO' } else { 'MONALISA FINANCEIRO' }
            $shortcut.Save()
        }
        $networkShortcutName = if ($showcaseEdition) { 'Configurar rede do Monalisa Financeiro Demonstracao.lnk' } else { 'Configurar rede do Monalisa Financeiro.lnk' }
        $networkShortcutPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $networkShortcutName
        $networkShortcut = $wsh.CreateShortcut($networkShortcutPath)
        $networkShortcut.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $networkShortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'Configurar-Rede-Financeira.ps1') + '"'
        $networkShortcut.WorkingDirectory = $InstallRoot
        $networkShortcut.IconLocation = $applicationPath + ',0'
        $networkShortcut.Description = 'CONFIGURAR DUAS MÁQUINAS COM OS MESMOS DADOS'
        $networkShortcut.Save()
    }

    if (-not $NoLaunch -or -not $NoShortcuts) {
        Add-Type -AssemblyName PresentationFramework
        $successMessage = if ($restartRequired) {
            if ($showcaseEdition) { 'MONALISA FINANCEIRO - DEMONSTRAÇÃO INSTALADO. REINICIE O WINDOWS ANTES DE ABRIR O PROGRAMA.' } else { 'MONALISA FINANCEIRO INSTALADO. REINICIE O WINDOWS ANTES DE ABRIR O PROGRAMA.' }
        } else {
            if ($showcaseEdition) { 'MONALISA FINANCEIRO - DEMONSTRAÇÃO INSTALADO COM SUCESSO. AO CLICAR EM OK, O SISTEMA SERÁ ABERTO.' } else { 'MONALISA FINANCEIRO INSTALADO COM SUCESSO. AO CLICAR EM OK, O SISTEMA SERÁ ABERTO.' }
        }
        [System.Windows.MessageBox]::Show($successMessage, 'MONALISA FINANCEIRO') | Out-Null
    }
    if (-not $NoLaunch -and -not $restartRequired) {
        Start-InstalledApplication $applicationPath $InstallRoot
    }
} finally {
    if ($removeStaging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
