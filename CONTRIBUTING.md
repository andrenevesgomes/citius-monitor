# Contributing

Thanks for helping improve **Citius Monitor**. This is a small, focused tool;
the goal is to keep it reliable, readable, and safe.

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer
  (the exact version is pinned in [`global.json`](global.json)).
- Windows for producing the shipped `win-x64` single-file executable.

## Getting started

```powershell
git clone <repo-url>
cd citius-monitor
dotnet restore
dotnet build -c Release
```

Run it during development:

```powershell
dotnet run --project src/CitiusMonitor -- --court 2871632 --from 01-08-2026 --to 11-08-2026
```

Produce the distributable single-file executable:

```powershell
dotnet publish -c Release -o publish
# -> publish/Citius.exe (self-contained, nothing to install)
```

## Coding standards

- **Language:** all code, comments, commit messages, and docs are in **English**.
  User-facing text (console prompts, Excel labels) stays in **Portuguese** on
  purpose — the operators are Portuguese courts.
- Analyzers run with **warnings-as-errors** (`TreatWarningsAsErrors=true`). Keep
  the build clean; use `CultureInfo.InvariantCulture` for parsing/formatting.
- Formatting follows [`.editorconfig`](.editorconfig). Run `dotnet format` before
  committing.
- Keep `Parser` free of any network access so it stays unit-testable.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/), e.g.
`feat: add Teams notifications`, `fix: handle empty results page`.

## Security

Never commit secrets, credentials, or generated reports (they are git-ignored).
See [`SECURITY.md`](SECURITY.md) for the disclosure policy and design notes.
