# Working in this repository

Runtime guidance for anyone — human or agent — changing this codebase. The authority is
[.specify/memory/constitution.md](.specify/memory/constitution.md); this file is the practical
summary plus the traps that are easy to fall into.

## What this is

A point-of-sale, inventory and customer-credit (udhaar) system for **Moiz Mobile & Corporation,
Danwran Lodhran** — a single mobile-accessories shop. Two users: the owner (Admin) and a
salesman (Staff).

**Two repositories**, cloned side by side:

```
backend/    (this repo, MobileAccessoriesBack)        ASP.NET Core 8 · Dapper · MySQL 8
  src/ tests/   the API and its three test projects
  specs/        the specification, plan and task list for every feature — both halves
  docs/         deployment and operational notes
frontend/   (Mobile-Accessories-Frontend)              React 18 · TypeScript · Vite
```

The specs, docs and this file live here because they were written for the whole system; the
frontend repo's own `CLAUDE.md` carries its working rules and points back here for the rest.

The backend is **one project**, `src/MoizPos`, holding every layer in its own folder —
`Domain/`, `Application/`, `Infrastructure/`, `Api/`, `Migrator/` — plus one test project per kind
of test. Folders map to the namespaces they always had, so `MoizPos.Domain.Entities` is
`Domain/Entities`.

## Commands

```bash
# database (once)
mysql -u root -p < docs/create-databases.sql
dotnet run --project src/MoizPos -- migrate

# run
dotnet run --project src/MoizPos                  # http://localhost:5080
cd ../frontend && npm run dev                     # http://localhost:5173

# test — both must pass before anything is merged
dotnet test
cd ../frontend && npm run test && npx tsc --noEmit
```

**The GitHub repository is public.** No file holding a credential may be tracked.
`appsettings.Development.json`, `appsettings.Production.json` and `appsettings.*.local.json` are all
gitignored and hold this machine's connection strings; `appsettings.Development.example.json` and
`appsettings.Production.example.json` are the committed templates a fresh clone copies from.
Only `appsettings.json` — which carries no secrets — is tracked.

Integration tests need MySQL running. They create and drop their own schema in `moizpos_test`
and refuse to run against a database whose name lacks "test".

## Product search

Search splits what is typed into words; a product is listed only when **every** word appears in its
name, model, brand or category. `c type`, `type-c` and `typec` all find "Type-C", and `oppo charger`
finds only Oppo chargers (feature 003).

- **The rules live in `ProductSearchTerms`** (Application, pure): split on non-alphanumerics,
  lower-case, strip one trailing `s` from words of **4+ characters**, de-duplicate, cap at 8 words.
  A search of only one-letter words throws `SearchTooShortException` (400 `VALIDATION_FAILED`).
- **Each field is normalised on its own** in SQL — `REGEXP_REPLACE(LOWER(col), '[^a-z0-9]', '')` —
  and **never concatenated first**. Concatenating would let a word match across the boundary
  between two fields.
- A full barcode is still matched exactly and whole, as an alternative to the word match.
- **Search is shared by the Products, POS and Purchases screens.** A change to it is a change to
  all three. Since feature 005 the POS shows the first 8 results as cards rather than taking the
  first one, so result order (by name) decides what the salesman is offered.
- It normalises at query time rather than from a stored column so a brand or category rename can
  never leave search stale. `A_multi_word_search_across_five_thousand_products_is_under_a_second`
  guards the cost; if it ever fails, the stored-column fallback is documented in
  `specs/003-product-filters-search/plan.md` — do not loosen the bound.

## Credit authority

**Only an Admin may complete a sale that leaves any amount outstanding** (FR-051, FR-052). A
Staff user selling for full payment is unaffected, and can still record recovery payments — 
collecting a debt does not create one.

The rule lives in `InvoiceService.CreateAsync`, applied to the **server-recomputed**
`totals.AmountRemaining`, after pricing and the stock check and **before the first write**.

- **It cannot be an `[Authorize]` policy on the endpoint.** Whether a sale is credit is a property
  of the recomputed total, which does not exist until `InvoiceCalculator` has run. A policy would
  have to trust the client's own `amountPaid` — the one number an attacker controls. A sale
  labelled `Cash` whose payment falls short is still credit, and a test asserts exactly that.
- **Never key it off `PaymentMethod`.** `Credit` and `Partial` are descriptive labels only.
- Because the check sits before any write, a refusal leaves no invoice, no stock movement and no
  balance change. `A_refused_credit_sale_changes_absolutely_nothing` asserts all four.
- `CreateAsync` takes the caller's `UserRole` as an argument rather than reading ambient context,
  so the rule stays unit-testable and cannot be bypassed by a caller that forgets to set one.

## Opening balances

A customer can carry an `opening_balance` — what they owed on paper before this software
(FR-065). Recorded via `PUT /api/customers/{id}/opening-balance`, Admin only.

**Recording it a second time is a CORRECTION, never a second debt.** The balance moves by
`new − old` (`OpeningBalanceRules.Delta`). Applying the requested amount instead would double what
the customer owes — the single most damaging way this feature can fail, and invisible until the
customer disputes it. `Recording_it_again_corrects_the_figure_and_never_doubles_the_debt` guards it.

- `opening_balance` is **nullable on purpose**: "never recorded" and "recorded as zero" are
  different facts, and only the first makes the next save a correction.
- A first recording writes an `OpeningBalance` ledger entry; a correction appends an `Adjustment`
  carrying the old and new figures in `note` and leaves the original entry untouched.
- The ledger's invariant still holds: replaying `bill_amount − paid_amount` reproduces
  `outstanding_balance`.
- The opening entry is **not back-dated** ahead of existing entries — `balance_after` is
  persisted, and rewriting it to improve display order would trade a real invariant for a
  cosmetic one. For a customer entered from the register before trading, it is first anyway.

## Retail and wholesale sales

Every invoice carries a `sale_type` of `Retail` or `Wholesale` (migration `0014`, defaulting
existing rows to Retail). The owner reads the day split between the two, and clicking either
total opens the sales behind it — invoice, time, customer, and the salesman who sold it.

- **The type is recorded, never inferred.** A discounted retail sale and a wholesale sale can
  reach the same figure, so the price cannot tell them apart afterwards.
- **Pricing is resolved server-side.** `ProductQuery.SaleType` decides which price comes back as
  `SalePrice`; a wholesale sale is quoted `wholesale_price`, falling back to `sale_price` where no
  wholesale price is set. This is why Staff never receive a `wholesalePrice` field — `wholesale`
  is on the architecture test's forbidden-for-Staff list (FR-040), so the salesman is told the one
  price that applies rather than handed the price list.
- **`saleType` must be threaded through every read the counter makes** — search, get-by-id and
  get-by-barcode. Omitting it on any one of them silently re-quotes the counter price; that bug
  shipped once and `Reading_one_product_honours_the_sale_type` now guards it.
- Reports: `GET /api/reports/sales-by-type` (both halves, zeroes included) and
  `GET /api/reports/sales-list` (the drill-down; omit `saleType` for the whole day).

## Product pictures

A product may carry one optional picture (feature 005). `products.image_path` holds the full-size
image's path; **the thumbnail has no column** — its path is derived by inserting `_thumb` before
the extension, by `ProductImagePaths.ThumbnailFor` on the server and `thumbnailPathFor` in
`ProductPicture.tsx` on the client. Two derivations of one stored fact cannot drift; two stored
columns can.

- **Uploading decodes the image** rather than copying the bytes through. The content type is the
  client's claim; a file that is not really an image would otherwise sit in the catalogue failing
  to render on every screen. Decoding is also what produces the thumbnail, so it is not extra work.
- A thumbnail fits inside 150×150 preserving aspect ratio, and **a smaller image is never
  enlarged** — upscaling invents detail and makes the "thumbnail" larger than the original.
- **Deleting an image deletes its thumbnail too.** A replaced picture that leaves its thumbnail
  behind is unreachable: nothing reads it and nothing ever deletes it.
- **Lists load thumbnails, never full images.** Only `ProductDetail` passes `size="full"`. Getting
  this wrong is invisible locally and ruinous over the shop's connection.
- **Backups include the image folder** (`ProductImageArchive`, a `-images.zip` beside each dump).
  The database holds only paths, so a database-only restore brings back a catalogue whose every
  photograph is gone for good. `PruneAsync` retires the zips alongside the dumps.

## The cart, and the counter after a sale

**The cart lives above the router** (`CartProvider`), not inside `PosScreen`. It was local state
until now, so walking to the Products list to fetch a second item destroyed it — which is why
"go back and add more" could never work: there was nothing left to come back to.

- **`useCart()` falls back to local state when no provider is present.** That is deliberate:
  `PosScreen` stays a function of its props and its cart maths remains testable in isolation,
  which is how every existing counter test renders it. Do not make the provider mandatory.
- **Persistence is `sessionStorage` with a same-trading-day expiry** (`cartStorage.ts`), never
  `localStorage`. A cart is worth surviving an accidental refresh; it is not worth surviving
  overnight.
- **A restored cart is re-priced before it can be sold.** `needsReprice` is raised only for lines
  that came back from storage. This is not belt-and-braces: `InvoiceService` takes the **unit
  price from the client**, so a cart carrying a price from before a purchase changed it would
  sell at the old figure and nothing would flag it.
- **The Products list sells too.** Every row has Add to cart, and the counter has "Add more
  items" leading back — a round trip the cart now survives. `CartBadge` shows the sale in
  progress and returns to the counter; it is hidden entirely when the cart is empty.
- **Adding from the Products list RE-READS the price** (`productApi.get(id, saleType)`) instead
  of taking the row's. That list is priced at the counter rate, and the sale being built may be
  a wholesale one — using the row's price would quietly sell wholesale goods at retail. If the
  price cannot be read the item is **not** added: adding at an unknown price is worse.
- **The merge rule lives in `withItem`** in the cart store, shared by the counter and the
  Products list. One product is one line — the server refuses two, and each would check stock
  against the same locked row and oversell.
- **Saving a sale clears the cart but keeps the receipt.** The receipt is the proof the sale
  happened and carries the payment-proof upload, so it is cleared only by the explicit **New
  sale** button. The old screen emptied the cart on save, which disabled the Save button (it is
  disabled when there is nothing to sell) and left a success banner sitting above a dead
  control — nothing was broken, but the screen read as frozen. A disabled Save now always says
  why, beside it.

## Checkout — who is buying, and how they are paying

Asked once, at the end, in `CheckoutModal` — not while the cart is being built. The counter's
button is **"Proceed to sale"**; the modal's is **"Complete sale"**.

- **The existing-customer picker is the point of this component.** Before it the counter could
  only *create* a customer, so selling to the same person twice on udhaar produced two records
  with two balances, and the owner chasing a debt saw half of it. Walk-in / Existing / New —
  and the picker shows what each customer already owes before adding to it.
- **Udhaar and part payment require a customer**, mirroring the server (FR-017). A walk-in is
  refused with an explanation rather than silently allowed.
- A part payment must be **more than zero and less than the total**. Anything else is a full
  payment mislabelled.
- **`amountPaid` is derived, never typed twice**: a cash-type method settles the bill, `Credit`
  pays nothing, `Partial` is what the shopkeeper entered. Two controls that can contradict each
  other will.
- **Account number and transaction ID appear only for a transfer** (`BankTransfer`, `JazzCash`,
  `EasyPaisa`, `Raast`). Cash in the drawer came from nowhere else and is never asked. They are
  optional — the counter must never wait while someone hunts for a reference.
- They are stored on the invoice (`payment_account_number`, `payment_transaction_id`,
  migration `0021`) and are the **customer's** account, not the shop's: the money came *from*
  there. **A Cash sale is refused both (422)**, the same rule and the same reasoning as the
  proof screenshot — and the check sits in `InvoiceService` **before the first write**, so a
  refusal leaves no invoice and no stock movement. Length is bounded by the validator; whether
  a reference is *allowed* is the service's call, so the rule lives in one place.
- Blank is stored as **NULL, never an empty string** (`Trimmed`), so "not given" is one value in
  the column rather than two every later query must handle.
- **Keep Raast.** It is in the `payment_method` enum and in `PAYMENT_METHODS`; dropping it from
  the UI would make it unsellable while remaining a legal stored value.
- `handleConfirm` **re-throws** on failure so the modal stays open and shows the error with the
  cart intact. Swallowing it would close the modal on a refusal and lose the sale.

## Udhaar on the dashboard

`UdhaarSalesCount` / `UdhaarSalesAmount` and `PartPaidSalesCount` / `PartPaidRemaining` show how
the period's `CreditSales` was made up.

- **Visibility, not new money.** Every rupee is already inside `CreditSales` and
  `TotalReceivables`. Adding either card to either total counts the same debt twice — the same
  discipline `TotalSaleReturns` follows. `The_two_halves_add_up_to_the_periods_credit_sales`
  asserts the reconciliation, and the "Owed to shop" card carries a note saying it already
  includes them, because adding them is the obvious wrong next step.
- **Split by the money, never by `payment_method`.** Full udhaar is `amount_paid = 0`; part paid
  is `amount_paid > 0`; both require `amount_remaining > 0`. A sale labelled `Cash` whose payment
  fell short is still credit — the same reasoning as the credit-authority rule, and a test
  asserts exactly that case.
- The two halves are mutually exclusive by construction, so nothing is counted twice and they
  always sum to `CreditSales`.

## The POS picker

Since feature 005 a **typed search at the counter offers candidates; it does not add one**. Up to
8 result cards appear and nothing enters the cart until Add is pressed — including when exactly
one product matched, because "the only thing matching what I typed" is not "the thing in the
customer's hand".

- **A scanned barcode still adds directly**, unchanged. It is unambiguous, so a confirming click
  would cost every sale time and prevent nothing. `ProductLookup` is a discriminated union
  (`{kind:'barcode'}` vs `{kind:'matches'}`) precisely so these two cannot be collapsed back
  together — that collapse is what made the old code add the first search hit sight-unseen.
- The search button is labelled **"Search", not "Add"** — it no longer puts anything in the cart.
- Sale type stays one choice at the top of the sale and still re-prices existing lines.

## Transaction proofs (every non-cash transaction)

The owner's rule: **every transaction except cash is kept with its proof.** Five kinds carry one
screenshot each (migration `0018` for sales, `0030` for the rest):

| Kind (`/api/proofs/{kind}/{id}`) | Column | Who may attach and view |
|---|---|---|
| `sale` | `invoices.payment_proof_path` | any signed-in user |
| `customer-payment` (udhaar recovered) | `customer_payments.payment_proof_path` | any signed-in user |
| `refund` (after a sale return) | `sale_returns.refund_proof_path` | any signed-in user |
| `supplier-payment` | `supplier_payments.payment_proof_path` | Admin only |
| `expense` (Bank only) | `expenses.payment_proof_path` | Admin only |

- **One controller for all five** (`ProofsController`): `POST` attaches or replaces, `GET` streams
  the image. `TransactionProofRules` (pure) decides who and when: cash is refused, a Till expense
  is refused, a return that refunded nothing is refused.
- **Optional when the transaction is saved, never blocking it.** `GET /api/proofs/missing`
  (Admin) — the **Proof missing** screen under Money — lists every transfer-paid transaction still
  without one, so "every transaction is provable" holds without the counter waiting on a picture.
- **Viewing needs a signed-in request.** Proofs live outside wwwroot and are never static files;
  `ImageStorageService.OpenPaymentProof` refuses any path outside the proof directory, including a
  sibling folder that merely shares its prefix.
- **Backups carry them** as `-proofs.zip` beside each dump (`BackupOptions.PaymentProofDirectory`,
  the same `Storage:PaymentProofRoot` uploads write to). Before `0030` they were not backed up at
  all — a restore would have lost every screenshot.
- **Deleting an expense deletes its proof file.** A proof whose row is gone is unreachable.
- **A refund must say how it was paid** (`RecordSaleReturnRequest.RefundMethod`, 422 when money
  goes back without one). Day close subtracts only `refund_method = 'Cash'`; assuming cash showed
  every JazzCash refund as the drawer running **over**. `0030` marked every earlier refund Cash,
  which is how day close had already counted it.
- **A Bank expense says which way it went** (`expenses.payment_method`) — required by the form,
  optional on the API (descriptive; day close does not read it), refused for Till.

The original sale-proof notes still hold:

- **A Cash sale is refused one** (422). The money was counted into the drawer; a "proof" there
  would be evidence of nothing and would invite proof on sales that never had a transfer.
- **Attached after the sale, never during.** The upload is addressed to an invoice id that does
  not exist until the sale is saved, and the counter must never wait on a picture.
- **Any signed-in user may attach and view one** — the salesman is who takes the payment. Narrow
  it later if the owner wants.
- **Never served through the public receipt link.** That link is unauthenticated and
  customer-facing; a payment screenshot is internal evidence. Proofs live in their own directory
  (`content/payment-proofs`) precisely so one can never be served by a rule meant for the other.
- No thumbnail: a proof is opened full-size in a dispute or not at all.
- **`InvoiceWithCustomerName.ToInvoice()` is hand-written.** A column added to the SELECT but not
  to that method is dropped in silence — that is exactly how `payment_proof_path` first read back
  null on a proof that had stored correctly.

## Customer sale type (retail/wholesale filtering)

Every customer carries a `sale_type` of `Retail` or `Wholesale` (migration `0019`, feature 004),
defaulting to Retail. The Customers screen offers All/Retail/Wholesale beside search, as
alternatives — never combined with each other, but combined with search and "owes money".

- **It is a standing label, never derived from invoices.** A wholesale party's occasional
  counter purchase must not reclassify them, and a customer with no sales yet must not have a
  type invented. This is the opposite of how `sale_type` works on `invoices` — that one is
  recorded per sale; this one is set once, by the owner, on the customer.
- **Admin-only to set** (FR-105), same pattern as opening balances: the request DTO carries a
  nullable `SaleType?`, and the controller applies it only when `CurrentUser.Role(User) ==
  Admin`, silently ignoring it otherwise. This keeps Staff's quick-create-during-a-sale
  (FR-017) working unchanged rather than rejecting the request outright.
- **Every enum column in this schema is written as a string explicitly** — `.ToString()` in the
  INSERT/UPDATE parameters, never the bare entity. Dapper has no enum type handler registered
  (`DapperConfig.Apply()`), so passing an enum property straight through serializes it as its
  underlying **int**, which a `CHECK (col IN ('Retail','Wholesale'))` constraint rejects at write
  time — a real bug this feature hit, caught immediately by a failing insert.
- The day's retail/wholesale takings split (feature 004 of `sale_type` on `invoices`) is
  unrelated and unchanged — a different question answered by Reports, not by this filter.

## Returns — history and dashboard visibility

Recording a return (feature: Returns module) was only half the job — until now there was no way
to see what had been returned. Two GET endpoints and two Dashboard fields close that gap.

- **`GET /api/sale-returns`** and **`GET /api/purchase-returns`** list what the POST endpoints
  already recorded — one row per product returned, with date, invoice/supplier, quantity, value,
  and (for sale returns) refund due. Same authority as the POST siblings: sale returns open to
  any signed-in user, purchase returns Admin-only (that route reveals cost and payables).
- **Purchase returns scope to a supplier** via `?supplierId=`, because a return always goes back
  to whichever supplier the goods came from — "every return to everyone" is rarely the question
  being asked. The Purchase return screen has a supplier picker that filters both the
  purchase list and the return history together.
- **Dashboard gained `totalSaleReturns` / `totalPurchaseReturns`.** These are visibility only —
  the money was already correct: `invoices.net_amount` (Sales) and the purchases total both
  already subtract returns, per the existing `TotalPurchasesAsync` query. The new fields do not
  change any total; they just show the owner the return figure that was already folded in,
  instead of leaving it invisible inside a smaller number.

## Return item — find a return by product name

Both return tabs have a **Return item** button (next to Find invoice / next to the supplier
dropdown) that finds a returnable sale or purchase by product name, so nobody needs an invoice
number in hand.

- **`GET /api/sale-returns/find?search=`** — matches product name against still-returnable
  invoice lines (`quantity > returned_qty`), newest first, capped at 8. Same 2-letter minimum as
  every other search in this app.
- **`GET /api/purchases?productSearch=`** — same idea for supplier returns, combined with the
  existing `supplierId` scope.
- **Every return result now names the exact product and its new stock** —
  `RecordSaleReturnResult.Items` (one entry per product) and
  `RecordPurchaseReturnResult.ProductName`. The confirmation banner reads the product name and
  updated quantity back to the shopkeeper, not just "it worked".
- `Purchase` gained `ProductName` (joined from `products`), because a return-by-name flow that
  cannot show the name it matched on defeats its own purpose.

## What a returned unit is worth

**A return refunds what the customer paid, not what the line was billed at.** An invoice line is
priced *before* the order discount: one charger at 600 on an invoice discounted by 10 stores
`line_total = 600` while the customer handed over 590. `ReturnPricing` (Application, pure) spreads
the order discount across the lines in proportion to their value — a line's net worth is
`line_total × (total / subtotal)`, and one unit is that over the quantity sold.

- **Why it matters:** refunding the billed price hands back money that was never taken, and
  returning every unit then adds up to more than the sale was worth. That is exactly what produced
  *"Returning 600.00 exceeds the invoice's remaining value of 590.00"* at the counter. With the
  discount spread, a full return settles at the invoice total precisely —
  `Returning_every_unit_of_a_discounted_sale_settles_the_invoice_exactly` asserts it.
- **The discount is RECORDED on the return, never just subtracted.** Migration `0020` adds
  `unit_refund_price` and `discount_total` to `sale_return_items`, so a return holds all three
  figures: billed 600, refunded 575, adjusted 25. `unit_sale_price` keeps its original meaning —
  what the line was **billed** at. Re-deriving the discount from the invoice at read time would
  re-price history the moment anyone edits the sale.
- **The adjustment is shown, in words.** The owner's phrase for it: *amount adjustment is the
  main feature*. The form carries Price / Discount / Refund per unit as three columns, an
  Item value → less discount → Value returned summary, and a note telling the salesman what to
  say. `RecordSaleReturnResult.TotalBilled`/`TotalDiscount` put the same figures in the
  confirmation, and the history list keeps them visible months later. Showing only the refund
  leaves the customer arguing that a 600 item came back as 575.
- **Line discounts need no spreading** — they are already inside `line_total`. Never apply one
  twice by subtracting `line_discount` again.
- **The cap against `net_amount` in `ReturnService` is now a backstop only.** It can no longer
  fire from a discount; leave it for rounding and figures edited outside the app.
- `sale_return_items.unit_sale_price` records the **effective** price, so `unit × quantity`
  reconciles with the money that actually moved.
- The search list (`GET /api/sale-returns/find`) carries the money with each result. The endpoint
  returns **`ReturnableSaleLine`**, mapped from the raw `ReturnableLineRow` by
  `ReturnableSaleLine.From` — the one place a search result's amounts are worked out, from the
  same `ReturnPricing` the write path charges at. Never re-derive a price in TypeScript: two
  derivations of one rule drift, and the screen would promise a refund the server does not pay.
- **Every amount on `ReturnableSaleLine` is a stored property, never a computed getter.** A
  getter is easy to leave out of a response and impossible to see missing from the server side;
  when `RefundPerUnit` and friends were getters, every figure on the Returns screen rendered as
  **`Rs NaN`** and no return could be recorded at all, while every server-side test still passed.
  `Return_item_search_carries_what_each_unit_is_worth_back` asserts the actual JSON, which is the
  only assertion that would have caught it.
- The names say what they are, in shop words:
  `quantitySold` / `quantityReturned` / `quantityAvailable`, `unitSalePrice` (what the receipt
  shows), **`refundPerUnit`** (what a unit is worth back), `discountPerUnit`, `maxRefund`.

## Categories and Brands

A product is filed against a row in the `categories` table (required) and optionally one in
`brands`. Both are modules the owner maintains at `/categories` and `/brands`; neither is free
text any more. Migration `0013` converted the old `products.category`/`products.brand` columns
into foreign keys, migrating every distinct value that existed rather than discarding it.

- Read is open to any signed-in user (the product list shows both); every write is `AdminOnly`.
- Names are unique, and the collation is case- and accent-insensitive, so "Cables" and "cables"
  collide by design — that is the point of the module.
- Nothing is hard-deleted. The foreign keys are `ON DELETE RESTRICT` and the service retires a
  row instead: products keep their label, and the retired row simply stops being offered.
- `ProductRow.Category` and `.Brand` are the joined **names**, for display; `CategoryId` and
  `BrandId` are what a write accepts.
- **Local goods are an ordinary brand, not a flag.** The owner handles them by creating a brand
  named for local stock and filing those products under it, exactly like Vivo or Oppo. The
  `is_local` tick-box on the brand form, the Local/Imported column on the Brands list and the
  "Local brands only" filter on Products were all **removed from the UI** at the owner's request —
  a brand is a name, and one more yes/no on every brand earned nothing the name did not.
- `brands.is_local` and the API's `localOnly` filter **still exist** and are untouched. Nothing
  sets the column any more, so it reads `FALSE` for every new brand and `localOnly` returns an
  empty list. They were left rather than dropped because removing a column needs a migration and
  buys nothing; if the owner ever wants the distinction back, the plumbing is already there.
  **Do not re-add the UI without asking** — its removal was deliberate, and three tests pin it:
  `offers no local-brands filter, and never asks the server for one`,
  `shows no Local or Imported label against any brand`, `offers no local tick-box on either screen`.

## Every product names a brand

Migration `0027` made `products.brand_id` **`NOT NULL`**, and `0028` left it that way.

- The owner's rule: goods with no well-known maker are filed under a brand created for them
  (a "local goods" brand), **not left blank**. So "no brand" is not a state a product can be in.
- `ProductUpsertRequest.BrandId` is a plain `long`, required by the validator.
- **`0027` also introduced a `brand_categories` link — that was REVERTED by `0028`.** The owner
  tried "a brand carries a set of categories" and asked for it out again: the extra setup step
  earned nothing at this shop's size. A category is chosen from the whole list, as before.
  `0027` was not edited, because an applied script is history; `0028` is its reversal.
- **Do not re-introduce the link without asking.** It cost a schema change in both directions.

## Adding a product, then stocking it

**A product is a catalogue entry — what the thing IS. Its first purchase is what makes it
sellable.** Add product → record a purchase → sell.

- **`ProductUpsertRequest` carries NO price and NO quantity.** Not zeroed — *absent*. Two screens
  that can both price an item is one more than the shop can keep straight, and the moment someone
  uses the wrong one the counter quotes a figure nobody intended.
  `Carries_no_price_or_quantity_at_all` asserts the properties do not exist.
- A new product is created with `cost_price`, `sale_price`, `wholesale_price`, `retail_price` and
  `quantity_on_hand` all **0**, and the product UPDATE statement **does not mention a price
  column** — so editing what a product IS can never change what it is worth.
- **The purchase sets all of it, in one statement**: quantity, cost, and both selling prices
  (`UpdateProductStockAndPricingAsync`). One statement on purpose — a product whose quantity rose
  but whose price did not is the half-stocked state the counter cannot sell from.
- **`NewRetailPrice` is required on a FIRST stocking**, optional afterwards. "First" means
  `product.SalePrice <= 0`, read from the **locked row** so two first-purchases racing cannot both
  see "no price yet". Omitted on a repeat delivery means *leave the price standing*.
- **The shop has exactly THREE prices**, and since migration `0029` so does the table:
  `cost_price` (what we paid), `wholesale_price` (what a bulk buyer pays) and `retail_price`
  (what a walk-in pays). A fourth column, `sale_price`, used to be the one pricing actually read
  while `retail_price` sat written-but-never-read — the names said the opposite of the truth.
  `0029` drops it and `retail_price` is now what a retail sale is quoted from.
- **`salePrice` on the API is not a column.** It is the RESOLVED price for the sale being made —
  `retail_price` or `wholesale_price`, whichever the `saleType` asked for — computed per request.
  Staff receive only that; the three stored prices reach the Admin DTO alone (FR-040).
- **`InvoiceService` refuses to sell a product whose `SalePrice <= 0`**, checked against the
  locked row beside the stock check. Necessary because the client supplies the unit price and
  would otherwise happily sell an unpriced product at whatever it claimed.
- Tests that need something sellable but are not about stocking use
  `ApiFactory.StockProductAsync(...)`. The real rule lives in `Products/StockingFlowTests`.

## The two business rules that drive the design

**1. Latest purchase cost.** When stock is bought, that purchase's unit cost *replaces* the
product's cost for every unit on hand — not a weighted average. Buying 10 at 800, selling 5,
then buying 10 at 850 leaves all 15 units costed at **850, not 825**. The owner chose this
deliberately: profit is measured against what it costs to replace the goods today.

**2. Current sale price.** Old stock sells at today's price, not the price in force when it was
bought.

`StockRules.NextCostPrice` is where rule 1 lives, and
`PurchaseCostRuleTests.Latest_purchase_cost_replaces_the_cost_of_all_stock_on_hand` asserts the
owner's own worked example, explicitly checking the answer is *not* 825. If you find yourself
"fixing" that into an average, stop and ask.

## Traps

Each of these caused a real bug during the build.

| Trap | What happens | Guard |
|---|---|---|
| Putting a price or a quantity back on the product form | Two ways to price an item, and they disagree the first time anyone uses the wrong one. It also re-opens editing history: an edit could re-price stock bought months ago | They are absent from `ProductUpsertRequest`, and the product UPDATE names no price column. `Carries_no_price_or_quantity_at_all` fails if they return |
| Re-adding a fourth price column | `0029` removed `sale_price` precisely because two columns for one price cannot be kept honest — one gets written and the other gets read | Three prices: `cost_price`, `wholesale_price`, `retail_price`. `salePrice` in the API is resolved, never stored |
| `DROP CHECK` in a migration | MySQL-only syntax. **The shop's server is MariaDB 10.5 and the test database is MySQL 8.0**, so it passes locally and fails on the live server — which is exactly how `0029` failed on its first run | `DROP CONSTRAINT`, understood by MariaDB 10.2+ and MySQL 8.0.19+ |
| Running `migrate` with `--no-build` after editing a script | The `.sql` files are **embedded resources**, so the migrator reruns the previous copy and fails identically | Build before migrating |
| Joining profit queries to `products.cost_price` | Every past month's profit silently rewrites itself whenever stock is bought | Always use `invoice_items.unit_cost_price` — the cost snapshotted at sale |
| Returning `ProductAdminDto` typed as `ProductStaffDto` | System.Text.Json serialises the *declared* type, so the Admin silently loses cost data | `[JsonDerivedType]` on the base — do not remove it |
| Dapper + positional records | Ids are `BIGINT UNSIGNED` (ulong) but the domain uses `long`; Dapper matches records by exact constructor signature and will not convert | Materialised types use init-only properties, never `record Foo(...)` |
| A new validator in the `Api` project | Never runs unless its assembly is scanned | `Program.cs` scans **both** Application and Api |
| A new enum on a request | Rejected as a 400 unless serialised by name | `JsonStringEnumConverter` is registered; keep it |
| Deciding "is this a credit sale?" from the request | The client controls `amountPaid` and `paymentMethod`, so the rule is evadable | Read `totals.AmountRemaining` after the server recomputes, never the request |
| Sending a search the server now refuses | A one-letter search returns 400; the POS surfaced it as an error at the counter | `PosPage` treats `VALIDATION_FAILED` from search as "no product found"; `ProductsPage` shows a hint and doesn't send it |
| Selecting `products.category` directly | The column no longer exists — it is `category_id`, joined to `categories` | Join `categories c ON c.id = p.category_id` and select `c.name` |
| Adding a folder outside the five layers | It is silently unchecked by the layering tests | `Every_source_file_sits_in_a_known_layer` fails until you add the layer to `LayeringTests.Allowed` and say what it may depend on |
| A config source added in `Program.cs` after `CreateBuilder` | It outranks what the test factory injects, and the connection string is read moments later — so the whole suite silently runs against whatever this machine names, including the live server | `ApiFactory` sets `SkipMachineLocalSettings`; `HostConfigurationTests` asserts the **data layer's own** connection, because `IConfiguration` showed the right database while the repositories held the wrong one |
| Two cart lines for one product | Each checks stock against the same locked row and can oversell | `InvoiceService` refuses duplicates; the POS merges them |
| Storing the thumbnail's path in its own column | It duplicates a value already derivable from `image_path`, and the two drift the moment a fix-up script updates one | Derive it — `ProductImagePaths.ThumbnailFor` / `thumbnailPathFor` |
| Rendering a full-size image in a list | Invisible on a dev machine; over the shop's connection the Products grid stalls | `ProductPicture` loads the thumbnail unless passed `size="full"` |
| Backing up the database alone | Every product comes back with a placeholder — the picture files were never copied | `ProductImageArchive` writes a `-images.zip` beside each dump |
| Adding an invoice column without touching `ToInvoice()` | The SELECT returns it and the hand-written mapper silently drops it; the API reads back null | Add the property to `InvoiceWithCustomerName` **and** its `ToInvoice()` |
| A supplier "opening balance" column | `payable_balance` has an asserted invariant (purchases − payments − returns); an opening figure belongs to none of those terms | It needs a ledger entry and correction-by-delta, like customer opening balances — its own feature |
| Posting FormData through the API client | The client forces `Content-Type: application/json`, so multipart uploads lose their boundary and the server answers 415 | The request interceptor deletes that header when the body is `FormData` |
| Building a test tag from `Guid…Where(char.IsLetter)` | A GUID's letters are only `a`–`f`, so six characters is a 46k-symbol space, not 300M. Seeds collide and fail a **random** test with a duplicate-key error that looks like a flake in whatever test drew the short straw | Map each hex digit onto its own letter (`HexToLetter` in `ProductSearchTests`) — letters-only, full 16 symbols |
| "Tidying" the rail groups | **Sell** holds *New sale* and *Sale return*; **Purchasing** holds Purchases, Suppliers, *Supplier ledger* and *Purchase return* — the owner split the old Returns screen so each return sits with the trade it reverses. For a Staff user **Inventory** holds only *Products*, which looks like a mistake and is not. Purchasing is all Admin-only, so it does not render for a salesman | `keeps selling and taking back together under Sell`, `puts purchase returns under Purchasing…`, `keeps the catalogue together under Inventory` and `shows a salesman Products under Inventory, and nothing else there`. `/returns` redirects to `/sale-returns` for old bookmarks |
| Letting minimal hosting insert `UseRouting()` for you | It goes at the **start** of the pipeline, so `MapFallbackToFile("{*path}")` selects an endpoint before the static-file middlewares run — and `StaticFileMiddleware` skips a request that already has one. **Every** static file then answers `200 text/html` with index.html: product pictures, the app's own JS and CSS, the favicon. The page loads and does nothing | `app.UseRouting()` is called explicitly **after** both `UseStaticFiles` calls. `BundledAppStaticFileTests` fails if it moves; the guard is verified by removing the line |
| Assuming the suite covers the deployed pipeline | The test host's content root is an empty temp directory, so it has no `wwwroot` — `counterAppIsBundled` is false and neither the SPA fallback nor wwwroot static files are ever registered. The shape the shop actually runs in went untested for as long as it existed | `BundledAppStaticFileTests` builds its own host WITH a wwwroot. Anything about serving the bundled app belongs there |
| Adding a route without a rail icon | Icons live in `index.css` keyed on `href`, not in markup (adding an element would change each link's text, which the nav tests read). A new module ships looking unfinished beside the rest — it happened twice | `AppShell.test.tsx` now reads the stylesheet and fails naming any link with no `::before` rule. The guard is verified: removing one reddens it |
| Treating `NOT NULL` on an **ENUM** as "the column refuses a missing value" | It refuses an explicit NULL and nothing else. **Strict mode does not help**: measured on MySQL 8.0.40 with `STRICT_ALL_TABLES` on, an INSERT omitting `expenses.payment_source` stored the ENUM's **first member** with no error, because MySQL treats it as an implicit default | You cannot stop the coercion, so choose what it lands on. Migration `0025` orders the members `('Bank','Till')` so a forgotten source is **excluded** from the drawer rather than deducted from it. `A_write_that_forgets_the_source_never_lands_on_cash` guards the order |
| Reordering ENUM members without `ALGORITHM=COPY` | An in-place change can reinterpret the stored index rather than re-mapping by string — turning every `Till` into `Bank` | `0025` states `ALGORITHM=COPY`, and `Both_sources_still_read_back_as_they_were_written` asserts values survived |
| Seeding rows with raw SQL that the service would have refused | The invariant suite scans the WHOLE database, so one bad seed row reddens `Every_invoice_that_owes_money_names_a_customer` — and the test database is never dropped, so it stays red until the row is removed | Seed valid data: a sale with `amount_remaining > 0` must name a customer (FR-017), even when inserted directly |
| An integration test that DELETEs to isolate itself | The test database is shared by every class in `ApiCollection`. Clearing `invoices`/`stock_movements` to reason about one day's figures reddened an unrelated reporting test | Seed your own data and assert deltas, or work on a private past date — never clear a shared table |
| A test fixture keyed off "today" for a once-only resource | The test database is created once and KEPT, so rows survive between runs. `day_closings` is unique per date, so days derived from today collided with the previous run and every close returned 422 | Draw a random base offset once per run (`RunBaseOffset` in `DayClosingTests`) so each run works on untouched dates |
| Reading a MySQL `DATE` into a `DateOnly` | Throws at materialisation — Dapper has no built-in handler | `DapperConfig` registers `DateOnlyHandler`; a trading day is a date with no time and no zone, and must never be carried as a `DateTime` something can shift across midnight |
| Building the WhatsApp link on `wa.me` | `wa.me` only redirects to `api.whatsapp.com`, and on WhatsApp Desktop/Web that hop turns every 4-byte emoji (🏪 📍 👤 🔔) into `�` — the shop's messages arrived that way while the encoded bytes were perfect | `WhatsAppLinkBuilder` links straight to `api.whatsapp.com/send?phone=…&text=…`; `Never_goes_through_the_wa_me_redirect_that_breaks_emoji` guards it |
| Passing an enum property straight to Dapper in an INSERT/UPDATE | No enum type handler is registered, so it writes as the underlying int — any `CHECK (col IN (...))` on that column then rejects every write | Spell it out: `saleType = customer.SaleType.ToString()`, matching every other enum column in this schema |

## Non-negotiables

- **Money is `decimal`.** Never `double` or `float`, anywhere near a price, total or balance.
  `DECIMAL(12,2)` in MySQL, `DECIMAL(12,4)` for cost. A test asserts no entity or calculator
  exposes a floating-point amount.
- **Every stock/balance/invoice mutation runs in one transaction** via `IUnitOfWorkFactory`, and
  locks affected rows with `SELECT ... FOR UPDATE` **ordered by id** before checking stock.
  Partial state is worse than no system.
- **The server recomputes all totals** from the line items it stored. Client totals are discarded.
- **Cost and profit never reach a Staff principal.** Separate DTOs, `AdminOnly` per endpoint, and
  architecture tests that fail the build if a Staff-reachable endpoint declares cost data.
- **TDD.** A failing test comes first. A phase is done only when unit tests pass, integration
  tests pass, and nothing previously green broke.
- **Schema changes go through DbUp**, as a new numbered script in
  `src/MoizPos/Migrator/Scripts/`. Never edit an applied script; never hand-edit a
  shared database.

## Layering

`Domain` → nothing. `Application` → `Domain`. `Infrastructure` → `Application`. `Api` → both.

**Enforced by `LayeringTests`, not by the compiler.** The backend was five projects until
2026-09-19, and project references made these rules impossible to break. It is now one project, so
`LayeringTests` reads every file's namespace and its `using` directives and fails the build on a
violation instead. The guard is verified: adding `using MoizPos.Application.Services;` to a domain
entity reddens two tests.

**If you need SQL inside a service, put it behind an interface in `Application/Abstractions` and
implement it in `Infrastructure`** — that is what the `*WriteRepository` types are, and
`A_service_never_opens_its_own_database_connection` enforces it by refusing `using Dapper` or
`using MySqlConnector` anywhere under `Application/`.

## Time

Stored in UTC. Reporting periods resolve against `Asia/Karachi` (+05:00, no DST) through
`PeriodResolver`, which returns half-open ranges so a sale lands in exactly one bucket. Inject
`IClock`; never call `DateTime.UtcNow` in a service.

## Giving a customer their bill

Print, Download, WhatsApp and SMS, from the counter receipt, the Invoices screen and the customer
ledger. All of it was built and tested long before any screen offered it (feature 009).

- **Both send routes are deep links; nothing is sent by the server.** `wa.me` and `sms:` prepare a
  message in the counter device's own app and the shopkeeper taps Send. The shop holds no
  messaging account, registers no sender id, and pays nothing per message. Do not replace either
  with a gateway without deciding to take on that cost.
- **SMS is the fallback, not the default — and it is plain on purpose.** WhatsApp carries emoji,
  bold amounts, the receipt link, the shop's number and the Asyntex credit. SMS carries the
  figures only: **no link, no contact line, no credit, no emoji** (the owner's choice). One emoji
  re-encodes an SMS as UCS-2 — 70 characters a part instead of 160 — so
  `An_sms_carries_no_link_no_contact_line_no_credit_and_no_emoji` pins it to the GSM alphabet.
- **Udhaar is due one month after the oldest purchase still unpaid** (`UdhaarDueDate`, pure).
  Payments settle the oldest debt first, so a small part payment never restarts the clock; past
  the date it rolls forward a month at a time and counts months overdue. Always counted from the
  purchase with `AddMonths`, never chained from the last due date, or one February moves every
  31st to the 28th for good.
- **A bill or receipt states its due date as of its own moment** (sale day; the ledger up to that
  payment), the same discipline as `balance_after`. Only the **reminder**
  (`GET /api/customers/{id}/reminder`, any signed-in user, 422 when nothing is owed) speaks about
  today, so only it can say "overdue".
- **Number normalisation is shared** (`WhatsAppLinkBuilder.NormaliseNumber`, reused by
  `SmsLinkBuilder`). Two copies would eventually disagree about what a valid number is.
- **Message wording lives in `DocumentMessages`, not on either channel builder.** It moved there
  when SMS arrived needing identical words. The figures go in the **body**, not only behind the
  link: most customers never tap it, and an acknowledgement that only works when opened
  acknowledges nothing.
- **A payment message states the ledger's `balance_after`, never `customers.outstanding_balance`.**
  The two part company the moment the customer buys again, and a re-shared receipt that restates
  itself contradicts the shop's own register. This was a real bug;
  `The_balance_is_the_one_recorded_at_that_payment_not_todays` guards it.
- **A settled account is said in words**, not as "Remaining: Rs 0.00" — true, and reads like a
  fault.
- **Print uses the same bytes the customer receives.** A print-styled view is a second rendering,
  and two renderings drift into a printed bill that disagrees with the sent one.
- **The PDF routes are `/api/invoices/{id}/pdf` and `/api/customer-payments/{id}/pdf`** — flat
  under `/api`, NOT under `/documents/`, despite sitting on `DocumentsController`. Only the
  share-link routes carry that prefix. The client was once written from a contract document that
  invented the prefix, so every Print and Download 404'd while the whole suite stayed green: the
  server tests built their own URLs, and the client test asserted the URL the client had chosen.
  `ClientDocumentRouteTests` now holds the literal strings the browser sends.
- **PDF responses bypass the envelope.** `fetchBlob`, never `unwrap` — and the response
  interceptor reads an error body back out of its Blob, or a failed document request reports a
  generic "unexpected error" while the server's real reason sits unread. Mirror image of the
  `FormData` trap.
- **A walk-in types a number for one send.** No customer record is created and nothing is stored
  — a one-off buyer is not a relationship. **A typed number never overrides a stored one**
  (FR-124): a stored number is the shop's record of where a bill went.
- **A share-link listing returns ids, never tokens.** Only the hash is stored; showing a live link
  on an authenticated screen turns a bystander into a link holder. Revocation is by id, is
  Admin-only, and is **idempotent without moving the original timestamp** — that timestamp is
  evidence of when the withdrawal happened.
- **`SharedDocumentExposureTests` fails the build if a document model gains a cost, profit,
  supplier or proof field.** A template can only print what the model carries. The guard is
  verified: adding `UnitCostPrice` to `InvoiceDocument` reddens it.
- **The Invoices screen exists for walk-ins.** A sale with no customer appears in no ledger, so
  without it most counter sales become unreachable once the counter resets. `customerName` comes
  back **null** for one, not an invented label — wording "nobody" is the screen's job.

## Counting the drawer

`day_closings` (migration `0023`) records what the day took, what was counted, and the difference.
**The only control the shop has over physical cash** — every other figure reconciles against
itself, so a cash sale recorded perfectly and pocketed leaves no trace in any report.

- **Admin only.** This is the control *over* the salesman's handling of cash; a short the
  responsible person can close away is not a control.
- **Figures are snapshotted at closing, never recomputed.** A return taken tomorrow against a sale
  made today must not rewrite what was counted last night. A closing is evidence of one evening.
- **One closing per day**, enforced by a unique key — otherwise a short could be closed away and
  reopened at a more comfortable figure.
- **Cash only.** A bank transfer never entered the drawer; counting it would show a false short
  every day and teach the owner to ignore the difference. **`Partial` counts as cash**, because
  `payment_method` does not record how the paid portion arrived and at this counter it is notes.
- **Nothing is rounded** (`CashDrawer`, pure). A paisa short is still short — smoothing hides
  exactly the small repeated differences that are the point.
- **Every expense states where its money came from** — `Till` or `Bank`, required by
  `CreateExpenseValidator` and `NOT NULL` since migration `0024`. Only `Till` is subtracted; a
  bank payment never entered the drawer. The field **starts unanswered on the form**: defaulting
  to Till would quietly drop every bank payment into the drawer calculation.
- **The validator is the rule, not the column.** `NOT NULL` on an ENUM cannot stop an omitted
  column even under strict mode (see Traps). `0025` orders the members so that when a write does
  forget, it lands on `Bank` and is excluded — a wrong figure, but never a phantom short that
  sends the owner hunting for cash that never left.
- **The test suite runs strict, because the shop's server does.** The live database is MariaDB
  with `STRICT_TRANS_TABLES` global; the development MySQL is not strict, so `ApiFactory` sets
  `Database:EnforceStrictSqlMode` and `DatabaseFixture` does the same for its own connections.
  Without it the suite proves behaviour under weaker rules than production — a coercion bug would
  pass here and fail in the shop. It is **off in production**, where re-asserting it would cost a
  round trip per connection for nothing.
- **A difference is shown, never accused.** The `note` column is where "Rs 300 to the delivery
  boy, not entered" goes; whether those notes stop appearing is how the owner learns the recording
  discipline has taken hold.

## A supplier's account

`GET /api/suppliers/{id}/ledger?from=&to=` — every purchase, purchase return and payment in date
order with what was owed after each. Admin only. The **Supplier ledger** screen sits under
Purchasing and opens from each supplier's **Ledger** button.

- **Derived, never stored.** `SupplierLedgerRepository` reads the same three tables and columns
  the payable invariant sums (`purchases.total − purchase_returns.total − supplier_payments.amount`),
  and `SupplierLedger.Build` (pure) runs the balance — so the last line **is** `payable_balance`
  and there is no second table to drift. An older `LedgerForSupplierAsync` that left out returns
  was removed: its balance disagreed with "You owe" the moment anything went back.
- **A range only chooses which lines to show.** The balance is run over the whole history first;
  the period opens on `openingBalance`, what was owed before `from`. The three totals are
  all-time, so purchased − returned − paid always equals what is owed.
- **A supplier payment must say how it was made** (`SupplierPaymentRequest.PaymentMethod`, no
  default, 400 if missing). It used to default to Cash, and day close subtracts only Cash
  supplier payments from the drawer — so every bank transfer to a supplier showed as a **short**.
  Credit and Partial are refused: they describe an unpaid sale, not money handed over. The Pay
  box starts unanswered for the same reason the expense form does.
- `note` on a supplier payment is the reference — cheque or transaction number — and shows on the
  ledger line.

## Shop accounts, and how an expense was paid

`shop_accounts` (migration `0031`) — the shop's own bank and wallet accounts, registered once by
the owner under Settings → **Shop accounts** (`/api/shop-accounts`, Admin only). A non-cash
expense or supplier payment names the account it left (`shop_account_id`, optional).

- **An account carries only its own methods** (`ShopAccountRules`, pure): Bank ← bank transfer
  and Raast; JazzCash ← JazzCash; EasyPaisa ← EasyPaisa. Cash, Credit and Partial go through no
  account. `ShopAccountCheck` (Api) refuses a mismatched or retired account with 422, and is the one
  place expenses and supplier payments check it.
- **Nothing is hard-deleted** — neither accounts nor expense categories. Hiding sets
  `is_active = FALSE`: no longer offered, still named on every past record. Names are unique;
  a duplicate is a 422 in words, not a database error.
- **An expense asks one question: Paid by** — `Cash (from the till)`, bank transfer, JazzCash,
  EasyPaisa, Raast. `CreateExpenseRequest.ResolvedSource` derives what day close reads: Cash →
  `Till`, anything else → `Bank`. `payment_method` stores only the transfer method (the column
  holds no Cash). The older `paymentSource` is still accepted; given both, they must agree.
  A cash expense is refused an account and a transaction ID.
- **Its proof is uploaded with the expense** (the page saves, then attaches) — the owner usually
  has the screenshot in hand, unlike the counter. A failed upload never reads as a failed expense:
  it says so, and the expense appears on Proof missing.
- **Expense categories are the owner's** (`/api/expense-categories`: add, `PUT` rename, `DELETE`
  hides, `/reactivate`). The expense form is offered active ones only; `includeInactive=true` is
  for the Manage categories panel.

## The team: jobs, sign-ins and the owner's view

One owner (Admin) and staff who do different **jobs** (`users.job`, migration `0032`): `Counter`
(the shopkeeper, beside the owner) or `FieldSales` (the salesman in the market). **Both keep the
Staff role** — every "no cost or profit for Staff" protection is unchanged; the job only says which
work, so later features (commission, carried stock) can tell them apart. A new job is a new ENUM
member, not a new role. The owner has no job (a create with one is a 400); staff created without
one work the counter.

- **Sign-ins are recorded** (`user_logins`) by `AuthController.Login` — time, address, device.
  Never a token refresh, or one working day would read as hundreds.
- **`/api/team`** (Admin only): a card per active person for the chosen days (today by default) —
  sales net of returns, bills, received at sale, udhaar given, discounts, returns, udhaar
  collected, last sign-in. **`/api/team/{userId}/activity`**: their sales, returns, recoveries,
  supplier payments, expenses, purchases and sign-ins, newest first. Built entirely from the
  `user_id` every table already records — nothing is recorded twice.
- **`/api/team/watchlist`**: big discounts (≥ 10% of the listed value — `WatchRules`, pure),
  transfer sales without proof, a same-day return of one's own sale, refunds by transfer. **A flag
  to look at, never an accusation.** "Same day" uses a fixed +05:00 (Karachi has no DST), so it
  needs no time-zone tables on MariaDB.
- **The counter drawer is shared** by the owner and the shopkeeper, so a day-close difference
  belongs to the counter, not to one person. The salesman's market cash is kept out of it until
  handed over (below).

## The field salesman's commission

The owner's rule: **a field salesman keeps half of whatever he sells above the owner's price,
earned once the customer has paid for it — and he may never sell below that price.**

- **The owner's price is the price the counter already quotes** (`Commission.BasePrice`): retail,
  or for a wholesale sale the wholesale price, falling back to retail. No second price list.
- **Snapshotted on each of his lines** (`invoice_items.base_unit_price`, `commission_rate`,
  migration `0033`), exactly like `unit_cost_price`: a price or rate changed tomorrow never
  rewrites what last week earned. NULL on every other line — only `StaffJob.FieldSales` earns.
- **The floor** sits in `InvoiceService`, before the first write, on what each unit REALLY fetched
  (`ReturnPricing.EffectiveUnitPrice` — after its line discount and its share of the whole-bill
  discount), against the price read from the LOCKED row. The seller's job comes from his account,
  never the request (`InvoicesController` reads it and passes `sellerJob`).
- **Earned is derived, never stored** (`CommissionService`, `Commission.Settle`, pure): a
  customer's payments settle their oldest debt first — amounts brought forward, then sales, whoever
  sold them — so a sale is earned the day the udhaar on it is fully recovered, in proportion when
  part-recovered. A returned unit earns nothing.
- **Payouts are stored** (`commission_payouts`); owed = earned − paid out, and a payout above what
  is owed is a 422 — commission on unrecovered udhaar is not his yet. A **cash** payout joins
  `CashPaidOut` at day close; every payout counts in `ExpenseRepository.SumForPeriodAsync` (net
  profit) and appears as "Salesman commission" in the expense breakdown, so the two agree.
- `/api/commissions/{userId}` and payouts are Admin only; `/api/commissions/me` gives a salesman
  his own — sale prices and the owner's price, never cost.
- A salesman may now sell on udhaar to the owner's udhaar customers (below); the commission
  already handled udhaar, so nothing here changed.

## The salesman in the market: udhaar customers and the cash he carries

**Udhaar customers** (`customers.credit_allowed`, migration `0034`). The owner marks which customers
the field salesman may leave money owing with. `UdhaarAuthority.MayLeaveOwing` (pure): the owner
always; a `FieldSales` seller only for a marked customer; the counter shopkeeper never.

- **Still decided in `InvoiceService`, on the recomputed `AmountRemaining`, before the first
  write** — the credit-authority rule is widened, not moved. A walk-in or an unmarked customer is
  refused with words that say why.
- **Owner-only to set**, like `SaleType`: `CustomerUpsertRequest.CreditAllowed` is applied only for
  an Admin and silently ignored otherwise; a Staff quick-create is always unmarked. A salesman must
  never be able to grant himself a customer to give credit to.
- `0034` backfilled the mark onto every customer who already had udhaar history (a balance, an
  opening balance, or an invoice with money remaining) — the owner's "already categorised" ones.

**His cash** (`in_field` on `invoices`, `customer_payments`, `sale_returns`; `salesman_handovers`).

- **`in_field` is snapshotted** from the seller's job when the row is written, exactly like
  `base_unit_price` — a salesman later moved to the counter never re-labels last month's cash.
- **Day close excludes `in_field` rows** from cash sales, recovery and refunds: that money is in his
  pocket, not the drawer, and counting it would show a false short every evening.
- **It joins the drawer only when handed over**: `POST /api/salesman-cash/{userId}/handovers`
  (Admin — "Received from salesman", never recorded by the salesman). A **cash** handover is
  `CashFromSalesmen` at day close (snapshotted on `day_closings.cash_from_salesmen`); a transfer
  went into a shop account and never touches the drawer. Credit/Partial are refused.
- **In hand is derived, never stored or typed** (`SalesmanCashService`): cash taken at his sales
  (Cash/Partial `amount_paid`) + cash udhaar recovered − cash refunds − handovers. Built from the
  same `in_field` rows day close leaves out, so the two can never disagree. A handover above what
  he holds is a 422.
- `GET /api/salesman-cash/me` gives a salesman his own; `/{userId}` is Admin. The Team card shows
  `cashInHand` (all-time) for a field salesman.

**His own screen** — `GET /api/my-day?from=&to=` (any signed-in user; today when left out): the
same card the owner sees on Team, plus his sales, returns and recoveries (`TeamService.MineAsync`).
**It takes no user id** — always the caller's own, so nobody can read another's through it. Sign-ins,
purchases, supplier payments and expenses are filtered out: they are the owner's business, and a
purchase line's amount is cost. With `/api/commissions/me` and `/api/salesman-cash/me` it is
everything the phone's **My day** screen shows.

## Stock the salesman carries

`salesman_stock` (what each salesman holds, per product) and `salesman_stock_movements` (every unit
in and out of his bag), migration `0035`. The owner **issues** goods and **takes them back**
(`POST /api/salesman-stock/{userId}/issue` and `/return`, Admin); a salesman reads his own
(`/me`). The Team card shows `stockUnits`.

- **`quantity_on_hand` stays what the shop OWNS.** Goods in his bag are still the shop's, so stock
  value, purchases and every report are untouched. Issuing and taking back never change it — they
  only move owned units between the shelf and his bag.
- **What the counter can sell is the shelf:** `SalesmanStockRules.AtShop` = owned − everything
  salesmen carry. A counter sale beyond that is `INSUFFICIENT_STOCK`, naming how many are with the
  salesman.
- **A field salesman sells out of his own bag, and only from it.** `InvoiceService` checks his
  holding instead of the shelf; a sale takes the units out of both (`quantity_on_hand` and his
  holding, movement `Sold`). He cannot sell what he was never issued — the owner issues it first.
- **A customer handing goods back to him** (an `in_field` sale return) puts them back into both
  (movement `CustomerReturn`). Returned at the counter, they go onto the shelf as before.
- **Owned can never fall below what salesmen carry**: a stock correction (`StockService.AdjustAsync`)
  or purchase return that would is refused (422) — it would describe goods that do not exist.
- **Locking:** every change to `salesman_stock` happens under the product rows' `FOR UPDATE` lock
  (ordered by id) that every sale, purchase and return already takes, and every read of it inside a
  write is itself `FOR UPDATE`. A plain read would see the transaction's snapshot, which can predate
  an issue committed while this one waited for the product lock.
- The holding is stored, like `quantity_on_hand`, with its movements beside it; the movements
  always add up to it (`Every_unit_in_and_out_of_his_bag_is_on_the_record`).
- **Tests that sell as a field salesman must issue first** — `ApiFactory.IssueToSalesmanAsync`
  goes through the real service. `FieldSalesTests` and `CommissionTests` do it inside `SellAsync`.
- **Every product read carries where the stock is**: `atShop` (the shelf), `withSalesmen`, and
  `inYourBag` — the caller's own holding, only when the caller is a field salesman (null
  otherwise; read from his account by `ProductsController`, never the request). `quantityOnHand`
  keeps meaning OWNED, and `isLowStock` is still judged on it: goods out with a salesman will be
  sold, and judging on the shelf would cry "low stock" every morning after the round is issued.
  Worked out in `ProductService.ProjectAllAsync` with one extra display query per page — kept out
  of the search SQL so the five-thousand-product bound is unaffected. These are quantities, not
  cost, so they sit on the Staff shape.

## Who sold what

`GET /api/reports/sales-by-user` — each salesman's period: sales, cash taken, credit given,
discount given, bill count. Admin only. **No cost and no profit**: this answers "who took the
money and who gave the discounts", not "what did we make".

- **Its real job is beside the drawer count.** A short is only answerable once you know who was
  selling, and a discount pattern only means something attached to a person. On its own it is a
  convenience — `sales-list` already names who sold each item.
- **Cash taken and credit given are separate columns** because they are different risks: money in
  the drawer tonight versus money that walked out of the shop.
- **Discount counts both kinds.** Line discounts are already inside the subtotal, so
  `order_discount` alone would understate what was given away.

## The one unauthenticated endpoint

`GET /api/public/documents/{token}` serves a customer their own receipt. A shop customer holds no
credential, and `wa.me` cannot attach a file — so the receipt travels as a link. Containment:
≥256-bit token stored only as a SHA-256 hash, resolving to one document, expiring, revocable,
rate-limited, access-logged. An architecture test fails the build if a second anonymous endpoint
appears.

## Where to look

| Question | File |
|---|---|
| What is the shop supposed to do? | `specs/001-pos-inventory-ledger/spec.md` |
| Why was it built this way? | `specs/001-pos-inventory-ledger/research.md` |
| What does the schema look like? | `specs/001-pos-inventory-ledger/data-model.md` |
| What does the API accept? | `specs/001-pos-inventory-ledger/contracts/` |
| How do I verify it works? | `specs/001-pos-inventory-ledger/quickstart.md` |
| How do I deploy it? | `docs/deployment.md` |
