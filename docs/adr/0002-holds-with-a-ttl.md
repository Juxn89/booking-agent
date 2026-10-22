# ADR 0002: holds that run out

Status: accepted, 22 Oct 2026.

## Context

A conversation takes time: the assistant finds a slot, the customer reads the summary, maybe asks a question.
Without holding the slot meanwhile, it can be gone by the time they agree. Holding it indefinitely lets an
abandoned chat block a professional's afternoon.

## Decision

A booking starts **held** for five minutes (`Bookings:HoldDuration`, between one and thirty). A held booking takes
its slot exactly like a confirmed one, as far as the constraint is concerned, until `hold_expires_at`. From then on
the availability ignores it, the next hold for an overlapping period releases it, and a background sweeper
releases the rest every 30 seconds (`Sweeper:Interval`). A released hold is cancelled with reason `HoldExpired`;
confirming it afterwards is a 409 `booking.hold_expired`.

A hold confirmed at the moment the sweeper releases it is settled by optimistic concurrency on `xmin`: one save
wins, the other answers `booking.changed`.

## Consequences

- Correctness does not depend on the sweeper: a hold that ran out stops counting immediately for availability
  and for new holds. The sweeper only keeps the calendar and the panel tidy.
- Five minutes is a guess at "long enough to read a summary"; the evals and the demo will tell whether it is.
