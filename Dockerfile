# Imagen de despliegue de CAE Manager (Blazor Server). Ver Project-Hydra-Negocio/tecnico/DEPLOY.md para
# variables de entorno necesarias (rutas de datos persistentes, credenciales
# del administrador inicial).

# Cadena de suministro (auditoría Módulo 10, 2026-08-30): tag flotante "10.0"
# resolvía a la última imagen del día del build, así que el mismo Dockerfile
# podía producir binarios distintos en CI y en el VPS. Fijado a la versión
# exacta MÁS el digest (comprobado contra mcr.microsoft.com, no copiado de un
# blog) — Dependabot (ver .github/dependabot.yml, ecosistema "docker") sigue
# proponiendo el salto de la imagen aspnet de abajo, con su propio PR, CI y
# escaneo Trivy antes de fusionar.
#
# La imagen sdk no: va a la par con global.json (exacta, sin rollForward).
# El SDK escribe en el packages.lock.json de CaeManager.Web la versión de
# Microsoft.AspNetCore.App.Internal.Assets que trae consigo, así que con otro
# SDK el restore bloqueado de abajo falla con NU1004 (ver Directory.Build.props).
# Un salto de SDK va a mano en una sola PR: global.json, esta línea y el lock
# de Web regenerado. Por eso dependabot.yml ignora dotnet/sdk.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src

# Restore con los packages.lock.json y en modo bloqueado (2026-10-25). Antes
# se copiaban solo los .csproj, sin locks ni Directory.Build.props, y la imagen
# resolvía los comodines (10.*) a lo último publicado el día del build. Copiar
# todo de una vez cuesta nada: ni el job `imagen` de deploy.yml ni el de CI
# reutilizan capas entre ejecuciones, y el restore necesita los .razor para
# que el SDK pida el paquete de assets de Blazor que el lock ya registra.
COPY global.json Directory.Build.props ./
COPY src/ src/
#
# -p:UseSharedCompilation=false (REC-199, apagón de producción 2026-09-04):
# sin esto, `dotnet restore`/`publish` levantan VBCSCompiler como servidor
# de compilación persistente entre los cinco proyectos — es exactamente el
# proceso que el kernel mató por OOM esa noche (anon-rss 2,1 GB). Desde el
# 2026-09-23 esta imagen ya no se construye en el VPS sino en el runner de
# GitHub Actions (job `imagen` de deploy.yml); el flag se conserva porque no
# cambia los binarios y sigue acotando el pico de memoria del build.
#
# Sin -r aquí a propósito: `restore -r linux-x64` es una propiedad global que
# llega a Domain, Application, Infrastructure y Migrations, cuyos locks no
# tienen ese RID, y el modo bloqueado los rechaza (NU1004). Web sí lo tiene,
# porque declara <RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>, y lo
# restaura junto al grafo sin RID.
RUN dotnet restore src/CaeManager.Web/CaeManager.Web.csproj --locked-mode -p:UseSharedCompilation=false

# -r linux-x64 --self-contained false: sin fijar un RuntimeIdentifier, este SDK
# no restaura el paquete de runtime que aporta los assets estáticos del propio
# framework (_framework/blazor.web.js) — el publish "genérico" completa sin
# error pero el archivo nunca se genera, y toda la interactividad (cualquier
# botón, cualquier circuit de SignalR) queda rota en silencio. Con
# --no-restore usa el restore bloqueado de arriba; las bibliotecas se
# compilan sin RID (el SDK las trata como independientes del RID).
#
# -p:RunAnalyzers=false (despliegue a staging roto desde 2026-09-21): el csc
# de CaeManager.Migrations.PostgreSQL (157 Designer.cs, ~42 MB de C#) muere
# con exit 137 dentro del techo de memoria del build que entonces corría en el
# VPS (LIMITE_MEMORIA_BUILD, retirado el 2026-09-23 al pasar el build al runner)
# cuando además ejecuta los analizadores; el último build
# que pasó tenía dos migraciones menos. Medido en local: pico de csc ~2,7 GB
# con analizadores y ~2,1 GB sin ellos, y bajo un heap limitado como en el VPS
# compila siempre sin ellos y falla por OutOfMemory en 4 de 6 con ellos. El
# SDK lo traduce a /skipanalyzers, que conserva los source generators: los
# binarios publicados son idénticos. Los analizadores siguen aplicándose
# donde deciden algo, en el `dotnet build -warnaserror` de CI.
RUN dotnet publish src/CaeManager.Web/CaeManager.Web.csproj -c Release -o /app/publish -r linux-x64 --self-contained false --no-restore -p:UseSharedCompilation=false -p:RunAnalyzers=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production

# libreoffice-writer (no el paquete "libreoffice" completo, que instala
# también Calc/Impress/etc. sin necesidad) aporta el binario "soffice" que
# LibreOfficeConversorWordPdfService invoca en modo headless para convertir
# Word (.docx) a PDF al subir un Documento — ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md.
#
# gosu: deja que el entrypoint arranque como root, corrija permisos del
# volumen si hace falta, y baje de privilegios a $APP_UID sin perder el
# manejo de señales (reemplaza el proceso, a diferencia de `su`) — ver
# docker-entrypoint.sh.
#
# tzdata: el día de negocio es el de Europe/Madrid (DiaDeNegocio) y el
# contenedor corre en UTC; .NET resuelve la zona IANA desde /usr/share/zoneinfo.
# Se declara aunque la imagen base lo traiga, para no depender de ello: sin
# tzdata la app no arranca (Program.cs resuelve la zona al inicio).
#
# libssl3t64 y openssl: la imagen base va fijada por digest y puede traer una versión
# con CVE ya parcheada en el archivo de Ubuntu (CVE-2026-84782, Trivy, 2026-09-30).
# Nombrarlos aquí hace que apt los actualice al instalar; sin ello el escaneo de Trivy
# (CRITICAL,HIGH, exit-code 1) pone en rojo todas las PRs hasta el siguiente digest.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-writer curl ca-certificates gosu tzdata libssl3t64 openssl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
RUN mkdir -p /app/temporales-conversion-word && chown "$APP_UID:$APP_UID" /app/temporales-conversion-word
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 8080

# No fijamos USER aquí a propósito (a diferencia de la primera versión de
# este cambio, P2 #25 de Project-Hydra-Negocio/MATURITY_REVIEW.md): el contenedor
# arranca como root para que el entrypoint pueda corregir la propiedad de
# /data si el volumen persistente viene de antes de este cambio
# (dataprotection-keys/ quedando sin permiso de escritura para $APP_UID en
# ese caso — reproducido en producción, ver docker-entrypoint.sh), y baja de
# privilegios a $APP_UID con gosu antes de ejecutar la app real. El proceso
# de la app en sí sigue sin correr nunca como root.
# Revisión (SHA) que contiene la imagen; /salud la devuelve en X-Talveg-Revision.
# Al final del Dockerfile a propósito: un ARG que cambia en cada commit invalida la caché
# de todo lo que va detrás, y aquí detrás ya no queda nada.
ARG REVISION=desconocida
ENV TALVEG_REVISION=$REVISION
ENTRYPOINT ["/docker-entrypoint.sh"]
