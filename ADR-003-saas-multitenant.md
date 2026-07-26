# ADR-003 — Hydra es un SaaS multi-tenant: implementación in-place en este repositorio

**Estado**: Decidido (2026-07-23). **Supersede a `ADR-002-single-tenant.md`** en su totalidad, incluida su cláusula de fork. Reactiva `ADR-001-multitenant.md` como guía técnica de implementación.

## Decisión

Hydra (CAE Manager) pasa a ser, como objetivo principal del producto, una **plataforma SaaS comercial multi-tenant** para la gestión de Coordinación de Actividades Empresariales, vendible a consultoras de PRL y a empresas contratistas. La implementación del aislamiento por tenant se hace **en este repositorio** (in-place), no en un fork.

La organización que hoy usa el sistema en producción pasa a ser el **tenant #1** mediante la migración por etapas descrita en `INFORME-MULTITENANT.md` § 12 (esquema aditivo → backfill → cierre con filtros e índices → partición de archivos → verificación end-to-end), con ensayo previo obligatorio sobre una copia de la base de datos real.

## Por qué se supersede ADR-002

ADR-002 (2026-07-18) pausó la vía SaaS bajo dos hipótesis: (1) que la necesidad comercial no existía todavía, y (2) que si algún día llegaba, un fork limpio protegería mejor los datos reales de producción que una migración in-place.

La hipótesis (1) ha dejado de ser válida: la estrategia SaaS es ahora el objetivo principal del producto (decisión de producto, 2026-07-23, escenarios de negocio documentados en `docs/MULTITENANCY.md`). Con (1) caída, (2) cambia de signo: mantener dos códigos base vivos (el interno y el fork SaaS) durante años es un coste de mantenimiento y un riesgo de divergencia mayor que una migración por etapas bien ensayada — especialmente para un equipo de este tamaño. El riesgo que la cláusula de fork protegía (migración estructural sobre datos personales reales, incluida categoría de salud) no se ignora: se mitiga con el plan por etapas, el ensayo sobre copia y la ampliación de `MigracionesTests`, y queda como condición explícita de la Fase de implementación.

## Qué se conserva de los ADR anteriores

- **De `ADR-001`** (íntegro, vuelve a ser la guía técnica vigente): modelo `TenantId` por fila + Global Query Filter (no BD por tenant); interceptor de sellado en escritura; los 7 índices únicos que pasan a compuestos `(TenantId, campo)`; y la regla de que **no hay self-signup ni facturación hasta que el aislamiento esté implementado y auditado**.
- **De `ADR-002`** (aunque quede superseded): todo su § 4 — las obligaciones RGPD/LOPDGDD que no dependen del modelo comercial (retención, derecho al olvido, DPIA, RAT, DPA con subencargados, auditoría de lectura, cifrado) siguen vigentes sin rebaja. Además, al volverse SaaS se **reactiva** la mitad "DPA con clientes externos / Términos de Uso" del Issue #13 que ADR-002 había marcado como no aplicable.
- El checklist de ADR-002 § 5 sobre qué no llevarse a un fork (datos reales, claves de cifrado, auditoría) se reinterpreta: no hay fork, pero el principio equivalente aplica al aprovisionamiento de tenants nuevos — un tenant nuevo nace vacío, jamás ve datos del tenant #1.

## Documentación de esta decisión

- `docs/MULTITENANCY.md` — filosofía del tenant, escenarios, reglas de aislamiento, clasificación de catálogos (global vs. por tenant) y Tenant Resolution Strategy. Es el documento normativo de multi-tenancy.
- `INFORME-MULTITENANT.md` — el análisis técnico completo que fundamenta esta decisión (validación del modelo, riesgos, estrategia de migración/índices/consultas).
- `PROJECT.md`, `CLAUDE.md`, `ROADMAP.md`, `ARCHITECTURE.md`, `DATABASE.md`, `DOMAIN.md` — actualizados en la misma fase de consolidación que este ADR, para que ninguna sesión futura trabaje contra esta decisión.

## Secuencia acordada (no saltarse fases)

1. ✅ Consolidación documental (este ADR + documentos listados arriba).
2. ⬜ Aprobación de la documentación por el propietario del producto.
3. ⬜ Plan de migración detallado (etapas de `INFORME-MULTITENANT.md` § 12 desarrolladas a nivel de ejecución).
4. ⬜ Implementación técnica del multitenancy.
5. ⬜ Validación (tests de aislamiento por agregado + verificación end-to-end en navegador).

## Condiciones de salida a producción SaaS (bloqueantes, heredadas del análisis)

Aislamiento implementado y auditado con tests "tenant A no ve a tenant B" por agregado; índices únicos compuestos; almacenamiento de archivos particionado por tenant; migración a PostgreSQL (SQLite no sostiene escritura concurrente multi-organización); DPA y Términos de Uso por tenant (con revisión legal, nunca implementación unilateral — regla de `CLAUDE.md`); sin self-signup ni billing antes de todo lo anterior (regla de `ADR-001`). El compromiso comercial de propiedad y portabilidad de datos que fundamenta el DPA vive en `docs/business/DATA_OWNERSHIP.md` (**TODO**: desarrollar antes de redactar el DPA).
