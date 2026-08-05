# Sesión 05 — Trabajadores + Vehículos

> Auditado el 2026-08-05 en ejecución (268 Trabajadores, demo; capturas propias en tema oscuro, ancho completo y ~833px). Archivos: `Features/Trabajadores/Pages/Trabajadores.razor(.cs)`, `Components/TrabajadorWorkspacePanel.razor`, `Features/Vehiculos/Pages/Vehiculos.razor(.cs)`, `Components/VehiculoWorkspacePanel.razor`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 5 | Búsqueda por nombre/alias/DNI y triple filtro (estado/Empresa/Subcontrata) son exactamente lo que el gestor necesita, pero la ficha del trabajador **no dice dónde está asignado** — la pregunta nº 1 sobre un trabajador queda sin respuesta en su propio panel. |
| UI | 5 | Los botones Ver/Editar/Eliminar apilados en vertical inflan cada fila (~4-9 filas visibles por pantalla con 268 registros) — densidad de tarjeta, no de tabla de trabajo. |
| Usabilidad | 6 | `q` y `estado` persisten en URL y el deep-link `?accion=crear` funciona; los filtros de Empresa/Subcontrata no persisten y la página es fija a 20 filas (14 páginas para el tenant demo). |
| Consistencia | 6 | Nombre clicable y filtro peor→mejor ✓ ("Sin documentos" como opción propia, conforme a `UX_PATTERNS.md:45`); Vehículos vuelve a ser la hermana pobre (nombre no clicable, sin export). |
| Escalabilidad | 5 | Servidor pagina y ordena ✓, pero 20 filas fijas + selección por página + densidad pobre hacen que 5.000 trabajadores sean 250 páginas de scroll. |
| Madurez | 6 | Alias con detección retroactiva y lote "Asignar a centro…" (Fase 87) son features maduras; una pestaña "Próximamente" en producción no lo es. |
| Competitividad | 5 | Para consultar la documentación de un trabajador ya gana al Excel; para operarlo (dónde está, desde cuándo, qué le falta para entrar a X) todavía no. |

## Hallazgos priorizados

### H1 — El panel del Trabajador no muestra sus Asignaciones `[OBSERVADO]`
Pestañas del panel: Información / Documentación / Citas / Vehículos / Historial (`TrabajadorWorkspacePanel.razor:40-97`) — ninguna lista los Centros donde el trabajador tiene asignación activa ni permite crearla, pese a que `UX_PATTERNS.md:59` prescribe asignar "desde el detalle del Trabajador o del Centro". El Centro sí muestra sus trabajadores; el inverso no existe: para saber dónde está Mina Aino hay que ir a `/asignaciones` y filtrar.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Medio | M | Alto — el Excel responde esto con una columna | Medio plazo |

### H2 — Densidad de tabla rota por las acciones apiladas `[OBSERVADO]`
Captura ancho completo: la columna Acciones apila Ver/Editar/Eliminar en vertical (3 botones × fila), cada fila mide ~3 líneas y solo caben ~9 (o 4 en ~833px). Una tabla de operación diaria con 268-5.000 filas necesita densidad de fila única.
| Alto (uso diario) | Medio | S (menú overflow o iconos) | Medio | Quick Win |

### H3 — Pestaña "Citas" en producción con "Próximamente" `[OBSERVADO]`
`TrabajadorWorkspacePanel.razor:56-58` muestra un `EstadoVacio` "Próximamente" — contradice el principio del propio `NavMenu.razor:2-4` ("un enlace a una pantalla que todavía no existe es peor que no tener el enlace").
| Bajo | Bajo | S | Bajo | Quick Win |

### H4 — Filtros de Empresa/Subcontrata no persisten en URL `[OBSERVADO]`
Solo `q` y `estado` tienen `SupplyParameterFromQuery` (`Trabajadores.razor.cs:70-84`); un enlace compartido "trabajadores vencidos de la Empresa X" pierde la mitad del filtro.
| Medio | Bajo | S | Bajo | Quick Win |

### H5 — 20 filas por página, fijo en las 12 listas `[OBSERVADO]`
`ItemsPerPage = 20` idéntico y no configurable en las 12 páginas de lista (grep verificado). El gestor con pantalla grande y 5.000 trabajadores no puede elegir 100.
| Medio | Bajo | S | Bajo | Quick Win |

### H6 — Vehículos, sin nombre clicable ni export `[OBSERVADO]`
`Vehiculos.razor:75-78`: columnas planas sin `enlace-nombre-fila` (Trabajadores sí lo tiene) y sin export — tercera lista hermana con patrón distinto (con sesiones 02-03 ya son cinco variantes de la misma tabla).
| Bajo | Bajo | S | Bajo | Quick Win |

### Positivo verificado
- Búsqueda que incluye **alias** (con `AsignarAliasTrabajadorCommand` y detección retroactiva) — resuelve el problema real de nombres OCR distintos al legal.
- Filtro de estado con "Sin documentos" como opción propia, nunca disfrazado de "al día" (`UX_PATTERNS.md:45`) ✓.
- Acción de lote "Asignar a centro…" desde la selección (`Trabajadores.razor:224-231`, Fase 87) — bulk real reutilizando el mismo Command que `/asignaciones`.
- Estado documental derivado (peor documento) como columna, consistente con Empresas.

## Riesgos futuros
- La combinación H2+H5 fija un techo práctico de ~100 trabajadores manejables por sesión; el tenant demo ya lo supera ×2,7.
- Pro-Inbound (§ 4.3): `PestanaDocumentacion` con `AmbitoAplicacion.Trabajador` es reutilizable tal cual para un futuro portal del contratista — buen cimiento, nada bloqueante.

## Propuestas

1. **Pestaña "Asignaciones" en el panel del Trabajador** (H1): lista de centros activos con fechas + botón "Asignar a centro" (el Command ya existe). — M, Medio plazo, la de más valor.
2. **Overflow menu (⋯) o iconos en Acciones** para restaurar la fila de una línea (H2), transversal a las 12 listas. — S, Quick Win.
3. **Quitar la pestaña Citas** hasta que exista la feature (H3). — S, Quick Win.
4. **Persistir todos los filtros en URL** (H4), completando el patrón ya definido. — S, Quick Win.
5. **Selector de tamaño de página (20/50/100)** compartido (H5). — S, Quick Win.
6. **Nivelar Vehículos** (H6): nombre clicable + export. — S, Quick Win.

## Referencias de principios
- **Linear**: densidad de una línea por fila con acciones tras hover/⋯ — la tabla es para escanear, las acciones aparecen al decidir (H2).
- **Notion**: toda relación es bidireccional — si el Centro lista trabajadores, el Trabajador lista centros (H1); una relación visible en un solo sentido obliga a memorizar el otro.
- **GitHub**: page size y filtros completos viven en la URL — cualquier vista es compartible como enlace (H4/H5).
