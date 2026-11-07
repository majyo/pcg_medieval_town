param(
    [ValidateSet('build', 'run', 'test', 'capture', 'sample', 'test-sampling', 'test-settlement', 'test-roads', 'test-road-grading', 'test-road-grading-ui', 'test-grading-limits', 'test-road-sweep', 'test-road-construction', 'export-town', 'benchmark-road-construction', 'test-parcels', 'test-medieval', 'test-regional', 'capture-landscape', 'test-buildings', 'capture-buildings', 'test-building-ui', 'test-road-geometry-ui', 'test-placement', 'test-placement-ui', 'capture-placement', 'test-terrain-visual', 'test-terrain-visual-ui', 'capture-terrain', 'benchmark-terrain', 'test-city-visual', 'test-city-visual-ui', 'capture-city', 'benchmark-city', 'preview-city')]
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
        'preview-city' { & $GodotPath --path $projectRoot -- --preview-city }
        'test' {
            & $GodotPath --headless --path $projectRoot -- --validate-terrain-visual
            if ($LASTEXITCODE -ne 0) { throw 'Terrain visual validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-map
            if ($LASTEXITCODE -ne 0) { throw 'Map validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-environment
            if ($LASTEXITCODE -ne 0) { throw 'Sampling validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-settlement
            if ($LASTEXITCODE -ne 0) { throw 'Settlement validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-roads
            if ($LASTEXITCODE -ne 0) { throw 'Road validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-road-grading
            if ($LASTEXITCODE -ne 0) { throw 'Road grading validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-road-construction
            if ($LASTEXITCODE -ne 0) { throw 'Road construction validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-parcels
            if ($LASTEXITCODE -ne 0) { throw 'Parcel validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-buildings
            if ($LASTEXITCODE -ne 0) { throw 'Building validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-placement
            if ($LASTEXITCODE -ne 0) { throw 'Placement validation failed.' }
            & $GodotPath --headless --path $projectRoot -- --validate-city-visual
        }
        'test-terrain-visual' { & $GodotPath --headless --path $projectRoot -- --validate-terrain-visual }
        'test-city-visual' { & $GodotPath --headless --path $projectRoot -- --validate-city-visual }
        'test-city-visual-ui' { & $GodotPath --path $projectRoot -- --ui-city-visual }
        'capture-city' { & $GodotPath --path $projectRoot -- --capture-city=artifacts/city_visual/after }
        'benchmark-city' { & $GodotPath --path $projectRoot -- --capture-city=artifacts/city_visual/after --benchmark-city }
        'test-terrain-visual-ui' { & $GodotPath --path $projectRoot -- --ui-terrain-visual }
        'capture-terrain' { & $GodotPath --path $projectRoot -- --capture-terrain=artifacts/terrain_visual/after }
        'benchmark-terrain' { & $GodotPath --path $projectRoot -- --capture-terrain=artifacts/terrain_visual/after --benchmark-terrain }
        'test-buildings' { & $GodotPath --headless --path $projectRoot -- --validate-buildings }
        'test-regional' { & $GodotPath --headless --path $projectRoot -- --validate-regional }
        'test-medieval' { & $GodotPath --headless --path $projectRoot -- --validate-medieval }
        'test-parcels' { & $GodotPath --headless --path $projectRoot -- --validate-parcels }
        'test-roads' { & $GodotPath --headless --path $projectRoot -- --validate-roads }
        'test-road-construction' { & $GodotPath --headless --path $projectRoot -- --validate-road-construction }
        'benchmark-road-construction' { & $GodotPath --path $projectRoot -- --capture-city=artifacts/road_construction --benchmark-road-construction }
        'export-town' { & $GodotPath --path $projectRoot -- --export-town }
        'test-road-sweep' { & $GodotPath --headless --path $projectRoot -- --validate-road-sweep }
        'test-grading-limits' { & $GodotPath --headless --path $projectRoot -- --validate-grading-limits }
        'test-road-grading' { & $GodotPath --headless --path $projectRoot -- --validate-road-grading }
        'test-road-grading-ui' { & $GodotPath --path $projectRoot -- --ui-road-grading }
        'test-settlement' { & $GodotPath --headless --path $projectRoot -- --validate-settlement }
        'sample' { & $GodotPath --headless --path $projectRoot -- --sample-environment }
        'test-sampling' { & $GodotPath --headless --path $projectRoot -- --validate-environment }
        'test-building-ui' { & $GodotPath --path $projectRoot -- --ui-buildings }
        'test-placement' { & $GodotPath --headless --path $projectRoot -- --validate-placement }
        'test-placement-ui' { & $GodotPath --path $projectRoot -- --ui-placement }
        'capture-placement' { & $GodotPath --path $projectRoot -- --capture-placement }
        'test-road-geometry-ui' { & $GodotPath --path $projectRoot -- --ui-road-geometry }
        'capture-buildings' { & $GodotPath --path $projectRoot -- --capture-buildings }
        'capture-landscape' { & $GodotPath --path $projectRoot -- --capture-landscape }
        'capture' {
            $captureDirectory = Join-Path $projectRoot 'artifacts\captures'
            & $GodotPath --path $projectRoot -- --ui-smoke "--capture-dir=$captureDirectory"
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "Godot $Action failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
