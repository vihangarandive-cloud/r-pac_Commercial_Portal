# GAP Brands automation — fixed copy (moved under SO-GEN)

Source: `A:\IT PROJECTS\R-PAC\Rpac GapBrands Integration`
(`vihangarandive-cloud/Rpac-GapBrands-Integration`, commit `8a22893` "Latest").

This folder is the same FastAPI service, de-duplicated and hardened so that it can be
hosted next to the RPAC Commercial (SO-GEN) MVC site without breaking either of them.
Nothing outside `GapIntegration/` was modified, and the SO-GEN application code is
untouched.

**Merged from the upstream `Latest` commit** (kept exactly as written, re-tested here):
the MAS / NON-MAS tabs and the LPN slab calculation
(`calculate_lpn_quantity`: +12/+18/+20/+24/+30 extra labels per quantity band for MAS,
a flat +10 for NON-MAS, forced odd for Old Navy 3-up, forced even for every other brand),
applied both to the PO detail lines and to the cart items right before `Confirm 1st`.
The only change to that logic is that a non-numeric `quantity` now fails that one PO with
a readable message instead of raising inside the worker thread.

---

## 1. Why it "worked before and now gives errors"

The automation itself is largely fine. What breaks when it is merged into SO-GEN is the
*environment*: working directory, URL prefix, inherited IIS configuration and the missing
`.env`. Ranked by how likely each one is to be the error you are seeing:

| # | Symptom | Root cause (original code) | Fixed by |
|---|---------|----------------------------|----------|
| 1 | Dropdowns stuck on `Loading...`, `[object Object]`, or the log says `ERROR: Authentication failed.` | `.env` is git-ignored, so the SO-GEN server has no `GAP_PASSWORD`. `auth.py` raises `ValueError` on every request. | `auth.py` resolves `.env` next to itself, `credentials_configured()`, `/api/health`, fail-fast message on start-job |
| 2 | HTTP 500 on `/` , `TemplateNotFound: index.html`, or `RuntimeError: Directory 'wwwroot/static' does not exist` | `Jinja2Templates("templates")` and `StaticFiles("wwwroot/static")` were **relative to the working directory**, which is not the app folder under IIS/ANCM or when started by a service/scheduler | `BASE_DIR = Path(__file__).parent` |
| 3 | Page loads, but **every** button fails: `Failed to fetch`, `Unexpected token '<'`, download 404 | `index.html` used absolute `/api/...` and `/static/...`. Under `https://so-gen/gap/` those URLs point at the **MVC site**, which answers with the login page (HTML), so `res.json()` throws | `APP_BASE` in the page + `GAP_BASE_PATH` middleware |
| 4 | `HTTP 404.0` / `500.0` only for `/gap/api/*`; `/gap/` is fine | SO-GEN `web.config` adds `ExtensionlessUrlHandler-Integrated-4.0` with `path="*."` and `runAllManagedModulesForAllRequests="true"`. A child app inherits it and MVC claims the extension-less API paths before the Python handler runs | `<handlers><clear />` in `Web.config` |
| 5 | `500.30` / `502.3` / app pool stops right after deployment | app pool must be **No Managed Code** (SO-GEN runs CLR v4), ASP.NET Core Module (Hosting Bundle) must be installed, plus `logs\python.log` | documented checklist in section 3 |
| 6 | Nothing changed after a code fix | `app.py` in the first commit contained **two complete copies** of the whole module: `app.mount`, `templates`, `jobs_store`, `JobRequest`, `run_job_sync`, `background_job_runner` and every route were defined twice. FastAPI keeps the **first** matching route, so edits in the second half never ran | removed upstream in `8a22893` (477 → 286 lines); this copy keeps the single copy |
| 7 | Request hangs, then `502.3 Bad Gateway` | no `timeout=` on any `requests` call — a stalled portal response blocks the worker forever | `TimedSession` + `GAP_CONNECT_TIMEOUT` / `GAP_READ_TIMEOUT` |
| 8 | Suddenly 401/lockout after a few page loads | `_fetch_from_gap()` performed a **full login for every dropdown request** (4+ logins per page load). Accounts with lockout / throttling start rejecting that | one cached session, re-login only on 401 |
| 9 | `Login failed! Please check your GAP_USERNAME and GAP_PASSWORD` even though the credentials are right | success was detected by searching the response HTML for `__RequestVerificationToken` **and** the word `Login`; any page containing a token looks like a failure, and a 2FA redirect looks like a success | success is decided by the final URL, with explicit 2FA detection |
| 10 | `Confirm 1st` / checkout rejected, or random PO failures | `to_form_urlencoded()` ran `str()` over nested `dict`/`list` values, sending Python reprs (`[{'Id': …}]`) to the MVC model binder | nested values are skipped |
| 11 | `ERROR: Job failed unexpectedly - ...` with no detail | `except Exception: pass`, `str(e)` without traceback, proxy errors returned as `{"error": ...}` which the UI ignored | logging + tracebacks (`logs\python.log`) + banner in the UI |
| 12 | Memory grows until the pool recycles | `jobs_store` never evicted finished jobs | 2-hour TTL purge |
| 13 | Build works locally, fails on a rebuilt server | `requirements.txt` had no versions — a fresh `pip install` picks the newest Starlette / sse-starlette | compatible ranges |
| 14 | Everything answered `500.19` (or `500.0`) right after deploying `8a22893` | that commit switched the handler to `modules="AspNetCoreModuleV2"`; the module only exists after the ASP.NET Core **Hosting Bundle 2.2+** is installed on the server | `hostingModel="OutOfProcess"` kept explicit + install checklist in section 3 |
| 15 | `ERROR: No POs successfully added to cart. Details: P2608760: … {"type":"e","status":false,"message":"Failed to add to shopping cart, please try again later!"}` | that sentence is the portal's catch-all, not a validation message. It is also what the portal answers when the PO is **already in the cart** — the old code emptied the cart inside `except: pass` and never looked at the result, so one failed `bulk-delete` made every later add fail — when `__RequestVerificationToken` is empty, and when the session expires mid-run | `clear_cart()` checks and verifies the delete (naming the POs that are stuck), `cart_token()` refuses to start with an empty token, a `401` during `to-job` re-logs in and retries once, and the rejection now echoes the payload fields (`brand`, `style`, `gender`, `type`, `season`, `lpn`, `details`) plus `diagnose.py <PO>` for the full request/response. A rejection is also **retried automatically** with the ids taken from the PO head and then with the ids resolved from the dropdown APIs (`GAP_PAYLOAD_VARIANT` pins whichever one the portal accepts) |
| 16 | The error text reads `Oops...` / `P2608760: API status=false \| {"type":"e","status":false,…}` | **This repository cannot print that.** `API status` and `Oops` occur in no blob of `19f586c` or `8a22893` and in no file of the current tree; this code reports the same rejection as `add-to-cart rejected: … [brand=…, variants tried: n (…)]`. The worker answering `/gap` is a build that exists only on that server (hand-copied folder, second site, scheduled task or console window) | `find-gap-copies.ps1` finds it (IIS paths, every `portal_api.py` sha, every file containing `API status`, running `python` processes) and `-Quarantine -Force` stops its pool, removes the IIS application and renames the folder, so the next deploy is provably the one answering |

### Verified against the live portal

`https://gapbrands.r-pac.com.hk` is reachable and unchanged, so the API surface is **not**
the problem: `/Account/Login` returns the expected form (`LoginInput.UserNameOrEmailAddress`,
`LoginInput.Password`, `__RequestVerificationToken`) and all eight endpoints used by the app
(`product-gender`, `season`, `size-chart/product-type-by-product-gender`,
`templates/market-code-list`, `shopping-cart`, `gap-po-item-head`, `gap-po-item`, `order`)
answer `401` — i.e. they exist and only need authentication.

### If it is still failing, check these in order

1. `GET https://so-gen/gap/api/health` → `gap_password_configured` must be `true`, and
   `portal_api_sha` must equal
   `python -c "import portal_api; print(portal_api.SOURCE_SHA)"` run **in the deployed folder**.
   A different hash (or no `portal_api_sha` field at all) means IIS is still running an older
   copy — no code change can show up until that is fixed (recycle the app pool, check the
   `Web.config` `processPath`, delete `__pycache__`). If the page's error text contains
   `API status` or `Oops`, it is not this code at all (row 16): run `.\find-gap-copies.ps1`
   instead of publishing again.
2. The same health response now contains a `runtime` block (also written to the log at startup):
   interpreter, `site-packages`, working directory, `.env` found/missing, `logs/` writable,
   package versions and any missing file. Compare it with the block printed by
   `python diagnose.py …` on the machine where it works — that is the difference between
   *library* and *code*.
3. ANCM log files: `logs\python_<pid>_<date>.log` (the name in `Web.config` is a prefix; IIS
   appends the pid and timestamp, so the literal file `python.log` usually does not exist).
   The app now logs the exception **and** the traceback there, plus the `runtime:` block.
4. `GET https://so-gen/gap/api/gap-brands/seasons` in a browser: JSON = good; the SO-GEN
   login page (HTML) = the request never reached Python (section 1 rows 3 and 4).
5. Browser console on the page → `GAP automation health: {...}` is printed at load, and any
   portal error is shown in a red banner instead of a silent `Loading...`.
6. Add-to-cart rejected? `python diagnose.py <PO> --post` (same folder, same `.env`) prints the
   exact JSON the app sends with the token redacted, the portal's verbatim answer, the cart
   before/after, and the ranked list of causes. `--clear-cart` empties the cart first, `--lpn NO`
   and `--brand GAP` isolate the LPN slab and the brand tab. Nothing else in this app produces
   `Failed to add to shopping cart, please try again later!` — the portal uses that one sentence
   for every rejection, so the request it prints is the only way to tell them apart.

### Works from the console, fails under IIS — the five real differences

The app talks to `gapbrands.r-pac.com.hk` over HTTPS; the web server it is hosted on is
irrelevant to the *portal's* answer. So when the identical folder works from a console and is
rejected under SO-GEN, only these can differ. Check them in this order — each one is a single
command:

| # | difference | how it shows up | confirm |
|---|---|---|---|
| 1 | **Another copy of the code is running** (old file, second `GapIntegration` folder, `__pycache__`, app pool not recycled). `GapIntegration` used to be **absent from `RpacCommercial.csproj`**, so a Web Deploy of SO-GEN silently left this folder untouched — the publish succeeded and nothing changed. It is now a `Content` item in the csproj, so a normal publish replaces it; a folder that predates that change (or a second copy elsewhere) still wins | every fix has "no effect" | `portal_api_sha` in `/api/health` vs `python -c "import portal_api; print(portal_api.SOURCE_SHA)"` in that folder |
| 2 | **Different `.env`** — other account, or no password at all | different `gap_username`; login/permission errors; `gap_password_configured:false` | compare `gap_username` in `/api/health` with the console; `diagnose.py` prints `.env` path + found/MISSING |
| 3 | **The GAP account is used from two places at once** (PC + server). The portal cart, the anti-forgery token and the "converted to job" state are **per account**, so a PO your PC already put in the cart is a duplicate for the server — and the portal answers exactly `Failed to add to shopping cart…` | works on the PC, fails on the server for the same PO until the cart is emptied; tokens rotate on each login | open the portal in a browser **as that user**, Shopping Cart → delete everything, then run from **one** machine only |
| 4 | **Interpreter / site-packages** (`.\venv\Scripts\python.exe` missing or built elsewhere; packages installed into a different Python) | IIS answers `500.30` / `502.3`, or the page loads but `/api/*` fails — **never** the portal's JSON | `runtime` block in `/api/health`; `logs\python_*.log` shows `ModuleNotFoundError` |
| 5 | **App-pool identity cannot write** (`logs/`, `__pycache__`) | startup traceback, empty log, or `500.30`; sometimes stale bytecode | `logs/` line of the `runtime` block; `icacls logs` |

If a *library* were missing or the wrong version, the request would never leave Python and the
browser would get `500`/`502` — the portal's `{"type":"e","status":false,…}` body is proof that
the login, the cookie, the token and the POST all worked and **the portal itself refused the
add**. That refusal is what rows 2, 3 and the `to-job` payload (section 4) explain.

A `venv` is **not** portable: `venv\Scripts\python.exe` points at the base interpreter it was
created from. If `venv` was copied from a laptop, recreate it on the server
(`python -m venv venv && .\venv\Scripts\pip install -r requirements.txt`) or point
`processPath` at the server's Python.

---

## 2. Running it standalone (unchanged behaviour)

```powershell
python -m venv venv
.\venv\Scripts\pip install -r requirements.txt
copy .env.example .env      # then put the real GAP_PASSWORD in it
.\venv\Scripts\python -m uvicorn app:app --host 127.0.0.1 --port 8000
```

Open `http://127.0.0.1:8000/` → `http://127.0.0.1:8000/api/health` must report
`"status":"ok"`.

---

## 3. Hosting it under SO-GEN (`https://so-gen/gap/`)

The folder is part of the SO-GEN repository and of the publish now:
`RpacCommercial.csproj` ships `GapIntegration\**` (minus `venv`, `__pycache__`, `logs`, `.env`),
and `Views\Shared\_Layout.cshtml` has a **GAP Brands** entry in the sidebar that opens
`~/gap/`. The IIS **alias** is `gap`; the **physical folder** stays `GapIntegration` — IIS does
not require them to match, and `GAP_BASE_PATH=gap` refers to the URL, not to the directory.

1. Publish SO-GEN as usual (Web Deploy / `msbuild /p:DeployOnBuild=true`). The tool lands in
   `<site>\GapIntegration\`. Never copy an old build over it by hand afterwards — that is what
   `portal_api_sha` exists to catch.
2. Register it in IIS (idempotent, creates the pool and the application, fixes `logs` ACLs):

   ```powershell
   .\install-gap-iis.ps1 -Site "SO-GEN" -Alias "gap"
   ```

   In IIS Manager the same thing is: SO-GEN site → right-click `GapIntegration` → **Convert to
   Application**, alias `gap`, pool *.NET CLR version = **No Managed Code***, Managed pipeline =
   Integrated, Enable 32-Bit Applications = False, Start Mode = AlwaysRunning.
3. Make sure the **ASP.NET Core Hosting Bundle** is installed (provides `AspNetCoreModuleV2`,
   which `Web.config` uses). On a server that only has the old v1 module IIS answers `500.19`
   ("AspNetCoreModuleV2" is not a native module); install the Hosting Bundle rather than editing
   the handler back to `AspNetCoreModule`.
4. Build the venv **on the server** (`python -m venv venv` + `pip install -r requirements.txt`
   inside `GapIntegration`), and add `GAP_BASE_PATH=gap` to `.env` (already set in `Web.config`).
5. `logs\` must exist and be writable by the app-pool identity (`IIS AppPool\<pool>`).
6. Browse `https://so-gen/gap/api/health` first — it answers without touching the portal.
7. If it returns `500.30`, read `logs\python_*.log`; if it returns `404`, the handler is still
   inherited (check that `Web.config` from this folder is deployed and that `<clear />` survived).

### Replacing the running copy (the step that is usually missed)

Two ways: run `deploy-gap.ps1` (below), or do the five steps by hand.

A normal SO-GEN publish already copies the folder now (`GapIntegration\**` is a `Content` item in
`RpacCommercial.csproj`) — what it still does **not** do is build the venv, restart the pool or
prove which file the worker imported. That is exactly what `deploy-gap.ps1` adds, so use it after
the publish (or instead of the manual robocopy below).

If the page still shows `API status=false` after a deploy, nothing below is the problem: that text
is not in this repository at all, so run `.\find-gap-copies.ps1` (next subsection) before spending
another publish on it.

`https://so-gen/gap/api/health` is the receipt. Do this every time, in this order:

```powershell
# 1. stop the worker so no file is locked and no stale module is cached
& $env:windir\system32\inetsrv\appcmd.exe stop apppool /apppool.name:"<gap pool>"

# 2. copy the folder (do not merge into an older tree by hand - a stray
#    portal_api.py or __pycache__ is exactly how "the fix did nothing")
robocopy C:\src\RpacCommercial\GapIntegration C:\inetpub\<site>\GapIntegration /MIR /XD __pycache__ venv .git /XF *.pyc

# 3. .env is not in the repo - it must survive the copy, check it exists
type C:\inetpub\<site>\GapIntegration\.env

# 4. same interpreter the Web.config names, so the packages are the ones used
C:\inetpub\<site>\GapIntegration\venv\Scripts\python.exe -m pip install -r C:\inetpub\<site>\GapIntegration\requirements.txt
C:\inetpub\<site>\GapIntegration\venv\Scripts\python.exe -c "import portal_api, envcheck; print(portal_api.SOURCE_SHA); print(envcheck.format_runtime())"

# 5. start, then compare the two numbers
& $env:windir\system32\inetsrv\appcmd.exe start apppool /apppool.name:"<gap pool>"
curl.exe -s https://so-gen/gap/api/health
```

`portal_api.SOURCE_SHA` printed in step 4 must equal `portal_api_sha` in step 5. If it does, the
code question is closed and the remaining cause is the portal state (cart / account / payload —
section 4). Run `python diagnose.py <PO> --post --dump-head` **on the server**, in that folder,
with that interpreter, and send the output.

#### Or let the script do it and refuse to lie about success

```powershell
.\deploy-gap.ps1 -Target C:\inetpub\<site>\GapIntegration -AppPool "<gap pool>" -HealthUrl https://so-gen/gap/api/health
```

It stops the pool, mirrors the folder (`/MIR`, skipping `venv`, `__pycache__`, `logs`, `.env`),
installs `requirements.txt` into the venv **in the target folder**, starts the pool, then polls
`/api/health` until the worker reports the sha of the file it just copied. Exit code `0` means the
running worker is running this code; `1` means another copy is being started (it prints
`runtime.app_dir` so you can see which one); `2` means a pre-check failed (missing file, import
error, failed `pip`). Nothing is reported as deployed on the strength of a `robocopy` message.

### Finding the copy that is actually running

The failure text identifies its own author. **No file in this repository prints `API status`** — not
`19f586c`, not `8a22893`, not the current tree (searched: all 96 blobs plus `*.py`, `*.pyc`,
`*.cshtml`, `*.js`). This code reports the same rejection as

```
add-to-cart rejected: Failed to add to shopping cart, please try again later!
[brand=Gap, style=…, gender=…, type=…, season=…, lpn=NO, priceTicket=<none>, details=…,
 itemHeadId=…, token=ok, variants tried: 3 (as-sent: …; po-head: …; resolved-ids: …)]
- run `python diagnose.py P2608760` for the full request/response
```

So while `P2608760: API status=false | {…}` is on screen, the browser is talking to a different
build and no edit to this folder can change the answer. Locate it first:

```powershell
cd C:\inetpub\<site>\GapIntegration
.\find-gap-copies.ps1                       # read-only report
.\find-gap-copies.ps1 -Quarantine -Force    # retire the copy that is answering /gap
```

The report lists every IIS application with its physical path, what `<HealthUrl>` (default
`http://localhost/gap/api/health`) actually answers, every `portal_api.py` with its sha1 (the same
value `/api/health` calls `portal_api_sha`) and last write time, every `.py`/`.pyc` that contains
`API status`, and every running `python` process with its command line — so a second site, a
scheduled task or a console window left running cannot hide.

`-Quarantine` destroys nothing: it stops that application's pool, removes the IIS application and
renames the folder to `<name>.stale-<timestamp>`. `/gap` then falls through to the SO-GEN MVC app
(404) until `install-gap-iis.ps1` + `deploy-gap.ps1` put this copy in place, so a stale worker can
no longer answer while you believe you are testing the new one. Exit codes: `0` clean, `1` a foreign
copy exists (or, with `-Quarantine` but without `-Force`, only the plan was printed), `2` pre-check
failed.

Doing it by hand, if you prefer:

```powershell
# every portal_api.py on the box, newest last
Get-ChildItem C:\inetpub -Recurse -Filter portal_api.py |
    Select-Object FullName, Length, LastWriteTime | Sort-Object LastWriteTime

# which file prints 'API status' (the text in your error), including .pyc-only folders
Get-ChildItem C:\inetpub -Recurse -Include *.py, *.pyc |
    Select-String -Pattern 'API status' -SimpleMatch |
    Select-Object Path, LineNumber, Line
```

If the second command returns nothing at all, the string is not in any Python file under
`C:\inetpub` — the site is loading from another drive/path, and
`Select-Object -ExpandProperty runtime.app_dir` from `/api/health` tells you which one.

Do **not** merge this `Web.config` into the SO-GEN root `web.config`: a site-level
`aspNetCore path="*"` handler would route the MVC application into Python.

### SSE

`/api/job-progress` is a `text/event-stream`. IIS does not compress it, but any reverse proxy
or WAF in front of the site must have response buffering disabled, otherwise the progress log
arrives only at the end.

---

## 4. What still has to be decided for the real merge

These are product decisions, not bugs — deliberately **not** changed here:

* **Authentication.** The app has none: `Web.config` allows anonymous so that `/api/health` stays
  pollable, and the sidebar entry makes it discoverable. Anybody who can reach the intranet can
  drive the GAP account. Options: Windows auth on that IIS application, an IP allow-list, or
  swapping `allow users="*"` for `<deny users="?" />` in `GapIntegration\Web.config` so the SO-GEN
  Forms ticket (`.ASPXAUTH`, default name because `web.config` sets no `name=`; path `/`) is
  required — the last option also means `deploy-gap.ps1`/`curl` must send that cookie, so decide
  before go-live.
* **Data bridge.** Nothing writes the Gap job back into RCOMM. There is no `GapJobId`/`Source`
  column on `Imports`, and `RpacCommercialDB.sql` still lacks `Imports.ApproximateFreightCost`
  and `Suppliers.BPCode` — an insert path would fail on a fresh database for the same reason
  the MVC shipment form does.
* **Account-specific GUIDs.** `confirmDto[userShipBillId]`, `confirmDto[printShopId]`, `cooId`
  and the `marketCode` are hard-coded for user `GAP0012`. If the account changes, checkout
  fails validation. They should move to `.env`.
* **GUID vs name in the payload.** `productGenderId` / `productTypeId` receive the dropdown
  *text* (`"BOYS"`, `"Tops"`), while every other `*Id` in the payload is a GUID, and `seasonId`
  is always empty. If `to-job` starts answering `status:false` with a validation message, map
  the names to ids first: `/api/app/product-gender` already exists but the UI no longer calls it
  (the genders are hard-coded in `brandData`). **This is the first thing to check for
  `Failed to add to shopping cart`** — `diagnose.py` prints these two fields next to the
  rejection so an empty or textual value is visible immediately. The app now also retries the
  same add with the ids taken from `gap-po-item-head` (`po-head`) and with the ids resolved
  from `/api/app/product-gender`, `/api/app/season` and
  `/api/app/size-chart/product-type-by-product-gender` (`resolved-ids`); whichever variant the
  portal accepts is logged, and `GAP_PAYLOAD_VARIANT` pins it so later runs post once.
* **Vendor-PO matching after checkout.** The job id is recovered by comparing the order
  history (`poNo` **and** an exact `vendorPo` match) with the highest id seen before checkout.
  If the portal does not echo `vendorPo` back exactly (trimmed / upper-cased / blank), the job is
  created but the run ends with `ERROR: No Job IDs found` and the cart is already consumed.
  Verified in simulation: with `vendorPo` present the whole chain completes; without it the run
  fails at that step even though every earlier call succeeded.
* **LPN slab on the cart.** `calculate_lpn_quantity` is applied twice: on the PO detail lines
  and again on the cart items just before `Confirm 1st`. The second pass uses
  `cartGapPoItemDto.originalQuantity` as the base, so it is idempotent. If the portal omits
  `originalQuantity` the cart quantity is used instead, which may already contain the slab —
  the job log then prints `WARNING: … returned no originalQuantity`; check the ordered quantity
  on the portal before printing. This is the one part of the new feature that a real run has to
  confirm.
* **Unused leftovers, dropped.** `master_info.json` (264 KB), `wwwroot/lib` (Bootstrap),
  `wwwroot/js/site.js`, `wwwroot/css/site.css`, `wwwroot/favicon.ico`, the pre-`wwwroot` `static/`
  folder and `.config/dotnet-tools.json` (from an abandoned .NET port) are not referenced by the
  app — `index.html` is self-contained and loads only `wwwroot/static/images/rpac_logo.png` — so this
  folder no longer carries them. They are left out of `gap-fixes-vs-8a22893.patch` on purpose (they
  are 80 k lines of vendored jQuery/Bootstrap and would bury the diff); delete them once, either by
  hand or with:

  ```powershell
  Remove-Item -Recurse -Force .config, master_info.json, static, wwwroot\css, wwwroot\js, wwwroot\lib, wwwroot\favicon.ico
  ```

  `deploy-gap.ps1` mirrors with `/MIR`, so it also removes the copies already on the server.

---

## 5. What was verified

* `python -m py_compile` on `app.py`, `auth.py`, `portal_api.py`, `diagnose.py`, `envcheck.py`.
* `envcheck.py` run standalone: reports interpreter/venv/site-packages/cwd/`.env`/`logs`/package
  versions and flags every missing deployment file, so "library vs code vs deployment" is decided
  by one command instead of by guessing.
* Module import against stubbed FastAPI/requests (no PyPI access in the build environment):
  import-time names, `BASE_DIR` resolution and the `StaticFiles`/`Jinja2Templates` directories.
* `StripBasePathMiddleware` unit-checked for `/gap/api/x` → `/api/x`, `/gap` → `/`, and a
  no-op at the site root.
* `to_form_urlencoded()` checked with a cart item containing nested `details`/`baseItem`.
* LPN slab logic from `8a22893` checked end-to-end in the simulated run (cart item `5` →
  `18` for GAP/MAS, `17` for Old Navy/MAS) and the value reaches `Confirm 1st` in **both**
  `quantity` and `lpnQuantity`; band edges and the odd/even rules spot-checked
  (`600`/NON-MAS → `610`, `6000`/MAS → `6030`).
* `Web.config` parses as XML; inline `<script>` passes `node --check`.
* Add-to-cart failure paths replayed against a fake portal: (a) `to-job` returning
  `{"status":false,"message":"Failed to add to shopping cart…"}` → the run ends with the
  payload-echoing message and `No POs successfully added to cart. Details: …`;
  (b) `to-job` answering `401` → one re-login, one retry, PO added;
  (c) `bulk-delete` answering `500` while the PO is still in the cart → the PO is skipped with
  `already in the portal cart` instead of a doomed `to-job`. `diagnose.py` dry-run and `--post`
  checked against the same fake portal (payload, slab column, rejection block, exit codes).
* The `to-job` payload ladder replayed with a fake portal that only accepts GUID ids: text ids
  rejected → **second** POST with the ids from `gap-po-item-head` accepted (2 POSTs);
  with the head empty → ids resolved through `product-gender` / `season` /
  `product-type-by-product-gender` accepted (2 POSTs); `GAP_PAYLOAD_VARIANT=po-head` → 1 POST.
  All three finish the run with the PO in the cart.
* Every commit of this repository (`19f586c`, `8a22893` — 96 blobs, both trees) was scanned for the
  strings in the failure report: **`API status` and `Oops` occur in no file**. The code running on
  SO-GEN therefore predates this repository or was edited on the server; no git ref can produce
  `P2608760: API status=false | …`. That is the reason `portal_api_sha` exists.
* `portal_api.SOURCE_SHA` is derived from the file's own bytes and is printed by `envcheck.py`,
  the startup log, `diagnose.py` and `/api/health`, so four independent views can be compared.
* `deploy-gap.ps1` reviewed for the two failure modes it exists to catch: it passes `.env`, `venv`,
  `logs`, `__pycache__` and `*.pyc` to robocopy's exclude list, imports the sha with
  `Push-Location $Target` so it cannot hash the source folder by accident, and exits non-zero when
  `/api/health` never reports that sha. Not executed here (no Windows/PowerShell in this
  environment) — the commands it wraps are the ones from section 3, run by hand and verified.
* `RpacCommercial.csproj` now carries `GapIntegration\**\*` as `Content` (excluding `venv`,
  `__pycache__`, `logs`, `.git`, `*.pyc`, `*.pyo`, `.env`), so a Web Deploy of SO-GEN ships this
  folder instead of publishing an MVC-only site. Checked by parsing the `.csproj` as XML and by
  applying the same include/exclude pair to the folder with Python, which ships the sources,
  `templates/`, `wwwroot/`, `Web.config`, the scripts and `requirements.txt` and skips the venv,
  bytecode, `.env` and `logs/*.log` (the folder itself still ships, so IIS has a place to write).
* `install-gap-iis.ps1` is idempotent by construction (`list` → `add` or `set`), refuses to run
  without `app.py`, warns when `-Alias` differs from `GAP_BASE_PATH`, and exits `3` when the venv
  does not exist yet. Not executed here (no Windows/IIS in this environment).
* `find-gap-copies.ps1` turns the "which copy is running" question into one command: it parses
  `appcmd list vdir` into name/physical-path pairs, hashes every `portal_api.py` the way
  `_source_sha()` does (SHA-1 of the file bytes, first 12 hex chars), reads `.py` **and** `.pyc` as
  bytes so a bytecode-only folder is still caught, and lists running `python` processes with their
  command line. Its `-Quarantine` branch only touches IIS applications whose folder contains
  `API status`, which the copy shipped here never does, so it cannot retire the good copy; it stops
  the pool, removes the application and renames the folder, and refuses to run without `-Force`.
  Not executed here (no Windows/IIS in this environment).
* Live portal probed read-only (login page + 8 endpoints) — see section 1.

Not verified: a real job against the portal (no `GAP_PASSWORD` in this environment) and the
FastAPI runtime itself (PyPI is unreachable here, so the real packages could not be installed).
