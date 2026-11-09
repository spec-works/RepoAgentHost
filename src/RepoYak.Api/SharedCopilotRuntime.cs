using GitHub.Copilot;

namespace RepoYak.Api;

public sealed class SharedCopilotRuntime(ILogger<SharedCopilotRuntime> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public async Task<CopilotClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null)
            {
                return _client;
            }
            var client = new CopilotClient();
            try
            {
                await client.StartAsync();
                cancellationToken.ThrowIfCancellationRequested();
                _client = client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
            logger.LogInformation("Started the host's shared Copilot runtime");
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
