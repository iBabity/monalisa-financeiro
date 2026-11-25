var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero) };
var sessions = new AuthSessions(clock);
var user = new AuthIdentity(7, "TESTE", "TESTE", true);
var token = sessions.Create(user);
Check(sessions.TryGet(token, out var found) && found.Id == user.Id, "Login válido no mesmo dia");
clock.UtcNow = new DateTimeOffset(2026, 11, 12, 23, 59, 59, TimeSpan.Zero);
Check(sessions.TryGet(token, out _), "Sessão permanece válida até o fim do dia");
clock.UtcNow = clock.UtcNow.AddSeconds(1);
Check(!sessions.TryGet(token, out _), "Meia-noite expira sessão apesar de atividade recente");
var nextDayToken = sessions.Create(user);
Check(sessions.TryGet(nextDayToken, out _), "Novo login funciona no dia seguinte");
sessions.Remove(nextDayToken);
Check(!sessions.TryGet(nextDayToken, out _), "Sair revoga o acesso imediatamente");
Check(!sessions.TryGet("", out _) && !sessions.TryGet("INVALIDO", out _), "Tokens ausentes e inválidos são recusados");
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("OK: " + label); }
sealed class TestClock : TimeProvider {
    public DateTimeOffset UtcNow { get; set; }
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
