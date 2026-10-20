#!/bin/sh
# Git pre-push: block push if any outgoing commit attributes Cursor / cursoragent.
remote="$1"
url="$2"

z40=0000000000000000000000000000000000000000
fail=0

while read -r local_ref local_sha remote_ref remote_sha
do
  [ -z "$local_sha" ] && continue
  if [ "$local_sha" = "$z40" ]; then
    continue
  fi
  if [ "$remote_sha" = "$z40" ]; then
    range="$local_sha"
  else
    range="$remote_sha..$local_sha"
  fi
  hits=$(git log --format='%H%n%B%n%an <%ae>%n%cn <%ce>' "$range" 2>/dev/null \
    | grep -iE 'cursoragent|co-authored-by:.*cursor|cursoragent@cursor\.com' || true)
  if [ -n "$hits" ]; then
    echo "REJECTED push: Cursor / cursoragent attribution found in commits:" >&2
    echo "$hits" >&2
    echo "Rewrite history (no Co-authored-by Cursor) before pushing." >&2
    fail=1
  fi
done

exit "$fail"
