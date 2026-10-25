"""Thin auction client. Business command schemas come from capabilities, not this file."""
from __future__ import annotations

import json
import re
import sys
import uuid
from pathlib import Path
from urllib import error as urlerror
from urllib import request as urlrequest

LIMIT = 64 * 1024
ID = re.compile(r"^[A-Za-z0-9._-]{1,128}$")
COMMAND = re.compile(r"^[A-Za-z][A-Za-z0-9.-]{0,127}$")
FIELDS = {"requestId", "operation", "targetAccountId", "targetCharacterId", "args", "dryRun"}
UNKNOWN = {"unknown-outcome", "ipc-unavailable", "auction-unavailable", "response-too-large", "pending", "unknown",
    "unavailable-or-unknown", "cancelled-or-unknown", "quarantined"}


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON property")
        value[key] = item
    return value


def _constant(_value):
    raise ValueError("non-standard JSON constant")


def parse(text):
    return json.loads(text, object_pairs_hook=_unique_object, parse_constant=_constant)


def read_json(source, api):
    try:
        if source == "-":
            raw = sys.stdin.buffer.read(LIMIT + 1)
        elif source.startswith("@"):
            with Path(source[1:]).open("rb") as stream:
                raw = stream.read(LIMIT + 1)
        else:
            raw = source.encode("utf-8")
        if len(raw) > LIMIT:
            raise ValueError("input exceeds limit")
        return parse(raw.decode("utf-8-sig"))
    except (ValueError, OSError, UnicodeError):
        raise api.CliError("args", "Input must be one unique-field JSON object within 64 KiB.", api.EXIT_ARGS) from None


def make_request(args, api):
    if args.input:
        if args.command or args.data is not None or args.operator is not None or args.reason is not None \
                or args.request_id is not None or args.target_account_id is not None or args.target_character_id is not None:
            raise api.CliError("args", "--input cannot be combined with envelope construction arguments.", api.EXIT_ARGS)
        source = args.input if args.input == "-" or args.input.startswith("@") else "@" + args.input
        request = read_json(source, api)
        if not isinstance(request, dict) or set(request) - FIELDS or request.get("operation") != "auction":
            raise api.CliError("args", "--input requires the closed operation=auction request envelope.", api.EXIT_ARGS)
        if type(request.get("dryRun")) is not bool or request["dryRun"] == args.apply:
            raise api.CliError("args", "Use dryRun=true by default, or dryRun=false together with --apply.", api.EXIT_ARGS)
    else:
        if not args.command or not COMMAND.fullmatch(args.command):
            raise api.CliError("args", "Supply a command; use capabilities to discover schemas.", api.EXIT_ARGS)
        if args.apply and not args.request_id:
            raise api.CliError("args", "--apply requires an explicit stable --request-id.", api.EXIT_ARGS)
        data = {} if args.data is None else read_json(args.data, api)
        if not isinstance(data, dict):
            raise api.CliError("args", "--data must be a JSON object.", api.EXIT_ARGS)
        request = {
            "requestId": args.request_id or "preview-" + uuid.uuid4().hex,
            "operation": "auction", "dryRun": not args.apply,
            "args": {"command": args.command, "operator": args.operator or "", "reason": args.reason or "", "data": data},
        }
        if args.target_account_id is not None:
            request["targetAccountId"] = args.target_account_id
        if args.target_character_id is not None:
            request["targetCharacterId"] = args.target_character_id
    request_id = request.get("requestId")
    if not isinstance(request_id, str) or not ID.fullmatch(request_id) or ".." in request_id:
        raise api.CliError("args", "requestId must contain 1..128 ASCII letters, digits, '.', '_' or '-', without '..'.", api.EXIT_ARGS)
    for field in ("targetAccountId", "targetCharacterId"):
        if field in request and (type(request[field]) is not int or not 1 <= request[field] <= 2147483647):
            raise api.CliError("args", field + " must be a positive int32.", api.EXIT_ARGS)
    if not isinstance(request.get("args"), dict):
        raise api.CliError("args", "args must be an object.", api.EXIT_ARGS)
    encoded = json.dumps(request, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    if len(encoded) > LIMIT:
        raise api.CliError("args", "Request exceeds 64 KiB.", api.EXIT_ARGS)
    return request


def http_exchange(api, args, request):
    context = api.HttpContext(api.resolve_base_url(args.base_url), args.timeout, args.password_file)
    # Authenticate before sending a command, never replay a write after a 401.
    # Cookie storage and password lookup are the existing GM implementation.
    api.ensure_login(context)
    body = json.dumps(request, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    message = urlrequest.Request(context.base_url + "/api/auction/commands", data=body, method="POST",
        headers={"Accept": "application/json", "Content-Type": "application/json; charset=utf-8"})
    try:
        try:
            stream = context.opener.open(message, timeout=args.timeout)
        except urlerror.HTTPError as error:
            stream = error
        with stream:
            status = stream.code
            raw = stream.read(LIMIT + 1)
        if status in (401, 403):
            raise api.CliError("authentication-required", "GM authentication was refused; command was not retried.", api.EXIT_AUTH)
        if 300 <= status < 400:
            raise api.CliError("unknown-outcome", "Redirect refused; reconcile the exact requestId.", api.EXIT_UNKNOWN)
        if len(raw) > LIMIT:
            raise ValueError()
        result = parse(raw.decode("utf-8"))
        if not isinstance(result, dict):
            raise ValueError()
        return result
    except api.CliError:
        raise
    except Exception:
        raise api.CliError("unknown-outcome", "HTTP result unavailable. Query this exact requestId before any retry.",
            api.EXIT_UNKNOWN, {"requestId": request["requestId"]}) from None


def pipe_exchange(api, args, request):
    adapter = api.load_admin_adapter()
    try:
        # Only the transport is reused; existing execute_owned/administer QA
        # policies remain unchanged and are never used to authorize auction.
        return adapter.exchange(request, timeout=args.timeout)
    except adapter.Refusal:
        raise api.CliError("unknown-outcome", "Pipe result unavailable. Query this exact requestId before any retry.",
            api.EXIT_UNKNOWN, {"requestId": request["requestId"]}) from None


def run(argv, api):
    parser = api.JsonArgumentParser(prog="cli.py", allow_abbrev=False,
        description="Auction-only admin API. Discover all commands and their schemas with capabilities.")
    parser.add_argument("command", nargs="?")
    parser.add_argument("--input", help="Complete game admin envelope file (or '-' for stdin).")
    parser.add_argument("--data", help="Command data JSON, @FILE or '-' for stdin.")
    parser.add_argument("--request-id")
    parser.add_argument("--operator")
    parser.add_argument("--reason")
    parser.add_argument("--target-account-id", type=int)
    parser.add_argument("--target-character-id", type=int)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--transport", choices=("pipe", "http"), default="pipe")
    parser.add_argument("--base-url", default=api.DEFAULT_BASE_URL)
    parser.add_argument("--password-file", default=str(api.DEFAULT_PASSWORD_FILE))
    parser.add_argument("--timeout", type=float, default=15.0, help="Pipe/socket timeout, greater than 0 and at most 15 seconds; HTTP authentication is a separate call.")
    try:
        if "--help" in argv or "-h" in argv:
            api.emit({"ok": True, "usage": parser.format_help(), "schema": "capabilities", "exitCode": 0})
            return 0
        args = parser.parse_args(argv)
        if not 0 < args.timeout <= 15:
            raise api.CliError("args", "--timeout must be greater than 0 and at most 15 seconds.", api.EXIT_ARGS)
        if args.transport == "pipe" and args.base_url != api.DEFAULT_BASE_URL:
            raise api.CliError("args", "--base-url applies only to --transport http.", api.EXIT_ARGS)
        request = make_request(args, api)
        result = (pipe_exchange if args.transport == "pipe" else http_exchange)(api, args, request)
        if not isinstance(result, dict) or type(result.get("success")) is not bool:
            raise api.CliError("unknown-outcome", "Invalid response; query this exact requestId.", api.EXIT_UNKNOWN,
                {"requestId": request["requestId"]})
        code = result.get("code")
        exit_code = api.EXIT_OK if result["success"] else (
            api.EXIT_UNKNOWN if code in UNKNOWN else api.EXIT_AUTH if code == "authentication-required" else api.EXIT_BUSINESS)
        api.emit({"ok": result["success"], "transport": args.transport, "requestId": request["requestId"],
            "response": result, "exitCode": exit_code})
        return exit_code
    except api.CliError as error:
        return api.emit_error(error)
    except KeyboardInterrupt:
        return api.emit_error(api.CliError("unknown-outcome", "Interrupted. Reconcile the exact requestId before any retry.", api.EXIT_UNKNOWN))
    except Exception:
        return api.emit_error(api.CliError("unknown-outcome", "Auction client failed; reconcile the exact requestId before any retry.", api.EXIT_UNKNOWN))
