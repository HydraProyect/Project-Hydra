# Sesión 09 — Proyectos

> Auditado el 2026-08-05 en ejecución (demo sin proyectos para el cliente probado — flujo verificado sobre `Features/Proyectos/Pages/Proyectos.razor(.cs)` y el vacío en runtime).

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 5 | Master-detail correcto (proyecto → técnicos con fechas → gancho a facturación por obra), pero la pantalla obliga a elegir cliente antes de mostrar nada — no existe "todos los proyectos abiertos", que es la vista con la que arranca quien lleva varias obras. |
| UI | 6 | Misma familia visual (badges Abierto/Cerrado, tabla estándar, estados de carga/vacío por sección). |
| Usabilidad | 5 | Sin búsqueda ni filtro de estado del proyecto; la selección de cliente no persiste en URL (no compartible). |
| Consistencia | 6 | Drawer/Modal/confirmaciones conforme al patrón; el detalle vive incrustado en la página en vez del Context Workspace que usa el resto del producto. |
| Escalabilidad | 5 | La lista por cliente será corta en la práctica; el problema de escala es organizativo (N clientes × obras) y la vista global ausente. |
| Madurez | 6 | Cierre de proyecto con confirmación y fecha real, técnicos con alta/baja fechadas — el ciclo de vida está completo. |
| Competitividad | 5 | Cubre el hueco real "obra nueva ≠ mantenimiento" (bien explicado en el propio copy), pero como herramienta de seguimiento multi-obra aún no supera una hoja por cliente. |

## Hallazgos priorizados

### H1 — No hay vista global de proyectos `[OBSERVADO]`
La página exige seleccionar Cliente para ver nada (verificado en ejecución; `Proyectos.razor:55` muestra vacío por cliente). "¿Qué obras tengo abiertas y cuáles cierran este mes?" — la pregunta de cartera — no tiene pantalla.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Medio | Medio | M | Medio | Medio plazo |

### H2 — Selección de cliente no persistida en URL `[OBSERVADO]`
Sin `SupplyParameterFromQuery` para el cliente: recargar u compartir pierde el contexto — patrón que el propio producto ya cumple en Clientes/Trabajadores/Documentos.
| Bajo | Bajo | S | Bajo | Quick Win |

### H3 — Detalle incrustado en vez del Context Workspace `[OBSERVADO]`
El detalle del proyecto (información + técnicos, `Proyectos.razor:106-235`) vive en la propia página; el resto del producto abre entidades en el panel lateral con pestañas e historial. Un Proyecto no tiene historial visible ni pestaña de documentación pese a que el ámbito Proyecto existe en Documentos (sesión 07).
| Medio | Bajo | M | Bajo | Medio plazo |

### Positivo verificado
- El copy de cabecera delimita el dominio ("documentación de requerimiento para obras nuevas — distinta del acceso de mantenimiento") — orientación excelente para el usuario nuevo.
- Técnicos con alta/baja fechadas y el vacío que explica el porqué ("para poder facturarlos por proyecto") — el vínculo negocio-pantalla explícito.

## Propuestas
1. **Vista "Todos los proyectos"** con columnas Cliente/Estado/Fechas/Técnicos y filtro abierto/cerrado (H1). — M, Medio plazo.
2. **Cliente en la URL** (H2). — S, Quick Win.
3. **Pestaña Documentación del Proyecto** reutilizando `PestanaDocumentacion` (ámbito ya existe) y mover el detalle al Workspace (H3). — M, Medio plazo.

## Referencias de principios
- **Linear**: los proyectos tienen una vista global con agrupación por estado — el detalle es un nivel más, nunca la única puerta (H1).
