using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Piloto de leitura: exclusivamente o conector fictício Pluggy Bank (2).
sealed class PluggySandbox
{
    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("https://api.pluggy.ai/"), Timeout = TimeSpan.FromSeconds(12) };
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monalisa Financeiro", "Integrations");
    string? key;
    DateTime keyExpires;

    public bool Configured => File.Exists(Path.Combine(root, "pluggy-credentials.dat"));

    async Task<string> Key()
    {
        if (key is not null && DateTime.UtcNow < keyExpires) return key;
        if (!OperatingSystem.IsWindows() || !Configured) throw new ArgumentException("Configure as credenciais da Pluggy neste computador.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(root, "pluggy-credentials.dat")), null, DataProtectionScope.CurrentUser);
        try
        {
            using var credentials = JsonDocument.Parse(plain);
            if (!credentials.RootElement.TryGetProperty("sandbox", out var sandbox) || !sandbox.GetBoolean()) throw new ArgumentException("Este piloto permite somente Sandbox.");
            using var response = await client.PostAsJsonAsync("auth", new {
                clientId = credentials.RootElement.GetProperty("clientId").GetString(),
                clientSecret = credentials.RootElement.GetProperty("clientSecret").GetString()
            });
            if (!response.IsSuccessStatusCode) throw new ArgumentException("A Pluggy recusou a autenticação. Revise as credenciais locais.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            key = json.RootElement.GetProperty("apiKey").GetString() ?? throw new ArgumentException("Resposta inválida da Pluggy.");
            keyExpires = DateTime.UtcNow.AddMinutes(100);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    async Task<JsonElement> Call(string path, object? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-API-KEY", await Key());
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 401) { key = null; }
            throw new ArgumentException($"A Pluggy não concluiu a consulta (HTTP {(int)response.StatusCode}). Tente consultar novamente.");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    string ItemFile(int company) => Path.Combine(root, $"sandbox-company-{company}.json");
    string? SavedItem(int company)
    {
        if (!File.Exists(ItemFile(company))) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(ItemFile(company)));
        return json.RootElement.GetProperty("itemId").GetString();
    }
    static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    static long Cents(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var amount) ? checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero)) : 0;

    public async Task<object> Connect(int company)
    {
        await gate.WaitAsync();
        try
        {
            var existing = SavedItem(company);
            if (existing is not null) return new { sandbox = true, connected = true, message = "CONEXÃO DE TESTE JÁ CADASTRADA. CONSULTE OS DADOS." };
            var connector = await Call("connectors/2");
            if (!connector.GetProperty("isSandbox").GetBoolean()) throw new ArgumentException("Conector não é Sandbox; operação recusada.");
            var item = await Call("items", new { connectorId = 2, parameters = new { user = "user-ok", password = "password-ok" } });
            var id = Text(item, "id");
            if (!Guid.TryParse(id, out _)) throw new ArgumentException("A Pluggy não devolveu uma conexão válida.");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(ItemFile(company), JsonSerializer.Serialize(new { itemId = id, connectorId = 2, sandbox = true }));
            return new { sandbox = true, connected = true, message = "CONEXÃO FICTÍCIA CRIADA. AGUARDE ALGUNS SEGUNDOS E CONSULTE OS DADOS." };
        }
        finally { gate.Release(); }
    }

    public async Task<object> Snapshot(int company)
    {
        await gate.WaitAsync();
        try
        {
            var id = SavedItem(company);
            if (id is null) return new { configured = Configured, connected = false, sandbox = true, ready = false, accounts = Array.Empty<object>() };
            var item = await Call($"items/{Uri.EscapeDataString(id)}");
            var connector = item.GetProperty("connector");
            if (connector.GetProperty("id").GetInt32() != 2 || !connector.GetProperty("isSandbox").GetBoolean()) throw new ArgumentException("Consulta limitada ao banco fictício do Sandbox.");
            var status = Text(item, "status");
            if (status != "UPDATED") return new { configured = Configured, connected = true, sandbox = true, ready = false, status, message = "A CONEXÃO AINDA NÃO ESTÁ PRONTA. CONSULTE NOVAMENTE EM ALGUNS SEGUNDOS.", accounts = Array.Empty<object>() };
            var accountPage = await Call($"accounts?itemId={Uri.EscapeDataString(id)}");
            var accounts = new List<object>();
            foreach (var account in accountPage.GetProperty("results").EnumerateArray().Where(a => Text(a, "type") == "BANK"))
            {
                var accountId = Text(account, "id");
                var path = "v2/transactions?accountId=" + Uri.EscapeDataString(accountId);
                var transactions = new List<object>(); var cursors = new HashSet<string>();
                for (var page = 0; page < 100; page++)
                {
                    if (!cursors.Add(path)) throw new ArgumentException("Paginação repetida na Pluggy.");
                    var data = await Call(path);
                    foreach (var t in data.GetProperty("results").EnumerateArray()) transactions.Add(new {
                        id = Text(t,"id"), description = Text(t,"description").ToUpperInvariant(), date = Text(t,"date"),
                        amount = Cents(t,"amount"), type = Text(t,"type"), status = Text(t,"status"), currency = Text(t,"currencyCode")
                    });
                    var next = Text(data,"next");
                    if (next.Length == 0) break;
                    // Nunca seguir um endereço externo com a chave da aplicação.
                    if (!next.StartsWith('?')) throw new ArgumentException("Formato de paginação inesperado na Pluggy.");
                    path = "v2/transactions" + next;
                    if (page == 99) throw new ArgumentException("Consulta muito extensa. Reduza o período antes de continuar.");
                }
                accounts.Add(new { name = Text(account,"name").ToUpperInvariant(), balance = Cents(account,"balance"), currency = Text(account,"currencyCode"), transactions });
            }
            return new { configured = Configured, connected = true, ready = true, sandbox = true, status, fetchedAt = DateTime.UtcNow, accounts };
        }
        finally { gate.Release(); }
    }
}
