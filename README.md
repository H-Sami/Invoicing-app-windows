# Invoices App

A modern, offline-first Windows 11 invoicing application.
## Product goals

- Arabic-first RTL interface with optional English UI
- Professional light and dark themes
- Customer and saved-item directories with fast autocomplete
- Autosaved drafts and immutable issued-invoice history
- Yearly invoice numbering such as `MHC-2026-100`
- UUID document serials
- Halalah-safe SAR and VAT calculations
- ZATCA-compatible TLV QR payloads
- Local preview, PDF export, and printing
- SQLite persistence with manual backup and restore
- Conventional self-contained `Setup.exe` distribution

## Technology

- .NET 10 LTS / C# 14
- WinUI 3 / Windows App SDK 2.3.1
- EF Core 10 with SQLite
- CommunityToolkit.Mvvm
- WebView2 document preview and PDF rendering
- xUnit v3
- Inno Setup 7

## Supported platform

Windows 11 x64.

## Planned solution layout

- `src/MHC.Invoicing.Domain` — accounting and business rules
- `src/MHC.Invoicing.Application` — use cases and interfaces
- `src/MHC.Invoicing.Infrastructure` — SQLite, documents, printing, and backup
- `src/MHC.Invoicing.App` — WinUI 3 application
- `tests/` — domain, application, infrastructure, and desktop tests

## Data privacy

The application stores invoice and customer information locally. Generated databases, PDFs, backups, credentials, and signing material must never be committed to Git.

## Local data

Runtime data is stored under:

`%LOCALAPPDATA%\MHC Technology\MHC Invoices V4\`

The SQLite database, generated invoice documents, WebView2 profile, and backups are user data and are never stored beside the executable or committed to Git.

## Build

```bash
dotnet restore MHC.Invoicing.sln --locked-mode
dotnet format MHC.Invoicing.sln --verify-no-changes --no-restore
dotnet build MHC.Invoicing.sln -c Release -p:Platform=x64 --no-restore
dotnet test MHC.Invoicing.sln -c Release -p:Platform=x64 --no-build --collect:"XPlat Code Coverage" --results-directory TestResults --logger trx
```

## Status

V4 rebuild in progress. The original Python implementation is preserved separately in the private legacy repository.
