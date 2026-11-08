#!/usr/bin/env bash
# CI check: everyone referenced in access/people.yaml is defined, and every repo has a lead.
set -euo pipefail
file=access/people.yaml
people=$(yq '.people | keys | .[]' "$file")
status=0
for ref in $(yq '.groups[][], ."repo-leads"[][]' "$file" | sort -u); do
  grep -qx "$ref" <<<"$people" || { echo "$file: unknown person '$ref'"; status=1; }
done
for repo in $(yq '."repo-leads" | keys | .[]' "$file"); do
  [ "$(yq ".\"repo-leads\".$repo | length" "$file")" -gt 0 ] || { echo "$file: repo '$repo' has no lead"; status=1; }
done
for person in $people; do
  [ "$(yq ".people.$person.github" "$file")" != "null" ] || { echo "$file: '$person' has no github handle"; status=1; }
done
[ $status -eq 0 ] && echo "access ok"
exit $status
