Option Explicit
Dim shell, fso, root, args, item
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
args = ""
For Each item In WScript.Arguments
  If item = "--background" Then args = " -NoBrowser"
Next
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & root & "\launch.ps1""" & args, 0, False
