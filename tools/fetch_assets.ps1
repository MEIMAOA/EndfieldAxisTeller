# Download the Endfield operator list page + icons, then build Assets/characters.json.
# PowerShell does the network part (python sometimes can't reach the internet behind proxies).
param(
  [string]$Python = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Resolve-Python {
  if ($Python -and (Test-Path $Python)) { return $Python }
  foreach ($candidate in @('python', 'py')) {
    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
  }
  throw 'python not found. Install Python 3 with Pillow, or pass -Python <path-to-python.exe>'
}

$py = Resolve-Python
Write-Host "using python: $py"

$work = Join-Path $env:TEMP 'ef_assets'
New-Item -ItemType Directory -Force -Path $work, (Join-Path $work 'icons') | Out-Null
$page = Join-Path $work 'operators.html'

Write-Host '1) downloading operator list page ...'
Invoke-WebRequest -Uri 'https://endfield.ooo.run/zh/operators' -OutFile $page -UseBasicParsing `
  -TimeoutSec 60 -Headers @{ 'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126.0 Safari/537.36' }

Write-Host '2) parsing page ...'
& $py (Join-Path $root 'tools\build_catalog.py') parse $page $work
if ($LASTEXITCODE -ne 0) { throw 'parse failed' }

Write-Host '3) downloading icons ...'
$urls = Get-Content (Join-Path $work 'icon_urls.txt') -Encoding UTF8
foreach ($u in $urls) {
  $name = [IO.Path]::GetFileNameWithoutExtension(($u -split '\?')[0])
  $dst = Join-Path $work ('icons\' + $name + '.webp')
  if (Test-Path $dst) { continue }
  try {
    Invoke-WebRequest -Uri $u -OutFile $dst -UseBasicParsing -TimeoutSec 40 `
      -Headers @{ 'User-Agent' = 'Mozilla/5.0'; 'Referer' = 'https://endfield.ooo.run/zh/operators' }
  } catch {
    Write-Warning ("icon failed: {0} - {1}" -f $u, $_.Exception.Message)
  }
}
Write-Host ("   downloaded {0} icons" -f (Get-ChildItem (Join-Path $work 'icons')).Count)

Write-Host '4) converting icons and writing characters.json ...'
& $py (Join-Path $root 'tools\build_catalog.py') finalize $work
if ($LASTEXITCODE -ne 0) { throw 'finalize failed' }
Write-Host 'done.'
