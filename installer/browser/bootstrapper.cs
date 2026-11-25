using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class BrowserSetup
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        string temp = Path.Combine(Path.GetTempPath(), "monalisa-browser-" + Guid.NewGuid().ToString("N"));
        using (Form window = new Form())
        {
            window.Text = "MONALISA FINANCEIRO";
            window.ClientSize = new System.Drawing.Size(480, 150);
            window.FormBorderStyle = FormBorderStyle.FixedDialog;
            window.ControlBox = false;
            window.StartPosition = FormStartPosition.CenterScreen;
            window.BackColor = System.Drawing.Color.FromArgb(247, 242, 234);
            Label label = new Label { Left = 25, Top = 25, Width = 430, Height = 60, Text = "PREPARANDO O MONALISA FINANCEIRO...\r\nBANCO INDEPENDENTE NESTE COMPUTADOR.", ForeColor = System.Drawing.Color.FromArgb(7, 63, 67) };
            ProgressBar progress = new ProgressBar { Left = 25, Top = 105, Width = 430, Style = ProgressBarStyle.Marquee };
            window.Controls.Add(label); window.Controls.Add(progress); window.Show(); Application.DoEvents();
            try
            {
                if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("ESTE INSTALADOR REQUER WINDOWS 64 BITS.");
                Directory.CreateDirectory(temp);
                string zip = Path.Combine(temp, "payload.zip");
                Extract("payload.zip", zip);
                string expected;
                using (StreamReader reader = new StreamReader(Resource("payload.sha256"))) expected = reader.ReadToEnd().Trim();
                using (SHA256 sha = SHA256.Create())
                using (FileStream file = File.OpenRead(zip))
                    if (!string.Equals(expected, BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("INSTALADOR CORROMPIDO. BAIXE NOVAMENTE.");
                string payload = Path.Combine(temp, "programa");
                ZipFile.ExtractToDirectory(zip, payload);
                string script = Path.Combine(temp, "install.ps1"); Extract("install.ps1", script);
                label.Text = "INSTALANDO O SERVIÇO LOCAL E OS ATALHOS...\r\nSEUS DADOS PERMANECEM NESTE COMPUTADOR."; Application.DoEvents();
                ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -PreparedPayloadDirectory \"" + payload + "\"");
                info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardError = true; info.RedirectStandardOutput = true;
                StringBuilder errors = new StringBuilder();
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) lock (errors) errors.AppendLine(args.Data); };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { };
                    process.Start(); process.BeginErrorReadLine(); process.BeginOutputReadLine();
                    while (!process.WaitForExit(100)) Application.DoEvents();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException(errors.ToString());
                }
                return 0;
            }
            catch (Exception error) { MessageBox.Show(window, "NÃO FOI POSSÍVEL CONCLUIR A INSTALAÇÃO.\r\n" + error.Message, "MONALISA FINANCEIRO", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            finally { window.Close(); try { Directory.Delete(temp, true); } catch { } }
        }
    }
    private static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }
    private static void Extract(string name, string path) { using (Stream source = Resource(name)) using (FileStream target = File.Create(path)) source.CopyTo(target); }
}
