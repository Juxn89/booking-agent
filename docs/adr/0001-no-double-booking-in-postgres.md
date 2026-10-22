# ADR 0001: no double booking, decided by Postgres

Status: accepted, 22 Oct 2026.

## Context

Two customers can want the same slot at the same moment: two browsers, the form and the assistant, a customer
double-clicking. A check in code ("is the slot free?") followed by an insert loses that race: both checks pass,
both inserts land. Serializable isolation would catch it, at the cost of retrying every conflicting transaction
in the application. Locks taken in the application only protect the code paths that remember to take them.

## Decision

The `bookings` table carries an **exclusion constraint**:

```sql
EXCLUDE USING gist (professional_id WITH =, period WITH &&) WHERE (status IN ('Held', 'Confirmed'))
```

`period` is a `tstzrange` (NodaTime `Interval`, half-open), so back-to-back appointments are allowed and any
overlap is not, for any writer, forever. `btree_gist` lets the uuid equality share the GiST index with the range.
A refused insert (`23P01`) becomes `SlotTakenException` and a 409 with the nearest free times.

The availability check before the insert stays: it gives the friendly answer in the common case. The constraint
is what makes the rare case impossible to get wrong.

**New bookings of one professional are saved one at a time**, under a transaction-scoped advisory lock keyed on
the professional. The fifty-request test found why: when fifty overlapping inserts run at once, each one's
constraint check waits for the others' uncommitted rows, Postgres untangles the resulting deadlocks one per
second (`deadlock_timeout`), and the last requests hit the 30-second command timeout. Under the lock each insert
sees the previous one committed and loses at once. Bookings of different professionals never wait for each other.

## Consequences

- Fifty simultaneous requests for one slot: one hold, forty-nine 409s, in about a second. An integration test
  asserts both, with a time limit.
- The constraint does not know about time: a hold that ran out still counts until it is released. The hold use
  case releases the stale holds overlapping its period in the same transaction, and a sweeper releases the rest
  every 30 seconds ([ADR 0002](0002-holds-with-a-ttl.md)).
- A reschedule cannot overlap the booking it replaces, since both take their slot until the new one is confirmed.
  Moving an appointment by less than its own length needs a cancel and a new booking.
- A deadlock (`40P01`) during a booking save is still read as a lost slot, as a defence for writers that bypass
  the lock.
