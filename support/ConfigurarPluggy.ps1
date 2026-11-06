$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Security
$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Monalisa Financeiro\Integrations'
$file = Join-Path $directory 'pluggy-credentials.dat'
$form = New-Object Windows.Forms.Form
$form.Text = 'MONALISA - CONFIGURAR PLUGGY (SANDBOX)'
$form.ClientSize = New-Object Drawing.Size(510, 300)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$intro = New-Object Windows.Forms.Label
$intro.Text = 'INFORME AS CREDENCIAIS DA APLICAÇÃO PLUGGY.' + [Environment]::NewLine + 'NÃO INFORME SENHA DO BANCO. OS DADOS SERÃO CRIPTOGRAFADOS NESTE USUÁRIO DO WINDOWS.'
$intro.SetBounds(20, 15, 470, 55)
$form.Controls.Add($intro)
$idLabel = New-Object Windows.Forms.Label
$idLabel.Text = 'CLIENT ID'
$idLabel.SetBounds(20, 80, 470, 20)
$form.Controls.Add($idLabel)
$idInput = New-Object Windows.Forms.TextBox
$idInput.SetBounds(20, 102, 470, 26)
$form.Controls.Add($idInput)
$secretLabel = New-Object Windows.Forms.Label
$secretLabel.Text = 'CLIENT SECRET'
$secretLabel.SetBounds(20, 142, 470, 20)
$form.Controls.Add($secretLabel)
$secretInput = New-Object Windows.Forms.TextBox
$secretInput.UseSystemPasswordChar = $true
$secretInput.SetBounds(20, 164, 470, 26)
$form.Controls.Add($secretInput)
$status = New-Object Windows.Forms.Label
$status.SetBounds(20, 205, 470, 35)
$status.Text = 'ESTA ETAPA APENAS SALVA AS CREDENCIAIS. NÃO CONECTA SEU BANCO.'
$form.Controls.Add($status)
$save = New-Object Windows.Forms.Button
$save.Text = 'SALVAR COM SEGURANÇA'
$save.SetBounds(275, 252, 215, 32)
$form.Controls.Add($save)
$save.Add_Click({
    if ([string]::IsNullOrWhiteSpace($idInput.Text) -or [string]::IsNullOrWhiteSpace($secretInput.Text)) {
        $status.Text = 'PREENCHA CLIENT ID E CLIENT SECRET.'
        return
    }
    try {
        $payload = @{clientId=$idInput.Text.Trim();clientSecret=$secretInput.Text.Trim();sandbox=$true} | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllBytes($file, $encrypted)
        [Array]::Clear($bytes, 0, $bytes.Length)
        $secretInput.Clear()
        $idInput.Clear()
        [Windows.Forms.MessageBox]::Show('CREDENCIAIS SALVAS COM CRIPTOGRAFIA. VOLTE AO CHAT PARA CONTINUAR A INTEGRAÇÃO.', 'MONALISA') | Out-Null
        $form.Close()
    } catch {
        $status.Text = 'NÃO FOI POSSÍVEL SALVAR. FECHE ESTA JANELA E AVISE NO CHAT.'
    }
})
$form.AcceptButton = $save
try { $form.ShowDialog() | Out-Null } finally { $secretInput.Clear(); $form.Dispose() }
