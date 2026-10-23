# ADR 0007: the business's panel, live over SignalR

Status: accepted, 23 Oct 2026.

## Context

The staff need to see each professional's week: what is held, what is confirmed, what came through the assistant,
and why. A booking made in the chat should appear in the panel at once, without anyone reloading. The demo's key
moment is exactly that.

## Decision

**Saved changes are announced, and the panel reloads its week.**

- The booking repository is where every change to a booking passes: hold, confirmation, move, cancellation, expiry
  by the sweeper. After a successful save it publishes the changed booking ids per business through a port,
  `ICalendarUpdates`. The API implements the port with a SignalR hub (`/hubs/calendar`, one group per business).
  Hosts without a panel, such as the evals, announce nothing. A failed announcement is logged, never the
  booking's failure.
- The panel does not apply deltas. It reloads the week it shows (`GET /panel/calendar`), so the calendar is always
  what the database says. A missed message costs nothing more than a stale view until the next one, and a
  reconnect reloads too.
- The assistant announces its holds once more when it saves the turn. Its steps are stored after the hold, so the
  first announcement arrives before the panel can tell the hold came from the chat.
- **Access:** the staff enter the panel's access key (`PANEL_ACCESS_KEY`, from the git-ignored `.env` through the
  AppHost). It is compared in constant time and limited to 5 attempts a minute per visitor.
  - The session is the business id and an expiry, sealed with ASP.NET Data Protection into an HttpOnly,
    SameSite=Strict cookie, and nothing is stored. The hub admits a connection only with that session.
  - Without a configured key the panel answers 503: an unset key never opens it.
- **The "why" of a booking:** the panel finds the `hold_slot` or `propose_reschedule` step whose output names the
  booking. It then shows that conversation's transcript and every model and tool call, with latency, tokens and
  cost.

## Consequences

- The panel is right by construction: it only ever shows a fresh read. The cost is one query per change per open
  panel, negligible at a clinic's scale.
- One shared key is right for a small business and a demo; it is not per-person. Staff accounts would replace the
  key form and keep everything else.
- Several API instances would need a SignalR backplane and shared Data Protection keys. With one instance, as now,
  neither is needed.
- In the browser, SignalR negotiates through the web app's `/api` proxy and falls back to long polling where
  WebSockets cannot pass. A test uses the same long-polling protocol with no client package (the 10.0.12 client
  is affected by GHSA-334f-vvp4-7g6p).
