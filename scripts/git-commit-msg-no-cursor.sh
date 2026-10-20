#!/bin/sh
# Git commit-msg hook: remove unwanted AI co-author trailers before commit is created.
# Install as: .git/hooks/commit-msg
msg_file="$1"
[ -n "$msg_file" ] && [ -f "$msg_file" ] || exit 0
# Portable in-place edit (BusyBox/GNU)
tmp="$(mktemp)" || exit 0
# Drop Co-authored-by / Signed-off lines that mention Cursor / cursoragent
grep -viE '^[[:space:]]*(Co-authored-by|Signed-off-by):[[:space:]].*[Cc]ursor' "$msg_file" \
  | grep -viE '^[[:space:]]*Co-authored-by:[[:space:]].*cursoragent@' \
  > "$tmp" || true
# Also drop bare cursoragent lines
grep -viE 'cursoragent@cursor\.com' "$tmp" > "${tmp}.2" || true
mv "${tmp}.2" "$msg_file"
rm -f "$tmp"
exit 0
