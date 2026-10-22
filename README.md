# booking-agent

**Work in progress.** Appointment booking for small businesses: customers book by chatting with an assistant
built on Claude Haiku, a person confirms every booking with a button, and a Postgres exclusion constraint makes
double booking impossible.

So far:

- **Availability** (`src/BookingAgent.Domain/Scheduling`): weekly hours per professional, on the business's
  clock, with NodaTime. Slots are laid out on the local clock and last real time, so the daylight-saving
  transitions behave as a receptionist would expect: a skipped hour is never offered, a repeated hour is
  offered once. Taken periods, time off and a minimum notice are honoured.
- **Use case** `FindAvailableSlots`, the one the form and the assistant's `check_availability` tool will share.
- **AppHost** (.NET Aspire): Postgres 18, Mailpit, the API and the web app.

```bash
(cd web && pnpm install)                            # once
dotnet run --project src/BookingAgent.AppHost       # Postgres, Mailpit, API and web (http://localhost:3020)
dotnet test                                         # domain, application and architecture tests
```

Rules for contributors and agents are in [`AGENTS.md`](AGENTS.md).
