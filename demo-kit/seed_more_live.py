"""DEMO KIT — two more running auctions, beside A8, for the monitor and the bidding screens.

Adds to the data seed_demo.py made (nothing is cleared):

  A11  مخطط الملقا — قطعة 44      online, open for 3 hours, a few bids already
  A12  مخطط القيروان — قطعة 9     online, open for 3 hours, one opening bid

Both with a cover, photos and a plan, and with Sara and Khalid qualified (free
booklet, terms accepted, deposit paid through the sandbox), so either can bid at
http://localhost:3000 straight away.

    python demo-kit/seed_more_live.py
    python demo-kit/seed_more_live.py --hours 5     # open longer
"""
import argparse, json, os
from datetime import datetime, timedelta, timezone

import seed_demo as kit


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=3, help="how long they stay open")
    ap.add_argument("--opens-in", type=int, default=3, help="minutes until they open")
    ap.add_argument("--reuse", action="append", default=[], metavar="KEY=ID",
                    help="an auction an interrupted run already created, e.g. A11=<id>")
    args = ap.parse_args()

    gateway = kit.call("GET", "http://localhost:5111/api/payments", ok=None)
    if isinstance(gateway, dict) and gateway.get("declineCharges"):
        raise SystemExit("The sandbox gateway is refusing payments — press «قبول الدفع» at http://localhost:5111.")

    kit.make_assets()
    admin = kit.direct("admin-user")
    kit._otp_window("committee-user")
    committee = kit.json.loads(kit.OPENER.open(kit.urllib.request.Request(kit.KC, kit.urllib.parse.urlencode({
        "grant_type": "password", "client_id": "admin-web", "username": "committee-user",
        "password": "dev-only-password", "totp": kit.totp()}).encode())).read())["access_token"]

    jpg = lambda name: kit.upload(admin, os.path.join(kit.ASSETS, name), "Public", "image/jpeg")
    docs = {
        "booklet": kit.upload(admin, os.path.join(kit.ASSETS, "booklet.pdf"), "Restricted", "application/pdf"),
        "site-plan": kit.upload(admin, os.path.join(kit.ASSETS, "site-plan.pdf"), "Public", "application/pdf"),
        **{k: jpg(f"{k}.jpg") for k in ("photo-north", "photo-street", "photo-corner", "photo-wide", "photo-dusk")},
    }

    opens = datetime.now(timezone.utc) + timedelta(minutes=args.opens_in)
    closes = opens + timedelta(hours=args.hours)
    plan = [("المخطط المعتمد", "site-plan", "Document")]

    specs = [
        dict(key="A11", nameAr="مخطط الملقا — قطعة 44", nameEn="Al-Malqa plan — plot 44", phase="مخطط الملقا",
             starts=opens, ends=closes, opening=850_000, reserve=800_000, increment=10_000,
             deposit=45_000, booklet=0, quiet=60, cover="photo-corner",
             attachments=[("زاوية القطعة", "photo-corner", "Photo"), ("الشارع الرئيسي", "photo-street", "Photo"),
                          ("منظر عام", "photo-wide", "Photo")] + plan,
             plot=dict(plotNumber="310114000044", areaSqm=640, landUse="ResidentialCommercial", facing="East",
                       streetWidthMeters=30, frontageMeters=24, latitude="24.8105", longitude="46.6187",
                       descriptionAr="قطعة سكنية تجارية على شارع 30 م — المزاد جارٍ الآن.")),
        dict(key="A12", nameAr="مخطط القيروان — قطعة 9", nameEn="Al-Qirawan plan — plot 9", phase="مخطط القيروان",
             starts=opens, ends=closes, opening=480_000, reserve=450_000, increment=5_000,
             deposit=25_000, booklet=0, quiet=60, cover="photo-north",
             attachments=[("صورة جوية للقطعة", "photo-north", "Photo"), ("عند الغروب", "photo-dusk", "Photo")] + plan,
             plot=dict(plotNumber="310116000009", areaSqm=450, landUse="Residential", facing="South",
                       streetWidthMeters=15, frontageMeters=18, latitude="24.8530", longitude="46.5890",
                       descriptionAr="قطعة سكنية هادئة — المزاد جارٍ الآن.")),
    ]

    reuse = dict(r.split("=", 1) for r in args.reuse)
    ids, subs = {}, {}
    for spec in specs:
        ids[spec["key"]] = reuse.get(spec["key"]) or kit.create_auction(admin, committee, spec, docs)
        for user in ("sara", "khalid"):
            subs[(user, spec["key"])] = kit.qualify(user, ids[spec["key"]], closes)

    kit.log(f"waiting for A11–A12 to open at {kit.iso(opens)}…")
    while datetime.now(timezone.utc) < opens + timedelta(seconds=5):
        kit.time.sleep(2)

    # A11: a contest under way. A12: one opening bid, the price still to move.
    for i, amount in enumerate((850_000, 860_000, 870_000, 880_000)):
        user = ("khalid", "sara")[i % 2]
        kit.bid(user, ids["A11"], subs[(user, "A11")], amount)
    kit.bid("sara", ids["A12"], subs[("sara", "A12")], 480_000)

    out = os.path.join(kit.HERE, "more-live.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({k: {"id": v, "nameAr": next(s["nameAr"] for s in specs if s["key"] == k)} for k, v in ids.items()}
                  | {"opens": kit.iso(opens), "closes": kit.iso(closes)}, f, ensure_ascii=False, indent=2)

    riyadh = timedelta(hours=3)
    kit.log(f"ready: A11 and A12 open until {(closes + riyadh):%H:%M} Riyadh time")


if __name__ == "__main__":
    main()
