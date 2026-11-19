using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Google Calendar read through the Calendar API with the user's own OAuth client (Desktop app type).</summary>
public sealed class GoogleCalendarConfig
{
    /// <summary>Read events from Google Calendar (needs a client and a completed sign-in).</summary>
    public bool Enabled { get; set; }
    /// <summary>OAuth client of type "Desktop app" from the user's own Google Cloud project.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>The Desktop client's secret. Google treats it as non-confidential for installed apps.</summary>
    public string ClientSecret { get; set; } = "";
    /// <summary>Calendar ids to read; "primary" is the signed-in account's own calendar.</summary>
    public List<string> CalendarIds { get; set; } = ["primary"];
    public string Name { get; set; } = "Google";
    public string? Color { get; set; }
}

/// <summary>
/// Sign-in (authorization code with PKCE and a loopback redirect to this agent) and event reads for Google Calendar.
/// For calendars whose "secret address in iCal format" is turned off, as Google Workspace admins often do.
/// The tokens stay on this computer, in a file next to the settings readable only by this user, never in config.json
/// (the settings API returns that whole file).
/// </summary>
public sealed class GoogleCalendar
{
    public const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string ApiBase = "https://www.googleapis.com/calendar/v3/calendars/";

    internal sealed record Token(string ClientId, string Access, string Refresh, long Expires);
    private sealed record Pending(string State, string Verifier, string ClientId, long Expires);

    private readonly ConfigStore _config;
    private readonly AgentEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Lock _gate = new();
    private Pending? _pending;
    private Token? _token;
    private bool _loaded;
    private long _generation;
    private volatile string? _error;

    public GoogleCalendar(ConfigStore config, AgentEndpoint endpoint) : this(config, endpoint,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }) { }
    internal GoogleCalendar(ConfigStore config, AgentEndpoint endpoint, HttpClient http) { _config = config; _endpoint = endpoint; _http = http; }

    internal string CredentialPath => _config.Path + ".google-calendar.json";
    public string RedirectUri => $"http://127.0.0.1:{_endpoint.Port}/api/google-calendar/callback";
    /// <summary>Changes on every connect and disconnect, so the calendar monitor refreshes right away.</summary>
    public long Generation => Interlocked.Read(ref _generation);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private GoogleCalendarConfig Cfg => _config.Get().GoogleCalendar;

    // ---------------------------------------------------------------- sign-in

    public string Begin()
    {
        var c = Cfg;
        if (string.IsNullOrWhiteSpace(c.ClientId) || c.ClientId.Length > 256) throw new InvalidOperationException("Save a Google OAuth Client ID first");
        if (string.IsNullOrWhiteSpace(c.ClientSecret)) throw new InvalidOperationException("Save the Google OAuth client secret first");
        var pending = new Pending(Base64Url(RandomNumberGenerator.GetBytes(32)), Base64Url(RandomNumberGenerator.GetBytes(64)), c.ClientId.Trim(), Now + 600_000);
        lock (_gate) _pending = pending;
        var query = new Dictionary<string, string>
        {
            ["client_id"] = pending.ClientId, ["redirect_uri"] = RedirectUri, ["response_type"] = "code", ["scope"] = Scope,
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = pending.State,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pending.Verifier))), ["code_challenge_method"] = "S256",
        };
        return AuthUrl + "?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    public bool HasPendingState(string? state)
    {
        lock (_gate) return !string.IsNullOrEmpty(state) && _pending is { } p && p.Expires > Now
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(p.State));
    }

    public async Task<bool> Complete(string state, string code, CancellationToken ct)
    {
        Pending pending;
        lock (_gate) { if (!HasPendingState(state) || code.Length is 0 or > 4096) return false; pending = _pending!; _pending = null; }
        var c = Cfg;
        if (pending.ClientId != c.ClientId.Trim()) return false;
        await _tokenGate.WaitAsync(ct);
        try
        {
            var token = await TokenRequest(new()
            {
                ["client_id"] = pending.ClientId, ["client_secret"] = c.ClientSecret.Trim(), ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = pending.Verifier,
            }, pending.ClientId, "", ct);
            if (token.Refresh.Length == 0) throw new IOException("Google returned no refresh token; disconnect and connect again");
            Save(token);
            _error = null;
            Interlocked.Increment(ref _generation);
            return true;
        }
        finally { _tokenGate.Release(); }
    }

    public async Task Disconnect(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            lock (_gate) _pending = null;
            var old = LoadToken();
            _token = null;
            _loaded = true;
            if (File.Exists(CredentialPath)) File.Delete(CredentialPath);
            Interlocked.Increment(ref _generation);
            // best effort: tell Google to forget the grant too
            if (old is { Refresh.Length: > 0 })
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, RevokeUrl) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = old.Refresh }) };
                    using var _ = await _http.SendAsync(req, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        finally { _tokenGate.Release(); }
    }

    public object Status()
    {
        var c = Cfg;
        var token = LoadToken();
        var connected = token is { Refresh.Length: > 0 } && token.ClientId == c.ClientId.Trim();
        return new { configured = c.ClientId.Length > 0 && c.ClientSecret.Length > 0, connected, enabled = c.Enabled, error = _error, redirectUri = RedirectUri };
    }

    /// <summary>True when a sign-in for the configured client is stored.</summary>
    public bool Connected => LoadToken() is { Refresh.Length: > 0 } t && t.ClientId == Cfg.ClientId.Trim();

    // ---------------------------------------------------------------- tokens

    private async Task<Token> TokenRequest(Dictionary<string, string> fields, string client, string oldRefresh, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(fields) };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = "";
            try { using var e = JsonDocument.Parse(body); reason = e.RootElement.TryGetProperty("error", out var r) ? r.GetString() ?? "" : ""; }
            catch (JsonException) { }
            if (reason == "invalid_grant") throw new UnauthorizedAccessException("Google sign-in expired or was revoked; connect again in settings");
            throw new IOException($"Google sign-in failed ({(int)response.StatusCode}{(reason.Length > 0 ? ", " + reason : "")})");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(access)) throw new IOException("Google returned no access token");
        var refresh = root.TryGetProperty("refresh_token", out var r2) && r2.GetString() is { Length: > 0 } next ? next : oldRefresh;
        var seconds = root.TryGetProperty("expires_in", out var e2) && e2.TryGetInt32(out var v) ? Math.Clamp(v, 60, 86400) : 3600;
        return new(client, access, refresh, Now + seconds * 1000L);
    }

    private Token? LoadToken()
    {
        if (_loaded) return _token;
        lock (_gate)
        {
            if (_loaded) return _token;
            try { if (File.Exists(CredentialPath)) _token = JsonSerializer.Deserialize<Token>(File.ReadAllText(CredentialPath)); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _token = null; }
            _loaded = true;
            return _token;
        }
    }

    private void Save(Token token)
    {
        var temporary = CredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialPath)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var file = new FileStream(temporary, options)) { JsonSerializer.Serialize(file, token); file.Flush(flushToDisk: true); }
            File.Move(temporary, CredentialPath, overwrite: true);
            _token = token; _loaded = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>A valid access token, refreshed when it expires within a minute; null when not signed in.</summary>
    private async Task<string?> Access(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            var c = Cfg;
            var token = LoadToken();
            if (token is null || token.Refresh.Length == 0 || token.ClientId != c.ClientId.Trim()) return null;
            if (token.Expires - Now > 60_000) return token.Access;
            var refreshed = await TokenRequest(new()
            {
                ["client_id"] = token.ClientId, ["client_secret"] = c.ClientSecret.Trim(), ["grant_type"] = "refresh_token", ["refresh_token"] = token.Refresh,
            }, token.ClientId, token.Refresh, ct);
            Save(refreshed);
            return refreshed.Access;
        }
        finally { _tokenGate.Release(); }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Events between the two instants from every configured calendar; recurring events come expanded.</summary>
    public async Task<List<CalendarEvent>> Events(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var c = Cfg;
        string? access;
        try { access = await Access(ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException) { _error = ex.Message; throw; }
        if (access is null) throw new InvalidOperationException("Google Calendar is not connected");
        var name = string.IsNullOrWhiteSpace(c.Name) ? "Google" : c.Name.Trim();
        var color = string.IsNullOrWhiteSpace(c.Color) ? "#60a5fa" : c.Color!;
        var ids = c.CalendarIds.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().DefaultIfEmpty("primary").ToList();
        var result = new List<CalendarEvent>();
        foreach (var id in ids)
        {
            string? page = null;
            var pages = 0;
            do
            {
                var url = ApiBase + Uri.EscapeDataString(id) + "/events?singleEvents=true&orderBy=startTime&maxResults=250"
                    + "&timeMin=" + Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    + "&timeMax=" + Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new("Bearer", access);
                using var res = await _http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    throw new IOException($"Google Calendar refused calendar \"{id}\" ({(int)res.StatusCode})");
                if (!res.IsSuccessStatusCode) throw new IOException($"Google Calendar answered {(int)res.StatusCode}");
                using var doc = JsonDocument.Parse(body);
                result.AddRange(ParseEvents(doc.RootElement, name, color));
                page = doc.RootElement.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
            } while (page is not null && ++pages < 10);
        }
        _error = null;
        return result;
    }

    /// <summary>One events.list page as calendar events: cancelled, declined and working-location entries are left out.</summary>
    internal static IEnumerable<CalendarEvent> ParseEvents(JsonElement root, string calendar, string color)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in items.EnumerateArray())
        {
            if (Str(e, "status") == "cancelled") continue;
            if (Str(e, "eventType") == "workingLocation") continue;
            if (e.TryGetProperty("attendees", out var att) && att.ValueKind == JsonValueKind.Array
                && att.EnumerateArray().Any(a => a.TryGetProperty("self", out var s) && s.ValueKind == JsonValueKind.True && Str(a, "responseStatus") == "declined"))
                continue;
            if (!e.TryGetProperty("start", out var start) || !e.TryGetProperty("end", out var end)) continue;
            if (When(start, out var allDay) is not { } s1 || When(end, out _) is not { } e1) continue;
            var title = Str(e, "summary") is { Length: > 0 } t ? t : "(no title)";
            var location = Str(e, "location") is { Length: > 0 } l ? l : null;
            yield return new CalendarEvent(title, new DateTimeOffset(s1).ToUnixTimeMilliseconds(), new DateTimeOffset(e1).ToUnixTimeMilliseconds(), allDay, location, calendar, color);
        }
    }

    /// <summary>A start or end: dateTime (with its offset) or, for all-day events, date as midnight UTC like the ICS reader.</summary>
    private static DateTime? When(JsonElement w, out bool allDay)
    {
        allDay = false;
        if (Str(w, "dateTime") is { Length: > 0 } dt)
            return DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var o) ? o.UtcDateTime : null;
        if (Str(w, "date") is { Length: > 0 } d && DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            allDay = true;
            return DateTime.SpecifyKind(day, DateTimeKind.Utc);
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
