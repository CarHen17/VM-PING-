using System.Net.Http.Json;
namespace VmPing.Core;

public static class TelegramClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static async Task SendAsync(string token, string chatId, string text, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) throw new InvalidOperationException("Informe o token do bot e o chat ID.");
        using var response = await Http.PostAsJsonAsync($"https://api.telegram.org/bot{token.Trim()}/sendMessage",
            new { chat_id = chatId.Trim(), text, disable_web_page_preview = true }, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram respondeu {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellation)}");
    }
}
// Groups status changes that happen close together (a POP going down takes many hosts with it) into one message.
public sealed class AlertBatcher(Func<string, Task> send, TimeSpan window)
{
    private readonly Lock gate = new();
    private readonly List<string> pending = [];
    private Task? flushing;
    public event Action<Exception>? Failed;
    public void Add(string line)
    {
        lock (gate)
        {
            pending.Add(line);
            flushing ??= FlushLaterAsync();
        }
    }
    private async Task FlushLaterAsync()
    {
        await Task.Delay(window);
        string[] lines;
        lock (gate) { lines = [.. pending]; pending.Clear(); flushing = null; }
        if (lines.Length == 0) return;
        const int limit = 40;
        var text = string.Join("\n", lines.Take(limit)) + (lines.Length > limit ? $"\n… e mais {lines.Length - limit} eventos" : "");
        try { await send(text); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
    public Task Flush() { lock (gate) return flushing ?? Task.CompletedTask; }
}
