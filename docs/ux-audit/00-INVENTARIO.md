# Auditoría UX Hydra — Fase 0: Inventario

> Generado: 2026-08-05 (revisado el mismo día con la nota de alcance Inbound/Outbound). Fuente: exploración del código en `src/CaeManager.Web` (páginas y componentes) y `src/CaeManager.Application` (Commands/Queries). Este documento es el índice de las sesiones de auditoría por módulo. **No contiene valoraciones** — solo estructura, flujos y huecos detectados.

## 0. Marco de alcance de la auditoría (nota de alcance 2026-08-05 — vinculante para todas las sesiones)

**Definiciones** (pendientes de alta en `docs/business/UBIQUITOUS_LANGUAGE.md`; `docs/business/PRODUCT_STRATEGY.md` ya usa esta terminología para las plataformas destino):

- **CAE Inbound**: la empresa titular contrata la validación de la documentación que entra en sus centros de trabajo (modelo Dokify/Nalanda/eCoordina/CTAIMA).
- **CAE Outbound**: el contratista contrata la gestión y subida de su documentación hacia las plataformas de las empresas titulares.

**Reglas que hereda cada sesión de Fase 1:**

1. **El MVP actual es CAE Outbound.** Vara de medir por módulo: *"¿esto hace el trabajo del Gestor CAE más rápido y fiable que Excel + operar directamente los portales?"*. Benchmark competitivo directo: **Konvergia** (ver `docs/business/PRODUCT_STRATEGY.md` § "Pregunta abierta: postura frente a Konvergia" y `BENCHMARK_PRECIOS_CAE.md` § 1-bis). Dokify/Nalanda/eCoordina/CTAIMA **no** son benchmark de competitividad del MVP: son plataformas destino de la operación Outbound y competidores del futuro Inbound. Citables solo como referencia de *principios* de UX (igual que Linear o Stripe), nunca como comparativa funcional.
2. **CAE Inbound es MVP futuro y su ausencia es deliberada.** No penaliza Escalabilidad ni Competitividad de ningún módulo; se registra una sola vez en "Fuera de alcance del MVP actual" del informe consolidado (`ROADMAP-UX.md`), no en cada ficha. Lo que **sí** se señala en cada ficha: toda decisión de diseño actual que bloquearía añadir Inbound después (modelo de permisos, entidad Documento, flujos de validación, notificaciones) — eso es deuda arquitectónica real hoy.
3. **Huecos vs. decisiones**: antes de puntuar una ausencia, consultar `docs/business/DECISION_LOG.md` y los ADR. Decisión registrada → se audita la calidad de la decisión. Sin registrar → hallazgo: "decisión implícita sin registrar". (Estado a fecha de este inventario: `DECISION_LOG.md` tiene una única entrada, 2026-07-25, sobre colisiones de nombre — ninguna ausencia funcional de las de § 4 está registrada como decisión ahí.)
4. **Las plataformas destino son objeto de la operación, no solo contexto.** Dokify, Nalanda, eCoordina y CTAIMA/Twind, al ser plataformas enfocadas en Inbound, son donde el Gestor CAE ejecuta a diario la operación Outbound. Cada sesión debe evaluar explícitamente qué hace el módulo para **facilitar la gestión sobre esas plataformas**: credenciales de acceso a mano en el punto de uso (`CredencialAccesoPortal`), seguimiento de qué queda pendiente en cada portal, requisitos propios de cada titular/plataforma, y fricción de cambiar de contexto Hydra ↔ portal. Un módulo que ignora que el trabajo termina en un portal ajeno no está midiendo el job real del Gestor CAE.

## 1. Módulos y pantallas detectadas

La navegación agrupa en Dashboard(s) + Negocio / Operación / Control / Administración ([NavMenu.razor](../../src/CaeManager.Web/Components/Layout/NavMenu.razor)). El rol Cliente tiene un menú mínimo propio (Dashboard, Empresas, Subcontratas, Centros, Trabajadores, Documentos).

### Dashboards

| Pantalla | Ruta | Archivo |
|---|---|---|
| Dashboard | `/` | `src/CaeManager.Web/Features/Dashboard/Pages/Dashboard.razor` |
| Visión de cartera | `/vision-cartera` | `src/CaeManager.Web/Features/VisionCartera/Pages/VisionCartera.razor` |
| Dashboard Ejecutivo | `/dashboard-ejecutivo` | `src/CaeManager.Web/Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor` |

### Negocio

| Pantalla | Ruta | Archivo |
|---|---|---|
| Clientes (lista) | `/clientes` | `src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor` |
| Alta guiada de Cliente | `/clientes/alta-guiada` | `src/CaeManager.Web/Features/Clientes/Pages/AltaGuiada.razor` |
| Importar Clientes | `/clientes/importar` | `src/CaeManager.Web/Features/Clientes/Pages/ImportarClientes.razor` |
| Importación combinada | `/clientes/importar-combinado` | `src/CaeManager.Web/Features/Clientes/Pages/ImportarCombinado.razor` |
| Configuración IA por Cliente | `/clientes/{ClienteId}/lectura-ia` | `src/CaeManager.Web/Features/Clientes/Pages/ConfiguracionIaCliente.razor` |
| Empresas (lista) | `/empresas` | `src/CaeManager.Web/Features/Empresas/Pages/Empresas.razor` |
| Detección de Trabajadores | `/empresas/{EmpresaId}/deteccion-trabajadores` | `src/CaeManager.Web/Features/Empresas/Pages/DeteccionTrabajadores.razor` |
| Subcontratas (lista) | `/subcontratas` | `src/CaeManager.Web/Features/Subcontratas/Pages/Subcontratas.razor` |
| Centros (lista) | `/centros` | `src/CaeManager.Web/Features/Centros/Pages/Centros.razor` |

Componentes propios del módulo: `ClienteWorkspacePanel`, `FormularioRapidoCliente` (Clientes); `EmpresaWorkspacePanel`, `FormularioRapidoEmpresa` (Empresas); `SubcontrataWorkspacePanel`; `CentroWorkspacePanel` (en `Features/<Modulo>/Components/`).

### Operación

| Pantalla | Ruta | Archivo |
|---|---|---|
| Trabajadores (lista) | `/trabajadores` | `src/CaeManager.Web/Features/Trabajadores/Pages/Trabajadores.razor` |
| Vehículos (lista) | `/vehiculos` | `src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor` |
| Asignaciones | `/asignaciones` | `src/CaeManager.Web/Features/Asignaciones/Pages/Asignaciones.razor` |
| Documentos (lista) | `/documentos` | `src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor` |
| Subida masiva de Documentos | `/documentos/subida-masiva` | `src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor` |
| Importar Documentos | `/documentos/importar` | `src/CaeManager.Web/Features/Documentos/Pages/ImportarDocumentos.razor` |
| Revisión IA de Documentos | `/documentos/revision-ia` | `src/CaeManager.Web/Features/Documentos/Pages/RevisionIa.razor` |
| Visitas | `/visitas` | `src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor` |
| Gestiones | `/gestiones` | `src/CaeManager.Web/Features/Gestiones/Pages/Gestiones.razor` |
| Proyectos | `/proyectos` | `src/CaeManager.Web/Features/Proyectos/Pages/Proyectos.razor` |
| Evaluaciones | `/evaluaciones` | `src/CaeManager.Web/Features/Evaluaciones/Pages/Evaluaciones.razor` |
| Incidencias | `/incidencias` | `src/CaeManager.Web/Features/Incidencias/Pages/Incidencias.razor` |

Componentes propios: `TrabajadorWorkspacePanel`, `VehiculoWorkspacePanel`, `DocumentoWorkspacePanel`, `VisorDocumento`.

### Comunicaciones (feature flag `Comunicaciones:Activo`, apagado por defecto)

El enlace de menú se oculta porque no hay ingesta real detrás (comentario en `NavMenu.razor:13-16`).

| Pantalla | Ruta | Archivo |
|---|---|---|
| Bandeja de conversaciones | `/comunicaciones` | `src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor` |
| Buzón | `/comunicaciones/buzon` | `src/CaeManager.Web/Features/Comunicaciones/Pages/Buzon.razor` |
| Chat WhatsApp | `/comunicaciones/chat` | `src/CaeManager.Web/Features/Comunicaciones/Pages/Chat.razor` |
| Macros | `/comunicaciones/macros` | `src/CaeManager.Web/Features/Comunicaciones/Pages/Macros.razor` |

Componente propio: `FilaConversacion`.

### Control

| Pantalla | Ruta | Archivo |
|---|---|---|
| Bandeja de trabajo | `/bandeja` | `src/CaeManager.Web/Features/Bandeja/Pages/Bandeja.razor` |
| Alertas | `/alertas` | `src/CaeManager.Web/Features/Alertas/Pages/Alertas.razor` |
| Facturación | `/facturacion` | `src/CaeManager.Web/Features/Facturacion/Pages/Facturacion.razor` |
| Calendario | `/calendario` | `src/CaeManager.Web/Features/Calendario/Pages/Calendario.razor` |
| Reportes | `/reportes` | `src/CaeManager.Web/Features/Reportes/Pages/Reportes.razor` |

Componente propio: `PanelResolverItem` (Bandeja). Reportes tiene exportación a Excel y PDF (`Reportes.razor:15-16`, endpoints `/reportes/documentos.xlsx` y `.pdf`).

### Administración

| Pantalla | Ruta | Archivo |
|---|---|---|
| Usuarios | `/usuarios` | `src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor` |
| Roles | `/roles` | `src/CaeManager.Web/Features/GestionRoles/Pages/Roles.razor` |
| Delegaciones (ADR-004) | `/delegaciones` | `src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor` |
| Claves API | `/configuracion/claves-api` | `src/CaeManager.Web/Features/ApiKeys/Pages/ClavesApi.razor` |
| Conexiones de integración | `/integraciones` | `src/CaeManager.Web/Features/Integraciones/Pages/Conexiones.razor` |
| Retención de datos | `/retencion` | `src/CaeManager.Web/Features/Retencion/Pages/Retencion.razor` |
| Tipos de Documento | `/tipos-documento` | `src/CaeManager.Web/Features/TiposDocumento/Pages/TiposDocumento.razor` |
| Configuración | `/configuracion` | `src/CaeManager.Web/Features/Configuracion/Pages/Configuracion.razor` |
| Auditoría | `/auditoria` | `src/CaeManager.Web/Features/Auditoria/Pages/Auditoria.razor` |
| Auditoría IA | `/auditoria-ia` | `src/CaeManager.Web/Features/AuditoriaIa/Pages/AuditoriaIa.razor` |
| Importar desde Excel | `/importacion` | `src/CaeManager.Web/Features/Importacion/Pages/Importacion.razor` |

Componente propio: `TablaItemsImportacion` (Importación).

### Cuenta

| Pantalla | Ruta | Archivo |
|---|---|---|
| Iniciar sesión | `/cuenta/iniciar-sesion` | `src/CaeManager.Web/Components/Account/Pages/Login.razor` |
| Verificación 2FA | `/cuenta/verificar-2fa` | `src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor` |
| Configurar 2FA | `/cuenta/configurar-2fa` | `src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor` |
| Cambiar contraseña | `/cuenta/cambiar-contrasena` | `src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor` |
| Pendiente de rol | `/cuenta/pendiente-de-rol` | `src/CaeManager.Web/Components/Account/Pages/PendienteDeRol.razor` |

Más `/Error` y `/not-found` (`Components/Pages/`).

### Transversales (no son páginas)

| Capacidad | Archivos |
|---|---|
| Context Workspace (panel contextual de entidad) | `src/CaeManager.Web/Components/Workspace/` (`ContextWorkspace`, `PestanaDocumentacion`, `PestanaHistorial`, `CampoInfo`, `FilaEntidadRelacionada`) + los `*WorkspacePanel.razor` por entidad |
| Búsqueda global | `src/CaeManager.Web/Features/BusquedaGlobal/` (`BotonBuscadorGlobal`, `BuscadorGlobal`) |
| Asistente IA | `src/CaeManager.Web/Features/AsistenteIa/` (`BotonAsistenteIa`, `AsistenteIa`) |
| Notificaciones in-app | `src/CaeManager.Web/Features/Notificaciones/NotificacionesPopup.razor` |
| Atajos de teclado globales | `src/CaeManager.Web/Features/AtajosGlobales/AtajosGlobales.razor` |
| Layout y navegación | `src/CaeManager.Web/Components/Layout/` (`MainLayout`, `NavMenu`, `NavegacionMovil`, `SelectorClienteActivo` — Delegated Workspace —, `SelectorTema`, `TrazaSoporte`, `ReconnectModal`, `AuthLayout`) |

## 2. Flujos de negocio identificables

Derivados de los Commands de `src/CaeManager.Application` (cada uno en `<Área>/Commands/<Flujo>/`):

**CRUD con soft delete y restauración** — Cliente, Empresa, Subcontrata, Centro, Trabajador, Vehículo, Documento: crear/editar/eliminar/restaurar, con borrado masivo (`Eliminar<Entidad>s`) en Clientes, Empresas, Subcontratas, Centros, Trabajadores, Vehículos, Documentos, Evaluaciones, Incidencias, Visitas.

**Ciclo documental** — `CrearDocumento` (subida), `RenovarDocumento`, subida masiva (`/documentos/subida-masiva`), importación (`/documentos/importar`), detección IA (`AplicarDeteccionIaDocumento`) y resolución de revisión IA (`ResolverRevisionIaDocumento`). El estado del Documento es **calculado** (regla central en `DATABASE.md`), no un workflow de aprobación manual.

**Requisitos documentales** — crear/editar/eliminar/marcar cumplido (`RequisitosDocumentales/Commands/`). Sin página propia: se opera desde los paneles de entidad.

**Asignaciones** — crear individual (`CrearAsignacion`) y masiva (`CrearAsignaciones`), dar de baja individual y masiva.

**Alta guiada de Cliente** — wizard en `/clientes/alta-guiada`; formularios rápidos de Cliente y Empresa como componentes.

**Detección de Trabajadores por IA** — flujo por Empresa (`/empresas/{id}/deteccion-trabajadores`, `ResolverDeteccionNuevo`, `ResolverDeteccionAusente`, `AsignarAliasTrabajador`).

**Operación diaria** — Gestiones (crear para trabajador, completar), Visitas (crear/editar/marcar notificado al cliente), Incidencias (crear/editar/resolver), Evaluaciones, Proyectos (crear/cerrar, asignar técnico), reasignación de ejecutivo de Cliente.

**Importación Excel** — `EjecutarImportacion`, `EjecutarImportacionCombinada`, más las variantes por página (Clientes, Documentos, combinada).

**Comunicaciones** (flag apagado) — conectar buzón Microsoft 365 / línea WhatsApp, responder conversación, asignar cliente/ejecutivo, cambiar estado, macros, sugerencias IA de gestión/visita desde correo.

**Control** — Bandeja de trabajo con resolución de items (`PanelResolverItem`), Alertas, Calendario, Reportes con exportación xlsx/pdf, Facturación (tarifas por cliente).

**Plataforma/Administración** — usuarios y roles, claves API (generar/revocar), delegaciones ADR-004 (crear delegación y cliente delegante, asignar/revocar operadores, revocar/reactivar), acceso de soporte (abrir/cerrar), retención de datos (detectar→avisar→autorizar→ejecutar), parámetros de sistema, filtros guardados, tipos de documento con configuración IA global y por cliente, auditoría y auditoría IA.

**Credenciales de portales CAE externos** — guardado de credenciales de acceso por Empresa y Subcontrata (`GuardarCredencialAccesoEmpresa/Subcontrata`, `CredencialAccesoPortal` en dominio). Son credenciales guardadas para portales ajenos, no una integración activa.

**Cuenta** — login con lockout, 2FA (configurar + verificar), cambio de contraseña, selector de Cliente activo (Delegated Workspace, `SelectorClienteActivo` + endpoint `/cuenta/cliente-activo`).

## 3. Sistema de diseño y componentes compartidos

Documento normativo: `DESIGN_SYSTEM.md` ("Design System 3.2 — identidad ProjectHydra": filosofía, identidad visual, catálogo de componentes, accesibilidad WCAG AA, responsive). Tokens CSS en `src/CaeManager.Web/wwwroot/css/tokens.css`; hojas por área: `base.css`, `list-page.css`, `workspace.css`, `dashboard.css`, `importacion.css`.

Catálogo real en `src/CaeManager.Web/Components/DesignSystem/` (30 componentes):

- **Acciones**: `Boton`, `BotonCopiar`, `BarraAccionesLote` (acciones masivas), `DialogoConfirmacion`
- **Formularios**: `CampoTexto`, `CampoTextarea`, `CampoSelect`, `CampoBuscarSelect`, `SelectorMultiple`, `SelectorEntidad`, `ZonaSoltarArchivo`
- **Contenedores**: `Modal`, `Drawer`, `Tarjeta`, `TarjetaMetrica`, `Pestanas`, `SeccionColapsable`
- **Estados y feedback**: `EstadoCargando`, `EstadoVacio`, `AnfitrionToasts`, `ProgresoConMensajes`, `IndicadorPasos`, `Badge`, `FiltroEstado`
- **Navegación y soporte**: `Breadcrumb`, `PaginadorSimple`, `Icono`, `AtajosListaTeclado`

Convenciones de producto en `UX_PATTERNS.md` y de código en `CODING_STANDARDS.md`.

## 4. Huecos detectados, clasificados según el marco de alcance (§ 0)

Todos `[INFERIDO]` — afirmo que *no los encuentro* en el código, no que no existan decisiones deliberadas detrás. Cada uno lleva su estado frente a `DECISION_LOG.md`/ADRs (regla 3 del § 0).

### 4.1 Huecos del MVP Outbound (a auditar en su sesión; vara: "¿más rápido y fiable que Excel + operar los portales?")

1. **Exportación fuera de Reportes** — corregido en Fase 1: `/clientes` sí exporta (`Clientes.razor:23`, verificado en ejecución), pero es la única lista; Empresas, Subcontratas, Centros, Trabajadores, Vehículos, Asignaciones, Incidencias, Evaluaciones y Auditoría no exportan. Sin decisión registrada. → Sesiones 03/05/06/08/11/14.
2. **Reclamación documental saliente a Empresas/Subcontratas** — Alertas y Notificaciones son internas (in-app); el módulo de Comunicaciones (entrante M365/WhatsApp) está apagado por falta de ingesta real. No encuentro el aviso a la Empresa cuando su documentación caduca o falta: hoy esa reclamación —parte del trabajo diario Outbound— vive fuera de Hydra (correo manual). Sin decisión registrada sobre el saliente automático. → Sesiones 10/12.
3. **Registro del estado de la documentación en la plataforma destino** — no encuentro entidad ni Command que registre si un Documento está subido/aceptado/rechazado en el portal de la titular (Dokify, Twind...). Existen `CredencialAccesoPortal` (credenciales guardadas por Empresa/Subcontrata) y Gestiones (`CrearGestionesParaTrabajador`, `CompletarGestion`) que podrían cubrir parcialmente ese seguimiento — confirmar en sesión. Es el corazón del job Outbound: saber qué queda pendiente en cada portal. → Sesiones 03/07/08.
4. **Subida automatizada a plataformas destino** — la operación de subir a los portales es manual hoy. Decisión **registrada**: `docs/business/PRODUCT_STRATEGY.md` la fasea explícitamente (Fase 2 — "Orquestador": conectores/agentes de subida automática hacia plataformas Inbound; Twind/CTAIMA primero) y `ARQUITECTURA-INTEGRACIONES.md` la diseña como backlog. Se audita la calidad de la decisión (secuencia de fases), no la ausencia. → Sesión 15.
5. **Validación documental manual (aprobar/rechazar con motivo)** — no encuentro ningún Command de aprobación/rechazo; el estado del Documento se calcula por fechas (`DATABASE.md`) y la única revisión humana es la de la lectura IA (`ResolverRevisionIaDocumento`). `DATABASE.md` documenta el mecanismo, pero ni `DECISION_LOG.md` ni los ADR registran la decisión de modelo (estado calculado en vez de workflow de validación) → hallazgo candidato: "decisión implícita sin registrar". Nota Inbound: un workflow de validación es además pieza central del futuro Inbound — evaluar en la sesión de Documentos si el modelo actual lo bloquea (regla 2 del § 0). → Sesión 07.

### 4.2 Fuera de alcance del MVP actual (mecánica Inbound — ausencia deliberada, no puntúa)

Se registran aquí una sola vez y pasarán al bloque "Fuera de alcance del MVP actual" del informe consolidado; no aparecen en las fichas de módulo:

- **Portal de autoservicio para Empresas/Subcontratas** (que la contrata suba su propia documentación y consulte su estado). Es la mecánica central del modelo Inbound (Dokify/Nalanda/eCoordina), no un defecto del MVP Outbound. Existe el rol Cliente con menú mínimo (`NavMenu.razor:144-163`), pero la carga documental es siempre interna del Gestor CAE — coherente con Outbound.
- **Apto/no apto operativo por centro (control de acceso en campo)** — "lista de aptos hoy en el centro X" consumible en tornos/QR/listado. Es operación de la empresa titular sobre sus centros: mecánica Inbound.

### 4.3 Vigilancia de deuda pro-Inbound (checklist transversal para todas las sesiones)

La ausencia de Inbound no puntúa, pero cada sesión debe señalar decisiones de hoy que lo bloquearían mañana (regla 2 del § 0). Puntos de vigilancia identificados en el inventario:

- **Modelo de permisos/roles** (`Roles.cs` de Infrastructure, menú por rol en `NavMenu.razor`): ¿admite un futuro rol de contratista externo con visibilidad solo de lo suyo, o asume que todo usuario es interno del tenant? → Sesiones 14/16.
- **Entidad Documento y su estado calculado** (`DATABASE.md`): ¿admite añadir un estado de validación (aprobado/rechazado con motivo) sin romper la regla central? → Sesión 07.
- **Notificaciones/Comunicaciones**: hoy todo el feedback es interno; Inbound exige comunicación bidireccional con terceros. → Sesiones 10/12.
- **Requisitos documentales** (`RequisitosDocumentales/Commands/`): ¿el modelo de requisito sirve para expresar "lo que la titular exige" además de "lo que el gestor controla"? → Sesión 07/08.

## 5. Índice de sesiones de auditoría

| Nº | Módulo (sesión) | Estado |
|---|---|---|
| 01 | Dashboard + Visión de cartera + Dashboard Ejecutivo | ✅ Auditada — [`01-dashboards.md`](01-dashboards.md) |
| 02 | Clientes (lista, alta guiada, importaciones, IA por cliente) | ✅ Auditada — [`02-clientes.md`](02-clientes.md) (importaciones → sesión 13) |
| 03 | Empresas + Subcontratas (incl. detección de trabajadores, credenciales) | ✅ Auditada — [`03-empresas-subcontratas.md`](03-empresas-subcontratas.md) |
| 04 | Centros | ✅ Auditada — [`04-centros.md`](04-centros.md) — **bug confirmado: la lista no carga (regresión en main)** |
| 05 | Trabajadores + Vehículos | ✅ Auditada — [`05-trabajadores-vehiculos.md`](05-trabajadores-vehiculos.md) |
| 06 | Asignaciones | ✅ Auditada — [`06-asignaciones.md`](06-asignaciones.md) |
| 07 | Documentos (lista, subida masiva, importar, Revisión IA, visor) | ✅ Auditada — [`07-documentos.md`](07-documentos.md) (importar → sesión 13) |
| 08 | Visitas + Gestiones + Incidencias + Evaluaciones | ✅ Auditada — [`08-visitas-gestiones-incidencias-evaluaciones.md`](08-visitas-gestiones-incidencias-evaluaciones.md) |
| 09 | Proyectos | ✅ Auditada — [`09-proyectos.md`](09-proyectos.md) |
| 10 | Bandeja + Alertas + Calendario | ✅ Auditada — [`10-bandeja-alertas-calendario.md`](10-bandeja-alertas-calendario.md) |
| 11 | Reportes + Facturación | ✅ Auditada — [`11-reportes-facturacion.md`](11-reportes-facturacion.md) |
| 12 | Comunicaciones (flag apagado) | ✅ Auditada — [`12-comunicaciones.md`](12-comunicaciones.md) |
| 13 | Importación Excel (todas las variantes) | ✅ Auditada — [`13-importacion.md`](13-importacion.md) |
| 14 | Administración (Usuarios, Roles, Configuración, Tipos de Documento, Auditorías) | ✅ Auditada — [`14-administracion.md`](14-administracion.md) |
| 15 | Plataforma (Delegaciones, Retención, Integraciones, Claves API) | ✅ Auditada — [`15-plataforma.md`](15-plataforma.md) |
| 16 | Transversales (navegación, Context Workspace, búsqueda global, asistente IA, atajos, cuenta) | ✅ Auditada — [`16-transversales.md`](16-transversales.md) |
| — | Consolidación final (`ROADMAP-UX.md`) | ✅ Completada — [`ROADMAP-UX.md`](ROADMAP-UX.md) |
