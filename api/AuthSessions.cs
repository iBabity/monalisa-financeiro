using System.Security.Cryptography;

record AuthIdentity(int Id, string Name, string Username, bool GlobalAdmin);

sealed class AuthSessions
{
    readonly Dictionary<string, (AuthIdentity Identity, DateTime Expires)> sessions = new();
    readonly object gate = new();
    public string Create(AuthIdentity identity) { lock (gate) { Cleanup(); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); sessions[token] = (identity, DateTime.Now.AddHours(12)); return token; } }
    public bool TryGet(string token, out AuthIdentity identity) { lock (gate) { Cleanup(); if (token.Length > 0 && sessions.TryGetValue(token, out var session)) { sessions[token] = (session.Identity, DateTime.Now.AddHours(12)); identity = session.Identity; return true; } identity = null!; return false; } }
    public void Remove(string token) { lock (gate) sessions.Remove(token); }
    void Cleanup() { foreach (var token in sessions.Where(item => item.Value.Expires <= DateTime.Now).Select(item => item.Key).ToList()) sessions.Remove(token); }
}
