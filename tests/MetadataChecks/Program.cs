using System.Net;
using Microsoft.Extensions.Logging;
using Sws2Cs2MapManager.Contracts;
using Sws2Cs2RockTheVote.Configuration;
using Sws2Cs2RockTheVote.Services;

var map = new MapInfo("workshop:123", "kz_configured", MapKind.Workshop, "123");
var settings = new WorkshopMetadataSettings();
var checks = 0;

// Reproduce the reported exception chain. The deepest exception must not decide
// whether a request is an expected network failure.
var log = new CapturingLogger();
var broken = new FakeHandler((_, _) => throw new TaskCanceledException("HttpClient timeout",
    new TimeoutException("timeout", new IOException("read failed", new ObjectDisposedException("NetworkStream")))));
using (var provider = new WorkshopMapMetadataProvider(log, settings, broken))
{
    var many = Enumerable.Range(1, 205)
        .Select(id => new MapInfo($"workshop:{id}", $"kz_{id}", MapKind.Workshop, id.ToString())).ToArray();
    var fallback = await provider.GetAsync(many);
    Check(fallback.Count == 205 && fallback.Values.All(item => item.Title is null), "All configured maps remain available");
    Check(broken.Requests == 1, "Stop remaining batches after a transport failure");
    Check(log.Warnings.Count == 1 && log.Warnings[0].Exception is null, "Timeout emits one warning without a stack trace");
    await provider.GetAsync(many);
    Check(broken.Requests == 1, "Failure cooldown suppresses immediate retries");
}

// Exercise a real cancellation deadline through HttpClient, rather than just
// throwing a timeout-shaped exception from a fake response.
log = new CapturingLogger();
var stalled = new FakeHandler(async (_, token) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return new HttpResponseMessage(HttpStatusCode.OK);
});
using (var provider = new WorkshopMapMetadataProvider(log, new() { TimeoutSeconds = 1 }, stalled))
{
    var request = provider.GetAsync([map]);
    var snapshot = provider.GetCached([map]);
    Check(!request.IsCompleted && snapshot["123"].Title is null, "Cached menu data returns while Steam is stalled");
    await request.WaitAsync(TimeSpan.FromSeconds(5));
    Check(log.Warnings.Count == 1 && log.Warnings[0].Exception is null, "Request deadline falls back without throwing");
}

log = new CapturingLogger();
using (var provider = new WorkshopMapMetadataProvider(log, settings, new FakeHandler(async (_, token) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return new HttpResponseMessage(HttpStatusCode.OK);
})))
using (var shutdown = new CancellationTokenSource())
{
    var request = provider.GetAsync([map], shutdown.Token);
    shutdown.Cancel();
    try
    {
        await request;
        throw new InvalidOperationException("Shutdown cancellation was swallowed");
    }
    catch (OperationCanceledException)
    {
        Check(log.Warnings.Count == 0, "Plugin shutdown cancellation emits no warning");
    }
}

var successful = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(request.Method == HttpMethod.Post
        ? """{"response":{"publishedfiledetails":[{"publishedfileid":"123","title":"Workshop title","creator":"42","preview_url":"https://example.com/map.jpg"}]}}"""
        : "<profile><steamID>Map author</steamID></profile>")
}));
using (var provider = new WorkshopMapMetadataProvider(new CapturingLogger(), new() { Enabled = false }, successful))
{
    await provider.GetAsync([map]);
    Check(successful.Requests == 0, "Disabled metadata makes no HTTP requests");
    provider.ApplySettings(settings);
    var data = await provider.GetAsync([map]);
    Check(data["123"] == new WorkshopMapMetadata("Workshop title", "Map author", "https://example.com/map.jpg"), "Title, author and HTTPS preview are cached");
    await provider.GetAsync([map, map]);
    Check(successful.Requests == 2, "Cached maps do not trigger duplicate detail/profile requests");
    provider.ApplySettings(new() { Enabled = false });
    Check((await provider.GetAsync([map]))["123"].Author == "Map author", "Disabling metadata preserves already cached details");
}

Console.WriteLine($"Passed {checks} Workshop metadata regression checks.");

void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }

    checks++;
    Console.WriteLine($"PASS: {message}");
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return send(request, cancellationToken);
    }
}

sealed class CapturingLogger : ILogger
{
    public List<(Exception? Exception, string Message)> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => true;

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (level == LogLevel.Warning)
        {
            Warnings.Add((exception, formatter(state, exception)));
        }
    }
}
