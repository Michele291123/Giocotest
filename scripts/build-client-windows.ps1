# Build del CLIENT per Windows (x64). Produce un singolo .exe autonomo.
#
# Uso (PowerShell, dalla cartella del progetto):
#     ./scripts/build-client-windows.ps1
# Output: dist/client/ClayPigeonClient.exe
#
# Avvio del client:
#     ./dist/client/ClayPigeonClient.exe <ip-del-pi> 5055

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

Write-Host ">> Publish Client per win-x64 ..."
dotnet publish src/Client/Client.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist/client

Write-Host ">> Fatto: dist/client/ClayPigeonClient.exe"
Write-Host ">> Avvia con:  ./dist/client/ClayPigeonClient.exe <ip-del-pi> 5055"
