# Novaritz — browser automation (LLM web capture)

A .NET 8 console app that takes the prompts clients already have in the
Novaritz database, asks each one on chatgpt.com through a real Chrome
window (Playwright), and stores the answer back in the same database
against the same client and brand.

It is a separate application from the Novaritz backend and modifies none
of its tables. It reads each audit's prompts from `audit_jobs.result`
(JSON, via `OPENJSON`) and writes one row per answer to its own table,
`LLM_web_responses`. A row is attributed to the client through
`audit_job → organization` and to the brand through `brand_ai_profiles`
(matched on name). `UNIQUE (audit_job_id, prompt_key, LLMType)` means a
prompt is never answered twice by the same LLM — re-running continues
where it stopped.

**Database: Microsoft SQL Server** — Azure SQL Database in production, a
local SQL Server 2019 copy for testing. `LLMType` records which LLM
answered (`chatgpt` today), so other engines can be added alongside.

## Rules

- **Never start a run without an explicit go from the owner** — it uses a
  real ChatGPT account and writes to the database it is pointed at.
- **Leave the Chrome window alone while a run is in progress.** Closing it
  ends the run and can sign the account out; the next run then needs
  `login` again.
- **Point it at production only deliberately.** The default connection is
  the local SQL Server; the production string goes in `dotnet user-secrets`,
  never in a committed file.
- `state/` (the saved ChatGPT login — treat it as a password) and
  `results/` (captured answers, screenshots) are git-ignored and must stay so.

## Setup (once)

- .NET 8 SDK, Google Chrome installed (`BrowserChannel: chrome` drives it).
- SQL Server with the Novaritz `quantico` database (local copy or Azure).
- A **dedicated ChatGPT account** — not a founder's.

```powershell
cd Novaritz.WebCapture
dotnet build
dotnet run -- login                          # once: sign in by hand; press Enter in the console when the chat box shows
```

## Running

```powershell
dotnet run -- list --limit 5                 # next pending prompts, read-only — touches nothing
dotnet run -- list --brand "Nyati Emerald"
dotnet run -- run --dry-run --limit 1        # asks ChatGPT, prints, writes nothing
dotnet run -- run --limit 5 --brand "GK Merai"
dotnet run -- run --client "Regency Group"   # every pending prompt for one client
```

`login.cmd` / `run.cmd` in the project folder do the same by double-click
(`run.cmd --limit 5 --brand "GK Merai"` works too).

## Settings

`appsettings.json`:

| Key | Meaning |
|---|---|
| `ConnectionStrings:Novaritz` | SQL Server connection (local Windows-auth by default) |
| `Capture:BatchLimit` | Prompts per run unless `--limit` is given |
| `Capture:PauseSeconds` | Pause between prompts |
| `Capture:AnswerTimeoutSeconds` | How long to wait for a reply |
| `Capture:StatePath` | Where the saved ChatGPT session lives |
| `Capture:ResultsPath` | Run logs (`run-<timestamp>.jsonl`) and failure screenshots |
| `Capture:BrowserChannel` | `chrome` (installed Chrome); empty for bundled Chromium |
| `Capture:LlmType` | Value written to `LLMType` (`chatgpt`) |

Production connection string, kept out of the repo:

```powershell
dotnet user-secrets set "ConnectionStrings:Novaritz" "Server=tcp:<server>.database.windows.net,1433;Database=quantico;User ID=<user>;Password=<password>;Encrypt=True"
dotnet user-secrets remove "ConnectionStrings:Novaritz"    # back to local
```

The project builds without an `.exe` (`UseAppHost=false`): Windows Smart
App Control blocked the unsigned one, so everything runs through the
signed `dotnet` host.

## What a run stores (`LLM_web_responses`)

| Column | Contents |
|---|---|
| `LLMType`, `brandId`, `audit_job_id`, `organization_id`, `prompt_key`, `prompt_text`, `intent` | Which LLM, brand, run, client and prompt |
| `answer` | The prose of the reply — map widgets, place cards and citation pills removed |
| `citations` | JSON list of `{text, href}` for the source pills the LLM showed |
| `places` | JSON list of `{name, rating, text}` for map/business cards, when any were rendered |
| `raw_text` | The whole message as displayed, untouched |
| `latency_ms`, `captured_at`, `createdDate` | Timing |

A Python implementation with identical behaviour lives alongside this
project outside the repository; keep the two in step when changing the
extraction or the SQL.
