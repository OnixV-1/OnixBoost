# Git hooks (block AI co-author trailers)

```bash
bash scripts/install-git-no-cursor-hooks.sh .
```

Installs `commit-msg`, `prepare-commit-msg`, and `pre-push` hooks that strip or reject unwanted `Co-authored-by` trailers so GitHub Contributors stays clean.
