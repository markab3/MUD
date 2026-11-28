namespace MUD.Web.Services;

// Debounce and latest-wins for a filter box that applies as you type (WEB_BLAZOR.md "Live filters"). A plain class, free of the renderer.
// Input(text) restarts a quiet period; when it settles, Apply runs once for the settled text. Apply does the work (an rpc, or a pass over
// the loaded list) and returns the action that publishes the result. The runner invokes that action only if no newer input arrived meanwhile,
// so a response that comes back after a later keystroke is dropped, and the older request's token is cancelled as soon as the newer input starts.
public sealed class LiveFilterRunner : IDisposable
{
    public const int DefaultDelayMs = 250;

    public delegate Task Delayer(TimeSpan delay, CancellationToken cancellation);

    private readonly TimeSpan _delay;
    private readonly Func<string, CancellationToken, Task<Action?>> _apply;
    private readonly Func<Func<Task>, Task> _marshal;
    private readonly Delayer _delayer;
    private readonly Action? _changed;
    private CancellationTokenSource? _cts;
    private int _generation;

    // marshal runs the publish step on the component's dispatcher (InvokeAsync); the default runs it inline. changed is raised when Busy or Pending flips.
    public LiveFilterRunner(Func<string, CancellationToken, Task<Action?>> apply, TimeSpan? delay = null, Func<Func<Task>, Task>? marshal = null, Delayer? delayer = null, Action? changed = null)
    {
        _apply = apply;
        _delay = delay ?? TimeSpan.FromMilliseconds(DefaultDelayMs);
        _marshal = marshal ?? (work => work());
        _delayer = delayer ?? Task.Delay;
        _changed = changed;
    }

    // True while Apply is running for the newest input.
    public bool Busy { get; private set; }

    // True from an input until its result is published (or a newer input takes over).
    public bool Pending { get; private set; }

    // A keystroke: apply once the text has been quiet for the delay. The returned task ends when this input's run ends (tests await it).
    public Task Input(string text) => Start(text, _delay);

    // Apply now (the clear button): no quiet period, same latest-wins rules.
    public Task Immediate(string text) => Start(text, TimeSpan.Zero);

    public void Cancel()
    {
        _generation++;
        _cts?.Cancel();
        var changed = Busy || Pending;
        Busy = false;
        Pending = false;
        if (changed) { _changed?.Invoke(); }
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private Task Start(string text, TimeSpan delay)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var generation = ++_generation;
        var wasQuiet = !Pending;
        Pending = true;
        if (wasQuiet) { _changed?.Invoke(); }
        return Run(text, delay, generation, _cts.Token);
    }

    private bool IsCurrent(int generation) => generation == _generation;

    private async Task Run(string text, TimeSpan delay, int generation, CancellationToken cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero) { await _delayer(delay, cancellation); }
            if (!IsCurrent(generation)) { return; }
            Busy = true;
            _changed?.Invoke();
            Action? publish = null;
            try { publish = await _apply(text, cancellation); }
            catch (OperationCanceledException) { }
            catch (Exception) { publish = null; }
            await _marshal(() =>
            {
                if (IsCurrent(generation))
                {
                    publish?.Invoke();
                    Busy = false;
                    Pending = false;
                    _changed?.Invoke();
                }
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
    }

    // "12 players", "1 player": the live-region result count.
    public static string Count(int count, string singular, string? plural = null) =>
        count + " " + (count == 1 ? singular : plural ?? singular + "s");

    // What a settled box value becomes: trimmed and cut to the box's length limit (the same rules as the old submit handlers).
    public static string Clean(string? text, int maxLength)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length > maxLength ? value.Substring(0, maxLength) : value;
    }
}

// The address a live filter writes with NavigateTo(..., replace: true) comes back as a navigation to the same page. Each pushed address is
// remembered once; the page skips reloading when its parameters are exactly one of those echoes, and reloads for any other navigation
// (links, back button), which also forgets the pending echoes.
public sealed class LiveUrlEchoes
{
    private readonly List<string> _expected = new();

    public void Expect(string url) => _expected.Add(url);

    // True when url is an echo of an address this page pushed (consumed); otherwise forgets every pending echo and returns false.
    public bool Consume(string url)
    {
        var index = _expected.IndexOf(url);
        if (index < 0) { _expected.Clear(); return false; }
        _expected.RemoveAt(index);
        return true;
    }
}
