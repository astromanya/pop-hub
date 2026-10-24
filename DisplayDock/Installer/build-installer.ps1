# DisplayDock: yayınla (self-contained) + Inno Setup ile kurulum dosyası üret.
# Kullanım:  pwsh Installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'bin\publish'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# .NET ve Windows App SDK uygulamayla birlikte gelir: hedef PC'de ek runtime kurulumu gerekmez.
dotnet publish "$root\DisplayDock.csproj" -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish başarısız.' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 bulunamadı (winget install JRSoftware.InnoSetup).' }

& $iscc "$PSScriptRoot\DisplayDock.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC başarısız.' }

Get-ChildItem "$root\bin\installer\*.exe" | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
