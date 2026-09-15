# Novaritz — browser automation (LLM web capture)

A .NET 8 console app that takes each approved brand's stored prompt set
from the Novaritz database, asks the prompts on chatgpt.com through a real
Chrome window (Playwright), and stores the answers back in the same
database against the brand and its client — once per cycle.

It is a separate application from the Novaritz backend and modifies none
of the backend's tables. It works **per brand, per cycle**:

- A brand is **due** when it is approved (`brand_entities`), has a prompt
  set (`prompts`) and has no `DONE` capture inside the current cycle. The
  cycle length is the client's plan frequency (`pricing.frequency`; Daily
  = 1 day). `dbo.fn_capture_queue(@llm)` (SQL script 010) is the queue.
- For each due brand the tool claims today's `brand_capture_runs` row,
  asks every prompt not yet answered in that run, writes one
  `LLM_web_responses` row per answer, and marks the run `DONE` when all
  prompts are answered. Stopping early (`--limit`, a logout, the page
  changing) leaves the run `PENDING` with its answers kept; the next start
  resumes it. Every cycle keeps its own answers, so a brand's replies can
  be compared day by day.
- `LLMType` records which LLM answered (`chatgpt` today), so other engines
  can be added alongside with their own cycles.

**Database: Microsoft SQL Server** — Azure SQL Database in production, a
local SQL Server 2019 copy for testing. The tool refuses to start against a
database that has not had script 010 applied.

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
dotnet run -- list                           # brands due in the current cycle, read-only
dotnet run -- list --client "Regency Group"
dotnet run -- run --dry-run --limit 1        # asks ChatGPT, prints, writes nothing
dotnet run -- run                            # works through the due brands, up to BatchLimit prompts
dotnet run -- run --brand "Nyati Elysia"     # only that brand
dotnet run -- run --client "Regency Group"   # only that client's brands
```

`login.cmd` / `run.cmd` in the project folder do the same by double-click
(`run.cmd --brand "Nyati Elysia"` works too). A daily Task Scheduler job
running `run.cmd --headless` is what makes "once per cycle" happen.

## Settings

`appsettings.json`:

| Key | Meaning |
|---|---|
| `ConnectionStrings:Novaritz` | SQL Server connection (local Windows-auth by default) |
| `Capture:BatchLimit` | Max prompts per start, across brands, unless `--limit` is given (a cut-off brand resumes next start) |
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
| `capture_run_id`, `prompt_id`, `LLMType`, `brandId`, `organization_id`, `prompt_key`, `prompt_text`, `intent` | Which cycle run, prompt, LLM, brand and client (`audit_job_id` is the pre-cycle link, kept on old rows) |
| `answer` | The prose of the reply — map widgets, place cards and citation pills removed |
| `citations` | JSON list of `{text, href}` for the source pills the LLM showed |
| `places` | JSON list of `{name, rating, text}` for map/business cards, when any were rendered |
| `raw_text` | The whole message as displayed, untouched |
| `latency_ms`, `captured_at`, `createdDate` | Timing |

A Python implementation with identical behaviour lives alongside this
project outside the repository; keep the two in step when changing the
extraction or the SQL.
