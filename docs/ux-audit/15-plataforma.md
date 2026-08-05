# Sesión 15 — Plataforma (Delegaciones, Retención de datos, Conexiones de integración, Claves API)

> Auditado el 2026-08-05: código de las 4 páginas + evidencia runtime de la sesión 01 (Delegated Workspace activo con cartera vacía). Archivos: `Features/Delegaciones/Pages/Delegaciones.razor(.cs)`, `Retencion/Pages/Retencion.razor(.cs)`, `Integraciones/Pages/Conexiones.razor(.cs)`, `ApiKeys/Pages/ClavesApi.razor(.cs)`; marco en ADR-004 y Fase 60.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | Los cuatro flujos respetan sus invariantes de seguridad con UI explícita (motivo+caducidad para soporte, autorización con fecha para purga, clave visible una sola vez); el hueco está en el aterrizaje del operador delegado (H1). |
| UI | 6 | Consistente; la clase `descripcion-delegaciones` reutilizada en Conexiones y Claves API delata que falta una clase genérica de descripción de página. |
| Usabilidad | 6 | Estados vacíos con causa y siguiente paso en todas ("Antes de emitir una clave hace falta una delegación de soporte… ver Delegaciones"); jerga de modelo interno en copy de administración (aceptable para su audiencia). |
| Consistencia | 7 | El principio "igual criterio que el acceso de soporte" se cita y se cumple entre pantallas — coherencia de plataforma deliberada. |
| Escalabilidad | 6 | Listas cortas por naturaleza; la actividad de soporte registrada se muestra por delegación (sin export). |
| Madurez | 7 | Retención con su invariante innegociable en UI (no hay "ejecutar" sin autorización con fecha), soporte trazado y visible, revocación bilateral — es la parte más disciplinada del producto. |
| Competitividad | 7 | Delegación reversible + acceso de soporte trazado + retención RGPD operativa es un argumento de venta enterprise que la mayoría del sector CAE no puede enseñar. |

## Hallazgos priorizados

### H1 — El alta de delegación no deja al operador con cartera: aterriza en un workspace "verde" y vacío `[OBSERVADO]`
Verificado en runtime (sesión 01 H1): un Operador Delegado con rol GestorCae y sin clientes asignados en el tenant delegante ve todos los KPI a 0 con SLA 100%. El flujo de `/delegaciones` (crear delegación + asignar operador) termina ahí — no asigna ni sugiere asignar cartera, y nada en la UI del workspace delegado explica el porqué del vacío. El primer contacto de una consultora con su cliente delegado es una pantalla que miente en verde.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto — es el flujo de venta a consultoras | M | Alto | Medio plazo |

### H2 — Actividad de soporte sin export `[OBSERVADO]`
`RegistroActividadSoporte` se muestra en un modal por delegación (`Delegaciones.razor:199-206`); el cliente que quiera el informe de "qué hizo soporte en mi cuenta" (petición RGPD/enterprise típica) no puede llevárselo.
| Medio | Medio | S | Medio | Quick Win |

### Positivo verificado
- **Retención**: ciclo detectar→autorizar con fecha→ejecutar con "Descartar la propuesta" y vacío "Nada pendiente de purgar" — la invariante legal traducida a UI sin atajos.
- **Claves API**: emisión ancladas a delegación de soporte (criterio citado en el propio copy), clave mostrada una única vez con confirmación "Ya la copié".
- **Delegaciones**: soporte nace inactivo, exige motivo y caducidad al abrir (`Delegaciones.razor:237-270`), y la actividad queda visible en la propia pantalla.
- Conexiones: desconectar buzón con confirmación destructiva correcta.

## Riesgos futuros
- H1 con ADR-004 § 12.2 (autoservicio pendiente): cuando las consultoras se den de alta solas, el aterrizaje vacío será la primera impresión del producto — resolverlo antes del autoservicio.
- Pro-Inbound: la delegación por propósito (`Gestion`/`Soporte`) es extensible a futuros propósitos sin romper el modelo — no tocar la puerta cerrada del rol global (ya vigilado por CLAUDE.md).

## Propuestas
1. **Checklist de aterrizaje de delegación** (H1): al asignar operador, paso opcional "asignar cartera" (reutiliza `ReasignarEjecutivoCliente`), y en el workspace delegado el estado "Sin cartera asignada" de la sesión 01 propuesta 1. — M, Medio plazo, la de más valor.
2. **Export de la actividad de soporte** (H2) por delegación y rango. — S, Quick Win.
3. **Clase de descripción de página genérica** en el DS (cosmético, al hacer otra cosa). — S, Quick Win.

## Referencias de principios
- **AWS IAM como principio**: crear el acceso y dotarlo de permisos útiles son un solo flujo guiado — un rol vacío recién creado siempre ofrece "attach policy" (H1).
- **Intercom/HelpScout**: el registro de accesos de soporte es exportable por el cliente — la trazabilidad vale lo que vale poder llevársela (H2).
