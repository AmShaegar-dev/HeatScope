$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) { $env:PATH = "$dotnetDir;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$version = if ($env:VERSION) { $env:VERSION } else { '0.1.1' }

Write-Host 'Публикация Linux-бинарника (linux-x64, self-contained)...' -ForegroundColor Cyan
& dotnet publish 'src\HeatScope.Linux\HeatScope.Linux.csproj' -c Release -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o dist-linux
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась (код $LASTEXITCODE)" }

$stage = Join-Path $root 'build\linux-tar'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $root 'release') | Out-Null

Copy-Item 'dist-linux\HeatScope' $stage
Copy-Item 'packaging\linux\install.sh' $stage
Copy-Item 'packaging\linux\heatscope.desktop' $stage
Copy-Item 'packaging\linux\heatscope.png' $stage
Copy-Item 'README-LINUX.md' $stage

$tar = Join-Path $root "release\HeatScope-$version-linux-x64.tar.gz"
if (Test-Path $tar) { Remove-Item $tar -Force }
& tar.exe -czf $tar -C $stage .
if ($LASTEXITCODE -ne 0) { throw "tar завершился с кодом $LASTEXITCODE" }

$size = [math]::Round((Get-Item $tar).Length / 1MB, 1)
Write-Host "Готово: $tar ($size МБ)" -ForegroundColor Green
