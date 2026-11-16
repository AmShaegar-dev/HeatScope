$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$iscc = $null
foreach ($p in @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $p) { $iscc = $p; break }
}

if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

if (-not $iscc) {
    Write-Host 'Inno Setup не найден. Устанавливаю через winget...' -ForegroundColor Cyan
    & winget install --id JRSoftware.InnoSetup -e --source winget --accept-source-agreements --accept-package-agreements --silent
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}

if (-not $iscc) { throw 'Не удалось найти ISCC.exe (Inno Setup).' }

$dist = Join-Path $root 'dist\HeatScope.exe'
if (-not (Test-Path $dist)) { throw "Нет $dist — сначала соберите приложение (build.ps1)." }

New-Item -ItemType Directory -Force -Path (Join-Path $root 'release') | Out-Null
Write-Host "Компилирую установщик: $iscc" -ForegroundColor Cyan
& $iscc (Join-Path $root 'installer\HeatScope.iss')

if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
Get-ChildItem (Join-Path $root 'release') -Filter '*.exe' |
    Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} |
    Format-Table -AutoSize
