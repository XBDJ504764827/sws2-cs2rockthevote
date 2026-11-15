using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sws2Cs2MapManager.Contracts;
using Sws2Cs2RockTheVote.Configuration;

namespace Sws2Cs2RockTheVote.Services;

internal sealed record WorkshopMapMetadata(string? Title, string? Author, string? ImageUrl);

internal sealed class WorkshopMapMetadataProvider : IDisposable
{
    private const string DetailsEndpoint =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private static readonly TimeSpan RefreshLockWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly WorkshopMapMetadata EmptyMetadata = new(null, null, null);

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, WorkshopMapMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _authors = new(StringComparer.Ordinal);
    private DateTimeOffset _retryAfterUtc;
    private volatile WorkshopMetadataSettings _settings;

    public WorkshopMapMetadataProvider(
        ILogger logger,
        WorkshopMetadataSettings settings,
        HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "sws2-cs2rockthevote/1.0");
    }

    public void ApplySettings(WorkshopMetadataSettings settings) => _settings = settings;

    public IReadOnlyDictionary<string, WorkshopMapMetadata> GetCached(IEnumerable<MapInfo> maps)
    {
        return Snapshot(GetWorkshopIds(maps));
    }

    private static string[] GetWorkshopIds(IEnumerable<MapInfo> maps)
    {
        return maps.Where(map => map.Kind == MapKind.Workshop)
            .Select(map => map.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, WorkshopMapMetadata>> GetAsync(
        IEnumerable<MapInfo> maps,
        CancellationToken cancellationToken = default)
    {
        var ids = GetWorkshopIds(maps);
        if (!_settings.Enabled || ids.Length == 0)
        {
            return Snapshot(ids);
        }

        var lockTaken = await _refreshLock.WaitAsync(RefreshLockWait, cancellationToken);
        if (!lockTaken)
        {
            // A background warm-up or another menu is already fetching metadata. Do not block
            // the reservation menu on a slow Steam request; show cached/fallback values now.
            return Snapshot(ids);
        }

        try
        {
            string[] missing;
            bool shouldRefresh;
            lock (_sync)
            {
                missing = ids.Where(id => !_metadata.ContainsKey(id)).ToArray();
                shouldRefresh = _settings.Enabled && missing.Length > 0 && DateTimeOffset.UtcNow >= _retryAfterUtc;
            }

            if (shouldRefresh)
            {
                await RefreshAsync(missing, cancellationToken);
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        return Snapshot(ids);
    }

    private IReadOnlyDictionary<string, WorkshopMapMetadata> Snapshot(IEnumerable<string> ids)
    {
        lock (_sync)
        {
            return ids.ToDictionary(
                id => id,
                id => _metadata.TryGetValue(id, out var metadata) ? metadata : EmptyMetadata,
                StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task RefreshAsync(string[] ids, CancellationToken cancellationToken)
    {
        foreach (var batch in ids.Chunk(100))
        {
            if (!_settings.Enabled)
            {
                return;
            }

            try
            {
                using var requestTimeout = CreateRequestTimeout(cancellationToken);
                using var content = new FormUrlEncodedContent(
                    new[] { new KeyValuePair<string, string>("itemcount", batch.Length.ToString()) }
                        .Concat(batch.Select((id, index) =>
                            new KeyValuePair<string, string>($"publishedfileids[{index}]", id))));
                using var response = await _httpClient.PostAsync(DetailsEndpoint, content, requestTimeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestTimeout.Token);
                if (!TryGetProperty(document.RootElement, "response", out var responseElement)
                    || !TryGetProperty(responseElement, "publishedfiledetails", out var details)
                    || details.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Steam response does not contain publishedfiledetails.");
                }

                var entries = new List<(string Id, string? Title, string? Creator, string? ImageUrl)>();
                foreach (var item in details.EnumerateArray())
                {
                    var id = ReadString(item, "publishedfileid");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    entries.Add((
                        id,
                        ReadString(item, "title"),
                        ReadString(item, "creator"),
                        ReadHttpsUrl(item, "preview_url")));
                }

                lock (_sync)
                {
                    _retryAfterUtc = DateTimeOffset.MinValue;
                    foreach (var entry in entries)
                    {
                        _metadata[entry.Id] = new WorkshopMapMetadata(entry.Title, null, entry.ImageUrl);
                    }
                }

                using var profileGate = new SemaphoreSlim(4, 4);
                var resolved = await Task.WhenAll(entries.Select(async entry =>
                {
                    string? author = null;
                    var hasCachedAuthor = false;
                    if (!string.IsNullOrWhiteSpace(entry.Creator))
                    {
                        lock (_sync)
                        {
                            hasCachedAuthor = _authors.TryGetValue(entry.Creator, out author);
                        }

                        if (!hasCachedAuthor)
                        {
                            await profileGate.WaitAsync(cancellationToken);
                            try
                            {
                                author = await GetAuthorNameAsync(entry.Creator, cancellationToken);
                            }
                            finally
                            {
                                profileGate.Release();
                            }

                            lock (_sync)
                            {
                                _authors[entry.Creator] = author;
                            }
                        }
                    }

                    return (entry.Id, entry.Title, Author: author, entry.ImageUrl);
                }));

                lock (_sync)
                {
                    foreach (var entry in resolved)
                    {
                        _metadata[entry.Id] = new WorkshopMapMetadata(
                            entry.Title,
                            entry.Author,
                            entry.ImageUrl);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _retryAfterUtc = DateTimeOffset.UtcNow + RefreshRetryDelay;
                }

                if (IsNetworkFailure(exception))
                {
                    _logger.LogWarning(
                        "[sws2-cs2rockthevote] Steam Workshop details unavailable ({Reason}). Using configured maps/cached details; voting and map changes remain available. Retrying on a later request after 5 minutes.",
                        exception is OperationCanceledException ? "request timed out" : exception.Message);
                }
                else
                {
                    _logger.LogWarning(exception, "[sws2-cs2rockthevote] Failed to load Workshop map details");
                }

                // Steam is unavailable. Do not repeat the timeout for every remaining batch.
                return;
            }
        }
    }

    private async Task<string?> GetAuthorNameAsync(string steamId, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return null;
            }

            using var requestTimeout = CreateRequestTimeout(cancellationToken);
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}/?xml=1";
            using var response = await _httpClient.GetAsync(url, requestTimeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, requestTimeout.Token);
            return document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("steamID", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "[sws2-cs2rockthevote] Failed to resolve Workshop creator {SteamId}", steamId);
            return null;
        }
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 120)));
        return timeout;
    }

    private static bool IsNetworkFailure(Exception exception)
    {
        // HttpClient timeouts can end in ObjectDisposedException inside SslStream.
        // Inspect the entire chain so a transport shutdown is not treated as an unknown error.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or TimeoutException or OperationCanceledException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? property.ToString()
                : null;
    }

    private static string? ReadHttpsUrl(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
