param(
    [ValidateSet('build', 'run', 'test', 'capture')]
    [string]$Action = 'run',
    [string]$GodotPath = 'E:\workspace\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & dotnet build PcgCitySandbox.csproj
    if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
    if ($Action -eq 'build') { return }
    if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Godot not found: $GodotPath" }
    switch ($Action) {
        'run' { & $GodotPath --path $projectRoot }
        'test' {
            & $GodotPath --headless --path $projectRoot -- --test
            if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
        }
        'capture' { & $GodotPath --path $projectRoot -- --capture=artifacts/capture }
    }
}
finally {
    Pop-Location
}
