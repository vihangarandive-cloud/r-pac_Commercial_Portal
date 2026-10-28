"""One-shot diagnosis for the GAP Brands add-to-cart flow.

The portal answers every rejected add with the same catch-all message
("Failed to add to shopping cart, please try again later!"), so the message alone
never says *which* field is wrong. This script prints the exact request the app
sends, then (with --post) the exact response.

    python diagnose.py P2608760                     # build the request, do not post
    python diagnose.py P2608760 --post              # post it and show the response
    python diagnose.py P2608760 --post --clear-cart # empty the cart first
    python diagnose.py P2608760 --lpn YES --brand GAP --gender <id> --type <id>

Credentials come from the same .env as the app (GAP_USERNAME / GAP_PASSWORD).
"""

import argparse
import json
import sys
from pathlib import Path

from auth import DOTENV_PATH, credentials_configured, get_authenticated_session
import portal_api as api
import envcheck


def _redacted(payload):
    clean = dict(payload)
    if clean.get("__RequestVerificationToken"):
        clean["__RequestVerificationToken"] = "<redacted>"
    return clean


def _cart_summary(session) -> str:
    try:
        res = session.get(api.CART_URL)
        if not res.ok:
            return f"cart unreadable (HTTP {res.status_code})"
        items = res.json().get("items", [])
    except Exception as exc:
        return f"cart unreadable ({type(exc).__name__}: {exc})"
    if not items:
        return "cart is empty"
    pos = api.PortalAPI.cart_po_numbers(items)
    return f"{len(items)} item(s) in the cart" + (f" (POs: {', '.join(pos)})" if pos else "")


def main() -> int:
    parser = argparse.ArgumentParser(description="Diagnose a GAP Brands add-to-cart rejection.")
    parser.add_argument("po", help="PO number, e.g. P2608760")
    parser.add_argument("--brand", default="Old Navy", choices=["Old Navy", "GAP"],
                        help="brand tab that was selected in the UI (default: Old Navy)")
    parser.add_argument("--gender", default="", help="productGenderId sent by the UI")
    parser.add_argument("--type", default="", help="productTypeId sent by the UI")
    parser.add_argument("--season", default="", help="seasonValue sent by the UI")
    parser.add_argument("--franchise", default="None", help="franchise GUID (default: None)")
    parser.add_argument("--vendor-po", default="", help="vendor PO, empty means 'no'")
    parser.add_argument("--price-tickets", default="None", help="priceTicketId (default: None)")
    parser.add_argument("--price-ticket-code", default="None", help="price ticket item code")
    parser.add_argument("--lpn", default="NO", choices=["YES", "NO"], help="order LPN labels")
    parser.add_argument("--order-type", default="MAS", choices=["MAS", "Non-MAS"])
    parser.add_argument("--post", action="store_true", help="actually POST to to-job (adds to the cart)")
    parser.add_argument("--clear-cart", action="store_true", help="empty the cart before posting")
    parser.add_argument("--dump-head", action="store_true",
                        help="print the raw gap-po-item-head record and the first detail line "
                             "(shows whether the portal already has productGenderId/seasonId)")
    args = parser.parse_args()

    username, has_password = credentials_configured()
    print(f"username     : {username}")
    print(f".env         : {DOTENV_PATH} ({'found' if DOTENV_PATH.exists() else 'MISSING'})")
    print(f"password set : {has_password}")
    print(f"portal_api   : {api.SOURCE_SHA} "
          f"(compare with the 'portal_api_sha' field of /api/health - "
          f"different value means IIS is running another copy)")
    print(envcheck.format_runtime(envcheck.runtime_info(Path(__file__).resolve().parent)))
    if not has_password:
        print("\nGAP_PASSWORD is not set - create the .env file next to auth.py.")
        return 2

    try:
        session = api.TimedSession(get_authenticated_session(verify=True))
    except Exception as exc:
        print(f"\nLOGIN FAILED: {type(exc).__name__}: {exc}")
        return 3
    print("login        : OK")
    print(f"cookies      : {len(session.cookies)}")

    print(f"\ncart before  : {_cart_summary(session)}")
    if args.clear_cart:
        cleared, remaining, note = api.PortalAPI().clear_cart(session)
        print(f"clear cart   : {'OK' if cleared else 'FAILED'} - {note}")
        if remaining:
            print(f"               still there: {', '.join(api.PortalAPI.cart_po_numbers(remaining)) or 'unknown POs'}")

    token = api.PortalAPI.cart_token(session)
    print(f"xsrf token   : {'present (%d chars)' % len(token) if token else 'MISSING - every write call will be rejected'}")

    trace = {}
    po_num, success, error = api.fetch_single_po(
        session, args.po, args.brand, args.gender, args.type, args.franchise,
        args.vendor_po, args.season, args.price_tickets, args.price_ticket_code,
        args.lpn, args.order_type, token, dry_run=not args.post, trace=trace,
    )

    head = trace.get("head_item") or {}
    details = trace.get("detail_items") or []
    print("\n--- PO lookup ---")
    print(f"head id      : {head.get('id')}")
    for key in ("poNo", "customerPo", "vendorPo", "style", "brand", "status", "jobId", "orderId"):
        if key in head:
            print(f"{key:<13}: {head.get(key)}")
    print(f"detail lines : {len(details)}")
    for d in details[:5]:
        print(f"  id={d.get('id')} qty={d.get('quantity')} style={d.get('style')} color={d.get('colorCode')}")
    if len(details) > 5:
        print(f"  ... {len(details) - 5} more")
    if args.dump_head:
        print("\n--- gap-po-item-head (raw) ---")
        print(json.dumps(head, indent=2, default=str))
        if details:
            print("\n--- first detail line (raw) ---")
            print(json.dumps(details[0], indent=2, default=str))

    payload = trace.get("payload") or {}
    qty_keys = {k: v for k, v in payload.items() if k.startswith(("qty_", "lpnQty_"))}
    print("\n--- quantities sent ---")
    if args.lpn == "YES":
        for d in details[:5]:
            try:
                base = int(float(d.get("quantity")))
            except (TypeError, ValueError):
                continue
            print(f"  {d.get('id')}: PO qty {base} -> slab "
                  f"{api.calculate_lpn_quantity(base, args.order_type, args.brand)}")
    print(f"  qty_/lpnQty_ entries: {len(qty_keys)}")
    print(f"  sample: {dict(list(qty_keys.items())[:6])}")

    print("\n--- request body (token redacted) ---")
    print(json.dumps(_redacted(payload), indent=2, default=str))

    variants = trace.get("variants") or []
    if len(variants) > 1:
        print(f"\n--- fallback payloads ({len(variants)}) ---")
        for variant in variants:
            body = variant.get("payload") or {}
            fields = {k: body.get(k) for k in
                      ("productGenderId", "productTypeId", "seasonId", "franchisesId", "cooId")}
            print(f"  {variant.get('name'):<13} {fields}")

    if not args.post:
        print("\nDry run only. Re-run with --post to send this request.")
        return 0

    print("\n--- response ---")
    attempts = trace.get("attempts") or []
    if attempts:
        for attempt in attempts:
            print(f"[{attempt.get('variant')}] HTTP {attempt.get('status_code')}")
            print(attempt.get("response") or "<no body>")
    else:
        print(f"HTTP {trace.get('status_code')}")
        print(trace.get("response") or "<no body>")
    accepted = trace.get("accepted_variant")
    print(f"result       : {'SUCCESS' if success else 'REJECTED'}"
          + (f" (accepted payload variant: {accepted})" if success and accepted else ""))
    if accepted and accepted != "as-sent":
        print(f"fix          : add GAP_PAYLOAD_VARIANT={accepted} to .env to skip the retries")
    if error:
        print(f"reason       : {error}")
    print(f"\ncart after   : {_cart_summary(session)}")

    if not success:
        print("""
--- what the catch-all message usually means ---
1. The PO is already in the cart or already converted to a job. Check the cart
   above and the portal's PO list; --clear-cart does not undo a finished order.
2. The brand tab does not match the PO (GAP vs Old Navy). Re-run with the other
   --brand value.
3. productGenderId / productTypeId / seasonValue are empty - the portal needs
   them. Rerun with the same --gender/--type/--season values the page posts
   (browser DevTools > Network > POST /api/app/gap-po-item/to-job).
4. The portal session expired between login and the POST (token short/missing).
5. The detail quantities are wrong (LPN slab). Try --lpn NO to isolate it.
""")
    return 0 if success else 1


if __name__ == "__main__":
    sys.exit(main())
