# booking-agent · Agent guidelines

Appointment booking for small businesses (clinics, salons, workshops): customers book by chatting with an
assistant built on Claude, a person confirms every booking with a button, and Postgres guarantees that no slot
is ever booked twice. The plan is in juangomezb's `docs/workana/booking-agent/plan.md`.

These rules are binding for any agent (Claude Code, Codex, Copilot) working in this repo.

## Stack (pinned — check `Directory.Packages.props` before adding anything)

- .NET 10 / C# 14 (`global.json`), Central Package Management, `TreatWarningsAsErrors=true`.
- Versions: the newest one that is **at least two weeks old** on the day it is added, verified against
  api.nuget.org, with the date in `Directory.Packages.props`. The web app enforces the same rule through pnpm's
  `minimumReleaseAge`.
- Licences: OSI only. No MediatR, no AutoMapper, no MassTransit.
- Tests: xUnit **v2** (ArchUnitNET's adapter requires it), Testcontainers.
- Local run: the AppHost (.NET Aspire) starts Postgres, Mailpit, the API and the web app.

## Dependency rule

```
Domain  ←  Application  ←  Infrastructure  ←  Api
```

- `Domain`: plain C# **plus NodaTime**, and nothing else: time is this domain. An architecture test fails on
  any other reference.
- `Application`: use cases plus ports. May reference `Microsoft.Extensions.AI.Abstractions`, nothing concrete.
- `Infrastructure`: adapters (EF Core, Npgsql, Anthropic, SMTP).
- `Api`: composition root and endpoints only.

## Time

- Instants are `Instant`, periods `Interval` (half-open: an appointment ending at 10:00 leaves 10:00 free),
  stored as `timestamptz` / `tstzrange`. Never `DateTime`.
- Opening hours are local times on the **business's** clock (`DateTimeZone`), converted with NodaTime at the
  edge of each computation. Relative dates ("next Tuesday") are resolved by the server, never by the model.
- The clock is injected (`IClock`); tests use `NodaTime.Testing.FakeClock`.

## The agent proposes, the person confirms

- Tools are thin adapters over the same use cases the form uses: the agent can do nothing the UI cannot.
- No tool confirms a booking. `hold_slot` holds a slot for a few minutes and returns a summary; the person
  confirms it with a button that calls the API with the hold's one-time token.
- No double booking: a Postgres exclusion constraint decides, not a check in code.

## AI rules

- Model: **`claude-haiku-5-5` only**. Model ids and prices are configuration.
- What a customer writes is **data, not instructions**: never concatenated into the system prompt.
- Every paid call goes through the spending guards and is recorded in `~/.anthropic-budget/ledger.tsv`
  (session `workana-booking-agent`, cap USD 0.50). Evals do not call the API without `--allow-paid`.

## No magic strings or numbers

Durations, limits, thresholds, model ids and prices live in named constants with a comment saying where the
value comes from, or in options classes validated at startup.

## Verification gate

Nothing is done until it ran: `dotnet build` (warnings are errors), `dotnet test`, and
`dotnet format whitespace --verify-no-changes`; in `web/`, `pnpm lint` and `pnpm typecheck`. When you add a
rule-like test, prove it can fail.

## Style

- Tabs for indentation (`.editorconfig`). File-scoped namespaces. `sealed` by default.
- Comments explain *why*, not *what*. No commented-out code.
- Commits and pull requests carry no AI attribution trailer.
