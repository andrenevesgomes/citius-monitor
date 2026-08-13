# Security Policy

## Reporting a vulnerability

If you discover a security issue in this project, **please do not open a public
issue**. Instead, report it privately to the repository owner (see the repo's
contact details) or via GitHub's *Report a vulnerability* feature under the
**Security** tab.

Please include:

- a clear description of the issue and its impact,
- steps to reproduce (proof of concept if possible),
- the affected version/commit.

You will receive an acknowledgement as soon as reasonably possible, and we will
keep you informed as the issue is investigated and resolved.

## Supported versions

Only the latest commit on the default branch is supported with security fixes.

## Security design notes

This tool is intentionally conservative:

- **Read-only.** It only issues the GET/POST requests a human would make on the
  public Citius search form. It never submits, edits, or deletes data.
- **No credentials.** It does not authenticate and stores no secrets. There are
  no API keys, passwords, or tokens in the codebase.
- **No personal data at rest by default.** Generated reports may contain names
  taken from the public court-distribution listings; they are written to the
  user's *Documents* folder and are **git-ignored** so they are never committed.
- **Responsible access.** Requests are throttled (configurable delay), retried
  with exponential backoff, and the tool stops immediately on HTTP 429.
- **Supply chain.** Dependencies are pinned via a NuGet lockfile, updated by
  Dependabot, and the code is scanned by CodeQL on every push.

## Responsible use

The data published on Citius is public but concerns real legal proceedings.
Use this tool only for legitimate purposes, respect the portal's terms of use,
and keep request volumes reasonable (run after the daily publication window and
avoid unnecessary re-runs).
