# Сборка релиза Lumen в папку dist:
#   Lumen-<версия>-portable.exe — один файл, .NET ставить не нужно
#   Lumen-<версия>-setup.exe    — установщик (нужен Inno Setup 6: https://jrsoftware.org/isdl.php)
#   SHA256SUMS.txt              — контрольные суммы
#
#   .\build.ps1                  версия из Lumen.App.csproj
#   .\build.ps1 -Version 1.2.0   своя версия (так делает GitHub Actions по тегу v1.2.0)
#   .\build.ps1 -NoInstaller     только переносная версия
param(
    [string]$Version,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Lumen.App\Lumen.App.csproj'
$dist = Join-Path $root 'dist'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Lumen $Version" -ForegroundColor Cyan

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

# Self-contained: среда .NET едет внутри exe. Обрезку (trimming) WPF не поддерживает.
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
            "-p:Version=$Version", '-nologo')

function Invoke-Publish([string]$output, [string[]]$extra) {
    & dotnet publish $project @common @extra -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой ($LASTEXITCODE)" }
}

# Переносная версия: всё, включая нативные библиотеки WPF, в одном сжатом exe.
# Библиотеки при первом запуске распаковываются в %TEMP%\.net — поэтому первый старт дольше.
Write-Host "`nПереносная версия..." -ForegroundColor Cyan
$portableDir = Join-Path $dist 'portable'
Invoke-Publish $portableDir @('-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
$portable = Join-Path $dist "Lumen-$Version-portable.exe"
Move-Item (Join-Path $portableDir 'Lumen.exe') $portable
Remove-Item $portableDir -Recurse -Force

if (-not $NoInstaller) {
    # Для установщика — без сжатия и распаковки: нативные библиотеки лежат рядом с exe,
    # а сжимает Inno Setup (LZMA2 жмёт лучше, чем сжатие single-file)
    Write-Host "`nУстановщик..." -ForegroundColor Cyan
    $appDir = Join-Path $dist 'app'
    Invoke-Publish $appDir @()

    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        throw "Не найден Inno Setup 6. Установите его (winget install JRSoftware.InnoSetup) или соберите без установщика: .\build.ps1 -NoInstaller"
    }

    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$appDir" "/DOutputDir=$dist" (Join-Path $root 'installer\Lumen.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с ошибкой ($LASTEXITCODE)" }
    Remove-Item $appDir -Recurse -Force
}

$files = Get-ChildItem $dist -Filter *.exe
$files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

Write-Host "`nГотово:" -ForegroundColor Green
$files | ForEach-Object { Write-Host ("  dist\{0}  ({1:N0} МБ)" -f $_.Name, ($_.Length / 1MB)) }
