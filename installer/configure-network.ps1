param(
    [ValidateSet('','server','client','standalone')][string]$Mode = '',
    [string]$ServerAddress = '',
    [string]$NetworkKey = '',
    [string]$ConfigurationRoot = (Join-Path $env:ProgramData 'Monalisa Financeiro'),
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-LanAddress {
    $address = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
        Sort-Object -Property InterfaceMetric |
        Select-Object -First 1 -ExpandProperty IPAddress
    if (-not $address) { $address = 'IP-DESTA-MAQUINA' }
    return $address
}

function New-NetworkKey {
    $bytes = New-Object byte[] 24
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return 'MLR-' + (($bytes | ForEach-Object { $_.ToString('X2') }) -join '')
}

function Normalize-ServerAddress([string]$Value) {
    $value = $Value.Trim().TrimEnd('/')
    if ($value -notmatch '^https?://') { $value = 'http://' + $value }
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri)) { throw 'INFORME O IP DA MÁQUINA PRINCIPAL.' }
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($uri.Host, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'INFORME O IP LOCAL DA MÁQUINA PRINCIPAL, POR EXEMPLO 192.168.1.20.' }
    $bytes = $ip.GetAddressBytes()
    $private = $bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $private) { throw 'O ENDEREÇO PRECISA PERTENCER À MESMA REDE LOCAL DAS DUAS MÁQUINAS.' }
    $builder = [UriBuilder]::new($uri)
    if ($builder.Port -eq 80 -or $builder.Port -eq -1) { $builder.Port = 5188 }
    $builder.Path = ''
    $builder.Query = ''
    return $builder.Uri.AbsoluteUri.TrimEnd('/')
}

function Save-NetworkConfiguration([string]$SelectedMode, [string]$Address, [string]$Key) {
    New-Item -ItemType Directory -Path $ConfigurationRoot -Force | Out-Null
    $selectedMode = $SelectedMode.ToLowerInvariant()
    if ($selectedMode -eq 'standalone') {
        Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-role.txt') -Value 'standalone' -Encoding ascii
        Remove-Item -LiteralPath (Join-Path $ConfigurationRoot 'network-server.txt') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $ConfigurationRoot 'network-key.txt') -Force -ErrorAction SilentlyContinue
        return 'MODO INDIVIDUAL CONFIGURADO.'
    }

    if ($Key.Length -lt 24) { throw 'A CHAVE DE CONEXÃO DA REDE É INVÁLIDA.' }
    if ($selectedMode -eq 'server') {
        $database = Join-Path $ConfigurationRoot 'data\financeiro.db'
        if (Test-Path -LiteralPath $database) {
            $backupRoot = Join-Path $ConfigurationRoot 'backups'
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            Copy-Item -LiteralPath $database -Destination (Join-Path $backupRoot ('financeiro-antes-rede-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.db')) -Force
        }
        Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-role.txt') -Value 'server' -Encoding ascii
        Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-server.txt') -Value $Address -Encoding ascii
        Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-key.txt') -Value $Key -Encoding ascii
        Get-NetFirewallRule -DisplayName 'Monalisa Financeiro - Dados compartilhados' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName 'Monalisa Financeiro - Dados compartilhados' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5188 -Profile Private,Public -RemoteAddress LocalSubnet -ErrorAction Stop | Out-Null
        return "MÁQUINA PRINCIPAL CONFIGURADA.`n`nENDEREÇO: $Address`nCHAVE DE CONEXÃO: $Key`n`nANOTE O ENDEREÇO E A CHAVE PARA CONFIGURAR A SEGUNDA MÁQUINA."
    }

    $address = Normalize-ServerAddress $Address
    try {
        $headers = @{ 'X-Monalisa-Network-Key' = $Key }
        $health = Invoke-RestMethod -Uri ($address + '/api/health') -Headers $headers -TimeoutSec 8
        if ($health.status -ne 'ok') { throw 'RESPOSTA INVÁLIDA.' }
    } catch {
        throw "NÃO FOI POSSÍVEL CONECTAR À MÁQUINA PRINCIPAL EM $address. DEIXE O FINANCEIRO ABERTO NELA, CONFIRA O IP, A CHAVE E A REDE PRIVADA DO WINDOWS."
    }
    Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-role.txt') -Value 'client' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-server.txt') -Value $address -Encoding ascii
    Set-Content -LiteralPath (Join-Path $ConfigurationRoot 'network-key.txt') -Value $Key -Encoding ascii
    return "SEGUNDA MÁQUINA CONECTADA A $address."
}

if (-not (Test-Administrator)) {
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'))
    if ($Mode) { $arguments += @('-Mode', $Mode) }
    if ($ServerAddress) { $arguments += @('-ServerAddress', ('"' + $ServerAddress + '"')) }
    if ($NetworkKey) { $arguments += @('-NetworkKey', ('"' + $NetworkKey + '"')) }
    if ($ConfigurationRoot) { $arguments += @('-ConfigurationRoot', ('"' + $ConfigurationRoot + '"')) }
    if ($NonInteractive) { $arguments += '-NonInteractive' }
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

if ($NonInteractive) {
    if ($Mode -eq '') { throw 'INFORME O MODO DA MÁQUINA.' }
    if ($Mode -eq 'server' -and -not $NetworkKey) { $NetworkKey = New-NetworkKey }
    if ($Mode -eq 'server' -and -not $ServerAddress) { $ServerAddress = 'http://' + (Get-LanAddress) + ':5188' }
    Write-Output (Save-NetworkConfiguration $Mode $ServerAddress $NetworkKey)
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$lanAddress = Get-LanAddress
$generatedKey = New-NetworkKey
$form = [Windows.Forms.Form]::new()
$form.Text = 'MONALISA FINANCEIRO - DADOS EM REDE'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = [Drawing.Size]::new(650,520)
$form.BackColor = [Drawing.Color]::FromArgb(247,243,235)
$form.Font = [Drawing.Font]::new('Segoe UI',10)
$form.MaximizeBox = $false
$form.FormBorderStyle = 'FixedDialog'

$title = [Windows.Forms.Label]::new(); $title.Text='COMPARTILHAR DADOS ENTRE DUAS MÁQUINAS'; $title.Font=[Drawing.Font]::new('Segoe UI Semibold',17); $title.ForeColor=[Drawing.Color]::FromArgb(0,73,78); $title.AutoSize=$true; $title.Location=[Drawing.Point]::new(28,24); $form.Controls.Add($title)
$info = [Windows.Forms.Label]::new(); $info.Text='ESCOLHA O PAPEL DESTA MÁQUINA. AS DUAS CONTINUAM COM LICENÇAS PRÓPRIAS.'; $info.AutoSize=$true; $info.Location=[Drawing.Point]::new(31,67); $form.Controls.Add($info)

$serverRadio=[Windows.Forms.RadioButton]::new();$serverRadio.Text='MÁQUINA PRINCIPAL (GUARDA OS DADOS)';$serverRadio.AutoSize=$true;$serverRadio.Location=[Drawing.Point]::new(34,112);$serverRadio.Checked=$true;$form.Controls.Add($serverRadio)
$clientRadio=[Windows.Forms.RadioButton]::new();$clientRadio.Text='SEGUNDA MÁQUINA (USA OS DADOS DA PRINCIPAL)';$clientRadio.AutoSize=$true;$clientRadio.Location=[Drawing.Point]::new(34,148);$form.Controls.Add($clientRadio)
$singleRadio=[Windows.Forms.RadioButton]::new();$singleRadio.Text='USAR SOMENTE NESTA MÁQUINA';$singleRadio.AutoSize=$true;$singleRadio.Location=[Drawing.Point]::new(34,184);$form.Controls.Add($singleRadio)

$addressLabel=[Windows.Forms.Label]::new();$addressLabel.Text='ENDEREÇO DA MÁQUINA PRINCIPAL';$addressLabel.AutoSize=$true;$addressLabel.Location=[Drawing.Point]::new(34,235);$form.Controls.Add($addressLabel)
$addressBox=[Windows.Forms.TextBox]::new();$addressBox.Location=[Drawing.Point]::new(34,260);$addressBox.Size=[Drawing.Size]::new(580,34);$addressBox.Text="http://${lanAddress}:5188";$form.Controls.Add($addressBox)
$keyLabel=[Windows.Forms.Label]::new();$keyLabel.Text='CHAVE DE CONEXÃO DA REDE';$keyLabel.AutoSize=$true;$keyLabel.Location=[Drawing.Point]::new(34,312);$form.Controls.Add($keyLabel)
$keyBox=[Windows.Forms.TextBox]::new();$keyBox.Location=[Drawing.Point]::new(34,337);$keyBox.Size=[Drawing.Size]::new(580,34);$keyBox.Text=$generatedKey;$form.Controls.Add($keyBox)
$help=[Windows.Forms.Label]::new();$help.Text='NA PRINCIPAL, SALVE E ANOTE OS DOIS CAMPOS. NA SEGUNDA, DIGITE EXATAMENTE OS MESMOS DADOS.';$help.AutoSize=$false;$help.Size=[Drawing.Size]::new(580,45);$help.Location=[Drawing.Point]::new(34,383);$help.ForeColor=[Drawing.Color]::FromArgb(80,80,80);$form.Controls.Add($help)
$save=[Windows.Forms.Button]::new();$save.Text='SALVAR CONFIGURAÇÃO';$save.Size=[Drawing.Size]::new(220,44);$save.Location=[Drawing.Point]::new(394,450);$save.BackColor=[Drawing.Color]::FromArgb(0,73,78);$save.ForeColor=[Drawing.Color]::White;$save.FlatStyle='Flat';$form.Controls.Add($save)

$refresh = {
    if ($serverRadio.Checked) { $addressBox.Text="http://${lanAddress}:5188"; if($keyBox.Text.Length -lt 24){$keyBox.Text=$generatedKey}; $addressBox.Enabled=$false; $keyBox.Enabled=$true }
    elseif ($clientRadio.Checked) { $addressBox.Enabled=$true; $keyBox.Enabled=$true; $addressBox.Text='http://'; $keyBox.Text='' }
    else { $addressBox.Enabled=$false; $keyBox.Enabled=$false }
}
$serverRadio.Add_CheckedChanged($refresh);$clientRadio.Add_CheckedChanged($refresh);$singleRadio.Add_CheckedChanged($refresh)
$save.Add_Click({
    try {
        $selected = if($serverRadio.Checked){'server'}elseif($clientRadio.Checked){'client'}else{'standalone'}
        $result = Save-NetworkConfiguration $selected $addressBox.Text.Trim() $keyBox.Text.Trim().ToUpperInvariant()
        Get-Process -Name @('MonalisaFinanceiro','Monalisa.Financeiro.Server') -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        [Windows.Forms.MessageBox]::Show($result + "`n`nABRA NOVAMENTE O MONALISA FINANCEIRO.",'CONFIGURAÇÃO CONCLUÍDA','OK','Information') | Out-Null
        $form.Close()
    } catch { [Windows.Forms.MessageBox]::Show($_.Exception.Message,'NÃO FOI POSSÍVEL CONFIGURAR','OK','Error') | Out-Null }
})

[void]$form.ShowDialog()
