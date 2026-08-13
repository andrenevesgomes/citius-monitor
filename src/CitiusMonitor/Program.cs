using System.Diagnostics;
using System.Globalization;
using CitiusMonitor;

// ---------------------------------------------------------------------------
// Citius Monitor — C# / .NET entry point (single self-contained executable,
// nothing to install for end users).
//
// Iterates over EVERY court in the Citius "Distribuição" dropdown, searches the
// configured date range, flags proceedings where the target defendant appears
// under the "Réu:" role, and writes an Excel report. Read-only by design.
//
// User-facing text (console prompts, summary, Excel labels) is intentionally in
// Portuguese because the operators are Portuguese courts; all code and comments
// are in English.
// ---------------------------------------------------------------------------

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

// The target defendant and match mode are configuration, not hard-coded: supply
// them with --defendant / --match-mode, via the CITIUS_TARGET_DEFENDANT /
// CITIUS_MATCH_MODE environment variables, or fall back to a neutral default.
string targetDefendant = options.Defendant
    ?? EnvOrNull("CITIUS_TARGET_DEFENDANT")
    ?? "Example Company, Lda.";
string matchMode = options.MatchMode
    ?? EnvOrNull("CITIUS_MATCH_MODE")
    ?? "variation"; // "strict" | "variation"

Console.OutputEncoding = System.Text.Encoding.UTF8;

var tz = ResolveLisbonTimeZone();
var (dateFrom, dateTo) = ResolveDateRange(options, tz);

// Non-technical users: when launched by double-click (interactive console) and
// no dates were supplied, ask for them in plain language. In headless/automated
// runs (scheduler, redirected stdin) or with --no-prompt, use the defaults so
// the process never blocks waiting for input.
if (options.From is null && options.To is null && !options.NoPrompt && !Console.IsInputRedirected)
{
    (dateFrom, dateTo) = PromptDates(dateFrom, dateTo);
}

Log($"Citius Monitor — Réu-alvo: \"{targetDefendant}\" | modo: {matchMode}");
Log($"Intervalo: {dateFrom} → {dateTo}");

var summary = new RunSummary
{
    StartedAt = Timestamp(),
    DateFrom = dateFrom,
    DateTo = dateTo,
    TargetDefendant = targetDefendant,
    MatchMode = matchMode,
};

var findings = new List<ProcessRecord>();
var failures = new List<CourtFailure>();
var seenGlobal = new HashSet<string>();

var clientConfig = new ClientConfig(PageDelaySeconds: options.PageDelay);
using var client = new CitiusClient(clientConfig);

List<CourtOption> courts;
try
{
    courts = await client.DiscoverCourtsAsync();
}
catch (Exception ex)
{
    Log($"ERRO ao descobrir tribunais: {ex.Message}");
    summary.Status = "FALHOU";
    summary.FinishedAt = Timestamp();
    return 3;
}

summary.CourtsDiscovered = courts.Count;
Log($"Tribunais descobertos: {courts.Count}");

var selected = SelectCourts(courts, options);
Log($"Tribunais a pesquisar: {selected.Count}");

var index = 0;
foreach (var court in selected)
{
    index++;
    try
    {
        var records = await client.SearchCourtAsync(court, dateFrom, dateTo);
        summary.CourtsSearched++;
        if (records.Count > 0) summary.CourtsWithResults++;

        var matchesHere = 0;
        foreach (var rec in records)
        {
            if (!Parser.MatchDefendant(rec, targetDefendant, matchMode)) continue;
            if (!seenGlobal.Add(rec.DedupKey)) continue;
            findings.Add(rec);
            matchesHere++;
        }
        if (matchesHere > 0)
        {
            summary.CourtsWithMatches++;
            summary.TotalMatches += matchesHere;
            Log($"[{index}/{selected.Count}] {court.Name}: {records.Count} processos, {matchesHere} correspondência(s) ***");
        }
        else
        {
            Log($"[{index}/{selected.Count}] {court.Name}: {records.Count} processos");
        }
    }
    catch (RateLimitedException ex)
    {
        Log($"[{index}/{selected.Count}] {court.Name}: LIMITE DE TAXA — a parar. {ex.Message}");
        failures.Add(new CourtFailure(court.CourtId, court.Name, "pesquisa", ex.Message));
        summary.CourtsFailed++;
        summary.Notes.Add("Execução interrompida por HTTP 429.");
        break;
    }
    catch (Exception ex)
    {
        Log($"[{index}/{selected.Count}] {court.Name}: FALHOU — {ex.Message}");
        failures.Add(new CourtFailure(court.CourtId, court.Name, "pesquisa", ex.Message));
        summary.CourtsFailed++;
    }
}

summary.Status = failures.Count == 0
    ? "OK"
    : (summary.CourtsSearched > 0 ? "PARCIAL" : "FALHOU");
summary.FinishedAt = Timestamp();

// Build the Excel report (timestamped so runs never overwrite each other).
var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
var reportsDir = ResolveReportsDir();
var xlsxPath = Path.Combine(reportsDir, $"citius_report_{stamp}.xlsx");
try
{
    Report.BuildWorkbook(xlsxPath, summary, findings, failures);
    Log($"Relatório: {xlsxPath}");
}
catch (Exception ex)
{
    Log($"ERRO ao gerar o Excel: {ex.Message}");
}

PrintSummary(summary, findings);

// Optional notifications (email + Teams). Both are off unless configured through
// environment variables; the CLI flags can force each channel off. Never fails
// the run — the Excel report already exists regardless of delivery outcome.
var notifications = NotificationConfig.FromEnvironment(
    disableEmail: options.NoEmail,
    disableTeams: options.NoTeams);
await Notifier.DispatchAsync(
    notifications,
    summary,
    findings,
    File.Exists(xlsxPath) ? xlsxPath : null,
    Log);

if (options.Open && File.Exists(xlsxPath))
{
    try { Process.Start(new ProcessStartInfo(xlsxPath) { UseShellExecute = true }); }
    catch (Exception ex) { Log($"Não foi possível abrir o Excel: {ex.Message}"); }
}

return summary.Status switch
{
    "OK" => 0,
    "PARCIAL" => 2,
    _ => 3,
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static void Log(string msg) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {msg}");

// Consistent, locale-independent timestamp for logs and the report.
static string Timestamp() =>
    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

// Reports live under the user's "Documents" folder: always writable, no elevation
// prompts, even if the .exe sits in Program Files. Falls back to the executable
// directory if "Documents" cannot be resolved for some reason.
static string ResolveReportsDir()
{
    string baseDir;
    try
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        baseDir = string.IsNullOrEmpty(docs) ? AppContext.BaseDirectory : docs;
    }
    catch
    {
        baseDir = AppContext.BaseDirectory;
    }
    var dir = Path.Combine(baseDir, "Citius Monitor", "reports");
    Directory.CreateDirectory(dir);
    return dir;
}

static TimeZoneInfo ResolveLisbonTimeZone()
{
    foreach (var id in new[] { "Europe/Lisbon", "GMT Standard Time" })
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { }
    }
    return TimeZoneInfo.Utc;
}

// Returns a trimmed environment variable value, or null when unset/blank.
static string? EnvOrNull(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    return string.IsNullOrEmpty(value) ? null : value;
}

static (string from, string to) ResolveDateRange(CliOptions o, TimeZoneInfo tz)
{
    const string fmt = "dd-MM-yyyy";
    if (o.From is not null && o.To is not null)
        return (o.From, o.To);

    var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
    var today = now.Date;
    var start = o.From is not null
        ? DateTime.ParseExact(o.From, fmt, CultureInfo.InvariantCulture)
        : today.AddDays(-1);
    var end = o.To is not null
        ? DateTime.ParseExact(o.To, fmt, CultureInfo.InvariantCulture)
        : today;
    return (start.ToString(fmt, CultureInfo.InvariantCulture),
            end.ToString(fmt, CultureInfo.InvariantCulture));
}

// Simple interactive date prompt for users who launch the tool by double-click.
static (string from, string to) PromptDates(string defFrom, string defTo)
{
    const string fmt = "dd-MM-yyyy";
    Console.WriteLine();
    Console.WriteLine("Que intervalo de datas quer pesquisar?");
    Console.WriteLine($"  • Prima ENTER para usar o predefinido: {defFrom} até {defTo}");
    Console.WriteLine("  • Ou escreva as datas no formato DD-MM-AAAA (ex.: 05-08-2026).");
    Console.WriteLine();

    var from = AskDate("Data inicial", defFrom, fmt);
    var to = AskDate("Data final  ", defTo, fmt);

    var df = DateTime.ParseExact(from, fmt, CultureInfo.InvariantCulture);
    var dt = DateTime.ParseExact(to, fmt, CultureInfo.InvariantCulture);
    if (df > dt)
    {
        (from, to) = (to, from);
        Console.WriteLine("  (As datas foram trocadas para ficarem por ordem.)");
    }
    Console.WriteLine();
    return (from, to);
}

static string AskDate(string label, string def, string fmt)
{
    while (true)
    {
        Console.Write($"{label} [{def}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return def;

        // Forgiving: accept '/', '.' or spaces as separators and normalise them.
        input = input.Trim().Replace('/', '-').Replace('.', '-').Replace(' ', '-');
        if (DateTime.TryParseExact(input, fmt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return input;

        Console.WriteLine("  Formato inválido. Use DD-MM-AAAA, por exemplo 05-08-2026.");
    }
}

static List<CourtOption> SelectCourts(List<CourtOption> all, CliOptions o)
{
    IEnumerable<CourtOption> q = all;
    if (o.Courts.Count > 0)
        q = all.Where(c => o.Courts.Contains(c.CourtId));
    if (o.MaxCourts is int max && max > 0)
        q = q.Take(max);
    return q.ToList();
}

static void PrintSummary(RunSummary s, List<ProcessRecord> findings)
{
    Console.WriteLine();
    Console.WriteLine("──────────────────────────────────────────────");
    Console.WriteLine($"  Estado ...................... {s.Status}");
    Console.WriteLine($"  Intervalo ................... {s.DateFrom} → {s.DateTo}");
    Console.WriteLine($"  Tribunais pesquisados ....... {s.CourtsSearched}/{s.CourtsDiscovered}");
    Console.WriteLine($"  Tribunais com falha ......... {s.CourtsFailed}");
    Console.WriteLine($"  Correspondências ............ {s.TotalMatches}");
    Console.WriteLine("──────────────────────────────────────────────");
    if (findings.Count > 0)
    {
        Console.WriteLine("  Processos encontrados:");
        foreach (var f in findings)
        {
            Console.WriteLine($"   • {f.ProcessNumber}  |  {f.CourtName}");
            Console.WriteLine($"     Réu: {f.DefendantRaw}   Autor: {f.PlaintiffRaw ?? "—"}");
            Console.WriteLine($"     Distribuição: {f.DistributionDate ?? "—"}   Valor: {f.Amount ?? "—"}");
        }
    }
    else
    {
        Console.WriteLine("  Sem correspondências neste intervalo.");
    }
    Console.WriteLine();
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------
sealed class CliOptions
{
    public string? From { get; private set; }
    public string? To { get; private set; }
    public string? Defendant { get; private set; }
    public string? MatchMode { get; private set; }
    public List<string> Courts { get; } = new();
    public int? MaxCourts { get; private set; }
    public bool Open { get; private set; }
    public double PageDelay { get; private set; } = 1.0;
    public bool NoPrompt { get; private set; }
    public bool NoEmail { get; private set; }
    public bool NoTeams { get; private set; }
    public bool ShowHelp { get; private set; }
    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from": o.From = args[++i]; break;
                case "--to": o.To = args[++i]; break;
                case "--defendant": o.Defendant = args[++i]; break;
                case "--match-mode": o.MatchMode = args[++i]; break;
                case "--court": o.Courts.Add(args[++i]); break;
                case "--max-courts": o.MaxCourts = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--page-delay": o.PageDelay = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--open": o.Open = true; break;
                case "--no-prompt": o.NoPrompt = true; break;
                case "--no-email": o.NoEmail = true; break;
                case "--no-teams": o.NoTeams = true; break;
                case "-h" or "--help": o.ShowHelp = true; break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
        Citius Monitor (.NET) — monitorização de distribuição

        Uso:
          Citius.exe [opções]

        Opções:
          --from DD-MM-YYYY    Data inicial (por omissão: ontem, hora de Lisboa)
          --to   DD-MM-YYYY    Data final   (por omissão: hoje, hora de Lisboa)
          --defendant NOME     Réu a vigiar (ou CITIUS_TARGET_DEFENDANT)
          --match-mode MODO    Correspondência: strict|variation (ou CITIUS_MATCH_MODE)
          --court ID           Pesquisar só este tribunal (pode repetir)
          --max-courts N       Limitar ao primeiro N de tribunais (testes)
          --page-delay SEG     Pausa entre páginas/pedidos (por omissão: 1)
          --open               Abrir o Excel no fim
          --no-prompt          Não perguntar datas (execução automática/agendador)
          --no-email           Não enviar e-mail (mesmo que esteja configurado)
          --no-teams           Não publicar no Teams (mesmo que esteja configurado)
          -h, --help           Esta ajuda

        Notificações (opcionais, configuradas por variáveis de ambiente):
          Alvo:   CITIUS_TARGET_DEFENDANT (réu a vigiar), CITIUS_MATCH_MODE (strict|variation)
          E-mail: CITIUS_SMTP_HOST, CITIUS_SMTP_PORT (587), CITIUS_SMTP_USER,
                  CITIUS_SMTP_PASSWORD, CITIUS_SMTP_FROM, CITIUS_SMTP_STARTTLS (true),
                  CITIUS_EMAIL_TO (separar por vírgula ou ponto-e-vírgula)
          Teams:  CITIUS_TEAMS_WEBHOOK_URL
                  CITIUS_TEAMS_MENTIONS (menções quando há correspondências;
                  pares "Nome=id" separados por ';', id = UPN/e-mail ou AAD object ID)
        """);
    }
}
