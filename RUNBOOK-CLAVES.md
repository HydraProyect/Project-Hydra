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

- Hoy Railway no hace backup automático del volumen en todos los planes (`DEPLOY.md` ya lo marca como pendiente de resolver) — revisa el plan actual y actívalo si existe, o programa un backup manual periódico.
- Un backup de `CaeManager.db` **sin** `dataprotection-keys/` (o viceversa) no sirve de nada útil por sí solo para las credenciales cifradas — ver la sección siguiente. Cualquier mecanismo de backup que se monte debe copiar **los dos juntos, como una unidad atómica**, no por separado ni en momentos distintos.
- Opción simple sin infraestructura nueva: un job programado (fuera de Railway, o un cron en el propio contenedor si se añade) que haga `sqlite3 /data/CaeManager.db ".backup /tmp/backup.db"` (backup consistente sin bloquear escrituras) + copie `dataprotection-keys/` al mismo destino externo (S3, un bucket, lo que ya use el equipo) en la misma operación.
- Alternativa más robusta a mediano plazo: `ProtectKeysWithCertificate` o un almacén gestionado (Azure Key Vault, AWS KMS) en vez de archivos en el volumen — elimina este riesgo de raíz porque las claves dejan de vivir junto a los datos que protegen. Ver también `ROADMAP.md` → "Iniciativa de hardening" § 4.

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
