# MATURITY_REVIEW 2026-08-13 — Informe de madurez SaaS — Hydra (CAE Manager)

**Tipo**: Informe — snapshot fechado, segundo de la serie (el primero es `MATURITY_REVIEW.md`, 2026-08-01). No se edita: un informe futuro se añade como documento nuevo.
**Fecha del snapshot**: 2026-08-13 (HEAD `c0e2284`, con PR #190 mergeado; PR #174 —salida de Railway— evaluado como dirección declarada en draft, no como hecho).
**Método**: evidencia del repositorio verificada con código, no con documentación (recuentos de tests reales, grep de patrones, lectura de `Program.cs`, snapshot de migraciones, workflow de CI). Los apartados sin evidencia se marcan "Información insuficiente". Cada nota lleva entre paréntesis la del snapshot 2026-08-01 para medir evolución.

> **Comité**: Staff Software Engineer · Principal Solutions Architect · Senior Backend Engineer · Senior Frontend Engineer · Staff UX Designer · Product Manager · Security Engineer · DevOps Engineer · Database Architect · SaaS CTO.

**Contexto de la ventana evaluada**: en 12 días se cerraron con verificación ~26 de los 33 ítems P0-P3 del informe anterior, y además se construyeron ~20 fases de producto nuevas (Fases 73-93: ingesta Graph real, WhatsApp Cloud API, bandeja única, asignaciones en lote, ADR-005 Subcontratas, Dashboard BPO, gamificación). Ese doble ritmo —cerrar deuda y abrir features a la vez— es la historia central de este snapshot: la deuda técnica bajó de verdad, y el riesgo de producto (construir por delante del uso) subió.

---

## 1. Arquitectura — Global: **7,6/10** (antes 6,5)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Separación de responsabilidades | 8,5 | 8 |
| Modularidad | 7,5 | 7 |
| Escalabilidad | 6 | 4 |
| Flexibilidad | 7,5 | 7 |
| Mantenibilidad | 8,5 | 8 |
| Complejidad | 7 | 7 |
| Calidad de decisiones arquitectónicas | 7,5 | 6 |
| Coherencia documentación ↔ implementación | 8,5 | 6 |

- **Lo que cambió de verdad**: el `IApplicationDbContext` monolítico se partió en ~30 interfaces por feature (PR #60) y `CaeManagerDbContext` bajó a 349 líneas — el "archivo más grave del sistema" del informe anterior ya no lo es. El pipeline de MediatR está completo: Validation, Autorización (ahora por marker `ICommand`, no por convención de nombre), Concurrencia, Logging y Serialización.
- **Coherencia 8,5 — la mejora más estructural**: la deriva documental que el informe anterior encontró 4 veces ahora tiene *enforcement*: el job de CI "Frontera de autoridad documental" (`scripts/validar-gobernanza-docs.py`) falla el build si un documento sin autoridad gobierna decisiones. Pasar de "la deriva es cuestión de tiempo" a "la deriva rompe CI" es un salto de categoría que casi ningún SaaS tiene.
- **Escalabilidad 6, no más**: `PuertaAccesoDatos` (el `SemaphoreSlim(1,1)` por circuito) **sigue ahí**. El intento de sustituirlo por `IDbContextFactory` se revirtió con razones documentadas y honestas (~31 repositorios Scoped reproducían el bug un nivel más abajo) — decisión correcta de no hacer el cambio a medias, pero el cuello de botella persiste. Multi-réplica está *construida* (backplane Redis, advisory lock, llavero en S3) pero apagada y jamás ejercitada en producción; el rate limiter es en memoria con techo 1 réplica autodocumentado.
- **Sin ADR nuevo donde hace falta**: Blazor Server interactivo en todo sigue sin examen escrito de capacidad (¿cuántos circuitos concurrentes por GB? ¿cuándo duele?). `CircuitOptions` sigue sin configurar — grep: 0 resultados, igual que hace 12 días.

## 2. Backend — Global: **7,8/10** (antes 7,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Organización | 8,5 | 8,5 |
| Clean Architecture | 8,5 | 8 |
| DDD | 6 | 6 |
| CQRS | 8 | 7,5 |
| Uso de patrones | 8 | 7 |
| Calidad del código | 8,5 | 8,5 |
| Legibilidad | 8,5 | 8,5 |
| Extensibilidad | 7,5 | 7 |
| Testabilidad | 8,5 | 8 |
| Manejo de errores | 7,5 | 7 |
| Logging | 8 | 3,5 |
| Validaciones | 8 | 7,5 |
| Rendimiento potencial | 6,5 | 6 |

- **Logging: de 3,5 a 8, el mayor salto individual del informe**: `LoggingBehavior` registra comando, duración, tenant, usuario y resultado; `UseSerilogRequestLogging` correlaciona la vía HTTP con `TenantId`/`UsuarioId`; sink Seq listo (inerte sin configurar — ver DevOps). Lo que falta para 9+: OpenTelemetry (1 mención en todo src, sin trazas ni métricas) — hoy hay *logs* correlacionados, no *observabilidad*.
- **DDD sigue en 6, y ahora con un agravante**: los value objects `Dni`/`Cif` existen en `Domain/Common` desde P2-27… y **cero entidades los usan** (grep de adopción: 0 resultados en src). Doce días después siguen siendo código muerto testeado. O se adoptan o se borran — un VO sin consumidores es peor que no tenerlo, porque miente sobre el modelo.
- Resiliencia HTTP cerrada (`AddStandardResilienceHandler` en IA/Graph), cola de IA durable en Postgres con reanudación al arrancar. Los dos hallazgos de resiliencia del informe anterior, resueltos.
- Manejo de errores: `Result<T>` disciplinado; sigue sin behavior que capture excepciones de dominio hacia un error de usuario — Sentry las captura (cuando tenga DSN) pero el usuario ve la página de error genérica.

## 3. Base de Datos — Global: **7,8/10** (antes 5,5)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Modelo de datos | 7,5 | 7 |
| Relaciones | 8,5 | 2 |
| Integridad | 8 | 3 |
| Índices | 8 | 6 |
| Escalabilidad | 6,5 | 4,5 |
| Multi-tenant | 9 | 8,5 |
| Migraciones | 8 | 6,5 |
| Riesgo de deuda técnica | 7 | 6 |
| Preparación para millones de registros | 6,5 | 4 |

- **El área más transformada.** El hallazgo más grave de la auditoría anterior (BD sin FKs de dominio) está cerrado con la solución cara y correcta: 60 `HasForeignKey` en el snapshot, compuestas `(TenantId, Id)` — el propio motor rechaza referencias cruzadas de tenant. CHECK XOR en el documento polimórfico. Índices orientados al filtro global (`TenantId` + filtro de soft delete) y `pg_trgm` para las búsquedas `Contains`.
- **Multi-tenant 9**: al filtro global (ahora por reflexión del modelo, con el bug de `Expression.Constant` cazado adversarialmente antes de mergear) se suma **RLS de PostgreSQL como segunda línea** (`TenantRlsConnectionInterceptor`, `set_config` parametrizado, fallo-cerrado). Descuento por lo único que importa: RLS está **inerte en producción** hasta rotar la credencial de runtime al rol restringido (paso manual de `RUNBOOK-RLS.md` que nadie ha ejecutado). Defensa en profundidad diseñada ≠ desplegada.
- **Millones de registros 6,5**: los índices ya no barren tenants ajenos, pero no hay particionado, no hay estrategia de archivado, y el punto de acceso (Blazor Server + semáforo por circuito) limita el throughput antes que Postgres. 68 migraciones limpias con gate de drift en CI y modo `--migrate-only` para pre-deploy — correcto.
- Deuda restante: purga RGPD sobre soft-deleted resuelta (P0-3), pero la regla vive en servicios con `IgnoreQueryFilters()` revisado a mano — un test de arquitectura que vigile nuevos usos sería barato.

## 4. APIs — Global: **6,0/10** (antes 4,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Diseño REST | 6,5 | N/A |
| Convenciones | 8 | 8 |
| Versionado | 7 | 0 |
| Consistencia | 8 | 8 |
| Contratos | 8 | 8 |
| DTOs | 8 | 8 |
| Documentación | 6,5 | 0 |
| Seguridad | 8,5 | 8 |
| Manejo de errores | 7,5 | 8 |
| Paginación | 7,5 | 7 |
| Filtrado | 6 | 7 |
| Idempotencia | 5 | 2 |

- La API pública existe: `/api/v1` con 5 recursos de lectura, OpenAPI en `/api/v1/openapi.json`, API keys por tenant con esquema de autenticación propio (bien: no hereda la cookie — un usuario con sesión no puede llamar a la API sin clave), rate limiting por tenant configurable.
- **Pero es media API**: solo lectura, 5 recursos de ~28 agregados, sin escrituras (idempotencia 5: los GET son idempotentes por naturaleza; no hay POST que evaluar), **sin webhooks salientes** — un ERP puede *leer* Hydra, no enterarse de nada ni escribir. Filtrado 6: `busqueda` + un flag por recurso, sin filtros por campo ni ordenación expuesta. Claves emitidas solo por el Administrador de plataforma, sin autoservicio, sin portal de desarrollador, no anunciada como producto.
- Veredicto: el andamiaje es de calidad (auth, versionado en ruta, límites, OpenAPI) y la decisión de montarla sobre los handlers CQRS fue la barata y correcta. Lo que falta ya no es arquitectura, es superficie — y contra Dokify/CTAIMA la integración se vende con escrituras y webhooks, no con 5 GET.

## 5. Seguridad — Global: **7,7/10** (antes 6,5)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Autenticación | 8 | 5,5 |
| Autorización | 8,5 | 8 |
| Tenant Isolation | 9 | 8 |
| Validaciones | 8 | 7 |
| Protección de datos | 7,5 | 6 |
| Gestión de secretos | 7,5 | 7 |
| OWASP | 8 | 8 |

- **Los 4 riesgos críticos del informe anterior están cerrados**: lockout + rate limiting por IP en `/cuenta/*` (con la regresión E2E del propio límite cazada y resuelta con criterio, no relajando el límite); admin hardcodeado retirado (arranque falla en producción sin `AdministradorInicial`); referencias cruzadas de tenant bloqueadas por FKs compuestas + verificación en Commands; PDFs cifrados at-rest con kill-switch de IA sobre reconocimientos médicos apagado por defecto. 2FA obligatoria para Administradores y soporte. SSO Entra ID opcional bien aislado ("inerte por defecto").
- **Riesgos medios vigentes**: (1) RLS diseñado pero inerte — la "segunda línea" es hoy una promesa; (2) el repositorio sigue **público** con exposición residual reconocida (el commit #189 sacó identificadores reales, la limpieza tiene un paso 2 pendiente) — para un SaaS que custodia datos de salud, el código de producción público es una decisión de coste (CI) que merece revisión periódica explícita; (3) pentest externo sigue sin contratar — toda la evidencia de seguridad es interna; (4) PR #190 arregló un fallo de aislamiento del buzón personal — el patrón "módulo nuevo reintroduce clase de fallo conocida" que motivó el checklist de Fase 70 sigue apareciendo, señal de que el checklist aún no es gate duro.
- **Riesgos futuros**: DPA sin firma (16 borradores listos, cero revisión de abogado) — con un tenant real dentro, tratar datos de salud como encargado sin DPA firmado no es deuda técnica, es incumplimiento; la mudanza de hosting (PR #174) mueve la frontera de seguridad física a una máquina doméstica detrás de Cloudflare Tunnel — aceptable como puente declarado, indefendible ante el primer cuestionario de seguridad de un cliente.

## 6. Frontend — Global: **7,3/10** (antes 7,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Organización | 8,5 | 8,5 |
| Arquitectura | 6 | 6 |
| Estado | 7,5 | 7,5 |
| Componentización | 8 | 7,5 |
| Reutilización | 8 | 7,5 |
| Rendimiento | 6,5 | 6,5 |
| Accesibilidad | 7 | 5 |
| Consistencia visual | 8,5 | 8,5 |
| Escalabilidad | 6,5 | 6,5 |

- Accesibilidad subió de verdad: focus trap + Escape + retorno de foco en Modal/Drawer (implementación "mejor esfuerzo" honesta), navegación móvil propia (`NavegacionMovil`) en vez del `display:none` sin alternativa. 112 tests bUnit (antes 19).
- **Lo estructural no se movió**: Blazor Server interactivo en todo, sin SSR/streaming en ninguna página, `CircuitOptions` sin configurar, debounce del buscador sigue viajando por SignalR en cada pulsación. La decisión de render sigue sin ADR. Es la misma nota de arquitectura 6 que hace 12 días y seguirá siéndolo hasta que alguien escriba el examen de capacidad o lo descarte con datos.
- Sin deep-links a entidades (P2-28 sigue 🟡): el estado vive en drawers efímeros; "pásame el enlace del trabajador X" sigue sin respuesta.

## 7. UX — Global: **7,7/10** (antes 7,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Flujo de usuario | 8,5 | 8 |
| Claridad | 9 | 9 |
| Consistencia | 8,5 | 8,5 |
| Descubribilidad | 7 | 6 |
| Carga cognitiva | 7 | 7,5 |
| Productividad | 7,5 | 4 |
| Diseño para usuarios expertos | 7,5 | 4 |
| Errores evitables | 8,5 | 8 |

- **El gap experto se cerró en lo esencial**: bulk actions en las 3 rejillas grandes, atajos j/k/x/Enter, filtros guardados por usuario, filtros persistidos en URL en las 12 listas, ordenación real por columna, deshacer al eliminar, asignaciones en lote con preflight de documentos, bandeja única priorizada del gestor (Fase 88 — la pieza de mayor valor UX de la ventana). Reserva: la verificación end-to-end en navegador de P3-31 sigue sin cerrar según el propio repo — hasta entonces es "código mergeado", no "flujo verificado", y este repo tiene esa regla por escrito.
- Carga cognitiva baja medio punto: gamificación (DDL-071) y Dashboard BPO añaden superficie visual a un producto cuyo usuario diario aún no ha validado la superficie anterior. Ninguna de las dos features tiene evidencia de demanda en el repo.
- Validación en blur: conectada solo en Centros — el resto de formularios sigue validando al enviar. Promesa de patrón a medio adoptar, igual que hace 12 días.

## 8. UI — Global: **8,2/10** (antes 7,9)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Lenguaje visual | 8,5 | 8,5 |
| Espaciado | 9 | 9 |
| Tipografía | 8 | 8 |
| Color | 8 | 7 |
| Iconografía | 8 | 8 |
| Consistencia | 8,5 | 8,5 |
| Jerarquía visual | 8 | 8 |
| Calidad profesional | 8,5 | 8,5 |

- El fallo insignia (semáforo por debajo de WCAG AA) se corrigió con los tonos `*-700` que ya existían. El sistema de gobernanza visual (8 documentos + Decision Log + validador en CI que impide que valores visuales vivan fuera de `02`/`06`) es más maduro que el de muchos equipos de diseño con plantilla. La auditoría de iconografía (#166) antes de tocar iconos es exactamente el orden correcto.
- Sin nota máxima porque la evidencia de este comité es código y documentos, no una revisión visual pantalla a pantalla del estado post-Dashboard-BPO/gamificación — y las superficies nuevas son las que menos ciclos de crítica visual han recibido.

## 9. Producto — Global: **6,6/10** (antes 6,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Definición del problema | 8,5 | 8 |
| Cobertura funcional | 7,5 | 5,5 |
| Coherencia | 9 | 9 |
| Escalabilidad del producto | 7 | 7 |
| Diferenciación | 7 | 6,5 |
| Potencial competitivo | 5,5 | 5 |
| Riesgo de funcionalidades innecesarias | 4 | 5 |

- Cobertura funcional dio un salto real: alerta de documento faltante (el hueco en la regla de negocio central, cerrado), Comunicaciones dejó de ser maqueta (ingesta Graph real por webhooks + OAuth por buzón, WhatsApp Cloud API como segundo canal, outbound con rastro y seguimiento en #190), Visitas con paquete documental automático, alta de delegaciones desde la UI (el bloqueante de go-to-market del segmento consultora, resuelto), ADR-005 Subcontratas Supervisadas alineado con el escenario Refrielectric/Arcos.
- **El riesgo de funcionalidades innecesarias empeora a 4 y es el hallazgo de producto de este snapshot**: ~20 fases nuevas en 12 días, incluyendo gamificación y un dashboard BPO de dos entregas, con **cero clientes de pago y cero evidencia nueva de mercado en el repo**. El informe anterior ya documentó a dónde lleva esto (Facturación rota semanas sin que nadie lo notara). La capacidad de ejecución es extraordinaria; el mecanismo que decide *qué* ejecutar sigue sin contrapeso externo. Cada semana de features sin cliente es inventario, no producto.
- Potencial competitivo 5,5: la distancia con Dokify/CTAIMA ya no es técnica (el aislamiento, la IA documental y la UX experta compiten); es comercial — sin API de escritura, sin certificaciones, sin un solo logo. Información insuficiente para evaluar pricing/win-rate: no hay datos de mercado nuevos desde el informe anterior.

## 10. DevOps — Global: **6,2/10** (antes 5,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| CI/CD | 8,5 | 7 |
| Docker | 8 | 7,5 |
| Configuración | 8 | 7 |
| Observabilidad | 5 | 4 |
| Logs | 7 | 6,5 |
| Monitoreo | 3 | 1 |
| Deploy | 5,5 | 5,5 |
| Gestión de ambientes | 5,5 | 5 |
| Escalabilidad operativa | 5 | 3,5 |

- **CI 8,5 — nivel profesional sin reservas**: 8 jobs con `-warnaserror`, formato, gate de migraciones, bUnit, E2E Playwright contra Postgres real, prueba de carga k6, gitleaks, Trivy sobre la imagen, Dependabot (NuGet+Docker+Actions), cobertura medida, y el gate de gobernanza documental. Docker no-root vía gosu (con el incidente de permisos de 2026-08-02 resuelto y documentado).
- **Monitoreo 3 — el enchufe sigue vacío y ya no hay excusa de fontanería**: Sentry integrado sin DSN ("no hay cuenta provisionada todavía", dice el propio código), Seq integrado sin URL, uptime check externo sin evidencia de existir. Toda la instrumentación está escrita; falta literalmente crear dos cuentas y poner dos variables. Doce días después de que esto fuera P0-5, si producción cae un viernes por la noche **sigue sin enterarse nadie**.
- **Deploy 5,5 en el peor momento posible**: el trial de Railway termina y el plan de salida (PR #174: compose local + Cloudflare Tunnel + Borg a Hetzner) está en draft, con la puesta en marcha real (dominio, túnel, Storage Box, primer backup) declarada como pasos fuera del repo aún no ejecutados. Hasta que esa mudanza esté hecha y verificada, el producto tiene fecha de desahucio y ninguna casa lista. El runbook es de buena calidad; lo que puntúa es lo ejecutado.
- `Migraciones:AlArrancar` sigue `true` por defecto; el pre-deploy `--migrate-only` existe pero no está adoptado como única vía.

## 11. Calidad — Global: **8,0/10** (antes 7,0)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Cobertura de tests | 8 | 7,5 |
| Calidad de documentación | 8,5 | 6,5 |
| ADR | 8 | 7 |
| Convenciones | 8,5 | 8 |
| Consistencia del proyecto | 8,5 | 8 |
| Facilidad para nuevos desarrolladores | 6,5 | 5 |

- **1.137 tests** (Domain 369, Integración 375 contra Postgres real, Application 261, bUnit 112, arquitectura 8, E2E 12) — 2,3× en 12 días, con la cobertura medida en CI. El punto débil sigue siendo el mismo: **12 E2E para ~30 features**; los flujos que se enseñarán en una demo de venta merecen navegador real.
- Documentación 8,5: la consolidación (un canónico por hecho, histórico en `docs/archive/`, README con quickstart, `docker-compose.yml`) más el gate de CI convierten el mayor riesgo documental del informe anterior en el sistema de gobernanza documental más serio que este comité ha visto en un proyecto de este tamaño. `AGENTS.md`/`docs/AGENT_GOVERNANCE.md` extienden esa disciplina a las sesiones de IA — adecuado para un repo que se desarrolla con agentes.
- Tests de arquitectura: 8 tests que atrapan la regresión literal del god-interface, no el antipatrón bajo otro nombre — la reserva del informe anterior sigue vigente.
- Onboarding 6,5: mejor (quickstart real), pero el repo sigue optimizado para sesiones de IA con contexto; un dev humano nuevo tarda en saber por dónde entrar.

## 12. Preparación para producción — Global: **5,6/10** (antes 4,5)

| Sub-apartado | Nota | Antes |
|---|---|---|
| Escalabilidad | 6 | 4 |
| Seguridad | 7,5 | 6 |
| Resiliencia | 6 | 4,5 |
| Observabilidad | 4,5 | 4 |
| Recuperación ante fallos | 4 | 4,5 |
| Mantenibilidad | 7,5 | 6 |
| Riesgo operativo | 4 | 4 |

- Mejoras reales: cola de IA durable (un deploy ya no pierde trabajo), health check real, smoke test post-deploy, gate de deploy con CI verde, multi-réplica construida (apagada), RLS listo (inerte).
- **Recuperación ante fallos BAJA a 4 pese a todo lo anterior, y es deliberado**: el ensayo de restauración —P0-6, señalado hace 12 días como "un backup no restaurado es una hipótesis"— **sigue sin ejecutarse jamás** (la tabla de registro de `docs/ENSAYO-RESTAURACION.md` está vacía: "_pendiente del primer ensayo real_"), los RPO/RTO siguen "propuestos, pendientes de ratificar", y ahora además el mecanismo entero de backup está a punto de cambiar (S3→Borg/Hetzner), lo que invalida parcialmente el ensayo que nunca se hizo. Es el único P0 técnico del informe anterior que sigue intacto, y empeora con la mudanza.
- Riesgo operativo 4: bus factor 1 sin cambios; hosting en transición forzada por fin de trial; monitoreo hueco. La mitad "operar el servicio" de ser un SaaS sigue siendo la asignatura pendiente — la diferencia con hace 12 días es que ya casi toda la instrumentación está escrita y solo falta *operar*.

---

# Valoración general

| Área | Nota /10 | Antes (01-08) | Nivel |
|---|---|---|---|
| Arquitectura | 7,6 | 6,5 | Bueno |
| Backend | 7,8 | 7,0 | Bueno |
| Base de Datos | 7,8 | 5,5 | Bueno |
| APIs | 6,0 | 4,0 | Aceptable |
| Seguridad | 7,7 | 6,5 | Bueno |
| Frontend | 7,3 | 7,0 | Bueno |
| UX | 7,7 | 7,0 | Bueno |
| UI | 8,2 | 7,9 | Muy bueno |
| Producto | 6,6 | 6,0 | Aceptable |
| DevOps | 6,2 | 5,0 | Aceptable |
| Calidad | 8,0 | 7,0 | Muy bueno |
| Preparación para producción | 5,6 | 4,5 | Débil |

## Las tres métricas (Actual / Potencial / Riesgo)

| Área | Actual | Potencial | Riesgo |
|---|---|---|---|
| Arquitectura | 7,6 | 9,0 | Bajo-Medio (PuertaAccesoDatos + Blazor Server sin examen) |
| Backend | 7,8 | 9,0 | Bajo |
| Base de Datos | 7,8 | 9,0 | Bajo (la deuda grave se pagó) |
| APIs | 6,0 | 8,5 | Medio (comercial: sin escrituras/webhooks no compite) |
| Seguridad | 7,7 | 9,0 | Medio (RLS inerte, repo público, sin pentest, DPA sin firma) |
| Frontend | 7,3 | 8,5 | Medio (decisión de render sin examinar) |
| UX | 7,7 | 9,0 | Bajo |
| UI | 8,2 | 9,0 | Bajo |
| Producto | 6,6 | 8,5 | **Alto** (features sin cliente al ritmo más alto del proyecto) |
| DevOps | 6,2 | 8,5 | **Alto** (hosting con fecha de caducidad, monitoreo vacío) |
| Calidad | 8,0 | 9,0 | Bajo |
| Producción | 5,6 | 8,5 | **Alto** (restauración jamás ensayada + mudanza en curso) |

Lectura de conjunto: hace 12 días el potencial 8-9 era una promesa; hoy la mitad está cobrada (BD, logging, seguridad de autenticación, UX experta). Los tres riesgos altos restantes comparten patrón: **no son código** — son operar (mudanza, ensayo, monitoreo), firmar (legal) y validar (cliente). El repositorio ya no puede resolverlos; el propietario sí.

---

# Ranking de prioridades

## P0 — Debe corregirse antes del MVP

1. **Ejecutar la mudanza de hosting completa** (cerrar y mergear PR #174, comprar dominio, levantar túnel, Storage Box, primer backup real) **antes de que expire el trial de Railway**. Es el único ítem con reloj externo: si vence antes de la mudanza, producción desaparece.
2. **Primer ensayo de restauración real, sobre el stack nuevo** (`ensayo-restauracion-borg.sh`), con fecha, duración y resultado en la tabla de `docs/ENSAYO-RESTAURACION.md`, y **ratificar RPO/RTO**. Heredado del informe anterior sin ejecutar; con la mudanza, hacerlo sobre Borg, no sobre S3.
3. **Encender el monitoreo que ya está escrito**: cuenta de Sentry + DSN, instancia/cuenta de Seq + URL, uptime check externo con alerta al móvil. Es ~2 horas de trabajo de cuentas y variables; llevar 12 días como P0 con los enchufes vacíos es la desproporción coste/riesgo más grande del proyecto.
4. **Revisión de abogado y firma del paquete legal** (DPA —declarando soporte y M365—, Términos). Bloqueante comercial absoluto: sin esto no existe contrato que un cliente pueda firmar, y con datos de salud el riesgo no es reputacional, es sancionador.

## P1 — Muy recomendable antes de vender

5. **Congelar features nuevas hasta validar con un usuario real de pago** (o un piloto formal): el mecanismo de priorización necesita una señal externa antes que otra fase. Gamificación y Dashboard BPO no deberían crecer más sin ella.
6. Pentest externo (pendiente desde P2-25; ahora con superficie API pública, más necesario).
7. Activar RLS en runtime (rotar credencial según `RUNBOOK-RLS.md`) — convertir la segunda línea de defensa de diseño en realidad.
8. Cerrar la verificación end-to-end en navegador de bulk actions/atajos/filtros guardados (P3-31, aún 🟡 por la propia regla del repo).
9. Value objects `Dni`/`Cif`: adoptarlos en las entidades o borrarlos. Código muerto en Domain es deuda que miente.
10. Decisión escrita sobre el repositorio público (completar el paso 2 de la limpieza o asumir el coste de privado) — revisar el trade-off de los ~22.000 min/mes de CI contra la exposición de código que custodia datos de salud.
11. ADR de capacidad de Blazor Server: `CircuitOptions`, límites medidos con k6 sobre circuitos (no solo HTTP), y umbral que dispara el plan multi-réplica ya construido.
12. Subir E2E de 12 a los flujos de demo/venta (alta guiada, ciclo documental completo, delegación, bandeja).

## P2 — Puede esperar unos meses

13. Rediseño de lifetimes para retirar `PuertaAccesoDatos` (la investigación cerrada de P1-11 es el punto de partida).
14. Tests de arquitectura que vigilen fronteras de módulo reales (cruces entre features), no solo la regresión literal del god-interface.
15. OpenTelemetry (trazas + métricas) sobre el logging ya correlacionado.
16. API v1: escrituras con idempotencia + webhooks salientes — cuando haya un consumidor identificado, no antes.
17. Billing/licensing: hoy no existe forma de cobrar recurrente dentro del producto; antes del segundo cliente es P1.
18. Validación en blur al resto de formularios; deep-links a entidades.

## P3 — Mejoras de excelencia

19. Camino de certificación (ENS/ISO 27001 ligero primero, SOC 2 si hay mercado internacional), status page pública, SLA formal.
20. SSO federado por tenant; portal de desarrollador de la API; feature flags por tenant.
21. Multi-región / DR activo-pasivo cuando haya ingresos que lo justifiquen.

---

# ¿Qué impediría que este SaaS fuera considerado de nivel Enterprise?

1. **Bus factor 1, sin cambios ni mitigación nueva.** Sigue siendo el techo absoluto: ninguna due diligence lo pasa. Todo lo demás de esta lista es subsanable con trabajo; esto exige estructura (partner, escrow operativo real, o adquisición de equipo).
2. **El hosting del puente**: un contenedor en una máquina doméstica detrás de Cloudflare Tunnel. Como etapa declarada y temporal es una decisión honesta y bien ejecutada; en un cuestionario de seguridad enterprise es respuesta eliminatoria. La fecha de salida del puente debería existir por escrito.
3. **Cero evidencia de terceros**: sin pentest externo, sin certificación, sin siquiera un cliente de referencia. Las tres auditorías buenas que tiene el repo son internas.
4. **Sin marco contractual firmado** — los 16 borradores no valen nada hasta que un abogado los revise y un tenant los firme.
5. **Operación sin ojos**: monitoreo sin provisionar, on-call inexistente, status page inexistente, RTO/RPO sin ratificar, restauración jamás ensayada.
6. **Sin billing**: no hay manera de cobrar, medir consumo ni gestionar suscripciones. Enterprise compra a proveedores que facturan solos.
7. **API de solo lectura sin webhooks**: la integración bidireccional con ERP/BI que exige un comprador enterprise no tiene superficie todavía.
8. **Alta disponibilidad**: multi-réplica construida pero jamás encendida; 1 proceso, 1 máquina, y durante la mudanza, 1 domicilio.

# ¿Qué aspectos están sorprendentemente bien para tratarse de un primer SaaS?

1. **La velocidad y calidad de digestión de la auditoría anterior.** ~26 de 33 ítems cerrados en 12 días, cada uno con verificación real (SQL directo para las FKs, prueba adversarial de 5 tenants para el filtro por reflexión, causa raíz de un flaky E2E en vez de timeouts). Equipos enteros tardan trimestres en digerir una auditoría así; aquí la digirió una persona sin dejar de construir producto.
2. **La gobernanza documental con enforcement en CI.** Un validador que hace fallar el build si un documento sin autoridad gobierna decisiones, con frontera de autoridad explícita, Decision Log y estados "Implementado hasta". Este comité no conoce SaaS comerciales pequeños con algo equivalente.
3. **El aislamiento multi-tenant como sistema de defensa en profundidad**: filtro global por reflexión + interceptor de sellado + FKs compuestas `(TenantId, Id)` + RLS listo en el motor + almacenamiento particionado + un test de aislamiento por agregado + tests adversariales. Con RLS activado, será mejor que el de la mayoría de SaaS B2B establecidos.
4. **La honestidad sistemática como práctica de ingeniería**: reversiones documentadas con causa raíz (P1-11), limitaciones autodocumentadas en el código ("techo 1 réplica", "inerte por defecto"), tablas de ensayo vacías que dicen "pendiente" en vez de fingir. Un auditor casi nunca puede fiarse de los comentarios de un repo; de los de este, sí — y eso vale dinero en una due diligence.
5. **CI de nivel profesional completo**: warnings como errores, drift de migraciones, Trivy, gitleaks, Dependabot, k6, E2E con navegador real contra Postgres real, cobertura medida — cada guarda motivada por un incidente concreto y no por cargo-cult.
6. **El paquete legal auto-redactado**: 16 borradores coherentes (DPA con subencargados, anexos de IA y M365, política de supresión alineada con la retención implementada) que reducen la revisión de abogado de "redactar desde cero" a "revisar y ajustar". La mayoría de primeros SaaS llegan al abogado con las manos vacías.
7. **Comunicaciones pasó de maqueta congelada a módulo real en 10 días**: OAuth por buzón, webhooks de Graph, WhatsApp Cloud API, outbound con rastro — con el fallo de aislamiento que introdujo cazado y arreglado dentro de la misma ventana.

---

# Síntesis del comité

El delta de 12 días es el mejor argumento sobre este proyecto: la deuda estructural grave del primer informe (FKs, logging, fuerza bruta, contraste, alta de delegaciones) está pagada con verificación, y tres áreas subieron más de dos puntos. Lo que queda arriba de la lista ya no se arregla con código, y por eso es más incómodo: mudarse de casa antes del desahucio del trial, ensayar una restauración de verdad, encender dos cuentas de monitoreo, poner el paquete legal delante de un abogado, y poner el producto delante de un cliente que pague. El riesgo nuevo que este comité señala sin suavizar: la máquina de construir features es tan buena que se ha convertido en el mecanismo de evasión perfecto de esas cinco tareas. Hasta que haya un tenant real pagando, cada fase nueva mejora un producto que nadie compra — y las notas de este informe ya no subirán por escribir más código.
