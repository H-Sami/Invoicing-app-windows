# MHC Invoices V4

A modern, offline-first Windows 11 invoicing application for MHC Technology.

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

## Build

The canonical commands will be finalized with the solution scaffold:

```bash
dotnet restore
dotnet build MHC.Invoicing.sln -c Release
dotnet test MHC.Invoicing.sln -c Release
```

## Status

V4 rebuild in progress. The original Python implementation is preserved separately in the private legacy repository.
