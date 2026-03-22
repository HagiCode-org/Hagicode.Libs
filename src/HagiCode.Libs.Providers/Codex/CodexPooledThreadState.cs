namespace HagiCode.Libs.Providers.Codex;

internal sealed class CodexPooledThreadState : IAsyncDisposable
{
    public string? ThreadId { get; set; }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
