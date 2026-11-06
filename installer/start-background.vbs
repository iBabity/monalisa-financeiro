Option Explicit
Dim shell, financeScript, panelScript
Set shell = CreateObject("WScript.Shell")
financeScript = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Programs\Monalisa Financeiro Demonstracao\Abrir-Monalisa-Financeiro.ps1"
panelScript = "C:\Projetos\Monalisa\monalisa_financeiro\iniciar-painel.ps1"
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & financeScript & """ -NoBrowser", 0, False
If CreateObject("Scripting.FileSystemObject").FileExists(panelScript) Then
    shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & panelScript & """ -NoBrowser", 0, False
End If
