using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CitiusMonitor;

// ---------------------------------------------------------------------------
// Notifications — optional SMTP email (with the Excel attached) and an optional
// Microsoft Teams webhook card.
//
// SAFETY: every channel is OFF by default. Nothing is ever sent unless the
// operator supplies the relevant configuration through ENVIRONMENT VARIABLES
// (never committed, never printed). Secrets (SMTP password, Teams webhook URL)
// are read from the environment only and are never written to the console or
// the report. Certificate validation is left at the library default (enabled).
//
// User-facing text is intentionally Portuguese; code and comments are English.
// ---------------------------------------------------------------------------

/// <summary>SMTP email settings. <see cref="Enabled"/> gates all sending.</summary>
public sealed record EmailConfig(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string Password,
    bool UseStartTls,
    string Sender,
    IReadOnlyList<string> Recipients);

/// <summary>A Teams user to @mention (name shown in the card + AAD id/UPN).</summary>
public sealed record TeamsMention(string Name, string Id);

/// <summary>Microsoft Teams incoming-webhook settings.</summary>
public sealed record TeamsConfig(bool Enabled, string WebhookUrl, IReadOnlyList<TeamsMention> Mentions);

/// <summary>Aggregated notification configuration, resolved from the environment.</summary>
public sealed record NotificationConfig(EmailConfig Email, TeamsConfig Teams)
{
    // Environment variable names (documented in the README).
    private const string EnvSmtpHost = "CITIUS_SMTP_HOST";
    private const string EnvSmtpPort = "CITIUS_SMTP_PORT";
    private const string EnvSmtpUser = "CITIUS_SMTP_USER";
    private const string EnvSmtpPassword = "CITIUS_SMTP_PASSWORD";
    private const string EnvSmtpFrom = "CITIUS_SMTP_FROM";
    private const string EnvSmtpStartTls = "CITIUS_SMTP_STARTTLS";
    private const string EnvEmailTo = "CITIUS_EMAIL_TO";
    private const string EnvTeamsWebhook = "CITIUS_TEAMS_WEBHOOK_URL";
    private const string EnvTeamsMentions = "CITIUS_TEAMS_MENTIONS";

    /// <summary>
    /// Build the configuration from environment variables. Email is considered
    /// enabled only when a host and at least one recipient are present; Teams is
    /// enabled only when a webhook URL is present. The optional flags let the CLI
    /// force a channel off (<c>--no-email</c>, <c>--no-teams</c>).
    /// </summary>
    public static NotificationConfig FromEnvironment(bool disableEmail = false, bool disableTeams = false)
    {
        var host = Env(EnvSmtpHost);
        var user = Env(EnvSmtpUser);
        var password = Env(EnvSmtpPassword);
        var from = Env(EnvSmtpFrom);
        var recipients = SplitRecipients(Env(EnvEmailTo));
        var port = ParsePort(Env(EnvSmtpPort), 587);
        var startTls = ParseBool(Env(EnvSmtpStartTls), defaultValue: true);

        var emailEnabled = !disableEmail
            && !string.IsNullOrWhiteSpace(host)
            && recipients.Length > 0;

        var email = new EmailConfig(
            Enabled: emailEnabled,
            Host: host,
            Port: port,
            Username: user,
            Password: password,
            UseStartTls: startTls,
            Sender: string.IsNullOrWhiteSpace(from) ? user : from,
            Recipients: recipients);

        var webhook = Env(EnvTeamsWebhook);
        var teamsEnabled = !disableTeams && !string.IsNullOrWhiteSpace(webhook);
        var teams = new TeamsConfig(teamsEnabled, webhook, ParseMentions(Env(EnvTeamsMentions)));

        return new NotificationConfig(email, teams);
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? "";

    // Recipients may be separated by comma or semicolon; blanks are dropped.
    private static readonly char[] RecipientSeparators = { ',', ';' };

    private static string[] SplitRecipients(string raw) =>
        raw.Split(RecipientSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Mentions are "Name=id" pairs separated by ';', where id is the user's AAD
    // object id or UPN/email, e.g. "André Gomes=andre.gomes@example.com;Ana=ana@example.com".
    private static readonly char[] MentionSeparators = { ';' };

    private static IReadOnlyList<TeamsMention> ParseMentions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<TeamsMention>();

        var result = new List<TeamsMention>();
        foreach (var entry in raw.Split(MentionSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = entry.IndexOf('=');
            if (sep <= 0 || sep >= entry.Length - 1)
                continue; // skip malformed entries silently
            var name = entry[..sep].Trim();
            var id = entry[(sep + 1)..].Trim();
            if (name.Length > 0 && id.Length > 0)
                result.Add(new TeamsMention(name, id));
        }
        return result;
    }

    private static int ParsePort(string raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0
            ? p
            : fallback;

    private static bool ParseBool(string raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue,
        };
    }
}

/// <summary>
/// Sends the run outcome over the configured channels. All methods are safe to
/// call unconditionally: a disabled channel is a no-op, and a delivery failure
/// is reported through <paramref name="log"/> without throwing, so a broken mail
/// server never fails the whole run (the Excel report already exists on disk).
/// </summary>
public static class Notifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Dispatch email and Teams notifications according to <paramref name="config"/>.</summary>
    public static async Task DispatchAsync(
        NotificationConfig config,
        RunSummary summary,
        IReadOnlyList<ProcessRecord> findings,
        string? attachmentPath,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        await SendEmailAsync(config.Email, summary, findings, attachmentPath, log, cancellationToken).ConfigureAwait(false);
        await SendTeamsAsync(config.Teams, summary, findings, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Send the notification email with the report attached.</summary>
    public static async Task SendEmailAsync(
        EmailConfig cfg,
        RunSummary summary,
        IReadOnlyList<ProcessRecord> findings,
        string? attachmentPath,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!cfg.Enabled)
            return;

        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(cfg.Sender));
            foreach (var recipient in cfg.Recipients)
                message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = BuildSubject(summary);

            var body = new BodyBuilder
            {
                TextBody = BuildEmailBody(summary, findings),
                HtmlBody = BuildEmailHtml(summary, findings),
            };
            if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
                await body.Attachments.AddAsync(attachmentPath, cancellationToken).ConfigureAwait(false);
            message.Body = body.ToMessageBody();

            using var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
            var secureOptions = cfg.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await smtp.ConnectAsync(cfg.Host, cfg.Port, secureOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cfg.Username))
                await smtp.AuthenticateAsync(cfg.Username, cfg.Password, cancellationToken).ConfigureAwait(false);
            await smtp.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            log($"E-mail enviado para {cfg.Recipients.Count} destinatário(s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never leak the SMTP password; only the error message is surfaced.
            log($"Não foi possível enviar o e-mail: {ex.Message}");
        }
    }

    /// <summary>POST a summary card to the configured Microsoft Teams webhook.</summary>
    public static async Task SendTeamsAsync(
        TeamsConfig cfg,
        RunSummary summary,
        IReadOnlyList<ProcessRecord> findings,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!cfg.Enabled)
            return;

        try
        {
            var payload = BuildTeamsCard(summary, findings, cfg.Mentions);
            var json = JsonSerializer.Serialize(payload);

            using var http = new HttpClient { Timeout = Timeout };
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(cfg.WebhookUrl), content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            log($"Notificação Teams publicada (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Não foi possível publicar no Teams: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Message building (Portuguese, mirrors the original Python notifier)
    // -----------------------------------------------------------------------

    private static string BuildSubject(RunSummary summary) =>
        $"[Citius] Monitorização {StatusLabel(summary.Status)} - " +
        $"{summary.TotalMatches} correspondência(s) - {summary.DateTo}";

    private static string BuildEmailBody(RunSummary summary, IReadOnlyList<ProcessRecord> findings)
    {
        var lines = new List<string>
        {
            $"Execução Citius - {summary.Status}",
            "",
            $"Intervalo pesquisado: {summary.DateFrom} a {summary.DateTo}",
            $"Empresa alvo (Réu): {summary.TargetDefendant}",
            $"Tribunais descobertos: {summary.CourtsDiscovered}",
            $"Tribunais pesquisados: {summary.CourtsSearched}",
            $"Tribunais com falha: {summary.CourtsFailed}",
            $"Total de correspondências: {summary.TotalMatches}",
            "",
        };

        if (findings.Count > 0)
        {
            lines.Add("Processos encontrados:");
            foreach (var rec in findings)
            {
                lines.Add($" - Processo {rec.ProcessNumber} | {rec.CourtName}");
                lines.Add($"   Réu: {rec.DefendantRaw}   Autor: {rec.PlaintiffRaw ?? "—"}");
                lines.Add($"   Distribuição: {rec.DistributionDate ?? "—"}   Valor: {rec.Amount ?? "—"}");
            }
            lines.Add("");
            lines.Add("O relatório completo segue em anexo (Excel).");
        }
        else if (summary.Status != "FALHOU")
        {
            lines.Add("Não foram detetadas correspondências para o período indicado.");
        }

        if (summary.CourtsFailed > 0)
            lines.Add($"ATENÇÃO: {summary.CourtsFailed} tribunal(is) falharam - execução parcial.");
        if (summary.Notes.Count > 0)
        {
            lines.Add("");
            lines.Add("Notas:");
            lines.AddRange(summary.Notes.Select(n => $" - {n}"));
        }

        lines.Add("");
        lines.Add("— Mensagem automática. Serviço em fase de testes e aperfeiçoamento.");
        return string.Join("\n", lines);
    }

    // Formatted HTML alternative (clients that support it render this instead of
    // the plain-text body). All dynamic values are HTML-encoded.
    private static string BuildEmailHtml(RunSummary summary, IReadOnlyList<ProcessRecord> findings)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "—");

        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2933;\">");
        sb.Append(CultureInfo.InvariantCulture, $"<h2 style=\"margin:0 0 8px;color:#1f4e78;\">Citius – Monitorização {E(StatusLabel(summary.Status))}</h2>");
        sb.Append("<table style=\"border-collapse:collapse;margin:8px 0;\">");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:2px 8px;color:#616e7c;\">Empresa alvo (Réu)</td><td style=\"padding:2px 8px;\"><strong>{E(summary.TargetDefendant)}</strong></td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:2px 8px;color:#616e7c;\">Intervalo</td><td style=\"padding:2px 8px;\">{E(summary.DateFrom)} a {E(summary.DateTo)}</td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:2px 8px;color:#616e7c;\">Tribunais</td><td style=\"padding:2px 8px;\">{summary.CourtsSearched}/{summary.CourtsDiscovered} pesquisados · {summary.CourtsFailed} falha(s)</td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:2px 8px;color:#616e7c;\">Correspondências</td><td style=\"padding:2px 8px;\"><strong>{summary.TotalMatches}</strong></td></tr>");
        sb.Append("</table>");

        if (findings.Count > 0)
        {
            sb.Append("<h3 style=\"margin:16px 0 4px;\">Processos encontrados</h3>");
            sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:13px;\">");
            sb.Append("<tr style=\"background:#f0f4f8;text-align:left;\">"
                + "<th style=\"padding:6px 8px;border:1px solid #d9e2ec;\">Processo</th>"
                + "<th style=\"padding:6px 8px;border:1px solid #d9e2ec;\">Tribunal</th>"
                + "<th style=\"padding:6px 8px;border:1px solid #d9e2ec;\">Autor</th>"
                + "<th style=\"padding:6px 8px;border:1px solid #d9e2ec;\">Distribuição</th>"
                + "<th style=\"padding:6px 8px;border:1px solid #d9e2ec;\">Valor</th></tr>");
            foreach (var rec in findings)
            {
                sb.Append("<tr>"
                    + $"<td style=\"padding:6px 8px;border:1px solid #d9e2ec;\">{E(rec.ProcessNumber)}</td>"
                    + $"<td style=\"padding:6px 8px;border:1px solid #d9e2ec;\">{E(rec.CourtName)}</td>"
                    + $"<td style=\"padding:6px 8px;border:1px solid #d9e2ec;\">{E(rec.PlaintiffRaw)}</td>"
                    + $"<td style=\"padding:6px 8px;border:1px solid #d9e2ec;\">{E(rec.DistributionDate)}</td>"
                    + $"<td style=\"padding:6px 8px;border:1px solid #d9e2ec;\">{E(rec.Amount)}</td></tr>");
            }
            sb.Append("</table>");
            sb.Append("<p style=\"margin:12px 0 0;\">O relatório completo segue em anexo (Excel).</p>");
        }
        else if (summary.Status != "FALHOU")
        {
            sb.Append("<p>Não foram detetadas correspondências para o período indicado.</p>");
        }

        if (summary.CourtsFailed > 0)
            sb.Append(CultureInfo.InvariantCulture, $"<p style=\"color:#b44d12;\"><strong>Atenção:</strong> {summary.CourtsFailed} tribunal(is) falharam — execução parcial.</p>");

        sb.Append("<p style=\"margin-top:16px;color:#9aa5b1;font-size:12px;\">Mensagem automática. Serviço em fase de testes e aperfeiçoamento.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    // Power Automate "Workflows" webhook payload: an Adaptive Card wrapped in the
    // message/attachments envelope Teams expects. (The classic MessageCard
    // connector format was retired by Microsoft in 2025, so this is the
    // future-proof shape for the "Post to a channel when a webhook request is
    // received" flow.)
    private static object BuildTeamsCard(RunSummary summary, IReadOnlyList<ProcessRecord> findings, IReadOnlyList<TeamsMention> mentions)
    {
        var facts = new List<object>
        {
            new { title = "Empresa alvo (Réu):", value = summary.TargetDefendant },
            new { title = "Intervalo:", value = $"{summary.DateFrom} a {summary.DateTo}" },
            new { title = "Tribunais:", value = $"{summary.CourtsSearched}/{summary.CourtsDiscovered} pesquisados · {summary.CourtsFailed} falha(s)" },
            new { title = "Correspondências:", value = summary.TotalMatches.ToString(CultureInfo.InvariantCulture) },
        };

        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"Citius – Monitorização {StatusLabel(summary.Status)}",
                weight = "Bolder",
                size = "Large",
                color = CardColor(summary.Status),
                wrap = true,
            },
            new { type = "FactSet", facts },
        };

        // Only @mention people when there is at least one match to act on.
        var mentionEntities = new List<object>();
        if (findings.Count > 0 && mentions.Count > 0)
        {
            var tags = string.Join(" ", mentions.Select(m => $"<at>{m.Name}</at>"));
            body.Insert(1, new
            {
                type = "TextBlock",
                text = $"{tags} — foram encontradas correspondências que requerem atenção.",
                wrap = true,
                weight = "Bolder",
                color = "Attention",
                spacing = "Small",
            });
            foreach (var m in mentions)
                mentionEntities.Add(new { type = "mention", text = $"<at>{m.Name}</at>", mentioned = new { id = m.Id, name = m.Name } });
        }

        if (findings.Count > 0)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = "Processos encontrados:",
                weight = "Bolder",
                spacing = "Medium",
                wrap = true,
            });
            foreach (var rec in findings.Take(20))
            {
                body.Add(new
                {
                    type = "TextBlock",
                    text = $"• **{rec.ProcessNumber}** — {rec.CourtName} (distribuição {rec.DistributionDate ?? "—"})",
                    wrap = true,
                    spacing = "None",
                });
            }
            if (findings.Count > 20)
                body.Add(new { type = "TextBlock", text = $"…e mais {findings.Count - 20}.", wrap = true, isSubtle = true });
        }
        else
        {
            body.Add(new { type = "TextBlock", text = "Sem correspondências no período indicado.", wrap = true, isSubtle = true });
        }

        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["msteams"] = mentionEntities.Count > 0
                ? new Dictionary<string, object> { ["width"] = "Full", ["entities"] = mentionEntities }
                : new Dictionary<string, object> { ["width"] = "Full" },
            ["body"] = body,
        };

        return new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = card,
                },
            },
        };
    }

    // Maps the internal status ("OK"/"PARCIAL"/"FALHOU") to a friendly label.
    private static string StatusLabel(string status) => status switch
    {
        "OK" => "Concluído",
        "PARCIAL" => "Parcial",
        "FALHOU" => "Falhou",
        _ => status,
    };

    // Adaptive Card text colour by status (green / amber / red / blue accent).
    private static string CardColor(string status) => status switch
    {
        "OK" => "Good",
        "PARCIAL" => "Warning",
        "FALHOU" => "Attention",
        _ => "Accent",
    };
}
