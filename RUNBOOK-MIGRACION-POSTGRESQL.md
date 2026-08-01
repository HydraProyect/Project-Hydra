# Runbook — Corte de SQLite a PostgreSQL (ADR-003)

**Histórico — el corte ya se ejecutó en producción el 2026-08-01** (ver
`ROADMAP.md` § migración a PostgreSQL, "Corte a producción"). Se documenta
aquí como registro de cómo se hizo, no como procedimiento a repetir: en la
práctica, la producción de entonces solo tenía datos de prueba, así que el
corte real fue más simple que lo escrito abajo — Postgres arrancó vacío y la
propia app lo migró/resembró al arrancar, sin ejecutar ningún migrador de
datos. La rama SQLite (incluida la clase `MigradorDatosPostgreSql` que
describe este documento) se retiró del código después del corte — ver
"Después del corte" más abajo. Si algún día hiciera falta migrar datos reales
desde una fuente externa, hay que escribir esa herramienta de nuevo; esta ya
no existe.

Procedimiento original, con ventana de parada y camino de vuelta **escritos
antes de tocar nada** (exigencia del punto 7 del backlog de migración,
`ROADMAP.md`). La herramienta de copia era `MigradorDatosPostgreSql` (modo de
un solo uso de la propia app); su ensayo completo en local —siembra real →
copia → recuentos idénticos en 50 tablas → login y páginas sobre la base
migrada— quedó hecho antes del corte real (2026-08-01, ver ROADMAP § migración
a PostgreSQL).

## Principio del diseño

El volumen de SQLite **no se toca en ningún paso**. La migración lee de una
copia (el backup de S3) y escribe solo en el PostgreSQL nuevo; el camino de
vuelta es siempre "quitar dos variables de entorno y arrancar como antes".
Las claves de Data Protection tampoco se mueven: siguen en el mismo volumen,
y el texto cifrado se copia intacto — por eso la migración no rompe ninguna
credencial cifrada (`RUNBOOK-CLAVES.md`).

## Prerrequisitos (antes de anunciar ninguna ventana)

1. Servicio de PostgreSQL creado en Railway **en la región `europe-west4`**
   (UE — ver `RGPD-TRATAMIENTO-DATOS.md` § 6) y su connection string en
   formato palabra clave de Npgsql (`Host=...;Port=...;Database=...;
   Username=...;Password=...;SSL Mode=Require`) — la URL `postgresql://` que
   muestra Railway hay que traducirla a este formato.
2. Comprobada la región real del servicio de la app (mismo § 6).
3. Backups automáticos funcionando (último backup en S3 < 24 h).
4. PostgreSQL instalado en la máquina desde la que se ejecuta la migración
   (ya está: PostgreSQL 17 local) y el repositorio compilado en `main`.

## Ventana de parada

1. **Parar la app en Railway** (Settings → eliminar el despliegue activo o
   escalar a 0). Desde aquí nadie escribe en el SQLite de producción.
2. **Backup manual final**: si el último backup automático no es de después
   de la parada, arrancar una vez con `Backups__IntervaloHoras=1` o forzar
   redeploy (hace backup al arrancar) y volver a parar. Confirmar en S3 el
   par `CaeManager.db` + `dataprotection-keys.zip` con marca de tiempo
   posterior a la parada.
3. **Descargar `CaeManager.db`** de ese backup a la máquina local.
4. **Ejecutar la migración** desde el repositorio, apuntando al PostgreSQL
   de Railway:

   ```bash
   dotnet run --project src/CaeManager.Web -- \
     "--ConnectionStrings:CaeManagerDb=Data Source=C:\ruta\al\CaeManager.db" \
     "--MigracionDatosPostgreSql:Destino=<connection string de Railway>"
   ```

   El proceso migra el esquema (línea base), copia las ~50 tablas en una
   única transacción y verifica recuento por tabla; termina solo, sin
   arrancar servidor. Si el destino ya tuviera datos reales se niega —
   `MigracionDatosPostgreSql:SobrescribirDestino=true` solo si se está
   repitiendo un corte fallido y se sabe por qué.
5. **Éxito = la línea final** `Migración de datos completada y verificada: N
   tablas con recuentos idénticos`. Cualquier excepción → el destino no se
   usa; ver "Camino de vuelta".
6. **Reconfigurar el servicio en Railway** (dos variables):
   - `Database__Proveedor=PostgreSql`
   - `ConnectionStrings__CaeManagerDb=<connection string de Railway>`
7. **Arrancar y verificar** (esto es la verificación end-to-end en navegador
   que exige `CLAUDE.md` para cerrar la fase):
   - `/salud` responde `ok`;
   - login real, Dashboard con los KPI esperados, listados de Clientes /
     Trabajadores / Documentos con los datos de siempre;
   - abrir una credencial de acceso guardada (Empresa o Subcontrata) y ver
     que descifra — es la prueba de que base y claves siguen emparejadas;
   - una escritura cualquiera (editar una nota) y su fila en
     `RegistrosAuditoria`;
   - en los logs, el backup de arranque subiendo ahora `CaeManager.dump`
     (pg_dump) a S3.

## Camino de vuelta

En cualquier punto, incluido después del arranque en PostgreSQL:

1. Quitar `Database__Proveedor` y devolver `ConnectionStrings__CaeManagerDb`
   a la ruta del SQLite del volumen.
2. Redeploy. La app arranca sobre el SQLite intacto, exactamente donde se
   paró en el paso 1.
3. Si el corte llegó a estar en producción un tiempo antes de volver, las
   escrituras hechas sobre PostgreSQL en ese intervalo **se pierden al
   volver** — por eso la verificación del paso 7 se hace antes de dar la
   ventana por cerrada, no al día siguiente.

La base PostgreSQL fallida se borra y se repite el corte otro día; no se
"arregla" a mano.

## Después del corte (no en la ventana)

- ✅ **Retirada la rama SQLite** (2026-08-01): `ProveedorBaseDatos`,
  `LectorProveedorBaseDatos`, el juego de migraciones de Infrastructure,
  `MigradorDatosPostgreSql`, `BackfillTenantPorDefectoTests`, y la rama
  SQLite de `BackupHostedService` — todo eliminado. La app ya no sabe hablar
  SQLite; `WebAppFixture` (E2E) también se movió a PostgreSQL.
- El volumen conserva `dataprotection-keys/` y los archivos subidos
  (`AlmacenamientoArchivos`): sigue siendo necesario. `CaeManager.db` ya no
  existe en ningún sitio — la base de datos vive en el servicio de Postgres
  de Railway, no en el volumen.
- ✅ Actualizados `DEPLOY.md`/`RGPD-TRATAMIENTO-DATOS.md`/`ARCHITECTURE.md`/
  `RUNBOOK-CLAVES.md` con el estado final; punto cerrado en `ROADMAP.md`.
