# Sesión 08 — Visitas + Gestiones + Incidencias + Evaluaciones

> Auditado el 2026-08-05 en ejecución (Visitas e Incidencias con datos demo; Gestiones vacía en demo — flujo auditado sobre código y `UX_PATTERNS.md:64-71`). Archivos: `Features/Visitas/Pages/Visitas.razor(.cs)`, `Features/Gestiones/Pages/Gestiones.razor(.cs)`, `Features/Incidencias/Pages/Incidencias.razor(.cs)`, `Features/Evaluaciones/Pages/Evaluaciones.razor(.cs)`.

## Puntuaciones (el rango refleja la disparidad interna: Visitas ≫ Evaluaciones)

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | Visitas es un flujo Outbound de primera (origen Plataforma, urgencia por ventana de validación, "Por gestionar", notificado al cliente); Evaluaciones es un CRUD sin contexto que no explica qué significa su propia puntuación. |
| UI | 6 | Badges semánticos correctos en Visitas/Incidencias y "—" como placeholder en Incidencias (el patrón que faltaba en sesión 03); Evaluaciones muestra "65 / 100" en texto plano sin semáforo. |
| Usabilidad | 5 | "Marcar resuelta"/"Reabrir" inline en Incidencias es el patrón correcto; Incidencias y Evaluaciones no tienen búsqueda ni filtros más allá del estado (y Evaluaciones, ninguno). |
| Consistencia | 6 | Misma familia de lista en las cuatro; pero el badge rojo "Por gestionar" no es accionable mientras el resto del producto enseña que lo rojo se clica. |
| Escalabilidad | 5 | Paginación servidor ✓; sin filtros por centro/gravedad, Incidencias y Evaluaciones se vuelven ilegibles a los 500 registros. |
| Madurez | 6 | La cadena visita→ventana de validación→gestión urgente→Bandeja (Fases F/C) es diseño de dominio maduro; Evaluaciones parece un módulo a medio adoptar. |
| Competitividad | 6 | La visita como disparador de acreditación con cuenta atrás es exactamente el job Outbound y no lo tiene el Excel; le falta cerrar el clic hacia "qué documentos faltan para esta visita". |

## Hallazgos priorizados

### H1 — "Por gestionar" no lleva a ninguna parte `[OBSERVADO]`
El badge rojo de Documentación en Visitas (`Visitas.razor:126`) es solo display: la pregunta que abre ("¿qué falta para esta visita?") se responde en otra pantalla (Bandeja/Documentos) sin enlace directo. El resto del producto (KPIs del Dashboard, faltantes de Alertas) enseña al usuario que lo rojo se clica.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Medio | S/M | Medio | Quick Win |

### H2 — Evaluaciones sin semántica visible `[OBSERVADO]`
Lista con Centro/Trabajador/Fecha/"NN / 100" (verificado en ejecución): sin semáforo, sin umbral visible de qué es riesgo alto, sin filtro ni búsqueda, y sin "Ver" (solo Editar/Eliminar). El Dashboard Ejecutivo sí interpreta la puntuación ("Centros con más riesgo") — la lista de origen no.
| Medio | Bajo | S | Bajo | Quick Win |

### H3 — Incidencias sin filtros de gravedad/centro ni búsqueda `[OBSERVADO]`
Solo filtro de estado (Sin resolver/Resuelta); con volumen real las "Muy grave" se mezclan con las "Leve" y encontrar las de un centro exige paginar.
| Medio | Medio | S | Medio | Quick Win |

### H4 — Sin export en las cuatro `[OBSERVADO]`
El registro de incidencias y evaluaciones es exactamente lo que un auditor o una titular pide en papel — transversal ya anotado (sesiones 03-07), aquí con más peso legal.
| Medio | Medio | S | Medio | Quick Win |

### Positivo verificado
- **Visitas**: filtros "Solo activas"/"Solo urgentes"/Notificado, columnas de origen (Plataforma) y trabajadores implicados; `MarcarNotificadoCliente` como acción propia — el ciclo de aviso al cliente está modelado.
- **Incidencias**: transición de estado inline con "Reabrir" (no solo resolver) y placeholder "—" correcto.
- **Gestiones**: estado vacío que explica de dónde nacen las gestiones (Bandeja/Trabajador/Centro) en vez de un "+ Nueva" incongruente con el modelo.

## Riesgos futuros
- La urgencia de visita depende de `ParametroSistema.HorasAvisoVisita/HorasCriticasVisita` globales del tenant; cuando las titulares tengan ventanas distintas por plataforma (48h Dokify vs 24h Twind), el parámetro único quedará corto — otro motivo para el catálogo de plataformas (sesión 04 H2).
- Pro-Inbound: Incidencias/Evaluaciones son las entidades que una titular Inbound querría ver de sus contratas; su modelo actual (colgadas de Centro) encaja sin cambios visibles.

## Propuestas

1. **Badge "Por gestionar" clicable** (H1) → abre los faltantes de esa visita (la query de faltantes por visita ya alimenta la Bandeja). — S/M, Quick Win, la de más valor.
2. **Semáforo y umbrales en Evaluaciones** (H2): color por tramo + filtro "solo riesgo alto", el mismo criterio que ya usa el Dashboard Ejecutivo. — S, Quick Win.
3. **Filtros de gravedad y centro + búsqueda en Incidencias** (H3). — S, Quick Win.
4. **Export en las cuatro listas** (H4), priorizando Incidencias (valor probatorio). — S, Quick Win.

## Referencias de principios
- **Linear**: todo indicador de estado es también un filtro/enlace — ver rojo y poder actuar sobre rojo son la misma feature (H1).
- **Stripe Radar**: una puntuación numérica siempre viaja con su interpretación (umbral visible y color) — un "65/100" desnudo delega el criterio en la memoria del usuario (H2).
