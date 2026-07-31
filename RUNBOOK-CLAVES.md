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
- **Implementado (2026-07-31)**: cifrado de las propias claves con **AWS KMS** — ver la sección siguiente. Elimina el riesgo de raíz del backup: las claves siguen viviendo en el volumen, pero ya no en claro, y la clave maestra que las abre nunca sale de AWS.

## KMS — cifrado en reposo de las claves de Data Protection

Sin esto, `dataprotection-keys/` viaja **en claro dentro del mismo backup** que la base de datos que protege. Quien consiguiera ese archivo tendría a la vez el candado y la llave: las credenciales de portales externos de Empresas y Subcontratas quedarían legibles. Con KMS, el backup por sí solo ya no descifra nada.

### Alta de la clave (una vez)

1. **Consola de AWS → KMS → Customer managed keys → Create key**, en la región del bucket de backups (`eu-south-2` / Europa-España, para no sacar datos de España — `RGPD-TRATAMIENTO-DATOS.md` § 6).
2. Tipo **Symmetric**, uso **Encrypt and decrypt** (los valores por defecto).
3. Alias: `caemanager-dataprotection`. Se usa como `alias/caemanager-dataprotection` — mejor que el ARN, porque sobrevive a una sustitución de clave.
4. **IAM → Users → Create user**, distinto del usuario de backups. Sin acceso a consola, solo clave de acceso programático. Política en línea, acotada a esa clave:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["kms:Encrypt", "kms:Decrypt", "kms:DescribeKey"],
    "Resource": "arn:aws:kms:eu-south-2:TU_CUENTA:key/EL_ID_DE_LA_CLAVE"
  }]
}
```

   Separar este usuario del de backups es lo que hace que filtrar las credenciales de S3 no entregue también la llave de lo que hay dentro. Sin `kms:*` fuera de esa clave y sin `kms:ScheduleKeyDeletion`: la aplicación nunca necesita poder borrar la clave, y un usuario que no puede borrarla tampoco puede destruir los datos por accidente.

5. **Habilita la rotación automática** en la clave (pestaña *Key rotation*). KMS guarda el material anterior, así que lo cifrado antes se sigue descifrando — el `KmsXmlDecryptor` no necesita saber qué versión cifró cada cosa.

### Puesta en marcha

Las cuatro variables van a **Railway → servicio → Variables** (nunca al repositorio):

```
DataProtection__Kms__Activo=true
DataProtection__Kms__KeyId=alias/caemanager-dataprotection
DataProtection__Kms__Region=eu-south-2
DataProtection__Kms__AccessKeyId=...
DataProtection__Kms__SecretAccessKey=...
```

En local, con `dotnet user-secrets` (nunca en `appsettings.json`, que sí se versiona):

```bash
dotnet user-secrets set "DataProtection:Kms:SecretAccessKey" "..." --project src/CaeManager.Web
```

### Verificación

El arranque hace un cifrado y descifrado de prueba contra la clave y lo deja escrito en el log:

- `Data Protection: cifrado con AWS KMS operativo` → funciona.
- `KMS está activado pero la clave ... no responde` → revisa credenciales, región y permisos. La aplicación **arranca igual**: negarse a arrancar convertiría una incidencia pasajera de AWS en una caída del producto, y con la configuración mal puesta va a fallar igualmente al primer uso real de la clave.

Si falta cualquiera de las cuatro variables, el cifrado queda apagado y el arranque lo advierte — un despliegue que crea estar cifrando y no lo esté es peor que uno que sepa que no lo está.

### Lo que activar KMS **no** arregla

Data Protection cifra cada clave cuando la crea; **no vuelve atrás a cifrar las que ya existen**. Es decir:

- Las claves que ya están en el volumen siguen en claro, y son justamente las que descifran las credenciales guardadas hasta hoy.
- Los backups anteriores a la activación siguen conteniendo esas claves en claro. Rotar la clave de KMS no los cambia.

Cerrar eso del todo es una migración aparte: activar KMS, forzar la creación de una clave nueva (que ya nace cifrada), volver a guardar las credenciales existentes para que se cifren con ella, y solo entonces retirar las claves antiguas del volumen y purgar los backups viejos. Hasta que eso se haga, lo correcto es asumir que **las credenciales de portales externos anteriores a la activación siguen expuestas** en cualquier copia antigua del backup.

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
