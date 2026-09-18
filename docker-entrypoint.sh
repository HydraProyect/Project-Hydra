#!/bin/sh
set -e

# El volumen persistente de Railway (/data) puede venir de antes de que la
# imagen empezara a correr como usuario no-root (P2 #25 de
# docs/business/MATURITY_REVIEW.md, ver también DEPLOY.md § 2). Sin este
# chown, dataprotection-keys/ y documentos/ quedan sin permiso de escritura
# para $APP_UID y el arranque falla en cascada: el keyring de DataProtection
# no se puede leer -> el antiforgery token no se puede cifrar/descifrar ->
# excepción sin capturar en cualquier página con formulario (incluido el
# login). Reproducido en producción el 2026-08-02.
#
# El chequeo de propietario antes de correr chown evita repetir un chown -R
# recursivo (potencialmente caro si documentos/ tiene muchos PDFs locales)
# en cada arranque una vez que el volumen ya quedó corregido.
if [ -d /data ]; then
  propietario_actual="$(stat -c '%u' /data)"
  if [ "$propietario_actual" != "$APP_UID" ]; then
    chown -R "$APP_UID":"$APP_UID" /data
  fi
fi

# Job efímero (REC-017/P39, ver docker-compose.*.yml servicio "migrador"):
# cuando el `command:` de Compose trae argumentos ($# > 0, hoy solo
# "--migrate-only"), se reenvían tal cual en vez del arranque normal de
# Kestrel — mismo chown y misma bajada de privilegios de arriba, porque
# MigrarBaseDeDatosAsync (Program.cs) resuelve el mismo IDataProtectionProvider
# que el servidor y necesita el mismo acceso a /data. El arranque normal (sin
# `command:`, $# = 0) no cambia.
if [ "$#" -gt 0 ]; then
  exec gosu "$APP_UID":"$APP_UID" dotnet CaeManager.Web.dll "$@"
fi

exec gosu "$APP_UID":"$APP_UID" dotnet CaeManager.Web.dll --urls "http://+:${PORT:-8080}"
