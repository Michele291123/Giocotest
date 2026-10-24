#!/usr/bin/env bash
# Build del laboratorio di simulazione cheat (gira ovunque ci sia .NET 8:
# sul tuo PC, sul Pi, ecc.). Attacca SOLO il nostro server, a scopo didattico.
#
# Uso:   ./scripts/build-cheatsim.sh
# Output: dist/cheatsim/ClayPigeonCheatSim
set -euo pipefail
cd "$(dirname "$0")/.."

echo ">> Publish CheatSim ..."
dotnet publish src/CheatSim/CheatSim.csproj -c Release -o dist/cheatsim

echo ">> Fatto. Esempio d'uso (con il server avviato):"
echo "     dotnet dist/cheatsim/ClayPigeonCheatSim.dll all 127.0.0.1 5055"
