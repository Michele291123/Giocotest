#!/usr/bin/env bash
# Build del SERVER per Raspberry Pi (Linux ARM 64 bit, es. Raspberry Pi OS 64-bit).
# Produce un eseguibile autonomo (non serve installare .NET sul Pi).
#
# Uso:   ./scripts/build-server-pi.sh
# Output: dist/server/ClayPigeonServer
#
# Se il tuo Pi ha un OS a 32 bit, cambia linux-arm64 in linux-arm.
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-linux-arm64}"
echo ">> Publish Server per $RID ..."
dotnet publish src/Server/Server.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -o dist/server

echo ">> Fatto. Copia dist/server sul Raspberry Pi e avvia:"
echo "     ./ClayPigeonServer 5055"
