# How Moiz Mobile POS works

A complete walk through the software: what it does for the shop, how a sale physically travels
from the counter to the database, what every module is for, and which rules are load-bearing.

This is the *explanation*. Three other documents cover different ground and are not repeated here:

| You want | Read |
|---|---|
| The rules an agent or developer must not break while editing | `CLAUDE.md` |
| The binding principles behind those rules | `.specify/memory/constitution.md` |
| What the shop asked for, feature by feature | `specs/001-pos-inventory-ledger/spec.md` |
| How to put it on a server | `docs/deployment.md` |

---

## 1. What this software is

A point-of-sale, inventory and customer-credit system for **Moiz Mobile & Corporation, Danwran
Lodhran** — one mobile-accessories shop, with **two people using it**:

- **The owner (Admin)** — sees everything: cost, profit, reports, the drawer count, user accounts.
- **The salesman (Staff)** — sells, takes payments, handles customer returns, and sees no cost
  and no profit anywhere.

That two-person split is not a generic permission system bolted on afterwards. It is the single
most structural decision in the codebase, and Section 5 explains how it is enforced.

The software answers six questions the shop actually has:

1. What did we sell today, and for how much?
2. What is in stock, and what is running out?
3. Who owes us money (*udhaar*), and how much?
4. What did we pay for the goods, and what did we make?
5. Where did the cash go — does the drawer agree with the day?
6. Can the customer have a copy of their bill?

---

## 2. The shape of the system

```
                      the shop's tablet / phone / PC
                                   │
                          browser (React app)
                                   │  HTTPS, JSON, JWT in the header
                                   ▼
             ┌──────────────────────────────────────────┐
             │   ASP.NET Core 8  —  one project         │
             │                                          │
             │   Api/            controllers, auth      │
             │   Application/    the rules (pure)       │
             │   Domain/         entities, enums        │
             │   Infrastructure/ Dapper, SQL, files     │
             │   Migrator/       DbUp schema scripts    │
             └──────────────────────────────────────────┘
                                   │
                                   ▼
                            MySQL 8 / MariaDB
                            22 tables, all money DECIMAL
```

### Two halves, two repositories

```
backend/src/MoizPos      the API and everything behind it           (backend repository)
backend/tests            three test projects (unit, integration, architecture)
backend/specs            specification, plans, task lists per feature
backend/docs             deployment and this document
frontend/src             the React app the shop actually touches    (frontend repository)
frontend/tests           its test suite
```

The backend is **one .NET project**, not five. Each layer is a folder, and the folder maps to the
namespace it always had — `MoizPos.Domain.Entities` lives in `Domain/Entities`. Layering used to be
enforced by project references; it is now enforced by a **test** that reads every file's `using`
directives (Section 10).

### The layer rule

```
Domain  ──►  nothing            entities, enums, exceptions. No dependencies at all.
Application ──►  Domain         the rules. Pure C#. No SQL, no HTTP, no clock.
Infrastructure ──►  Application Dapper, MySQL, files, backups, hashing.
Api ──►  both                   controllers, validation, authorization, DI wiring.
```

The important consequence: **a service never opens its own database connection.** If a rule needs
SQL, the SQL goes behind an interface in `Application/Abstractions` and is implemented in
`Infrastructure`. That is what every `*WriteRepository` is. A test fails the build if `using Dapper`
or `using MySqlConnector` ever appears under `Application/`.

Why it matters: the shop's rules — what a return is worth, whether a sale is credit, what the
drawer should hold — are all plain functions you can test in milliseconds with no database.

---

## 3. The screens, and who can reach them

Routing lives in `../frontend/src/routes/AppRoutes.tsx`. The sidebar groups them
(`components/AppShell.tsx`).

| Screen | Path | Who | What it is for |
|---|---|---|---|
| **New sale** | `/pos` | Everyone | The counter. The default screen after sign-in. |
| **Products** | `/products` | Everyone | The catalogue. Staff see no cost. Sells too — every row has *Add to cart*. |
| **Categories** | `/categories` | Admin | The category list a product is filed against. |
| **Brands** | `/brands` | Admin | The brand list, including which brands are *local*. |
| **Invoices** | `/invoices` | Everyone | Past bills. Exists chiefly so a **walk-in** sale stays reachable. |
| **Customers** | `/customers` | Everyone | The register: who owes what, and each customer's ledger. |
| **Returns** | `/returns` | Everyone (customer half) | Goods coming back — from a customer, or to a supplier (Admin). |
| **Purchases** | `/purchases` | Admin | Stock coming in. Reveals cost, so Admin only. |
| **Suppliers** | `/suppliers` | Admin | Who we buy from, and what we owe them. |
| **Expenses** | `/expenses` | Admin | Money going out, and **where it came from** (till or bank). |
| **Reports** | `/reports` | Admin | Sales, profit, stock, receivables, payables, expenses. |
| **Day close** | `/day-close` | Admin | Counting the drawer against what the day took. |
| **Dashboard** | `/dashboard` | Admin | The owner's overview. |
| **Admin** | `/admin` | Admin | Users, audit trail, backups. |

The sidebar groups these as **Sell** (open by default) · **Customers & bills** · **Purchasing** ·
**Money** · **Settings**, with Dashboard above the groups. A group whose every item is hidden from
Staff does not render at all, so the salesman sees no empty headings for work that is not theirs.

---

## 4. The life of a sale

This is the path most of the software exists to serve. Follow it once and most of the rest makes
sense.

### 4.1 At the counter

The salesman does one of two things:

- **Scans a barcode** → the product is added to the cart **directly**. A barcode is unambiguous, so
  a confirming tap would cost every sale time and prevent nothing.
- **Types a search** → up to **8 result cards** appear and *nothing enters the cart until Add is
  pressed* — including when exactly one product matched. "The only thing matching what I typed" is
  not "the thing in the customer's hand".

These two cannot be collapsed back together: `ProductLookup` is a discriminated union
(`{kind:'barcode'}` vs `{kind:'matches'}`) precisely because collapsing them is what once made the
software add the first search hit sight-unseen.

**Sale type** — Retail or Wholesale — is chosen once at the top of the sale and **re-prices every
line already in the cart**. The server decides which price that is (Section 6.2); the client never
picks.

### 4.2 The cart

The cart lives **above the router** (`CartProvider`), not inside the counter screen. It used to be
local state, which meant walking to the Products list to fetch a second item destroyed it — so "go
back and add more" could never work, because there was nothing to come back to.

- Persisted in **`sessionStorage` with a same-trading-day expiry**, never `localStorage`. A cart is
  worth surviving an accidental refresh; it is not worth surviving overnight.
- A **restored** cart is **re-priced before it can be sold**. This is not belt-and-braces: the
  server takes the unit price *from the client*, so a cart carrying yesterday's price would sell at
  that price and nothing would flag it.
- Adding from the Products list **re-reads the price** rather than taking the row's, because that
  list is priced at the counter rate and the sale in progress may be wholesale. If the price cannot
  be read, the item is **not added** — adding at an unknown price is worse than not adding.
- **One product is one line.** The merge rule (`withItem`) is shared by the counter and the Products
  list, because the server refuses duplicates and two lines would each check stock against the same
  locked row and oversell.

### 4.3 Checkout

Pressing **Proceed to sale** opens `CheckoutModal`, which asks — *once, at the end* — who is buying
and how they are paying.

**Who:** Walk-in · Existing customer · New customer. The **existing-customer picker is the whole
point of this component**: before it, the counter could only *create* a customer, so selling to the
same person twice on udhaar produced two records with two balances, and the owner chasing a debt saw
half of it. The picker shows what each customer already owes before adding to it.

**How:** Cash · Bank transfer · JazzCash · EasyPaisa · Raast · Credit (udhaar) · Partial.

- `amountPaid` is **derived, never typed twice** — a cash-type method settles the bill, `Credit` pays
  nothing, `Partial` is what the shopkeeper entered. Two controls that can contradict each other
  will.
- **Account number and transaction ID appear only for a transfer.** They are the **customer's**
  account — the money came *from* there — and they are optional, because the counter must never wait
  while someone hunts for a reference.
- Udhaar and part payment **require a customer**, mirroring the server.

### 4.4 What the server does

`POST /api/invoices` → `InvoiceService.CreateAsync`. **One transaction, in a deliberate order:**

```
 1  refuse an empty sale, and refuse the same product twice
 2  quick-create the customer if one was typed        (outside the transaction — see below)
 3  BEGIN
 4  idempotency key already seen?  → return the original sale, do not sell twice
 5  SELECT ... FOR UPDATE every product row, ORDERED BY ID
 6  recompute every total from scratch          ← the client's figures are discarded
 7  check stock for EVERY line before writing anything
 8  a sale that owes money must name a customer
 9  ONLY AN ADMIN MAY LEAVE MONEY OUTSTANDING    ← the credit rule
10  a cash sale may not carry a payment reference
11  ── first write happens here ──
    insert invoice · insert lines (snapshotting cost) · deduct stock ·
    append stock movements · append audit rows · update the customer's balance ·
    append the ledger entry
12  COMMIT
```

Five things in that list are worth understanding properly.

**Locks are taken in id order** (step 5). Two sales for the last unit then acquire locks in the same
sequence, so one succeeds and one is refused — instead of deadlocking or overselling.

**Every total is recomputed** (step 6). Whatever the browser calculated is thrown away. The server
prices the sale from the rows it just locked.

**Every check sits before the first write** (steps 7–10). A refusal therefore leaves *no* invoice, no
stock movement and no balance change. There is a test named
`A_refused_credit_sale_changes_absolutely_nothing` that asserts all four.

**Each line snapshots the product's cost at that moment** (step 11) into
`invoice_items.unit_cost_price`. Under the shop's latest-cost rule (Section 6.1) a later purchase
overwrites `products.cost_price` — so without this snapshot, **every past month's profit would
silently rewrite itself every time stock was bought.**

**A quick-created customer is committed *before* the sale** (step 2), deliberately. If the sale then
fails, the shop keeps a harmless new contact rather than losing the details the shopkeeper just
typed.

### 4.5 After the sale

Saving **clears the cart but keeps the receipt** on screen. The receipt is the proof the sale
happened and carries the payment-proof upload, so it is cleared only by the explicit **New sale**
button.

From the receipt the shopkeeper can **Print**, **Download PDF**, **send on WhatsApp**, or **send by
SMS** (Section 8), and — for a non-cash sale — attach a **payment-proof screenshot**.

---

## 5. Authority: who may do what

### Sign-in

`POST /api/auth/login` returns a **JWT access token (60 minutes)** and a **refresh token (30 days)**.
Passwords are **PBKDF2-SHA256, 210,000 iterations, 16-byte salt, 32-byte hash**. Refresh tokens are
stored **hashed**, never in the clear.

### Three layers of enforcement

1. **The sidebar hides what you cannot use.** A convenience only — it prevents confusion, not
   access.
2. **`[Authorize(Policy = Policies.AdminOnly)]` on the endpoint.** This is the real control for
   whole screens: Purchases, Suppliers, Expenses, Reports, Day close, Dashboard, Admin, and every
   write to Categories and Brands.
3. **Architecture tests that fail the build.** `StaffDtoExposureTests` fails if a Staff-reachable
   endpoint ever declares a field named for cost, profit or wholesale. You cannot leak cost by
   accident; the build stops first.

### The one rule that cannot be an `[Authorize]` policy

> **Only an Admin may complete a sale that leaves any amount outstanding.**

It lives *inside* `InvoiceService.CreateAsync`, not on the endpoint, and it must stay there:

- Whether a sale is credit is a property of the **server-recomputed total**, which does not exist
  until the calculator has run. A policy would have to trust the client's own `amountPaid` — the one
  number an attacker controls.
- It is **never keyed off `PaymentMethod`.** `Credit` and `Partial` are descriptive labels. A sale
  labelled `Cash` whose payment falls short **is still credit**, and a test asserts exactly that.
- `CreateAsync` takes the caller's role **as an argument** rather than reading ambient context, so
  the rule stays unit-testable and cannot be bypassed by a caller that forgets to set one.

A Staff user selling for full payment is unaffected, and **can still record recovery payments** —
collecting a debt does not create one.

---

## 6. Inventory and money

### 6.1 The two business rules that drive the design

**Rule 1 — Latest purchase cost.** When stock is bought, that purchase's unit cost **replaces** the
product's cost for *every unit on hand*. Not a weighted average.

> Buy 10 at 800. Sell 5. Buy 10 at 850.
> All 15 units are now costed at **850 — not 825.**

The owner chose this deliberately: profit is measured against **what it costs to replace the goods
today**. `StockRules.NextCostPrice` is where it lives, and a test asserts the owner's own worked
example, explicitly checking the answer is *not* 825. If you ever find yourself "fixing" this into an
average — stop and ask.

**Rule 2 — Current sale price.** Old stock sells at today's price, not the price in force when it was
bought.

### 6.2 Retail and wholesale

Every invoice carries a `sale_type` of `Retail` or `Wholesale`.

- **The type is recorded, never inferred.** A discounted retail sale and a wholesale sale can reach
  the same figure, so the price cannot tell them apart afterwards.
- **Pricing is resolved server-side.** A wholesale sale is quoted `wholesale_price`, falling back to
  `sale_price` where none is set. This is why **Staff never receive a `wholesalePrice` field** — the
  salesman is told the one price that applies rather than handed the price list.
- `saleType` must be threaded through **every** read the counter makes — search, get-by-id,
  get-by-barcode. Omitting it on any one silently re-quotes the counter price. That bug shipped once.

Separately, every **customer** carries a `sale_type` too — but that one is a **standing label set by
the owner**, never derived from invoices. A wholesale party's occasional counter purchase must not
reclassify them.

### 6.3 Stock

Every movement is recorded in `stock_movements` with a reason: `Purchase`, `Sale`, `SaleReturn`,
`PurchaseReturn`, `Adjustment`. Stock can never go below zero, and the check happens while the row
is locked.

### 6.4 Product search

Search splits what is typed into words, and lists a product only when **every** word appears in its
name, model, brand or category. So `c type`, `type-c` and `typec` all find "Type-C", and
`oppo charger` finds only Oppo chargers.

- Rules live in `ProductSearchTerms` (pure): split on non-alphanumerics, lower-case, strip one
  trailing `s` from words of 4+ characters, de-duplicate, cap at 8 words.
- **Each field is normalised on its own** in SQL and **never concatenated first** — concatenating
  would let a word match across the boundary between two fields.
- A full barcode is still matched exactly and whole, as an alternative.
- Normalisation happens **at query time**, not from a stored column, so a brand or category rename
  can never leave search stale. A performance test guards the cost across 5,000 products.
- **One search serves Products, POS and Purchases.** A change to it is a change to all three.

---

## 7. Customers, udhaar and returns

### 7.1 The ledger

Every customer has a running `outstanding_balance` and a `ledger_entries` trail. Entry types:
`Invoice`, `Payment`, `SaleReturn`, `Adjustment`, `OpeningBalance`.

**The invariant:** replaying `bill_amount − paid_amount` down the ledger reproduces
`outstanding_balance`. Tests assert it across the whole database.

### 7.2 Opening balances

A customer can carry what they owed **on paper, before this software**. Admin only.

> **Recording it a second time is a CORRECTION, never a second debt.**

The balance moves by `new − old`. Applying the requested amount instead would **double what the
customer owes** — the single most damaging way this feature can fail, and invisible until the
customer disputes it.

- `opening_balance` is **nullable on purpose**: "never recorded" and "recorded as zero" are different
  facts, and only the first makes the next save a correction.
- A first recording writes an `OpeningBalance` entry; a correction appends an `Adjustment` carrying
  the old and new figures and **leaves the original untouched**.

### 7.3 What a returned unit is worth

This is subtle, and getting it wrong was a real bug at the counter.

> **A return refunds what the customer paid, not what the line was billed at.**

An invoice line is priced *before* the order discount. One charger at 600 on an invoice discounted by
10 stores `line_total = 600`, while the customer handed over 590. `ReturnPricing` spreads the order
discount across the lines in proportion to their value.

Refunding the billed price hands back money that was never taken — and returning every unit then adds
up to more than the sale was worth. That is exactly what produced
*"Returning 600.00 exceeds the invoice's remaining value of 590.00"*. With the discount spread, a
full return settles the invoice **precisely**.

**The discount is recorded on the return, not merely subtracted.** A return row holds all three
figures — billed 600, refunded 575, adjusted 25 — so the screen can tell the customer exactly what
happened months later. In the owner's words: *amount adjustment is the main feature.* Showing only
the refund leaves the customer arguing that a 600 item came back as 575.

### 7.4 Finding a return

Neither tab needs an invoice number in hand. **Return item** finds a returnable sale or purchase by
**product name** — matching against lines that still have quantity left to return, newest first,
capped at 8. Every result names the exact product and its new stock, so the confirmation reads the
product name and updated quantity back to the shopkeeper rather than just "it worked".

Customer returns are open to any signed-in user (the salesman takes them at the counter). **Supplier
returns are Admin only** — that route reveals cost and payables.

---

## 8. Giving the customer their bill

Print · Download PDF · WhatsApp · SMS — from the counter receipt, the Invoices screen, and the
customer ledger.

- **Both send routes are deep links. Nothing is sent by the server.** `wa.me` and `sms:` prepare a
  message in the counter device's own app and the shopkeeper taps Send. The shop holds no messaging
  account, registers no sender id, and **pays nothing per message.**
- **SMS is the fallback, not the default** — 160 characters against a long link means two or three
  charged parts.
- **The figures go in the message body, not only behind the link.** Most customers never tap it, and
  an acknowledgement that only works when opened acknowledges nothing.
- A payment message states the **ledger's balance at that payment**, never today's balance. The two
  part company the moment the customer buys again, and a re-shared receipt that restates itself
  contradicts the shop's own register.
- A settled account is said **in words** — not "Remaining: Rs 0.00", which is true and reads like a
  fault.
- **Print uses the same bytes the customer receives.** A separate print-styled view is a second
  rendering, and two renderings drift into a printed bill that disagrees with the sent one.

### The one unauthenticated endpoint

`GET /api/public/documents/{token}` serves a customer their own receipt. A shop customer holds no
credential, and `wa.me` cannot attach a file — so the receipt travels as a link.

Containment, all of it:

- **≥256-bit token, stored only as a SHA-256 hash**
- resolves to exactly **one** document
- **expires** (30 days by default), and is **revocable** by the Admin
- **rate-limited** and **access-logged**
- a listing returns **ids, never tokens** — showing a live link on a screen turns a bystander into a
  link holder
- **an architecture test fails the build if a second anonymous endpoint ever appears**

And a second guard: `SharedDocumentExposureTests` fails the build if a shared-document model ever
gains a cost, profit, supplier or **payment-proof** field. A template can only print what the model
carries.

Payment proofs live in their own directory precisely so one can never be served by a rule meant for
receipts.

---

## 9. The owner's controls

### 9.1 Dashboard

Sales, purchases, profit, expenses, receivables, payables, stock value, returns, and the **udhaar
split** — how much of the period's credit was full udhaar versus part-paid.

**These extra cards are visibility, not new money.** Every rupee is already inside the totals beside
them. Adding either to `CreditSales` or `TotalReceivables` counts the same debt twice, so the "Owed
to shop" card carries a note saying it already includes them — because adding them is the obvious
wrong next step. A test asserts that the two halves sum exactly to the period's credit sales.

The split is **by the money, never by `payment_method`**: full udhaar is `amount_paid = 0`, part paid
is `amount_paid > 0`, both need `amount_remaining > 0`. Same reasoning as the credit rule.

### 9.2 Reports

Sales · sales by salesman · sales by type · sales drill-down · profit · profit by product · purchases
· stock · stock movements · receivables · payables · expenses.

**Sales by salesman** answers "who took the money and who gave the discounts" — deliberately **no
cost and no profit**. Cash taken and credit given are **separate columns**, because they are
different risks: money in the drawer tonight versus money that walked out of the shop. Discount
counts both line and order discounts, since order discount alone understates what was given away.

### 9.3 Day close — counting the drawer

```
   opening float
 + cash sales
 + cash received against udhaar
 − cash refunded on returns
 − cash paid out (expenses from the till)
 − cash paid to suppliers
 ─────────────────────────────────────────
 = what should be in the drawer
```

**This is the only control the shop has over physical cash.** Every other figure reconciles against
itself: a salesman can take a cash sale, hand over the goods, record it *perfectly*, and pocket the
notes — and no report will ever disagree, because the sale **was** recorded correctly. Counting the
drawer against what the day took is the only thing that makes that visible.

- **Admin only.** A short the responsible person can close away is not a control.
- **Figures are snapshotted at closing, never recomputed.** A return taken tomorrow must not rewrite
  what was counted last night. A closing is evidence of one evening.
- **One closing per day**, enforced by a unique key — otherwise a short could be closed away and
  reopened at a more comfortable figure.
- **Cash only.** A bank transfer never entered the drawer; counting it would show a false short every
  day and teach the owner to ignore the difference.
- **Nothing is rounded.** A paisa short is still short — smoothing hides exactly the small repeated
  differences that are the point.
- **Only two figures are typed:** the opening float and what was counted. Everything else comes from
  the server. A figure you can type over is a figure you can fudge.
- **A difference is shown, never accused.** The `note` field is where "Rs 300 to the delivery boy,
  not entered" goes. Whether those notes stop appearing is how the owner learns the recording
  discipline has taken hold.

**Supplier cash was the gap.** The first version only knew about till expenses, so handing a supplier
Rs 5,000 from the till reported a Rs 5,000 short with nothing to explain it — precisely the false
accusation this feature exists to avoid. It is now its own line, kept separate from expenses because
buying stock and paying the electricity bill are different questions.

**Every expense states where its money came from** — `Till` or `Bank` — required on the form and
`NOT NULL` in the column. Only `Till` is subtracted. The field **starts unanswered**: defaulting to
Till would quietly drop every bank payment into the drawer calculation.

### 9.4 Admin

**Users** — create, reset a password, deactivate. **Audit trail** — `audit_entries` records
old-value/new-value for stock and balance changes, with who and when. **Backups** — a MySQL dump on a
schedule, retained 30 days.

> **A backup includes the product images.** The database holds only *paths*, so a database-only
> restore brings back a catalogue whose every photograph is gone for good. A `-images.zip` is written
> beside each dump, and pruning retires the two together.

---

## 10. How correctness is kept

### Test-driven, and the tests are load-bearing

At last full run: **822 backend tests** (19 architecture · 295 unit · 508 integration) and **518
frontend tests** across 44 files, with `tsc --noEmit` clean. Both suites must pass before anything
is merged.

### Architecture tests — the ones that fail the *build*

These are not coverage. They are structural rules a compiler cannot express:

| Test | Refuses |
|---|---|
| `LayeringTests` | A `using` that breaks the layer rule. Verified: adding `using MoizPos.Application.Services;` to a domain entity reddens two tests. |
| `A_service_never_opens_its_own_database_connection` | `using Dapper` / `using MySqlConnector` under `Application/` |
| `StaffDtoExposureTests` | A Staff-reachable endpoint declaring cost, profit or wholesale data |
| `SharedDocumentExposureTests` | A customer-facing document model gaining cost, profit, supplier or proof data |
| The anonymous-endpoint guard | A second `[AllowAnonymous]` endpoint |
| `Every_source_file_sits_in_a_known_layer` | A new folder outside the five layers — silently unchecked otherwise |
| The nav-icon guard | A new screen shipping without a sidebar icon (it happened twice) |
| Money-type tests | Any entity or calculator exposing a `double` or `float` amount |

Several of these have been **deliberately broken and reverted** to prove they actually catch what
they claim to.

### The non-negotiables

- **Money is `decimal`.** Never `double` or `float`, anywhere near a price, total or balance.
  `DECIMAL(12,2)` in MySQL; `DECIMAL(12,4)` for cost.
- **Every stock/balance/invoice mutation runs in one transaction**, locking affected rows with
  `SELECT ... FOR UPDATE` **ordered by id** before checking stock. Partial state is worse than no
  system.
- **The server recomputes all totals.** Client totals are discarded.
- **Cost and profit never reach a Staff principal.**
- **Schema changes go through DbUp** as a new numbered script. Never edit an applied script; never
  hand-edit a shared database.

### Time

Everything is stored in **UTC**. Reporting periods resolve against **Asia/Karachi** (+05:00, no DST)
through `PeriodResolver`, which returns **half-open ranges** so a sale lands in exactly one bucket.
Services inject `IClock` and never call `DateTime.UtcNow` — which is what makes "what happened on the
23rd" testable.

---

## 11. The database

22 tables. Grouped by what they are for:

| Area | Tables |
|---|---|
| People | `users`, `refresh_tokens` |
| Catalogue | `products`, `categories`, `brands` |
| Stock | `stock_movements` |
| Buying | `suppliers`, `purchases`, `supplier_payments`, `purchase_returns` |
| Selling | `invoices`, `invoice_items`, `sale_returns`, `sale_return_items` |
| Credit | `customers`, `customer_payments`, `ledger_entries` |
| Money out | `expenses`, `expense_categories` |
| Controls | `day_closings`, `audit_entries` |
| Sharing | `document_tokens` |

Schema history is 28 numbered DbUp scripts in `src/MoizPos/Migrator/Scripts/`, applied by:

```bash
dotnet run --project src/MoizPos -- migrate
```

Each one is a written record of *why* the change was needed — several read as post-mortems of bugs
found in the shop.

---

## 12. Running it

```bash
# once
mysql -u root -p < docs/create-databases.sql
dotnet run --project src/MoizPos -- migrate

# day to day
dotnet run --project src/MoizPos     # http://localhost:5080
cd ../frontend && npm run dev                   # http://localhost:5173

# before merging anything — both must pass
dotnet test                      
cd ../frontend && npm run test && npx tsc --noEmit
```

**The GitHub repository is public.** No file holding a credential may be tracked.
`appsettings.Development.json`, `appsettings.Production.json` and `appsettings.*.local.json` are
gitignored and hold this machine's connection strings; the `*.example.json` files are the committed
templates a fresh clone copies from. Only `appsettings.json` — which carries no secrets — is tracked.

Integration tests need MySQL running. They create and drop their own schema in `moizpos_test` and
**refuse to run against a database whose name lacks "test"**.

In production the API also **serves the built React app**, so the shop runs on one origin.

---

## 13. If you change one thing, know this

The short list of things that look wrong and are not, and things that look safe and are not:

1. **Latest cost is not a bug.** 850, not 825. Ask before "fixing" it.
2. **The credit check cannot move to the endpoint.** It needs a total that does not exist yet.
3. **Never decide anything from `payment_method`.** It is a label. Read the recomputed money.
4. **Recording an opening balance twice is a correction.** Applying it doubles a customer's debt.
5. **A return refunds what was paid**, not what was billed.
6. **Add a column to an invoice SELECT and you must add it to `ToInvoice()`** — that mapper is
   hand-written and drops anything you forget, in silence.
7. **Pass enums to Dapper as `.ToString()`.** No type handler is registered, so a bare enum writes as
   an *int* and any `CHECK (col IN (...))` rejects it.
8. **Never join a profit query to `products.cost_price`.** Use `invoice_items.unit_cost_price`, or
   every past month's profit rewrites itself the next time stock is bought.
9. **A database-only backup loses every product photograph.**
10. **`saleType` must reach every product read**, or the counter silently re-quotes retail prices on
    a wholesale sale.

`CLAUDE.md` carries the full trap table — every entry in it caused a real bug during the build.
