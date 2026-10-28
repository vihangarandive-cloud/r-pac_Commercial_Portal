"""Answers "why does the same code behave differently on the SO-GEN server?"

The same folder that runs fine from a console (`python app.py`) is started by IIS
through AspNetCoreModuleV2, as a different user, from a different working
directory, with whatever interpreter `Web.config` names. This module collects the
handful of facts that actually differ, so a support call does not have to guess
between "Python library problem" and "code problem":

    python -c "import envcheck; print(envcheck.format_runtime())"

The same block is logged at startup and returned by /api/health ("runtime"), so
the console run, the IIS worker and diagnose.py can be compared line by line.

No secret is included: the password is reported as present/absent only.
"""

import importlib.metadata
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

# Imported by app.py (needs to exist on the server), so keep it dependency-free.

CHECKED_PACKAGES = (
    "fastapi",
    "uvicorn",
    "requests",
    "urllib3",
    "python-dotenv",
    "jinja2",
    "starlette",
    "pydantic",
)

# Files that must travel with the code. A missing one is the difference between
# "the fix is deployed" and "IIS is running a folder that only looks updated".
REQUIRED_PATHS = (
    "app.py",
    "auth.py",
    "portal_api.py",
    "diagnose.py",
    "requirements.txt",
    ".env",
    "Web.config",
    "templates/index.html",
    "wwwroot/static",
    "logs",
)


def package_versions():
    """{name: version} for the packages the app imports, or the import error."""
    found = {}
    for name in CHECKED_PACKAGES:
        try:
            found[name] = importlib.metadata.version(name)
        except Exception as exc:  # PackageNotFoundError, or broken metadata
            found[name] = f"MISSING ({type(exc).__name__})"
    return found


def runtime_info(base_dir=None) -> dict:
    """Facts that differ between a console run and the IIS worker."""
    py_file = globals().get("__file__") or ""
    here = Path(base_dir or (Path(py_file).resolve().parent if py_file else Path.cwd())).resolve()
    venv = sys.prefix != getattr(sys, "base_prefix", sys.prefix)
    versions = package_versions()
    missing = [name for name, ver in versions.items() if str(ver).startswith("MISSING")]

    paths = {}
    for rel in REQUIRED_PATHS:
        target = here / rel
        paths[rel] = "yes" if target.exists() else "MISSING"

    log_dir = here / "logs"
    log_writable = False
    if log_dir.is_dir():
        probe = log_dir / ".write-test"
        try:
            probe.write_text("x", encoding="utf-8")
            probe.unlink()
            log_writable = True
        except OSError:
            log_writable = False

    env_file = here / ".env"

    return {
        "python": sys.version.split()[0],
        "python_executable": sys.executable,
        "python_64bit": sys.maxsize > 2 ** 32,
        "in_venv": venv,
        "venv_prefix": sys.prefix,
        "site_packages": next(
            (p for p in sys.path if p.endswith(("site-packages", "dist-packages"))), ""
        ),
        "cwd": os.getcwd(),
        "app_dir": str(here),
        "user": os.getenv("USERNAME") or os.getenv("USER") or "",
        "packages": versions,
        "packages_missing": missing,
        "files": paths,
        "logs_writable": log_writable,
        "env_file": str(env_file),
        "env_file_found": env_file.exists(),
        "base_path": os.getenv("GAP_BASE_PATH", "") or "/",
        "payload_variant": os.getenv("GAP_PAYLOAD_VARIANT") or "auto",
        "server_time_utc": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC"),
        "pid": os.getpid(),
    }


def format_runtime(info=None) -> str:
    """The runtime block as plain text (for the log and for diagnose.py)."""
    info = info or runtime_info()
    lines = [
        "runtime:",
        f"  python      : {info['python']} ({'64-bit' if info['python_64bit'] else '32-bit'})"
        f" venv={'yes' if info['in_venv'] else 'no'}",
        f"  executable  : {info['python_executable']}",
        f"  site-packages: {info['site_packages'] or '<none>'}",
        f"  app_dir     : {info['app_dir']}",
        f"  cwd         : {info['cwd']}",
        f"  user/pid    : {info['user'] or '?'} / {info['pid']}",
        f"  .env        : {info['env_file']} "
        f"({'found' if info['env_file_found'] else 'MISSING'})",
        f"  logs/       : {'writable' if info['logs_writable'] else 'NOT WRITABLE'}",
        f"  base_path   : {info['base_path']}  payload_variant: {info['payload_variant']}",
        f"  server time : {info['server_time_utc']}",
        "  packages    : "
        + ", ".join(f"{name}={ver}" for name, ver in info["packages"].items()),
    ]
    missing = [name for name, state in info["files"].items() if state == "MISSING"]
    lines.append(
        "  files       : " + ("all present" if not missing else "MISSING " + ", ".join(missing))
    )
    return "\n".join(lines)


if __name__ == "__main__":
    print(format_runtime())
