param(
    [datetime]$Data = (Get-Date),
    [string]$Chave = (Join-Path $PSScriptRoot '..\.private-support\master.key')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Chave)) {
    throw "Chave privada de suporte não encontrada em: $Chave"
}

$keyBytes = [Convert]::FromBase64String((Get-Content -LiteralPath $Chave -Raw).Trim())
$day = $Data.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$hmac = [Security.Cryptography.HMACSHA256]::new($keyBytes)
try {
    $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($day))
} finally {
    $hmac.Dispose()
}

$firstEight = [byte[]]::new(8)
[Array]::Copy($hash, $firstEight, 8)
if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($firstEight) }
$number = [BitConverter]::ToUInt64($firstEight, 0) % [uint64]10000000000
$pin = $number.ToString('D10', [Globalization.CultureInfo]::InvariantCulture)

[pscustomobject]@{
    Data = $Data.ToString('dd/MM/yyyy')
    Usuario = 'ADMIN'
    Senha = $pin
    Validade = 'ATÉ 23:59 DESTA DATA'
}
