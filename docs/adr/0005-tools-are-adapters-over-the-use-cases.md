# ADR 0005: the assistant's tools are adapters over the form's use cases

Status: accepted, 23 Oct 2026.

## Context

The assistant books through tools the model calls. A tool can be its own small service: query the database, check
the slot, insert the booking. Then the chat and the form become two implementations of the same rules, and they
drift apart. A tool written that way can also do things the form cannot, and those are exactly the things
nobody reviews.

## Decision

**Each tool is a thin adapter over a use case the form already uses**:

| Tool | Use case |
|---|---|
| `list_services`, `list_professionals` | The catalog `GetCatalog` reads (`ICatalogReader`), loaded once per reply |
| `resolve_date` | `DateExpression.Resolve` in the domain: relative dates are worked out by the server |
| `check_availability` | `FindAvailableSlots`, the same as `GET /v1/professionals/{id}/availability` |
| `hold_slot` | `HoldSlot`, the same as `POST /v1/holds` |

The adapter does three things:

- **Checks the input against a hand-written JSON Schema**, with every property required and nothing extra. The
  tool is declared `strict` to the API, and the adapter checks the input again, because a schema constrains the
  model, not the request.
- **Calls the use case.**
- **Turns its result into an answer the model can act on.** A taken slot comes back with the nearest free times. A
  bad date comes back with "use `resolve_date`". There are never stack traces or ids meant for a person.

Every call is recorded as an `agent_steps` row, with emails masked.

**No tool confirms, cancels or moves a booking.** An architecture test fails if anything in the tools' namespace
depends on `ConfirmHold`, `CancelBooking` or `RescheduleBooking`. `hold_slot` hands the confirmation token to the
widget's card, not to the model: the model learns that a slot was held and that the customer confirms it with the
button ([ADR 0003](0003-the-person-confirms.md)).

The loop is Microsoft.Extensions.AI's `FunctionInvokingChatClient` over the official Anthropic SDK's `IChatClient`.
A meter between the two records each model call's tokens and cost and stops the loop when the conversation's money
runs out. The loop allows eight model calls per reply. The history is stored on the server, so a client cannot
forge a tool's answer. Holding the history in the browser would let it.

## Consequences

- The assistant can do nothing the form cannot, and a fix to a use case fixes both doors.
- Tests of the use cases cover most of what the tools do. The tools' own tests cover the shape of their answers and
  the loop's limits, with a scripted model and no API key.
- The SDK's adapter does not send `strict` for tools in `ChatOptions.Tools`. A decorator moves them into the raw
  request, where it does send it. A test checks the request body.
