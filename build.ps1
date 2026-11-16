$ErrorActionPreference = 'Stop'

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (-not (Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) {
    Write-Host 'Устанавливаю .NET 8 SDK...' -ForegroundColor Cyan
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $dotnetDir -NoPath
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -Runtime windowsdesktop -InstallDir $dotnetDir -NoPath
}

$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host 'Сборка HeatScope...' -ForegroundColor Cyan
& dotnet publish 'src\HeatScope\HeatScope.csproj' -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o dist

if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась (код $LASTEXITCODE)" }

$exe = Join-Path $root 'dist\HeatScope.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green
} else {
    throw 'exe не найден'
}
