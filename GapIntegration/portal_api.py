import io
import os
import time
import logging
import hashlib
import traceback
import zipfile
import urllib.parse
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import List, Dict, Generator, Any, Tuple
import requests
import json
import re

from auth import get_authenticated_session, TIMEOUT

logger = logging.getLogger("gap.portal")

PORTAL_ROOT = "https://gapbrands.r-pac.com.hk"
CART_URL = f"{PORTAL_ROOT}/api/app/shopping-cart"
CART_PAGE_URL = f"{PORTAL_ROOT}/Main/ShoppingCart"

# Sentinel returned by fetch_single_po when the portal answers 401/403: the
# session expired mid-run, so the PO is worth retrying after a fresh login.
SESSION_EXPIRED = "__SESSION_EXPIRED__"


class TimedSession:
    """Proxy around requests.Session that applies a default timeout to every call.

    Without this every portal call could block indefinitely (no timeout anywhere
    in the original code), which is what makes the IIS worker hang and answer
    502.3 instead of an error message.
    """

    def __init__(self, session: requests.Session, timeout=TIMEOUT):
        self._session = session
        self._timeout = timeout

    def get(self, url, **kwargs):
        kwargs.setdefault("timeout", self._timeout)
        return self._session.get(url, **kwargs)

    def post(self, url, **kwargs):
        kwargs.setdefault("timeout", self._timeout)
        return self._session.post(url, **kwargs)

    @property
    def headers(self):
        return self._session.headers

    @property
    def cookies(self):
        return self._session.cookies


def _body_snippet(res, limit=400) -> str:
    """Compact response body for error messages (HTML pages are useless in full)."""
    try:
        text = res.text or ""
    except Exception:
        return f"{len(res.content)} bytes"
    text = " ".join(text.split())
    return text[:limit] + ("…" if len(text) > limit else "")


def _no_internet_hint(res) -> str:
    if res.status_code == 401:
        return " (401 = the portal session expired or the account was logged out)"
    if res.status_code in (502, 503, 504):
        return " (portal or a reverse proxy in front of it is unavailable)"
    return ""


def _source_sha() -> str:
    """Hash of this file, so /api/health can prove which copy is running."""
    try:
        with open(__file__, "rb") as fh:
            return hashlib.sha1(fh.read()).hexdigest()[:12]
    except Exception:
        return "unknown"


SOURCE_SHA = _source_sha()

GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


def _looks_like_guid(value) -> bool:
    return isinstance(value, str) and bool(GUID_RE.match(value.strip()))


def _head_lookup(item, *names):
    """Case-insensitive read of a gap-po-item-head field."""
    if not isinstance(item, dict):
        return None
    lowered = {str(k).lower(): v for k, v in item.items()}
    for name in names:
        value = lowered.get(name.lower())
        if value not in (None, "", "None", "null"):
            return value
    return None


def _resolve_name_to_id(session, url, name):
    """Resolve a dropdown label ("BOYS", "Tops", "FA26") to the portal's id.

    The page hard-codes the labels in brandData, so the payload carries text where
    the API expects an id — a plausible cause for a server-side exception inside
    to-job, which the portal reports with its catch-all "Failed to add to shopping
    cart" sentence. Every failure here is non-fatal: the text payload stays first.
    """
    if not name:
        return None
    try:
        res = session.get(url)
        if not res.ok:
            return None
        body = res.json()
    except Exception as exc:
        logger.debug("lookup %s failed: %s", url, exc)
        return None
    rows = body.get("items") if isinstance(body, dict) else body
    if not isinstance(rows, list):
        return None
    wanted = str(name).strip().lower()
    for row in rows:
        if not isinstance(row, dict):
            continue
        label = None
        for key in ("name", "displayName", "text", "value", "code"):
            if row.get(key):
                label = str(row[key]).strip().lower()
                break
        if label == wanted:
            for key in ("id", "value", "code"):
                if row.get(key):
                    return row[key]
    return None


def _payload_variants(payload, item, session, product_gender, product_type, season):
    """Candidate to-job payloads, most likely first.

    The portal answers one catch-all sentence for every exception it swallows, so a
    rejection is retried with the values the portal itself knows about instead of
    failing the whole run:

      1. ``as-sent``      – exactly what the UI dropdowns produced (text values).
      2. ``po-head``      – gender/type/season/franchise/coo taken from gap-po-item-head.
      3. ``resolved-ids`` – dropdown text resolved to ids through the lookup APIs.

    The variant that is accepted is logged; ``GAP_PAYLOAD_VARIANT`` pins it so the
    retries can be skipped next time.
    """
    variants = []

    def add(name, candidate):
        for _existing_name, existing in variants:
            if existing == candidate:
                return
        variants.append((name, candidate))

    add("as-sent", dict(payload))

    head = dict(payload)
    overrides = {}
    for field, keys in (
        ("productGenderId", ("productGenderId", "productGender")),
        ("productTypeId", ("productTypeId", "productType")),
        ("seasonId", ("seasonId",)),
        ("franchisesId", ("franchisesId", "franchiseId")),
        ("cooId", ("cooId",)),
    ):
        value = _head_lookup(item, *keys)
        if _looks_like_guid(value):
            overrides[field] = value.strip()
    if overrides:
        head.update(overrides)
        add("po-head", head)

    resolved = dict(head)
    changed = False
    gender_id = resolved.get("productGenderId")
    if not _looks_like_guid(gender_id):
        gender_id = _resolve_name_to_id(session, f"{PORTAL_ROOT}/api/app/product-gender", product_gender)
        if _looks_like_guid(gender_id):
            resolved["productGenderId"] = gender_id
            changed = True
        else:
            gender_id = None
    if not _looks_like_guid(resolved.get("productTypeId")):
        type_url = (
            f"{PORTAL_ROOT}/api/app/size-chart/product-type-by-product-gender"
            f"?productGenderId={urllib.parse.quote(str(gender_id or product_gender or ''))}"
        )
        type_id = _resolve_name_to_id(session, type_url, product_type)
        if _looks_like_guid(type_id):
            resolved["productTypeId"] = type_id
            changed = True
    if not _looks_like_guid(resolved.get("seasonId")):
        season_id = _resolve_name_to_id(session, f"{PORTAL_ROOT}/api/app/season", season)
        if _looks_like_guid(season_id):
            resolved["seasonId"] = season_id
            changed = True
    if changed:
        add("resolved-ids", resolved)

    pinned = (os.getenv("GAP_PAYLOAD_VARIANT") or "").strip().lower()
    if pinned:
        for name, candidate in variants:
            if name == pinned:
                return [(name, candidate)]
        logger.warning(
            "GAP_PAYLOAD_VARIANT=%r matches no payload variant (%s) - trying all of them",
            pinned, ", ".join(name for name, _ in variants),
        )
    return variants


def to_form_urlencoded(cart_items, token):
    data = {
        "confirmDto[userShipBillId]": "3a0ec547-277d-bfa1-b124-e5a138679563",
        "confirmDto[printShopId]": "3a0cc21c-7ffe-1270-cb6a-7649de5278d0",
        "confirmDto[deliveryMethod]": "0",
        "confirmDto[courier]": "no",
        "confirmDto[account]": "",
        "confirmDto[specialRequest]": "",
        "confirmDto[customerPo]": "",
        "confirmDto[vendorPo]": "",
    }
    
    for i, item in enumerate(cart_items):
        prefix = f"confirmDto[cartItemDtos][{i}]"
        
        for k, v in item.items():
            if k not in ["cartCatalogItemDto", "cartPoItemDto", "cartGapPoItemDto", "cartOldNavyPoItemDto"]:
                # Nested objects/lists must not be stringified: str([...]) produces
                # a Python repr such as "[{'Id': ...}]" which the MVC binder rejects.
                if isinstance(v, (dict, list)):
                    continue
                if v is not None and v != "":
                    data[f"{prefix}[{k}]"] = str(v)
                
        if "cartGapPoItemDto" in item and item["cartGapPoItemDto"]:
            gap_dto = item["cartGapPoItemDto"]
            for k, v in gap_dto.items():
                if isinstance(v, dict) or isinstance(v, list):
                    pass
                else:
                    if v is not None and v != "":
                        data[f"{prefix}[cartGapPoItemDto][{k}]"] = str(v)
                    
        if "cartOldNavyPoItemDto" in item and item["cartOldNavyPoItemDto"]:
            on_dto = item["cartOldNavyPoItemDto"]
            for k, v in on_dto.items():
                if isinstance(v, dict) or isinstance(v, list):
                    pass
                else:
                    if v is not None and v != "":
                        data[f"{prefix}[cartOldNavyPoItemDto][{k}]"] = str(v)
                    
    data["__RequestVerificationToken"] = token
    return urllib.parse.urlencode(data)

def calculate_lpn_quantity(base_qty: int, order_type: str, brand: str = "") -> int:
    """Calculate final total ordered LPN quantity according to slab rules.

    - Old Navy (US/CA 3-up print layout): the quantity must always be an odd number.
    - Other brands (e.g. GAP): the quantity must always be an even number.
    """
    if base_qty <= 0:
        return 0

    additional_lpn = 0
    if order_type == "MAS":
        if 1 <= base_qty <= 100:
            additional_lpn = 12
        elif 100 < base_qty <= 500:
            additional_lpn = 18
        elif 500 < base_qty <= 1000:
            additional_lpn = 20
        elif 1000 < base_qty <= 5000:
            additional_lpn = 24
        elif base_qty > 5000:
            additional_lpn = 30
    else:
        additional_lpn = 10

    total_qty = base_qty + additional_lpn

    is_old_navy = bool(brand and "old" in brand.lower() and "navy" in brand.lower())
    if is_old_navy:
        # ONPT Reference Stickers - US/CA (3-Up): must be an odd number
        if total_qty % 2 == 0:
            total_qty += 1
    else:
        # Other brands: must be an even number
        if total_qty % 2 != 0:
            total_qty += 1

    return total_qty


def fetch_single_po(session, po_num: str, req_brand: str, product_gender: str, product_type: str, franchise: str, vendor_po: str, season: str, price_tickets: str, price_ticket_code: str, order_lpn_labels: str, order_type: str, token: str, dry_run: bool = False, trace: Dict[str, Any] = None):
    """Look a PO up and add it to the shopping cart.

    dry_run/trace exist for diagnose.py: they build the exact same payload but
    skip the POST, so a rejected request can be inspected field by field.
    """
    po_num = po_num.strip()
    if not po_num:
        return None, False, "Empty PO number"
    
    search_url = f"https://gapbrands.r-pac.com.hk/api/app/gap-po-item-head?poNo={po_num}&Sorting=CreationTime%20desc&SkipCount=0&MaxResultCount=1"
        
    try:
        search_res = session.get(search_url)
        if not search_res.ok:
            return po_num, False, f"Search failed: HTTP {search_res.status_code}{_no_internet_hint(search_res)}"
        
        items = search_res.json().get('items', [])
        if not items:
            return po_num, False, "PO not found"
            
        item = items[0]
        gapPoItemHeadId = item['id']
        
        detail_url = f"https://gapbrands.r-pac.com.hk/api/app/gap-po-item?itemHeadId={gapPoItemHeadId}"
        detail_res = session.get(detail_url)
        if not detail_res.ok:
            return po_num, False, f"Details failed: HTTP {detail_res.status_code}{_no_internet_hint(detail_res)}"
            
        detail_items = detail_res.json().get("items", [])
        if not detail_items:
            return po_num, False, "No detail items found"
            
        details = []
        qty_dict = {}
        for i, d_item in enumerate(detail_items):
            raw_qty = d_item.get("quantity")
            try:
                base_qty = int(float(raw_qty))
            except (TypeError, ValueError):
                return po_num, False, f"Invalid quantity {raw_qty!r} on item {d_item.get('id')}"
            det = {
                "Id": d_item["id"],
                "quantity": str(base_qty),
                "poNo": po_num
            }
            if order_lpn_labels == "YES":
                lpn_qty = str(calculate_lpn_quantity(base_qty, order_type, req_brand))
                det["lpnQuantity"] = lpn_qty
                det["quantity"] = lpn_qty
                qty_dict[f"lpnQty_{d_item['id']}"] = lpn_qty
                qty_dict[f"qty_{d_item['id']}"] = lpn_qty

            details.append(det)
            if order_lpn_labels != "YES":
                qty_dict[f"qty_{d_item['id']}"] = str(base_qty)
            

            
        if price_tickets == "None" or not price_tickets:
            json_data_str = "[]"
            item_codes = ""
            item_types = ""
            t_type = ""
        else:
            json_data_str = f'[{{"itemCode":"{price_ticket_code}","id":"{price_tickets}","itemType":"PriceTicket","reprint":"False"}}]'
            item_codes = price_ticket_code
            item_types = "PriceTicket"
            if req_brand == "Old Navy":
                t_type = "ONPT"
            else:
                t_type = price_ticket_code

        payload = {
            "type": "0",
            "style": detail_items[0].get("style", ""),
            "brand": "OldNavy" if req_brand == "Old Navy" else "Gap",
            "ticketTypes": t_type,
            "itemHeadIds": gapPoItemHeadId,
            "poNos": "",
            "messagePoNos": po_num,
            "productGenderId": product_gender,
            "productTypeId": product_type,
            "vendorPo": vendor_po if vendor_po else "no",
            "overage": "0",
            "otherOverage": "",
            "needLpnStickes": "1" if order_lpn_labels == "YES" else "0",
            "lpnOverage": "0",
            "otherLpnOverage": "",
            "priceTicketId": price_tickets if price_tickets != "None" else "",
            "coating": "0",
            "needAdditionalItems": "0",
            "matchBookId": "",
            "mainHangtagId": "",
            "secondaryHangtagId": "",
            "mainLabelId": "",
            "sizeLabelId": "",
            "packagingId": "",
            "traceabilityItemsId": "",
            "fitDisplay": "",
            "fabricationDisplay": "",
            "productNameText": "",
            "productNameId": "",
            "pieceSet": "NONE",
            "cooId": "d2c28254-45b4-4b55-8677-6c82668f7a46" if req_brand != "Old Navy" else "",
            "seasonId": "",
            "seasonValue": season,
            "sustainableSymbols": "",
            "styleNumber": "",
            "colorCode": "",
            "manufacturingDate": "",
            "productType": "",
            "styleText": "",
            "qrCode": "",
            "attribute1Id": "",
            "winkCopy": "",
            "attribute2Id": "",
            "attribute3Id": "",
            "bottomAttributesInseamId": "",
            "bottomAttributesRiseId": "",
            "bottomAttributesLengthId": "",
            "qualityAttributesId": "",
            "gapAttributes1Id": "",
            "gapAttributes2Id": "",
            "gapAttributes3Id": "",
            "gapAttributes4Id": "",
            "gapAttributes5Id": "",
            "gapAttributes6Id": "",
            "gapAttributes7Id": "",
            "gapAttributes8Id": "",
            "crockinInfo": "0",
            "crockinRecyclePercent": "0",
            "crockinRecyclePercentValue": "",
            "kcCode": "",
            "copyrightYearId": "",
            "antiMigrations": "0",
            "babyStripInId": "",
            "warmthLevel": "",
            "sleeveLengthId": "",
            "importFabricOrYarn": "0",
            "baseItem": {"compositions_Percentages": "0"},
            "details": details,
            "ItemCodes": item_codes,
            "ItemTypes": item_types,
            "LpnItemCodes": "",
            "LpnItemTypes": "",
            "TicketTypes": t_type,
            "hasReprint": "0",
            "marketCode": "1",
            "fabricationId": "",
            "fitId": "",
            "gapGarmentPartId": "",
            "fiberId": "",
            "exclusiveCalloutId": "",
            "babyStripIn": "",
            "SleeveLengthId": "",
            "jsonData": json_data_str,
            "careLabelData": "",
            "__RequestVerificationToken": token
        }
        
        if franchise != "None":
            payload["franchisesId"] = franchise
            
        payload.update(qty_dict)

        to_job_url = f"{PORTAL_ROOT}/api/app/gap-po-item/to-job?type=0"
        variants = _payload_variants(payload, item, session, product_gender, product_type, season)

        if trace is not None:
            trace["head_item"] = item
            trace["detail_items"] = detail_items
            trace["payload"] = payload
            trace["token_present"] = bool(token)
            trace["url"] = to_job_url
            trace["variants"] = [{"name": name, "payload": body} for name, body in variants]
        if dry_run:
            return po_num, True, None

        logger.info("to-job payload variants for %s: %s", po_num, ", ".join(name for name, _ in variants))

        attempts = []
        accepted_name = None
        for variant_name, variant_payload in variants:
            to_job_res = session.post(to_job_url, json=variant_payload, headers={"X-XSRF-TOKEN": token})

            if trace is not None:
                trace.setdefault("attempts", []).append({
                    "variant": variant_name,
                    "status_code": to_job_res.status_code,
                    "response": _body_snippet(to_job_res, limit=4000),
                })

            if to_job_res.status_code in (401, 403):
                # Session died between the cart read and this POST (or the account was
                # logged out elsewhere). process_job re-logs in and retries once.
                logger.warning("to-job for %s answered HTTP %s - session expired", po_num, to_job_res.status_code)
                return po_num, False, SESSION_EXPIRED

            if not to_job_res.ok:
                detail = f"HTTP {to_job_res.status_code}{_no_internet_hint(to_job_res)} -> {_body_snippet(to_job_res)}"
                logger.warning("to-job variant %r for %s: %s", variant_name, po_num, detail)
                attempts.append((variant_name, f"HTTP {to_job_res.status_code}", detail))
                continue

            try:
                res_data = to_job_res.json()
            except ValueError:
                snippet = _body_snippet(to_job_res)
                attempts.append((variant_name, "non-JSON response", snippet))
                continue

            if res_data.get("status") is True:
                accepted_name = variant_name
                break

            message = res_data.get("message") or res_data.get("error") or _body_snippet(to_job_res)
            # Keep the whole request next to the rejection: "Failed to add to shopping
            # cart" is the portal's catch-all, the payload is what identifies the cause.
            logger.warning(
                "to-job rejected PO %s with variant %r: %s | payload=%s",
                po_num, variant_name, json.dumps(res_data, default=str),
                json.dumps(variant_payload, default=str)[:6000],
            )
            attempts.append((variant_name, message, json.dumps(res_data, default=str)[:400]))

        if accepted_name:
            if accepted_name != variants[0][0]:
                logger.warning(
                    "to-job accepted %s only with payload variant %r - add "
                    "GAP_PAYLOAD_VARIANT=%s to .env to skip the retries",
                    po_num, accepted_name, accepted_name,
                )
            elif len(variants) > 1:
                logger.info("to-job accepted %s with the usual payload", po_num)
            if trace is not None:
                trace["accepted_variant"] = accepted_name
            return po_num, True, None

        tried = "; ".join(f"{name}: {msg}" for name, msg, _ in attempts) or "no response"
        last_message = attempts[-1][1] if attempts else "unknown"
        return po_num, False, (
            f"add-to-cart rejected: {last_message} "
            f"[brand={payload['brand']}, style={payload['style'] or '<empty>'}, "
            f"gender={product_gender or '<empty>'}, type={product_type or '<empty>'}, "
            f"season={season or '<empty>'}, lpn={order_lpn_labels or 'NO'}, "
            f"priceTicket={payload['priceTicketId'] or '<none>'}, "
            f"details={len(details)}, itemHeadId={gapPoItemHeadId}, "
            f"token={'MISSING' if not token else 'ok'}, "
            f"variants tried: {len(attempts)} ({tried[:300]})] - "
            f"run `python diagnose.py {po_num}` for the full request/response"
        )
            
    except Exception as e:
        logger.error("PO %s crashed: %s\n%s", po_num, e, traceback.format_exc())
        return po_num, False, f"{type(e).__name__}: {e}"

def download_single_job(session, job_id: str, brand: str = "GAP"):
    try:
        url = f"https://gapbrands.r-pac.com.hk/api/app/gap-po-order-detail/{job_id}/export-order-to-file"
            
        res = session.post(url)
        if not res.ok:
            return job_id, False, None, None, f"Export API error: HTTP {res.status_code}{_no_internet_hint(res)}"
        
        content = res.content
        filename = f"Order_{job_id}.xlsx"
        
        try:
            json_resp = res.json()
            if "fileName" in json_resp and json_resp["fileName"]:
                filename = json_resp["fileName"]
            
            if "fileToken" in json_resp:
                token = json_resp["fileToken"]
                ftype = json_resp.get("fileType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
                fname = json_resp.get("fileName", filename)
                
                # Use the correct ASP.NET Zero file download endpoint
                import urllib.parse
                dl_url = f"https://gapbrands.r-pac.com.hk/File/DownloadTempFile?fileType={urllib.parse.quote(ftype)}&fileToken={urllib.parse.quote(token)}&fileName={urllib.parse.quote(fname)}"
                
                dl_res = session.get(dl_url)
                if dl_res.ok:
                    content = dl_res.content
                    cd = dl_res.headers.get("Content-Disposition")
                    if cd and "filename=" in cd:
                        fname_match = re.search(r'filename="?([^";]+)"?', cd)
                        if fname_match:
                            filename = fname_match.group(1).strip()
        except Exception:
            pass
                
        if len(content) > 100:
            return job_id, True, filename, content, None
        else:
            return job_id, False, None, None, "File content too short or invalid"
    except Exception as e:
        return job_id, False, None, None, str(e)

class PortalAPI:
    def __init__(self):
        self.session = None

    def authenticate(self):
        # verify=True performs one authenticated probe so a silent login failure
        # is reported here instead of surfacing later as 401 on every PO.
        self.session = TimedSession(get_authenticated_session(verify=True))
        return self.session is not None

    @staticmethod
    def cart_token(session) -> str:
        """Read the anti-forgery token the portal expects on every write call.

        Without it bulk-delete and to-job are rejected. Returns "" when the page
        is not the cart (i.e. the session expired and we got the login page).
        """
        try:
            html_res = session.get(CART_PAGE_URL)
        except Exception as exc:
            logger.warning("Could not load %s: %s", CART_PAGE_URL, exc)
            return ""
        if not html_res.ok:
            return ""
        match = re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', html_res.text)
        return match.group(1) if match else ""

    def clear_cart(self, session):
        """Remove every cart item and verify the cart is really empty.

        Returns (cleared, remaining_items, note). The old code deleted the cart
        inside a bare `except: pass` and never looked at the result, so a failed
        delete left the previous PO in place and the next to-job answered the
        portal catch-all "Failed to add to shopping cart, please try again later!".
        """
        try:
            cart_res = session.get(CART_URL)
            if not cart_res.ok:
                return False, [], f"could not read the cart (HTTP {cart_res.status_code})"
            items = cart_res.json().get("items", [])
        except Exception as exc:
            return False, [], f"could not read the cart ({type(exc).__name__}: {exc})"
        if not items:
            return True, [], "cart was already empty"

        token = self.cart_token(session)
        if not token:
            return False, items, ("no __RequestVerificationToken on /Main/ShoppingCart "
                                  "(the portal session is not authenticated)")
        payload = [{"cartId": it.get("cartId"), "type": it.get("type", 0)} for it in items]
        try:
            res = session.post(
                f"{CART_URL}/bulk-delete",
                json=payload,
                headers={"X-XSRF-TOKEN": token, "x-requested-with": "XMLHttpRequest"},
            )
        except Exception as exc:
            return False, items, f"bulk-delete crashed ({type(exc).__name__}: {exc})"
        if not res.ok:
            return False, items, f"bulk-delete answered HTTP {res.status_code} -> {_body_snippet(res)}"

        try:
            remaining = session.get(CART_URL).json().get("items", [])
        except Exception:
            remaining = []
        if remaining:
            return False, remaining, f"{len(remaining)} item(s) are still in the cart after bulk-delete"
        return True, [], f"cart cleared ({len(items)} item(s) removed)"

    @staticmethod
    def cart_po_numbers(cart_items) -> List[str]:
        """PO numbers referenced by cart items (used to skip already-present POs)."""
        po_numbers = []
        for ci in cart_items:
            for key in ("messagePoNos", "poNos"):
                raw = ci.get(key)
                if raw:
                    for part in re.split(r"[,;/\s]+", str(raw)):
                        if part:
                            po_numbers.append(part)
            for dto_key in ("cartGapPoItemDto", "cartOldNavyPoItemDto", "cartPoItemDto"):
                dto = ci.get(dto_key)
                if isinstance(dto, dict):
                    for key in ("poNo", "messagePoNos"):
                        if dto.get(key):
                            po_numbers.append(str(dto[key]).strip())
        return sorted({p for p in po_numbers if p})

    def process_job(self, po_list: List[str], req=None) -> Generator[str, None, Tuple[bytes, List[str], List[str]]]:
        successful_pos = []
        missing_pos = []
        file_result = None

        try:
            if not self.session:
                yield "Authenticating..."
                if not self.authenticate():
                    yield "ERROR: Authentication failed."
                    return None, [], po_list
                yield "Authentication successful."
                
            stuck_pos = set()
            cleared, remaining, clear_note = self.clear_cart(self.session)
            if cleared:
                yield f"Shopping cart ready - {clear_note}."
            else:
                stuck_pos = set(self.cart_po_numbers(remaining))
                yield f"WARNING: could not clear the shopping cart - {clear_note}"
                if stuck_pos:
                    yield ("WARNING: still in the cart: " + ", ".join(sorted(stuck_pos))
                           + " - the portal refuses a second add for these (\"Failed to add "
                             "to shopping cart\"), so they are skipped. Remove them in the "
                             "portal and run again.")

            token = self.cart_token(self.session)
            if not token:
                yield "Anti-forgery token missing - logging in again..."
                if self.authenticate():
                    token = self.cart_token(self.session)
            if not token:
                yield ("ERROR: /Main/ShoppingCart returned no __RequestVerificationToken, so "
                       "every add-to-cart would be rejected. Check the account, then run "
                       "`python diagnose.py <PO>` to see the login result.")
                return None, [], po_list
            
            brand = getattr(req, 'brand', "Old Navy")
            product_gender = getattr(req, 'product_gender', "")
            product_type = getattr(req, 'product_type', "")
            franchise = getattr(req, 'franchise', "None")
            vendor_po = getattr(req, 'vendor_po', "")
            season = getattr(req, 'season', "")
            price_tickets = getattr(req, 'price_tickets', "None")
            price_ticket_code = getattr(req, 'price_ticket_code', "None")
            order_lpn_labels = getattr(req, 'order_lpn_labels', "NO")
            order_type = getattr(req, 'order_type', "MAS")

            clean_po_list = []
            for p in po_list:
                p = p.strip()
                if p and p not in clean_po_list:
                    clean_po_list.append(p)
            if not clean_po_list:
                yield "ERROR: No valid PO numbers provided."
                return None, [], []

            # A PO that is still in the cart (delete failed) can never be added a
            # second time, so report it instead of firing a doomed to-job.
            if stuck_pos:
                still_ok = []
                for p in clean_po_list:
                    if p in stuck_pos:
                        missing_pos.append(p)
                        yield f"WARNING: skipping {p} - it is already in the portal cart"
                    else:
                        still_ok.append(p)
                clean_po_list = still_ok
                if not clean_po_list:
                    yield ("ERROR: No POs successfully added to cart. Every PO is already in "
                           "the portal cart - remove it on the portal and run again.")
                    return None, successful_pos, missing_pos

            yield f"Searching {len(clean_po_list)} POs in parallel..."

            # Snapshot cart IDs before adding to identify our new items later
            try:
                pre_cart_res = self.session.get(CART_URL)
                pre_cart_ids = set(item["cartId"] for item in pre_cart_res.json().get("items", [])) if pre_cart_res.ok else set()
            except Exception:
                pre_cart_ids = set()

            def add_pos(pos_to_add, tok):
                """Add POs in parallel; returns {po: (po, success, error_msg)}."""
                results = {}
                with ThreadPoolExecutor(max_workers=5) as executor:
                    futures = {
                        executor.submit(
                            fetch_single_po,
                            self.session,
                            po,
                            brand,
                            product_gender,
                            product_type,
                            franchise,
                            vendor_po,
                            season,
                            price_tickets,
                            price_ticket_code,
                            order_lpn_labels,
                            order_type,
                            tok
                        ): po for po in pos_to_add
                    }
                    for future in as_completed(futures):
                        po = futures[future]
                        try:
                            results[po] = future.result()
                        except Exception as exc:
                            results[po] = (po, False, f"{type(exc).__name__}: {exc}")
                return results

            failure_details = []
            expired_pos = []
            for po_num, success, error_msg in add_pos(clean_po_list, token).values():
                if success:
                    yield f"{po_num} job add to cart"
                    successful_pos.append(po_num)
                elif error_msg == SESSION_EXPIRED:
                    expired_pos.append(po_num)
                    yield f"WARNING: portal session expired while adding {po_num} - retrying after login"
                else:
                    yield f"WARNING: Failed to process PO {po_num} - {error_msg}"
                    failure_details.append(f"{po_num}: {error_msg}")
                    missing_pos.append(po_num)

            if expired_pos:
                if self.authenticate():
                    token = self.cart_token(self.session) or token
                    yield f"Retrying {len(expired_pos)} PO(s) with a fresh session..."
                    for po_num, success, error_msg in add_pos(expired_pos, token).values():
                        if success:
                            yield f"{po_num} job add to cart"
                            successful_pos.append(po_num)
                        else:
                            yield f"WARNING: Failed to process PO {po_num} - {error_msg}"
                            failure_details.append(f"{po_num}: {error_msg}")
                            missing_pos.append(po_num)
                else:
                    yield "ERROR: could not log in again after the session expired."
                    missing_pos.extend(expired_pos)

            if not successful_pos:
                details = "; ".join(failure_details) if failure_details else "no detail returned"
                yield f"ERROR: No POs successfully added to cart. Details: {details}"
                return None, successful_pos, missing_pos

            cart_res = self.session.get("https://gapbrands.r-pac.com.hk/api/app/shopping-cart")
            if not cart_res.ok:
                yield f"ERROR: Failed to retrieve cart: {cart_res.text}"
                return None, successful_pos, missing_pos
                
            all_cart_items = cart_res.json().get("items", [])
            # Only use the items we just added (not stale items from previous runs)
            cart_items = [ci for ci in all_cart_items if ci.get("cartId") not in pre_cart_ids]
            if not cart_items:
                # Fallback: use all items if tracking failed
                cart_items = all_cart_items
            if not cart_items:
                yield "ERROR: Shopping cart is empty despite successful additions."
                return None, successful_pos, missing_pos

            # Patch cart items: inject cooId for GAP brand (required by server validation)
            COO_MADE_IN_SRI_LANKA = "d2c28254-45b4-4b55-8677-6c82668f7a46"
            for ci in cart_items:
                gap_dto = ci.get("cartGapPoItemDto")
                if gap_dto is not None:
                    ci_brand = gap_dto.get("brand", "")
                    if ci_brand and ci_brand.lower() not in ("oldnavy", "old navy"):
                        if not gap_dto.get("cooId"):
                            gap_dto["cooId"] = COO_MADE_IN_SRI_LANKA

            # Enforce LPN slab quantities on the cart items before checkout
            if order_lpn_labels == "YES":
                missing_original = 0
                for ci in cart_items:
                    for dto_key in ["cartGapPoItemDto", "cartOldNavyPoItemDto"]:
                        dto = ci.get(dto_key)
                        if dto is None:
                            continue
                        try:
                            # originalQuantity keeps the PO quantity, so a re-run of this
                            # patch cannot add the slab twice. Without it the cart value is
                            # used, which *may* already contain the slab — hence the
                            # warning below instead of a silent double increment.
                            original = dto.get("originalQuantity")
                            base_qty = int(float(original or dto.get("quantity") or 0))
                            if not original:
                                missing_original += 1
                            if base_qty == 0:
                                base_qty = int(float(ci.get("quantity") or 0))
                            item_brand = dto.get("brand") or brand
                            if base_qty > 0:
                                new_qty = str(calculate_lpn_quantity(base_qty, order_type, item_brand))
                                dto["lpnQuantity"] = new_qty
                                dto["quantity"] = new_qty
                                # The top-level cart item carries the quantity too — both
                                # must agree or the checkout posts the un-slabbed value.
                                ci["quantity"] = new_qty
                                ci["lpnQuantity"] = new_qty
                        except (ValueError, TypeError):
                            yield f"WARNING: Could not apply LPN slab to cart item {ci.get('cartId', '?')}"
                if missing_original:
                    yield (f"WARNING: {missing_original} cart item(s) returned no originalQuantity — "
                           f"the slab was calculated from the cart quantity itself; check the "
                           f"ordered qty on the portal before printing")

            # Confirm 1st
            form_data = to_form_urlencoded(cart_items, token)
            headers = {
                "content-type": "application/x-www-form-urlencoded; charset=UTF-8",
                "x-requested-with": "XMLHttpRequest"
            }
            conf1_url = "https://gapbrands.r-pac.com.hk/Main/ShoppingCart/Confirm"
            conf1_res = self.session.post(conf1_url, data=form_data, headers=headers)
            if not conf1_res.ok:
                yield f"WARNING: Confirm 1st failed with status {conf1_res.status_code}"

            # Confirm 2nd
            checkout_payload = {
                "confirmDto": {
                    "userShipBillId": "3a0ec547-277d-bfa1-b124-e5a138679563",
                    "printShopId": "3a0cc21c-7ffe-1270-cb6a-7649de5278d0",
                    "deliveryMethod": "0",
                    "courier": "no",
                    "account": "",
                    "specialRequest": "",
                    "customerPo": "",
                    "vendorPo": "",
                    "cartItemDtos": cart_items
                },
                "__RequestVerificationToken": token
            }
            
            # Get latest global job ID BEFORE checkout to prevent overlap with older test jobs
            global_latest_id = 0
            try:
                import time
                hist_res = self.session.get(f"https://gapbrands.r-pac.com.hk/api/app/order?orderType=All&sorting=id%20desc&skipCount=0&maxResultCount=1&_t={int(time.time() * 1000)}")
                if hist_res.ok:
                    items = hist_res.json().get("items", [])
                    if items:
                        global_latest_id = items[0].get("id", 0)
            except Exception:
                pass
                
            # Use order/common for all brands (as confirmed from browser network capture)
            checkout_url = "https://gapbrands.r-pac.com.hk/api/app/order/common"
            checkout_res = self.session.post(
                checkout_url,
                json=checkout_payload,
                headers={
                    "X-XSRF-TOKEN": token,
                    "x-requested-with": "XMLHttpRequest"
                }
            )
            if not checkout_res.ok:
                yield f"ERROR: Checkout failed: {checkout_res.text}"
                return None, successful_pos, missing_pos

            yield "job created"
            job_ids = []

            yield "Checking global history fallback to locate new Job IDs..."
            from datetime import datetime, timedelta, timezone
            # Allow a 5-minute window for the server time difference
            checkout_start_time = datetime.now(timezone.utc) - timedelta(minutes=5)
            
            import urllib.parse
            for po in successful_pos:
                import time
                history_url = f"https://gapbrands.r-pac.com.hk/api/app/order?customerPo={urllib.parse.quote(po)}&orderType=All&sorting=id%20desc&skipCount=0&maxResultCount=10&_t={int(time.time() * 1000)}"
                hist_res = self.session.get(history_url)
                if hist_res.ok:
                    items = hist_res.json().get("items", [])
                    for item in items:
                        # STRICT MATCHING: Check exact PO No
                        item_po = item.get("poNo") or item.get("customerPo") or item.get("vendorPo") or ""
                        if item_po != po:
                            continue
                            
                        # STRICT MATCHING: Check exact Vendor PO
                        if vendor_po and item.get("vendorPo") != vendor_po:
                            continue
                            
                        j_id = str(item.get("id"))
                        ctime_str = item.get("creationTime")
                        if ctime_str:
                            try:
                                # Parse server time (e.g., '2026-08-06T04:05:12.347827Z')
                                ctime = datetime.fromisoformat(ctime_str.replace("Z", "+00:00"))
                                is_new = False
                                if global_latest_id > 0:
                                    is_new = int(j_id) > global_latest_id
                                else:
                                    is_new = ctime > checkout_start_time
                                    
                                if is_new:
                                    if j_id and j_id not in job_ids and j_id != "None":
                                        job_ids.append(j_id)
                            except Exception:
                                # Fallback if parsing fails, just take the first one
                                if j_id and j_id not in job_ids and j_id != "None":
                                    job_ids.append(j_id)
                                    break
            
            if not job_ids:
                yield "ERROR: No Job IDs found in checkout response or fallback."
                return None, successful_pos, missing_pos

            zip_buffer = io.BytesIO()
            valid_files = 0

            with zipfile.ZipFile(zip_buffer, "w", zipfile.ZIP_DEFLATED) as zip_file:
                with ThreadPoolExecutor(max_workers=5) as executor:
                    futures = {executor.submit(download_single_job, self.session, jid, brand): jid for jid in job_ids}

                    for future in as_completed(futures):
                        jid = futures[future]
                        try:
                            job_id, success, filename, content, error_msg = future.result()
                            if success:
                                zip_file.writestr(filename, content)
                                valid_files += 1
                                yield "job downloaded"
                            else:
                                yield f"WARNING: Failed download for Job {job_id} - {error_msg}"
                        except Exception as exc:
                            yield f"WARNING: Exception download for Job {jid} - {str(exc)}"

            if valid_files == 0:
                file_result = None
                yield "WARNING: Failed to download any order files."
            else:
                file_result = zip_buffer.getvalue()
                yield "zip ready"

        except Exception as e:
            logger.error("Job crashed: %s\n%s", e, traceback.format_exc())
            yield f"ERROR: Failed during process. {type(e).__name__}: {e}"

        yield "DONE"
        return file_result, successful_pos, missing_pos
