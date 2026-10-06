# Stakeholder demo — one auction, end to end

A script for showing the platform to people who are deciding whether to fund or
accept it. Thirty to forty minutes at a walking pace, or fifteen if you skip §6–§8.

Everything below runs on one machine against real Keycloak, real Kafka and real
PostgreSQL. Nothing is mocked and nothing is pre-seeded — you will be creating the
auction in front of them.

---

## Before they arrive

```bash
tools/smoke/run-portals.sh --with-deps --keep-up
```

Then plant some history, so التقارير and سجل المراجعة have something to show
beyond whatever you create in the room:

```bash
dotnet run --project tools/seed -- --count 24
# Compose instead of the local stack? Kafka is on 9192 there:
#   dotnet run --project tools/seed -- --kafka localhost:9192 --count 24
```

About two dozen auctions across six months, in every outcome the platform can
produce — sold, unsold, rejected, and both halves of a winner defaulting. Safe to
run twice; see `tools/seed/README.md` for why, and for why it does not appear in
المزادات.

Wait for `Everything is up`. That brings up eleven services and both portals and
leaves them running. Then **run it once more as a rehearsal** — the full
walk-through, which takes about four minutes and tells you whether anything is
broken before an audience finds out:

```bash
tools/smoke/run-smoke.sh --with-deps      # 89 checks, all should pass
```

Open these tabs ahead of time, each in its own browser profile or window so the
sessions do not fight:

| Tab | URL | Sign in as |
|---|---|---|
| **Sandbox** | http://localhost:5111 | nobody — no sign-in |
| Catalogue | http://localhost:3000 | nobody — stay signed out |
| Bidder | http://localhost:3000 | `sara` |
| Second bidder | http://localhost:3000 | `khalid` |
| Administration | http://localhost:3001 | `admin-user` |
| Committee | http://localhost:3001 | `committee-user` | 
| **Live board** | http://localhost:3001 → المتابعة المباشرة | `committee-user` |
| Reports | http://localhost:3001 | `reporting-user` |
| Audit | http://localhost:3001 | `auditor-user` |

Password for every account: `dev-only-password`.

The addresses above are the ones `run-portals.sh` uses. If you brought the stack up
with Docker Compose instead, the portals, Keycloak and the sandbox are at these same
addresses, but the services sit on different ports — see the header of
`deploy/compose/docker-compose.yml`, which lists them. Nothing in this script asks
you to open a service directly.

**Open the sandbox first and keep it open**, ideally on a second screen. Registering
as a bidder and paying a deposit are both behind a second factor, and the sandbox is
the only place its code can be read — without it you cannot get past the first step
of the bidder's journey, because the gate is real even though Nafath behind it is
not. The code changes every thirty seconds and the page counts down to the next one;
if the bar is nearly empty, wait for the next code rather than racing it.

> Earlier versions of this page said any authenticator app seeded with
> `eauctiondevsecret1234567890` would produce the code. It will not: that is an
> ASCII secret and authenticator apps expect base32, so the codes come out wrong
> and the login fails for a reason nothing on screen explains. Use the sandbox.

The sandbox's second panel is the payment gateway. It shows the booklet fee, the
deposit, the brokerage and any refunds as they are charged, with the reference the
bidder would quote — worth pointing at, because otherwise the money in this system
moves entirely out of sight. It also has the switch described in §6.1.

**Put المتابعة المباشرة on the second screen.** It shows every open auction at
once — price, leader, countdown — and refreshes itself every two seconds, so while
you drive a bid in one window the board moves beside it. An auction inside its last
two minutes outlines itself in red, which is the moment worth pointing at. Nobody
needs to touch it during the demonstration; it is the thing people watch while you
talk.

**Pick your times before you start.** When you create the auction, set it to open
about four minutes out and close about three minutes after that. Too soon and you
are talking against a clock; too far out and the room waits.

---

## 1. The catalogue, signed out (2 min)

Start on the tab where nobody is signed in.

> "This is what a citizen sees before they have an account. The auctions, the
> plots, the areas, the opening price."

Point at what is **not** there: no bidder names, and no reserve price. The reserve
is secret by design and never leaves the one service that needs it — not to the
catalogue, not to the reports, not to the audit trail.

## 2. Preparing the auction — `admin-user` (6 min)

New auction → name it → add three plots with their deed numbers and areas → upload
the كراسة الشروط → set the prices, the deposit, the brokerage percentage, and the
reserve.

Two things to say while you type:

- **The three plots sell as one package.** Nobody bids on a plot. That is why
  there is one opening price and one reserve for the three of them.
- **The reserve price is write-only.** Reload the page and the field is empty: no
  read path in the platform returns it, including this one.

Then press **رفع للمراجعة**. Now try to approve it yourself — you cannot. The
administrator who sets an auction's terms is not the person who approves them.

## 3. Approval — `committee-user` (2 min)

Switch to the committee tab, open the same auction, approve it.

> "Two different people, enforced by the system rather than by procedure."

Behind that button the auction's public definition went onto one Kafka topic and
its reserve price onto a second, restricted one. Worth saying out loud, because it
is the reason the rest of the demo can be honest about secrecy.

## 4. Qualifying a bidder — `sara` (6 min)

On the bidder tab, walk the steps the portal shows:

1. **Register** — the identity comes from Nafath, not from a form. Sara is asked
   for a second factor here: this binds a national identity to an account for good.
2. **Buy كراسة الشروط** — a card payment, so a second factor again.
3. **Accept the terms.**
4. **Pay the deposit (التأمين)** — second factor, and then *wait*. The screen says
   the payment is in flight.

That wait is the thing to point at:

> "She is not eligible because she asked to be. She is eligible when the payment
> service reports that the money settled. An earlier version of this took her word
> for it, and a bidder could have reached the bid floor of a state land auction
> without a riyal moving."

When it settles the screen flips to مؤهّل and the bid box appears. Do the same for
`khalid` — faster, now that they have seen it.

While you are here: open the booklet. It downloads, and the link is useless to
anybody else — the permission is signed, lasts five minutes and names Sara.

## 5. The auction (5 min)

When the clock reaches your start time the auction opens by itself. Put Sara's and
Khalid's tabs side by side and bid against each other.

- The price moves on both screens **without a reload**. That is a push channel, not
  polling.
- Khalid sees `مزايد #1`, never Sara's name. The administrator can switch an
  auction to named; masked is the default.
- Bid below the current price and read the refusal.
- Open a **شهادة مزايدة** from Sara's own bid: her bid, signed, with a receipt she
  can keep. Khalid cannot open it.

If the quiet period is on, bid in the last seconds and watch the close time move.

## 6. The award — `committee-user` (4 min)

The auction closes on its own. The processor — not the portal — decides the winner
from the Kafka offset order, and offers the committee a candidate.

Confirm the award. **This is the one action that needs a second factor from a
committee member**, because it transfers a parcel of state land to a named person.

Then show the cascade, which is the part worth the time: **disqualify the winner**
with a reason, and forfeit the deposit. The award moves down the ladder to Khalid
at his own lower price — not Sara's. Confirm it, and settle.

## 7. التقارير — `reporting-user` (5 min)

Switch tabs. Press **التقارير**.

- **الإيرادات**, grouped by مخطط. Two columns that are never added together: the
  value of land sold, and the cash this platform actually collected. The price of
  the land does not pass through here — only the booklet fee, the brokerage and
  forfeited deposits do. A report that summed them would claim the platform
  received money it never touched.
- **نتائج المزادات** — note the final price is Khalid's, not the disqualified
  bidder's, and the brokerage is computed on it.
- **القطع** — how much of the مخطط has sold and at what rate per square metre.
  Say that the rate is the package's: nobody bid on a plot.
- **التأمينات المحتجزة** — whose money the municipality is holding right now.
- **تنزيل CSV** on any of them. Open it in Excel. The Arabic is correct and the
  riyals are a number, not text.

One line worth saying: this account **cannot change anything.** It exists so that
finance staff do not need the role that can edit an auction's reserve price.

## 8. سجل المراجعة — `auditor-user` (4 min)

Before you switch, go back to the `admin-user` tab and point out that there is **no
audit tab there.**

> "The person who approves auctions cannot read the record of their own approvals.
> That is not a permission we forgot."

Now the auditor tab. Every consequential thing done in the last half hour is
listed: who created the auction, who approved it, who accepted the guarantee, who
disqualified the winner. Find the reserve-price change — it says the reserve was
changed and by whom, and no figure.

Then press **تحقّق من السلسلة.**

> "Each entry's hash is computed over the one before it. This just recomputed every
> one of them from the stored record. If anybody had edited a row — in the
> database, with the service's own credentials — this would name the entry."

The table is also append-only in PostgreSQL: an `UPDATE` on it is refused by a
trigger. Altering the trail takes a privileged, separately auditable act, and the
chain still shows it afterwards.

---

## If they ask — answer plainly

| Question | The honest answer |
|---|---|
### 6.1 Showing the path where a bidder does not pay

Worth ninety seconds if anyone asks what happens when a payment fails, because it
is the only part of the money story that cannot be shown by it working.

In the sandbox's payment panel press **اجعلها ترفض**, pick a reason, then have the
second bidder try to pay their deposit. The portal tells them the payment was
refused and offers to try again; they never become eligible; and the committee's
screen shows them as not qualified. Press **أعِد القبول** afterwards, or the rest of
the demonstration will fail in the same way and you will be debugging in front of an
audience.

What this is not: a real decline from a real bank. It is the platform's own refusal
path being driven on purpose, which is the point — the behaviour on the far side of
a refusal is real, and is what a procurement officer is actually asking about.

---

| Is Nafath connected? | No. Registration reads the identity claims from the token and stands in for the real callback. It needs credentials from Elm/NIC under contract. **Until then this must not touch real citizens' identities.** |
| Did money move? | No. The payment gateway is a simulator that settles everything. The service refuses to start in production without an explicit flag, so it cannot ship by accident. PayTabs/SADAD need merchant accounts. |
| Do bidders get an SMS? | Not yet. The in-product inbox works; SMS needs a licensed aggregator and a registered sender name. |
| Is there a mobile app? | Not built. It is the one thing in the architecture's service list with no code. |
| Has this been deployed? | No. The Helm chart is written, linted and rendered for both plain Kubernetes and OpenShift, and has never been applied to a cluster. |
| How many plots are in the plan? | The deck says 372 and the phases add to 327. `التقارير → القطع` answers it from the data once the real plan is loaded. |
| Is it fast enough? | 11,308 bids/second at a 99th-percentile latency of 35 ms, measured with Kafka and authentication in the path against a 50 ms target. What is not yet measured is the longer loop out to every watcher. |
| What about the reserve price? | It leaves the auction service on one restricted topic to one consumer. It is in no report, no audit entry, no public API, and no screen. |

Do not soften these. A stakeholder who finds out later that Nafath was mocked
remembers the demo differently.

---

## If something breaks mid-demo

- **The auction will not open.** Nothing closes an onsite auction on a clock, so
  several runs leave auctions on the topics. Restart with `--with-deps`, which
  wipes the broker.
- **A new account cannot sign in.** Keycloak imports the realm only on a fresh
  start: `pkill -f "[k]c.sh start-dev"` and run again.
- **A bid is refused as `InvalidSignature`.** The bidder's key is per auction and
  per epoch. Reload the bidder tab.
- **A screen is empty.** The reports and the audit trail are Kafka consumers a
  second or two behind the thing they describe. Press تحديث.
- **Logs** are in `/tmp/eauction-smoke/`, one file per service.
