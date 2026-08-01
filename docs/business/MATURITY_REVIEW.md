# MATURITY_REVIEW — Informe de madurez SaaS — Hydra (CAE Manager)

**Tipo**: Informe — registro puntual de evaluación, no documento temático (análogo a `DECISION_LOG.md`: no sigue la clasificación Estratégico/Operativo ni los estados de `DOCUMENT_STANDARDS.md` § 3; es un snapshot fechado que no se reescribe).
**Fecha del snapshot**: 2026-08-01 (HEAD `977a5a7`).
**Propósito**: Servir de apoyo al seguimiento de decisiones de negocio: qué áreas del producto están maduras, cuáles bloquean el go-to-market (ver ranking P0-P3) y con qué riesgo. No es fuente de verdad de ninguna decisión — las decisiones que deriven de este informe se registran en `DECISION_LOG.md` y en los documentos temáticos correspondientes. Un informe futuro se añade como documento nuevo fechado, no editando este.

> **Comité de revisión**: Staff Software Engineer · Principal Solutions Architect · Senior Backend Engineer · Senior Frontend Engineer · Staff UX Designer · Product Manager · Security Engineer · DevOps Engineer · Database Architect · SaaS CTO.
>
> **Fecha**: 2026-08-01 · **Base**: HEAD `977a5a7`. Evaluación basada exclusivamente en la evidencia del repositorio; los apartados sin evidencia suficiente se marcan como "Información insuficiente". Comparación contra SaaS comerciales bien construidos, no contra proyectos personales.

**Nota previa**: `CLAUDE.md` afirma que la migración a PostgreSQL "sigue sin hacer". Es **falso** a fecha de este informe — el corte se ejecutó en producción el 2026-08-01 (`RUNBOOK-MIGRACION-POSTGRESQL.md`, commit `67b3b52` que retira SQLite). Esta deriva documental es en sí un hallazgo y reaparece varias veces.

---

## 1. Arquitectura — Global: **6,5/10**

| Sub-apartado | Nota |
|---|---|
| Separación de responsabilidades | 8 |
| Modularidad | 7 |
| Escalabilidad | 4 |
| Flexibilidad | 7 |
| Mantenibilidad | 8 |
| Complejidad | 7 |
| Calidad de decisiones arquitectónicas | 6 |
| Coherencia documentación ↔ implementación | 6 |

- **Bien**: Clean Architecture real, no de nombre — el csproj de Domain no referencia ningún paquete; contratos hacia dentro, implementaciones en Infrastructure. Comentarios inline que citan incidentes reales ("hallazgo N-15", "verificado con dos pestañas") — mantenibilidad excepcional.
- **Mal (escalabilidad, 4/10)**: `PuertaAccesoDatos` es un `SemaphoreSlim(1,1)` que **serializa todo acceso a datos dentro de cada circuito Blazor** — un parche al DbContext scoped compartido cuya solución canónica es `IDbContextFactory<T>`. Se eligió el workaround, no el patrón. Súmale cola de IA en memoria, archivos en disco local y `AlcanceDatosService` filtrando con listas `Contains` sin cota: el techo es 1 réplica y está auto-documentado, pero el producto se vende como SaaS multi-tenant.
- **Mal (coherencia, 6/10)**: `ARCHITECTURE.md` promete pipeline behaviors de logging y de captura de excepciones de dominio → **ninguno de los dos existe** (grep: 0 resultados). Una `ArgumentException` de entidad llega cruda a la UI.
- **Decisión sin justificar**: el `CaeManagerDbContext` acumula Identity + IUnitOfWork + cifrado + 38 filtros enumerados a mano + ~40 IQueryables duplicados. Es el archivo más grave del sistema y crece con cada agregado.
- **Sobreingeniería puntual**: 36 repositorios passthrough de 2-4 métodos idénticos; 3 clases de credenciales con 3 protectores (deuda ya reconocida).

## 2. Backend — Global: **7/10**

| Sub-apartado | Nota |
|---|---|
| Organización | 8,5 |
| Clean Architecture | 8 |
| DDD | 6 |
| CQRS | 7,5 |
| Uso de patrones | 7 |
| Calidad de código | 8,5 |
| Legibilidad | 8,5 |
| Extensibilidad | 7 |
| Testabilidad | 8 |
| Manejo de errores | 7 |
| Logging | 3,5 |
| Validaciones | 7,5 |
| Rendimiento potencial | 6 |

- **DDD (6/10)**: rico a nivel de entidad (`Documento.Renovar`, `Anonimizar` con semántica RGPD razonada, factorías con invariantes), pobre a nivel de agregado: **cero value objects** (Dni/Cif/Email son strings con un helper estático), ninguna entidad con colecciones hijas, invariantes inter-entidad viviendo en handlers. Es transaction script disciplinado con entidades defensivas — funciona, pero no es DDD táctico completo.
- **Logging (3,5/10) — el punto más débil**: **cero `ILogger` en los 73 Command handlers y 76 Query handlers**. Sin OpenTelemetry, sin métricas, sin correlación tenant/usuario. Hoy no se puede responder "¿qué comandos fallan y con qué latencia?".
- **Hallazgo grave (validaciones)**: la regla de `CLAUDE.md` "todo Command que reciba Ids ajenos los carga antes de usarlos" está **violada** en `CrearDocumentoCommandHandler` (no carga el propietario) y `CrearAsignacionCommandHandler` (no carga ni Trabajador ni Centro). Ver Seguridad.
- **Incoherencia interna directa**: `AuditoriaInterceptor` solo cubre `SavingChangesAsync` — la vía síncrona no audita, exactamente el agujero N-15 que los otros dos interceptores cierran citándolo.
- **Resiliencia HTTP inexistente**: los HttpClients de IA no tienen timeout, retry ni circuit breaker; el procesador de cola es secuencial — un documento colgado detiene la cola entera.
- **Fragilidad por convención**: `AutorizacionEscrituraBehavior` decide qué es Command por `Name.EndsWith("Command")`. Un typo desactiva la autorización en silencio.

## 3. Base de Datos — Global: **5,5/10**

| Sub-apartado | Nota |
|---|---|
| Modelo de datos | 7 |
| Relaciones | 2 |
| Integridad | 3 |
| Índices | 6 |
| Escalabilidad | 4,5 |
| Multi-tenant | 8,5 |
| Migraciones | 6,5 |
| Riesgo de deuda técnica | 6 |
| Preparación para millones de registros | 4 |

- **El hallazgo más grave de toda la auditoría: la base de datos no tiene claves foráneas de dominio.** En la migración baseline hay exactamente 8 `ForeignKey`: 6 de Identity y 2 de Correo. Centro→Cliente, Trabajador→Empresa, Documento→propietario, Asignación→Trabajador/Centro y ~30 relaciones más son **Guids sueltos sin constraint**. La integridad descansa al 100% en una convención de revisión de código — que además ya está violada (ver arriba). No existe ningún ADR que justifique esta ausencia. Ningún SaaS bien construido renuncia a FKs sin documentarlo.
- **Agujero legal detectado**: el filtro global `!EstaEliminado` hace que las queries de purga RGPD **nunca alcancen las filas soft-deleted** — y una baja de trabajador es soft delete. Resultado: los datos personales de trabajadores eliminados son invisibles para el ciclo de retención y persisten indefinidamente. Dos mecanismos correctos por separado, incumpliendo juntos la invariante de `RGPD-TRATAMIENTO-DATOS.md` § 5.
- **Multi-tenant (8,5/10) — lo mejor**: 38/38 entidades con `HasQueryFilter` centralizado, interceptor de sellado que también rechaza Modified/Deleted ajenos y cubre la vía síncrona, cero `IgnoreQueryFilters`/`FromSqlRaw` en src (verificado), unicidades compuestas con `TenantId` primero, 39+ tests de aislamiento (uno por agregado). Debilidades: `AmbitoTenantExplicito` es un static público sin gobernanza (cualquier código puede saltarse el aislamiento), y no hay RLS de PostgreSQL como segunda línea.
- **Índices**: los únicos son correctos (compuestos con TenantId), pero los **no únicos de tablas calientes no llevan TenantId ni filtro de soft delete** — `Documentos(FechaVencimiento)`, la query central del producto, barrerá entradas de todos los tenants. 32 búsquedas `ToUpper().Contains` sin trigram → seq scan garantizado a escala.
- **Documento polimórfico** (5 FKs nullable) sin CHECK constraint XOR: la BD acepta un documento con dos propietarios o ninguno.

## 4. APIs — Global: **4/10**

| Sub-apartado | Nota |
|---|---|
| Diseño REST | N/A — **no existe API pública** |
| Convenciones | 8 |
| Versionado | 0 |
| Consistencia | 8 |
| Contratos | 8 |
| DTOs | 8 |
| Documentación (OpenAPI) | 0 |
| Seguridad de endpoints | 8 |
| Manejo de errores | 8 |
| Paginación | 7 |
| Filtrado | 7 |
| Idempotencia | 2 |

- Es Blazor Server puro: toda la lógica viaja por SignalR y MediatR. La superficie HTTP son 8 minimal endpoints auxiliares (descargas, exports, identidad, salud), bien protegidos por `FallbackPolicy` global.
- Lo interno es bueno (7,5/10): `Result<T>` con códigos, DTOs por Query, `ResultadoPaginado<T>`, autorización en el pipeline y no en la UI. **Lo que un comprador SaaS llamaría "API" es 0/10**: sin ella no hay integración con ERPs del cliente, ni conectores, ni automatización — y los competidores (Dokify, CTAIMA) sí la ofrecen.
- El punto a favor: los handlers CQRS **ya son la API** — falta el transporte, tokens y OpenAPI, no el rediseño.
- Defecto puntual: el export usa `TamanoPagina: int.MaxValue` + `MemoryStream` — vector de presión de memoria a bajo coste para un usuario autenticado.

## 5. Seguridad — Global: **6,5/10**

| Sub-apartado | Nota |
|---|---|
| Autenticación | 5,5 |
| Autorización | 8 |
| Tenant Isolation | 8 |
| Validaciones | 7 |
| Protección de datos | 6 |
| Gestión de secretos | 7 |
| OWASP | 8 |
| Rate limiting | 0 |
| Auditoría de acceso | 7,5 |

**Riesgos críticos (explotables hoy):**

1. **Fuerza bruta sin fricción**: `Login.razor:92` — `lockoutOnFailure: false` + cero rate limiting en toda la aplicación + logins fallidos sin auditar. Un ataque de credenciales es invisible.
2. **Credenciales de administrador hardcodeadas y públicas en el repo**: `IdentitySeeder.cs:22-23` (`admin@caemanager.local` / contraseña en el código), usadas si falta configuración, con `DebeCambiarContrasena=false` deliberado. Nada impide que producción arranque con ellas.
3. **Inyección de referencia cruzada de tenant** (hallazgo combinado): Commands que persisten Guids ajenos sin verificar + ausencia total de FKs = un Id de otro tenant **se persiste sin error**, sellado con el tenant actual. Ni el filtro global ni el interceptor lo detectan, porque solo inspeccionan la entidad escrita, no sus referencias. Es la brecha real en la propiedad que el producto vende como "frontera absoluta" — por eso Tenant Isolation baja de 9 a 8 pese a ser lo mejor construido del sistema.
4. **Datos de salud (art. 9 RGPD) en claro at-rest**: el cifrado por Data Protection cubre las credenciales, no los PDFs de reconocimientos médicos (`App_Data/documentos`). Y se envían a Anthropic/Mistral/Gemini **sin DPA de subencargado** (Issue #13 abierto, reconocido por el propio repo).

**Riesgos medios**: open redirect post-login vía `ReturnUrl` sin validar (`Login.razor:117`); 2FA existente pero no exigible ni a Administradores ni a soporte cross-tenant; archivos validados solo por extensión (sin magic bytes) directos a PdfSharp/LibreOffice; purga RGPD ciega a soft-deleted (ver BD).

**Lo que está bien de verdad**: la cadena de resolución de tenant es fallo-cerrado con token Data Protection ligado a usuario y TTL (fix del secuestro C-1, con tests adversariales de regresión); rol efectivo consultado contra BD en cada petición, no contra el claim; headers de seguridad completos con CSP sin `unsafe-inline` en scripts; cero SQL crudo; sanitización HTML del correo entrante; KMS para claves.

## 6. Frontend — Global: **7/10**

| Sub-apartado | Nota |
|---|---|
| Organización | 8,5 |
| Arquitectura (render mode) | 6 |
| Estado | 7,5 |
| Componentización | 7,5 |
| Reutilización | 7,5 |
| Rendimiento | 6,5 |
| Accesibilidad | 5 |
| Consistencia visual | 8,5 |
| Escalabilidad | 6,5 |

- **Blazor Server interactivo en todo, sin examen escrito**: cada usuario es un circuito en RAM + WebSocket; sin `CircuitOptions` configuradas, sin SSR/streaming en ninguna página. Es la decisión de mayor riesgo estructural del frontend frente a la ambición SaaS de ADR-003 y **no hay ADR que la examine**.
- **Accesibilidad, la contradicción**: `Pestanas.razor` implementa el patrón ARIA Tabs completo citando la APG de W3C… y a la vez `Modal`/`Drawer` declaran `aria-modal` **sin cerrar con Escape, sin focus trap, sin devolver el foco**. Y a <1024px el sidebar hace `display:none` **sin alternativa**: la app queda sin navegación en tablet/móvil pese a que `DESIGN_SYSTEM.md` promete un drawer.
- Detalle fino real: paginación server-side con `GridItemsProvider`, debounce con cancelación, listener JS delegado global (lo correcto en Blazor Server), protección mousedown-drag en drawers. Pero el debounce del buscador es server-side: **cada pulsación viaja por SignalR**.
- Sin rutas de detalle: todo vive en drawers efímeros — no hay deep-link a "Trabajador X".

## 7. UX — Global: **7/10**

| Sub-apartado | Nota |
|---|---|
| Flujo de usuario | 8 |
| Claridad | 9 |
| Consistencia | 8,5 |
| Descubribilidad | 6 |
| Carga cognitiva | 7,5 |
| Productividad | 4 |
| Diseño para usuarios expertos | 4 |
| Errores evitables | 8 |

- **Claridad 9/10 — nivel Stripe, sin exagerar**: `/retencion` explica el modelo mental en un párrafo; confirmaciones con consecuencia real, nunca "¿Estás seguro?"; el DNI valida dígito de control en vivo distinguiendo DNI/NIE/CIF/TIE.
- **Errores evitables 8/10**: concurrencia optimista **visible al usuario** (el conflicto vuelve como mensaje de formulario), drawers que no se cierran con clic fuera si hay trabajo sin guardar — patrones anti-pérdida que muchos SaaS "modernos" no tienen.
- **Productividad experta 4/10 — el gap frente a Linear**: un solo atajo (⌘K), cero bulk actions (un gestor CAE que renueva 40 documentos los toca de uno en uno), sin filtros guardados, sin edición inline, y los filtros **no persisten en URL** — contradiciendo la promesa explícita de `UX_PATTERNS.md:37`.
- La validación inline llega al enviar, no al salir del campo — otra promesa del propio doc incumplida.

## 8. UI — Global: **7,9/10**

| Sub-apartado | Nota |
|---|---|
| Lenguaje visual | 8,5 |
| Espaciado | 9 |
| Tipografía | 8 |
| Color | 7 |
| Iconografía | 8 |
| Consistencia | 8,5 |
| Jerarquía visual | 8 |
| Calidad profesional | 8,5 |

- Sistema de tokens de verdad (escala de 8px nombrada, motion, modo oscuro con identidad propia y no inversión), micro-interacciones disciplinadas con `prefers-reduced-motion` respetado en todas. Desde el código, esto renderiza como un SaaS cuidado, no como un CRUD de Bootstrap.
- **Pero el fallo es en el peor sitio posible**: el semáforo verde/ámbar/rojo — el patrón visual insignia del producto según su propio design system — usa texto `*-500` sobre fondo `*-50` (≈2,2:1 el verde, ≈1,9:1 el ámbar) e **incumple WCAG AA (4,5:1)**, contradiciendo el "WCAG AA no negociable" de `DESIGN_SYSTEM.md:187`. Los tonos `*-700` que sí pasan **ya existen en tokens.css y no se usan**. Corregible en una tarde.

## 9. Producto — Global: **6/10**

| Sub-apartado | Nota |
|---|---|
| Definición del problema | 8 |
| Cobertura funcional | 5,5 |
| Coherencia | 9 |
| Escalabilidad del producto | 7 |
| Diferenciación | 6,5 |
| Potencial competitivo | 5 |
| Riesgo de funcionalidades innecesarias | 5 (riesgo real, disciplina parcial) |

- **Cobertura CAE (5,5/10)**: cubre bien el back-office del SPA, pero un CAE que alerta de lo que caduca y **no de lo que falta** tiene un hueco en su regla de negocio central (la exigencia documental está fuera de alcance declarado; `RequisitoDocumental` es una tabla muerta sin Commands ni UI). Sin portal de terceros, sin control de acceso físico.
- **El bloqueante de go-to-market disfrazado de decisión pendiente**: el alta de delegaciones (ADR-004 § 12.2) no existe en la UI — **el escenario que destraba al primer segmento (consultoras) no se puede aprovisionar desde el producto**; solo lo siembra un seeder de demo.
- **Evidencia dura de construir por delante del uso**: Facturación estuvo **rota en producción desde su merge** (la tabla no existía) sin que nadie lo notara — si un módulo puede estar roto semanas, nadie lo usaba. Cinco módulos entre el 26-07 y el 30-07, ninguno validado con cliente pagador. Comunicaciones es una bandeja tipo Zendesk **sin ingesta real de Graph** — una maqueta funcional cara que además reintrodujo clases de fallo ya resueltas (XSS, páginas sin `[Authorize]`).
- **El vaivén ADR-002→ADR-003**: single-tenant decidido y revertido en **cinco días**. Bien registrado, reversión bien razonada — pero como señal de gobierno de producto, una decisión estratégica mayor que dura menos de una semana es preocupante.
- Toda la capa de negocio es Draft por autodeclaración; un solo dato de mercado en todo el repo; 0 clientes de pago firmados.
- `ARQUITECTURA-INTEGRACIONES.md`: previsión sana, no YAGNI violado — 227 líneas de papel, cero código especulativo. Aprobado con reservas.

## 10. DevOps — Global: **5/10**

| Sub-apartado | Nota |
|---|---|
| CI/CD | 7 |
| Docker | 7,5 |
| Configuración | 7 |
| Observabilidad | 4 |
| Logs | 6,5 |
| Monitoreo | 1 |
| Deploy | 5,5 |
| Gestión de ambientes | 5 |
| Escalabilidad operativa | 3,5 |

- **CI notable**: 6 jobs con `-warnaserror`, gate de migraciones pendientes, tests de integración y E2E contra PostgreSQL real, gitleaks, reconstrucción de la imagen exacta de Railway (motivada por un incidente real).
- **Monitoreo 1/10**: **si producción se cae un viernes por la noche, nadie recibe nada.** Sentry integrado pero sin DSN (enchufe vacío), `/salud` devuelve `Results.Ok("ok")` incondicional (con Postgres caído responde 200), cero métricas, cero alertas, logs solo en el volumen que se incendia con el contenedor. El propio repo lo tiene inventariado (Issue #9) — punto a favor de la honestidad, en contra de la madurez.
- **Deploy**: Railway on-push desacoplado del resultado de CI (no hay gate), migraciones al arrancar la app (incompatible con N réplicas y con rollback sano), sin smoke test post-deploy. Runbooks excepcionales, eso sí.
- **La restauración de backups jamás se ha ensayado end-to-end.** Un backup no restaurado es una hipótesis.
- Docker corre como root, sin HEALTHCHECK, sin escaneo de imagen ni Dependabot.

## 11. Calidad — Global: **7/10**

| Sub-apartado | Nota |
|---|---|
| Cobertura de tests | 7,5 |
| Calidad de documentación | 6,5 |
| ADR | 7 |
| Convenciones | 8 |
| Consistencia del proyecto | 8 |
| Facilidad para nuevos desarrolladores | 5 |

- **442-503 tests** (Domain 168+, Integración 152 contra Postgres real, E2E Playwright con navegador real). El aislamiento multi-tenant está **testeado en serio**: un test por cada uno de los 38 tipos con filtro — exactamente lo que un auditor pide y casi nadie tiene. Débil: 8 E2E para 92 pantallas, 19 tests de UI para ~92 componentes, cobertura numérica desconocida (no se mide en CI).
- **Documentación: activo con síntomas de carga.** ~46 documentos con la verdad replicada en 5+ sitios; hoy mismo hay **4 contradicciones verificadas** (todas secuelas del corte a PostgreSQL: `CLAUDE.md:15`, `CODING_STANDARDS.md:49`, `DEPLOY.md:40`, `ROADMAP.md:983`). Con un solo mantenedor, la deriva es cuestión de tiempo. Los ADRs son buenos (historial de reversión honesto, decisión abierta marcada como abierta) aunque sin plantilla de opciones-descartadas.
- **Onboarding 5/10, la paradoja**: demasiado material y ningún camino corto. `README.md` tiene 2 líneas; no hay "levanta el entorno en 10 minutos" ni `docker-compose.yml`; el índice (`CLAUDE.md`) está escrito para sesiones de IA, no para humanos.

## 12. Preparación para producción — Global: **4,5/10**

| Sub-apartado | Nota |
|---|---|
| Escalabilidad | 4 |
| Seguridad (go-to-market) | 6 |
| Resiliencia | 4,5 |
| Observabilidad | 4 |
| Recuperación ante fallos | 4,5 |
| Mantenibilidad | 6 |
| Riesgo operativo | 4 |

- Una réplica, una región, un proveedor; RPO implícito de 24h sin decidir si es aceptable; RTO indefinido; restauración sin ensayar; la cola de IA pierde trabajo en cada deploy sin rastro para el usuario; un `.docx` de un tenant bloquea la conversión de todos los tenants (`SemaphoreSlim(1,1)` global); cero pruebas de carga.
- **Sin DPA ni Términos de Uso, literalmente no hay contrato que firmar hoy** — es la única condición de salida de ADR-003 que queda de verdad pendiente (la de PostgreSQL ya está cumplida).
- Bus factor = 1 sin mitigación estructural, con soporte, ventas, desarrollo y legal en la misma persona, y una garantía de implantación a 90 días ya comprometida en la oferta a GESEME.
- Patrón de proceso peligroso: la seguridad se recupera por auditoría reactiva (Fase 59 lo demostró), no se garantiza por gate preventivo.

---

# Valoración general

| Área | Nota /10 | Nivel |
|---|---|---|
| Arquitectura | 6,5 | Aceptable |
| Backend | 7,0 | Bueno |
| Base de Datos | 5,5 | Débil |
| APIs | 4,0 | Crítico |
| Seguridad | 6,5 | Aceptable |
| Frontend | 7,0 | Bueno |
| UX | 7,0 | Bueno |
| UI | 7,9 | Bueno |
| Producto | 6,0 | Aceptable |
| DevOps | 5,0 | Débil |
| Calidad | 7,0 | Bueno |
| Preparación para producción | 4,5 | Crítico |

## Las tres métricas (Actual / Potencial / Riesgo)

| Área | Actual | Potencial | Riesgo |
|---|---|---|---|
| Arquitectura | 6,5 | 8,0 | Medio |
| Backend | 7,0 | 8,0 | Medio |
| Base de Datos | 5,5 | 8,0 | **Medio-Alto** (FKs + purga RGPD) |
| APIs | 4,0 | 8,5 | Medio (comercial, no técnico) |
| Seguridad | 6,5 | 9,0 | Medio → **Alto** si entra un segundo tenant real sin cerrar los P0 |
| Frontend | 7,0 | 8,5 | Medio (Blazor Server sin plan de escala) |
| UX | 7,0 | 9,0 | Bajo-Medio |
| UI | 7,9 | 9,0 | Bajo |
| Producto | 6,0 | 8,0 | Medio |
| DevOps | 5,0 | 8,5 | **Alto** |
| Calidad | 7,0 | 8,5 | Medio (deriva documental unipersonal) |
| Producción | 4,5 | 8,0 | **Alto** |

La lectura de conjunto: **el potencial es uniformemente 8-9 porque casi ninguna deuda exige rediseño** — FKs, factory de DbContext, observabilidad, API pública, cola durable son *adiciones* sobre una base bien cortada. El estado actual baja porque la mitad operativa y comercial de "ser un SaaS" (monitoreo, DR ensayado, contrato, API, escala >1 réplica) no existe todavía.

---

# Ranking de prioridades

## P0 — Debe corregirse antes del MVP (antes de activar un tenant real)

1. **Cerrar la inyección de referencia cruzada de tenant**: verificar Ids referenciados en todos los Commands de creación/vinculación (mínimo `CrearDocumentoCommandHandler`, `CrearAsignacionCommandHandler`; auditar los 73) **y** añadir FKs reales en BD — idealmente compuestas `(TenantId, Id)` para que el propio motor rechace referencias cruzadas. Con datos reales dentro, esta migración se encarece cada semana.
2. **Login**: `lockoutOnFailure: true` + rate limiting en `/cuenta/*`; retirar el admin por defecto hardcodeado (fallar el arranque en producción si no hay `AdministradorInicial` configurado); validar `ReturnUrl` local.
3. **Purga RGPD vs soft delete**: decidir (decisión legal — requiere confirmación del propietario, según la regla de `CLAUDE.md`) si las filas `EstaEliminado` entran en detección/anonimización. Hoy los datos de trabajadores dados de baja persisten indefinidamente fuera del ciclo de retención.
4. **DPA + Términos de Uso** con revisión legal, incluyendo acceso de soporte, estructura tripartita de ADR-004 § 13 y el subencargado Anthropic para datos de salud (o desactivar la IA sobre reconocimientos médicos hasta resolverlo).
5. **Monitoreo mínimo viable** (~2 horas de trabajo): DSN de Sentry, uptime check externo con alerta, y health check real (`AddHealthChecks().AddNpgSql()` en vez del `Ok("ok")` incondicional).
6. **Ensayo de restauración completo** (dump + claves DataProtection) siguiendo `RUNBOOK-CLAVES.md`, con fecha y resultado documentados. Definir RPO/RTO.
7. **Flujo de alta de delegaciones** (decidir § 12.2 aunque sea "solo Administrador de plataforma en v1") — sin él, el segmento consultora no se puede aprovisionar.
8. **Accesibilidad del patrón insignia**: contraste del semáforo a tonos `*-700` (ya existen en tokens.css); Escape + focus trap en Modal/Drawer; navegación en <1024px.
9. **Correcciones baratas de coherencia**: sincronizar los 4 documentos desactualizados por el corte a PostgreSQL (empezando por `CLAUDE.md`); `AuditoriaInterceptor` en la vía síncrona; timeouts en los HttpClients de IA.

## P1 — Muy recomendable antes de vender

10. Observabilidad de aplicación: `LoggingBehavior` (comando, duración, tenant, usuario, resultado) + enricher de Serilog con TenantId + sink de logs en la nube.
11. Sustituir `PuertaAccesoDatos` por `IDbContextFactory<CaeManagerDbContext>` — elimina el cuello de botella por circuito y ~150 líneas de complejidad accidental.
12. Cifrado at-rest de los PDFs (el dato más sensible del sistema es el único sin cifrar).
13. 2FA obligatoria para Administradores y para activar delegaciones de Soporte; auditar eventos de autenticación.
14. Índices orientados al filtro global (`Documentos(TenantId, FechaVencimiento) WHERE NOT EstaEliminado`), CHECK XOR del propietario polimórfico, `pg_trgm` para las 32 búsquedas `Contains`.
15. Alerta de "documento faltante" por exigencia de Centro (cierra el hueco funcional CAE más importante) y UI de `RequisitoDocumental` o su retirada.
16. Validación por magic bytes de archivos subidos; `AddStandardResilienceHandler` en HTTP de IA/Graph.
17. Marker interface `ICommand` en vez de convención de nombre + test de arquitectura.
18. Filtros persistidos en URL y validación inline en blur (ambos prometidos por `UX_PATTERNS.md`).
19. Cobertura en CI; E2E de los 3 flujos diferenciadores (Delegated Workspace, retención, soporte); prueba de carga base para tener una cifra defendible.
20. Gate de proceso: checklist de seguridad obligatorio para todo módulo nuevo (la lección de Fase 59).

## P2 — Puede esperar unos meses

21. PostgreSQL Row-Level Security como segunda línea bajo el filtro de EF.
22. Cola de IA durable + archivos a S3 + migraciones separadas del arranque (los tres desbloquean multi-réplica).
23. Gate de deploy (Railway solo despliega con CI verde) + smoke test post-deploy.
24. Consolidación documental: un documento canónico por hecho, informes históricos a `docs/archive/`, README real con quickstart y `docker-compose.yml`.
25. Pentest externo; Dependabot + escaneo de imagen; `USER` no-root en Dockerfile.
26. Ingesta Graph real de Comunicaciones **o congelar el módulo y no venderlo**.
27. Unificación de las 3 clases de credenciales; value objects para Dni/Cif/Email; catálogo central de códigos de error; filtros de tenant por reflexión del modelo.
28. Deep-links a entidades; attribute splatting en el design system; límite de 3 toasts.

## P3 — Mejoras de excelencia

29. **API pública versionada** (`/api/v1` + OpenAPI + API keys por tenant + rate limiting) montada sobre los handlers existentes — prerequisito de la Plataforma de Integraciones y argumento de venta enterprise.
30. Multi-réplica completa (backplane SignalR, leader election, sticky sessions); OpenTelemetry; feature flags por tenant; Licensing/billing.
31. Bulk actions, atajos j/k, acciones en el palette, filtros guardados — el kit del usuario experto de 8h/día.
32. Fronteras de módulo reales (partir `IApplicationDbContext` por feature + tests de dependencia) — el paso honesto hacia el "kernel vs. módulos" que `docs/PLATFORM.md` ya promete.
33. Primer conector de integración cuando haya proveedor confirmado; SSO federado por tenant.

---

# ¿Qué impediría que este SaaS fuera considerado de nivel Enterprise?

1. **Proveedor unipersonal sin continuidad estructural.** Ninguna due diligence corporativa lo pasa; un escrow "si el cliente lo pide" no basta. Es el techo real, por encima de cualquier consideración técnica.
2. **Sin marco contractual**: no existen DPA ni Términos de Uso — hoy no hay nada que un cliente pueda firmar.
3. **Sin evidencia de terceros**: ni pentest externo, ni ISO 27001/ENS/SOC2. Las dos auditorías (buenas) son internas.
4. **Alta disponibilidad inexistente**: 1 réplica, 1 región, 1 proveedor, RTO indefinido, sin status page, sin SLA de servicio, sin on-call. Un redeploy corta todos los circuitos de todos los tenants.
5. **Sin API pública ni webhooks**: la integración enterprise (ERP, BI, IdP propio) no tiene superficie de contacto. El SSO está limitado al tenant corporativo del propio proveedor, no federable por cliente.
6. **Integridad de datos por convención**: una base relacional sin claves foráneas de dominio no supera una revisión técnica de compra.
7. **Observabilidad casi nula**: sin métricas, sin trazas, sin alertas, con logs en el disco del contenedor — un incidente hoy se descubre porque el cliente llama.
8. **Categoría especial de datos (salud) con deberes abiertos**: PDFs médicos en claro at-rest, enviados a proveedores de IA sin DPA de subencargado, y auditoría de lectura de datos sensibles incompleta. Pregunta segura en cualquier cuestionario de seguridad enterprise, y hoy la respuesta es mala.

# ¿Qué aspectos están sorprendentemente bien para tratarse de un primer SaaS?

1. **La ingeniería del aislamiento multi-tenant.** 38/38 entidades con filtro global, interceptor de sellado con defensa en profundidad (incluida la vía síncrona), resolución de tenant fallo-cerrado con token firmado y revalidación por petición, almacenamiento particionado con anti-path-traversal, y un test de aislamiento por agregado. Muchos SaaS con años de mercado no resisten este escrutinio. (Que aun así exista el agujero de referencias cruzadas demuestra que la frontera de verdad son los detalles — pero la base es de primera.)
2. **La cultura de auditoría adversarial.** Dos auditorías internas despiadadas con hallazgos numerados, verificación posterior de cada arreglo, y bugs cazados a mano que ningún test vio (la concurrencia optimista inerte descubierta con dos pestañas de navegador). El repo se conoce a sí mismo: su propia autoevaluación de preparación (5/10) coincide con la de este comité.
3. **Higiene de decisión.** ADRs que registran reversiones sin reescribir la historia, decisiones abiertas marcadas como abiertas (ADR-004 § 12.2), lenguaje ubicuo con colisiones resueltas, y honestidad sistemática ("hipótesis débil", "cualquier cifra sería inventada"). Esto es rarísimo a cualquier escala.
4. **RGPD por diseño, no por checklist**: retención con ciclo detectar→avisar→autorizar→ejecutar sin camino a "ejecutada" sin autorización expresa; anonimización que borra el PDF porque "el dato vive dentro del archivo"; claves envueltas con KMS; traza de navegación del acceso de soporte contra el tenant visitado — más de lo que hacen muchos SaaS maduros.
5. **La migración a PostgreSQL**: ensayada, con runbook y camino de vuelta escritos antes del corte, ejecutada destapando y arreglando dos bugs latentes que SQLite enmascaraba. Ejecución de nivel profesional.
6. **CI con guardas poco comunes**: `-warnaserror`, drift de migraciones, reconstrucción de la imagen exacta de producción, E2E contra Postgres real con navegador real — cada guarda motivada por un incidente concreto.
7. **Microcopy y patrones anti-error de la UI**: concurrencia visible al usuario, confirmaciones con consecuencia real, validación de dígito de control en vivo. Por encima de la mayoría de SaaS comerciales del sector.

---

# Síntesis del comité

Hydra tiene la disciplina interna (aislamiento, tests, decisiones documentadas, RGPD de diseño) de un producto muy por encima de un "primer SaaS", y a la vez carece de la mitad operativa y comercial que define a un SaaS: monitoreo, recuperación ensayada, contrato firmable, API, escala más allá de un proceso. Los tres hallazgos que más deberían doler porque contradicen las propias reglas del repo: una base de datos sin claves foráneas con Commands que persisten Ids sin verificar (contra la regla escrita de `CLAUDE.md`), una purga RGPD ciega a las filas soft-deleted, y un semáforo — el patrón insignia — que incumple el "WCAG AA no negociable" del propio design system. Nada de esto exige rehacer: el potencial 8-9 en casi todas las áreas es real, pero se cobra ejecutando los P0 antes de que exista un cliente que sufra su ausencia.
