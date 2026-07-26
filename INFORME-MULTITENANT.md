# Informe técnico — Redefinición de la arquitectura Multi-Tenant (Fase 1: Análisis / Fase 2: Propuesta)

**Estado**: Análisis y propuesta. **Nada de este documento está implementado.** Corresponde a las Fases 1–2 del proceso acordado (Analizar → Proponer → Debatir → Aprobar → Implementar → Validar).

> **Addendum (2026-07-23)**: la decisión pendiente de § 12 quedó **resuelta — aprobado in-place con `ADR-003-saas-multitenant.md`** (supersede la cláusula fork de ADR-002). En el debate se añadieron dos precisiones que este informe incorpora por referencia a `docs/MULTITENANCY.md`: (1) los catálogos **no** son todos por tenant — clasificación global vs. por-tenant justificada caso a caso en `MULTITENANCY.md` § 7 (matiza el R5 de este informe); (2) la **Tenant Resolution Strategy** (resolución por claim de sesión con fallo cerrado, subdominios como evolución, jobs con ámbito explícito por tenant) está en `MULTITENANCY.md` § 8 y debe estar aprobada antes de iniciar implementación. La fase de consolidación documental (ADR-003, `DOMAIN.md`, `docs/MULTITENANCY.md`, actualización de PROJECT/ROADMAP/CLAUDE/ARCHITECTURE/DATABASE) está completada y pendiente de aprobación final.

**Contexto de partida**: dos escenarios de negocio definen el tenant — (1) una consultora PRL (ej. ArcoSPA) que gestiona la CAE de varias Empresas contratistas (Ibertec S.A., EcoPlant Reciclaje S.L., Techmed Equipos S.A.) frente a sus Clientes finales (Retail Iberia S.A., Bebidas del Norte S.A., Refrescos Levante S.A., Distribuciones Iberia S.L.), todo dentro de un mismo tenant; (2) venta directa a una contratista (tenant = Ibertec S.A., con una sola Empresa y sus Clientes). El Tenant es la organización que compra Hydra — **nunca** la entidad `Cliente` del dominio — y es la frontera absoluta de aislamiento.

---

## 1. Validación del modelo multi-tenant propuesto

**El modelo de dominio actual ya cumple los principios de dominio enunciados.** Verificado contra el código real (no contra la documentación, que está desactualizada — ver § 2):

| Principio enunciado | Estado en el código | Evidencia |
|---|---|---|
| Tenant ≠ Cliente; el tenant es un nivel por encima | Coherente con `ADR-001` (que ya hace esta distinción explícita) | `ADR-001` § Decisión |
| Empresa NO pertenece a un Cliente; N:N | ✅ Ya es así | `EmpresaCliente` con índice único `(EmpresaId, ClienteId)` — `EmpresaClienteConfiguration.cs` |
| Cliente puede contratar múltiples Empresas | ✅ Ya es así | Misma tabla N:N |
| Trabajador pertenece a una sola Empresa | ⚠️ Casi — pertenece a una Empresa **o** a una Subcontrata (`EmpresaId?`/`SubcontrataId?` mutuamente excluyentes, `EsDeSubcontrata`) | `Trabajador.cs` |
| Vehículo pertenece a una sola Empresa | ⚠️ Mismo matiz — Empresa o Subcontrata | `Vehiculo.cs` |
| Sin `ClienteId` redundante donde no hay un único Cliente garantizado | ✅ Ya es así — Trabajador/Vehiculo/Empresa/Subcontrata no tienen `ClienteId` | Verificado en Domain |
| Centro pertenece a un único Cliente, con `ClienteId` | ✅ Ya es así (FK requerida). Además tiene `EmpresaId` requerida — el Centro es "de un Cliente, operado por una Empresa" | `Centro.cs` |
| Vista de Cliente por consultas agregadas, no por denormalización | ✅ Alineado con el diseño ya hecho del Context Workspace | `PLAN-CONTEXT-WORKSPACE.md` |

**Conclusión de validación**: no hay que cambiar ninguna relación del dominio para soportar multi-tenant. El trabajo es **añadir la dimensión Tenant encima del modelo existente**, no corregir el modelo. Los dos escenarios de negocio (consultora ArcoSPA / venta directa a Ibertec S.A.) funcionan con el mismo esquema — el escenario 2 es simplemente un tenant con una sola Empresa; no requiere ninguna rama de código especial. Eso es señal de que la frontera está bien elegida.

El matiz Subcontrata debe resolverse en el enunciado, no en el código: la frase "Trabajador pertenece únicamente a una Empresa" debe ampliarse a "a una Empresa o a una Subcontrata" en `docs/MULTITENANCY.md` cuando se escriba — el dominio real ya distingue personal propio de personal subcontratado y eso es correcto para CAE.

---

## 2. Inconsistencias detectadas (documentación ↔ dominio ↔ petición)

Siguiendo la regla de trabajo acordada ("si encuentras contradicciones, detente y explícalas antes de implementar"):

### 2.1 ⛔ La contradicción mayor: `ADR-002` prohíbe hacer esto en este repositorio

`ADR-002-single-tenant.md` § Decisión, literal: *"Si en el futuro se decide explotar el modelo SaaS multi-cliente, se hace en un **repositorio distinto** (fork/duplicado de este...), nunca añadiendo `TenantId` a este repo mientras sirve datos reales de producción de una sola organización"*. Y `CLAUDE.md` instruye a toda sesión futura a no retomar multi-tenant sin instrucción explícita.

La instrucción explícita ahora existe (esta petición). Pero la petición pide implementar Tenant **en este repo**, y la cláusula del fork no era burocracia: protege una base de datos de producción con datos personales reales (incluida categoría de salud) de una migración de esquema estructural. **[DECISIÓN PENDIENTE — la más importante de este informe]**, ver § 12 y § 17.

### 2.2 Identidad del producto: la documentación dice lo contrario que esta petición

`PROJECT.md` ("uso inicial ~10 usuarios... single-tenant"), `CLAUDE.md` (multi-tenant en pausa), `ROADMAP.md` (punto 3 en pausa) e Issue #8 (`on-hold`/`future-fork`) describen un producto interno. Esta petición lo redefine como SaaS comercial para consultoras PRL y contratistas. Si se aprueba, **todos** esos documentos deben actualizarse en la misma fase (no después), o la próxima sesión de trabajo obedecerá `CLAUDE.md` y deshará el rumbo. Se necesita un **ADR-003** que supersede formalmente a ADR-002 — no basta con empezar a codificar.

### 2.3 `DOMAIN.md` no existe

La petición pide analizarlo; no hay tal archivo en el repo. El modelo de dominio está descrito en `DATABASE.md`, que además está **desactualizado**: no documenta `Subcontrata`, `Vehiculo`, `Visita`, `EmpresaCliente`, `TipoDocumentoCentro`, `DeteccionTrabajador`, `NotificacionUsuario`, ni el propietario polimórfico de `Documento` (hoy `TrabajadorId?`/`ClienteId?`/`EmpresaId?`/`VehiculoId?`; el doc solo describe Trabajador). Dice también que `Trabajador.EmpresaId` es requerida — hoy es nullable. Actualizar `DATABASE.md` es prerequisito de cualquier trabajo serio de esquema, multi-tenant o no.

### 2.4 `ARCHITECTURE.md` menciona Domain Events que no existen

`ARCHITECTURE.md` lista `DomainEvent` en `Domain/Common`; el código no tiene ninguna infraestructura de eventos de dominio (verificado por búsqueda). No es bloqueante para multi-tenant, pero la petición lista "Domain Events" entre los principios a preservar — no se puede preservar lo que no existe. Decidir: construirlos cuando haya un caso de uso real (YAGNI), o corregir `ARCHITECTURE.md`. Recomendación: lo segundo, por ahora.

### 2.5 La auditoría de ADR-001 quedó corta: ahora hay 46 queries, no 39

Desde la auditoría de 2026-07-17 se añadieron queries (Visitas, Vehículos, Lectura IA, Detección, Notificaciones...). Verificado hoy: **sigue sin existir ni un solo uso de `FromSqlRaw`/`ExecuteSqlRaw`/`IgnoreQueryFilters`** en `src/` — la propiedad que hace seguro el filtro global se mantiene intacta. La conclusión central de ADR-001 ("el filtro global protege todas las queries a la vez") sigue siendo válida, pero el número debe re-certificarse en la fase de implementación, no citarse de memoria.

### 2.6 SQLite contra la ambición SaaS

`ARCHITECTURE.md` fija SQLite como BD de v1 con proveedor intercambiable. Para un SaaS multi-tenant comercial (N organizaciones concurrentes escribiendo), SQLite de archivo único es un límite real (un escritor a la vez). No bloquea implementar `TenantId` (el mecanismo es idéntico), pero la salida a producción SaaS debe ir acompañada de la migración a PostgreSQL ya prevista como intercambiable. Debe constar como condición de salida, junto a la ya existente en ADR-001 (no self-signup/billing sin aislamiento auditado).

### 2.7 SSO Entra ID está atado a un único tenant de Microsoft

`RestriccionLoginLocalClaimsTransformation` y la config `AzureAd:*` asumen **el** tenant corporativo de una sola organización. En SaaS, cada tenant de Hydra querrá su propio IdP (o ninguno). No es bloqueante para la fase de datos, pero es deuda conocida del modelo de identidad — debe entrar en el backlog SaaS explícitamente.

---

## 3. Riesgos

| # | Riesgo | Severidad | Mitigación propuesta |
|---|---|---|---|
| R1 | **Migración de esquema sobre datos personales reales de producción** (la razón de la cláusula fork de ADR-002) | Crítica | Ver § 12 — decisión fork vs. in-place; si in-place: backup verificado + ensayo en copia + `MigracionesTests` ampliados |
| R2 | **Fuga entre tenants por una vía no cubierta por el filtro global**: almacenamiento de archivos (`IFileStorageService` guarda en disco por ruta no particionada), descargas (`/documentos/{id}/archivo`), exportaciones Excel/PDF, `BuscarGlobalQuery` | Crítica | `TenantId` en la ruta física de archivos; los endpoints ya pasan por queries filtradas (verificar en test de integración por endpoint) |
| R3 | **Tablas transversales sin partición**: `RegistroAuditoria`, `Alerta`, `NotificacionUsuario`, `DeteccionTrabajador` mezclarían datos de todos los tenants si se olvidan | Alta | Incluidas en la lista § 7 — `TenantId` en todas, sin excepciones "porque es solo log" |
| R4 | **Identity**: `ApplicationUser` sin `TenantId` permitiría a un usuario autenticarse y resolver un tenant equivocado; email único global impediría que una misma persona exista en dos tenants | Alta | `TenantId` en `ApplicationUser`; unicidad de usuario por `(TenantId, NormalizedUserName)` — requiere sustituir el índice de Identity |
| R5 | **Catálogos hoy globales**: `TipoDocumento` (con seed de 15+ tipos) y `ParametroSistema` (umbrales 30/15) son configuración de negocio que cada tenant querrá suya | Alta | Pasan a ser por-tenant, con seed al aprovisionar el tenant (§ 7) |
| R6 | **RGPD cambia de naturaleza**: al vender Hydra, la organización pasa de responsable único a **encargado del tratamiento** de cada tenant — reactiva la mitad "DPA con clientes externos / Términos SaaS" que ADR-002 § 6 marcó como "no aplica" (Issue #13) | Alta | Regla ya vigente en `CLAUDE.md`: nada de cumplimiento sin confirmación del usuario — se lista como bloqueante de salida a producción, no se implementa unilateralmente |
| R7 | **Cifrado**: los `IDataProtector` de credenciales usan purpose strings globales; las claves Data Protection son una sola instalación | Media | Suficiente en BD compartida v1; si algún tenant migra a BD propia (previsto en ADR-001), revisar entonces — no antes (YAGNI) |
| R8 | **Referencias cruzadas en escritura**: un Command que recibe un Id de otro tenant (p. ej. `CrearCentro` con `ClienteId` ajeno) | Media | El filtro global hace que el lookup del agregado devuelva "no encontrado" — pero solo si **todo** Command carga la entidad referenciada antes de usar el Id; añadir esa verificación al checklist de revisión + tests |
| R9 | **Baja de un tenant** (offboarding): borrado/exportación completa de sus datos, incluidos archivos y auditoría | Media | Diseñarlo como caso de uso desde el inicio (Art. 28 RGPD lo exigirá por contrato); no construirlo hasta que haya segundo tenant real |
| R10 | **Vecino ruidoso** (un tenant grande degrada a los demás) | Baja hoy | Aceptado en v1; la vía de escape ya está en ADR-001 (mover tenant a BD propia) |

---

## 4. Cambios necesarios (inventario completo, sin implementar)

1. **Nueva entidad `Tenant`** (agregado raíz nuevo): `Id`, `Nombre`, `Estado` (activo/suspendido), `CreadoEnUtc`. Sin billing ni self-signup (regla ADR-001 se mantiene).
2. **`TenantId` (Guid, requerido)** en todas las entidades de § 7.
3. **`ITenantActual`** (Application): servicio que resuelve el tenant del usuario autenticado desde claims — mismo patrón que `ICurrentUserService`.
4. **Filtro global combinado** por entidad: `HasQueryFilter(x => !x.EstaEliminado && x.TenantId == tenantActual)` — EF Core admite **un solo** `HasQueryFilter` por entidad, así que se **combina** con el de soft-delete existente, no se añade un segundo (detalle fácil de hacer mal: un `HasQueryFilter` nuevo reemplazaría silenciosamente al de soft-delete).
5. **Interceptor de sellado en escritura** (`SaveChanges`): asigna `TenantId` a toda entidad nueva desde `ITenantActual` y **rechaza** cualquier entidad modificada cuyo `TenantId` no coincida — mismo lugar arquitectónico que `AuditoriaInterceptor`. Los Commands no reciben ni pasan `TenantId` jamás.
6. **Índices únicos**: 7 globales → compuestos por tenant (§ 11).
7. **Identity**: `TenantId` en `ApplicationUser` + unicidad por tenant (R4). Roles siguen siendo globales (son código, no datos de tenant).
8. **Seed por tenant**: `TipoDocumentoSeedData` y `ParametroSistema` pasan de seed global a aprovisionamiento al crear tenant.
9. **`IFileStorageService`**: prefijo de ruta por tenant (`{tenantId}/{...}`) para archivos nuevos; migración de rutas existentes según § 12.
10. **Migración EF** + backfill (§ 12).
11. **Tests**: unitarios del interceptor de sellado; integración "dos tenants no se ven" por cada agregado expuesto en `IApplicationDbContext`; ampliar `MigracionesTests`.
12. **Documentación**: ADR-003, `docs/MULTITENANCY.md`, y actualización de `DATABASE.md` (ya debida, § 2.3), `ARCHITECTURE.md`, `PROJECT.md`, `CLAUDE.md`, `ROADMAP.md`, Issue #8.

---

## 5. Relaciones correctas (confirmación)

El grafo verificado en `PLAN-MASTER-DETAIL-WORKSPACE.md` § 2 es el correcto y **no cambia** con multi-tenant. En particular se preservan intactas, por ser exactamente lo que el negocio CAE exige:

- `Empresa ←→ EmpresaCliente ←→ Cliente` (N:N) — **no se rompe**.
- `Subcontrata` N:N con Cliente y con Empresa.
- `Trabajador`/`Vehiculo` → Empresa **o** Subcontrata; relación con Cliente solo vía `Asignacion`+`Centro` (Trabajador) o transitiva (Vehículo).
- `Centro` → un Cliente (`ClienteId` ✓) y una Empresa operadora (`EmpresaId` ✓).
- `Documento` → propietario polimórfico excluyente (Trabajador/Cliente/Empresa/Vehículo).

`TenantId` es **ortogonal** a todo esto: no sustituye ninguna FK, no reordena la jerarquía, y el Cliente sigue sin ser el padre de Empresa (la conclusión ya debatida y acordada en la conversación previa sobre `ClienteId` denormalizado se mantiene: la "vista Cliente" se sirve con consultas agregadas del Context Workspace, no con FKs redundantes).

## 6. Aggregate Roots

Raíces actuales (cada una con repositorio propio, según la convención del proyecto): `Cliente`, `Empresa`, `Subcontrata`, `Centro` (con `PlataformaAcceso` y `RequisitoDocumental` como satélites), `Trabajador`, `Vehiculo`, `Documento`, `TipoDocumento`, `Asignacion`, `Visita`, `Alerta`, `NotificacionUsuario`, `ParametroSistema`, `RegistroAuditoria`. Se añade **`Tenant`** como raíz nueva. Las tablas de unión (`EmpresaCliente`, `SubcontrataCliente`, `SubcontrataEmpresa`, `TipoDocumentoCentro`, `VisitaTrabajador`) no son raíces — se gestionan a través de sus raíces.

**Dónde vive `TenantId` en DDD**: en `EntidadBase` (y `Entity` para las de unión), como propiedad de asignación única cuyo único escritor es el interceptor de Infrastructure. El dominio no razona sobre tenants (ninguna regla de negocio compara TenantIds — eso es aislamiento, no negocio); mantenerlo fuera de los constructores de dominio evita contaminar 40+ firmas y respeta que sea un concern transversal, igual que ya se trata `EstaEliminado`. Es un compromiso pragmático deliberado, no un descuido — se documenta como tal en `docs/MULTITENANCY.md`.

## 7. Entidades que deben tener `TenantId`

**Todas las tablas de datos, sin excepción**, incluidas las de unión y las transversales — la defensa en profundidad exige que ninguna tabla dependa de un JOIN para saber de quién es:

`Cliente`, `Centro`, `PlataformaAcceso`, `Empresa`, `EmpresaCliente`, `CredencialAccesoEmpresa`, `Subcontrata`, `SubcontrataCliente`, `SubcontrataEmpresa`, `CredencialAccesoSubcontrata`, `Trabajador`, `DeteccionTrabajador`, `Vehiculo`, `TipoDocumento`, `TipoDocumentoCentro`, `ConfiguracionIaDocumentoCliente`, `Documento`, `Asignacion`, `Visita`, `VisitaTrabajador`, `RequisitoDocumental`, `Alerta`, `NotificacionUsuario`, `ParametroSistema` (deja de ser singleton global → una fila por tenant), `RegistroAuditoria`, `ApplicationUser`.

Quedan **sin** `TenantId`: `AspNetRoles` (catálogo de código, global), tablas puramente de infraestructura de Identity que derivan del usuario (`AspNetUserRoles` etc. — el usuario ya está particionado), y `Tenant` misma.

## 8. Entidades que NO deben tener `ClienteId`

Confirmando el principio ya acordado (y cerrando el debate de la conversación anterior): **`Empresa`, `Subcontrata`, `Trabajador`, `Vehiculo`** no llevan `ClienteId` — el dominio no garantiza un único Cliente para ellas (N:N y asignaciones múltiples). `Documento` conserva su `ClienteId?` **solo** como uno de los cuatro propietarios polimórficos posibles (es propiedad del documento, no partición). `Centro.ClienteId` se queda como está (relación real 1:N). La sensación de "todo pertenece al Cliente" se construye en la capa de lectura (Context Workspace, § 5), no en el esquema.

## 9. Estrategia de autorización (capas, de fuera hacia dentro)

1. **Tenant** (nuevo, infraestructura): filtro global + interceptor de sellado. Invisible para Application. Decide *de qué organización* es cada fila.
2. **Rol** (existente): policies de ASP.NET Core (`Administrador`...`Cliente`). Decide *qué puede hacer* un usuario.
3. **Cartera** (existente, `IAlcanceDatosService`): decide *qué subconjunto del tenant* ve un rol restringido (GestorCae, CoordinadorCae, rol Cliente). **No se toca** — opera dentro del tenant, después del filtro. La corrección IDOR del Issue #18 (checks en `*PorId*`) se mantiene tal cual.
4. **Escritura** (existente): `AutorizacionEscrituraBehavior` + la verificación R8 (los Commands cargan las entidades referenciadas, y el filtro de tenant convierte un Id ajeno en "no encontrado").

Resolución del tenant actual: claim `tenant_id` en la cookie/token, poblado al autenticar desde `ApplicationUser.TenantId`. Fallo cerrado: sin claim de tenant resoluble → sin datos (lista vacía / 403), nunca "sin filtro".

## 10. Estrategia de consultas

- Las 46 queries actuales **no se modifican una a una**: el filtro global las cubre (validado: cero SQL crudo, cero `IgnoreQueryFilters`, todo LINQ sobre `IApplicationDbContext`). La regla de `CLAUDE.md` que prohíbe introducirlos sin revisión pasa de "buena práctica" a **frontera de seguridad entre tenants** y se refuerza en `CODING_STANDARDS.md`.
- `CaeManagerDbContext` pasa a recibir `ITenantActual` por DI para poder expresar el filtro — el DbContext ya es scoped por request/circuito, encaja sin cambios de ciclo de vida.
- Las consultas agregadas del Context Workspace (pestañas por entidad) heredan el aislamiento automáticamente — ninguna necesita conocer el tenant.
- Reportes y exportaciones (Excel/PDF) usan las mismas queries → cubiertos; se verifica con un test de integración por endpoint de descarga (R2).

## 11. Estrategia de índices

- Únicos globales → compuestos: `(TenantId, Cif)` en Cliente y Empresa, `(TenantId, RazonSocial)` en Empresa y Subcontrata, `(TenantId, Nombre)` en TipoDocumento, `(TenantId, Dni)` en Trabajador, `(TenantId, NumeroPlaca)` en Vehiculo — los 7 de ADR-001, confirmados hoy en las `*Configuration.cs`. Caso de negocio real: el mismo trabajador (mismo DNI) puede existir legítimamente en dos tenants (Ibertec S.A. como tenant directo y ArcoSPA gestionando a Ibertec S.A.).
- Únicos de unión → se les antepone `TenantId` igualmente (`(TenantId, EmpresaId, ClienteId)`, etc.): redundante en teoría, barato en la práctica, y hace imposible una fila de unión cruzada entre tenants incluso ante un bug del sellado.
- Identity: `(TenantId, NormalizedUserName)` y `(TenantId, NormalizedEmail)` sustituyen la unicidad global (R4).
- Con el filtro global inyectando `TenantId = ?` en cada WHERE, poner `TenantId` como **primera** columna de los índices compuestos es lo que mantiene el plan de consulta selectivo — no hacen falta índices sueltos adicionales sobre `TenantId` en tablas que ya tengan compuestos.

## 12. Estrategia de migración — **[DECISIÓN PENDIENTE: fork vs. in-place]**

La cláusula de ADR-002 (§ 2.1) obliga a decidir esto **antes** de cualquier implementación. Trade-offs honestos:

| | **A. En este repo (in-place)** | **B. Fork limpio (lo que dicta ADR-002)** |
|---|---|---|
| Riesgo sobre datos reales de producción | Real: migración estructural de 25+ tablas con datos personales/salud | Cero: el fork nace con BD vacía |
| Continuidad | La organización actual se convierte en "tenant #1" sin re-migrar datos a mano | Hay que decidir qué pasa con la instalación interna (¿se queda en el repo viejo? ¿doble mantenimiento?) |
| Coste de mantenimiento | Un solo código base | Dos códigos base divergiendo desde el día 1 — el coste que ADR-002 no contabilizó |
| Coherencia documental | Requiere ADR-003 que supersede explícitamente la cláusula fork | Cumple ADR-002 tal cual |

**Recomendación del arquitecto**: **A (in-place), con ADR-003 formal**, porque el supuesto de ADR-002 ("la necesidad todavía no existe") es exactamente lo que esta petición cambia — y un fork con dos productos vivos es el mayor generador de deuda posible para un equipo de este tamaño. **Condicionado a** este plan de ejecución por etapas, cada una desplegable y reversible:

1. **Etapa 0 — Ensayo**: backup verificado + restauración de prueba; la migración completa se ensaya contra una copia real antes de tocar producción. `MigracionesTests` (ya existen) se amplían para cubrirla.
2. **Etapa 1 — Esquema aditivo**: tabla `Tenant` + columna `TenantId` **nullable** en todo § 7. Sin filtros aún. Deploy sin impacto funcional.
3. **Etapa 2 — Backfill**: se crea el tenant "default" (la organización actual) y un `UPDATE` por tabla sella todas las filas. Verificación: cero filas con `TenantId` NULL.
4. **Etapa 3 — Cierre**: `TenantId` pasa a NOT NULL, se activan filtro global + interceptor, los índices únicos se sustituyen por los compuestos (en SQLite esto es table-rebuild gestionado por EF — otra razón para la Etapa 0). Un solo deploy, el único con riesgo real.
5. **Etapa 4 — Archivos**: los archivos existentes se mueven a `{tenantDefault}/...` con un comando de mantenimiento idempotente.
6. **Etapa 5 — Verificación end-to-end en navegador** (regla ya vigente en `CLAUDE.md` para cierre de fases) + test de integración "tenant A no ve datos de tenant B" por agregado.

Si prefieres respetar ADR-002 y hacer fork (opción B), este mismo plan aplica al fork menos las etapas 2, 4 y el riesgo de la 3 — todo lo demás del informe es idéntico en ambas opciones.

## 13. Impacto sobre CQRS

Mínimo y deliberadamente asimétrico: **las Queries no cambian** (filtro global); **los Commands no cambian de firma** (sellado por interceptor — mismo razonamiento por el que `AutorizacionEscrituraBehavior` es un pipeline behavior y no código repetido en cada handler). Lo único nuevo en la capa Application es la interfaz `ITenantActual` y la disciplina R8 en revisión de código. Ningún handler existente se edita.

## 14. Impacto sobre DDD

- El dominio queda **casi** intacto: `TenantId` en las clases base como dato sellado, sin lógica de negocio asociada (§ 6). Ninguna invariante de agregado cambia; ningún constructor cambia de firma.
- `Tenant` es un agregado raíz pequeño y anémico a propósito — hoy solo aprovisionamiento; crecerá (plan, límites, branding) cuando haya casos de uso reales, no antes.
- `ParametroSistema` cambia de semántica (singleton → por tenant): es el único cambio conceptual de dominio real de todo el proyecto multi-tenant.
- Domain Events: siguen sin existir y multi-tenant no los necesita (§ 2.4). No se introducen "de paso".

## 15. Impacto sobre rendimiento

- Filtro global = un predicado indexado más por query — despreciable a la escala actual y a la de decenas de tenants, **si** los índices compuestos de § 11 acompañan (TenantId primera columna).
- El interceptor de sellado es O(entidades cambiadas) por `SaveChanges` — despreciable.
- El límite real de rendimiento SaaS no es `TenantId`, es SQLite (§ 2.6): un escritor concurrente. La condición de salida a producción SaaS debe incluir PostgreSQL (cambio de proveedor ya diseñado como intercambiable en `ARCHITECTURE.md`).

## 16. Riesgos futuros (más allá de la implementación inicial)

Aprovisionamiento y baja de tenants como casos de uso completos (alta con seed, suspensión, exportación y borrado certificado — R9); SSO por tenant (§ 2.7); DPA/Términos SaaS por tenant (R6 — reactivación del Issue #13, con confirmación del usuario, nunca unilateral); facturación y self-signup (bloqueados por la regla de ADR-001 hasta que el aislamiento esté auditado); cuotas por tenant para los servicios de IA (Anthropic) que hoy son una sola clave global; migración de tenants grandes a BD propia (prevista, no construida); backup/restore selectivo por tenant.

## 17. Recomendaciones y orden de decisión

1. **Decidir § 12 (fork vs. in-place)** — todo lo demás cuelga de esto. Mi recomendación: in-place con ADR-003.
2. **Aprobar este informe** (Fase 4) con las correcciones que salgan del debate (Fase 3).
3. **Escribir ADR-003 + `docs/MULTITENANCY.md`** y actualizar `DATABASE.md` (deuda previa), `ARCHITECTURE.md`, `PROJECT.md`, `CLAUDE.md`, `ROADMAP.md`, Issue #8 — **antes** del primer commit de código, para que ninguna sesión futura trabaje contra la decisión.
4. **Implementar por las etapas de § 12**, con la verificación end-to-end de cada etapa.
5. **No mezclar** con este trabajo: el refactor Context Workspace (`PLAN-CONTEXT-WORKSPACE.md` — ortogonal, puede continuar en paralelo sobre queries que el filtro cubrirá igual), la unificación de las 3 clases de credenciales, ni ningún punto de cumplimiento normativo sin confirmación explícita.

**Bloqueantes de salida a producción SaaS** (resumen, ya justificados arriba): aislamiento implementado y auditado con tests por agregado; índices compuestos; archivos particionados; PostgreSQL; DPA/Términos por tenant; y la regla de ADR-001 intacta — sin self-signup ni billing antes de todo lo anterior.
