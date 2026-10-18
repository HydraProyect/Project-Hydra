<#
.SYNOPSIS
    Preflight local antes de abrir una PR: lo que el CI rechazaria despues, aqui y ahora.

.DESCRIPTION
    Espeja .github/workflows/ci.yml (jobs "compilacion-y-unitarios", "formato" y
    "bunit-tests"), sin anadir pasos propios:
      1. dotnet build-server shutdown (sin servidores de compilacion viejos).
      2. dotnet build CaeManager.slnx -warnaserror -nodeReuse:false. Compila TODA la solucion,
         incluidos tests/CaeManager.IntegrationTests y tests/CaeManager.E2ETests (solo compilar).
      3. dotnet format CaeManager.slnx --no-restore --verify-no-changes (mismas banderas del CI).
      4. Tests Domain, Application, Architecture y Web (bUnit). Con -Rapido: Architecture mas los
         proyectos de test tocados (carpeta de tests o su proyecto de src, por git diff
         origin/main...HEAD y cambios sin commitear); si cambia un fichero de build global
         (*.props, *.targets, *.slnx, .config) corren todos.
      5. Pista de E2E: clases de tests/CaeManager.E2ETests que mencionan las rutas @page o los
         ficheros tocados. No las ejecuta.
    Nunca ejecuta la suite de integracion (necesita PostgreSQL y el turno de la base).
    Los tests corren con --no-build sobre lo compilado en el paso 2.

    Salida: PASA / FALLA / OMITIDO por paso y codigo de salida distinto de 0 si algo falla.
    El log completo de cada paso queda en %TEMP%\preflight\.

.PARAMETER Rapido
    Solo Architecture mas los proyectos de test tocados respecto a origin/main.

.EXAMPLE
    pwsh -File scripts/preflight.ps1 -Rapido
    powershell -ExecutionPolicy Bypass -File scripts\preflight.ps1
#>
param([switch]$Rapido)

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)
$logs = Join-Path $env:TEMP 'preflight'
New-Item -ItemType Directory -Force $logs | Out-Null
$resultados = New-Object System.Collections.ArrayList

function Invoke-Paso([string]$Nombre, [string]$Comando) {
    $log = Join-Path $logs (($Nombre -replace '[^\w]+', '-') + '.log')
    $reloj = [Diagnostics.Stopwatch]::StartNew()
    cmd.exe /c "$Comando > `"$log`" 2>&1"
    $ok = ($LASTEXITCODE -eq 0)
    $s = [int]$reloj.Elapsed.TotalSeconds
    [void]$resultados.Add([pscustomobject]@{ Paso = $Nombre; Estado = $(if ($ok) { 'PASA' } else { 'FALLA' }); Segundos = $s })
    if (-not $ok) {
        Write-Host "--- $Nombre : FALLA (log: $log)" -ForegroundColor Red
        Get-Content $log | Select-String -Pattern 'error|Failed|Con error' |
            ForEach-Object { $_.Line.Trim() } | Select-Object -Unique -First 25 | ForEach-Object { Write-Host "    $_" }
    } else { Write-Host "OK  $Nombre ($s s)" -ForegroundColor Green }
    return $ok
}

function Add-Omitido([string]$Nombre, [string]$Motivo) {
    [void]$resultados.Add([pscustomobject]@{ Paso = $Nombre; Estado = "OMITIDO ($Motivo)"; Segundos = 0 })
}

# Ficheros tocados: rama respecto a origin/main mas lo aun sin commitear (tambien ficheros nuevos).
$tocados = @(git diff --name-only origin/main...HEAD) + @(git diff --name-only HEAD) + @(git ls-files --others --exclude-standard) |
    Where-Object { $_ } | Sort-Object -Unique

# 1-2. Servidores de compilacion y build de toda la solucion
[void](Invoke-Paso 'build-server shutdown' 'dotnet build-server shutdown')
$compila = Invoke-Paso 'Compilar solucion (-warnaserror)' 'dotnet build CaeManager.slnx -warnaserror -nodeReuse:false'

# 3. Formato, igual que el job "formato" del CI (tras compilar ya esta restaurado)
[void](Invoke-Paso 'Formato (dotnet format)' 'dotnet format CaeManager.slnx --no-restore --verify-no-changes')

# 4. Tests unitarios
$proyectos = [ordered]@{
    'Domain'       = @('tests/CaeManager.Domain.Tests', 'src/CaeManager.Domain')
    'Application'  = @('tests/CaeManager.Application.Tests', 'src/CaeManager.Application')
    'Architecture' = @('tests/CaeManager.Architecture.Tests', $null)
    'Web'          = @('tests/CaeManager.Web.Tests', 'src/CaeManager.Web')
}
$globales = [bool]($tocados | Where-Object { $_ -match '(^|/)(Directory\.[^/]+|[^/]+\.(props|targets|slnx))$|^\.config/' })
foreach ($nombre in $proyectos.Keys) {
    $carpeta, $fuente = $proyectos[$nombre]
    $paso = "Tests de $nombre"
    $toca = $globales -or [bool]($tocados | Where-Object { $_.StartsWith("$carpeta/") -or ($fuente -and $_.StartsWith("$fuente/")) })
    if ($Rapido -and $nombre -ne 'Architecture' -and -not $toca) { Add-Omitido $paso '-Rapido: sin cambios'; continue }
    if (-not $compila) { Add-Omitido $paso 'no compila'; continue }
    [void](Invoke-Paso $paso "dotnet test $carpeta --no-build")
}

# 5. Pista de E2E (no se ejecutan): clases que mencionan rutas @page o ficheros tocados
$e2e = Get-ChildItem tests/CaeManager.E2ETests -Filter *.cs -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$pistas = New-Object System.Collections.Generic.HashSet[string]
foreach ($f in ($tocados | Where-Object { $_ -match '\.(razor|cs)$' -and (Test-Path $_) })) {
    if ($f -like 'tests/CaeManager.E2ETests/*') { [void]$pistas.Add($f); continue }
    # Pagina con @page: sus rutas (las que los E2E navegan). Resto de ficheros: su nombre como palabra.
    $patrones = @()
    if ($f -like '*.razor') {
        foreach ($m in (Select-String -Path $f -Pattern '^@page\s+"([^"{]+)')) {
            $r = $m.Matches[0].Groups[1].Value.TrimEnd('/')
            if ($r.Length -ge 3) { $patrones += [regex]::Escape($r) + '\b' }
        }
    }
    if ($patrones.Count -eq 0) { $patrones = @('\b' + [IO.Path]::GetFileNameWithoutExtension($f) + '\b') }
    foreach ($p in $patrones) {
        $e2e | Select-String -Pattern $p -CaseSensitive -List | ForEach-Object { [void]$pistas.Add($_.Path.Substring((Get-Location).Path.Length + 1).Replace('\', '/')) }
    }
}Write-Host "`nE2E posiblemente afectados (pista, no se ejecutan):"
if ($pistas.Count -eq 0) { Write-Host '    ninguno detectado' } else { $pistas | Sort-Object | ForEach-Object { Write-Host "    $_" } }

# 6. Resumen
Write-Host "`n=== Resumen preflight$(if ($Rapido) { ' (-Rapido)' }) ==="
$resultados | ForEach-Object { '{0,-36} {1,-34} {2,5} s' -f $_.Paso, $_.Estado, $_.Segundos } | Write-Host
$fallos = @($resultados | Where-Object { $_.Estado -eq 'FALLA' }).Count
if ($fallos -gt 0) { Write-Host "`nPREFLIGHT: FALLA ($fallos paso/s)" -ForegroundColor Red; exit 1 }
Write-Host "`nPREFLIGHT: PASA" -ForegroundColor Green
exit 0