<#
.SYNOPSIS
    Respalda a Google Drive lo que no vive en GitHub, sin exponer .git a la sincronizacion.

.DESCRIPTION
    El repositorio es publico, asi que GitHub no puede ser la copia de seguridad de todo:
    los ficheros locales con valores reales (*.local.md, appsettings.Development.json)
    estan deliberadamente fuera del control de versiones. Y sincronizar la carpeta de
    trabajo entera contra Drive es peor que no hacer nada: el cliente de Drive escribe
    dentro de .git mientras git escribe en el mismo sitio, que es una via conocida de
    corrupcion del historial, y de paso sube 15 GB de .claude/worktrees y de binarios
    de compilacion que se regeneran solos.

    Este script invierte el planteamiento: Drive no toca la carpeta de trabajo. Se le
    entregan estos artefactos cerrados por ejecucion:

      1. hydra-<fecha>.bundle          - el repositorio publico entero (todas las ramas)
                                          en un solo fichero, verificado antes de
                                          publicarse. Se restaura con `git clone`.
      2. hydra-negocio-<fecha>.bundle  - el repositorio de documentacion de negocio
                                          (docs/business/ hasta 2026-08-13, ahora vive
                                          aparte y SIN remoto — ver CLAUDE.md). Esta
                                          copia es su UNICA redundancia fuera del disco
                                          local: a diferencia del repo publico, no hay
                                          GitHub detras que lo respalde. Se omite en
                                          silencio si $RaizNegocio no existe.
      3. locales-<fecha>.zip           - los ficheros ignorados del repo publico que si
                                          importan, mas un parche con el trabajo sin
                                          commitear (que ningun bundle ve).

    Cada artefacto se escribe con extension .part y solo se renombra al terminar, para
    que Drive nunca sincronice un fichero a medio escribir.

.PARAMETER Destino
    Carpeta de Drive donde se dejan los artefactos.

.PARAMETER RaizNegocio
    Repositorio local (sin remoto) con la documentacion de negocio. Si no existe en esta
    maquina, ese bundle simplemente no se genera — no es un error.

.PARAMETER Conservar
    Cuantas copias de cada tipo se mantienen. Las mas antiguas se borran.

.PARAMETER IncluirDatosSubidos
    Incluye src/CaeManager.Web/App_Data (documentos subidos). Apagado a proposito: son
    datos personales de trabajadores, y una cuenta personal de Google Drive no figura
    como subencargado en RGPD-TRATAMIENTO-DATOS.md. Actívalo solo si esa carpeta
    contiene unicamente datos de prueba.

.EXAMPLE
    pwsh scripts/respaldo-local.ps1
    powershell -ExecutionPolicy Bypass -File scripts\respaldo-local.ps1 -Conservar 30
#>
[CmdletBinding()]
param(
    [string]$Destino = "G:\Mi unidad\Hydra-Respaldos",
    [string]$RaizNegocio = "C:\Users\chris\Project-Hydra-Negocio",
    [int]$Conservar = 14,
    [switch]$IncluirDatosSubidos
)

$ErrorActionPreference = "Stop"

$raiz = Split-Path -Parent $PSScriptRoot
$marca = Get-Date -Format "yyyy-MM-dd_HHmm"
$temporal = Join-Path $env:TEMP "hydra-respaldo-$marca"

function Escribir($mensaje) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $mensaje"
}

# Reutilizada para el repo publico y para el de negocio: mismo criterio de verificacion
# para ambos, un bundle que no pasa `git bundle verify` no se publica nunca.
function Generar-Bundle([string]$RaizRepo, [string]$Prefijo, [string]$CarpetaTemporal) {
    $destinoBundle = Join-Path $CarpetaTemporal "$Prefijo-$marca.bundle"
    Push-Location $RaizRepo
    try {
        # Sin 2>&1: en Windows PowerShell 5.1 redirigir la salida de error de un
        # ejecutable nativo envuelve cada linea en un ErrorRecord y, con
        # $ErrorActionPreference = "Stop", aborta el script aunque git haya terminado
        # con codigo 0. git escribe su progreso por stderr, asi que aqui pasaria siempre.
        git bundle create $destinoBundle --all HEAD | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git bundle create ($Prefijo) fallo con codigo $LASTEXITCODE" }

        git bundle verify $destinoBundle | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "El bundle de $Prefijo no supera 'git bundle verify'" }

        $ramasRepo = (git for-each-ref --format="%(refname:short)" refs/heads | Measure-Object).Count
        $commitsRepo = (git rev-list --all --count)
    }
    finally { Pop-Location }
    return [PSCustomObject]@{
        Ruta    = $destinoBundle
        Ramas   = $ramasRepo
        Commits = $commitsRepo
        MB      = [math]::Round((Get-Item $destinoBundle).Length / 1MB, 1)
    }
}

if (-not (Test-Path (Join-Path $raiz ".git"))) {
    throw "No parece un repositorio git: $raiz"
}

# Se comprueba la unidad de Drive antes de trabajar: si no esta montada, fallar aqui es
# mejor que generar los artefactos y no poder colocarlos.
$unidad = Split-Path -Qualifier $Destino
if (-not (Test-Path $unidad)) {
    throw "La unidad de Google Drive ($unidad) no esta montada. Arranca Drive para escritorio y reintenta."
}
if (-not (Test-Path $Destino)) {
    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    Escribir "Creada la carpeta de destino: $Destino"
}
New-Item -ItemType Directory -Path $temporal -Force | Out-Null

try {
    # --- 1. Los repositorios ---------------------------------------------------------
    # --all incluye ramas, etiquetas y notas; HEAD entra aparte porque --all no lo
    # arrastra y sin el un `git clone` del bundle no sabe que rama sacar.
    Escribir "Generando el bundle del repositorio publico..."
    $infoHydra = Generar-Bundle -RaizRepo $raiz -Prefijo "hydra" -CarpetaTemporal $temporal
    $bundle = $infoHydra.Ruta
    $ramas = $infoHydra.Ramas
    $commits = $infoHydra.Commits
    Escribir "Bundle correcto: $ramas ramas, $commits commits, $($infoHydra.MB) MB"

    $bundleNegocio = $null
    if (Test-Path (Join-Path $RaizNegocio ".git")) {
        Escribir "Generando el bundle del repositorio de negocio (sin remoto)..."
        $infoNegocio = Generar-Bundle -RaizRepo $RaizNegocio -Prefijo "hydra-negocio" -CarpetaTemporal $temporal
        $bundleNegocio = $infoNegocio.Ruta
        Escribir "Bundle correcto: $($infoNegocio.Ramas) ramas, $($infoNegocio.Commits) commits, $($infoNegocio.MB) MB"
    }
    else {
        Escribir "Repositorio de negocio no encontrado en $RaizNegocio - se omite ese bundle."
    }

    # --- 2. Lo que el repositorio no guarda ----------------------------------------
    $areaLocal = Join-Path $temporal "locales"
    New-Item -ItemType Directory -Path $areaLocal -Force | Out-Null

    # Ficheros ignorados a proposito pero que cuesta o es imposible regenerar.
    $patrones = @(
        "*.local.md",
        "src\CaeManager.Web\appsettings.Development.json",
        ".claude\settings.local.json",
        ".claude\launch.json"
    )
    $copiados = 0
    foreach ($patron in $patrones) {
        $encontrados = Get-ChildItem -Path (Join-Path $raiz $patron) -Force -ErrorAction SilentlyContinue
        foreach ($f in $encontrados) {
            $relativa = $f.FullName.Substring($raiz.Length).TrimStart('\')
            $destinoF = Join-Path $areaLocal $relativa
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinoF) -Force | Out-Null
            Copy-Item $f.FullName $destinoF -Force
            $copiados++
        }
    }

    # El bundle solo contiene lo commiteado. Sin esto, una sesion de trabajo sin cerrar
    # se pierde entera: el escenario mas probable de perdida real, no el disco muerto.
    Push-Location $raiz
    try {
        $parche = Join-Path $areaLocal "cambios-sin-commitear.patch"
        git diff HEAD > $parche
        if ((Get-Item $parche).Length -eq 0) { Remove-Item $parche -Force }

        # Los ficheros nuevos todavia sin `git add` no salen en `git diff HEAD`.
        $nuevos = git ls-files --others --exclude-standard
        foreach ($n in $nuevos) {
            $origen = Join-Path $raiz $n
            if (-not (Test-Path $origen)) { continue }
            $destinoN = Join-Path (Join-Path $areaLocal "sin-seguimiento") $n
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinoN) -Force | Out-Null
            Copy-Item $origen $destinoN -Force
            $copiados++
        }
    }
    finally { Pop-Location }

    if ($IncluirDatosSubidos) {
        $datos = Join-Path $raiz "src\CaeManager.Web\App_Data"
        if (Test-Path $datos) {
            Escribir "AVISO: incluyendo App_Data (documentos subidos) por peticion explicita."
            Copy-Item $datos (Join-Path $areaLocal "App_Data") -Recurse -Force
        }
    }

    $zip = Join-Path $temporal "locales-$marca.zip"
    Compress-Archive -Path (Join-Path $areaLocal "*") -DestinationPath $zip -Force
    Escribir "Ficheros locales empaquetados: $copiados elementos"

    # --- 3. Publicar en Drive ------------------------------------------------------
    # .part primero y rename despues: Drive sincroniza lo que ve, y lo que ve tiene que
    # estar completo o no estar.
    $artefactos = @($bundle, $zip)
    if ($bundleNegocio) { $artefactos += $bundleNegocio }
    foreach ($artefacto in $artefactos) {
        $nombre = Split-Path -Leaf $artefacto
        $parcial = Join-Path $Destino "$nombre.part"
        Copy-Item $artefacto $parcial -Force
        Move-Item $parcial (Join-Path $Destino $nombre) -Force
        Escribir "Publicado: $nombre"
    }

    # --- 4. Rotacion ---------------------------------------------------------------
    # hydra-negocio-*.bundle no colisiona con hydra-*.bundle: el patron exige que
    # justo despues de "hydra-" venga la marca de fecha (digitos), no "negocio".
    foreach ($tipo in @("hydra-????-??-??_*.bundle", "hydra-negocio-*.bundle", "locales-*.zip")) {
        $viejos = Get-ChildItem -Path (Join-Path $Destino $tipo) -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -Skip $Conservar
        foreach ($v in $viejos) {
            Remove-Item $v.FullName -Force
            Escribir "Rotado (borrado): $($v.Name)"
        }
    }

    # --- 5. Instrucciones junto a la copia ------------------------------------------
    # El dia que hagan falta, la maquina donde vivia este script puede no existir. Las
    # instrucciones tienen que estar dentro de Drive, no en el repositorio que se
    # intenta restaurar.
    $comoRestaurar = @"
COMO RESTAURAR ESTA COPIA
=========================
Generado por scripts/respaldo-local.ps1. Ultima actualizacion: $(Get-Date -Format 'yyyy-MM-dd HH:mm')

1. EL REPOSITORIO PUBLICO (hydra-<fecha>.bundle)

   git -c core.longpaths=true clone hydra-<fecha>.bundle C:\Project-Hydra

   El -c core.longpaths=true NO es opcional en Windows: este repositorio tiene rutas
   que pasan de 260 caracteres (los directorios por Command/Query del patron CQRS) y
   sin esa opcion el clon deja el arbol de trabajo a medias con "Filename too long".
   Comprobado: con la opcion, 1.667 archivos y arbol limpio; sin ella, falla.

   Restaura en una ruta CORTA (C:\Project-Hydra, no dentro de Documentos\...\algo).

   El bundle contiene todas las ramas y etiquetas. Para verlo antes de clonar:
     git bundle verify hydra-<fecha>.bundle
     git bundle list-heads hydra-<fecha>.bundle

1b. EL REPOSITORIO DE NEGOCIO (hydra-negocio-<fecha>.bundle, si existe)

   git clone hydra-negocio-<fecha>.bundle C:\Users\chris\Project-Hydra-Negocio

   Este repositorio nunca tuvo remoto (ver CLAUDE.md del repositorio publico, seccion
   "Documentos que hay que leer segun la tarea"): antes de este cambio del 2026-08-13
   vivia como docs/business/ dentro del repositorio publico; ahora es privado y esta
   copia en Drive es su UNICA redundancia. Si este bundle falta o esta corrupto y la
   maquina original ya no existe, ese contenido se ha perdido.

2. LOS FICHEROS LOCALES DEL REPOSITORIO PUBLICO (locales-<fecha>.zip)

   Descomprimir SOBRE la carpeta ya restaurada, respetando la estructura:
     - RUNBOOK-GRAPH-M365.local.md ......... identificadores reales del tenant M365
     - src/CaeManager.Web/appsettings.Development.json
     - .claude/ ............................ configuracion de las sesiones de agente
     - cambios-sin-commitear.patch ......... trabajo en curso al hacer la copia:
                                             git apply cambios-sin-commitear.patch
     - sin-seguimiento/ .................... ficheros que aun no estaban en git;
                                             copiar a mano a su ruta equivalente

3. QUE NO ESTA AQUI

   - Los documentos subidos (App_Data): excluidos a proposito, son datos personales
     de trabajadores y esta cuenta de Drive no figura como subencargado en
     RGPD-TRATAMIENTO-DATOS.md. Se respaldan por la via que decida ese documento.
   - Los secretos de produccion (variables de entorno de Railway, secreto de cliente
     de Graph): nunca han estado en disco. Ver DEPLOY.md y RUNBOOK-CLAVES.md.
   - node_modules, bin, obj, .claude/worktrees: se regeneran solos.
"@
    Set-Content -Path (Join-Path $Destino "COMO-RESTAURAR.txt") -Value $comoRestaurar -Encoding utf8

    $negocioLog = if ($bundleNegocio) { "SI ($($infoNegocio.MB)MB, $($infoNegocio.Commits) commits)" } else { "no" }
    $linea = "{0}  bundle={1}MB  ramas={2}  commits={3}  locales={4}  negocio={5}" -f `
        (Get-Date -Format "yyyy-MM-dd HH:mm"),
        [math]::Round((Get-Item $bundle).Length / 1MB, 1), $ramas, $commits, $copiados, $negocioLog
    Add-Content -Path (Join-Path $Destino "historial.log") -Value $linea -Encoding utf8

    Escribir "Respaldo completado en: $Destino"
}
finally {
    Remove-Item $temporal -Recurse -Force -ErrorAction SilentlyContinue
}
