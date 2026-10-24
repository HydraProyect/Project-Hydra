#!/usr/bin/env bash
# Descarga la base libre «IP to City Lite» de DB-IP (formato MMDB) para la
# detección de inicios de sesión sospechosos (GeolocalizadorIpDbIp).
#
# Licencia de la base: Creative Commons Attribution 4.0 International, de
# DB-IP.com. Uso libre, también comercial, con atribución a DB-IP.com y un
# enlace a https://db-ip.com en las páginas que muestran o usan sus resultados.
# No necesita clave ni cuenta. Quien ejecuta este guion acepta esa licencia.
#
# No va en el repositorio ni en la imagen (unos 130 MB y cambia cada mes): se
# deja en el directorio que la aplicación ve como /data/geoip (el volumen de
# datos). La aplicación vuelve a abrir el fichero en unos minutos cuando cambia;
# si no está, sigue funcionando y marca el país como desconocido.
#
# Uso:   actualizar-base-geolocalizacion.sh <directorio-destino> [AAAA-MM]
# Mes:   por defecto el actual (UTC); si aún no está publicado, el anterior.
# Salida: <directorio-destino>/dbip-city-lite.mmdb, sustituido de forma atómica.
set -euo pipefail

destino="${1:?Falta el directorio de destino (el que la aplicación monta como /data/geoip)}"
mes="${2:-$(date -u +%Y-%m)}"
tamano_minimo=$((50 * 1024 * 1024))

trabajo="$(mktemp -d)"
trap 'rm -rf "$trabajo"' EXIT

descargar() {
    curl -fsSL --retry 3 --max-time 600 -o "$trabajo/base.mmdb.gz" \
        "https://download.db-ip.com/free/dbip-city-lite-$1.mmdb.gz"
}

if ! descargar "$mes"; then
    if [[ $# -ge 2 ]]; then
        echo "No se pudo descargar la base de $mes." >&2
        exit 1
    fi
    anterior="$(date -u -d "${mes}-01 -1 month" +%Y-%m)"
    echo "La base de $mes aún no está publicada; se usa la de $anterior." >&2
    descargar "$anterior"
fi

gunzip -t "$trabajo/base.mmdb.gz"
gunzip -c "$trabajo/base.mmdb.gz" > "$trabajo/base.mmdb"

tamano="$(stat -c %s "$trabajo/base.mmdb")"
if (( tamano < tamano_minimo )); then
    echo "La base descargada mide $tamano bytes: demasiado pequeña, no se instala." >&2
    exit 1
fi
# Marcador de metadatos del formato MMDB.
if ! LC_ALL=C grep -aq $'\xab\xcd\xefMaxMind.com' "$trabajo/base.mmdb"; then
    echo "El fichero descargado no tiene formato MMDB; no se instala." >&2
    exit 1
fi

mkdir -p "$destino"
chmod 0644 "$trabajo/base.mmdb"
mv -f "$trabajo/base.mmdb" "$destino/dbip-city-lite.mmdb.nuevo"
mv -f "$destino/dbip-city-lite.mmdb.nuevo" "$destino/dbip-city-lite.mmdb"
echo "Base de geolocalización instalada en $destino/dbip-city-lite.mmdb ($tamano bytes)."
