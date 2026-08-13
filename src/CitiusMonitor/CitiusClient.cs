using System.Net;

namespace CitiusMonitor;

public sealed record ClientConfig(
    string UserAgent =
        "CitiusMonitor/1.0 (+responsible court distribution monitoring; contact: IT)",
    int TimeoutSeconds = 60,
    int MaxRetries = 3,
    double BackoffBaseSeconds = 2.0,
    double PageDelaySeconds = 1.0);

public sealed class RateLimitedException(string message) : Exception(message);

/// <summary>
/// HTTP client for the Citius portal (a legacy ASP.NET Web Forms site). Keeps the
/// session via cookies and always sends the Referer/Origin headers — without them
/// the server returns a generic "Erro" page instead of results.
///
/// The client is read-only: it only issues GET/POST requests that a browser would
/// issue when a human uses the public search form.
/// </summary>
public sealed class CitiusClient : IDisposable
{
    public const string BaseUrl =
        "https://www.citius.mj.pt/portal/consultas/ConsultasDistribuicao.aspx";
    public const string Origin = "https://www.citius.mj.pt";

    // Exact ASP.NET form field names, copied verbatim from the live page.
    private const string FDdl = "ctl00$ContentPlaceHolder1$ddlTribunais";
    private const string FDesde = "ctl00$ContentPlaceHolder1$txtCalendarDesde";
    private const string FAte = "ctl00$ContentPlaceHolder1$txtCalendarAte";
    private const string FParte = "ctl00$ContentPlaceHolder1$txtParte";
    private const string FEntrada = "ctl00$ContentPlaceHolder1$txtNEntrada";
    private const string FBtn = "ctl00$ContentPlaceHolder1$btnSearch";

    private static readonly HashSet<int> RetryableStatus = new() { 429, 500, 502, 503, 504 };
    private const int MaxPagesSafety = 200;

    private readonly ClientConfig _cfg;
    private readonly HttpClient _http;
    private List<CourtOption>? _courtsCache;

    public CitiusClient(ClientConfig? config = null)
    {
        _cfg = config ?? new ClientConfig();
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_cfg.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_cfg.UserAgent);
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "pt-PT,pt;q=0.9,en;q=0.8");
    }

    private async Task<string> RequestAsync(HttpMethod method, IEnumerable<KeyValuePair<string, string>>? form)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(method, BaseUrl);
                if (method == HttpMethod.Post && form is not null)
                    req.Content = new FormUrlEncodedContent(form);
                req.Headers.Referrer = new Uri(BaseUrl);
                req.Headers.Add("Origin", Origin);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                if (status == 429)
                    throw new RateLimitedException("HTTP 429 — the server asked us to slow down.");
                if (RetryableStatus.Contains(status))
                    throw new HttpRequestException($"HTTP {status} (transient).");
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (RateLimitedException)
            {
                throw; // Never retry on top of a 429.
            }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                last = ex;
                var delay = _cfg.BackoffBaseSeconds * Math.Pow(2, attempt);
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            }
        }
        throw last ?? new HttpRequestException("Request failed.");
    }

    public async Task<List<CourtOption>> DiscoverCourtsAsync()
    {
        if (_courtsCache is not null) return _courtsCache;
        var html = await RequestAsync(HttpMethod.Get, null).ConfigureAwait(false);
        _courtsCache = Parser.ParseCourtOptions(html);
        return _courtsCache;
    }

    /// <summary>Searches a single court for the given date range, across all result pages.</summary>
    public async Task<List<ProcessRecord>> SearchCourtAsync(
        CourtOption court, string dateFrom, string dateTo)
    {
        // 1) Fresh GET to obtain valid hidden fields (__VIEWSTATE, etc.).
        var html = await RequestAsync(HttpMethod.Get, null).ConfigureAwait(false);
        var hidden = Parser.ParseHiddenFields(html);

        // 2) POST the search request.
        var form = BuildBaseForm(hidden);
        form.Add(new("__EVENTTARGET", ""));
        form.Add(new("__EVENTARGUMENT", ""));
        form.Add(new(FDdl, court.CourtId));
        form.Add(new(FDesde, dateFrom));
        form.Add(new(FAte, dateTo));
        form.Add(new(FParte, ""));
        form.Add(new(FEntrada, ""));
        form.Add(new(FBtn, "Pesquisar"));

        html = await RequestAsync(HttpMethod.Post, form).ConfigureAwait(false);

        var records = new List<ProcessRecord>();
        var seen = new HashSet<string>();
        var page = 0;
        while (true)
        {
            page++;
            foreach (var rec in Parser.ParseResults(html))
            {
                Stamp(rec, court, dateFrom, dateTo);
                if (seen.Add(rec.DedupKey)) records.Add(rec);
            }

            if (!Parser.HasNextPage(html) || page >= MaxPagesSafety) break;

            if (_cfg.PageDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_cfg.PageDelaySeconds)).ConfigureAwait(false);

            // 3) Next page: post back through the pager link (do NOT resend the
            //    search button, or ASP.NET restarts the query from page 1).
            hidden = Parser.ParseHiddenFields(html);
            var next = BuildBaseForm(hidden);
            next.Add(new("__EVENTTARGET", Parser.PagerNextEventTarget));
            next.Add(new("__EVENTARGUMENT", ""));
            next.Add(new(FDdl, court.CourtId));
            next.Add(new(FDesde, dateFrom));
            next.Add(new(FAte, dateTo));
            next.Add(new(FParte, ""));
            next.Add(new(FEntrada, ""));
            html = await RequestAsync(HttpMethod.Post, next).ConfigureAwait(false);
        }
        return records;
    }

    private static List<KeyValuePair<string, string>> BuildBaseForm(Dictionary<string, string> hidden)
    {
        var form = new List<KeyValuePair<string, string>>();
        foreach (var kv in hidden) form.Add(new(kv.Key, kv.Value));
        return form;
    }

    private static void Stamp(ProcessRecord rec, CourtOption court, string dateFrom, string dateTo)
    {
        rec.CourtId = court.CourtId;
        rec.CourtName = court.Name;
        rec.SearchDate = $"{dateFrom} — {dateTo}";
    }

    public void Dispose() => _http.Dispose();
}
