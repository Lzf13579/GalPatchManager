namespace GalPatchManager.Services;

/// <summary>
/// 简单重试器（参考 PCL 的 Retrier 思路：失败后按次数重试，退避递增）。
/// </summary>
public static class Retrier
{
    public const int DefaultMaxAttempts = 3;

    public static async Task<T> RunAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = DefaultMaxAttempts,
        int baseDelayMs = 400,
        Action<int, Exception>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (maxAttempts < 1) maxAttempts = 1;

        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= maxAttempts) break;

                onRetry?.Invoke(attempt, ex);
                await Task.Delay(baseDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("重试失败但没有捕获到异常。");
    }

    public static async Task RunAsync(
        Func<Task> action,
        int maxAttempts = DefaultMaxAttempts,
        int baseDelayMs = 400,
        Action<int, Exception>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        await RunAsync<object?>(
            async () =>
            {
                await action().ConfigureAwait(false);
                return null;
            },
            maxAttempts,
            baseDelayMs,
            onRetry,
            cancellationToken).ConfigureAwait(false);
    }
}
