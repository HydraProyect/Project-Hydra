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
| `ConversionWordPdf__TimeoutSegundos` | (opcional, por defecto `60`) | Tiempo máximo que se espera a LibreOffice al convertir un Word (.docx) a PDF antes de darlo por fallado — no suele hacer falta tocarlo |
| `DatosPrueba__Activo` | `true` (opcional, solo para un entorno de pruebas) | Siembra una base de datos genérica de cientos de filas por entidad — ver más abajo |
| `Logging__RutaArchivo` | (opcional, por defecto `App_Data/logs/log-.txt` relativo al volumen) | Ruta del log estructurado de Serilog en disco (consola + archivo con rotación diaria) — ver "Iniciativa de hardening" en `ROADMAP.md` |
| `Sentry__Dsn` | (opcional, vacío por defecto) | Activa el error tracking de Sentry si se rellena con el DSN de un proyecto real — sin esta variable la SDK queda completamente inerte, no hace falta tener cuenta de Sentry para desplegar |
| `Backups__Activo` | `true` (opcional, por defecto `false`) | Activa el backup automático diario de `CaeManager.db` + `dataprotection-keys/` a S3 — ver `RUNBOOK-CLAVES.md` |
| `Backups__Aws__AccessKeyId` / `Backups__Aws__SecretAccessKey` | credenciales de un usuario IAM con permisos solo sobre el bucket de backups (`s3:PutObject`/`GetObject`/`ListBucket`) | Nunca uses las credenciales root de la cuenta de AWS para esto |
| `Backups__Aws__BucketName` | nombre del bucket S3 | |
| `Backups__Aws__Region` | región del bucket, p. ej. `eu-south-2` | |
| `Backups__IntervaloHoras` | `24` (opcional, es el valor por defecto) | Cada cuánto corre el backup |
| `DataProtection__Kms__Activo` | `true` (opcional, por defecto `false`) | Cifra las claves de Data Protection con AWS KMS antes de escribirlas al volumen — sin esto viajan **en claro** dentro del mismo backup que la base de datos que protegen (`RUNBOOK-CLAVES.md` § KMS) |
| `DataProtection__Kms__KeyId` | ARN o alias de la clave, p. ej. `alias/caemanager-dataprotection` | Clave simétrica de cifrado/descifrado, en la misma región que el bucket |
| `DataProtection__Kms__AccessKeyId` / `DataProtection__Kms__SecretAccessKey` | credenciales de un usuario IAM **distinto** del de backups, con permiso solo de `kms:Encrypt`/`kms:Decrypt` sobre esa clave | Separarlo del de backups es lo que hace que filtrar las credenciales de S3 no dé también la llave de lo que hay dentro |
| `DataProtection__Kms__Region` | región de la clave, p. ej. `eu-south-2` | Debe coincidir con la región donde se creó la clave |

Las cuatro variables de `DataProtection__Kms__*` van juntas: si falta cualquiera, el cifrado queda apagado y el arranque lo advierte por log. Con todas puestas, el arranque hace un cifrado/descifrado de prueba y deja dicho si la clave responde — busca `cifrado con AWS KMS operativo` en el log del despliegue.

Producción y staging pueden usar las mismas variables de `Backups__Aws__*` (mismo bucket) sin pisarse los backups entre sí — cada uno sube a su propia carpeta dentro del bucket, identificada automáticamente por el nombre del servicio en Railway.

| Variable | Valor | Para qué |
|---|---|---|
| `Anthropic__ApiKey` | tu API key de [console.anthropic.com](https://console.anthropic.com) | Activa el chat "Pregúntale a Hydra" (botón flotante) — sin esta variable el botón ni se muestra. Si la key tiene fecha de expiración (recomendado), hay que rotarla en Railway antes de que caduque o el chat deja de responder |
| `Anthropic__Modelo` | (opcional, por defecto `claude-sonnet-5`) | Modelo usado para el chat |

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
