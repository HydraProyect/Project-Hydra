# Desplegar CAE Manager en Railway

Guía para dejar CAE Manager accesible por navegador para todo el equipo (piloto/pruebas), sin que nadie necesite instalar .NET.

## Por qué Railway

CAE Manager usa PostgreSQL (servicio gestionado aparte, ver más abajo) y guarda los PDFs adjuntos en disco local (ver `ARCHITECTURE.md`). Railway encaja bien con eso: despliega directo desde el repo de GitHub, sirve HTTPS automáticamente, soporta un volumen persistente sencillo para la app y ofrece Postgres como servicio adicional del mismo proyecto. **Escalar a más de 1 réplica** (P3-30 de `docs/business/MATURITY_REVIEW.md`) ya es posible, pero exige activar antes `SignalR__Redis__*` y `DataProtection__S3__*` (ver § "Notas para producción real" más abajo) — sin ellos, sigue siendo un despliegue de una sola réplica.

## 1. Crear el proyecto en Railway

1. Entra en [railway.app](https://railway.app) y crea una cuenta (o inicia sesión).
2. "New Project" → "Deploy from GitHub repo" → selecciona `christopherjp1-jpg/Project-Hydra`.
3. Railway detecta el `Dockerfile` en la raíz del repo automáticamente y lo usa para construir la imagen — no hace falta configurar nada más para el build.
4. En la pestaña **Settings** del servicio, en "Networking", pulsa "Generate Domain" para obtener una URL pública tipo `algo.up.railway.app`.

## 2. Añadir la base de datos y un volumen persistente

La base de datos vive en un servicio de PostgreSQL aparte, no en el volumen — sin él, o sin el volumen, los PDFs adjuntos y las claves de cifrado se pierden en cada redeploy.

1. En el lienzo del proyecto, "+ New" → "Database" → "Add PostgreSQL". Comprueba en su pestaña **Settings → Region** que queda en la misma región que el servicio de la app (relevante para RGPD, ver `RGPD-TRATAMIENTO-DATOS.md` § 6).
2. En el servicio de la **app**, pestaña **Volumes** → "New Volume".
3. Mount path: `/data`.
4. Tamaño: 1 GB es de sobra para empezar (se puede ampliar después).

**Si el volumen ya existía de un despliegue anterior a que la imagen empezara a correr como usuario no-root (P2 #25 de `docs/business/MATURITY_REVIEW.md`)**: verifica en el primer arranque tras actualizar que la app puede seguir escribiendo en `/data` — busca errores de permisos en el log (`dataprotection-keys/`, `documentos/` si no está activo `AlmacenamientoS3`). Un volumen nuevo no tiene este problema.

## 3. Variables de entorno

En la pestaña **Variables** del servicio de la **app**, añade:

| Variable | Valor | Para qué |
|---|---|---|
| `ConnectionStrings__CaeManagerDb` | `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}` | Conexión al servicio de PostgreSQL del paso 2 — usa referencias de variable de Railway (`${{NombreDelServicio.VARIABLE}}`, ajustando el nombre si tu servicio de Postgres no se llama "Postgres") en vez de copiar la contraseña a mano, para que no se desincronice si Railway la rota |
| `AlmacenamientoArchivos__Ruta` | `/data/documentos` | PDFs adjuntos de Documentos, en el volumen — se ignora si `AlmacenamientoS3__Activo=true` (ver fila siguiente) |
| `AlmacenamientoS3__Activo` | `true` (opcional, por defecto `false`) | Guarda los PDFs de Documento en S3 en vez de en el volumen local (P2 #22 de `docs/business/MATURITY_REVIEW.md`) — necesario para más de una réplica, donde un archivo subido a una réplica no lo verían las demás en disco local |
| `AlmacenamientoS3__AccessKeyId` / `AlmacenamientoS3__SecretAccessKey` | credenciales de un usuario IAM **distinto** de los de Backups y de KMS, con permisos solo sobre el bucket de documentos (`s3:PutObject`/`GetObject`/`DeleteObject`) | Igual que con KMS: separarlo de los otros usuarios IAM es lo que hace que filtrar unas credenciales no dé acceso a lo que protegen las otras — y aquí además importa que ese bucket puede contener datos de salud (reconocimientos médicos), ver `RGPD-TRATAMIENTO-DATOS.md` |
| `AlmacenamientoS3__BucketName` | nombre del bucket S3, **distinto** del de Backups | No reutilices el bucket de backups: ese ya contiene un volcado completo de la base de datos |
| `AlmacenamientoS3__Region` | región del bucket, p. ej. `eu-south-2` | Misma consideración de RGPD que el resto de buckets — no sacar datos de España |
| `DataProtection__RutaClaves` | `/data/dataprotection-keys` | Claves de cifrado de credenciales (Empresa/Centro) — si no se persisten, cada redeploy invalida las credenciales ya guardadas |
| `AdministradorInicial__Email` | (tu elección, p. ej. `admin@ProjectHydra.com`) | **Obligatoria en producción** (`ASPNETCORE_ENVIRONMENT=Production`): sin ella el arranque falla a propósito — las credenciales por defecto son públicas en el código y solo se usan en desarrollo |
| `AdministradorInicial__Contrasena` | (una contraseña real, mínimo 10 caracteres) | Igual que arriba, para la contraseña — también obligatoria en producción |
| `ConversionWordPdf__TimeoutSegundos` | (opcional, por defecto `60`) | Tiempo máximo que se espera a LibreOffice al convertir un Word (.docx) a PDF antes de darlo por fallado — no suele hacer falta tocarlo |
| `DatosPrueba__Activo` | `true` (opcional, solo para un entorno de pruebas) | Siembra una base de datos genérica de cientos de filas por entidad — ver más abajo |
| `Logging__RutaArchivo` | (opcional, por defecto `App_Data/logs/log-.txt` relativo al volumen) | Ruta del log estructurado de Serilog en disco (consola + archivo con rotación diaria) — ver "Iniciativa de hardening" en `ROADMAP.md` |
| `Sentry__Dsn` | (opcional, vacío por defecto) | Activa el error tracking de Sentry si se rellena con el DSN de un proyecto real — sin esta variable la SDK queda completamente inerte, no hace falta tener cuenta de Sentry para desplegar |
| `Serilog__Seq__ServerUrl` | (opcional, vacío por defecto) | Envía los logs a una instancia de Seq (propia o Seq cloud) además de a consola y disco. **Es lo único que hace que los logs sobrevivan al contenedor**: el volumen de logs se pierde en cada redeploy, así que sin esto un incidente de la semana pasada no se puede investigar. Sin la variable, el sink ni se registra |
| `Serilog__Seq__ApiKey` | (opcional) | Clave de API de Seq. Solo hace falta si la instancia exige autenticación |
| `Serilog__MinimumLevel__Override__CaeManager.Application.Requests` | (opcional, por defecto `Information`) | Detalle de la traza de MediatR. A `Debug` registra también cada Query (útil para diagnosticar una pantalla lenta, ruidoso para dejarlo puesto); a `Warning` solo fallos y requests por encima de 1 s |
| `Comunicaciones__Activo` | `true` (opcional, por defecto `false`) | Módulo congelado (P2 #26 de `docs/business/MATURITY_REVIEW.md`): sin esta variable, `/comunicaciones` responde como si la ruta no existiera. No hay ingesta real de Microsoft Graph detrás — no lo actives frente a un cliente real hasta que la haya |
| `Backups__Activo` | `true` (opcional, por defecto `false`) | Activa el backup automático diario del volcado de PostgreSQL (`pg_dump --format=custom`) + `dataprotection-keys/` a S3 — ver `RUNBOOK-CLAVES.md` |
| `Backups__Aws__AccessKeyId` / `Backups__Aws__SecretAccessKey` | credenciales de un usuario IAM con permisos solo sobre el bucket de backups (`s3:PutObject`/`GetObject`/`ListBucket`) | Nunca uses las credenciales root de la cuenta de AWS para esto |
| `Backups__Aws__BucketName` | nombre del bucket S3 | |
| `Backups__Aws__Region` | región del bucket, p. ej. `eu-south-2` | |
| `Backups__IntervaloHoras` | `24` (opcional, es el valor por defecto) | Cada cuánto corre el backup |
| `DataProtection__Kms__Activo` | `true` (opcional, por defecto `false`) | Cifra las claves de Data Protection con AWS KMS antes de escribirlas al volumen — sin esto viajan **en claro** dentro del mismo backup que la base de datos que protegen (`RUNBOOK-CLAVES.md` § KMS) |
| `DataProtection__Kms__KeyId` | ARN o alias de la clave, p. ej. `alias/caemanager-dataprotection` | Clave simétrica de cifrado/descifrado, en la misma región que el bucket |
| `DataProtection__Kms__AccessKeyId` / `DataProtection__Kms__SecretAccessKey` | credenciales de un usuario IAM **distinto** del de backups, con permiso solo de `kms:Encrypt`/`kms:Decrypt` sobre esa clave | Separarlo del de backups es lo que hace que filtrar las credenciales de S3 no dé también la llave de lo que hay dentro |
| `DataProtection__Kms__Region` | región de la clave, p. ej. `eu-south-2` | Debe coincidir con la región donde se creó la clave |

Las cuatro variables de `DataProtection__Kms__*` van juntas: si falta cualquiera, el cifrado queda apagado y el arranque lo advierte por log. Con todas puestas, el arranque hace un cifrado/descifrado de prueba y deja dicho si la clave responde — busca `cifrado con AWS KMS operativo` en el log del despliegue.

| `DataProtection__S3__Activo` | `true` (opcional, por defecto `false`) | Guarda el llavero de Data Protection en S3 en vez de en el volumen local (P3-30) — necesario para más de una réplica, donde cada réplica genera/lee su propio juego de claves en disco local |
| `DataProtection__S3__AccessKeyId` / `DataProtection__S3__SecretAccessKey` | credenciales de un usuario IAM **distinto** de los de Backups/KMS/AlmacenamientoS3 | Mismo criterio que el resto: que se filtren unas no da acceso a lo que protegen las otras |
| `DataProtection__S3__BucketName` | bucket S3, puede ser el mismo que `AlmacenamientoS3__BucketName` (usa un prefijo `dataprotection-keys/` distinto) o uno propio | |
| `DataProtection__S3__Region` | región del bucket, p. ej. `eu-south-2` | |
| `SignalR__Redis__Activo` | `true` (opcional, por defecto `false`) | Backplane de SignalR (P3-30) — necesario para más de una réplica: sin él, un circuito de Blazor Server no sobrevive a que el balanceador cambie de réplica a mitad de sesión |
| `SignalR__Redis__CadenaConexion` | cadena de conexión del servicio Redis (add-on de Railway u otro) | |

Las tres piezas de multi-réplica (`AlmacenamientoS3`, `DataProtection__S3`, `SignalR__Redis`) arrancan con una verificación de conectividad que deja constancia en el log de despliegue — revísalo tras activarlas y antes de escalar el servicio a más de una réplica. La elección de líder entre réplicas para `BackupHostedService`/`ProcesadorAnalisisDocumentoHostedService` (`pg_try_advisory_lock` de PostgreSQL) no tiene variable propia — usa siempre el mismo `ConnectionStrings__CaeManagerDb`, así que está activa desde ya.

Producción y staging pueden usar las mismas variables de `Backups__Aws__*` (mismo bucket) sin pisarse los backups entre sí — cada uno sube a su propia carpeta dentro del bucket, identificada automáticamente por el nombre del servicio en Railway.

| Variable | Valor | Para qué |
|---|---|---|
| `Anthropic__ApiKey` | tu API key de [console.anthropic.com](https://console.anthropic.com) | Activa el chat "Pregúntale a Hydra" (botón flotante) — sin esta variable el botón ni se muestra. Si la key tiene fecha de expiración (recomendado), hay que rotarla en Railway antes de que caduque o el chat deja de responder |
| `Anthropic__Modelo` | (opcional, por defecto `claude-sonnet-5`) | Modelo usado para el chat |
| `DeteccionPreviaDocumento__Activa` | (opcional, por defecto `false`) | **Apagada a propósito** (P0-4 de `docs/business/MATURITY_REVIEW.md`): activarla envía el PDF completo de cualquier Documento de Trabajador —incluidos reconocimientos médicos— a un proveedor de IA externo antes de conocer el tipo de documento. No la actives hasta que el DPA declare ese tratamiento de datos de salud (art. 9 RGPD) como subencargado |

Las dos variables de `AdministradorInicial` solo se aplican **la primera vez que arranca** (cuando todavía no existe ningún usuario administrador) — si el servicio ya arrancó una vez sin ellas, cambia la contraseña desde `/usuarios` en vez de tocar estas variables.

### Login corporativo con Microsoft (SSO / Entra ID)

| Variable | Valor | Para qué |
|---|---|---|
| `AzureAd__TenantId` | Id del tenant de Entra ID de la empresa | Restringe el login a cuentas de esa organización — nunca "cualquier cuenta Microsoft" |
| `AzureAd__ClientId` | Application (client) ID del App Registration | |
| `AzureAd__ClientSecret` | un Client Secret del mismo App Registration | Como cualquier secreto, va directo aquí — nunca se comparte por chat. Los Client Secret de Entra ID caducan (máximo 24 meses) — hay que rotarlo antes de que caduque o el login con Microsoft deja de funcionar (el login local sigue disponible siempre) |

**Sin estas tres variables, "Iniciar sesión con Microsoft" ni se muestra** — el login local sigue siendo el único camino, exactamente igual que hoy (mismo principio que `Sentry__Dsn`/`Backups__Activo`/`Anthropic__ApiKey`).

**En cuanto se configuran, cambia el comportamiento del login local**: sigue funcionando (nunca se bloquea), pero el rol efectivo de esa sesión queda limitado a Consulta (solo lectura), sin importar el rol real guardado — es una capa extra de control pensada para que los roles editores (Dirección CAE, Coordinador/Gestor CAE) solo puedan editar si entran por Microsoft. **Excepción explícita: Administrador conserva su rol real incluso por login local** — vía de escape deliberada para nunca perder acceso de administración al portal si algo falla del lado de Entra ID (pedida por el usuario tras las primeras pruebas). Para el resto de roles, **antes de activar esto en producción**, confirma que el email de cada `ApplicationUser` editor coincide exactamente con su cuenta de Microsoft corporativa real — si no coincide, esa persona queda atascada en solo-lectura hasta que un Administrador (que sí puede entrar siempre localmente) le dé de alta correctamente o le asigne un rol si aparece como pendiente (ver más abajo).

Pasos para crear el App Registration en [entra.microsoft.com](https://entra.microsoft.com) (Aplicaciones → Registros de aplicaciones → Nuevo registro):
1. Tipo de cuenta: **"Cuentas solo en este directorio organizativo"** (single-tenant) — nunca "cualquier cuenta Microsoft" ni multi-tenant.
2. Plataforma de redirección: **Web**, con la URI `https://tu-dominio.up.railway.app/signin-microsoft` (una por cada dominio real — producción y staging necesitan cada uno la suya, añadidas ambas al mismo App Registration).
3. En **Certificados y secretos**, crea un Client Secret nuevo (anota la fecha de caducidad) — es el valor de `AzureAd__ClientSecret`.
4. Permisos de API: no hace falta añadir ninguno además de los delegados por defecto (`openid`/`profile`/`email` los pide el propio flujo de inicio de sesión, no requieren consentimiento de administrador aparte) — salvo que también actives el envío de correo (ver siguiente sección), que sí necesita un permiso de aplicación aparte.
5. El **Application (client) ID** y el **Directory (tenant) ID** están en la pantalla "Introducción" del propio registro.

**Cualquier cuenta del tenant configurado puede iniciar sesión** (restringido por `AzureAd__TenantId`, nunca "cualquier cuenta Microsoft") — pero solo entra con acceso real si un Administrador ya le asignó un rol. Si es la primera vez que esa persona inicia sesión, se crea automáticamente una cuenta sin rol y queda en una pantalla de espera ("Asignación de rol pendiente") hasta que un Administrador le asigna uno desde la pestaña **"Pendientes de asignar"** en `/roles` — ver la sección de correo más abajo para las notificaciones asociadas.

### Envío de correo (Microsoft Graph)

| Variable | Valor | Para qué |
|---|---|---|
| `Graph__TenantId` / `Graph__ClientId` / `Graph__ClientSecret` | mismos valores que `AzureAd__*` si reutilizas el mismo App Registration, o los de uno distinto | Requiere el permiso de **aplicación** (no delegado) `Mail.Send` en Entra ID, con **consentimiento de administrador** concedido — a diferencia del login SSO, este envío no depende de que haya ningún usuario con sesión iniciada |
| `Graph__BuzonRemitente` | UPN de un buzón real del tenant (ej. `notificaciones@empresa.com`) | Remitente de los correos — debe existir como buzón real con licencia de correo |

**Sin las cuatro variables, el envío de correo queda inerte** — las notificaciones que lo disparan (usuario pendiente de rol, confirmación de rol asignado) se registran en el log como aviso pero no impiden la acción de negocio (crear la cuenta pendiente, asignar el rol siguen funcionando igual). La plantilla/diseño final de estos correos está todavía por definir — hoy es HTML mínimo, sin estilos.

### Conector de Microsoft 365 para Comunicaciones (P3-33)

| Variable | Valor | Para qué |
|---|---|---|
| `Integraciones__Microsoft365__ClientId` | Application (client) ID de un App Registration **distinto** al de SSO/envío de correo | Consentimiento delegado por buzón (OAuth authorization code + `offline_access`), no el flujo de aplicación de las dos secciones anteriores — nunca reutilices el mismo registro |
| `Integraciones__Microsoft365__ClientSecret` | un Client Secret del mismo App Registration | Igual que cualquier secreto de este documento, va directo aquí — nunca por chat. Caduca igual que los anteriores (máximo 24 meses) |

**Sin estas dos variables, el módulo Comunicaciones sigue apagado** (`ComunicacionesOptions__Activo`, sección aparte) y la pantalla `/integraciones` no tiene nada que conectar — mismo principio "inerte por defecto" del resto de integraciones de este documento.

Pasos para crear el App Registration en [entra.microsoft.com](https://entra.microsoft.com):
1. Tipo de cuenta: **"Cuentas en cualquier directorio organizativo"** (multi-tenant) — a diferencia del App Registration de SSO, este tiene que aceptar buzones de organizaciones cliente distintas a la tuya, no solo la propia. El endpoint de autorización usa `common`, nunca un tenant fijo.
2. Plataforma de redirección: **Web**, con la URI `https://tu-dominio.up.railway.app/integraciones/microsoft365-callback`.
3. Permisos de API → **Microsoft Graph → Delegados**: `Mail.Read`, `Mail.Send`, `offline_access`. Delegados, no de aplicación — cada conexión actúa como el buzón que dio el consentimiento, no como la app.
4. En **Certificados y secretos**, crea un Client Secret nuevo (anota la fecha de caducidad) — es el valor de `Integraciones__Microsoft365__ClientSecret`.
5. No hace falta consentimiento de administrador del lado de Hydra: cada buzón se conecta desde `/integraciones` (rol Administrador), consintiendo el propio usuario del buzón en la pantalla de Microsoft.

**Antes de conectar un buzón de un cliente real**, confirma que el DPA declara este acceso — el refresco automático de token y la ingesta de correo entrante son tratamiento de datos personales por cuenta del tenant, igual que el resto de accesos de soporte documentados en `RGPD-TRATAMIENTO-DATOS.md`.

Las suscripciones de notificaciones de Graph expiran a los ~3 días — `RenovacionSuscripcionWebhookHostedService` las renueva sola cada 24h, no hace falta ninguna intervención manual salvo que el log muestre fallos repetidos de renovación (revisar el estado de la conexión en `/integraciones`, que pasa a "Con error").

### Datos de prueba para pruebas de carga y verificación de perfiles

Con `DatosPrueba__Activo=true`, el primer arranque siembra automáticamente (solo si todavía no hay ningún Cliente — no duplica en redeploys posteriores) una cartera con la forma de un cliente fundador real — Clientes con varias Empresas contratistas cada uno, Empresas con varios Centros y Trabajadores, documentación estándar completa con fechas de vencimiento repartidas entre vencido/urgente/próximo/vigente, y datos ya preparados para probar la purga de retención (ver `ROADMAP.md` § Fase 62 para el detalle exacto y los números). Nombres de personas, empresas y lugares son de ficción a propósito, para que nada de la siembra se confunda con un dato real.

3 usuarios de prueba por cada uno de los 6 perfiles (Administrador/DireccionCae/CoordinadorCae/GestorCae/Consulta/Cliente), con email `prueba.<rol><n>@caemanager.local` y contraseña `Prueba#2026` para todos — así se puede iniciar sesión con cada perfil y comprobar qué ve cada uno con volumen real de datos.

**No actives esto en el entorno real del equipo** — pensado para un servicio aparte (o una base de datos que luego se descarta) dedicado solo a pruebas de carga y QA.

`PORT` la asigna Railway automáticamente — no hace falta configurarla, el contenedor ya la lee al arrancar (ver `Dockerfile`).

## 4. Desplegar

Con el Dockerfile detectado, el volumen montado y las variables puestas, Railway despliega automáticamente en cada push a la rama configurada (por defecto, la rama por defecto del repo — cámbiala en Settings → "Source" si quieres desplegar desde otra, p. ej. `claude/cae-manager-setup-2995sb`).

Primer arranque: la app ejecuta las migraciones de base de datos y crea el usuario administrador automáticamente (ver `Program.cs` → `IdentitySeeder`) — no hace falta ningún paso manual.

**Migraciones fuera del arranque (P2 #22 de `docs/business/MATURITY_REVIEW.md`)**: `railway.json` (config-as-code, en la raíz del repo) declara un `deploy.preDeployCommand` que ejecuta `dotnet CaeManager.Web.dll --migrate-only` — aplica las migraciones pendientes y termina, antes de que Railway arranque el proceso web del deploy. Railway lo recoge solo si no hay un Start/Pre-Deploy Command puesto a mano en el dashboard del servicio (Settings → Deploy) que lo pise; si el proyecto ya tiene uno configurado ahí, hay que borrarlo para que `railway.json` mande. El arranque normal del proceso web **sigue aplicando las migraciones también** (`Migraciones:AlArrancar`, por defecto `true`) — a propósito, para no depender de que el pre-deploy ya esté activo; aplicar una migración ya aplicada no hace nada. Dos réplicas repitiendo esto arrancando a la vez sí sería la carrera que este mecanismo existe para evitar — pon `Migraciones__AlArrancar=false` en las variables del servicio antes de escalar a más de una réplica, no antes.

### Gate de deploy y smoke test post-deploy (P2 #23)

Los dos mecanismos ya están activos — uno por código, el otro activado a mano en el dashboard el 2026-08-02:

- **Smoke test post-deploy — activo, y ya con el health check real de P0 #5.** `railway.json` declara `deploy.healthcheckPath=/salud` y `deploy.healthcheckTimeout=300`: Railway no corta el tráfico hacia el deploy nuevo hasta que ese endpoint responda `200`, y si nunca responde, el deploy se descarta y el tráfico se queda en el anterior (zero-downtime nativo de Railway, sin script propio). `/salud` (`Program.cs`) ya no devuelve `Results.Ok("ok")` incondicional — corre `AddHealthChecks().AddNpgSql()` (`SELECT 1` contra la base), así que un `200` significa que el proceso vive **y** PostgreSQL responde, no solo que el proceso arrancó.
- **Gate de CI verde — activado.** Railway soporta "Wait for CI" (Settings del servicio → Deploy), que deja el deploy en espera hasta que los GitHub Actions del commit terminen, y lo descarta si alguno falla — es un ajuste que Railway solo expone en el dashboard, no en config-as-code. Activado en el servicio de producción.

## 5. Verificar

- `https://tu-dominio.up.railway.app/salud` debe responder `200 Healthy` (endpoint sin autenticación, pensado para healthchecks). Desde el cierre del P0-5 (`MATURITY_REVIEW.md`) ya no es un "ok" incondicional: ejecuta una consulta real contra PostgreSQL, así que un 503 aquí significa que la base de datos no responde, no solo que el proceso esté caído.
- **Uptime check externo (pendiente de provisionar, P0-5)**: apunta un monitor externo gratuito (UptimeRobot, Better Stack…) a `/salud` con alerta por email — sin esto, una caída un viernes por la noche no la notifica nadie. Misma pendiente para `Sentry__Dsn`: crear el proyecto en sentry.io y poner el DSN en Railway activa el error tracking sin tocar código.
- `https://tu-dominio.up.railway.app/` debe redirigir a la pantalla de inicio de sesión.
- Inicia sesión con el email/contraseña de `AdministradorInicial` que configuraste en el paso 3.

## Notas para producción real (fuera de alcance de un piloto)

- **Multi-réplica (P3-30).** Las piezas que ataban la app a una sola réplica ya están resueltas: `TrabajoAnalisisDocumento` (P2 #22) hace la cola de IA durable en PostgreSQL, `AlmacenamientoS3__Activo` saca los PDFs del volumen local, la elección de líder (`pg_try_advisory_lock`, sin configuración — siempre activa) evita que dos réplicas de `BackupHostedService`/`ProcesadorAnalisisDocumentoHostedService` compitan por el mismo trabajo, y el llavero de Data Protection puede vivir en S3 en vez del disco local de cada réplica (`DataProtection__S3__Activo`/`AccessKeyId`/`SecretAccessKey`/`BucketName`/`Region`, credenciales propias). Lo único que sigue exigiendo configuración explícita antes de escalar: el backplane de SignalR (`SignalR__Redis__Activo=true` + `SignalR__Redis__CadenaConexion`, servicio Redis de Railway u otro) — sin él, un circuito de Blazor Server no sobrevive a que el balanceador cambie de réplica a mitad de sesión. Los tres arrancan con verificación de conectividad en el log (mismo patrón que KMS/AlmacenamientoS3): revisa el arranque tras activarlos antes de escalar de verdad.
- **Backups.** Automatizados con `Backups__Activo=true` (`pg_dump` de la base de datos + `dataprotection-keys/` a S3, ver `RUNBOOK-CLAVES.md`) — no dependen de la política de backups de volúmenes de Railway.
- **Cifrado de las claves de Data Protection en reposo.** Con `DataProtection__Kms__*` configurado (ver tabla más arriba y `RUNBOOK-CLAVES.md` § KMS), las claves se cifran con AWS KMS antes de escribirse al volumen — confírmalo en el log de arranque (`cifrado con AWS KMS operativo`). Sin esas variables, quedan sin cifrar (advertencia esperada en los logs).
