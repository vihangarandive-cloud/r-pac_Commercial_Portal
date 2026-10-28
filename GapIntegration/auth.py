import os
import threading
import time
from pathlib import Path

import requests
from bs4 import BeautifulSoup
from dotenv import load_dotenv

# .env must be resolved next to this file. load_dotenv() on its own only walks
# the process working directory, which under IIS/ANCM is not the application
# folder — the classic "GAP_PASSWORD environment variable is not set" error
# after moving the app to another server.
BASE_DIR = Path(__file__).resolve().parent
DOTENV_PATH = BASE_DIR / ".env"
load_dotenv(dotenv_path=DOTENV_PATH)
load_dotenv()  # fallback: process CWD / real environment variables

LOGIN_URL = "https://gapbrands.r-pac.com.hk/Account/Login"
CART_URL = "https://gapbrands.r-pac.com.hk/api/app/shopping-cart"
PORTAL_ROOT = "https://gapbrands.r-pac.com.hk"

# (connect, read) — without a timeout a stalled portal call blocks the worker
# forever and IIS answers 502.3 after its own proxy timeout.
TIMEOUT = (int(os.getenv("GAP_CONNECT_TIMEOUT", "10")), int(os.getenv("GAP_READ_TIMEOUT", "90")))

USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

_cache_lock = threading.Lock()
_cached_session = None
_cached_at = 0.0


def credentials_configured():
    """Return (username, password_is_set) so callers can fail fast."""
    username = os.getenv("GAP_USERNAME", "GAP0012")
    return username, bool(os.getenv("GAP_PASSWORD"))


def _login_error_message(html: str) -> str:
    soup = BeautifulSoup(html, "html.parser")
    for selector in ("div.validation-summary-errors", "div.alert-danger",
                     "span.field-validation-error", "div.toast-message"):
        node = soup.select_one(selector)
        if node:
            text = " ".join(node.get_text(" ", strip=True).split())
            if text:
                return text
    lowered = html.lower()
    if "recaptcha" in lowered or "captcha" in lowered:
        return "The portal now asks for a captcha."
    return "unknown reason (the login page was returned again)"


def get_authenticated_session(verify: bool = False) -> requests.Session:
    """Authenticate against the GAP Brands portal and return a configured session."""
    username, has_password = credentials_configured()

    if not has_password:
        raise ValueError(
            f"GAP_PASSWORD is not configured. Expected a .env file at {DOTENV_PATH} "
            f"(or a GAP_PASSWORD environment variable) with GAP_USERNAME / GAP_PASSWORD."
        )

    session = requests.Session()
    session.headers.update({"User-Agent": USER_AGENT})

    # Step 1: GET the login page to extract the anti-forgery token
    response = session.get(LOGIN_URL, timeout=TIMEOUT)
    response.raise_for_status()

    soup = BeautifulSoup(response.text, "html.parser")
    token_input = soup.find("input", {"name": "__RequestVerificationToken"})

    if not token_input:
        raise ValueError(
            "Could not find __RequestVerificationToken on the login page. "
            "The portal login page changed or an upstream proxy returned an error page."
        )
        
    token = token_input.get("value")
    
    # Step 2: POST the login form
    login_data = {
        "ReturnUrl": "",
        "ReturnUrlHash": "",
        "LoginInput.UserNameOrEmailAddress": username,
        "LoginInput.Password": os.getenv("GAP_PASSWORD"),
        "LoginInput.RememberMe": "true",
        "__RequestVerificationToken": token
    }
    
    headers = {
        "Content-Type": "application/x-www-form-urlencoded",
        "Origin": PORTAL_ROOT,
        "Referer": LOGIN_URL
    }
    
    post_response = session.post(LOGIN_URL, data=login_data, headers=headers, timeout=TIMEOUT)
    post_response.raise_for_status()

    # Step 3: decide success by where we ended up, not by sniffing for text.
    # A failed login redirects back to /Account/Login; a successful one does not.
    final_url = post_response.url or ""
    if "/Account/Login" in final_url:
        raise ValueError(
            f"Login failed for '{username}': {_login_error_message(post_response.text)} "
            "Check GAP_USERNAME / GAP_PASSWORD in .env."
        )
    if "/Account/SendSecurityCode" in final_url or "/Account/TwoFactor" in final_url:
        raise ValueError(
            f"Login for '{username}' requires an SMS/e-mail security code (2FA). "
            "Automation cannot pass this step — use an account without 2FA."
        )

    # Set default headers for all future API requests so they return 401 instead of 302 redirects
    session.headers.update({
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
    })

    if verify:
        probe = session.get(CART_URL, timeout=TIMEOUT)
        if probe.status_code == 401:
            raise ValueError(
                f"Login for '{username}' returned a page but the API still answers 401. "
                "The account may be locked, disabled, or the session cookie was rejected."
            )

    return session


def get_cached_session(max_age_seconds: int = 900) -> requests.Session:
    """Return a shared, still-valid session for the read-only proxy endpoints.

    The dropdown endpoints used to perform a full login on *every* request
    (4+ logins per page load). That is slow, and portals that enable lockout /
    rate limiting start rejecting it — which looks exactly like "the automation
    suddenly stopped working". Re-login only when the cached session expires or
    the portal answers 401.
    """
    global _cached_session, _cached_at
    with _cache_lock:
        if _cached_session is not None and (time.time() - _cached_at) < max_age_seconds:
            return _cached_session
        _cached_session = get_authenticated_session()
        _cached_at = time.time()
        return _cached_session


def invalidate_cached_session():
    global _cached_session, _cached_at
    with _cache_lock:
        _cached_session = None
        _cached_at = 0.0
