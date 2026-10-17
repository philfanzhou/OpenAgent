#!/usr/bin/env python3
"""Build the pinned contract fixtures from the locked v1 schema.

Idempotent: re-running rewrites the same bytes. Existing definition/variant cases are
preserved; the script only repairs their legacy envelope-shape payloads to canonical
samples, attaches each case's `required` list from the locked schema, and derives one
JSON-RPC envelope fixture per method payload definition. `$defs` without an envelope
union upstream (the protocol-side `$/cancel_request` notification) are skipped and stay
covered at definition level.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
SCHEMA = os.path.join(HERE, "..", "schema", "v1", "schema.json")
SOURCES = os.path.join(HERE, "..", "sources.json")
CONTRACTS = os.path.join(HERE, "contracts.json")

# Legacy envelope definition samples pinned before the model review; keep them valid
# protocol payloads (the initialize result advertises the negotiated version 1).
REPAIRS = {
    ("definition-AgentResponse", "result"): {"protocolVersion": 1},
    ("variant-AgentResponse-1", "result"): {"protocolVersion": 1},
}


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main():
    schema = load(SCHEMA)["$defs"]
    commit = load(SOURCES)["protocol_baseline"]["commit"]
    contracts = load(CONTRACTS)
    assert contracts["protocolVersion"] == 1 and contracts["sourceCommit"] == commit

    cases = []
    payloads = {}
    for case in contracts["cases"]:
        item = {"id": case["id"], "schema": case["schema"], "value": case["value"]}
        repair = REPAIRS.get((case["id"], "result"))
        if repair is not None:
            assert case["schema"] in ("AgentResponse",), case["id"]
            assert case["value"].keys() == {"id", "result"}, case["id"]
            item["value"] = {"id": case["value"]["id"], "result": repair}
        item["required"] = schema[case["schema"]].get("required", [])
        cases.append(item)
        if case["id"].startswith("definition-"):
            payloads[case["schema"]] = case["value"]

    cases = [case for case in cases if not case["id"].startswith("envelope-")]
    for name, definition in schema.items():
        method = definition.get("x-method")
        side = definition.get("x-side")
        if method is None or side not in ("agent", "client") or name not in payloads:
            continue  # ext shapes and the protocol-side cancel notification have no envelope
        if name.endswith("Request"):
            envelope, value, kind = ("ClientRequest" if side == "agent" else "AgentRequest"), {
                "id": 1, "method": method, "params": payloads[name]}, "request"
        elif name.endswith("Response"):
            envelope, value, kind = ("AgentResponse" if side == "agent" else "ClientResponse"), {
                "id": 1, "result": payloads[name]}, "response"
        elif name.endswith("Notification"):
            envelope, value, kind = ("ClientNotification" if side == "agent" else "AgentNotification"), {
                "method": method, "params": payloads[name]}, "notification"
        else:
            continue
        cases.append({"id": f"envelope-{method}-{kind}", "schema": envelope,
                      "value": value, "required": schema[envelope].get("required", [])})

    with open(CONTRACTS, "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"protocolVersion": 1, "sourceCommit": commit, "cases": cases},
                  handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    envelopes = sum(1 for case in cases if case["id"].startswith("envelope-"))
    print(f"wrote {len(cases)} cases ({envelopes} envelopes)")


if __name__ == "__main__":
    sys.exit(main())
