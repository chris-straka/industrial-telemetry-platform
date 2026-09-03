# Why this project maps onto payments

The plan is to retarget this at fintech roles. The architecture barely changes; the
vocabulary and the STAKES do.

```
industrial                          payments
-----------------------------------------------------------------
sensor / equipment            ->    POS terminal, card reader, ATM
edge gateway                  ->    store controller, branch server
ingestion API                 ->    acquirer / processor gateway
telemetry reading             ->    authorization or capture message
anomaly detection (ML.NET)    ->    fraud scoring, velocity checks
EngineTemperature (double)    ->    Amount (DECIMAL, never double)
telemetry-events (Kafka)      ->    payment-authorizations
```

Yes, payment terminals are squarely fintech. Square, SumUp, Adyen and Stripe all ship
terminal hardware plus the software behind it, and payments infrastructure is one of the
largest fintech categories by headcount.

Worth knowing though: not every fintech job is payments. Lending, neobanks, trading,
regtech and personal finance do not have a terminal anywhere. What generalises to ALL of
them is the correctness machinery, not the topology:

- idempotency keys minted at the origin
- at-least-once delivery plus an idempotent consumer
- event time vs processing time
- sequence numbers that make loss detectable
- an audit trail for anything discarded

For a trading firm specifically, lead with ordering, sequence gaps and replay rather than
store-and-forward.

# Money is not a double

`decimal` in C#, `BigDecimal` in Java, or integer minor units (cents) in a `long`.

Binary floating point cannot represent 0.1 exactly, so `0.1 + 0.2 != 0.3`. Accumulate
that over a million transactions and the ledger does not balance. Interviewers notice this
one immediately.

# Card networks really do have two durable layers

The thing that makes the store-and-forward story land harder in payments than in industry:

```
card reader  --->  store controller  --->  acquirer  --->  network  --->  issuer
   DURABLE           DURABLE
```

Both of the first two hops persist locally before acknowledging. Retail terminals support
"offline mode" or "store and forward" explicitly: when the link to the processor is down,
the terminal keeps accepting cards, stores the transactions locally, and forwards them
when connectivity returns. Floor limits, offline PIN verification, and issuer risk rules
exist precisely to bound how much risk the merchant takes on during that window.

That is a real business decision encoded in a durability choice: a shop that stops
accepting cards when the network hiccups loses revenue immediately, so the industry chose
to accept some fraud risk in exchange for staying open.

Contrast the industrial version, where the sensor holds telemetry in RAM and the gateway
is the only durable layer.

## Glossary for the paragraph above

FLOOR LIMIT
  A monetary threshold below which a terminal may approve a sale WITHOUT asking the issuer
  online. Say the floor limit is 30 -- a 20 purchase can be approved locally, a 50 one has
  to go online. Set per merchant and card type according to how much risk the acquirer
  will wear.

OFFLINE PIN
  An EMV chip card can verify the PIN against the CHIP ITSELF rather than sending it to the
  issuer. The card confirms the cardholder is present with no network round trip at all.

THE RISK THEY ACCEPT
  An offline-approved transaction can turn out to be bad: stolen card, closed account, no
  funds. The merchant has already handed over the goods, so they eat the loss or take the
  chargeback. That is the trade -- a shop that stops accepting cards during an outage loses
  revenue with certainty, so the industry chose bounded fraud losses over closed tills.
  Floor limits are the bound.

# The principle

> The durability of a buffer should match the cost of losing one item in it.

That single sentence explains every buffering decision in this repo, and why the fintech
version answers differently:

| item | cost of losing one | correct buffer |
| --- | --- | --- |
| temperature reading | shrug -- another arrives in 2s, the physical process is continuous | RAM, bounded, drop and count |
| payment authorization | chargeback, support call, possibly a regulator | disk, fsync'd, never dropped |

A missed temperature sample is unrecoverable but cheap: you cannot go back and measure the
past, but the past resembled the present closely enough that nobody is harmed. A dropped
payment is unrecoverable AND expensive, because it represents a customer intent that
existed exactly once.

So in the fintech rewrite, the emulator's `Channel<T>` becomes durable storage. You end up
with two store-and-forward layers instead of one -- which looks like duplication until you
notice that is exactly what the real card networks do.

# What gets harder, not easier

Do not claim the fintech version is the same project with renamed fields. Things that are
genuinely harder once money is involved:

- **Reconciliation.** Telemetry has no counterparty. Payments have an acquirer whose
  totals must match yours at end of day, and a process for when they do not.
- **Reversals.** A reading is never un-taken. An authorization gets voided, refunded, or
  charged back, so the ledger needs entries that undo without deleting.
- **Retention and audit.** You cannot drop a poison-pill payment message and move on. It
  goes to a DEAD LETTER QUEUE -- a separate topic where unprocessable messages land with
  their error attached, so a human can inspect and replay them instead of the data simply
  vanishing. The telemetry project now demonstrates publish-before-commit DLQ handling, but a
  payment system would additionally need controlled remediation, replay approval, and much longer
  retention guarantees.
- **PCI scope.** PCI DSS is the card industry's security standard. "Scope" means which of
  your systems fall under its audit: anything that stores, processes or transmits
  cardholder data, especially the PAN (Primary Account Number -- the 16 digits on the
  front). In-scope systems need encryption, access control, retained logs and an annual
  audit, all of which is expensive. So real designs work hard to MINIMISE scope --
  tokenisation swaps the PAN for a meaningless token as early as possible so that
  everything downstream never sees card data and stays out of audit entirely.

Being able to name these is worth more than the rename itself -- it shows you know the
domain has depth rather than assuming a find-and-replace covers it.
