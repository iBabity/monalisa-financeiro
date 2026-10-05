param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\showcase'),
    [string]$BuildName = '',
    [string]$BuildNumber = '',
    [string]$FileName = 'Monalisa-Financeiro-Demonstracao-Setup-1.1.17.exe'
)

$ErrorActionPreference = 'Stop'
$arguments = @{
    OutputDirectory = $OutputDirectory
    FileName = $FileName
    Showcase = $true
}
if ($BuildName) { $arguments.BuildName = $BuildName }
if ($BuildNumber) { $arguments.BuildNumber = $BuildNumber }

& (Join-Path $PSScriptRoot 'package-installer.ps1') @arguments
