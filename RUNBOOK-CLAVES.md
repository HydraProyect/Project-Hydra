# Runbook — recuperación si se pierde el volumen de Railway

Este documento cubre exactamente un escenario: el volumen persistente de Railway (montado en `/data`, ver `DEPLOY.md`) se pierde, se corrompe, o hay que restaurarlo desde un backup. Léelo *antes* de que pase, no durante — el error más caro de este escenario se comete en los primeros cinco minutos.

## Qué hay en `/data`

| Ruta | Qué es | Qué pasa si se pierde |
|---|---|---|
| `CaeManager.db` | La base de datos SQLite completa: Clientes, Empresas, Trabajadores, Documentos, todo. | Pérdida de datos obvia — es la que todo el mundo piensa primero. |
| `dataprotection-keys/` | Las claves de cifrado de ASP.NET Core Data Protection. | **La menos obvia y la más grave** — ver siguiente sección. |
| `documentos/` | Los PDFs adjuntos de los Documentos. | Los archivos en sí desaparecen (las filas de la BD que los referencian quedan huérfanas). |

## El punto crítico: las claves de Data Protection no son solo "para las cookies"

`dataprotection-keys/` no es un detalle de sesión — es lo que cifra **en reposo** las credenciales de acceso a plataformas externas (`CredencialAccesoEmpresa`, `CredencialAccesoSubcontrata`, ver `CredencialAccesoEmpresaConfiguration.cs`/`CredencialAccesoSubcontrataConfiguration.cs` y el `ValueConverter` en `CaeManagerDbContext.cs`), documentado en `ARCHITECTURE.md` § cifrado de credenciales.

Esto significa: **si `dataprotection-keys/` se pierde pero `CaeManager.db` sobrevive**, cada fila de credencial guardada (usuario/contraseña de portales tipo CTAIMA) sigue estando en la base de datos, pero como bytes cifrados con una clave que ya no existe en ningún sitio. No hay fuerza bruta, no hay "recuperar la clave" — es matemáticamente irrecuperable. La fila sigue ahí, pero es basura permanente.

Esto **no** afecta a las contraseñas de los propios usuarios de CAE Manager (esas usan el hash de ASP.NET Identity, no Data Protection) — solo a las credenciales de plataformas externas guardadas en Empresa/Subcontrata.

## Prevención

- **Implementado (2026-07-18)**: `BackupHostedService` (`src/CaeManager.Infrastructure/Backups/`) sube automáticamente `CaeManager.db` + `dataprotection-keys/` **juntos, en la misma operación**, a un bucket de S3 — cada `Backups:IntervaloHoras` (24h por defecto) y una vez más al arrancar el proceso. La base de datos se respalda con `SqliteConnection.BackupDatabase` (el mecanismo online de SQLite, no bloquea escrituras mientras corre) y las claves se comprimen en un `.zip`. Apagado por defecto (`Backups:Activo=false`, mismo patrón que `DatosPrueba:Activo`) — no intenta nada sin cuenta de AWS configurada. Variables necesarias en Railway: `Backups__Activo=true`, `Backups__Aws__AccessKeyId`, `Backups__Aws__SecretAccessKey`, `Backups__Aws__BucketName`, `Backups__Aws__Region` (ver `DEPLOY.md`).
  - Producción y staging pueden compartir el mismo bucket/credenciales sin pisarse — cada backup se sube bajo un prefijo `{RAILWAY_SERVICE_NAME}/{fecha-hora}/`, tomado automáticamente de la variable que Railway ya inyecta por servicio.
  - Retención: el bucket tiene versionado activado (recomendado al crearlo) para poder recuperar una versión anterior si un backup corrupto sobrescribe uno bueno. Para borrar automáticamente backups viejos, configura una **Lifecycle rule** en el propio bucket de S3 (consola de AWS → el bucket → pestaña *Management* → *Create lifecycle rule* → expirar objetos con más de N días) — no hay borrado automático implementado en la app a propósito, para no arriesgarse a borrar algo que todavía hiciera falta por un bug.
- Alternativa más robusta a mediano plazo, todavía sin implementar: `ProtectKeysWithCertificate` o un almacén gestionado (Azure Key Vault, AWS KMS) en vez de archivos en el volumen — elimina el riesgo de raíz porque las claves dejan de vivir junto a los datos que protegen. Ver también `ROADMAP.md` → "Iniciativa de hardening" § 4.

## Recuperación — desde un backup en S3

1. En la consola de AWS → S3 → el bucket configurado → entra a la carpeta `{nombre-del-servicio}/` (el nombre que le pusiste al servicio en Railway) → elige la carpeta con la fecha-hora más reciente antes del incidente.
2. Descarga los dos archivos de esa carpeta: `CaeManager.db` y `dataprotection-keys.zip`.
3. Sube `CaeManager.db` al volumen de Railway (`/data/CaeManager.db`) y descomprime `dataprotection-keys.zip` dentro de `/data/dataprotection-keys/` — **los dos juntos, del mismo backup**, nunca mezclando fechas distintas entre uno y otro.
4. Sigue con la sección "Recuperación — con backup disponible" de más abajo para el resto de la verificación.

## Recuperación — con backup disponible

1. **Restaura `CaeManager.db` y `dataprotection-keys/` juntos, del mismo backup, en la misma operación.** Este es el error #1 a evitar: restaurar solo la base de datos (porque "es lo importante") y dejar que Railway/la app genere una carpeta de claves nueva desde cero. Si eso pasa, acabas con una base de datos íntegra pero con todas las credenciales cifradas ya ilegibles — el mismo resultado que no tener backup en absoluto, solo que sin darte cuenta hasta que alguien intente usar una credencial guardada.
2. Verifica que el servicio arranca limpio (revisa los logs de Railway o, si tienes acceso, `/salud`) — la app corre sus migraciones pendientes automáticamente al arrancar (`Program.cs`, `dbContext.Database.MigrateAsync()`), así que un backup de una versión de esquema anterior sigue funcionando.
3. Como verificación puntual: entra como Administrador, abre una Empresa que tuviera credenciales guardadas antes del incidente, y confirma que se ven correctamente (no como error/basura). Si algo salió mal en el paso 1, este es el primer sitio donde se nota.

## Recuperación — sin backup (el volumen se perdió y no había nada que restaurar)

No hay atajo aquí — es una recuperación operativa, no técnica:

1. Railway crea un volumen nuevo y vacío en `/data`. Al arrancar, la app corre las migraciones desde cero y queda con una base de datos limpia (o sembrada con datos de prueba si `DatosPrueba__Activo=true` sigue puesto — **quítalo** si este era un entorno con datos reales, para no mezclar datos de prueba con producción).
2. Todo el contenido de negocio hay que volver a cargarlo: Clientes, Empresas, Centros, Trabajadores, Documentos — desde la fuente original que se haya usado la primera vez (importación por Excel, ver `ROADMAP.md` Fases 5/18, o alta manual).
3. **Las credenciales de acceso a plataformas externas (Empresa/Subcontrata) no se pueden recuperar de ningún sitio automatizado** — hay que volver a pedírselas a quien las tenga (el propio cliente, o quien gestione el acceso a esa plataforma) y volver a introducirlas manualmente, una por una, desde `/empresas` y `/subcontratas`.
4. Comunica explícitamente a quien gestione la relación con cada cliente afectado que sus credenciales guardadas se perdieron y hay que volver a capturarlas — no asumas que "ya estarán" en algún otro sitio.
