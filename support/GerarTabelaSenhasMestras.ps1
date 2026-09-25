param(
    [datetime]$Inicio = [datetime]'2026-01-01',
    [datetime]$Fim = [datetime]'2045-12-31',
    [string]$Chave = (Join-Path $PSScriptRoot '..\.private-support\master.key'),
    [string]$Destino = (Join-Path $PSScriptRoot '..\api\master-access-codes.json')
)

$ErrorActionPreference = 'Stop'
if ($Fim -lt $Inicio) { throw 'A data final deve ser igual ou posterior à inicial.' }
if (-not (Test-Path -LiteralPath $Chave)) { throw "Chave privada de suporte não encontrada em: $Chave" }

$keyBytes = [Convert]::FromBase64String((Get-Content -LiteralPath $Chave -Raw).Trim())
$entries = [ordered]@{}
for ($date = $Inicio.Date; $date -le $Fim.Date; $date = $date.AddDays(1)) {
    $day = $date.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $hmac = [Security.Cryptography.HMACSHA256]::new($keyBytes)
    try { $raw = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($day)) } finally { $hmac.Dispose() }
    $firstEight = [byte[]]::new(8)
    [Array]::Copy($raw, $firstEight, 8)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($firstEight) }
    $number = [BitConverter]::ToUInt64($firstEight, 0) % [uint64]10000000000
    $pin = $number.ToString('D10', [Globalization.CultureInfo]::InvariantCulture)
    $verification = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("${day}:$pin"))
    $entries[$day] = [Convert]::ToHexString($verification)
}

$parent = Split-Path -Parent $Destino
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$entries | ConvertTo-Json -Depth 2 | Set-Content -LiteralPath $Destino -Encoding utf8
Write-Output "Tabela criada em $Destino com $($entries.Count) dias."
