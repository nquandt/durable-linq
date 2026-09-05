using System.Net.Http.Json;

namespace DurableLinq.Examples.Tests.Support;

/// <summary>Small polling helper for waiting on eventually-consistent state in the examples under test.</summary>
public static class Poll
{
    public static async Task<T> UntilAsync<T>(Func<Task<T>> probe, Func<T, bool> isDone, TimeSpan timeout, TimeSpan? interval = null)
    {
        var delay = interval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTime.UtcNow + timeout;
        T last;
        do
        {
            last = await probe().ConfigureAwait(false);
            if (isDone(last)) return last;
            await Task.Delay(delay).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Condition not met within {timeout}. Last value: {last}");
    }

    /// <summary>
    /// GETs and deserializes JSON, returning <c>default</c> instead of throwing when the endpoint is not
    /// (yet) ready, e.g. a 404 while an instance is still being created asynchronously.
    /// </summary>
    public static async Task<T?> GetJsonOrDefaultAsync<T>(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
    }
}
