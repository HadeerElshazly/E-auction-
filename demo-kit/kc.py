"""DEMO KIT — delete with the rest of demo-kit/ after the demo.

Stepped-up Keycloak login, the way the portals do it: auth code + PKCE with
acr_values=high, password form, then the OTP form fed from the dev realm's secret."""
import base64, hashlib, hmac, html, json, os, re, struct, time, urllib.parse, urllib.request
from http.cookiejar import CookieJar

ISSUER = "http://localhost:8080/realms/eauction"
SECRET = b"eauctiondevsecret1234567890"


def totp() -> str:
    c = int(time.time()) // 30
    h = hmac.new(SECRET, struct.pack(">Q", c), hashlib.sha1).digest()
    o = h[-1] & 15
    return "%06d" % ((struct.unpack(">I", h[o:o + 4])[0] & 0x7FFFFFFF) % 1000000)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def _form_action(page: str) -> str:
    m = re.search(r'<form[^>]*action="([^"]+)"', page)
    if not m:
        raise RuntimeError("no form on page: " + page[:300])
    return html.unescape(m.group(1))


def stepped_token(user: str, client: str, redirect: str, acr: str = "high") -> str:
    # Cookies by hand: Keycloak marks its session cookies Secure, and a cookie jar
    # will not send those back over plain http://localhost.
    cookies: dict[str, str] = {}
    base = urllib.request.build_opener(_NoRedirect(), urllib.request.ProxyHandler({}))

    class _Opener:
        def open(self, req):
            if isinstance(req, str):
                req = urllib.request.Request(req)
            if cookies:
                req.add_header("Cookie", "; ".join(f"{k}={v}" for k, v in cookies.items()))
            try:
                r = base.open(req)
                headers = r.headers
            except urllib.error.HTTPError as e:
                headers = e.headers
                for c in headers.get_all("Set-Cookie") or []:
                    k, v = c.split(";", 1)[0].split("=", 1)
                    cookies[k.strip()] = v
                raise
            for c in headers.get_all("Set-Cookie") or []:
                k, v = c.split(";", 1)[0].split("=", 1)
                cookies[k.strip()] = v
            return r

    opener = _Opener()
    verifier = base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    q = urllib.parse.urlencode({
        "client_id": client, "redirect_uri": redirect, "response_type": "code",
        "scope": "openid", "code_challenge": challenge, "code_challenge_method": "S256",
        "acr_values": acr, "prompt": "login",
    })
    page = opener.open(f"{ISSUER}/protocol/openid-connect/auth?{q}").read().decode()

    def post(url, data):
        try:
            r = opener.open(urllib.request.Request(url, urllib.parse.urlencode(data).encode()))
            return r.read().decode(), None
        except urllib.error.HTTPError as e:
            if e.code in (302, 303):
                return None, e.headers["Location"]
            raise

    page, loc = post(_form_action(page), {"username": user, "password": "dev-only-password"})
    for _ in range(3):  # OTP page (maybe more than once if the code rolled over)
        if loc:
            break
        if "otp" not in page.lower():
            raise RuntimeError("unexpected page after password: " + page[:400])
        # Avoid a code about to expire.
        if 30 - int(time.time()) % 30 < 3:
            time.sleep(4)
        page, loc = post(_form_action(page), {"otp": totp(), "login": "Sign In"})
    if not loc:
        raise RuntimeError("login did not complete")
    code = urllib.parse.parse_qs(urllib.parse.urlparse(loc).query)["code"][0]
    tok = urllib.request.build_opener(urllib.request.ProxyHandler({})).open(urllib.request.Request(
        f"{ISSUER}/protocol/openid-connect/token",
        urllib.parse.urlencode({"grant_type": "authorization_code", "client_id": client,
                                "code": code, "redirect_uri": redirect,
                                "code_verifier": verifier}).encode())).read()
    return json.loads(tok)["access_token"]


def claims(token: str) -> dict:
    p = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(p + "=" * (-len(p) % 4)))


