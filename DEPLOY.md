# Desplegar CAE Manager en Railway

Guía para dejar CAE Manager accesible por navegador para todo el equipo (piloto/pruebas), sin que nadie necesite instalar .NET.

## Por qué Railway

CAE Manager usa SQLite y guarda los PDFs adjuntos en disco local (ver `ARCHITECTURE.md`) — una elección deliberada para v1, pensada para una única instancia. Railway encaja bien con eso: despliega directo desde el repo de GitHub, sirve HTTPS automáticamente y soporta un volumen persistente sencillo. **No escales el servicio a más de 1 réplica** — SQLite no soporta escritura concurrente desde varios procesos.

## 1. Crear el proyecto en Railway

1. Entra en [railway.app](https://railway.app) y crea una cuenta (o inicia sesión).
2. "New Project" → "Deploy from GitHub repo" → selecciona `christopherjp1-jpg/Project-Hydra`.
3. Railway detecta el `Dockerfile` en la raíz del repo automáticamente y lo usa para construir la imagen — no hace falta configurar nada más para el build.
4. En la pestaña **Settings** del servicio, en "Networking", pulsa "Generate Domain" para obtener una URL pública tipo `algo.up.railway.app`.

## 2. Añadir un volumen persistente

Sin esto, la base de datos, los PDFs adjuntos y las claves de cifrado se pierden en cada redeploy.

1. En el servicio, pestaña **Volumes** → "New Volume".
2. Mount path: `/data`.
3. Tamaño: 1 GB es de sobra para empezar (se puede ampliar después).

## 3. Variables de entorno

En la pestaña **Variables** del servicio, añade:

| Variable | Valor | Para qué |
|---|---|---|
| `ConnectionStrings__CaeManagerDb` | `Data Source=/data/CaeManager.db` | Base de datos SQLite en el volumen persistente |
| `AlmacenamientoArchivos__Ruta` | `/data/documentos` | PDFs adjuntos de Documentos, en el volumen |
| `DataProtection__RutaClaves` | `/data/dataprotection-keys` | Claves de cifrado de credenciales (Empresa/Centro) — si no se persisten, cada redeploy invalida las credenciales ya guardadas |
| `AdministradorInicial__Email` | (tu elección, p. ej. `admin@ProjectHydra.com`) | Evita arrancar con el email de administrador por defecto, público en el propio código |
| `AdministradorInicial__Contrasena` | (una contraseña real, mínimo 10 caracteres) | Igual que arriba, para la contraseña |
| `DatosPrueba__Activo` | `true` (opcional, solo para un entorno de pruebas) | Siembra una base de datos genérica de cientos de filas por entidad — ver más abajo |
| `Logging__RutaArchivo` | (opcional, por defecto `App_Data/logs/log-.txt` relativo al volumen) | Ruta del log estructurado de Serilog en disco (consola + archivo con rotación diaria) — ver "Iniciativa de hardening" en `ROADMAP.md` |
| `Sentry__Dsn` | (opcional, vacío por defecto) | Activa el error tracking de Sentry si se rellena con el DSN de un proyecto real — sin esta variable la SDK queda completamente inerte, no hace falta tener cuenta de Sentry para desplegar |
| `Backups__Activo` | `true` (opcional, por defecto `false`) | Activa el backup automático diario de `CaeManager.db` + `dataprotection-keys/` a S3 — ver `RUNBOOK-CLAVES.md` |
| `Backups__Aws__AccessKeyId` / `Backups__Aws__SecretAccessKey` | credenciales de un usuario IAM con permisos solo sobre el bucket de backups (`s3:PutObject`/`GetObject`/`ListBucket`) | Nunca uses las credenciales root de la cuenta de AWS para esto |
| `Backups__Aws__BucketName` | nombre del bucket S3 | |
| `Backups__Aws__Region` | región del bucket, p. ej. `eu-south-2` | |
| `Backups__IntervaloHoras` | `24` (opcional, es el valor por defecto) | Cada cuánto corre el backup |

Producción y staging pueden usar las mismas variables de `Backups__Aws__*` (mismo bucket) sin pisarse los backups entre sí — cada uno sube a su propia carpeta dentro del bucket, identificada automáticamente por el nombre del servicio en Railway.

| Variable | Valor | Para qué |
|---|---|---|
| `Anthropic__ApiKey` | tu API key de [console.anthropic.com](https://console.anthropic.com) | Activa el chat "Pregúntale a Hydra" (botón flotante) — sin esta variable el botón ni se muestra. Si la key tiene fecha de expiración (recomendado), hay que rotarla en Railway antes de que caduque o el chat deja de responder |
| `Anthropic__Modelo` | (opcional, por defecto `claude-sonnet-5`) | Modelo usado para el chat |

Las dos variables de `AdministradorInicial` solo se aplican **la primera vez que arranca** (cuando todavía no existe ningún usuario administrador) — si el servicio ya arrancó una vez sin ellas, cambia la contraseña desde `/usuarios` en vez de tocar estas variables.

### Datos de prueba para pruebas de carga y verificación de perfiles

Con `DatosPrueba__Activo=true`, el primer arranque siembra automáticamente (solo si todavía no hay ningún Cliente — no duplica en redeploys posteriores):

- 200 Clientes, 220 Empresas, 200 Subcontratas, ~300 Centros, 500 Trabajadores (repartidos entre Empresa y Subcontrata) y ~1000 Documentos con fechas de vencimiento repartidas entre vencido/urgente/próximo/vigente.
- 3 usuarios de prueba por cada uno de los 6 perfiles (Administrador/DireccionCae/CoordinadorCae/GestorCae/Consulta/Cliente), con email `prueba.<rol><n>@caemanager.local` y contraseña `Prueba#2026` para todos — así se puede iniciar sesión con cada perfil y comprobar qué ve cada uno con volumen real de datos.

**No actives esto en el entorno real del equipo** — pensado para un servicio aparte (o una base de datos que luego se descarta) dedicado solo a pruebas de carga y QA.

`PORT` la asigna Railway automáticamente — no hace falta configurarla, el contenedor ya la lee al arrancar (ver `Dockerfile`).

## 4. Desplegar

Con el Dockerfile detectado, el volumen montado y las variables puestas, Railway despliega automáticamente en cada push a la rama configurada (por defecto, la rama por defecto del repo — cámbiala en Settings → "Source" si quieres desplegar desde otra, p. ej. `claude/cae-manager-setup-2995sb`).

Primer arranque: la app ejecuta las migraciones de base de datos y crea el usuario administrador automáticamente (ver `Program.cs` → `IdentitySeeder`) — no hace falta ningún paso manual.

## 5. Verificar

- `https://tu-dominio.up.railway.app/salud` debe responder `200 ok` (endpoint sin autenticación, pensado para healthchecks).
- `https://tu-dominio.up.railway.app/` debe redirigir a la pantalla de inicio de sesión.
- Inicia sesión con el email/contraseña de `AdministradorInicial` que configuraste en el paso 3.

## Notas para producción real (fuera de alcance de un piloto)

- **Una sola réplica.** Si el equipo crece y hace falta más capacidad, el paso siguiente es migrar de SQLite a PostgreSQL (la arquitectura ya está preparada para eso, ver `ROADMAP.md` → "Fuera de alcance de v1") antes de escalar horizontalmente.
- **Backups del volumen.** Railway no hace backups automáticos de los volúmenes en todos los planes — revisa la política de tu plan o exporta `CaeManager.db` periódicamente.
- **Cifrado de las claves de Data Protection en reposo.** Hoy se persisten en el volumen sin una capa adicional de cifrado del propio archivo de claves (advertencia esperada en los logs: "No XML encryptor configured") — aceptable para un piloto donde el acceso al volumen ya está restringido; para un despliegue más sensible, considera `ProtectKeysWithCertificate` o un almacén de claves gestionado (Azure Key Vault, AWS KMS).
