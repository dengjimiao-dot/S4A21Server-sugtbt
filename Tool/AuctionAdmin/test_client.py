"""Offline client contract tests. All pipe/HTTP/password seams are mocked."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("auction_gm_api", HERE / "cli.py")
gm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gm)
spec = importlib.util.spec_from_file_location("auction_client", HERE / "client.py")
auction = importlib.util.module_from_spec(spec)
spec.loader.exec_module(auction)


class AuctionClientTests(unittest.TestCase):
    def call(self, argv, response=None):
        exchange = Mock(return_value=response or {"success": True, "code": "ok", "data": {"commands": []}})
        stream = io.StringIO()
        with patch.object(auction, "pipe_exchange", exchange), contextlib.redirect_stdout(stream):
            code = auction.run(argv, gm)
        return code, json.loads(stream.getvalue()), exchange

    def test_default_preview_and_generic_schema(self):
        code, _result, call = self.call(["capabilities"])
        self.assertEqual(0, code)
        request = call.call_args[0][2]
        self.assertIs(True, request["dryRun"])
        self.assertEqual("auction", request["operation"])
        self.assertEqual("capabilities", request["args"]["command"])

    def test_apply_requires_explicit_id_before_transport(self):
        code, _result, call = self.call(["buyout", "--apply"])
        self.assertEqual(2, code)
        call.assert_not_called()

    def test_actor_label_and_nested_data_are_preserved(self):
        code, _result, call = self.call(["bid", "--apply", "--request-id", "bid-1", "--operator", "ops",
            "--reason", "reviewed", "--target-account-id", "1", "--target-character-id", "4",
            "--data", '{"orderId":5,"expectedVersion":4,"amount":1000}'])
        self.assertEqual(0, code)
        request = call.call_args[0][2]
        self.assertEqual({"orderId": 5, "expectedVersion": 4, "amount": 1000}, request["args"]["data"])
        self.assertEqual("ops", request["args"]["operator"])
        self.assertIs(False, request["dryRun"])
        self.assertEqual(4, request["targetCharacterId"])

    def test_file_full_envelope_does_not_change_hash_material(self):
        envelope = {"operation": "auction", "requestId": "file-1", "dryRun": False,
            "args": {"command": "cancel", "operator": "ops", "reason": "reviewed", "data": {"orderId": 5}}}
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / "request.json"
            file.write_text(json.dumps(envelope), encoding="utf-8")
            code, _result, call = self.call(["--input", str(file), "--apply"])
        self.assertEqual(0, code)
        self.assertEqual(envelope, call.call_args[0][2])

    def test_full_envelope_apply_gate_and_operation_gate(self):
        for body in (
            {"operation": "character.offlineGm", "requestId": "x", "dryRun": True, "args": {}},
            {"operation": "auction", "requestId": "x", "dryRun": False, "args": {}},
            {"operation": "auction", "requestId": "x", "dryRun": True, "path": "forbidden", "args": {}},
        ):
            with self.subTest(body=body), tempfile.TemporaryDirectory() as directory:
                file = Path(directory) / "input.json"
                file.write_text(json.dumps(body), encoding="utf-8")
                code, _result, call = self.call(["--input", str(file)])
                self.assertEqual(2, code)
                call.assert_not_called()

    def test_duplicate_nonfinite_and_oversized_input_fail_before_transport(self):
        for data in ('{"amount":1,"amount":2}', '{"amount":NaN}', '{"x":"' + 'x' * auction.LIMIT + '"}'):
            code, _result, call = self.call(["bid", "--data", data])
            self.assertEqual(2, code)
            call.assert_not_called()

    def test_bad_id_target_and_timeout_fail_before_transport(self):
        for extra in (["--request-id", "bad..id"], ["--target-character-id", "0"],
            ["--target-account-id", "2147483648"], ["--timeout", "16"], ["--timeout", "nan"]):
            code, _result, call = self.call(["orders.query"] + extra)
            self.assertEqual(2, code)
            call.assert_not_called()

    def test_unknown_result_never_retries_or_changes_id(self):
        for reason in auction.UNKNOWN:
            code, result, call = self.call(["cancel", "--apply", "--request-id", "stable-1"],
                {"success": False, "code": reason})
            self.assertEqual(7, code)
            self.assertEqual("stable-1", result["requestId"])
            self.assertEqual(1, call.call_count)

    def test_business_rejection_is_not_transport_unknown(self):
        code, _result, call = self.call(["cancel"], {"success": False, "code": "stale-version"})
        self.assertEqual(4, code)
        self.assertEqual(1, call.call_count)

    def test_core_frozen_unknown_codes_are_not_known_zero_write_refusals(self):
        for reason in ("unavailable-or-unknown", "cancelled-or-unknown", "quarantined", "pending"):
            with self.subTest(code=reason):
                code, result, call = self.call(["buyout", "--apply", "--request-id", "unknown-core-1"],
                    {"success": False, "code": reason})
                self.assertEqual(7, code)
                self.assertEqual("unknown-core-1", result["requestId"])
                self.assertEqual(reason, result["response"]["code"])
                self.assertEqual(1, call.call_count)

    def test_http_authenticates_before_single_command(self):
        events = []
        class Response:
            code = 200
            def read(self, size):
                self.size = size
                return b'{"success":true,"code":"ok"}'
            def __enter__(self): return self
            def __exit__(self, *_args): pass
        response = Response()
        def send(request, timeout):
            events.append("command")
            self.assertEqual("http://127.0.0.1:5051/api/auction/commands", request.full_url)
            self.assertEqual("write-1", json.loads(request.data)["requestId"])
            return response
        context = SimpleNamespace(base_url=gm.DEFAULT_BASE_URL, opener=SimpleNamespace(open=send))
        with patch.object(gm, "HttpContext", return_value=context), \
                patch.object(gm, "ensure_login", side_effect=lambda _context: events.append("login")):
            args = SimpleNamespace(base_url=gm.DEFAULT_BASE_URL, timeout=15, password_file="never-opened")
            auction.http_exchange(gm, args, {"requestId": "write-1", "operation": "auction"})
        self.assertEqual(["login", "command"], events)
        self.assertEqual(auction.LIMIT + 1, response.size)

    def test_http_failure_emits_no_exception_or_secret_and_does_not_retry(self):
        send = Mock(side_effect=OSError("secret-never-print"))
        context = SimpleNamespace(base_url=gm.DEFAULT_BASE_URL, opener=SimpleNamespace(open=send))
        with patch.object(gm, "HttpContext", return_value=context), patch.object(gm, "ensure_login"), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            code = auction.run(["cancel", "--apply", "--request-id", "unknown-1", "--transport", "http"], gm)
        self.assertEqual(7, code)
        self.assertEqual(1, send.call_count)
        self.assertNotIn("secret-never-print", output.getvalue())
        self.assertEqual("unknown-1", json.loads(output.getvalue())["requestId"])

    def test_help_does_not_contact_any_transport(self):
        code, _result, call = self.call(["--help"])
        self.assertEqual(0, code)
        call.assert_not_called()


if __name__ == "__main__":
    unittest.main()
