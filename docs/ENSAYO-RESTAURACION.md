# Ensayo de restauración de backups — registro y objetivos RPO/RTO

**Origen**: P0-6 de `docs/business/MATURITY_REVIEW.md` — "La restauración de backups jamás se ha ensayado end-to-end. Un backup no restaurado es una hipótesis."

## Cómo se ensaya

`scripts/ensayo-restauracion.sh` ejecuta la parte automatizable del procedimiento de `RUNBOOK-CLAVES.md` contra un PostgreSQL local desechable (descarga del último backup real de S3, `pg_restore`, verificación de tablas núcleo e integridad del zip de claves de Data Protection). Los dos pasos finales son manuales y el script los imprime al terminar: arrancar la app contra la copia restaurada y comprobar que una credencial guardada se descifra.

El ensayo requiere las credenciales de AWS del bucket de backups y acceso a un backup real de producción — **por eso no puede ejecutarlo una sesión de desarrollo**: lo ejecuta quien opere el despliegue, y anota el resultado abajo.

**Cadencia propuesta**: tras cada cambio en el mecanismo de backup (`BackupHostedService`, formato del dump, KMS) y como mínimo una vez por trimestre. Un ensayo que falla es un incidente de severidad alta aunque producción esté sana: significa que hoy no hay recuperación.

## Objetivos RPO/RTO (propuestos — pendientes de ratificar por el propietario)

| Métrica | Valor propuesto | Base |
|---|---|---|
| **RPO** (pérdida máxima de datos) | **24 horas** | Es el intervalo actual de `Backups:IntervaloHoras`. Ratificarlo = aceptar que un día de trabajo de documentación CAE es re-introducible. Si no es aceptable para un tenant de pago, bajar el intervalo (el backup con `pg_dump --format=custom` no bloquea al servidor) o pasar a WAL archiving — decisión de coste/operación. |
| **RTO** (tiempo máximo de recuperación) | **4 horas laborables** | Procedimiento manual siguiendo `RUNBOOK-CLAVES.md` + este ensayo: localizar backup, restaurar dump + claves juntos, redeploy y verificación. Sin on-call, fuera de horario laboral el RTO real es "hasta la mañana siguiente" — eso debe decirse tal cual en el SLA/Términos de Uso (P0-4), no maquillarse aquí. |

La cola de análisis de IA es durable desde P2-22 (`ITrabajoAnalisisDocumentoRepository`, consumida por `ProcesadorAnalisisDocumentoHostedService`): un reinicio del proceso ya no pierde los encargos pendientes, quedan en Postgres y se retoman al arrancar. El usuario puede además relanzar el análisis desde la ficha del documento.

## Registro de ensayos

| Fecha | Backup usado (prefijo S3) | Ejecutor | Resultado dump | Resultado claves (credencial legible) | Duración total | Notas |
|---|---|---|---|---|---|---|
| _(pendiente del primer ensayo real)_ | | | | | | |
