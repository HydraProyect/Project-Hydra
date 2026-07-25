# ADR-004 — Delegación reversible de gestión CAE a consultoras externas

**Estado**: Propuesta de arquitectura, modelo revisado y ampliado con el usuario el 2026-07-25 (segunda vuelta, tras una revisión que corrigió la prioridad de la primera versión). No implementado — este documento es la fase de diseño pedida antes de tocar código (mismo criterio que `PLAN-CONTEXT-WORKSPACE.md`/`ADR-003`). Motivado por el caso real que destraba el primer cliente de Hydra: Geseme necesita operar la CAE de varios clientes (KHS, Tomra, Blasau) sin que cada uno sea un tenant que Geseme "posee".

**Corrección de enfoque respecto a la v1 de este documento**: no se trata de "un usuario puede entrar en varios tenants" (eso es un detalle de sesión). Es una **jerarquía organizacional transversal** — Hydra vende a dos tipos de cliente a la vez (el tenant que opera su propia CAE, y la consultora que opera la CAE de varios tenants por delegación), y ambos necesitan sus propias vistas agregadas sin que se mezclen con el aislamiento entre tenants. Es, probablemente, uno de los diferenciadores de producto más importantes de Hydra frente a otras plataformas CAE — se diseña como capacidad de negocio, no como ajuste de permisos.

---

## 1. El problema

`docs/MULTITENANCY.md` § 8 (Tenant Resolution Strategy) resuelve **exactamente un tenant por sesión**, desde un claim fijado al login, fallo cerrado, sin ningún modo "sin filtro". Es correcto para el Escenario 1 de su § 2 (una consultora que gestiona varias `Empresa` **dentro** de su propio tenant), pero no cubre el caso real pedido: una consultora cuyos usuarios necesitan operar sobre **varios tenants ajenos**, cada uno propiedad de un cliente distinto, sin que los datos se mezclen ni se dupliquen — y cuyos mandos intermedios (Coordinador, Director CAE Externo) necesitan además **ver el rendimiento agregado de su equipo entre esos tenants**, algo que ningún tenant individual puede responder por sí solo.

## 2. Los cuatro escenarios de negocio (pedidos por el usuario, 2026-07-25)

1. **Gestión interna** — una pyme (p. ej. Aislamientos Juan) usa Hydra directamente. Su tenant, sus datos, sus usuarios. Sin cambios sobre el modelo actual.
2. **Crecimiento y externalización** — la pyme crece y delega la operación completa a una consultora (Geseme). **No se migra nada, no se crea un tenant nuevo, no se duplica la base de datos** — solo se conceden permisos delegados para que usuarios de Geseme operen sobre el tenant ya existente del cliente.
3. **Modelo híbrido** — el Coordinador CAE interno del cliente y los Gestores CAE de Geseme trabajan **simultáneamente** sobre el mismo tenant, cada uno con su rol/alcance, con trazabilidad de quién hizo qué y desde dónde.
4. **Internalización** — el cliente monta su propio departamento CAE y retira la delegación. Los usuarios de Geseme pierden acceso, pero **ningún dato desaparece** — todo el histórico (documentos, auditoría, configuración) sigue siendo del cliente, porque siempre vivió en su tenant.

**Principio fundamental (cita literal del usuario, es la frase que fija el diseño)**: *"La autorización no debe modelarse como una relación de propiedad, sino como una delegación de acceso."* La separación entre **quién es dueño del dato** (siempre el tenant cliente) y **quién puede operar sobre él en cada momento** (nativo del tenant, delegado, o ambos) es un principio arquitectónico, no un detalle de permisos.

## 3. Modelo conceptual: tres niveles de información que nunca se mezclan

```
Hydra (plataforma)
│
├── Tenant KHS ───────────────┐
├── Tenant Tomra              │  aislados entre sí — sin cambios,
├── Tenant Blasau             │  cada uno es dueño de su información
│                             ┘
└── Workspace Geseme (Consultora = Tenant sin datos operativos propios)
     ├── Director CAE Externo
     │    ├── Coordinador Norte
     │    │    ├── Gestor A ──► delegado en KHS
     │    │    └── Gestor B ──► delegado en KHS, Tomra
     │    └── Coordinador Sur
     │         └── Gestor C ──► delegado en Blasau
     └── Clientes autorizados del Workspace: KHS · Tomra · Blasau
```

Tres niveles distintos de información, que **no deben mezclarse**:

| Nivel | Qué contesta | Alcance de datos | Mecanismo |
|---|---|---|---|
| **Tenant** | "¿Cuántos documentos vencidos tiene KHS?" | Un único tenant, aislado | Filtro global + `IAlcanceDatosService` — **sin cambios**, § 6 |
| **Workspace / Consultora** | "¿Cuántos documentos pendientes gestiona Christopher en total, sumando KHS+Tomra+Blasau? ¿Cuál es la cartera del Coordinador Norte?" | Varios tenants, pero solo los delegados a ese Workspace, y solo agregado — nunca operación | Capa de Reporting transversal — **nueva**, § 7 |
| **Hydra** | "¿Cuántos tenants activos hay? ¿Qué consultoras operan sobre cuántos clientes?" | Toda la plataforma | Fuera de alcance de este documento — administración de plataforma, no de negocio CAE |

El Nivel Tenant nunca sabe que existe el Nivel Workspace (un usuario nativo de KHS no ve nada de Geseme). El Nivel Workspace nunca escribe directamente en varios tenants a la vez (§ 7). El Nivel Hydra no se diseña aquí — se menciona solo para dejar constancia de que existe y de que este documento no lo cubre.

## 4. Por qué esto no toca el mecanismo de aislamiento ya auditado

Consecuencia directa del principio de § 2: el filtro global de EF Core (`TenantId == tenantActual.TenantId`) y el interceptor de sellado en escritura (`TenantSelladoInterceptor`) **no cambian una sola línea**. Siguen siendo exactamente un `TenantId` por query, sellado en cada fila nueva, fallo cerrado. Lo único que cambia es **de dónde sale ese `TenantId`** para una sesión de un usuario delegado — el mecanismo que ya tiene 25 tests de aislamiento por agregado no se reabre.

Esto es deliberado: es la forma más segura de añadir esta capacidad sin arriesgar la garantía ya probada. La delegación vive **por encima** de la Capa 1 (Tenant) de `docs/MULTITENANCY.md` § 6 — es una Capa 0 nueva ("¿puede este usuario resolver este tenant en absoluto?"), no una modificación de las capas 1-4 existentes. El Nivel Workspace de § 3 (reporting transversal) tampoco la toca — ver § 7, es la pieza que explica cómo se consigue una vista entre tenants sin que ninguna query individual cruce la frontera.

## 5. Modelo de dominio

### 5.1 Workspace = Consultora = un `Tenant` sin datos operativos propios

Se evaluaron dos caminos:

| Opción | Descripción | Veredicto |
|---|---|---|
| **(a) El Workspace de la consultora es un `Tenant` más** | Geseme es tenant #N, igual que cualquier otro. Sus usuarios (`christopher@geseme.com`) tienen `ApplicationUser.TenantId = TenantGeseme`. Ese tenant **nunca tiene filas** de `Empresa`/`Centro`/`Documento`/etc. — solo existe para dar de alta a sus propios usuarios y su jerarquía interna (§ 5.2). | **Elegida.** Encaja literalmente con `docs/MULTITENANCY.md` § 1: "el Tenant es la organización que compra y utiliza Hydra" — Geseme compra Hydra igual que KHS, solo que no para almacenar su propia CAE. Reutiliza el 100% de la infraestructura de `Tenant`/`ApplicationUser` ya construida y auditada: cero entidades nuevas para la identidad ni para la jerarquía interna. |
| (b) Consultora es un concepto nuevo, fuera del modelo de Tenant | Tabla `Consultora` global, con sus propios usuarios fuera del particionado por tenant. | Descartada: `ApplicationUser` está diseñado 1:1 con `Tenant` (unicidad `(TenantId, NormalizedUserName)`, filtro global). Sacar usuarios de ese modelo es más invasivo que reutilizarlo, y no aporta nada que (a) no dé ya. |

Con (a), la unicidad de login por email global (`docs/MULTITENANCY.md` § 8, limitación v1 ya aceptada) sigue funcionando sin cambios: Christopher tiene **una sola cuenta**, en el Workspace/tenant Geseme — nunca una cuenta duplicada por cada cliente al que se le delega.

### 5.2 Jerarquía interna de la consultora (Director CAE Externo → Coordinador → Gestor)

**No es una entidad nueva.** Es el mismo mecanismo que ya existe hoy *dentro* de un tenant para `CoordinadorCae`/`GestorCae` (`ApplicationUser.CoordinadorUsuarioId`, `Cliente.EjecutivoUsuarioId`, resuelto por `AlcanceDatosService.ObtenerClienteIdsParaCoordinadorAsync`), aplicado sin cambios **dentro del tenant Geseme**: Christopher, María y Pedro son `ApplicationUser` del tenant Geseme; el Director CAE Externo y los Coordinadores son roles/relaciones de ese mismo tenant. Esto es una decisión deliberada de reutilización: la jerarquía de mando de la consultora es información de *identidad y estructura organizativa de Geseme*, no información CAE — vive perfectamente dentro del tenant Geseme sin tocar ninguna `Empresa`/`Centro` ajenos.

Lo único nuevo es **qué hace esa jerarquía cuando mira hacia fuera** — ver § 7 (Reporting transversal): un Coordinador necesita agregar datos operativos de tenants ajenos (KHS, Tomra...) para sus Gestores subordinados, no solo su propia estructura de mando (que ya resuelve el mecanismo existente).

### 5.3 Entidades nuevas — delegación de acceso ("clientes autorizados")

- **`DelegacionTenant`** (catálogo global, mismo tratamiento que `Tenant` — exceptuado del filtro estándar de `TenantId`, ver `docs/MULTITENANCY.md` § 4 excepción 1): `TenantConsultoraId` (FK a `Tenant`), `TenantClienteId` (FK a `Tenant`), `Activa` (bool). Es la lista de "clientes autorizados" del Workspace (§ 3). **Desactivar, nunca borrar** — Escenario 4 (internalización) es `Activa = false`, no un `DELETE`; conserva el histórico de qué consultora operó sobre qué tenant y cuándo, ver § 5.5.
- **`AsignacionUsuarioDelegado`**: `DelegacionTenantId` (FK), `UsuarioId` (FK a `ApplicationUser`, usuario de la consultora), `Rol` (el rol con el que ese usuario opera en *ese* tenant cliente — un mismo Gestor de Geseme puede tener roles distintos en clientes distintos). Resuelve exactamente el ejemplo del usuario: *"Christopher → KHS y Tomra · María → Blasau · Pedro → KHS"*.

Ninguna de las dos entidades tiene FK hacia `Empresa`/`Centro`/`Trabajador`/`Documento` — son puramente de autorización, nunca de dominio CAE. Es la representación literal de "delegación de acceso, no de propiedad".

### 5.4 Qué NO cambia

`Empresa`, `Centro`, `Trabajador`, `Vehiculo`, `Documento`, `RequisitoDocumental`... — cero cambios de esquema. Todas sus filas siguen teniendo `TenantId = TenantCliente`, nunca `TenantId = TenantConsultora`. Es la prueba de que el modelo cumple "el cliente es siempre dueño de la información": técnicamente no hay forma de que una fila de KHS termine con `TenantId = Geseme`, el interceptor de sellado sigue sellando contra el **tenant activo** de la sesión (§ 6), que para un Gestor de Geseme operando sobre KHS es `TenantClienteId = KHS` — nunca el suyo propio.

### 5.5 Reversibilidad sin migración (Escenario 4)

Apagar una delegación (`DelegacionTenant.Activa = false`) no mueve ni borra una sola fila de `Empresa`/`Documento`/etc. — nunca las tocó, porque nunca fueron del tenant de la consultora. Es la consecuencia natural de § 5.4, no una funcionalidad aparte que haya que construir con cuidado extra.

## 6. Cadena de resolución de sesión — cuarto modo de Tenant Resolution Strategy

`docs/MULTITENANCY.md` § 8 documenta hoy tres modos: **claim de sesión** (usuarios interactivos), **ámbito explícito de jobs** (`AmbitoTenantExplicito`, procesos de fondo) y **webhooks de integraciones** (identificador de recurso + firma HMAC). Se añade un cuarto, explícito en capas — no se reutiliza el claim `tenant_id` tal cual, para no forzar el modelo de "un tenant fijo por sesión" sobre un caso que es "un tenant *elegible* por sesión, *activo* uno a la vez":

```
Identity (usuario autenticado)
   ↓
Workspace (tenant "de origen" del usuario — Geseme, o el suyo propio si no es de una consultora)
   ↓
Clientes autorizados del Workspace (DelegacionTenant + AsignacionUsuarioDelegado — vacío si el Workspace no es una consultora)
   ↓
Tenant activo de la sesión (uno solo, elegido — nunca varios a la vez)
```

- **Un usuario nunca opera sobre varios tenants simultáneamente.** Si el conjunto de "clientes autorizados" tiene un solo elemento (el caso de hoy, el usuario normal de un tenant sin delegación: su Workspace y su único cliente autorizado son el mismo tenant), no hay selector — comportamiento idéntico al actual, cero cambio de UX para quien no está delegado.
- Si tiene **más de uno**, la UI presenta "Mis clientes" (selector, no necesariamente una pantalla de login aparte — puede vivir en la cabecera, tipo "cambiar de organización"; **decisión de UI pendiente**, ver § 9) y el usuario elige uno. Ese pasa a ser el **tenant activo** — desde ahí, "el sistema funciona exactamente igual que para un usuario interno de ese tenant" (cita literal del usuario): mismas Capas 2-4 de `docs/MULTITENANCY.md` § 6, sin ninguna rama especial.
- **Cambiar de tenant activo a mitad de sesión** (Christopher pasa de operar KHS a operar Tomra sin cerrar sesión) exige que `ITenantActual` deje de depender solo del claim fijo de la cookie de login — se añade una fuente de resolución adicional, con la misma prioridad/patrón que ya se introdujo en la Fase 44 de `ROADMAP.md` para el fallback de `IHttpContextAccessor` en `TenantActual`/`CurrentUserService`: un valor de "tenant activo elegido" con ámbito de circuito/sesión, consultado **antes** que el claim, y **siempre revalidado** contra `DelegacionTenant`/`AsignacionUsuarioDelegado` en cada resolución — nunca se confía en un valor elegido una vez y cacheado sin volver a comprobar que la delegación sigue activa (si se desactiva a mitad de sesión, la siguiente resolución debe fallar cerrado, no seguir sirviendo datos del tenant retirado).
- Sigue siendo **fallo cerrado**: si el tenant activo solicitado no está en el conjunto de clientes autorizados resuelto en ese momento, `TenantId` resuelve a `null` — mismo criterio que hoy, nunca "sin filtro".
- **El Global Query Filter no cambia.** Sigue leyendo un único `TenantId` activo — la novedad entera está en esta cadena de resolución, no en cómo se aplica el filtro.

## 7. Capa de Reporting transversal (nueva — necesaria para el Nivel Workspace de § 3)

### 7.1 El problema que resuelve

Un Coordinador o un Director CAE Externo necesitan ver **agregados de productividad entre varios tenants** — algo que ningún tenant individual puede calcular por sí solo:

```
Christopher (Gestor)          Coordinador Norte              Director CAE Externo (Geseme)
KHS  — 180 pendientes         Gestor A — 276 pendientes      Coordinador Norte — 612 pendientes
Tomra —  75 pendientes        Gestor B — 336 pendientes      Coordinador Sur   — 298 pendientes
Blasau—  21 pendientes        ─────────────────              ─────────────────
─────────────                 Total cartera — 612             Total Geseme — 910
Total — 276 pendientes
```

### 7.2 Regla arquitectónica: nunca una query cruza la frontera de tenant

`docs/MULTITENANCY.md` § 1 es categórico: *"ninguna consulta ni operación puede cruzar esa frontera"*. Este documento no la relaja — la Capa de Reporting se diseña para **cumplirla literalmente** mientras entrega el resultado agregado:

1. Se resuelve el conjunto de `TenantClienteId` autorizados para el reporte solicitado (vía § 5.2 + § 5.3: para un Gestor, sus propias `AsignacionUsuarioDelegado`; para un Coordinador, la unión de las de todos sus Gestores subordinados; para el Director CAE Externo, la unión de todo el Workspace).
2. Por cada `TenantClienteId` del conjunto, se ejecuta **la misma Query de un solo tenant que ya existe** (p. ej. una variante de `ObtenerKpisDashboardQuery`), con `AmbitoTenantExplicito.Establecer(tenantClienteId)` fijando ese tenant como activo **solo para esa llamada** — exactamente el mismo mecanismo `AsyncLocal` que ya usan los jobs de fondo (Modo 2 de `docs/MULTITENANCY.md` § 8), reutilizado aquí en vez de inventado.
3. Los resultados (uno por tenant, cada uno obtenido con el filtro global normal, sin excepciones) se combinan **en memoria, en Application o Web** — nunca en SQL, nunca con un `UNION`/`JOIN` entre tenants.

Ninguna query individual "ve" más de un tenant. La agregación pasa **después** de que cada una ya devolvió su resultado filtrado y aislado — es una orquestación de N llamadas seguras, no una excepción al filtro.

### 7.3 Autorización de quién puede pedir qué agregado

Nueva capa, evaluada **antes** de ejecutar el fan-out de § 7.2 — no es la Capa 0 de § 8 (esa decide si un tenant se puede activar operativamente; esta decide si un conjunto de tenants se puede agregar para lectura):

- Un `GestorCae` del Workspace solo puede pedir el agregado de **sus propias** `AsignacionUsuarioDelegado`.
- Un `CoordinadorCae` del Workspace puede pedir el agregado de las suyas propias **más las de sus Gestores subordinados** (resuelto vía `CoordinadorUsuarioId`, § 5.2 — sin tabla nueva).
- Un `DireccionCae` del Workspace (Director CAE Externo) puede pedir el agregado de todo el Workspace.
- Es **de solo lectura** — la Capa de Reporting nunca escribe, nunca abre un tenant activo operable para el usuario que consulta el agregado (ver el Coordinador *no* pasa automáticamente a poder operar sobre los tenants de sus Gestores solo por poder ver el agregado — operar sigue exigiendo su propia `AsignacionUsuarioDelegado`, si la tiene).

### 7.4 Relación con el backlog de Dashboard ya pedido

Esta es la pieza de datos que le faltaba al "Backlog — Rework de Dashboard" (`ROADMAP.md`, pedido el mismo día): las comparativas "Coordinador vs Coordinador" y "Gestor vs Gestor" que se pidieron ahí **son** consultas de esta Capa de Reporting, con el Coordinador/Gestor como unidad de agregación en vez del tenant. Cuando se aborde ese rework, la Capa de Reporting de este documento es su cimiento de datos — no son dos trabajos independientes.

## 8. Autorización en capas — Capa 0 nueva + Reporting

Extiende la tabla de `docs/MULTITENANCY.md` § 6:

| Capa | Mecanismo | Decide | Estado |
|---|---|---|---|
| **0. Delegación** (nueva) | `DelegacionTenant` + `AsignacionUsuarioDelegado` | Si un usuario ajeno al tenant puede resolverlo como *tenant activo* (operar) | A implementar (§ 5-6) |
| **0-R. Reporting** (nueva) | Jerarquía del Workspace (§ 5.2) + `AsignacionUsuarioDelegado` de los subordinados | Sobre qué conjunto de tenants puede pedir un *agregado de solo lectura* (sin activarlos) | A implementar (§ 7) |
| 1. Tenant | Filtro global + interceptor | De qué organización es cada fila | Implementado (`ADR-003`) |
| 2. Rol | Policies ASP.NET Core | Qué puede hacer un usuario | Existe, sin cambios |
| 3. Cartera | `IAlcanceDatosService` | Qué subconjunto del tenant ve un rol restringido | Existe, sin cambios |
| 4. Escritura | `AutorizacionEscrituraBehavior` | Qué puede mutar | Existe, sin cambios |

Las Capas 0/0-R deciden **si** se entra o se agrega; las capas 2-4 deciden **qué se ve/hace dentro** de un tenant ya activo, exactamente igual para un usuario nativo que para uno delegado — es el Escenario 3 (híbrido) resuelto por construcción: el Coordinador nativo de KHS y el Gestor delegado de Geseme conviven en el mismo tenant, cada uno con su propio rol evaluado normalmente por las Capas 2-3, sin que ninguna de las dos sepa "quién es nativo y quién es delegado" — esa distinción solo importa en la Capa 0.

## 9. Auditoría dual

`RegistroAuditoria` gana un campo nuevo, nullable: `ActuoDesdeTenantId` (el Workspace/tenant de origen del usuario, cuando difiere del tenant activo sobre el que se escribió — es decir, cuando la operación fue delegada). El interceptor que ya escribe `RegistroAuditoria` en cada `SaveChanges` lo rellena comparando `ApplicationUser.TenantId` contra el tenant activo de la sesión — sin tocar ningún Command ni Query existente, mismo patrón que el resto de la auditoría (invisible para Application/Domain). Satisface literalmente el pedido: *"todas las acciones deben quedar auditadas indicando tanto el usuario que ejecutó la acción como la consultora desde la que actuó."* Las lecturas de la Capa de Reporting (§ 7) no escriben en un tenant y por tanto no generan `RegistroAuditoria` por sí mismas — si hiciera falta auditar "quién consultó qué agregado", es una traza distinta, más ligera (log de aplicación), no forzada a encajar en el modelo de auditoría por-tenant existente.

## 10. Compatibilidad con lo ya construido

- **Aislamiento por tenant** (`ADR-003`, 25 tests): sin cambios, ver § 4.
- **`IAlcanceDatosService`** (cartera por rol): sin cambios — opera siempre dentro del tenant activo ya resuelto, sea nativo o delegado.
- **Unicidad de login por email global** (`docs/MULTITENANCY.md` § 8, limitación v1): sin cambios — una cuenta por persona, nunca duplicada por cliente delegado.
- **`AmbitoTenantExplicito`** (jobs de fondo, Modo 2 de § 8): sin cambios de contrato — se **reutiliza** como mecanismo del fan-out de § 7.2, no se modifica.
- **Jerarquía `CoordinadorUsuarioId`/`EjecutivoUsuarioId`** (Fase 31, `AlcanceDatosService`): sin cambios — se reutiliza tal cual dentro del tenant Geseme para la jerarquía de mando de § 5.2.

## 11. Qué queda abierto (a resolver antes de implementar, no decisiones tomadas)

1. **UI del Workspace/selector de cliente activo**: ¿pantalla dedicada tras login, o un control persistente en la cabecera ("Workspace: Geseme · Cliente activo: KHS ▼")? Y qué pasa con el Context Workspace (`PLAN-CONTEXT-WORKSPACE.md`) si está abierto al cambiar de tenant activo — probablemente se cierra, mismo criterio que su § 8.1 (cambiar de contexto de alto nivel cierra el panel). Nombrar bien esto importa: no llamarlo "selector de tenant" de cara al usuario (es lenguaje interno) — el usuario ve "Workspace"/"cliente activo", nunca la palabra "tenant".
2. **Quién puede crear/desactivar una `DelegacionTenant`**: ¿el administrador del tenant cliente (autoservicio, "delego mi gestión a Geseme") o solo `Administrador`/`DireccionCae` de la plataforma? Tiene implicaciones comerciales (¿es un flujo de venta autoservicio o gestionado por Hydra?).
3. **Límites de la delegación operativa**: ¿es todo-o-nada dentro del rol asignado (§ 5.3), o puede restringirse más (p. ej. una consultora delegada nunca puede eliminar un Trabajador, aunque su rol nativo sí podría)? Asumir todo-o-nada hasta que se pida lo contrario (YAGNI).
4. **Expiración/renovación de delegaciones**: ¿`DelegacionTenant` necesita fecha de fin automática o es puramente manual (on/off)? No especificado.
5. **Alcance exacto de la Capa de Reporting (§ 7)**: ¿qué KPIs concretos se agregan en v1 (documentos pendientes, ¿algo más)? ¿Hace falta cachear/paginar el fan-out si un Director tiene delegación sobre decenas de tenants, o basta con ejecutarlo en caliente para v1? No especificado, y afecta directamente el rework de Dashboard (§ 7.4).
6. **Migración de datos existentes**: Geseme como tenant #1 (la organización actual en producción, `ADR-003`) ya tiene datos operativos propios (Empresas, Centros...) — no encaja tal cual en "un Workspace sin datos operativos" de § 5.1. Hace falta decidir si Geseme-tenant-#1 se queda como está (gestionando sus propias `Empresa` puertas adentro, Escenario 1 de `docs/MULTITENANCY.md` § 2) y la delegación se usa solo para clientes *nuevos* que se sumen después, o si hay que separar "Geseme como operador" de "los datos que Geseme ya tiene cargados" — a confirmar con el usuario antes de tocar el tenant #1 real.
7. **Relación con las condiciones de salida de `ADR-003`**: la migración a PostgreSQL y el DPA/Términos de Uso (pendientes) probablemente necesiten contemplar explícitamente el caso "cliente delega su gestión a un tercero" en el propio DPA — no es solo Hydra-como-encargado-del-cliente, ahora hay una tercera parte (la consultora) operando sobre los datos del cliente. Revisión legal, no implementación unilateral (regla de `CLAUDE.md`).
