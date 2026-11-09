param(
    [ValidateSet('build', 'run', 'test', 'capture')]
    [string]$Action = 'run',
    [string]$GodotPath = 'E:\workspace\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe',
    # run / capture only: map seed instead of the one in configs/temperate_plain.tres (capture then saves to artifacts/capture_<seed>).
    [int]$Seed = -1
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

# Which components each component may reference (folder under scripts/ -> namespace).
# Files directly under scripts/ are the composition root and may reference everything.
$components = [ordered]@{
    terrain   = @{ Namespace = 'Terrain';       Allowed = @() }
    city      = @{ Namespace = 'City';          Allowed = @('Terrain') }
    camera    = @{ Namespace = 'CameraControl'; Allowed = @() }
    rendering = @{ Namespace = 'Rendering';     Allowed = @('Terrain', 'City') }
    ui        = @{ Namespace = 'Ui';            Allowed = @('City') }
}
$sceneTypes = '\b(Node|Node3D|Control|Camera3D|CanvasLayer|MeshInstance3D|SceneTree|GetTree)\b'

function Test-Dependencies {
    $violations = @()
    foreach ($folder in $components.Keys) {
        $rule = $components[$folder]
        $allowed = @($rule.Namespace) + $rule.Allowed
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot "scripts/$folder") -Recurse -Filter *.cs) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            $relative = $file.FullName.Substring($projectRoot.Length + 1)
            foreach ($match in [regex]::Matches($text, 'PcgCitySandbox\.(\w+)')) {
                $target = $match.Groups[1].Value
                if ($allowed -notcontains $target) { $violations += "${relative}: $folder must not use $target" }
            }
            if ($text -cnotmatch "namespace PcgCitySandbox\.$($rule.Namespace);") {
                $violations += "${relative}: expected namespace PcgCitySandbox.$($rule.Namespace)"
            }
            if ($folder -in 'terrain', 'city' -and $text -cmatch $sceneTypes) {
                $violations += "${relative}: generation code must not touch scene nodes ($($Matches[1]))"
            }
        }
    }
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "DEPENDENCY: $_" -ForegroundColor Red }
    if ($violations) { throw 'Component dependency check failed.' }
    Write-Host 'DEPENDENCIES OK'
}

Push-Location -LiteralPath $projectRoot
try {
    if ($Action -eq 'test') { Test-Dependencies }
    & dotnet build PcgCitySandbox.csproj
    if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
    if ($Action -eq 'build') { return }
    if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Godot not found: $GodotPath" }
    # GDExtensions (Terrain3D) load only once the editor has scanned the project, e.g. on a fresh clone.
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.godot/extension_list.cfg'))) {
        & $GodotPath --headless --path $projectRoot --import | Out-Null
    }
    $seedArgs = @(if ($Seed -ge 0) { "--seed=$Seed" })
    switch ($Action) {
        'run' { & $GodotPath --path $projectRoot -- $seedArgs }
        'test' {
            & $GodotPath --headless --path $projectRoot -- --test
            if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
        }
        'capture' {
            $directory = if ($Seed -ge 0) { "artifacts/capture_$Seed" } else { 'artifacts/capture' }
            & $GodotPath --path $projectRoot -- "--capture=$directory" $seedArgs
        }
    }
}
finally {
    Pop-Location
}
