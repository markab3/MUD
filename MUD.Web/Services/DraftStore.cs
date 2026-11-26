using System.Text.Json;
using Microsoft.JSInterop;

namespace MUD.Web.Services;

// Local draft of an editor form in the browser's localStorage (WEB_BLAZOR.md 6.4), so a lost circuit does not lose input.
// Never used for passwords. Key: draft:<user>:<route>. The browser side drops a draft after 24 hours. Failures are swallowed:
// a draft is a convenience, never a reason to break the page.
public sealed class DraftStore
{
    private readonly IJSRuntime _js;

    public DraftStore(IJSRuntime js) { _js = js; }

    public static string KeyFor(string user, string route) => "draft:" + user + ":" + route;

    public async Task SaveAsync<T>(string key, T value)
    {
        try { await _js.InvokeVoidAsync("mudDraft.set", key, JsonSerializer.Serialize(value)); }
        catch (Exception e) when (e is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException) { }
    }

    public async Task<T?> LoadAsync<T>(string key)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("mudDraft.get", key);
            return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException or JsonException) { return default; }
    }

    public async Task RemoveAsync(string key)
    {
        try { await _js.InvokeVoidAsync("mudDraft.remove", key); }
        catch (Exception e) when (e is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException) { }
    }
}
