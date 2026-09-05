# Imagen de despliegue de CAE Manager (Blazor Server). Ver DEPLOY.md para
# variables de entorno necesarias (rutas de datos persistentes, credenciales
# del administrador inicial).

# Cadena de suministro (auditoría Módulo 10, 2026-08-30): tag flotante "10.0"
# resolvía a la última imagen del día del build, así que el mismo Dockerfile
# podía producir binarios distintos en CI y en el VPS. Fijado a la versión
# exacta MÁS el digest (comprobado contra mcr.microsoft.com, no copiado de un
# blog) — Dependabot (ver .github/dependabot.yml, ecosistema "docker") sigue
# proponiendo el salto cuando salga una versión nueva, con su propio PR,
# CI y escaneo Trivy antes de fusionar.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

# Copiar solo los .csproj primero para que `dotnet restore` se cachee entre
# builds mientras no cambien las dependencias — el resto del código cambia
# mucho más a menudo que las referencias de paquete.
COPY src/CaeManager.Domain/CaeManager.Domain.csproj src/CaeManager.Domain/
COPY src/CaeManager.Application/CaeManager.Application.csproj src/CaeManager.Application/
COPY src/CaeManager.Infrastructure/CaeManager.Infrastructure.csproj src/CaeManager.Infrastructure/
COPY src/CaeManager.Migrations.PostgreSQL/CaeManager.Migrations.PostgreSQL.csproj src/CaeManager.Migrations.PostgreSQL/
COPY src/CaeManager.Web/CaeManager.Web.csproj src/CaeManager.Web/
#
# -p:UseSharedCompilation=false (REC-199, apagón de producción 2026-09-04):
# sin esto, `dotnet restore`/`publish` levantan VBCSCompiler como servidor
# de compilación persistente entre los cinco proyectos — es exactamente el
# proceso que el kernel mató por OOM esa noche (anon-rss 2,1 GB). La
# contención real del incidente es el techo de memoria del paso de build en
# ci-deploy.sh; esto es una segunda línea, gratuita, que apaga el propio
# mecanismo que acumuló ese estado.
RUN dotnet restore src/CaeManager.Web/CaeManager.Web.csproj -r linux-x64 -p:UseSharedCompilation=false

COPY src/ src/
# -r linux-x64 --self-contained false: sin fijar un RuntimeIdentifier, este SDK
# no restaura el paquete de runtime que aporta los assets estáticos del propio
# framework (_framework/blazor.web.js) — el publish "genérico" completa sin
# error pero el archivo nunca se genera, y toda la interactividad (cualquier
# botón, cualquier circuit de SignalR) queda rota en silencio.
#
# Sin --no-restore a propósito: el restore de la capa anterior (hecho solo con
# los .csproj, antes de copiar los .razor) no basta para que el SDK detecte que
# necesita ese paquete de runtime — deja el mismo hueco. El restore completo,
# ya con el código fuente presente, sí lo detecta. Sigue siendo
# framework-dependent (no self-contained), solo fuerza qué paquete de runtime
# se resuelve.
RUN dotnet publish src/CaeManager.Web/CaeManager.Web.csproj -c Release -o /app/publish -r linux-x64 --self-contained false -p:UseSharedCompilation=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production

# libreoffice-writer (no el paquete "libreoffice" completo, que instala
# también Calc/Impress/etc. sin necesidad) aporta el binario "soffice" que
# LibreOfficeConversorWordPdfService invoca en modo headless para convertir
# Word (.docx) a PDF al subir un Documento — ver ARCHITECTURE.md.
#
# gosu: deja que el entrypoint arranque como root, corrija permisos del
# volumen si hace falta, y baje de privilegios a $APP_UID sin perder el
# manejo de señales (reemplaza el proceso, a diferencia de `su`) — ver
# docker-entrypoint.sh.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-writer curl ca-certificates gosu \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 8080

# No fijamos USER aquí a propósito (a diferencia de la primera versión de
# este cambio, P2 #25 de docs/business/MATURITY_REVIEW.md): el contenedor
# arranca como root para que el entrypoint pueda corregir la propiedad de
# /data si el volumen persistente de Railway viene de antes de este cambio
# (dataprotection-keys/ quedando sin permiso de escritura para $APP_UID en
# ese caso — reproducido en producción, ver docker-entrypoint.sh), y baja de
# privilegios a $APP_UID con gosu antes de ejecutar la app real. El proceso
# de la app en sí sigue sin correr nunca como root.
ENTRYPOINT ["/docker-entrypoint.sh"]
