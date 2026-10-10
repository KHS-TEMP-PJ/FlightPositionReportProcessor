#!/usr/bin/env bash
set -euo pipefail
(cd infrastructure && npm install --no-audit --no-fund && npx cdk destroy --force)