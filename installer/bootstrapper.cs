using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class Bootstrapper
{
    [STAThread]
    private static int Main()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "monalisa-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            string payloadPath = Path.Combine(temporaryDirectory, "payload.zip");
            Extract("payload.zip", payloadPath);
            VerifyPayload(payloadPath, ReadTextResource("payload.sha256"));

            string preparedPayload = Path.Combine(temporaryDirectory, "programa");
            Directory.CreateDirectory(preparedPayload);
            try
            {
                ZipFile.ExtractToDirectory(payloadPath, preparedPayload);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("O PACOTE DO PROGRAMA ESTÁ CORROMPIDO. BAIXE NOVAMENTE O INSTALADOR COMPLETO.");
            }

            string scriptPath = Path.Combine(temporaryDirectory, "install.ps1");
            Extract("install.ps1", scriptPath);
            string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -PreparedPayloadDirectory \"" + preparedPayload + "\"";
            string licenseServer = Environment.GetEnvironmentVariable("MONALISA_LICENSE_URL") ?? "";
            bool updateMode = string.Equals(Environment.GetEnvironmentVariable("MONALISA_UPDATE_MODE"), "1", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(licenseServer))
                arguments += " -LicenseServerUrl \"" + licenseServer.Replace("\"", "") + "\"";
            if (updateMode)
                arguments += " -SkipRuntimeInstall -NoLaunch";
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe", arguments);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process process = Process.Start(start);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException("A INSTALAÇÃO NÃO FOI CONCLUÍDA.\r\n\r\n" + Clean(detail));
            }
            if (updateMode)
            {
                string installedApplication = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Monalisa Financeiro",
                    "MonalisaFinanceiro.exe");
                if (!File.Exists(installedApplication))
                    throw new FileNotFoundException("A ATUALIZAÇÃO TERMINOU, MAS O EXECUTÁVEL INSTALADO NÃO FOI ENCONTRADO.", installedApplication);
                ProcessStartInfo applicationStart = new ProcessStartInfo(installedApplication);
                applicationStart.WorkingDirectory = Path.GetDirectoryName(installedApplication);
                applicationStart.UseShellExecute = true;
                Process.Start(applicationStart);
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "MONALISA FINANCEIRO", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); } catch { }
        }
    }

    private static void VerifyPayload(string path, string expectedHash)
    {
        string actualHash;
        using (SHA256 sha = SHA256.Create())
        using (FileStream file = File.OpenRead(path)) actualHash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        if (!string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O INSTALADOR ESTÁ INCOMPLETO OU CORROMPIDO. BAIXE O ARQUIVO NOVAMENTE.");
    }

    private static string ReadTextResource(string resourceName)
    {
        Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (source == null) throw new InvalidOperationException("RECURSO AUSENTE NO INSTALADOR: " + resourceName);
        using (source)
        using (StreamReader reader = new StreamReader(source, Encoding.ASCII)) return reader.ReadToEnd();
    }

    private static void Extract(string resourceName, string destination)
    {
        Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (source == null) throw new InvalidOperationException("RECURSO AUSENTE NO INSTALADOR: " + resourceName);
        using (source)
        using (FileStream target = File.Create(destination)) source.CopyTo(target);
    }

    private static string Clean(string value)
    {
        value = (value ?? "").Trim();
        return value.Length > 1200 ? value.Substring(0, 1200) : value;
    }
}
