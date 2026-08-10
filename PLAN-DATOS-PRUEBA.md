# Plan de extensión de datos de prueba (Requerimiento global nº 1)

Auditoría 2026-08-10: de ~20 módulos funcionales, 13 no tienen ningún dato de prueba y ~10 más
tienen variantes sin cubrir. Este plan cierra esos huecos por tandas ejecutables, cada una un
cambio independiente sobre `src/CaeManager.Infrastructure/Persistence/Seed/`.

**Reglas transversales (aplican a toda tanda, ver CLAUDE.md § Reglas de trabajo):** por tenant y
con el filtro global activo; idempotente (guard "ya sembrado" por tenant); apagado por defecto
(`DatosPrueba:Activo`); nombres ficticios de caricaturas; determinista (`Random` con semilla
fija); no romper el reparto exacto de clientes por gestor del que depende `AlcanceRolesTests`.
Cada tanda termina con verificación end-to-end en navegador de las pantallas que alimenta.

## Estado

- [x] Tanda 1 — Módulos con pantalla vacía del núcleo operativo (2026-08-10, verificada vía suite E2E de CI con `DatosPrueba:Activo`)
- [x] Tanda 2 — Ciclo documental avanzado (2026-08-10; acreditaciones CAE movidas a la Tanda 3, ver nota)
- [x] Tanda 3 — Centros bloqueantes y canal WhatsApp (2026-08-10; las líneas WhatsApp exigen
  `ConexionIntegracion` — se siembran dos conexiones WhatsApp simuladas con token de demo, lo
  que resuelve parcialmente la decisión abierta nº 2; la decisión sigue abierta solo para las
  conexiones Microsoft 365 de la Tanda 5)
- [x] Tanda 4 — Plataforma: delegaciones, soporte, identidad (2026-08-10) — **parcial**: la
  retención (`SolicitudPurga`) sigue fuera a la espera de la decisión abierta nº 3
- [x] Tanda 5 — Infraestructura y menores (2026-08-10) — **parcial**: las conexiones
  Microsoft 365 simuladas siguen fuera a la espera de la decisión abierta nº 2 (las WhatsApp
  ya se sembraron en la Tanda 3 por ser dependencia del canal)

## Tanda 1 — Módulos con pantalla vacía del núcleo operativo

Lo que un usuario de gestión ve vacío hoy nada más entrar.

- **Proyectos**: 4-6 `Proyecto` en tenant demo 1 (abiertos y cerrados), con `ProyectoTecnico`
  asignados y alguno desasignado. Añadir `TarifaCliente` de los 3 conceptos de proyecto
  (`TecnicoAsignadoProyecto`, `GestionProyectoRealizada`, `DiaProyectoAbierto`) y de
  `VisitaTrabajadorExtranjero` para completar los 7 `ConceptoFacturable`.
- **Gestiones**: `Gestion` en ambos estados (`Pendiente`, `Completada`) ligadas a trabajadores
  existentes.
- **Notificaciones**: `NotificacionUsuario` leídas y no leídas para varios usuarios `prueba.*`.
- **Reclamaciones**: 2-3 `ReclamacionDocumental` con sus `ReclamacionDocumentalDocumento`.

## Tanda 2 — Ciclo documental avanzado

Todo el pipeline que hoy solo existe como flags de catálogo.

- **Aprobación manual**: marcar algún `TipoDocumento` como `TipoAprobacionDocumento.Manual` en
  los tenants demo y sembrar `AprobacionDocumento` (aprobadas y pendientes).
- **Revisión IA**: `RevisionIaDocumento` pendientes y resueltas; `TrabajoAnalisisDocumento` en
  los 4 estados (`Pendiente`, `Procesando`, `Completado`, `Fallido`); `AuditoriaExtraccionIa`
  para que `/auditoria-ia` y `/clientes/{id}/lectura-ia` muestren datos.
- **Validación oficial**: `FirmaDigitalDocumento` + `VerificacionDocumentoOficial` con
  combinaciones representativas (no exhaustivas): `AutoValidado`, `RevisionRequerida`,
  `SinFirmaValida`; cotejo `Coincide` y `Discrepancia`; al menos un documento por cada
  `PerfilDocumentoOficial` configurado.
- **Detección de trabajadores**: `DeteccionTrabajador` de tipo `Nuevo` y `Ausente`, pendientes
  y resueltas, en 2-3 empresas.
- ~~Acreditaciones CAE~~ **movida a la Tanda 3**: `AcreditacionDocumentoPlataforma` referencia
  `CanalGestionDocumentalId`, y los canales se siembran en la Tanda 3 — sembrarla antes que
  su dependencia no es posible.
- Nota de ejecución (2026-08-10): los `TrabajoAnalisisDocumento` se siembran en `Completado`,
  `Fallido` y `Procesando`; **no** se siembra ninguno `Pendiente` a propósito — el worker lo
  consumiría al arrancar llamando al proveedor de IA real sobre un documento sin archivo.
  Además se corrigió que la copia de `TipoDocumento` de los tenants de demo perdiera
  `DeteccionTrabajadoresActiva` y `PerfilDocumentoOficial` (el constructor no los expone).

## Tanda 3 — Centros bloqueantes y canal WhatsApp

- **Canales de gestión documental**: `CanalGestionDocumental` por centro (tipo `Plataforma`
  con proveedor del catálogo y tipo `Email`; uno marcado principal) en una muestra de centros.
- **Acreditaciones CAE** (movida desde la Tanda 2 — depende de los canales):
  `AcreditacionDocumentoPlataforma` en los 5 `EstadoAcreditacion`, con rechazos que cubran
  varias `CausaRechazoAcreditacion`.
- **Requisitos documentales por centro**: `TipoDocumentoCentro` en varios centros, con
  requisitos bloqueantes incumplidos en al menos uno → cubre `EstadoCentro.Bloqueado` y la
  documentación bloqueante pendiente.
- **WhatsApp**: `LineaWhatsApp` (modos `GestorFijo` y `PoolInbound` con `MiembroPoolLinea`),
  `ContactoWhatsApp`, y conversaciones con `CanalConversacion.WhatsApp` en varios estados,
  con `EstadoEntregaMensaje` variado (`Enviado`/`Entregado`/`Leido`/`Fallido`).
- **Comunicaciones restantes**: `AdjuntoMensaje`, `EventoConversacion` (los 3 tipos),
  `SugerenciaVisitaCorreo` y `SugerenciaGestionCorreo` pendientes,
  `SolicitudPrioridadDocumento`. Visitas con `OrigenVisita.Correo` y `.WhatsApp`.

## Tanda 4 — Plataforma: delegaciones, soporte, identidad, retención

- **Delegaciones (ADR-004)**: añadir una delegación comercial **revocada** y una **reactivada**;
  crear 2-3 usuarios en el tenant #1 (Consultora) con roles distintos de Administrador y sus
  `AsignacionOperadorDelegado` (incluida una revocada) para probar el Delegated Workspace y
  "retirar operador" desde ambos lados.
  - Nota de ejecución (2026-08-10): la delegación revocada vive en un tercer tenant de demo sin
    datos (`Hosteleria Krusty Krab S.L.`). Dos variantes del plan resultaron **no sembrables por
    diseño del dominio**: una delegación "reactivada" es indistinguible en estado de una activa
    (no hay historial en la entidad), y una `AsignacionOperadorDelegado` "revocada" no existe
    como estado — retirar un operador es una baja física. Ambos flujos se ejercitan desde la UI
    sobre los datos sembrados.
- **Soporte**: una `DelegacionTenant` de soporte **vigente** (motivo + caducidad futura) y una
  **caducada**, más `RegistroActividadSoporte` de ejemplo de los 5 `TipoActividadSoporte`.
  - Nota de ejecución (2026-08-10): la vigente y la traza viven en el **tenant demo 3** y la
    caducada en el demo 2 — la delegación de soporte del demo 1 se deja sin activar a
    propósito porque `FlujoSoporteTests` (E2E) ejercita el ciclo completo sobre ella y
    necesita encontrarla virgen (fallo real de CI al sembrarla activada).
- **Identidad y términos**: un usuario con aceptación de términos de versión antigua (ejercita
  el gate), un usuario sin rol (`/cuenta/pendiente-de-rol`), uno con
  `DebeCambiarContrasena = true`, y un `prueba.*` con 2FA activa.
- **Retención**: sembrar `SolicitudPurga` en los 5 estados. La invariante no se toca: la
  sembrada como `Ejecutada` lleva autorización expresa con usuario y fecha, igual que exige el
  flujo real. Los veteranos actuales se mantienen como candidatos vivos para el barrido.
- **Usuarios del demo 2**: sembrar al menos 1 usuario Administrador y 1 GestorCae en el tenant
  demo 2 (hoy no tiene ninguno).
- **Fix lateral**: `SegundoTenantSeeder` debe crear `ParametroSistema` y la copia de
  `TipoDocumento` de su tenant (hoy no lo hace y las queries con `SingleAsync()` pueden fallar).

## Tanda 5 — Infraestructura y menores

- `ClaveApi` de prueba (activa y revocada) para poder ejercitar la API pública V1.
- `CredencialAccesoEmpresa` / `CredencialAccesoSubcontrata` en una muestra.
- `ConexionIntegracion` simuladas en los 3 estados (`Habilitada`, `Deshabilitada`, `ConError`)
  sin credenciales reales — solo para que `/integraciones` sea probable visualmente.
- `FiltroGuardado` y `PreferenciaDashboardUsuario` para algún usuario `prueba.*`.

## Decisiones abiertas (confirmar con el propietario antes de la tanda que las toca)

1. ~~Vencimiento en documentos de vehículo~~ **Resuelta (2026-08-10)**: el propietario decidió
   que toda la documentación de vehículo vence — los 4 tipos pasan a vigencia anual
   (`VigenciaMeses = 12`, `AplicaVencimientoAutomatico = true`), migración
   `VencimientoAnualDocumentosVehiculo`. Con esto la siembra existente ya produce vehículos
   con documentación en los cuatro estados (el reparto de `CrearDocumento` aplica solo).
2. **Integraciones simuladas** (Tanda 5): ¿se siembran conexiones falsas o se deja la pantalla
   fuera del requerimiento por depender de servicios externos?
3. **Retención** (Tanda 4): sembrar solicitudes ejecutadas es historia sintética de un flujo
   RGPD — confirmar que se acepta como dato de demo (CLAUDE.md exige confirmar lo que roce
   cumplimiento normativo).
