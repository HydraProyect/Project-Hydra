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
- [ ] Tanda 2 — Ciclo documental avanzado
- [ ] Tanda 3 — Centros bloqueantes y canal WhatsApp
- [ ] Tanda 4 — Plataforma: delegaciones, soporte, identidad, retención
- [ ] Tanda 5 — Infraestructura y menores

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
- **Acreditaciones CAE**: `AcreditacionDocumentoPlataforma` en los 5 `EstadoAcreditacion`,
  con rechazos que cubran varias `CausaRechazoAcreditacion`, contra proveedores del catálogo.
- **Detección de trabajadores**: `DeteccionTrabajador` de tipo `Nuevo` y `Ausente`, pendientes
  y resueltas, en 2-3 empresas.

## Tanda 3 — Centros bloqueantes y canal WhatsApp

- **Canales de gestión documental**: `CanalGestionDocumental` por centro (tipo `Plataforma`
  con proveedor del catálogo y tipo `Email`; uno marcado principal) en una muestra de centros.
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
- **Soporte**: una `DelegacionTenant` de soporte **vigente** (motivo + caducidad futura) y una
  **caducada**, más `RegistroActividadSoporte` de ejemplo de los 5 `TipoActividadSoporte`.
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

1. **Vencimiento en documentos de vehículo** (Tanda 2/3): hoy los 4 tipos de vehículo tienen
   `AplicaVencimiento = false`, así que no puede existir un vehículo con documentación vencida.
   Cubrir esa variante exige cambiar el catálogo (¿ITV con vencimiento?) — es decisión de
   producto, no de siembra.
2. **Integraciones simuladas** (Tanda 5): ¿se siembran conexiones falsas o se deja la pantalla
   fuera del requerimiento por depender de servicios externos?
3. **Retención** (Tanda 4): sembrar solicitudes ejecutadas es historia sintética de un flujo
   RGPD — confirmar que se acepta como dato de demo (CLAUDE.md exige confirmar lo que roce
   cumplimiento normativo).
