#!/usr/bin/env bash
set -euo pipefail
: "${AWS_REGION:?Set AWS_REGION}"
ACCOUNT="$(aws sts get-caller-identity --query Account --output text)"
export CDK_DEFAULT_ACCOUNT="$ACCOUNT" CDK_DEFAULT_REGION="$AWS_REGION"
(cd infrastructure && npm install --no-audit --no-fund && npx cdk bootstrap "aws://${ACCOUNT}/${AWS_REGION}" && npx cdk deploy --require-approval never)