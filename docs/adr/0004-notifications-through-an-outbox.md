# ADR 0004: notifications through a transactional outbox

Status: accepted, 23 Oct 2026.

## Context

A confirmed booking must reach the customer: a notice at once, and reminders a day and two hours before. Sending
the email inside the request that confirms the booking ties the two together badly. If the SMTP server is down, either
the confirmation fails for a reason the customer cannot fix, or the booking is saved and the email is lost. Sending
it after the commit, from the same request, loses it whenever the process stops in between. A message broker would
need a second transactional resource next to Postgres.

## Decision

**The notifications are rows in Postgres, written in the same transaction as the change they announce.** A booking
records what happened to it (`BookingChanged`: confirmed, rescheduled, cancelled). When the repository saves the
booking, it turns those changes into `notifications` rows (`NotificationPlanner`): one notice per channel (email
and in-app) and, for an appointment that is on, reminders 24 h and 2 h before, unless their moment has already
passed. If the save fails, nothing was announced. If it succeeds, the announcement cannot get lost.

A background dispatcher drains the outbox every five seconds:

- It **claims** due rows with `FOR UPDATE SKIP LOCKED` and **leases** them for five minutes in a short transaction,
  then sends without holding any lock. A second dispatcher skips leased rows. If a dispatcher dies, its rows come
  back when the lease ends.
- It **records each outcome** as soon as it is known: sent, skipped, or failed with the error. A failure is retried
  after 30 s, 1, 2, 4 and 8 minutes, then left as `Failed` for a person to look at.
- At send time a **reminder for a booking that is no longer confirmed is skipped**. Cancelling or moving a booking
  never has to find and delete its pending reminders.

Delivery is at least once. The in-app channel stages its message in the dispatcher's own unit of work, keyed by the
notification's id, so the message and the "sent" mark commit together, exactly once. An email uses the notification's
id as its Message-ID, so a duplicate after a crash can be recognised.

## Consequences

- A confirmation never fails because the email server is down, and never goes unannounced. Atomicity is
  covered by an integration test: a confirmation whose save loses a race leaves no notification behind.
- Notices reach the customer up to one poll interval late (`Notifications:PollInterval`, 5 s).
- The outbox is also the audit trail: every notification, its attempts and its last error stay in the table.
- Adding WhatsApp is one more `INotificationChannel` and one more entry in `NotificationPlanner.Channels`.
- The texts are in Spanish, for the demo business. A second language would make the template depend on the
  business.
