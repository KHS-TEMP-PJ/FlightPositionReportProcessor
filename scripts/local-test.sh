#!/usr/bin/env bash
set -euo pipefail
dotnet restore FlightPositionReportProcessor.sln
dotnet build FlightPositionReportProcessor.sln -c Release --no-restore
dotnet test FlightPositionReportProcessor.sln -c Release --no-build
(cd infrastructure && npm install --no-audit --no-fund && npm run build && npm run synth)