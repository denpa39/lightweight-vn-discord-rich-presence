param([switch]$Test)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskLocal=Join-Path $taskRoot '.rust-tools'
if(Test-Path (Join-Path $taskLocal 'cargo/bin/cargo.exe')) {
    $env:RUSTUP_HOME=Join-Path $taskLocal 'rustup'
    $env:CARGO_HOME=Join-Path $taskLocal 'cargo'
    $taskCargo=Join-Path $taskLocal 'cargo/bin/cargo.exe'
} else {$taskCargo='cargo'}
$taskManifest=Join-Path $PSScriptRoot 'Cargo.toml'
if($Test) {& $taskCargo test --manifest-path $taskManifest --locked; if($LASTEXITCODE){exit $LASTEXITCODE}}
& $taskCargo build --manifest-path $taskManifest --release --locked
if($LASTEXITCODE){exit $LASTEXITCODE}
$taskDist=Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'target/release/vn-presence.exe') -Destination (Join-Path $taskDist 'VN Presence.exe')
Write-Output (Join-Path $taskDist 'VN Presence.exe')
