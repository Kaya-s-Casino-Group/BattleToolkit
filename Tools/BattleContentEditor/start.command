#!/bin/bash
set -e
root="$(cd "$(dirname "$0")/../.." && pwd)"
exec bash "$root/start.command" "$@"
