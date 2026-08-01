# Imagen de despliegue de CAE Manager (Blazor Server). Ver DEPLOY.md para
# variables de entorno necesarias (rutas de datos persistentes, credenciales
# del administrador inicial).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar solo los .csproj primero para que `dotnet restore` se cachee entre
# builds mientras no cambien las dependencias — el resto del código cambia
# mucho más a menudo que las referencias de paquete.
COPY src/CaeManager.Domain/CaeManager.Domain.csproj src/CaeManager.Domain/
COPY src/CaeManager.Application/CaeManager.Application.csproj src/CaeManager.Application/
COPY src/CaeManager.Infrastructure/CaeManager.Infrastructure.csproj src/CaeManager.Infrastructure/
COPY src/CaeManager.Migrations.PostgreSQL/CaeManager.Migrations.PostgreSQL.csproj src/CaeManager.Migrations.PostgreSQL/
COPY src/CaeManager.Web/CaeManager.Web.csproj src/CaeManager.Web/
RUN dotnet restore src/CaeManager.Web/CaeManager.Web.csproj -r linux-x64

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
RUN dotnet publish src/CaeManager.Web/CaeManager.Web.csproj -c Release -o /app/publish -r linux-x64 --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production

# libreoffice-writer (no el paquete "libreoffice" completo, que instala
# también Calc/Impress/etc. sin necesidad) aporta el binario "soffice" que
# LibreOfficeConversorWordPdfService invoca en modo headless para convertir
# Word (.docx) a PDF al subir un Documento — ver ARCHITECTURE.md.
#
# postgresql-client aporta pg_dump, que BackupHostedService invoca cuando el
# motor es PostgreSQL. La base Debian de esta imagen (trixie) trae el cliente
# 17 — pg_dump tiene que ser >= que la versión del servidor, así que si el
# Postgres de Railway sube de versión mayor hay que revisar esto.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-writer postgresql-client \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 8080

# El host asigna el puerto en tiempo de ejecución vía la variable PORT (p.
# ej. Railway) — no se puede fijar en ENV porque cambia por despliegue, así
# que se lee al arrancar el contenedor, no al construir la imagen.
ENTRYPOINT ["sh", "-c", "dotnet CaeManager.Web.dll --urls http://+:${PORT:-8080}"]
