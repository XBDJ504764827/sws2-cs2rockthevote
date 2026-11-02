using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sws2Cs2MapManager.Contracts;

namespace Sws2Cs2RockTheVote.Services;

internal sealed record WorkshopMapMetadata(string? Title, string? Author, string? ImageUrl);

internal sealed class WorkshopMapMetadataProvider : IDisposable
{
    private const string DetailsEndpoint =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, WorkshopMapMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _authors = new(StringComparer.Ordinal);

    public WorkshopMapMetadataProvider(ILogger logger)
    {
        _logger = logger;
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "sws2-cs2rockthevote/1.0");
    }

    public async Task<IReadOnlyDictionary<string, WorkshopMapMetadata>> GetAsync(
        IEnumerable<MapInfo> maps,
        CancellationToken cancellationToken = default)
    {
        var ids = maps
            .Where(map => map.Kind == MapKind.Workshop)
            .Select(map => map.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            string[] missing;
            lock (_sync)
            {
                missing = ids.Where(id => !_metadata.ContainsKey(id)).ToArray();
                foreach (var id in missing)
                {
                    _metadata[id] = new WorkshopMapMetadata(null, null, null);
                }
            }

            if (missing.Length > 0)
            {
                await RefreshAsync(missing, cancellationToken);
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        lock (_sync)
        {
            return ids.ToDictionary(id => id, id => _metadata[id], StringComparer.Ordinal);
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
            try
            {
                using var content = new FormUrlEncodedContent(
                    new[] { new KeyValuePair<string, string>("itemcount", batch.Length.ToString()) }
                        .Concat(batch.Select((id, index) =>
                            new KeyValuePair<string, string>($"publishedfileids[{index}]", id))));
                using var response = await _httpClient.PostAsync(DetailsEndpoint, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!TryGetProperty(document.RootElement, "response", out var responseElement)
                    || !TryGetProperty(responseElement, "publishedfiledetails", out var details)
                    || details.ValueKind != JsonValueKind.Array)
                {
                    continue;
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
                _logger.LogWarning(exception, "[sws2-cs2rockthevote] Failed to load Workshop map details");
            }
        }
    }

    private async Task<string?> GetAuthorNameAsync(string steamId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}/?xml=1";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
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
