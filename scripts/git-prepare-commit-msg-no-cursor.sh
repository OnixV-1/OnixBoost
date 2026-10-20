#!/bin/sh
# Git prepare-commit-msg: strip Cursor trailers even if injected into the template.
msg_file="$1"
[ -n "$msg_file" ] && [ -f "$msg_file" ] || exit 0
tmp="$(mktemp)" || exit 0
grep -viE '^[[:space:]]*(Co-authored-by|Signed-off-by):[[:space:]].*[Cc]ursor' "$msg_file" \
  | grep -viE 'cursoragent@cursor\.com' \
  > "$tmp" || true
mv "$tmp" "$msg_file"
exit 0
