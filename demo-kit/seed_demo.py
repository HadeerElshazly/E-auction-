"""DEMO KIT — synthetic demo data. Delete demo-kit/ (and reset the stack) after the demo.

Creates, through the platform's own APIs, one auction — one plot each — in every
stage a stakeholder demo walks through, with real photos («الصور») and plans
(«المستندات») and every plot field (رقم القطعة، الاستخدام، الواجهة، عرض الشارع):

  A1  مخطط الياسمين — قطعة 101   upcoming · 3 photos + 2 plans · Sara eligible,
                                  Khalid's bank guarantee waiting for review
  A2  مخطط الياسمين — قطعة 102   upcoming · free booklet
  A3  مخطط النرجس — قطعة 7       upcoming · hall (on-site) auction · commercial
  A4  مخطط الياسمين — قطعة 103   approved, then cancelled — deposits and booklet refunded
  A5  مخطط الملقا — قطعة 15      ran now · closed · awaiting the committee's award
  A6  مخطط الملقا — قطعة 16      ran now · awarded to Sara · letters · partly paid
  A7  مخطط الملقا — قطعة 17      ran now with no bids · unsold — re-offer at a lower price
  A8  مخطط الياسمين — قطعة 120   LIVE for three hours with bids — bidding, monitor, «إنهاء المزاد»
  A9  مخطط النرجس — قطعة 12      submitted · awaiting the committee's approval
  A10 مخطط النرجس — قطعة 14      draft · to finish on the auction page

Usage (the Docker stack must be up, sandbox gateway on «قبول الدفع»):
    python demo-kit/seed_demo.py

Takes about 10 minutes: A5–A8 really open and take real signed bids; A5–A7 close.
Writes demo-kit/demo-data.json with every id it created.
"""
import json, os, subprocess, sys, time, urllib.parse, urllib.request
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from kc import stepped_token, claims, totp  # noqa: E402

ADMIN, PART, QUERY, CATCH, DOCS = (f"http://localhost:{p}" for p in (5090, 5095, 5094, 5080, 5096))
KC = "http://localhost:8080/realms/eauction/protocol/openid-connect/token"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))
ASSETS = os.path.join(HERE, "assets")
SAR = 100  # halalas per riyal


def log(*a):
    print(datetime.now().strftime("%H:%M:%S"), *a, flush=True)


def iso(d):
    return d.strftime("%Y-%m-%dT%H:%M:%SZ")


# --- HTTP ---------------------------------------------------------------------

def call(method, url, token=None, body=None, raw=None, ctype="application/json", ok=(200, 201, 202, 204)):
    data = raw if raw is not None else (None if body is None else json.dumps(body, ensure_ascii=False).encode())
    req = urllib.request.Request(url, data=data, method=method)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", ctype)
    try:
        r = OPENER.open(req)
        t = r.read().decode()
        status, out = r.status, (json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        t = e.read().decode(errors="replace")
        try:
            status, out = e.code, json.loads(t)
        except Exception:
            status, out = e.code, t
    if ok and status not in ok:
        raise RuntimeError(f"{method} {url} -> {status} {out}")
    return out


def upload(token, path, access, ctype):
    boundary = "----demokit"
    with open(path, "rb") as f:
        payload = f.read()
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"access\"\r\n\r\n{access}\r\n"
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{os.path.basename(path)}\"\r\n"
            f"Content-Type: {ctype}\r\n\r\n").encode() + payload + f"\r\n--{boundary}--\r\n".encode()
    return call("POST", f"{DOCS}/documents", token, raw=body,
                ctype=f"multipart/form-data; boundary={boundary}")["id"]


# --- logins -------------------------------------------------------------------
# Keycloak refuses the same one-time code twice, so a user is logged in at most
# once per 30-second window; tokens are reused until the step-up is near its 5 minutes.

_tokens, _last_otp = {}, {}


def _otp_window(user):
    since = time.time() - _last_otp.get(user, 0)
    if since < 31:
        time.sleep(31 - since)
    _last_otp[user] = time.time()


def stepped(user, client, redirect):
    t = _tokens.get(user)
    if t and time.time() - t[1] < 210:
        return t[0]
    _otp_window(user)
    tok = stepped_token(user, client, redirect)
    _tokens[user] = (tok, time.time())
    return tok


def bidder(user):
    return stepped(user, "bidder-web", "http://localhost:3000/")


def direct(user, client="admin-web"):
    d = {"grant_type": "password", "client_id": client, "username": user, "password": "dev-only-password"}
    r = OPENER.open(urllib.request.Request(KC, urllib.parse.urlencode(d).encode()))
    return json.loads(r.read())["access_token"]


# --- assets -------------------------------------------------------------------

def pdf(path, lines):
    """A small valid one-page PDF. Latin text only: no font embedding here."""
    text = "BT /F1 16 Tf 72 770 Td " + " ".join(
        "(" + l.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)") + ") Tj 0 -26 Td" for l in lines) + " ET"
    objs = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R "
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            f"<< /Length {len(text)} >>\nstream\n{text}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    out, offsets = b"%PDF-1.4\n", []
    for i, o in enumerate(objs, 1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n{o}\nendobj\n".encode()
    xref = len(out)
    out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode()
    out += "".join(f"{o:010d} 00000 n \n" for o in offsets).encode()
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    with open(path, "wb") as f:
        f.write(out)


def cover(path, title, sub, c1, c2):
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="500" viewBox="0 0 1200 500">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="{c1}"/><stop offset="1" stop-color="{c2}"/></linearGradient></defs>
<rect width="1200" height="500" fill="url(#g)"/>
<g stroke="#ffffff" stroke-opacity=".25" stroke-width="2" fill="none">
<path d="M80 420 L300 260 L520 330 L760 180 L1120 300"/><rect x="760" y="120" width="260" height="160"/>
<rect x="240" y="300" width="200" height="120"/></g>
<text x="1140" y="120" font-family="Tahoma, Arial" font-size="56" font-weight="700" fill="#fff" text-anchor="end" direction="rtl">{title}</text>
<text x="1140" y="180" font-family="Tahoma, Arial" font-size="28" fill="#fff" fill-opacity=".85" text-anchor="end" direction="rtl">{sub}</text>
<text x="60" y="470" font-family="Arial" font-size="20" fill="#fff" fill-opacity=".7">DEMO DATA</text>
</svg>"""
    with open(path, "w", encoding="utf-8") as f:
        f.write(svg)


def make_assets():
    os.makedirs(ASSETS, exist_ok=True)
    pdf(os.path.join(ASSETS, "booklet.pdf"), [
        "Terms & Conditions Booklet  (DEMO)", "", "Land auction - sample terms for demonstration only.",
        "1. The plots are sold as one lot in an ascending auction.",
        "2. A deposit is required before bidding.",
        "3. The winner pays the price within the compliance window.",
        "4. Brokerage is charged on the price won.", "", "Not a real contract."])
    pdf(os.path.join(ASSETS, "site-plan.pdf"), [
        "Approved site plan  (DEMO)", "", "Plan: Al-Yasmin, phase 1", "Plots 101-103", "", "Not a real document."])
    pdf(os.path.join(ASSETS, "award-letter.pdf"), [
        "Award letter  (DEMO)", "", "The award committee confirms the award of the plot", "to the winning bidder.",
        "", "Not a real document."])
    pdf(os.path.join(ASSETS, "award-letter-signed.pdf"), [
        "Award letter - SIGNED  (DEMO)", "", "Signed copy uploaded by the committee.", "", "Not a real document."])
    pdf(os.path.join(ASSETS, "bank-guarantee.pdf"), [
        "Bank guarantee  (DEMO)", "", "Issuing bank: Demo Bank", "Amount: 42,500.00 SAR",
        "Valid until the auction end + 30 days", "", "Not a real document."])
    cover(os.path.join(ASSETS, "cover-yasmin.svg"), "مخطط الياسمين", "الرياض — المرحلة الأولى", "#06a8b7", "#0b6f7a")
    cover(os.path.join(ASSETS, "cover-narjis.svg"), "مخطط النرجس", "الرياض — مزاد حضوري", "#c6ab83", "#8a6d45")
    cover(os.path.join(ASSETS, "cover-malqa.svg"), "مخطط الملقا", "الرياض — المرحلة الثانية", "#5a7bd8", "#2f4a99")
    cover(os.path.join(ASSETS, "aerial-101.svg"), "صورة جوية — قطعة 101", "توضيحية", "#7aa36b", "#3f6b35")


# --- auctions -----------------------------------------------------------------

def create_auction(admin, committee, spec, docs):
    a = call("POST", f"{ADMIN}/auctions", admin,
             {"createdByUserId": claims(admin)["sub"], "nameAr": spec["nameAr"], "nameEn": spec["nameEn"]})
    aid = a["id"]
    call("PUT", f"{ADMIN}/auctions/{aid}", admin, {
        "nameAr": spec["nameAr"], "nameEn": spec["nameEn"], "channel": spec.get("channel", "Online"),
        "bidderVisibility": "Masked", "startsAt": iso(spec["starts"]), "endsAt": iso(spec["ends"]),
        "openingPriceMinorUnits": spec["opening"] * SAR, "reservePriceMinorUnits": spec["reserve"] * SAR,
        "minIncrementMinorUnits": spec["increment"] * SAR, "depositMinorUnits": spec["deposit"] * SAR,
        "brokerageFeePercent": 2.5, "bookletPriceMinorUnits": spec["booklet"] * SAR,
        "quietPeriodSeconds": spec.get("quiet"), "maxExtensions": 3 if spec.get("quiet") else 0,
        "phase": spec["phase"]})
    # One auction, one plot.
    call("POST", f"{ADMIN}/auctions/{aid}/plots", admin, spec["plot"])
    if spec.get("stage") != "draft":
        call("POST", f"{ADMIN}/auctions/{aid}/booklet", admin, {"documentId": docs["booklet"]})
    if spec.get("cover"):
        call("POST", f"{ADMIN}/auctions/{aid}/cover-image", admin, {"documentId": docs[spec["cover"]]})
    for title, key, kind in spec.get("attachments", []):
        # «الصور» take the photos, «المستندات» the plans — each its own line in
        # «إعدادات العرض للزوار».
        call("POST", f"{ADMIN}/auctions/{aid}/attachments", admin, {"documentId": docs[key], "titleAr": title, "kind": kind})
    if spec.get("stage") == "draft":
        log(f"created {spec['key']}  {spec['nameAr']}  ({aid})  — draft")
        return aid
    call("POST", f"{ADMIN}/auctions/{aid}/submit", admin)
    if spec.get("stage") == "review":
        log(f"created {spec['key']}  {spec['nameAr']}  ({aid})  — awaiting the committee's approval")
        return aid
    call("POST", f"{ADMIN}/auctions/{aid}/approve", committee)
    for _ in range(30):  # until the catalogue has it
        seen = call("GET", f"{QUERY}/auctions/{aid}", ok=None)
        if isinstance(seen, dict) and seen.get("id") == aid:
            break
        time.sleep(1)
    log(f"created {spec['key']}  {spec['nameAr']}  ({aid})")
    return aid


def ensure_registered(user):
    tok = bidder(user)
    sub = claims(tok)["sub"]
    b = call("GET", f"{PART}/bidders/{sub}", tok, ok=None)
    if not isinstance(b, dict) or "id" not in b:
        call("POST", f"{PART}/bidders/register", tok)
        b = call("GET", f"{PART}/bidders/{sub}", tok)
    if not b.get("profileComplete"):
        call("POST", f"{PART}/bidders/{sub}/profile", tok,
             {"phone": "+966500000000", "email": f"{user}@example.sa"})
    return sub


def wait_status(user, aid, sub, wanted, seconds=60):
    for _ in range(seconds):
        s = call("GET", f"{PART}/auctions/{aid}/subscriptions/{sub}", bidder(user))
        if s["status"] in wanted:
            return s
        time.sleep(1)
    raise RuntimeError(f"{user} on {aid}: stuck at {s['status']}")


def qualify(user, aid, ends, method="Payment"):
    sub = ensure_registered(user)
    base = f"{PART}/auctions/{aid}/subscriptions/{sub}"
    call("POST", f"{PART}/auctions/{aid}/subscriptions", bidder(user), {"bidderId": sub})
    call("POST", f"{base}/booklet", bidder(user))
    wait_status(user, aid, sub, {"BookletPurchased"})          # instant when free, else the gateway
    call("POST", f"{base}/terms", bidder(user))
    if method == "Payment":
        call("POST", f"{base}/deposit-method", bidder(user), {"method": "Payment"})
        call("POST", f"{base}/deposit", bidder(user))
        wait_status(user, aid, sub, {"Eligible"})
        log(f"  {user}: eligible")
    else:
        call("POST", f"{base}/deposit-method", bidder(user), {"method": "BankGuarantee"})
        doc = upload(bidder(user), os.path.join(ASSETS, "bank-guarantee.pdf"), "Private", "application/pdf")
        call("POST", f"{base}/guarantee", bidder(user),
             {"documentId": doc, "expiresAt": iso(ends + timedelta(days=30))})
        log(f"  {user}: bank guarantee submitted — under review")
    return sub


def bid(user, aid, sub, riyals):
    tok = bidder(user)
    key = call("GET", f"{PART}/auctions/{aid}/subscriptions/{sub}/signing-key", tok)
    out = subprocess.run(["node", "--experimental-strip-types", "--no-warnings", os.path.join(HERE, "frame.ts"),
                          aid, sub, str(riyals * SAR), key["secretHex"]],
                         capture_output=True, text=True, check=True, cwd=HERE).stdout.strip()
    call("POST", f"{CATCH}/bids", tok, raw=bytes.fromhex(json.loads(out)["hex"]), ctype="application/octet-stream")
    log(f"  {user} bid {riyals:,} SAR on {aid[:8]}")
    time.sleep(2)


def wait_admin_status(admin_user, aid, wanted, seconds=180):
    for _ in range(seconds // 2):
        a = call("GET", f"{ADMIN}/auctions/{aid}", direct(admin_user))
        if a["status"] in wanted:
            return a
        time.sleep(2)
    raise RuntimeError(f"{aid}: stuck at {a['status']}")


# --- the run ------------------------------------------------------------------

def main():
    gateway = call("GET", "http://localhost:5111/api/payments", ok=None)
    if isinstance(gateway, dict) and gateway.get("declineCharges"):
        sys.exit("The sandbox gateway is set to refuse payments. Press «قبول الدفع» at http://localhost:5111 first.")

    make_assets()
    admin = direct("admin-user")
    _otp_window("committee-user")
    d = {"grant_type": "password", "client_id": "admin-web", "username": "committee-user",
         "password": "dev-only-password", "totp": totp()}
    committee = json.loads(OPENER.open(urllib.request.Request(KC, urllib.parse.urlencode(d).encode())).read())["access_token"]

    jpg = lambda name: upload(admin, os.path.join(ASSETS, name), "Public", "image/jpeg")
    docs = {
        "booklet": upload(admin, os.path.join(ASSETS, "booklet.pdf"), "Restricted", "application/pdf"),
        "site-plan": upload(admin, os.path.join(ASSETS, "site-plan.pdf"), "Public", "application/pdf"),
        "scheme": upload(admin, os.path.join(ASSETS, "scheme.jpeg"), "Public", "image/jpeg"),
        **{k: jpg(f"{k}.jpg") for k in
           ("photo-cover", "photo-north", "photo-street", "photo-corner", "photo-wide", "photo-dusk")},
    }

    now = datetime.now(timezone.utc)
    day = (now + timedelta(days=2)).replace(hour=7, minute=0, second=0, microsecond=0)   # 10:00 Riyadh
    fast_open = now + timedelta(minutes=5)
    fast_close = fast_open + timedelta(minutes=2)

    # The land's photos and its papers, as the auction page's «الصور» and «المستندات».
    photos = lambda *keys: [(t, k, "Photo") for t, k in zip(
        ("صورة جوية للقطعة", "الشارع الرئيسي", "زاوية القطعة", "منظر عام", "عند الغروب"), keys)]
    papers = [("المخطط المعتمد", "site-plan", "Document"), ("المخطط العام", "scheme", "Document")]

    def plot(number, area, use, facing, street, frontage, lat, lng, desc):
        return dict(plotNumber=number, areaSqm=area, landUse=use, facing=facing,
                    streetWidthMeters=street, frontageMeters=frontage,
                    latitude=lat, longitude=lng, descriptionAr=desc)

    specs = [
        # upcoming, the full page: cover, photos, plans; Sara eligible, Khalid's guarantee to review
        dict(key="A1", nameAr="مخطط الياسمين — قطعة 101", nameEn="Al-Yasmin plan — plot 101", phase="مخطط الياسمين — المرحلة الأولى",
             starts=day, ends=day + timedelta(hours=26), opening=850_000, reserve=800_000, increment=5_000,
             deposit=42_500, booklet=500, quiet=60, cover="photo-cover",
             attachments=photos("photo-north", "photo-street", "photo-corner") + papers,
             plot=plot("310105004101", 625.5, "Residential", "NorthEast", 30, 25, "24.8240", "46.6400",
                       "قطعة سكنية على زاوية شارعين، قريبة من الخدمات.")),
        # upcoming, free booklet
        dict(key="A2", nameAr="مخطط الياسمين — قطعة 102", nameEn="Al-Yasmin plan — plot 102", phase="مخطط الياسمين — المرحلة الأولى",
             starts=day + timedelta(hours=2), ends=day + timedelta(hours=28), opening=780_000, reserve=720_000,
             increment=5_000, deposit=39_000, booklet=0, quiet=60, cover="photo-wide",
             attachments=photos("photo-wide", "photo-dusk") + papers[:1],
             plot=plot("310105004102", 590, "Residential", "South", 20, 22, "24.8243", "46.6408",
                       "قطعة سكنية على شارع ٢٠ م بواجهة جنوبية.")),
        # upcoming, in the hall
        dict(key="A3", nameAr="مخطط النرجس — قطعة 7", nameEn="Al-Narjis plan — plot 7", phase="مخطط النرجس",
             channel="Onsite", starts=day + timedelta(days=1), ends=day + timedelta(days=1, hours=3),
             opening=1_400_000, reserve=1_300_000, increment=10_000, deposit=70_000, booklet=1_000,
             cover="photo-street", attachments=photos("photo-street", "photo-corner") + papers,
             plot=plot("310108000007", 900, "Commercial", "East", 40, 30, "24.8455", "46.6531",
                       "قطعة تجارية على طريق رئيسي — مزاد حضوري.")),
        # approved, then withdrawn with the bidders' money returned
        dict(key="A4", nameAr="مخطط الياسمين — قطعة 103", nameEn="Al-Yasmin plan — plot 103", phase="مخطط الياسمين — المرحلة الأولى",
             starts=day + timedelta(hours=4), ends=day + timedelta(hours=30), opening=700_000, reserve=650_000,
             increment=5_000, deposit=35_000, booklet=500, cover="photo-dusk",
             plot=plot("310105004103", 560, "Residential", "West", 15, 20, "24.8236", "46.6395",
                       "قطعة سكنية داخلية.")),
        # run now: closes, awaits the committee (Khalid leads)
        dict(key="A5", nameAr="مخطط الملقا — قطعة 15", nameEn="Al-Malqa plan — plot 15", phase="مخطط الملقا — المرحلة الثانية",
             starts=fast_open, ends=fast_close, opening=1_200_000, reserve=1_100_000, increment=10_000,
             deposit=60_000, booklet=0, cover="photo-north", attachments=photos("photo-north", "photo-wide"),
             plot=plot("310112000015", 750, "ResidentialCommercial", "NorthWest", 36, 28, "24.8100", "46.6000",
                       "سكني تجاري على شارعين.")),
        # run now: awarded to Sara, letters issued, part paid
        dict(key="A6", nameAr="مخطط الملقا — قطعة 16", nameEn="Al-Malqa plan — plot 16", phase="مخطط الملقا — المرحلة الثانية",
             starts=fast_open, ends=fast_close, opening=950_000, reserve=900_000, increment=10_000,
             deposit=48_000, booklet=0, cover="photo-corner", attachments=photos("photo-corner", "photo-street"),
             plot=plot("310112000016", 640, "Residential", "SouthEast", 25, 24, "24.8106", "46.6011",
                       "قطعة سكنية بواجهة جنوبية شرقية.")),
        # run now with no bids: ends unsold — «إعادة الطرح بسعر مخفّض»
        dict(key="A7", nameAr="مخطط الملقا — قطعة 17", nameEn="Al-Malqa plan — plot 17", phase="مخطط الملقا — المرحلة الثانية",
             starts=fast_open, ends=fast_close, opening=1_000_000, reserve=850_000, increment=10_000,
             deposit=50_000, booklet=0, cover="photo-dusk", attachments=photos("photo-dusk"),
             plot=plot("310112000017", 700, "Residential", "North", 20, 26, "24.8112", "46.6022",
                       "قطعة سكنية لم تُبع في الطرح الأول.")),
        # live for three hours, with bids — the bidding screen, the monitor, «إنهاء المزاد»
        dict(key="A8", nameAr="مخطط الياسمين — قطعة 120", nameEn="Al-Yasmin plan — plot 120", phase="مخطط الياسمين — المرحلة الأولى",
             starts=fast_open, ends=fast_open + timedelta(hours=3), opening=600_000, reserve=550_000, increment=5_000,
             deposit=30_000, booklet=0, quiet=60, cover="photo-wide",
             attachments=photos("photo-wide", "photo-north", "photo-corner") + papers[:1],
             plot=plot("310105004120", 500, "Residential", "NorthEast", 20, 20, "24.8251", "46.6421",
                       "قطعة سكنية — المزاد جارٍ الآن.")),
        # submitted, waiting for the committee's approval
        dict(key="A9", nameAr="مخطط النرجس — قطعة 12", nameEn="Al-Narjis plan — plot 12", phase="مخطط النرجس",
             stage="review", starts=day + timedelta(days=3), ends=day + timedelta(days=3, hours=24),
             opening=1_100_000, reserve=1_000_000, increment=10_000, deposit=55_000, booklet=500,
             cover="photo-street", attachments=photos("photo-street") + papers[:1],
             plot=plot("310108000012", 820, "ResidentialCommercial", "SouthWest", 30, 27, "24.8460", "46.6540",
                       "سكني تجاري — بانتظار اعتماد اللجنة.")),
        # a draft to finish on the auction page
        dict(key="A10", nameAr="مخطط النرجس — قطعة 14", nameEn="Al-Narjis plan — plot 14", phase="مخطط النرجس",
             stage="draft", starts=day + timedelta(days=5), ends=day + timedelta(days=5, hours=24),
             opening=900_000, reserve=850_000, increment=5_000, deposit=45_000, booklet=0,
             plot=plot("310108000014", 600, "Residential", "East", 20, 22, "24.8466", "46.6548",
                       "مسودة — تُستكمل بياناتها من صفحة المزاد.")),
    ]
    ids = {s["key"]: create_auction(admin, committee, s, docs) for s in specs}

    log("qualifying bidders…")
    subs = {}
    subs["sara"] = qualify("sara", ids["A5"], fast_close)
    qualify("sara", ids["A6"], fast_close)
    subs["sara-A8"] = qualify("sara", ids["A8"], specs[7]["ends"])
    qualify("sara", ids["A1"], specs[0]["ends"])
    qualify("sara", ids["A4"], specs[3]["ends"])
    subs["khalid"] = qualify("khalid", ids["A5"], fast_close)
    qualify("khalid", ids["A6"], fast_close)
    subs["khalid-A8"] = qualify("khalid", ids["A8"], specs[7]["ends"])
    qualify("khalid", ids["A1"], specs[0]["ends"], method="Guarantee")

    # A4: withdrawn before it opened, the deposit and the booklet fee returned.
    call("POST", f"{ADMIN}/auctions/{ids['A4']}/cancel", admin,
         {"reason": "إعادة ترقيم القطع في المخطط المعتمد من الأمانة", "refund": True})
    log("A4 cancelled with a reason — deposits and booklet fees refunded")

    log(f"waiting for A5–A8 to open at {iso(fast_open)}…")
    while datetime.now(timezone.utc) < fast_open + timedelta(seconds=4):
        time.sleep(2)
    for _ in range(30):
        live = call("GET", f"{QUERY}/auctions/live", direct("admin-user"))["items"]
        if sum(r["auctionId"] in (ids["A5"], ids["A6"], ids["A8"]) for r in live) >= 3:
            break
        time.sleep(1)

    # A5: Khalid wins. A6: Sara wins. A7: nobody bids. A8: a contest still running.
    bid("sara", ids["A5"], subs["sara"], 1_200_000)
    bid("khalid", ids["A6"], subs["khalid"], 950_000)
    bid("khalid", ids["A5"], subs["khalid"], 1_210_000)
    bid("sara", ids["A6"], subs["sara"], 960_000)
    bid("sara", ids["A5"], subs["sara"], 1_220_000)
    bid("khalid", ids["A5"], subs["khalid"], 1_230_000)
    bid("sara", ids["A8"], subs["sara-A8"], 600_000)
    bid("khalid", ids["A8"], subs["khalid-A8"], 605_000)
    bid("sara", ids["A8"], subs["sara-A8"], 615_000)

    log(f"waiting for A5–A7 to close at {iso(fast_close)} and the processor to name candidates…")
    wait_admin_status("admin-user", ids["A5"], {"PendingAward"})
    wait_admin_status("admin-user", ids["A6"], {"PendingAward"})
    log("A5 and A6 closed — candidates offered")
    try:
        wait_admin_status("admin-user", ids["A7"], {"Unsold"}, seconds=60)
        log("A7 closed with no bids — unsold")
    except RuntimeError as e:
        log(f"A7 not unsold yet ({e}) — check it on the auctions list")

    # A6: award, letters, notice — then a deposit credit and one receipt.
    cm = stepped("committee-user", "admin-web", "http://localhost:3001/")
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award", cm, {"committeeUserId": claims(cm)["sub"]})
    letter = upload(cm, os.path.join(ASSETS, "award-letter.pdf"), "Restricted", "application/pdf")
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award/letter", cm, {"documentId": letter})
    signed = upload(cm, os.path.join(ASSETS, "award-letter-signed.pdf"), "Restricted", "application/pdf")
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award/signed-letter", cm, {"documentId": signed})
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award/notify", cm)
    log("A6 awarded to Sara, letters uploaded, winner notified")

    admin = direct("admin-user")
    apps = call("GET", f"{PART}/auctions/{ids['A6']}/applications", admin)["items"]
    ref = next(a["depositPaymentRef"] for a in apps if a["bidderId"] == subs["sara"]) or "SIM-DEPOSIT"
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award/deposit-credit", admin, {"reference": ref})
    call("POST", f"{ADMIN}/auctions/{ids['A6']}/award/receipts", admin, {
        "amountMinorUnits": 400_000 * SAR, "paidOn": iso(datetime.now(timezone.utc)),
        "reference": "SADAD-DEMO-0001"})
    log("A6: deposit credited and a first payment of 400,000 SAR receipted")

    out = {k: {"id": v, "nameAr": next(s["nameAr"] for s in specs if s["key"] == k)} for k, v in ids.items()}
    with open(os.path.join(HERE, "demo-data.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    log("done — ids written to demo-kit/demo-data.json")


if __name__ == "__main__":
    main()
