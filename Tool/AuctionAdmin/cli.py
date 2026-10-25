"""Auction CLI transport facade; no database access, retry or private GM policy."""
import argparse
from http.cookiejar import CookieJar
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
from urllib import request, parse

EXIT_OK, EXIT_ARGS, EXIT_AUTH, EXIT_BUSINESS, EXIT_UNKNOWN = 0, 2, 3, 4, 7
DEFAULT_BASE_URL = "http://127.0.0.1:5051"
DEFAULT_PASSWORD_FILE = Path.home() / ".dfoserver" / "auction" / "http-password.txt"
LIMIT = 64 * 1024
PIPE = r"\\.\pipe\DfoServer.A21.Auction.v1"


class CliError(Exception):
    def __init__(self, code, message, exit_code, extra=None):
        super().__init__(message)
        self.code, self.message, self.exit_code, self.extra = code, message, exit_code, extra or {}


def emit(value):
    print(json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False))


def emit_error(error):
    emit(dict(ok=False, error=dict(code=error.code, message=error.message), exitCode=error.exit_code, **error.extra))
    return error.exit_code


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message):
        raise CliError("args", message, EXIT_ARGS)


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def resolve_base_url(value):
    value = (value or DEFAULT_BASE_URL).rstrip("/")
    url = parse.urlsplit(value)
    if url.username or url.password or url.path or url.query or url.fragment or not url.hostname \
            or url.scheme not in ("http", "https") \
            or (url.scheme == "http" and url.hostname not in ("127.0.0.1", "localhost", "::1")):
        raise CliError("args", "Use a loopback HTTP origin or an HTTPS origin without credentials or path.", EXIT_ARGS)
    return value


class HttpContext:
    def __init__(self, base_url, timeout, password_file):
        self.base_url, self.timeout, self.password_file = base_url, timeout, password_file
        self.opener = request.build_opener(request.ProxyHandler({}), request.HTTPCookieProcessor(CookieJar()), NoRedirect())


def ensure_login(context):
    try:
        password = os.environ.get("AUCTION_ADMIN_HTTP_PASSWORD")
        if password is None:
            password = Path(context.password_file).expanduser().read_text(encoding="utf-8-sig").rstrip("\r\n")
        if not password:
            raise ValueError()
        message = request.Request(context.base_url + "/api/auth/login",
            data=json.dumps({"password": password}).encode("utf-8"), method="POST",
            headers={"Content-Type": "application/json", "Accept": "application/json"})
        password = None
        with context.opener.open(message, timeout=context.timeout) as stream:
            raw = stream.read(LIMIT + 1)
            if len(raw) > LIMIT or json.loads(raw).get("success") is not True:
                raise ValueError()
    except Exception:
        raise CliError("authentication-required", "GM login failed; no auction command was sent.", EXIT_AUTH) from None


class Refusal(Exception):
    pass


def exchange(value, timeout=15):
    try:
        body = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
        if len(body) > LIMIT:
            raise ValueError()
        result = subprocess.run([sys.executable, str(Path(__file__).resolve()), "_exchange"],
            input=body + b"\n", capture_output=True, timeout=timeout, check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if len(result.stdout) > LIMIT + 1:
            raise ValueError()
        answer = json.loads(result.stdout)
        if not isinstance(answer, dict):
            raise ValueError()
        return answer
    except Exception:
        raise Refusal() from None


def load_admin_adapter():
    return SimpleNamespace(exchange=exchange, Refusal=Refusal)


def worker():
    try:
        if os.name != "nt":
            raise Refusal()
        body = sys.stdin.buffer.readline(LIMIT + 2)
        if len(body) > LIMIT + 1 or not body.endswith(b"\n"):
            raise Refusal()
        with open(PIPE, "r+b", buffering=0) as stream:
            written = 0
            while written < len(body):
                count = stream.write(body[written:])
                if not count:
                    raise Refusal()
                written += count
            answer = bytearray()
            while True:
                byte = stream.read(1)
                if not byte:
                    raise Refusal()
                if byte == b"\n":
                    break
                answer.extend(byte)
                if len(answer) > LIMIT:
                    raise Refusal()
        # Keep the response bounded even when Unicode escaping grows its bytes.
        json.loads(answer.decode("utf-8"))
        sys.stdout.buffer.write(answer + b"\n")
        return 0
    except Exception:
        emit(dict(success=False, code="unknown-outcome", detail="Pipe result unavailable; reconcile this exact requestId."))
        return EXIT_UNKNOWN


if __name__ == "__main__":
    if sys.argv[1:] == ["_exchange"]:
        raise SystemExit(worker())
    import client
    raise SystemExit(client.run(sys.argv[1:], sys.modules[__name__]))
