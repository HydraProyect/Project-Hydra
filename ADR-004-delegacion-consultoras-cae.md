# ADR-004 — Delegación reversible de gestión CAE a consultoras externas

**Estado**: Propuesta de arquitectura, decisiones cerradas con el usuario el 2026-07-25. No implementado — este documento es la fase de diseño pedida antes de tocar código (mismo criterio que `PLAN-CONTEXT-WORKSPACE.md`/`ADR-003`). Motivado por el caso real que destraba el primer cliente de Hydra: Geseme necesita operar la CAE de varios clientes (KHS, Tomra, Blasau) sin que cada uno sea un tenant que Geseme "posee".

---

## 1. El problema

`docs/MULTITENANCY.md` § 8 (Tenant Resolution Strategy) resuelve **exactamente un tenant por sesión**, desde un claim fijado al login, fallo cerrado, sin ningún modo "sin filtro". Es correcto para el Escenario 1 de § 2 (una consultora que gestiona varias `Empresa` **dentro** de su propio tenant), pero no cubre el caso real pedido: una consultora cuyos usuarios necesitan operar sobre **varios tenants ajenos**, cada uno propiedad de un cliente distinto, sin que los datos se mezclen ni se dupliquen.

## 2. Los cuatro escenarios de negocio (pedidos por el usuario, 2026-07-25)

1. **Gestión interna** — una pyme (p. ej. Aislamientos Juan) usa Hydra directamente. Su tenant, sus datos, sus usuarios. Sin cambios sobre el modelo actual.
2. **Crecimiento y externalización** — la pyme crece y delega la operación completa a una consultora (Geseme). **No se migra nada, no se crea un tenant nuevo, no se duplica la base de datos** — solo se conceden permisos delegados para que usuarios de Geseme operen sobre el tenant ya existente del cliente.
3. **Modelo híbrido** — el Coordinador CAE interno del cliente y los Gestores CAE de Geseme trabajan **simultáneamente** sobre el mismo tenant, cada uno con su rol/alcance, con trazabilidad de quién hizo qué y desde dónde.
4. **Internalización** — el cliente monta su propio departamento CAE y retira la delegación. Los usuarios de Geseme pierden acceso, pero **ningún dato desaparece** — todo el histórico (documentos, auditoría, configuración) sigue siendo del cliente, porque siempre vivió en su tenant.

**Principio fundamental (cita literal del usuario, es la frase que fija el diseño)**: *"La autorización no debe modelarse como una relación de propiedad, sino como una delegación de acceso."* La separación entre **quién es dueño del dato** (siempre el tenant cliente) y **quién puede operar sobre él en cada momento** (nativo del tenant, delegado, o ambos) es un principio arquitectónico, no un detalle de permisos.

## 3. Por qué esto no toca el mecanismo de aislamiento ya auditado

Consecuencia directa del principio de § 2: el filtro global de EF Core (`TenantId == tenantActual.TenantId`) y el interceptor de sellado en escritura (`TenantSelladoInterceptor`) **no cambian una sola línea**. Siguen siendo exactamente un `TenantId` por query, sellado en cada fila nueva, fallo cerrado. Lo único que cambia es **de dónde sale ese `TenantId`** para una sesión de un usuario delegado — el mecanismo que ya tiene 25 tests de aislamiento por agregado no se reabre.

Esto es deliberado: es la forma más segura de añadir esta capacidad sin arriesgar la garantía ya probada. La delegación vive **por encima** de la Capa 1 (Tenant) de `docs/MULTITENANCY.md` § 6 — es una Capa 0 nueva ("¿puede este usuario resolver este tenant en absoluto?"), no una modificación de las capas 1-4 existentes.

## 4. Modelo de dominio

### 4.1 `Consultora` = un `Tenant` sin datos operativos propios (decisión, no un concepto nuevo)

Se evaluaron dos caminos:

| Opción | Descripción | Veredicto |
|---|---|---|
| **(a) Consultora es un `Tenant` más** | Geseme es tenant #N, igual que cualquier otro. Sus usuarios (`christopher@geseme.com`) tienen `ApplicationUser.TenantId = TenantGeseme`. Ese tenant **nunca tiene filas** de `Empresa`/`Centro`/`Documento`/etc. — solo existe para dar de alta a sus propios usuarios. | **Elegida.** Encaja literalmente con `docs/MULTITENANCY.md` § 1: "el Tenant es la organización que compra y utiliza Hydra" — Geseme compra Hydra igual que KHS, solo que no para almacenar su propia CAE. Reutiliza el 100% de la infraestructura de `Tenant`/`ApplicationUser` ya construida y auditada: cero entidades nuevas para la identidad. |
| (b) Consultora es un concepto nuevo, fuera del modelo de Tenant | Tabla `Consultora` global, con sus propios usuarios fuera del particionado por tenant. | Descartada: `ApplicationUser` está diseñado 1:1 con `Tenant` (unicidad `(TenantId, NormalizedUserName)`, filtro global). Sacar usuarios de ese modelo es más invasivo que reutilizarlo, y no aporta nada que (a) no dé ya. |

Con (a), la unicidad de login por email global (`docs/MULTITENANCY.md` § 8, limitación v1 ya aceptada) sigue funcionando sin cambios: Christopher tiene **una sola cuenta**, en el tenant Geseme — nunca una cuenta duplicada por cada cliente al que se le delega.

### 4.2 Entidades nuevas

- **`DelegacionTenant`** (catálogo global, mismo tratamiento que `Tenant` — exceptuado del filtro estándar de `TenantId`, ver `docs/MULTITENANCY.md` § 4 excepción 1): `TenantConsultoraId` (FK a `Tenant`), `TenantClienteId` (FK a `Tenant`), `Activa` (bool). **Desactivar, nunca borrar** — Escenario 4 (internalización) es `Activa = false`, no un `DELETE`; conserva el histórico de qué consultora operó sobre qué tenant y cuándo, ver § 4.4.
- **`AsignacionUsuarioDelegado`**: `DelegacionTenantId` (FK), `UsuarioId` (FK a `ApplicationUser`, usuario de la consultora), `Rol` (el rol con el que ese usuario opera en *ese* tenant cliente — un mismo Gestor de Geseme puede tener roles distintos en clientes distintos). Resuelve exactamente el ejemplo del usuario: *"Christopher → KHS y Tomra · María → Blasau · Pedro → KHS"*.

Ninguna de las dos entidades tiene FK hacia `Empresa`/`Centro`/`Trabajador`/`Documento` — son puramente de autorización, nunca de dominio CAE. Es la representación literal de "delegación de acceso, no de propiedad".

### 4.3 Qué NO cambia

`Empresa`, `Centro`, `Trabajador`, `Vehiculo`, `Documento`, `RequisitoDocumental`... — cero cambios de esquema. Todas sus filas siguen teniendo `TenantId = TenantCliente`, nunca `TenantId = TenantConsultora`. Es la prueba de que el modelo cumple "el cliente es siempre dueño de la información": técnicamente no hay forma de que una fila de KHS termine con `TenantId = Geseme`, el interceptor de sellado sigue sellando contra el tenant *activo* de la sesión (§ 5), que para un Gestor de Geseme operando sobre KHS es `TenantClienteId = KHS` — nunca el suyo propio.

### 4.4 Reversibilidad sin migración (Escenario 4)

Apagar una delegación (`DelegacionTenant.Activa = false`) no mueve ni borra una sola fila de `Empresa`/`Documento`/etc. — nunca las tocó, porque nunca fueron del tenant de la consultora. Es la consecuencia natural de § 4.3, no una funcionalidad aparte que haya que construir con cuidado extra.

## 5. Tenant Resolution Strategy v2 — cuarto modo

`docs/MULTITENANCY.md` § 8 documenta hoy tres modos: **claim de sesión** (usuarios interactivos), **ámbito explícito de jobs** (`AmbitoTenantExplicito`, procesos de fondo) y **webhooks de integraciones** (identificador de recurso + firma HMAC). Se añade un cuarto:

**Modo 4 — Tenant activo por delegación.** Al login, se resuelve el conjunto de tenants operables por el usuario: `{ su propio tenant (home) } ∪ { TenantClienteId de toda DelegacionTenant activa donde TenantConsultoraId = home y exista una AsignacionUsuarioDelegado para este usuario }`.

- Si el conjunto tiene **un solo elemento** (el caso de hoy, el 95% de los usuarios: un `Cliente`/`KHS` sin ninguna delegación), no hay selector — comportamiento idéntico al actual, cero cambio de UX para quien no está delegado.
- Si tiene **más de uno**, se presenta un selector de "tenant activo" tras el login (pantalla nueva). El tenant elegido pasa a ser el activo para el resto de la sesión.
- **Cambiar de tenant activo a mitad de sesión** (Christopher pasa de operar KHS a operar Tomra sin cerrar sesión) exige que `ITenantActual` deje de depender solo del claim fijo de la cookie de login — se añade una fuente de resolución adicional, con la misma prioridad/patrón que ya se introdujo en la Fase 44 de `ROADMAP.md` para el fallback de `IHttpContextAccessor` en `TenantActual`/`CurrentUserService`: un valor de "tenant activo elegido" con ámbito de circuito/sesión, consultado **antes** que el claim, y **siempre revalidado** contra `DelegacionTenant`/`AsignacionUsuarioDelegado` en cada resolución — nunca se confía en un valor elegido una vez y cacheado sin volver a comprobar que la delegación sigue activa (si se desactiva a mitad de sesión, la siguiente resolución debe fallar cerrado, no seguir sirviendo datos del tenant retirado).
- Sigue siendo **fallo cerrado**: si el tenant activo solicitado no está en el conjunto operable resuelto en ese momento, `TenantId` resuelve a `null` — mismo criterio que hoy, nunca "sin filtro".

## 6. Autorización en capas — Capa 0 nueva

Extiende la tabla de `docs/MULTITENANCY.md` § 6:

| Capa | Mecanismo | Decide | Estado |
|---|---|---|---|
| **0. Delegación** (nueva) | `DelegacionTenant` + `AsignacionUsuarioDelegado` | Si un usuario ajeno al tenant puede siquiera resolverlo como activo | A implementar (este ADR) |
| 1. Tenant | Filtro global + interceptor | De qué organización es cada fila | Implementado (`ADR-003`) |
| 2. Rol | Policies ASP.NET Core | Qué puede hacer un usuario | Existe, sin cambios |
| 3. Cartera | `IAlcanceDatosService` | Qué subconjunto del tenant ve un rol restringido | Existe, sin cambios |
| 4. Escritura | `AutorizacionEscrituraBehavior` | Qué puede mutar | Existe, sin cambios |

La Capa 0 decide **si** se entra; las capas 2-4 deciden **qué se ve/hace dentro**, exactamente igual para un usuario nativo que para uno delegado — es el Escenario 3 (híbrido) resuelto por construcción: el Coordinador nativo de KHS y el Gestor delegado de Geseme conviven en el mismo tenant, cada uno con su propio rol evaluado normalmente por las Capas 2-3, sin que ninguna de las dos sepa "quién es nativo y quién es delegado" — esa distinción solo importa en la Capa 0.

## 7. Auditoría dual

`RegistroAuditoria` gana un campo nuevo, nullable: `ActuoDesdeTenantId` (el tenant de origen del usuario, cuando difiere del tenant sobre el que se escribió — es decir, cuando la operación fue delegada). El interceptor que ya escribe `RegistroAuditoria` en cada `SaveChanges` lo rellena comparando `ApplicationUser.TenantId` contra el tenant activo de la sesión — sin tocar ningún Command ni Query existente, mismo patrón que el resto de la auditoría (invisible para Application/Domain). Satisface literalmente el pedido: *"todas las acciones deben quedar auditadas indicando tanto el usuario que ejecutó la acción como la consultora desde la que actuó."*

## 8. Compatibilidad con lo ya construido

- **Aislamiento por tenant** (`ADR-003`, 25 tests): sin cambios, ver § 3.
- **`IAlcanceDatosService`** (cartera por rol): sin cambios — opera siempre dentro del tenant ya resuelto, sea nativo o delegado.
- **Unicidad de login por email global** (`docs/MULTITENANCY.md` § 8, limitación v1): sin cambios — una cuenta por persona, nunca duplicada por cliente delegado.
- **`AmbitoTenantExplicito`** (jobs de fondo, Modo 2 de § 8): sin cambios — sigue siendo el mecanismo correcto para procesos sin usuario.

## 9. Qué queda fuera de este documento (a resolver antes de implementar, no decisiones tomadas)

1. **UI del selector de tenant activo**: dónde vive (¿pantalla dedicada tras login, o un selector persistente en la cabecera, tipo "cambiar de organización"?), y qué pasa con el Context Workspace (`PLAN-CONTEXT-WORKSPACE.md`) si está abierto al cambiar de tenant activo — probablemente se cierra, mismo criterio que § 8.1 de ese documento (cambiar de contexto de alto nivel cierra el panel).
2. **Quién puede crear/desactivar una `DelegacionTenant`**: ¿el administrador del tenant cliente (autoservicio, "delego mi gestión a Geseme") o solo `Administrador`/`DireccionCae` de la plataforma? Tiene implicaciones comerciales (¿es un flujo de venta autoservicio o gestionado por Hydra?).
3. **Límites de la delegación**: ¿es todo-o-nada (el usuario delegado ve/hace lo mismo que un nativo con su rol) o puede restringirse más (p. ej. una consultora delegada nunca puede eliminar un Trabajador, aunque su rol nativo sí podría)? El pedido original no lo especifica — asumir todo-o-nada dentro del rol asignado (§ 4.2) hasta que se pida lo contrario (YAGNI).
4. **Expiración/renovación de delegaciones**: ¿`DelegacionTenant` necesita fecha de fin automática o es puramente manual (on/off)? No especificado.
5. **Migración de datos existentes**: Geseme como tenant #1 (la organización actual en producción, `ADR-003`) ya tiene datos operativos propios (Empresas, Centros...) — no encaja tal cual en "un tenant sin datos operativos" de § 4.1. Hace falta decidir si Geseme-tenant-#1 se queda como está (gestionando sus propias `Empresa` puertas adentro, Escenario 1 de `docs/MULTITENANCY.md` § 2) y la delegación se usa solo para clientes *nuevos* que se sumen después, o si hay que separar "Geseme como operador" de "los datos que Geseme ya tiene cargados" — a confirmar con el usuario antes de tocar el tenant #1 real.
6. **Relación con las condiciones de salida de `ADR-003`**: la migración a PostgreSQL y el DPA/Términos de Uso (pendientes, ver conversación con el usuario 2026-07-25) probablemente necesiten contemplar explícitamente el caso "cliente delega su gestión a un tercero" en el propio DPA — no es solo Hydra-como-encargado-del-cliente, ahora hay una tercera parte (la consultora) operando sobre los datos del cliente. Revisión legal, no implementación unilateral (regla de `CLAUDE.md`).
