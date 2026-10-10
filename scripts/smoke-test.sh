#!/usr/bin/env bash
set -euo pipefail
: "${API_URL:?Set API_URL to the stack ApiUrl output}"
REPORT='POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800'
RESPONSE="$(curl -fsS -X POST "${API_URL}pos-reports" -H 'Content-Type: text/plain' --data-binary "$REPORT")"
echo "$RESPONSE"
FLIGHT_ID="$(printf '%s' "$RESPONSE" | python3 -c 'import json,sys; print(json.load(sys.stdin)["flightId"])')"
for _ in $(seq 1 20); do STATUS="$(curl -ss -w '\n%{http_code}' "${API_URL}status/${FLIGHT_ID}")"; echo "$STATUS" | grep -q CALCULATED && exit 0; sleep 3; done
echo 'Calculation did not complete within 60 seconds.' >&2; exit 1