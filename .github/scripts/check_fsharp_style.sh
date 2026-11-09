#!/usr/bin/env bash
# CI check for the rules in docs/fsharp-style.md that a grep can catch: no mutation, no discarded
# results, no null handling outside a commented library boundary. Comments are ignored.
set -euo pipefail
status=0
while IFS= read -r file; do
  # Strip // comments before matching, so documentation can mention the words.
  if hits=$(sed 's|//.*$||' "$file" | grep -nE '\bmutable\b|<-|\|> *ignore\b|\bisNull\b|= *null\b|\| *null\b|Unchecked\.defaultof'); then
    while IFS= read -r hit; do echo "$file:$hit"; done <<<"$hits"
    status=1
  fi
done < <(git ls-files '*.fs' '*.fsx')
if [ $status -ne 0 ]; then
  echo "F# style: mutation, discarded results or null found. See docs/fsharp-style.md §2–3."
else
  echo "F# style ok"
fi
exit $status
