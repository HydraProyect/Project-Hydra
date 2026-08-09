# RUNBOOK — Despliegue local con Cloudflare Tunnel (etapa puente post-Railway)

**Contexto (2026-08-09).** El trial gratuito de Railway terminó. Mientras se cierra el primer
cliente, el sistema corre en una máquina local: app + PostgreSQL 18 en Docker Compose,
expuesto a internet con Cloudflare Tunnel sobre dominio propio, y backups off-site en un
Storage Box de Hetzner (Borg, ~4 €/mes). **Toda la data de Railway/S3 era de prueba** — este
es un despliegue limpio, sin migración de datos: BD vacía (las migraciones corren al
arrancar), claves de Data Protection nuevas y datos de prueba re-sembrados
(`DatosPrueba__Activo=true`).

Cuando se cierre el primer cliente, este mismo stack se mueve a un VPS (p. ej. Hetzner
CX22/CPX11): mismo `docker-compose.produccion.yml`, mismo `backup-borg.sh`; solo cambia la
máquina y, si se quiere, sustituir cloudflared por un reverse proxy con DNS directo. No se
construye nada de eso por adelantado (YAGNI).

## Piezas

| Pieza | Qué es | Coste |
|---|---|---|
| `deploy/local/docker-compose.produccion.yml` | app (Dockerfile del repo) + `postgres:18` + `cloudflared` | — |
| Dominio propio en Cloudflare | DNS + TLS en el edge + Tunnel (plan Free, con WebSockets para Blazor Server) | ~10 €/año |
| Hetzner Storage Box (BX11, 1 TB) | Destino Borg de los backups (BD + claves + PDFs) | ~4 €/mes |
| `scripts/backup-borg.sh` | Backup diario por cron del host (sustituye al `BackupHostedService`, apagado) | — |
| `scripts/ensayo-restauracion-borg.sh` | Ensayo de restauración periódico | — |

## Prerrequisitos

1. Máquina con Docker + Docker Compose y `borg`, encendida de forma continua.
2. Dominio comprado y con la zona DNS en Cloudflare (Cloudflare Registrar o transferencia de
   nameservers) — el Tunnel lo exige.
3. Storage Box de Hetzner con acceso SSH activado (puerto 23) y clave SSH subida.
4. Las variables de integraciones que hubiera en Railway (SSO Entra ID, Graph, WhatsApp,
   Anthropic…) copiadas antes de perder acceso al dashboard.

## Puesta en marcha

1. `cd deploy/local && cp .env.example .env` y rellenar (referencia de variables:
   `DEPLOY.md` § 3). Sin `AdministradorInicial__Email`/`__Contrasena` el arranque falla a
   propósito en Production.
2. En Cloudflare Zero Trust → Networks → Tunnels: crear un túnel, copiar el token a
   `CLOUDFLARED_TUNNEL_TOKEN`, y añadir un *public hostname* `app.<dominio>` →
   `http://app:8080` (HTTP: el tramo interno va por la red del compose; el TLS lo pone el
   edge de Cloudflare).
3. `docker compose -f docker-compose.produccion.yml up -d --build`. El primer arranque
   aplica las migraciones (`Migraciones:AlArrancar`, default `true`), crea el administrador
   inicial y siembra los datos de prueba.
4. Verificar: `curl -fsS http://127.0.0.1:8080/salud` (hace `SELECT 1` real contra
   Postgres) y después `https://app.<dominio>/salud` a través del túnel.

## Reapuntar integraciones al dominio nuevo

Todo lo que apuntaba a `*.up.railway.app` tiene que apuntar a `https://app.<dominio>`:

- **SSO Entra ID**: añadir los redirect URIs con el dominio nuevo en el App Registration
  (`DEPLOY.md` § 3.1).
- **Webhooks de Microsoft Graph** (conector M365): recrear las suscripciones con la URL
  nueva — ver `RUNBOOK-GRAPH-M365.md`.
- **WhatsApp Cloud API**: en el App Dashboard de Meta, callback URL
  `https://app.<dominio>/api/integraciones/webhooks/whatsapp` (`DEPLOY.md` § 3.5).
- **`AlertasPorCorreo__UrlBase`**: poner el dominio nuevo para que el enlace del resumen
  diario funcione.

## Backups (Borg → Storage Box)

El `BackupHostedService` queda apagado (`Backups__Activo=false` en el compose): su destino
era S3 y el Storage Box no habla S3. Lo sustituye `scripts/backup-borg.sh`, que mantiene la
invariante de `RUNBOOK-CLAVES.md` (BD y `dataprotection-keys/` siempre en el mismo backup) y
añade `/data/documentos` (los PDFs, que el servicio antiguo no respaldaba).

1. Inicializar el repo una sola vez:
   `borg init --encryption=repokey-blake2 'ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager'`.
   **Guardar la passphrase y `borg key export` fuera de la máquina** (sin ellos el backup es
   ilegible — mismo rango de criticidad que las claves de Data Protection).
2. Credenciales del cron en un archivo solo-root, p. ej. `/etc/caemanager-backup.env`
   (`chmod 600`) con `BORG_REPO=...` y `BORG_PASSPHRASE=...`.
3. Cron del host (diario + verificación semanal del repo):

   ```cron
   15 3 * * *  . /etc/caemanager-backup.env; /ruta/al/repo/scripts/backup-borg.sh >> /var/log/caemanager-backup.log 2>&1
   45 3 * * 0  . /etc/caemanager-backup.env; /ruta/al/repo/scripts/backup-borg.sh --check >> /var/log/caemanager-backup.log 2>&1
   ```

4. Retención: 7 diarios / 4 semanales / 6 mensuales (`borg prune` dentro del script).
5. Ensayo de restauración periódico con `scripts/ensayo-restauracion-borg.sh`, anotando el
   resultado en `docs/ENSAYO-RESTAURACION.md` — un backup que nunca se ha restaurado no es
   un backup.

El bucket S3 de AWS de backups deja de usarse; su contenido era de prueba. No borrar la
cuenta hasta tener al menos una semana de backups Borg con un ensayo de restauración en
verde.

## Verificación end-to-end del despliegue

Checklist en navegador contra `https://app.<dominio>` (regla del proyecto: ninguna
fase/tarea se cierra solo con tests):

1. Login del administrador inicial.
2. Subir un PDF a un Documento y descargarlo.
3. Subir un `.docx` y comprobar la conversión a PDF (LibreOffice va dentro de la imagen).
4. Lanzar un análisis IA de documento y ver la notificación al terminar (cola durable en
   PostgreSQL — sobrevive a un `docker compose restart app`).
5. Guardar una credencial de Empresa, reiniciar el stack y comprobar que sigue legible
   (prueba de que `dataprotection-keys/` persiste en el volumen).
6. Primer `backup-borg.sh` manual + `ensayo-restauracion-borg.sh` completo.

## Limitaciones asumidas de esta etapa

- **Una sola máquina, sin alta disponibilidad**: si se apaga o pierde red, el servicio cae y
  los circuitos de Blazor Server se cortan. Aceptado mientras solo haya datos de prueba y
  ningún cliente real.
- **Los logs no salen de la máquina** salvo que se configure `Serilog__Seq__ServerUrl` o
  `Sentry__Dsn` (igual que en Railway).
- **RGPD**: cuando entre el primer cliente real, la infraestructura efectiva (máquina
  local/VPS, Storage Box en la UE, Cloudflare como proxy) debe reflejarse en
  `RGPD-TRATAMIENTO-DATOS.md` y en el paquete legal — decisión con componente legal que
  toma el propietario, no esta guía.

## Camino de vuelta

Mientras no haya datos reales, el camino de vuelta es trivial: cualquier PaaS (Railway de
pago u otro) con el mismo `Dockerfile` y las mismas variables de `DEPLOY.md` § 3 — ese
documento sigue siendo la referencia completa de configuración.
