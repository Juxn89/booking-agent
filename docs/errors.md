# Errors

Every error is an RFC 9457 Problem Details response with a stable `code`; the `type` links here. Codes are dotted:
the area, then what went wrong.

## Catalog

### catalog-not_found
`catalog.not_found` · 404. No business with that id.

## Availability

### availability-bad_date
`availability.bad_date` · 400. `from` and `to` are plain ISO dates, such as `2026-10-27`.

### availability-bad_range
`availability.bad_range` · 400. The range ends before it starts, or covers 62 days or more.

### availability-not_offered
`availability.not_offered` · 404. The professional does not exist, the service does not, or the professional
does not offer it.

## Bookings

### booking-invalid_customer
`booking.invalid_customer` · 400. A hold needs the customer's name and a plausible email.

### booking-slot_unavailable
`booking.slot_unavailable` · 409. The start asked for is not a free slot of that professional and service right
now: taken, outside their hours, too soon, or not on the business's grid. `alternatives` lists up to three free
starts nearby (ISO 8601 instants, comma-separated).

### booking-slot_taken
`booking.slot_taken` · 409. The slot was free when checked, but another request saved a booking for it a moment
before; Postgres's exclusion constraint refused the second one. `alternatives` as above.

### booking-not_found
`booking.not_found` · 404. No booking with that id, or, when confirming, a token that is not the hold's own: the
answer says nothing about whether the booking exists.

### booking-hold_expired
`booking.hold_expired` · 409. The hold ran out before it was confirmed and has been released; the time may still
be free.

### booking-cancelled
`booking.cancelled` · 409. The booking was cancelled.

### booking-too_late
`booking.too_late` · 409. Too close to the appointment to cancel or move it (the cut-off, two hours by default).

### booking-not_confirmed
`booking.not_confirmed` · 409. Only a confirmed booking can be moved.

### booking-changed
`booking.changed` · 409. The booking changed while the request was handled, e.g. a hold was released at the
moment it was confirmed. Asking again sees the new state.

## Assistant

### assistant-invalid_message
`assistant.invalid_message` · 400. The message is empty or longer than `Assistant:MaxMessageLength` (1000 characters).

### assistant-conversation_not_found
`assistant.conversation_not_found` · 404. No conversation with that id on this business. Start a new one by sending no
`conversationId`.

### assistant-conversation_limit
`assistant.conversation_limit` · 409. The conversation reached its message limit (30) or its spending cap (USD 0.05).
Start a new one, or use the booking form.

### assistant-busy
`assistant.busy` · 409. Another message of the same conversation was answered at the same moment. Send yours again.

### assistant-unavailable
`assistant.unavailable` · 503. The assistant cannot answer: there is no API key, the model call failed, or the day's
budget (`Assistant:DailyBudgetUsd`) is spent. The booking form keeps working.

### assistant-too_many_requests
`assistant.too_many_requests` · 429. More than `AssistantRateLimit:PermitsPerMinute` (10) messages in a minute from one
visitor. `Retry-After` says when to write again.

## Inbox

### inbox-invalid_email
`inbox.invalid_email` · 400. The in-app inbox is read by the email the booking was made with; what was given is not
one. (Development only until customers sign in with a code.)
