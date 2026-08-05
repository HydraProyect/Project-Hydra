# SECTOR_AND_TRENDS — Diferencias sectoriales y tendencias del mercado Inbound

**Tipo**: Operativo
**Estado**: Draft — investigación externa sin contrastar con clientes reales de Hydra.
**Propósito**: Documentar cómo cambia la gestión CAE entre sectores económicos y hacia dónde evoluciona el mercado, como insumo para `docs/business/ICP.md` y `docs/business/PRODUCT_STRATEGY.md`. Es la parte de este material con menos solapamiento con documentación existente de Hydra.

## Qué pertenece aquí

- Diferencias funcionales de la gestión CAE entre sectores económicos.
- Tendencias observadas en el mercado español de plataformas Inbound.

## Qué NO pertenece aquí

- A qué sectores se dirige Hydra o en qué orden → decisión pendiente en `docs/business/ICP.md`.
- Roadmap de producto derivado de estas tendencias → `docs/business/ROADMAP_BUSINESS.md` / `PRODUCT_STRATEGY.md`.

## Por qué importa el sector

El proceso CAE es conceptualmente el mismo en cualquier sector (empresas, trabajadores, documentos, requisitos, validaciones, incidencias, cumplimiento — el mismo núcleo que ya modela `DOMAIN.md`). Lo que cambia es el **volumen y tipo de requisitos documentales**, los riesgos predominantes, la complejidad organizativa y la frecuencia de renovación. La investigación original concluye que esto no exige un dominio distinto por sector — se resuelve mediante configuración (catálogo documental, requisitos, actividades, centros), no mediante modelos paralelos. Esto es coherente con cómo Hydra ya trata `TipoDocumento` y `RequisitoDocumental` como catálogo configurable por tenant (`docs/MULTITENANCY.md` § 7), así que no aporta una decisión nueva — sí confirma que el enfoque actual es el correcto para escalar a más sectores.

## Comparativa sectorial

| Sector | Complejidad documental | Complejidad operativa |
|---|---|---|
| Construcción | Muy Alta | Muy Alta |
| Industria | Muy Alta | Muy Alta |
| Energía | Muy Alta | Muy Alta |
| Petroquímica | Muy Alta | Muy Alta |
| Logística | Alta | Alta |
| Transporte | Alta | Media |
| Alimentación | Alta | Media |
| Sanidad | Alta | Alta |
| Retail | Media | Baja |
| Servicios | Media | Baja |
| Oficinas | Baja | Baja |

## Particularidades por sector

- **Construcción**: mayor madurez en procesos CAE del mercado. Alto número de contratistas, elevada rotación, obras temporales, subcontratación en múltiples niveles. Documentación frecuente: formación PRL, REA, aptitud médica, seguros, maquinaria, vehículos, permisos específicos. La documentación depende tanto del centro de trabajo como de la obra concreta.
- **Industria**: procesos altamente controlados; gran importancia de maquinaria, procedimientos, permisos de trabajo y riesgos específicos.
- **Energía** (eléctrico, renovables, distribución, generación): elevado número de empresas colaboradoras, gran dependencia de trabajos planificados, uso frecuente de permisos especiales.
- **Petroquímica**: el sector con mayor nivel de control — accesos restringidos, permisos complejos, validaciones exhaustivas.
- **Logística**: gran volumen de transportistas, vehículos y empresas externas; gestión centrada en empresas, conductores y vehículos.
- **Transporte**: requisitos predominantes sobre conductores, vehículos, licencias y seguros.
- **Alimentación**: se añaden requisitos de higiene, seguridad alimentaria y procedimientos internos.
- **Sanidad**: elevado control documental — formación, vacunaciones, protocolos, accesos restringidos.
- **Retail**: procesos más sencillos, centrados en empresas de mantenimiento, limpieza, seguridad e instalaciones.
- **Servicios** (consultoría, limpieza, jardinería, seguridad privada, mantenimiento): documentación menos compleja que en sectores industriales.
- **Oficinas**: nivel más simple — documentación corporativa, formación básica, seguros.

## Variables que cambian entre sectores

| Variable | Observación |
|---|---|
| Número de requisitos | Construcción e industria requieren mayor volumen documental |
| Frecuencia de renovación | Mayor en sectores industriales |
| Gestión de maquinaria | Prácticamente inexistente en oficinas; muy relevante en construcción e industria — **Hydra no modela `Maquinaria` hoy** (ver `INBOUND_DOMAIN_GLOSSARY.md`) |
| Gestión de vehículos | Especialmente importante en transporte, logística, construcción — ya cubierto por `Vehiculo` en `DOMAIN.md` |
| Gestión de actividades | Algunos sectores requieren autorización específica por intervención — **Hydra no modela `Actividad` hoy** |
| Gestión de permisos | Muy relevante en industria, energía, petroquímica |

## Implicaciones para Hydra (según el material original)

- El dominio funcional puede seguir siendo único para todos los sectores; las diferencias se resuelven mediante configuración, no mediante modelos independientes.
- Ningún tipo documental debería tratarse como exclusivo de un único sector en el diseño del catálogo documental.
- Los flujos podrán especializarse (por ejemplo, validación por actividad en industria) sin alterar el modelo de dominio base — si `Actividad` llegara a construirse, debería hacerlo como extensión opcional, no como cambio estructural.

## Tendencias del mercado Inbound observadas

Resumen condensado — la investigación original numera doce tendencias; se agrupan aquí por relevancia para Hydra.

| Tendencia | Observación | Impacto para Hydra |
|---|---|---|
| Consolidación del mercado | Adquisiciones y fusiones crean grupos empresariales especializados (confirmado en la práctica: Once For All = Nalanda + Dokify; CTAIMA Group = Twind, ver `docs/business/COMPETITOR_ANALYSIS.md`) | Mantener independencia de proveedor concreto sigue siendo la apuesta correcta de `ARQUITECTURA-INTEGRACIONES.md` § 1 |
| Configuración frente a desarrollo | Las plataformas nuevas sustituyen desarrollo a medida por motores configurables | Coherente con el enfoque ya adoptado por Hydra (`TipoDocumento`/`RequisitoDocumental` configurables por tenant) |
| Incremento de automatización | Recordatorios, validaciones básicas, notificaciones, gestión de caducidades | Ya parcialmente cubierto (`Alerta`, `CalculadoraEstadoDocumento` en `DOMAIN.md`) |
| Mayor uso de IA | Clasificación documental, extracción de información, asistencia, búsqueda — todavía no sustituye la validación humana | Coherente con el patrón "sugerencia, nunca automática" ya aplicado en Hydra (`DeteccionTrabajador`, `SugerenciaGestionCorreo`) |
| Crecimiento del modelo SaaS | Nuevas implantaciones mayoritariamente SaaS | Alineado con `ADR-003-saas-multitenant.md` |
| Incremento de integraciones | Demanda de conectividad con ERP, RRHH, control de accesos, HSE | Confirma la apuesta de `ARQUITECTURA-INTEGRACIONES.md` |
| Mayor importancia del dato | Indicadores, cuadros de mando, auditorías, métricas de cumplimiento — el documento deja de ser el único objetivo | Pista para `docs/business/PRODUCT_STRATEGY.md`, sin desarrollo hoy |
| Simplificación de UX | Interfaces más limpias, menos clics, más asistentes | Sin acción directa; referencia para `UX_PATTERNS.md` si se revisa |
| Movilidad | Crece el uso móvil para consulta, aprobaciones e incidencias; la gestión documental completa sigue siendo de escritorio | Sin decisión pendiente |
| Mayor especialización sectorial | Aparecen soluciones dedicadas a energía, construcción, industria pesada | Insumo para priorizar sector objetivo en `ICP.md` |
| Crecimiento del modelo multiempresa | Más empresas colaboradoras por organización → más necesidad de escalabilidad y automatización | Ya es el modelo N:N Cliente↔Empresa de `DOMAIN.md` |
| Orientación al ecosistema | Las plataformas evolucionan de aplicaciones aisladas a ecosistemas conectados | Refuerza el ángulo de agregación multi-plataforma de `MARKET_GAPS_AND_POSITIONING.md` |

### Tendencias con mayor incertidumbre (según la investigación original)

- Validación completamente automática: la revisión humana sigue siendo predominante en el mercado.
- Reutilización documental entre plataformas: no existe estándar ampliamente adoptado.
- Normalización del dominio: no se observa convergencia entre proveedores.
- Interoperabilidad completa: las integraciones siguen siendo heterogéneas (confirma el enfoque de conectores desacoplados de `ARQUITECTURA-INTEGRACIONES.md`, en vez de esperar un estándar de mercado que no existe).

## Documentos relacionados

- `MARKET_CATALOG.md` — plataformas por segmento.
- `MARKET_GAPS_AND_POSITIONING.md` — oportunidades derivadas de estas tendencias.
- `docs/business/ICP.md` — a qué sectores se dirige Hydra (decisión pendiente, este documento es insumo).
- `DOMAIN.md` — confirma qué entidades existen hoy (`Cliente`, `Empresa`, `Trabajador`, `Vehiculo`...) y cuáles no (`Maquinaria`, `Actividad`).
