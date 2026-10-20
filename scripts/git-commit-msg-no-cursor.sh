#!/bin/sh
# Reject unwanted AI co-author trailers in commit messages
sed -i '/[Cc]o-[Aa]uthored-[Bb]y:.*[Cc]ursor/d' "$1"
sed -i '/[Cc]ursor[Aa]gent/d' "$1"
