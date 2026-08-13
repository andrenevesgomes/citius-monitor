# Citius Monitor

A small, fast, **read-only** tool that monitors the Portuguese
[Citius court-distribution portal](https://www.citius.mj.pt/portal/consultas/ConsultasDistribuicao.aspx).
It iterates over **every court**, searches a date range, and flags proceedings
where a target defendant appears under the **`Réu:`** (defendant) role, producing
an Excel report.

It ships as a **single self-contained `.exe`** — end users install nothing (no
.NET runtime, no Python).

> **Note on language.** All code, comments, and documentation are in English.
> The text end users actually see (console prompts and Excel labels) is in
> Portuguese on purpose, because the operators are Portuguese courts.

---

## Highlights

- **Nothing to install.** One `Citius.exe` (~38 MB), runs on any Windows 64-bit PC.
- **Non-technical friendly.** Double-click and it *asks* for the dates
  (Enter = yesterday → today). Tolerant input (`05-08-2026`, `05/08/2026`, …).
- **Safe by design.** Read-only, throttled, retries with backoff, stops on HTTP 429.
- **Reliable output.** Multi-sheet Excel report (summary / results / failures),
  written to your **Documents** folder (no permission prompts), de-duplicated.
- **Production quality.** Analyzers with warnings-as-errors, NuGet lockfile,
  CodeQL scanning, and Dependabot updates.

## How it works

The portal is a legacy ASP.NET Web Forms site. For each court the tool:

1. GETs the page to obtain fresh hidden fields (`__VIEWSTATE`, `__EVENTVALIDATION`, …).
2. POSTs the search (sending the required `Referer`/`Origin` headers — without
   them the server returns a generic error page).
3. Follows pagination via the pager post-back until the last page.
4. Parses the results grid and matches the defendant name under the `Réu:` role
   (accent- and punctuation-insensitive by default).

`Parser` performs all parsing/matching with **no network access**, which keeps it
deterministic and unit-testable.

## Quick start (end users)

1. Get `Citius.exe` (from a release or by building — see below).
2. Double-click it (or `Executar-Citius.bat`).
3. Choose the dates when prompted, or press **Enter** twice for yesterday → today.
4. The Excel report opens automatically and is saved under
   `Documents\Citius Monitor\reports\`.

## Build & publish (developers)

Requirements: [.NET SDK 10.0+](https://dotnet.microsoft.com/download)
(pinned in [`global.json`](global.json)).

```powershell
# Restore, build, and run the quality gates
dotnet build -c Release

# Produce the single self-contained executable
dotnet publish -c Release -o publish
# -> publish/Citius.exe
```

## Usage

```
Citius.exe [options]

  --from DD-MM-YYYY    Start date (default: yesterday, Europe/Lisbon)
  --to   DD-MM-YYYY    End date   (default: today, Europe/Lisbon)
  --defendant NAME     Defendant to watch for (or CITIUS_TARGET_DEFENDANT)
  --match-mode MODE    Matching: strict|variation (or CITIUS_MATCH_MODE)
  --court ID           Search only this court (repeatable)
  --max-courts N       Limit to the first N courts (testing)
  --page-delay SEC     Delay between requests (default: 1)
  --open               Open the Excel report when finished
  --no-prompt          Do not ask for dates (automation / scheduler)
  --no-email           Do not send email (even if configured)
  --no-teams           Do not post to Teams (even if configured)
  -h, --help           Show help
```

Exit codes: `0` OK · `2` PARTIAL (isolated court failures) · `3` FAILED.

### Choosing the target defendant

The defendant to watch for is **configuration, not hard-coded**. Provide it with
`--defendant "Company Name, Lda."`, the `CITIUS_TARGET_DEFENDANT` environment
variable, or leave it and a neutral placeholder is used. `--match-mode`
(`strict` or `variation`, default `variation`) controls how tolerant the name
matching is to accents and punctuation.

### Changing the dates (non-technical users)

When launched by double-click, the tool asks:

```
Que intervalo de datas quer pesquisar?
  • Prima ENTER para usar o predefinido: 10-08-2026 até 11-08-2026
  • Ou escreva as datas no formato DD-MM-AAAA (ex.: 05-08-2026).

Data inicial [10-08-2026]:
Data final   [11-08-2026]:
```

Press **Enter** to accept a default, or type a date (`-`, `/` or `.` separators
all work). Reversed ranges are auto-corrected. In headless/scheduled runs the
prompt is skipped and defaults are used (or pass `--no-prompt`).

### Output location

Reports are written to `Documents\Citius Monitor\reports\citius_report_YYYYMMDD_HHMMSS.xlsx`
— always writable, no elevation prompts, even if the `.exe` lives in `Program Files`.

### Notifications (optional)

Email and Microsoft Teams notifications are **off by default** and are enabled
only when the relevant settings are provided through **environment variables** —
so secrets are never committed to the repository or printed to the console.

- **Email** is enabled when a host and at least one recipient are configured; the
  Excel report is attached and the body lists every matched proceeding as a
  formatted HTML table (with a plain-text fallback). Transport uses
  [MailKit](https://github.com/jstedfast/MailKit) with STARTTLS and default
  certificate validation.
- **Teams** is enabled when a webhook URL is configured; an Adaptive Card
  summary is posted. It targets the current **Power Automate "Workflows"**
  webhook (the *"Post to a channel when a webhook request is received"* flow) —
  the classic Office 365 connector `MessageCard` format was retired in 2025.
  Teams webhooks cannot carry attachments, so the Excel is delivered by email /
  shared folder / the workflow artifact.
  - **@mentions.** When at least one match is found, the card can tag people so
    they receive a real Teams notification. Configure them with
    `CITIUS_TEAMS_MENTIONS` as `Name=id` pairs separated by `;`, where `id` is
    the user's **UPN/email** or **Azure AD object ID** (e.g.
    `André Gomes=andre.gomes@example.com;Ana=ana@example.com`). No one is mentioned
    on empty runs. If an email UPN does not ping, use the Azure AD object ID
    instead (Entra admin center → Users → the person → *Object ID*).

A delivery failure never fails the run — the Excel report already exists on disk.
Use `--no-email` / `--no-teams` to force a channel off for a single run.

| Variable | Purpose | Default |
| --- | --- | --- |
| `CITIUS_TARGET_DEFENDANT` | Defendant name to watch for | `Example Company, Lda.` |
| `CITIUS_MATCH_MODE` | Name matching: `strict` or `variation` | `variation` |
| `CITIUS_SMTP_HOST` | SMTP server host (enables email) | — |
| `CITIUS_SMTP_PORT` | SMTP server port | `587` |
| `CITIUS_SMTP_USER` | SMTP username | — |
| `CITIUS_SMTP_PASSWORD` | SMTP password (**secret** — env only) | — |
| `CITIUS_SMTP_FROM` | Sender address | falls back to `CITIUS_SMTP_USER` |
| `CITIUS_SMTP_STARTTLS` | Use STARTTLS (`true`/`false`) | `true` |
| `CITIUS_EMAIL_TO` | Recipients, separated by `,` or `;` | — |
| `CITIUS_TEAMS_WEBHOOK_URL` | Teams Workflows webhook URL (**secret** — env only) | — |
| `CITIUS_TEAMS_MENTIONS` | People to @mention on a match: `Name=id;…` (id = UPN/email or AAD object ID) | — |

```powershell
# Example: enable email + Teams (with @mentions) for a single scheduled run
$env:CITIUS_SMTP_HOST = "smtp.office365.com"
$env:CITIUS_SMTP_USER = "citius@example.com"
$env:CITIUS_SMTP_PASSWORD = "<secret>"   # never hard-code in scripts committed to git
$env:CITIUS_EMAIL_TO = "legal@example.com; compliance@example.com"
$env:CITIUS_TEAMS_WEBHOOK_URL = "<workflows-webhook-url>"
$env:CITIUS_TEAMS_MENTIONS = "André Gomes=andre.gomes@example.com"
.\Citius.exe --no-prompt
```

> **Microsoft 365 note.** With `smtp.office365.com` the sending mailbox must have
> **Authenticated SMTP** enabled, and if MFA is on you must use an **app
> password** (the normal password fails with `535 5.7.139`). If the tenant
> blocks basic auth entirely, switch to OAuth2 / Microsoft Graph sending.

### Scheduled run in the cloud (GitHub Actions)

The [`Daily monitor`](.github/workflows/daily.yml) workflow runs the search on a
schedule — **weekdays at 08:00 Europe/Lisbon** (a gate job checks the local hour
so it stays correct across daylight-saving changes) — with **nothing running on
your PC**. It posts the Adaptive Card to Teams, sends the email (when SMTP is
configured), and keeps the Excel as a downloadable run artifact.

You can also run it on demand from the **Actions** tab → *Daily monitor* →
**Run workflow**, optionally passing a custom **date range** (`date_from` /
`date_to`, `DD-MM-YYYY`); leaving them empty uses yesterday → today.

Set it up once by adding **repository secrets** (**Settings → Secrets and
variables → Actions → New repository secret**). All are optional — a channel
stays off until its secrets exist, so nothing breaks in the meantime:

| Secret | Purpose |
| --- | --- |
| `CITIUS_TARGET_DEFENDANT` | Defendant name to watch for (otherwise a placeholder is used) |
| `CITIUS_TEAMS_WEBHOOK_URL` | Teams Workflows webhook URL (enables the Teams card) |
| `CITIUS_TEAMS_MENTIONS` | Optional @mentions on a match, e.g. `André Gomes=andre.gomes@example.com` |
| `CITIUS_SMTP_HOST` | SMTP host, e.g. `smtp.office365.com` (enables email) |
| `CITIUS_SMTP_PORT` | SMTP port (default `587`) |
| `CITIUS_SMTP_USER` | SMTP username / sending mailbox |
| `CITIUS_SMTP_PASSWORD` | SMTP password or **app password** |
| `CITIUS_SMTP_FROM` | Sender address (defaults to the username) |
| `CITIUS_SMTP_STARTTLS` | `true`/`false` (default `true`) |
| `CITIUS_EMAIL_TO` | Recipients, separated by `,` or `;` |

To create the Teams webhook: in Microsoft Teams add the **Workflows** app to the
target channel, create a flow from the *"Post to a channel when a webhook request
is received"* template, and copy the generated webhook URL into
`CITIUS_TEAMS_WEBHOOK_URL`.

Trigger the workflow manually the first time to confirm the card/email arrive; it
then runs on schedule. Secrets are injected only into the run step's environment
— they are never printed and never stored in the repository.

## Project structure

```
citius-monitor/
├── .github/workflows/    # CI, CodeQL scan, Release, and the daily monitor
├── src/CitiusMonitor/
│   ├── Program.cs         # Orchestration, CLI, prompts, console summary
│   ├── CitiusClient.cs    # HTTP session, headers, retries, pagination
│   ├── Parser.cs          # HTML parsing + normalisation + matching (no network)
│   ├── Report.cs          # Excel workbook (ClosedXML)
│   ├── Notifications.cs   # Optional email (MailKit) + Teams webhook
│   └── Models.cs          # Data types
├── Executar-Citius.bat    # One-click launcher for end users
├── CitiusMonitor.slnx
└── global.json
```

## Responsible use & security

This tool is deliberately conservative: read-only, throttled, no credentials, and
it stops on rate-limiting. Generated reports may contain names from the public
listings and are git-ignored so they are never committed. See
[`SECURITY.md`](SECURITY.md) for details and the disclosure policy.

Run it after the daily publication window (Citius publishes new distributions in
the evening, Europe/Lisbon) and avoid unnecessary re-runs.

## Smaller/faster binary (optional)

The shipped executable is a self-contained single file (~38 MB). A ~10 MB
Native AOT build is possible but requires the C++ Build Tools (linker) and
replacing ClosedXML with hand-written `.xlsx` output; it is intentionally not the
default to keep the toolchain simple and dependency-light.

## License

Proprietary — all rights reserved. See [`LICENSE`](LICENSE).
