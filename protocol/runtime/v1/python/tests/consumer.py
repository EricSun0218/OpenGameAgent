from __future__ import annotations

import json
import pathlib
import sys

PACKAGE_ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGE_ROOT))

from opengameagent_runtime_protocol import (  # noqa: E402
    RuntimeClient,
    RuntimeProtocolError,
    RuntimeReducer,
    SCHEMA_SHA256,
    parse_json_text,
)

fixture = PACKAGE_ROOT.parent / "fixtures" / "canonical-run.jsonl"
reducer = RuntimeReducer()
for line in fixture.read_text(encoding="utf-8").splitlines():
    reducer.apply(json.loads(line))
snapshot = reducer.snapshot()
if snapshot["status"] != "completed" or snapshot["lastSequence"] != 7 or len(snapshot["items"]) != 1:
    raise RuntimeError("Python Runtime Protocol fixture failed.")
if len(SCHEMA_SHA256) != 64:
    raise RuntimeError("Schema provenance is missing.")
for invalid, expected_code in ((r'{"value":1,"value":2}', "duplicate-field"), ("[" * 129 + "0" + "]" * 129, "json-too-deep")):
    try:
        parse_json_text(invalid)
    except RuntimeProtocolError as error:
        if error.code != expected_code:
            raise
    else:
        raise RuntimeError(f"Expected Runtime Protocol error {expected_code}.")

sse = "".join(
    f"event: runtime\nid: {event['eventId']}\ndata: {line}\n\n"
    for line in fixture.read_text(encoding="utf-8").splitlines()
    for event in (json.loads(line),)
)


class FakeResponse:
    def __init__(self, body: str, streaming: bool = False) -> None:
        self._body = body.encode("utf-8")
        self._streaming = streaming

    def read(self, maximum: int) -> bytes:
        return self._body[:maximum]

    def __iter__(self):
        if not self._streaming:
            return iter(())
        return iter(self._body.splitlines(keepends=True))

    def close(self) -> None:
        pass


def fake_opener(request, timeout: float):
    del timeout
    path = request.full_url
    body = json.loads(request.data.decode("utf-8"))
    if path.endswith("/initialize"):
        return FakeResponse(json.dumps({"version": 1, "capabilities": [], "serverName": "fixture", "serverVersion": "1"}))
    if path.endswith("/events"):
        return FakeResponse(json.dumps({
            "sessionId": body["sessionId"], "actorId": body["actorId"], "requestedAfterSequence": body["afterSequence"],
            "firstRetainedSequence": 1, "lastSequence": 7, "nextAfterSequence": 7, "gap": False, "events": [],
        }))
    if path.endswith("/steer") or path.endswith("/interrupt"):
        return FakeResponse(json.dumps({"status": "accepted", "activeRunId": "run", "activeTurn": 1, "accepted": True}))
    if path.endswith("/stream"):
        return FakeResponse(sse, streaming=True)
    raise RuntimeError("Unexpected Runtime endpoint.")


client = RuntimeClient("http://127.0.0.1:5157/", opener=fake_opener)
client.initialize()
client.read_events({"sessionId": "session", "actorId": "actor", "afterSequence": 0, "maximum": 32})
control = {"sessionId": "session", "actorId": "actor", "expectedRunId": "run", "expectedTurnId": "turn-1", "expectedTurn": 1, "messageJson": "{}"}
if not client.steer(control)["accepted"] or not client.interrupt(control)["accepted"]:
    raise RuntimeError("Python control client failed.")
streamed: list[object] = []
stream_result = client.stream({"requestId": "request", "inputJson": "{}"}, streamed.append)
if not stream_result.terminal or len(streamed) != 7 or stream_result.last_sequence != 7:
    raise RuntimeError("Python SSE client failed.")
print("OPENGAMEAGENT_RUNTIME_PYTHON_OK")
