#!/bin/bash
set -euo pipefail

# Only run in remote (Claude Code on the web) environments
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# Install .NET 8 SDK if not already present
if ! command -v dotnet &> /dev/null || ! dotnet --list-sdks 2>/dev/null | grep -q "^8\."; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
fi

export PATH="$HOME/.dotnet:$PATH"

# Persist PATH for the session
echo "export PATH=\"\$HOME/.dotnet:\$PATH\"" >> "$CLAUDE_ENV_FILE"

# Restore NuGet packages
dotnet restore "$CLAUDE_PROJECT_DIR/Inferno.sln"
