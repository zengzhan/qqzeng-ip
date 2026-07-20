#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec dotnet run --project "$SCRIPT_DIR/batch_csharp" --configuration Release -- "$@"
