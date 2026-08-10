# ADR-005 — Subcontratas: nivel de servicio y supervisión externa

**Estado**: Aceptado (2026-08-10) — aprobado explícitamente y en su totalidad por el propietario del producto el mismo día. Registrado en `docs/business/DECISION_LOG.md` (entrada 2026-08-10); término **Nivel de servicio de Subcontrata** pasado a `Approved` en `docs/business/UBIQUITOUS_LANGUAGE.md`.
**Decisores**: propietario del producto (decisiones de negocio 1-3 del § 2, propuestas por él el 2026-08-10) + esta sesión (traducción a dominio).
**Relacionado**: `DOMAIN.md` (modelo actual de `Subcontrata`), `ADR-004` (dónde vive cada dato en el modo Consultora), `ARQUITECTURA-INTEGRACIONES.md` (automatización futura de la verificación).

## 1. Contexto

Dos escenarios de negocio reales motivan esto (análisis del propietario del producto, 2026-08-10):

- **Caso 1 — Cliente Directo**: una contratista (ej. ficticio "Refrielectric") usa Hydra para su propia CAE. Sus clientes finales (titulares de Centros) le exigen acreditar documentación en sus portales (Dokify, Twind...). Además contrata subcontratas (ej. "Diagnosticos S.A.") cuyo cumplimiento en esos mismos portales le afecta contractualmente, **sin que ella gestione sus documentos**.
- **Caso 2 — Consultora (ADR-004)**: una consultora BPO opera el Delegated Workspace de varios Clientes Delegantes. Dentro del workspace de cada cliente aparece la misma necesidad: subcontratas del cliente cuya documentación la consultora **no** carga, pero cuyo estado debe auditar y reportar.

**Lo que ya existe** (verificado en código, `DOMAIN.md`): `Subcontrata` es agregado raíz con tenant — N:N con `Cliente` y con `Empresa`, Trabajadores/Vehículos propios (`SubcontrataId` excluyente con `EmpresaId`) cuyos Documentos se gestionan igual que los de una Empresa, y `CredencialAccesoSubcontrata` para sus portales. Es decir: la modalidad "Gestionada" (subimos y validamos sus documentos) **ya es el comportamiento actual** del modelo. La cadena Cliente → Empresa → Subcontrata también.

**Lo que no existe**: la distinción de *nivel de servicio* (¿gestionamos sus documentos o solo vigilamos su cumplimiento externo?) y cualquier mecanismo de supervisión sin gestión — hoy una Subcontrata a la que no se le cargan documentos simplemente aparece vacía/roja, indistinguible de una gestionada con todo caducado.

## 2. Decisión propuesta

### 2.1 Nivel de servicio de la Subcontrata

Nuevo valor de dominio en el agregado `Subcontrata`:

```
NivelServicioSubcontrata { Gestionada, Supervisada }
```

- **Gestionada** (valor por defecto y semántica actual): sus Trabajadores, Vehículos y Documentos se gestionan dentro de Hydra, sin cambio alguno de comportamiento. Toda Subcontrata existente queda `Gestionada` en la migración.
- **Supervisada**: no se cargan ni validan sus documentos; su cumplimiento se registra mediante Verificaciones Externas (§ 2.3). La UI de gestión documental para ella se muestra en modo lectura/aviso, no se oculta (decisión de UX a detallar en su blueprint, no aquí).
- El cambio de nivel es una operación de negocio del agregado (`CambiarNivelServicio`), **no** un cambio de entidad ni una migración: decisión explícita del propietario del producto — pasar de Supervisada a Gestionada es una ampliación de contrato, el historial (verificaciones incluidas) se conserva. En la UI, el cambio **no lleva diálogo de confirmación**: `04_UX_PATTERNS.md` § 7.2 reserva la confirmación modal a lo destructivo o irreversible en la práctica, y este cambio es reversible en ambos sentidos — mismo tratamiento que "Reactivar" en `Delegaciones.razor` (acción directa con estado de carga, resultado confirmado por toast).
- El nivel es **por Subcontrata dentro del tenant**, no por relación Subcontrata–Cliente. YAGNI: si un caso real exige "gestionada para el Cliente A, supervisada para el B", se revisará entonces — no se modela ahora.

### 2.2 Dónde vive (multi-tenancy)

Sin cambios respecto a lo ya construido, y confirmando la propuesta del propietario del producto: la Subcontrata **vive en el tenant del Cliente Delegante** (en el Caso 2) o del Cliente Directo (Caso 1) — ya es así vía `EntidadBase.TenantId`. Si el día de mañana la subcontrata contrata Hydra (directamente o vía la consultora), se crea **su propio tenant** y se conecta con `DelegacionTenant` (ADR-004); su historial de supervisión **no se migra** — pertenece al contexto de quien la supervisó, no a ella.

### 2.3 Supervisión: Verificación Externa (manual primero, integrable después)

Aporta valor desde el MVP sin depender de conectores (decisión del propietario del producto). Un único agregado nuevo:

**`VerificacionExternaSubcontrata`** (con `TenantId`, soft delete, agregado raíz con repositorio propio):

| Campo | Notas |
|---|---|
| `SubcontrataId` | Obligatorio. Con el filtro de tenant activo, un Id ajeno = no encontrado (regla general de Commands). |
| `CentroId` | Obligatorio: la exigencia documental es del portal del titular, y el portal es un atributo del Centro (`CanalGestionDocumental`). |
| `TipoDocumentoId` | Reutiliza el catálogo por tenant. **La lista de "qué verificar" es `TipoDocumentoCentro`** (los tipos exigidos por el Centro, ya existentes) — no se crea un segundo catálogo de requisitos. |
| `FechaVerificacion` | Cuándo se comprobó en el portal externo. |
| `Resultado` | `Valido` / `NoValido` / `NoEncontrado`. |
| `ValidoHasta?` | Fecha declarada por el portal, si consta. |
| `EvidenciaArchivoRuta?` | Captura/justificante opcional, en el almacenamiento particionado por tenant. |
| `UsuarioVerificadorId` | Quién verificó (trazabilidad; en Delegated Workspace, el Operador Delegado). |
| `Observaciones?` | Texto libre corto. |

Reglas que siguen los patrones centrales del producto:

- **El estado de supervisión se calcula, nunca se almacena** — mismo principio que el estado de Documento y que `NivelUrgenciaVisita`: para cada `(Subcontrata, Centro, TipoDocumento)` exigido, la última verificación no eliminada + `ValidoHasta` contra los umbrales de `ParametroSistema` dan el semáforo (sin verificación = pendiente; `NoValido`/vencida = rojo; dentro del umbral ámbar = próximo). Cálculo puro en Domain (`CalculadoraEstadoSupervision`), reutilizando los umbrales existentes.
- **Los avisos de caducidad** reutilizan el motor de Alertas existente sobre `ValidoHasta` — misma mecánica que los vencimientos de Documento; el detalle de generación se decide en la fase de implementación, no aquí.
- **Nada automático crea verificaciones** en esta versión: las registra el Gestor (patrón sugerencia-nunca-automática del resto del producto no aplica todavía porque no hay fuente automática). Cuando exista el conector de la Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md` — CTAIMA/Twind prioritario por evidencia de uso), el conector **alimentará este mismo agregado** (con un `UsuarioVerificadorId` de sistema o campo de origen a decidir entonces); la entidad no se rediseña.
- El "reporte de estado a Refrielectric" del flujo de negocio **no es una entidad**: es una vista/expediente sobre estas verificaciones, y su salida natural es el módulo de Comunicaciones ya existente (responder con el estado desde la conversación del Cliente). No se modela nada nuevo para ello.

### 2.4 Lo que este ADR deliberadamente no decide

- **Vínculo directo Subcontrata–Centro**: la propuesta original planteaba un N:M explícito. No se añade: el despliegue operativo ya se deriva de las Asignaciones de sus Trabajadores, y para la supervisión basta el `CentroId` de cada verificación. Si una Supervisada sin trabajadores dados de alta necesita aparecer "operando en" un Centro sin verificación previa, se revisará entonces.
- **Bandeja unificada multi-cliente del gestor** (idea del artefacto de diseño, 2026-08-10): en el modo Consultora, "todos los clientes del gestor" son tenants distintos (ADR-004), y `ConversacionCorreo` vive por tenant — un timeline unificado es una superficie **transversal** tipo Visión de cartera (agregación vía clientes autorizados, jamás `IgnoreQueryFilters`), o bien exige decidir que los buzones de la consultora viven en su propio tenant. Es una decisión de arquitectura pendiente con entidad propia; no se resuelve de pasada aquí.
- **Métricas BPO**: fasado decidido por el propietario del producto (Fase 1 operativa desde actividad del sistema; Fase 2 financiera con fee/coste-hora por cuenta en el tenant de la consultora) — registrado en `UBIQUITOUS_LANGUAGE.md` (Draft). Su diseño (KPIs exactos, agregación cross-tenant, modelo de estimación de horas) es trabajo aparte.
- **Matching de subcontratas entre tenants por CIF/NIF y `DelegacionSubcontrata`** (dirección aportada por el propietario del producto, 2026-08-10): la misma subcontrata puede existir en varios tenants (una fila por tenant, aislamiento intacto — es el diseño de este ADR), y el CIF es el ancla natural para reconocerla transversalmente (sugerencias de reutilización en la vista de cartera, indicador agregado para dirección, y el futuro bucle "la subcontrata se hace Cliente Directo y comparte su expediente en tiempo real con quien la contrata"). Nada de este ADR lo impide; nada de este ADR lo construye (YAGNI — es diseño de la capa transversal/Integraciones, con entidad de compartición propia tipo `DelegacionSubcontrata` cuando llegue). **Decisión tomada el mismo día**: el CIF/NIF pasa a ser obligatorio en `Empresa` y `Subcontrata` (supersede la opcionalidad de Issue #5) — ver `docs/business/DECISION_LOG.md` 2026-08-10. Se implementa como cambio propio, separado de esta fase (toca `Empresa`, importación y datos históricos sin CIF).

## 3. Consecuencias

- La migración añade `NivelServicio` a `Subcontratas` (default `Gestionada`) y la tabla de `VerificacionExternaSubcontrata` con `TenantId`, índices por `(TenantId, SubcontrataId, CentroId, TipoDocumentoId)`.
- Commands de edición llevan `Version` + `ConcurrenciaOptimista` (regla global del repo); los que reciben Ids ajenos los cargan bajo filtro de tenant.
- Requerimiento global nº 1: la siembra de datos de prueba debe cubrir Subcontratas en ambos niveles, verificaciones en todos los `Resultado` posibles, con y sin `ValidoHasta`, con y sin evidencia, y los usuarios de rol necesarios — parte del mismo cambio que la implementación, no posterior.
- La verificación end-to-end en navegador al cierre incluye: cambiar el nivel de servicio en ambos sentidos, registrar una verificación con evidencia, y comprobar el semáforo calculado y sus alertas.
