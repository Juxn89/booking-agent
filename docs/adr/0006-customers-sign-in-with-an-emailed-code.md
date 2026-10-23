# ADR 0006: customers sign in with an emailed code

Status: accepted, 23 Oct 2026.

## Context

To see, move or cancel their appointments, customers must show the bookings are theirs. A clinic's customers book
a few times a year. They will not create an account with a password for that, and a password store would be one
more thing to protect. Anyone can type an email into a form, so knowing the email proves nothing.

## Decision

**A six-digit code goes to the email the bookings are under, and the code opens a session.**

- `POST /sign-in/code` always answers 202. A code is sent only when the email has bookings with the business,
  and at most one a minute, so the endpoint can neither reveal who is a customer nor flood an inbox. The code goes
  out at once by SMTP, not through the outbox: the customer is waiting for it.
- A code works for 10 minutes and once, and allows 5 wrong attempts. Only `SHA-256(id:code)` is stored, compared in
  constant time. Code requests and checks are limited to 5 a minute per visitor. Five guesses out of a million
  combinations is a 1 in 200,000 chance per code.
- `POST /sign-in` with the right code starts a 2-hour session. Its token is 256 random bits in an **HttpOnly,
  SameSite=Strict cookie**, and only its hash is stored. Scripts cannot read the cookie, other sites cannot send it,
  and the browser only ever talks to the web app's origin (`/api` is forwarded to the API).
- What a signed-in customer may do is checked in the use cases: `CancelBooking` and `RescheduleBooking` take the
  requesting customer, and someone else's booking reads as not found.

The assistant sees who is signed in: the chat endpoint reads the same cookie. `find_my_bookings`,
`propose_cancel` and `propose_reschedule` need a session. Without one, they answer so, and the chat shows the
sign-in form. **They propose and the customer acts**, as with a new booking ([ADR 0003](0003-the-person-confirms.md)):

- A cancellation is a card with its own Cancelar button, which calls `POST /me/bookings/{id}/cancel`.
- A move holds the new time, and the customer confirms it with the card's button.

The architecture test keeps `ConfirmHold` and `CancelBooking` out of the tools. `RescheduleBooking` is allowed,
since it only holds the new time.

## Consequences

- No passwords and no account sign-up; email is already the channel the bookings live on.
- Whoever controls the customer's inbox controls their bookings, the same as with a password reset.
- The in-app inbox moved behind the session (`GET /me/inbox`), so the development-only endpoint is gone.
- Sessions live in Postgres. A multi-instance deployment needs nothing more, and signing out ends the session
  on the server.
