#!/usr/bin/env python3
"""
End-to-end smoke test for the running Storefront API (Python 3.9+, standard library only).

Exercises every endpoint, the checkout repricing rules, validation, CORS and Swagger.
When pointed at tools/fakestore_mock.py (--mock) it also drives upstream failure modes.

Usage
  # This script sends a few hundred checkouts, far above the API's normal rate limits, so start
  # the API with RateLimiting__CheckoutPermitLimit=10000 and RateLimiting__AccountPermitLimit=1000.

  # Against the offline stand-in (start the API with FakeStore__BaseUrl=http://localhost:9100/
  # and FakeStore__CatalogueCacheSeconds=0 so failure modes aren't hidden by the cache):
  python3 tools/smoke_test.py --api http://localhost:5080 --mock http://localhost:9100

  # Against live FakeStoreAPI (no failure-mode checks):
  python3 tools/smoke_test.py --api http://localhost:5080

Exit code is 0 only if no check failed. Skipped checks are listed with the reason.
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import time
import urllib.error
import urllib.request
from decimal import ROUND_HALF_UP, Decimal

TWO = Decimal("0.01")


class Result:
    rate_limited = False

    def __init__(self) -> None:
        self.passed = 0
        self.failed: list[str] = []
        self.skipped: list[str] = []

    def check(self, name: str, condition: bool, detail: str = "") -> bool:
        if condition:
            self.passed += 1
            print(f"  PASS  {name}")
        else:
            self.failed.append(name)
            detail = detail if len(detail) <= 300 else detail[:300] + "..."
            print(f"  FAIL  {name}{' - ' + detail if detail else ''}")
        return condition

    def skip(self, name: str, reason: str) -> None:
        self.skipped.append(f"{name}: {reason}")
        print(f"  SKIP  {name} - {reason}")


def request(method: str, url: str, body: object | None = None, headers: dict | None = None,
            raw: bytes | None = None, timeout: float = 30):
    """Returns (status, headers, parsed JSON or text)."""
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    hdrs = {"Accept": "application/json", **(headers or {})}
    if body is not None and "Content-Type" not in hdrs:
        hdrs["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            status, rh, payload = resp.status, resp.headers, resp.read()
    except urllib.error.HTTPError as err:
        status, rh, payload = err.code, err.headers, err.read()
    if status == 429:
        Result.rate_limited = True
    text = payload.decode("utf-8", "replace")
    try:
        parsed = json.loads(text) if text else None
    except json.JSONDecodeError:
        parsed = text
    return status, rh, parsed


def money(value) -> Decimal:
    return Decimal(str(value)).quantize(TWO, rounding=ROUND_HALF_UP)


def expected_totals(lines: list[tuple[Decimal, int]], cfg: dict) -> dict:
    """The documented pricing policy, computed independently of the API."""
    subtotal = sum((money(price) * qty for price, qty in lines), Decimal("0"))
    tax = (subtotal * Decimal(str(cfg["taxRate"]))).quantize(TWO, rounding=ROUND_HALF_UP)
    threshold = Decimal(str(cfg["freeShippingThreshold"]))
    shipping = Decimal(str(cfg["flatShippingFee"])) if 0 < subtotal < threshold else Decimal("0")
    return {"subtotal": subtotal, "tax": tax, "shipping": shipping, "total": subtotal + tax + shipping}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--api", default="http://localhost:5080")
    ap.add_argument("--mock", help="Base URL of tools/fakestore_mock.py to enable failure-mode checks")
    ap.add_argument("--origin", default="http://localhost:5173", help="Allowed frontend origin")
    ap.add_argument("--carts", type=int, default=200, help="Random carts for the repricing check")
    args = ap.parse_args()
    api, r = args.api.rstrip("/"), Result()
    customer = {"name": "Ada Lovelace", "email": "ada@example.com"}

    print(f"Storefront smoke test against {api}")

    print("\n[health & docs]")
    status, _, _ = request("GET", f"{api}/health")
    if not r.check("GET /health returns 200", status == 200, f"got {status}"):
        print("API not reachable; aborting.")
        return 1
    status, _, doc = request("GET", f"{api}/swagger/v1/swagger.json")
    if status == 404:
        r.skip("Swagger document", "404 - Swagger UI and JSON are only mapped when ASPNETCORE_ENVIRONMENT=Development")
    else:
        paths = (doc or {}).get("paths", {}) if isinstance(doc, dict) else {}
        r.check("GET /swagger/v1/swagger.json lists the API",
                status == 200 and {"/api/products", "/api/products/{id}", "/api/categories",
                                   "/api/checkout", "/api/store/config"} <= set(paths),
                f"status {status}, paths {sorted(paths)}")
        status, _, ui = request("GET", f"{api}/swagger/index.html")
        r.check("GET /swagger/index.html serves Swagger UI", status == 200 and "swagger" in str(ui).lower(), f"got {status}")

    print("\n[catalogue]")
    status, _, cfg = request("GET", f"{api}/api/store/config")
    r.check("GET /api/store/config", status == 200 and isinstance(cfg, dict) and "taxRate" in cfg, f"got {status}")
    status, _, listing = request("GET", f"{api}/api/products")
    items = listing.get("items", []) if isinstance(listing, dict) else []
    if not r.check("GET /api/products returns products", status == 200 and len(items) > 0, f"got {status}"):
        print("Catalogue unavailable; aborting.")
        return 1
    r.check("count matches items", listing.get("count") == len(items))
    r.check("priceMinor equals price in cents for every product",
            all(int(money(p["price"]) * 100) == p["priceMinor"] for p in items))
    r.check("category slugs are normalised", all(p["category"] and " " not in p["category"] and "'" not in p["category"] for p in items))
    by_id = {p["id"]: p for p in items}

    status, _, cats = request("GET", f"{api}/api/categories")
    slugs = {c["slug"] for c in cats} if isinstance(cats, list) else set()
    r.check("GET /api/categories includes clothing group and jewelry", status == 200 and {"clothing", "jewelry"} <= slugs, str(slugs))

    status, _, res = request("GET", f"{api}/api/products?category=clothing&sort=price-asc")
    got = res.get("items", []) if isinstance(res, dict) else []
    prices = [Decimal(str(p["price"])) for p in got]
    r.check("category=clothing&sort=price-asc: only clothing, ascending",
            status == 200 and got and all(p["categoryGroup"] == "clothing" for p in got) and prices == sorted(prices))
    status, _, res = request("GET", f"{api}/api/products?sort=rating")
    rates = [(p["rating"] or {"rate": -1})["rate"] for p in res.get("items", [])]
    r.check("sort=rating is descending", status == 200 and rates == sorted(rates, reverse=True))
    term = items[0]["title"].split()[0].lower()
    status, _, res = request("GET", f"{api}/api/products?search={urllib.request.quote(term)}")
    r.check(f"search={term!r} only returns matching titles",
            status == 200 and res["count"] > 0 and all(term in p["title"].lower() for p in res["items"]))
    first = items[0]["id"]
    status, _, res = request("GET", f"{api}/api/products/{first}")
    r.check(f"GET /api/products/{first}", status == 200 and res["id"] == first)
    status, _, res = request("GET", f"{api}/api/products/987654")
    r.check("unknown product -> 404 NOT_FOUND", status == 404 and isinstance(res, dict) and res.get("code") == "NOT_FOUND", f"{status} {res}")
    status, _, res = request("GET", f"{api}/api/products/0")
    r.check("product id 0 -> 400", status == 400)
    status, _, res = request("GET", f"{api}/api/products?sort=cheapest")
    r.check("invalid sort -> 400 VALIDATION_FAILED with 'sort' key",
            status == 400 and res.get("code") == "VALIDATION_FAILED" and "sort" in res.get("errors", {}), f"{status} {res}")
    status, _, res = request("GET", f"{api}/api/products?minPrice=50&maxPrice=10")
    r.check("minPrice > maxPrice -> 400", status == 400)

    print("\n[checkout repricing]")
    rng = random.Random(42)
    mismatches = []
    for _ in range(args.carts):
        chosen = rng.sample(sorted(by_id), k=rng.randint(1, min(4, len(by_id))))
        lines = [(pid, rng.choice([1, 1, 1, 2, 3, 7])) for pid in chosen]
        exp = expected_totals([(Decimal(str(by_id[pid]["price"])), q) for pid, q in lines], cfg)
        body = {"customer": customer, "items": [{"productId": p, "quantity": q} for p, q in lines],
                "expectedTotal": float(exp["total"])}
        status, _, res = request("POST", f"{api}/api/checkout", body)
        if status != 200 or any(money(res["totals"][k]) != exp[k] for k in ("subtotal", "tax", "shipping", "total")):
            mismatches.append((lines, status, res if status != 200 else res["totals"], {k: str(v) for k, v in exp.items()}))
    r.check(f"{args.carts} random carts: server totals equal the documented policy", not mismatches,
            f"{len(mismatches)} mismatches, first: {mismatches[:1]}")

    # Below / above the free-shipping threshold, using the cheapest product.
    cheapest = min(items, key=lambda p: p["price"])
    threshold = Decimal(str(cfg["freeShippingThreshold"]))
    below = max(1, int((threshold - TWO) // Decimal(str(cheapest["price"]))))
    above = int(threshold // Decimal(str(cheapest["price"]))) + 1
    for qty, label in ((below, "below"), (above, "at/above")):
        if qty > cfg["maxQuantityPerLine"]:
            r.skip(f"shipping {label} threshold", "cheapest product too cheap to reach threshold within max quantity")
            continue
        exp = expected_totals([(Decimal(str(cheapest["price"])), qty)], cfg)
        status, _, res = request("POST", f"{api}/api/checkout", {
            "customer": customer, "items": [{"productId": cheapest["id"], "quantity": qty}], "expectedTotal": float(exp["total"])})
        r.check(f"shipping {label} threshold (subtotal {exp['subtotal']}) = {exp['shipping']}",
                status == 200 and money(res["totals"]["shipping"]) == exp["shipping"], f"{status} {res}")

    pid = first
    exp = expected_totals([(Decimal(str(by_id[pid]["price"])), 1)], cfg)
    status, _, res = request("POST", f"{api}/api/checkout", {
        "customer": customer, "items": [{"productId": pid, "quantity": 1, "unitPrice": 0.01, "price": 0.01}],
        "expectedTotal": float(exp["total"])})
    r.check("client-supplied unit prices are ignored", status == 200 and money(res["totals"]["total"]) == exp["total"], f"{status} {res}")
    r.check("confirmation is marked simulated with a DEMO- reference",
            status == 200 and res.get("isSimulated") is True and str(res.get("orderReference", "")).startswith("DEMO-"))

    status, _, res = request("POST", f"{api}/api/checkout", {
        "customer": customer, "items": [{"productId": pid, "quantity": 1}], "expectedTotal": 0.01})
    quote_total = money(((res or {}).get("quote") or {}).get("totals", {}).get("total", -1)) if isinstance(res, dict) else None
    r.check("stale expectedTotal -> 409 PRICE_CHANGED with server quote",
            status == 409 and res.get("code") == "PRICE_CHANGED" and quote_total == exp["total"], f"{status} {res}")
    status, _, res = request("POST", f"{api}/api/checkout", {"customer": customer, "items": [{"productId": pid, "quantity": 1}]})
    r.check("missing expectedTotal -> 400 (customer must confirm a total)",
            status == 400 and "expectedTotal" in (res or {}).get("errors", {}), f"{status} {res}")

    print("\n[checkout validation]")
    def bad(name, body, key=None, code=400, raw=None, ctype="application/json"):
        status, _, res = request("POST", f"{api}/api/checkout", None if raw is not None else body,
                                 headers={"Content-Type": ctype}, raw=raw)
        ok = status == code and (key is None or key in (res or {}).get("errors", {}))
        r.check(f"{name} -> {code}", ok, f"{status} {res if isinstance(res, dict) else str(res)[:120]}")
        return res
    base = {"customer": customer, "expectedTotal": float(exp["total"])}
    bad("quantity 0", {**base, "items": [{"productId": pid, "quantity": 0}]}, "items[0].quantity")
    bad("quantity -1", {**base, "items": [{"productId": pid, "quantity": -1}]}, "items[0].quantity")
    bad("quantity above maximum", {**base, "items": [{"productId": pid, "quantity": cfg["maxQuantityPerLine"] + 1}]}, "items[0].quantity")
    bad("product id 0", {**base, "items": [{"productId": 0, "quantity": 1}]}, "items[0].productId")
    bad("duplicate product lines", {**base, "items": [{"productId": pid, "quantity": 1}, {"productId": pid, "quantity": 1}]}, "items[1].productId")
    bad("empty cart", {**base, "items": []}, "items")
    bad("invalid email", {**base, "customer": {"name": "Ada", "email": "nope"}, "items": [{"productId": pid, "quantity": 1}]}, "customer.email")
    bad("missing name", {**base, "customer": {"email": "ada@example.com"}, "items": [{"productId": pid, "quantity": 1}]}, "customer.name")
    bad("negative expectedTotal", {**base, "expectedTotal": -1, "items": [{"productId": pid, "quantity": 1}]}, "expectedTotal")
    bad("malformed JSON", None, raw=b'{"items": [', code=400)
    bad("non-JSON content type", None, raw=b"items=1", ctype="application/x-www-form-urlencoded", code=415)
    big = json.dumps({**base, "items": [{"productId": pid, "quantity": 1}] * 20000}).encode()
    bad(f"oversized body ({len(big) // 1024} KiB)", None, raw=big, code=413)
    status, _, res = request("POST", f"{api}/api/checkout", {**base, "items": [{"productId": pid, "quantity": 1}, {"productId": 987654, "quantity": 1}]})
    r.check("unknown product -> 422 PRODUCTS_UNAVAILABLE with ids",
            status == 422 and res.get("unavailableProductIds") == [987654], f"{status} {res}")

    print("\n[idempotency]")
    key = f"smoke-{random.Random().getrandbits(64):016x}"
    one = {"customer": customer, "items": [{"productId": pid, "quantity": 1}], "expectedTotal": float(exp["total"])}
    s1, h1, r1 = request("POST", f"{api}/api/checkout", one, headers={"Idempotency-Key": key})
    s2, h2, r2 = request("POST", f"{api}/api/checkout", one, headers={"Idempotency-Key": key})
    r.check("same Idempotency-Key twice -> same order, second marked Idempotent-Replayed",
            s1 == 200 and s2 == 200 and r1.get("orderReference") == r2.get("orderReference")
            and h2.get("Idempotent-Replayed") == "true" and h1.get("Idempotent-Replayed") is None, f"{s1} {s2}")
    two = {**one, "items": [{"productId": pid, "quantity": 2}]}
    s3, _, r3 = request("POST", f"{api}/api/checkout", two, headers={"Idempotency-Key": key})
    r.check("same key, different order -> 422 IDEMPOTENCY_KEY_REUSED",
            s3 == 422 and (r3 or {}).get("code") == "IDEMPOTENCY_KEY_REUSED", f"{s3} {r3}")

    print("\n[accounts & orders]")
    status, _, res = request("GET", f"{api}/api/orders")
    r.check("GET /api/orders without a token -> 401 UNAUTHORIZED",
            status == 401 and isinstance(res, dict) and res.get("code") == "UNAUTHORIZED", f"{status} {res}")
    email, password = f"smoke-{random.Random().getrandbits(32):08x}@example.com", "Smoke!Test123"
    status, _, res = request("POST", f"{api}/api/account/register", {"email": email, "password": password})
    r.check("register a new account", status == 200, f"{status} {res}")
    status, _, res = request("POST", f"{api}/api/account/register", {"email": "weak@example.com", "password": "short"})
    r.check("weak password -> 400 VALIDATION_FAILED", status == 400 and res.get("code") == "VALIDATION_FAILED", f"{status} {res}")
    status, _, res = request("POST", f"{api}/api/account/login", {"email": email, "password": "Wrong!Pass123"})
    r.check("wrong password -> 401", status == 401, f"{status}")
    status, _, token = request("POST", f"{api}/api/account/login", {"email": email, "password": password})
    if r.check("login returns a bearer token", status == 200 and "accessToken" in (token or {}), f"{status} {token}"):
        auth = {"Authorization": f"Bearer {token['accessToken']}"}
        status, _, placed = request("POST", f"{api}/api/checkout", one, headers=auth)
        status, _, mine = request("GET", f"{api}/api/orders", headers=auth)
        r.check("signed-in checkout appears in GET /api/orders",
                status == 200 and [o["orderReference"] for o in mine] == [placed.get("orderReference")], f"{status} {mine}")
        status, _, detail = request("GET", f"{api}/api/orders/{placed.get('orderReference')}", headers=auth)
        r.check("GET /api/orders/{reference} returns the stored order",
                status == 200 and money(detail["totals"]["total"]) == exp["total"], f"{status} {detail}")
        status, _, _ = request("GET", f"{api}/api/orders/{r1.get('orderReference')}", headers=auth)
        r.check("someone else's (guest) order -> 404", status == 404, f"{status}")

    print("\n[CORS]")
    pre = {"Origin": args.origin, "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "content-type"}
    status, h, _ = request("OPTIONS", f"{api}/api/checkout", headers=pre)
    r.check(f"preflight from {args.origin} allowed", h.get("Access-Control-Allow-Origin") == args.origin, f"{status} {dict(h)}")
    status, h, _ = request("OPTIONS", f"{api}/api/checkout", headers={**pre, "Origin": "http://evil.example"})
    r.check("preflight from another origin refused", h.get("Access-Control-Allow-Origin") is None)
    status, h, _ = request("GET", f"{api}/api/products/987654", headers={"Origin": args.origin})
    r.check("CORS header present on 404 error responses", h.get("Access-Control-Allow-Origin") == args.origin)
    status, h, _ = request("POST", f"{api}/api/checkout", {**base, "items": [{"productId": pid, "quantity": 0}]}, headers={"Origin": args.origin})
    r.check("CORS header present on 400 error responses", h.get("Access-Control-Allow-Origin") == args.origin)

    print("\n[upstream failures]")
    if not args.mock:
        r.skip("upstream failure modes", "pass --mock to drive tools/fakestore_mock.py")
    else:
        mock = args.mock.rstrip("/")
        try:
            def mode(m):
                request("GET", f"{mock}/__mode/{m}")
            mode("500")
            status, h, res = request("GET", f"{api}/api/products", headers={"Origin": args.origin})
            if status == 200:
                r.skip("upstream failure modes", "catalogue is cached; restart the API with FakeStore__CatalogueCacheSeconds=0")
            else:
                r.check("upstream HTTP 500 -> 502 UPSTREAM_UNAVAILABLE", status == 502 and res.get("code") == "UPSTREAM_UNAVAILABLE", f"{status} {res}")
                r.check("CORS header present on 502 (browser can read the error)", h.get("Access-Control-Allow-Origin") == args.origin)
                status, _, res = request("POST", f"{api}/api/checkout", {**base, "items": [{"productId": pid, "quantity": 1}]})
                r.check("checkout during outage -> 502 (no order without authoritative prices)", status == 502, f"{status} {res}")
                mode("malformed")
                status, _, res = request("GET", f"{api}/api/categories")
                r.check("malformed upstream body -> 502 UPSTREAM_MALFORMED", status == 502 and res.get("code") == "UPSTREAM_MALFORMED", f"{status} {res}")
                mode("slow")
                started = time.monotonic()
                status, _, res = request("GET", f"{api}/api/products", timeout=60)
                r.check(f"slow upstream -> 504 UPSTREAM_TIMEOUT ({time.monotonic() - started:.1f}s)",
                        status == 504 and res.get("code") == "UPSTREAM_TIMEOUT", f"{status} {res}")
                mode("ok")
                status, _, _ = request("GET", f"{api}/api/products")
                r.check("recovers once upstream is healthy", status == 200)
        finally:
            request("GET", f"{mock}/__mode/ok")

    print(f"\n{r.passed} passed, {len(r.failed)} failed, {len(r.skipped)} skipped")
    if Result.rate_limited:
        print("  note: some requests got 429 TOO_MANY_REQUESTS. Restart the API with "
              "RateLimiting__CheckoutPermitLimit=10000 and RateLimiting__AccountPermitLimit=1000 (see the docstring).")
    for name in r.failed:
        print(f"  failed: {name}")
    for s in r.skipped:
        print(f"  skipped: {s}")
    return 1 if r.failed else 0


if __name__ == "__main__":
    sys.exit(main())
