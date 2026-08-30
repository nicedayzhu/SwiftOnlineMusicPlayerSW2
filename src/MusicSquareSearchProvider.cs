using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SwiftOnlineMusicPlayerSW2;

internal sealed class MusicSquareSearchProvider : IDisposable
{
    private const int MaxResponseBytes = 512 * 1024;
    private readonly HttpClient _httpClient;

    public MusicSquareSearchProvider()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SwiftOnlineMusicPlayerSW2", "0.4.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<MusicTrackConfig>> SearchAsync(
        string query,
        MusicSquareSearchConfig config,
        CancellationToken cancellationToken)
    {
        Exception? kuwoFailure = null;
        if (config.KuwoEnabled)
        {
            try
            {
                var kuwoTracks = await SearchKuwoAsync(query, config, cancellationToken)
                    .ConfigureAwait(false);
                if (kuwoTracks.Count > 0)
                {
                    return kuwoTracks;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                kuwoFailure = exception;
            }
        }

        try
        {
            return await SearchNeteaseAsync(query, config, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception neteaseFailure) when (kuwoFailure is not null)
        {
            throw new AggregateException(
                "Both the Kuwo primary provider and Netease fallback failed.",
                kuwoFailure,
                neteaseFailure);
        }
    }

    private async Task<IReadOnlyList<MusicTrackConfig>> SearchKuwoAsync(
        string query,
        MusicSquareSearchConfig config,
        CancellationToken cancellationToken)
    {
        var endpoint = ParseEndpoint(config.KuwoSearchEndpoint, "Kuwo");
        await EnsurePublicEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);

        var candidateLimit = Math.Clamp(config.ResultLimit * 3, config.ResultLimit, 30);
        var searchUri = AppendQuery(endpoint,
            ("msg", query),
            ("page", "1"),
            ("limit", candidateLimit.ToString()));
        using var document = await GetJsonAsync(searchUri, "Kuwo search", cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Kuwo search response does not contain a result array.");
        }

        var candidates = new List<SearchCandidate>();
        var originalIndex = 0;
        foreach (var item in data.EnumerateArray())
        {
            originalIndex++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            _ = TryReadText(item, "song", out var title);
            _ = TryReadText(item, "singer", out var artist);
            _ = TryReadText(item, "rid", out var sourceId);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            {
                continue;
            }

            candidates.Add(new SearchCandidate(
                originalIndex,
                NormalizeText(title, "Untitled", 80),
                NormalizeText(artist, "Unknown artist", 80),
                NormalizeText(sourceId, string.Empty, 96),
                ScoreCandidate(query, title, artist)));
        }

        var tracks = new List<MusicTrackConfig>();
        foreach (var candidate in candidates
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.OriginalIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detailUri = AppendQuery(endpoint,
                ("msg", query),
                ("n", candidate.OriginalIndex.ToString()),
                ("br", config.KuwoQualityIndex.ToString()));
            using var detailDocument = await GetJsonAsync(detailUri, "Kuwo track detail", cancellationToken)
                .ConfigureAwait(false);
            if (!TryParseKuwoTrack(detailDocument.RootElement, candidate, config, out var track))
            {
                continue;
            }

            tracks.Add(track);
            if (tracks.Count >= config.ResultLimit)
            {
                break;
            }
        }

        return tracks;
    }

    private async Task<IReadOnlyList<MusicTrackConfig>> SearchNeteaseAsync(
        string query,
        MusicSquareSearchConfig config,
        CancellationToken cancellationToken)
    {
        var endpoint = ParseEndpoint(config.SearchEndpoint, "Netease");
        await EnsurePublicEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var candidateLimit = Math.Clamp(config.ResultLimit * 3, config.ResultLimit, 30);
        var requestUri = AppendQuery(endpoint,
            ("type", "search"),
            ("id", query),
            ("limit", candidateLimit.ToString()),
            ("server", "netease"));
        using var document = await GetJsonAsync(requestUri, "Netease search", cancellationToken)
            .ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Netease search response is not a JSON array.");
        }

        var candidates = new List<(MusicTrackConfig Track, int Score, int Index)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object ||
                !TryReadText(item, "url", out var urlText) ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var audioUri) ||
                !IsAllowedAudioUri(audioUri, config.AllowedAudioHostSuffixes) ||
                !seenUrls.Add(audioUri.AbsoluteUri))
            {
                continue;
            }

            _ = TryReadText(item, "name", out var title);
            _ = TryReadText(item, "artist", out var artist);
            candidates.Add((
                new MusicTrackConfig
                {
                    Title = NormalizeText(title, "Untitled", 80),
                    Artist = NormalizeText(artist, "Unknown artist", 80),
                    Url = audioUri.AbsoluteUri,
                    DurationSeconds = 0,
                    Source = "Netease",
                    SourceId = NormalizeText(ReadQueryValue(audioUri, "id"), string.Empty, 96)
                },
                ScoreCandidate(query, title, artist),
                index));
        }

        return candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(config.ResultLimit)
            .Select(item => item.Track)
            .ToList();
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<JsonDocument> GetJsonAsync(
        Uri requestUri,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException($"{operation} returned an unexpected redirect.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException($"{operation} response is too large.");
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var limitedStream = new SizeLimitedReadStream(responseStream, MaxResponseBytes);
        return await JsonDocument.ParseAsync(
            limitedStream,
            new JsonDocumentOptions { MaxDepth = 20 },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseKuwoTrack(
        JsonElement root,
        SearchCandidate candidate,
        MusicSquareSearchConfig config,
        out MusicTrackConfig track)
    {
        track = new MusicTrackConfig();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !TryReadText(data, "url", out var urlText) ||
            !Uri.TryCreate(urlText, UriKind.Absolute, out var audioUri) ||
            !IsAllowedAudioUri(audioUri, config.AllowedAudioHostSuffixes))
        {
            return false;
        }

        _ = TryReadText(data, "song", out var title);
        _ = TryReadText(data, "singer", out var artist);
        _ = TryReadText(data, "rid", out var sourceId);
        if (!SameCandidate(candidate, title, artist))
        {
            return false;
        }

        track = new MusicTrackConfig
        {
            Title = NormalizeText(title, candidate.Title, 80),
            Artist = NormalizeText(artist, candidate.Artist, 80),
            Url = audioUri.AbsoluteUri,
            DurationSeconds = TryReadInt(data, "time", out var duration)
                ? Math.Clamp(duration, 0, 86_400)
                : 0,
            Source = "Kuwo",
            SourceId = NormalizeText(sourceId, candidate.SourceId, 96)
        };
        return true;
    }

    private static bool SameCandidate(SearchCandidate candidate, string title, string artist)
    {
        var candidateTitle = NormalizeForMatch(candidate.Title);
        var candidateArtist = NormalizeForMatch(candidate.Artist);
        var detailTitle = NormalizeForMatch(title);
        var detailArtist = NormalizeForMatch(artist);
        return candidateTitle == detailTitle &&
               (candidateArtist == detailArtist ||
                candidateArtist.Contains(detailArtist, StringComparison.Ordinal) ||
                detailArtist.Contains(candidateArtist, StringComparison.Ordinal));
    }

    private static readonly string[] VariantMarkers =
    [
        "live", "remix", "demo", "cover", "伴奏", "翻唱", "柔情版", "3d", "环绕",
        "montagem", "童声", "儿歌", "女声", "男声", "男生", "女生", "吉他版", "钢琴版",
        "现场", "原唱", "dj", "mix", "speed", "slowed", "低音", "加速", "变调"
    ];

    private static int ScoreCandidate(string query, string title, string artist)
    {
        var normalizedQuery = NormalizeForMatch(query);
        var normalizedTitle = NormalizeForMatch(title);
        var normalizedArtist = NormalizeForMatch(artist);
        var score = 0;
        if (normalizedQuery == normalizedTitle + normalizedArtist ||
            normalizedQuery == normalizedArtist + normalizedTitle)
        {
            score += 2000;
        }
        if (normalizedQuery == normalizedTitle)
        {
            score += 1200;
        }

        var tokens = query.Split([' ', '\t', '-', '—', '/', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeForMatch)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var token in tokens)
        {
            if (normalizedTitle == token)
            {
                score += 500;
            }
            else if (normalizedTitle.Contains(token, StringComparison.Ordinal))
            {
                score += 220;
            }
            if (normalizedArtist == token)
            {
                score += 450;
            }
            else if (normalizedArtist.Contains(token, StringComparison.Ordinal))
            {
                score += 180;
            }
        }

        var queryMarkerCount = CountMarkers(query);
        var titleArtistMarkerCount = CountMarkers(title) + CountMarkers(artist);
        var unmatchedVariants = Math.Max(0, titleArtistMarkerCount - queryMarkerCount);
        score -= Math.Min(1600, unmatchedVariants * 450);

        if (IsCanonicalResult(normalizedQuery, normalizedTitle, artist))
        {
            score += 400;
        }

        if (artist.Contains("-/", StringComparison.Ordinal))
        {
            score -= 500;
        }
        if (artist.Contains('&'))
        {
            score -= 220;
        }
        return score;
    }

    private static int CountMarkers(string text)
    {
        var lower = text.ToLowerInvariant();
        var count = 0;
        foreach (var marker in VariantMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static bool IsCanonicalResult(string normalizedQuery, string normalizedTitle, string rawArtist)
    {
        if (normalizedQuery != normalizedTitle || string.IsNullOrWhiteSpace(rawArtist))
        {
            return false;
        }

        if (rawArtist.Contains('/') ||
            rawArtist.Contains('&') ||
            CountMarkers(rawArtist) > 0)
        {
            return false;
        }

        return true;
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static bool TryReadText(JsonElement item, string propertyName, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()?.Trim() ?? string.Empty;
            return value.Length > 0;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        value = string.Join(", ", property.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        return value.Length > 0;
    }

    private static bool TryReadInt(JsonElement item, string propertyName, out int value)
    {
        value = 0;
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return false;
        }
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt32(out value)
            : property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value);
    }

    private static Uri ParseEndpoint(string endpointText, string providerName)
    {
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"{providerName} search endpoint is invalid.");
        }
        return endpoint;
    }

    private static Uri AppendQuery(Uri endpoint, params (string Name, string Value)[] values)
    {
        var builder = new StringBuilder(endpoint.AbsoluteUri);
        builder.Append(string.IsNullOrEmpty(endpoint.Query) ? '?' : '&');
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('&');
            }
            builder.Append(Uri.EscapeDataString(values[index].Name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(values[index].Value));
        }
        return new Uri(builder.ToString());
    }

    private static string ReadQueryValue(Uri uri, string name)
    {
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var key = separator >= 0 ? item[..separator] : item;
            if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return separator >= 0 ? Uri.UnescapeDataString(item[(separator + 1)..]) : string.Empty;
        }
        return string.Empty;
    }

    private static bool IsAllowedAudioUri(Uri uri, IReadOnlyCollection<string> allowedHostSuffixes)
    {
        if ((uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.IsLoopback ||
            IsPrivateLiteralAddress(uri.Host))
        {
            return false;
        }

        if (allowedHostSuffixes.Count == 0)
        {
            return true;
        }

        return allowedHostSuffixes.Any(suffix =>
            uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task EnsurePublicEndpointAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if ((endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp) ||
            endpoint.IsLoopback ||
            string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new InvalidOperationException("Music search endpoint must be a public HTTP(S) URL.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("Music search endpoint DNS lookup failed.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new InvalidOperationException("Music search endpoint resolved to a private or invalid address.");
        }
    }

    private static bool IsPrivateLiteralAddress(string host) =>
        IPAddress.TryParse(host, out var address) && IsPrivateAddress(address);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10 or 127 ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                   bytes[0] >= 224;
        }

        return address.Equals(IPAddress.IPv6Any) ||
               address.Equals(IPAddress.IPv6None) ||
               address.Equals(IPAddress.IPv6Loopback) ||
               address.IsIPv6LinkLocal ||
               (bytes[0] & 0xFE) == 0xFC ||
               bytes[0] == 0xFF;
    }

    private static string NormalizeText(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private sealed record SearchCandidate(
        int OriginalIndex,
        string Title,
        string Artist,
        string SourceId,
        int Score);

    private sealed class SizeLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            CountBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            CountBytes(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void CountBytes(int count)
        {
            _bytesRead += count;
            if (_bytesRead > maximumBytes)
            {
                throw new InvalidDataException("Music search response exceeded the size limit.");
            }
        }
    }
}
