param([string]$PreparedPayloadDirectory, [string]$Root = (Join-Path $env:LOCALAPPDATA 'MonalisaFinanceiroIndependente'), [switch]$NoLaunch, [switch]$NoShortcuts)
$ErrorActionPreference='Stop'
$rootFull=[IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($rootFull -eq [IO.Path]::GetPathRoot($rootFull).TrimEnd('\')) { throw 'PASTA DE INSTALAÇÃO INVÁLIDA.' }
$info=Get-Content -LiteralPath (Join-Path $PreparedPayloadDirectory 'package.json') -Raw | ConvertFrom-Json
if ($info.channel -ne 'browser-independent' -or $info.version -notmatch '^\d+\.\d+\.\d+$') { throw 'PACOTE INVÁLIDO.' }
$bad=Get-ChildItem -LiteralPath $PreparedPayloadDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)\.db($|-)|\.ofx$|\.license\.json$|credentials|private.*key|^\.env' }
if ($bad) { throw 'O INSTALADOR CONTÉM DADOS QUE NÃO PODEM SER DISTRIBUÍDOS.' }
. (Join-Path $PreparedPayloadDirectory 'common.ps1')
$currentPath=Join-Path $rootFull 'current.json'
if (Test-Path -LiteralPath $currentPath) {
    $current=Read-JsonFile $currentPath
    if ([version]$info.version -lt [version]$current.version) { throw 'JÁ EXISTE UMA VERSÃO MAIS RECENTE NESTE COMPUTADOR.' }
    if ([version]$info.version -gt [version]$current.version) {
        if(Test-OwnServer $rootFull){throw 'REINICIE O COMPUTADOR E EXECUTE O INSTALADOR ANTES DE ABRIR O FINANCEIRO, OU AGUARDE A ATUALIZAÇÃO AUTOMÁTICA.'}
        Backup-IndependentData $rootFull
    }
}
$target=Join-Path $rootFull ('versions/' + $info.version)
if (-not(Test-Path -LiteralPath $target)) {
    $staging=Join-Path $rootFull ('versions/.install-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $PreparedPayloadDirectory '*') -Destination $staging -Recurse -Force
    Move-Item -LiteralPath $staging -Destination $target
}
New-Item -ItemType Directory -Path (Join-Path $rootFull 'data') -Force | Out-Null
if (-not(Test-Path -LiteralPath (Join-Path $rootFull 'update-public.xml'))) { Copy-Item -LiteralPath (Join-Path $target 'update-public.xml') -Destination $rootFull }
Write-AtomicJson $currentPath @{version=$info.version}
Copy-Item -LiteralPath (Join-Path $target 'start.vbs') -Destination $rootFull -Force
$dispatcher=@'
param([switch]$NoBrowser)
$ErrorActionPreference='Stop'
$current=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'current.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot ('versions/' + $current.version + '/launch.ps1')) -Root $PSScriptRoot -NoBrowser:$NoBrowser
'@
[IO.File]::WriteAllText((Join-Path $rootFull 'launch.ps1'),$dispatcher,[Text.UTF8Encoding]::new($true))
if(-not $NoShortcuts){
    $wsh=New-Object -ComObject WScript.Shell
    foreach($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Monalisa Financeiro.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'Monalisa Financeiro.lnk'),(Join-Path ([Environment]::GetFolderPath('Startup')) 'Monalisa Financeiro Independente.lnk'))){
        $shortcut=$wsh.CreateShortcut($shortcutPath)
        $shortcut.TargetPath=Join-Path $env:WINDIR 'System32/wscript.exe'
        $shortcut.Arguments='"'+(Join-Path $rootFull 'start.vbs')+'"'
        if($shortcutPath.StartsWith([Environment]::GetFolderPath('Startup'))){$shortcut.Arguments+=' --background'}
        $shortcut.WorkingDirectory=$rootFull
        $shortcut.IconLocation=(Join-Path $rootFull 'monalisa.ico')
        $shortcut.Description='MONALISA FINANCEIRO - BANCO INDEPENDENTE'
        $shortcut.Save()
    }
}
Copy-Item -LiteralPath (Join-Path $target 'monalisa.ico') -Destination $rootFull -Force
if(-not $NoLaunch){Start-Process -FilePath (Join-Path $env:WINDIR 'System32/wscript.exe') -ArgumentList ('"'+(Join-Path $rootFull 'start.vbs')+'"') -WindowStyle Hidden | Out-Null}
