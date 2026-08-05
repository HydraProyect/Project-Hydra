# Sesión 10 — Bandeja + Alertas + Calendario

> Auditado el 2026-08-05 en ejecución (cientos de items reales en Bandeja/Alertas; panel de día del Calendario verificado con click; capturas propias). Archivos: `Features/Bandeja/Pages/Bandeja.razor(.cs)`, `Components/PanelResolverItem.razor`, `Features/Alertas/Pages/Alertas.razor(.cs)`, `Features/Calendario/Pages/Calendario.razor(.cs)`, patrón en `UX_PATTERNS.md:64-65`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | La cola única priorizada con una sola acción por tarjeta es el concepto correcto (y raro de ver bien hecho), pero sin límites ni agrupación se convierte en un muro de cientos de tarjetas que diluye su propia priorización. |
| UI | 5 | Tarjetas y tabla correctas; el Calendario tiene celdas de relleno en blanco puro sobre tema oscuro (captura) y badges de tres colores sin leyenda. |
| Usabilidad | 6 | "Gestionar" en un click desde Bandeja/Alertas/Calendario, deep-links `?estado=` desde el Dashboard ✓; el filtro por tipo no dice cuántos hay de cada. |
| Consistencia | 7 | Bandeja, su miniatura en Alertas, el preflight de Asignaciones y "Pedir prioridad" beben todos de `IDocumentosFaltantesService` — una sola verdad de faltantes, ejemplar. |
| Escalabilidad | 4 | La Bandeja carga y renderiza **todos** los items sin paginación ni tope (`Bandeja.razor.cs:55`, sin `Take` en `Application/Bandeja`) — con 5.000 trabajadores serán miles de tarjetas en un render. |
| Madurez | 6 | Prioridad por tipo bien pensada (sorpresa > faltas > vencidos…) y navegación j/k/Enter; faltan agregación y "hecho por hoy". |
| Competitividad | 6 | "Un solo sitio que decide qué atender primero" es el diferencial más vendible frente a Excel + portales; hoy su forma no sobrevive al volumen que lo haría valioso. |

## Hallazgos priorizados

### H1 — Bandeja sin tope, sin paginación y sin agregación `[OBSERVADO]`
`ObtenerBandejaGestorQuery` devuelve todo y la página lo pinta entero (`Bandeja.razor.cs:14-23,55`); en demo ya son cientos de tarjetas Vencido. Además cada documento vencido es una tarjeta individual: 40 vencidos del mismo trabajador son 40 tarjetas, no una ("Correcaminos: 5 documentos vencidos").
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto | M | Alto — la feature estrella muere de éxito | Medio plazo |

### H2 — Filtro por tipo sin contadores `[OBSERVADO]`
Los chips de tipo (Visita sorpresa / Falta / Vencido / …) no muestran cuántos hay de cada — la decisión "¿qué atiendo primero?" pide los números antes de filtrar.
| Medio | Bajo | S | Bajo | Quick Win |

### H3 — Calendario: celdas vacías blancas en tema oscuro y badges sin leyenda `[OBSERVADO]`
Captura 2026-08-05: los huecos previos al día 1 y posteriores al 31 se pintan blanco puro sobre fondo oscuro; los badges rojo/ámbar/gris no tienen leyenda en pantalla (¿vencidos/urgentes/visitas?).
| Medio | Bajo | S | Bajo | Quick Win |

### H4 — Sin puente al calendario del gestor (ICS) `[OBSERVADO]` (ausencia, sin decisión registrada)
El gestor Outbound vive en Outlook/M365 (el producto ya se conecta a M365 para buzones); los vencimientos y visitas no se pueden suscribir como calendario externo ni exportar.
| Medio | Medio | M | Medio | Medio plazo |

### Positivo verificado
- **Click en día → panel con los vencimientos de esa fecha y "Gestionar" por fila** (verificado 07/08/2026) — del mapa mensual a la acción en dos clicks.
- **Miniatura de la Bandeja en `/alertas`** (5 filas + "Ver la bandeja completa →") exactamente como la documenta `UX_PATTERNS.md:65`.
- Deep-links `?estado=Vencido|Urgente|Proximo` desde los KPI del Dashboard aterrizando con el filtro aplicado.

## Riesgos futuros
- H1 es el riesgo de escala más visible del producto: la pantalla que define el valor diario es la única sin paginar.
- Pro-Inbound: la Bandeja tipada es el esqueleto perfecto para futuros tipos Inbound (validaciones pendientes de titular); mantener el enum de tipos abierto.

## Propuestas

1. **Agregar + paginar la Bandeja** (H1): agrupar por trabajador/empresa ("5 vencidos de X" → expandir), cargar por bloques, y tope visual con "N más". — M, Medio plazo, la de más valor.
2. **Contadores por tipo en los chips** (H2). — S, Quick Win.
3. **Tema y leyenda del Calendario** (H3): tokens oscuros para celdas de relleno + leyenda de colores. — S, Quick Win.
4. **Feed ICS de vencimientos/visitas por gestor** (H4): suscribible desde Outlook — colaboración sin abrir Hydra. — M, Medio plazo.

## Referencias de principios
- **Linear (Triage)**: la cola de entrada agrupa, cuenta y se vacía — una cola sin números ni agrupación no es triage, es un feed (H1/H2).
- **GitHub notifications**: agregación por repositorio con expansión — el patrón exacto para "N vencidos de la misma empresa" (H1).
- **Google Calendar**: cualquier calendario es suscribible por URL ICS — interoperar con el calendario donde ya vive el usuario en vez de competir con él (H4).
