using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftOnlineMusicPlayerSW2;

internal sealed record LyricLine(TimeSpan Timestamp, string Text);

internal sealed class MusicLyricsProvider : IDisposable
{
    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxLyricLines = 2_000;
    private static readonly Regex LrcTimestampPattern = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{1,2}(?:\.\d{1,3})?)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LrcOffsetPattern = new(
        @"\[offset:(?<milliseconds>[+-]?\d{1,7})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;

    public MusicLyricsProvider()
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
    }

    public async Task<IReadOnlyList<LyricLine>> FetchAsync(
        MusicTrackConfig track,
        LyricsConfig config,
        CancellationToken cancellationToken)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(track.SourceId))
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        if (track.Source.Equals("Kuwo", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchKuwoAsync(track.SourceId, config, timeout.Token).ConfigureAwait(false);
        }

        if (track.Source.Equals("Netease", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchNeteaseAsync(track.SourceId, config, timeout.Token).ConfigureAwait(false);
        }

        return [];
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<IReadOnlyList<LyricLine>> FetchKuwoAsync(
        string sourceId,
        LyricsConfig config,
        CancellationToken cancellationToken)
    {
        var musicId = NormalizeNumericId(sourceId);
        if (musicId.Length == 0 || !TryParseEndpoint(config.KuwoEndpoint, out var endpoint))
        {
            return [];
        }

        var requestUri = AppendQuery(endpoint, ("musicId", musicId));
        var body = await GetBodyAsync(requestUri, useKuwoReferer: true, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 20 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("lrclist", out var lyricList) ||
            lyricList.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<LyricLine>();
        foreach (var item in lyricList.EnumerateArray())
        {
            if (lines.Count >= MaxLyricLines ||
                item.ValueKind != JsonValueKind.Object ||
                !TryReadText(item, "lineLyric", out var text) ||
                !TryReadText(item, "time", out var timestampText) ||
                !double.TryParse(timestampText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                seconds < 0 || seconds > 86_400)
            {
                continue;
            }

            lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), text));
        }

        return NormalizeLines(lines);
    }

    private async Task<IReadOnlyList<LyricLine>> FetchNeteaseAsync(
        string sourceId,
        LyricsConfig config,
        CancellationToken cancellationToken)
    {
        var musicId = NormalizeNumericId(sourceId);
        if (musicId.Length == 0 || !TryParseEndpoint(config.NeteaseEndpoint, out var endpoint))
        {
            return [];
        }

        var requestUri = AppendQuery(
            endpoint,
            ("server", "netease"),
            ("type", "lrc"),
            ("id", musicId));
        var body = await GetBodyAsync(requestUri, useKuwoReferer: false, cancellationToken)
            .ConfigureAwait(false);
        return ParseLrc(body);
    }

    private async Task<string> GetBodyAsync(
        Uri requestUri,
        bool useKuwoReferer,
        CancellationToken cancellationToken)
    {
        await EnsurePublicEndpointAsync(requestUri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        if (useKuwoReferer)
        {
            request.Headers.Referrer = new Uri("https://www.kuwo.cn/");
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("Lyrics endpoint returned an unexpected redirect.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("Lyrics response is too large.");
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var limitedStream = new SizeLimitedReadStream(responseStream, MaxResponseBytes);
        using var reader = new StreamReader(
            limitedStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<LyricLine> ParseLrc(string source)
    {
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        var offsetMatch = LrcOffsetPattern.Match(source);
        if (offsetMatch.Success &&
            int.TryParse(
                offsetMatch.Groups["milliseconds"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var offsetMilliseconds))
        {
            offset = TimeSpan.FromMilliseconds(Math.Clamp(offsetMilliseconds, -30_000, 30_000));
        }

        foreach (var rawLine in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (lines.Count >= MaxLyricLines)
            {
                break;
            }

            var matches = LrcTimestampPattern.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var textStart = matches[^1].Index + matches[^1].Length;
            var text = SanitizeLine(rawLine[textStart..]);
            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["minutes"].Value, out var minutes) ||
                    !double.TryParse(
                        match.Groups["seconds"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds))
                {
                    continue;
                }

                var totalSeconds = minutes * 60d + seconds;
                var timestamp = TimeSpan.FromSeconds(totalSeconds) + offset;
                if (timestamp >= TimeSpan.Zero && timestamp <= TimeSpan.FromDays(1))
                {
                    lines.Add(new LyricLine(timestamp, text));
                }
            }
        }

        return NormalizeLines(lines);
    }

    private static IReadOnlyList<LyricLine> NormalizeLines(IEnumerable<LyricLine> source) =>
        source
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.Timestamp)
            .GroupBy(line => line.Timestamp)
            .Select(group => new LyricLine(
                group.Key,
                SanitizeLine(string.Join(" / ", group
                    .Select(line => line.Text)
                    .Distinct(StringComparer.Ordinal)))))
            .Where(line => line.Text.Length > 0)
            .Take(MaxLyricLines)
            .ToArray();

    private static bool TryReadText(JsonElement item, string propertyName, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
        value = SanitizeLine(value);
        return value.Length > 0;
    }

    private static string SanitizeLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        var text = new string(decoded.Where(character => !char.IsControl(character)).ToArray()).Trim();
        text = WhitespacePattern.Replace(text, " ");
        return text.Length <= 180 ? text : text[..180];
    }

    private static string NormalizeNumericId(string sourceId)
    {
        var value = sourceId.Trim();
        var separator = value.LastIndexOf('_');
        if (separator >= 0 && separator + 1 < value.Length)
        {
            value = value[(separator + 1)..];
        }

        return value.Length is > 0 and <= 32 && value.All(char.IsDigit)
            ? value
            : string.Empty;
    }

    private static bool TryParseEndpoint(string endpointText, out Uri endpoint) =>
        Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint!) &&
        (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp);

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

    private static async Task EnsurePublicEndpointAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if ((endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            endpoint.IsLoopback ||
            IsPrivateLiteralAddress(endpoint.Host))
        {
            throw new InvalidOperationException("Lyrics endpoint must be a public HTTP(S) address.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new HttpRequestException("Lyrics endpoint DNS lookup failed.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new InvalidOperationException("Lyrics endpoint resolved to a private or invalid address.");
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
                throw new InvalidDataException("Lyrics response exceeded the size limit.");
            }
        }
    }
}
