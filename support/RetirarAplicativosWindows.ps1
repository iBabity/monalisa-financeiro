$ErrorActionPreference = 'Stop'
$archive = 'C:\ProgramData\Monalisa Financeiro Demonstracao\backups\aplicativos-windows-20261101'
New-Item -ItemType Directory -Path $archive -Force | Out-Null
$targets = @(
    @{ Path = 'C:\Program Files\Monalisa Financeiro\MonalisaFinanceiro.exe'; Backup = 'MonalisaFinanceiro-copia-1.exe' },
    @{ Path = 'C:\Program Files\Monalisa Financeiro Demonstracao\MonalisaFinanceiro.exe'; Backup = 'MonalisaFinanceiro-copia-2.exe' }
)
foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target.Path) {
        Move-Item -LiteralPath $target.Path -Destination (Join-Path $archive $target.Backup) -Force
    }
}
'Aplicativos Windows retirados. Banco e servidor Web preservados.' |
    Out-File -LiteralPath (Join-Path $archive 'resultado.txt') -Encoding utf8
