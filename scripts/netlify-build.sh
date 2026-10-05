#!/usr/bin/env bash
set -euo pipefail

DOTNET_SDK_VERSION="10.0.100"
DOTNET_INSTALL_DIR="$HOME/.dotnet"

curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version "$DOTNET_SDK_VERSION" --install-dir "$DOTNET_INSTALL_DIR"

export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_INSTALL_DIR:$PATH"

dotnet --info
dotnet publish src/FamilyApp.Web/FamilyApp.Web.csproj -c Release -o publish
node scripts/configure-google-calendar.mjs publish/wwwroot/index.html
