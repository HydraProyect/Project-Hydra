# Sesión 06 — Asignaciones

> Auditado el 2026-08-05 en ejecución (298 Asignaciones demo; drawer de alta abierto y verificado, capturas propias). Archivos: `Features/Asignaciones/Pages/Asignaciones.razor(.cs)`, `Application/Asignaciones/Commands/*`, patrón de referencia en `UX_PATTERNS.md:59-62`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 7 | El alta N Trabajadores × M Centros con pestaña Matriz por celda y preflight documental no bloqueante es el mejor flujo del producto — nivel de patrón de referencia; la baja no recibió el mismo diseño. |
| UI | 6 | Drawer claro (fecha default hoy, Lista/Matriz), pero el `SelectorMultiple` interno pagina de 20 en 20 con sus propios Anterior/Siguiente — un control dentro de un control. |
| Usabilidad | 6 | Búsqueda por trabajador o centro, filtro de estado y cross-links de cada celda al Workspace ✓; marcar decenas de trabajadores exige paginar y clicar uno a uno. |
| Consistencia | 7 | Reutiliza el mismo `CrearAsignacionesCommand` y la misma fuente de faltantes (`IDocumentosFaltantesService`) que `/alertas`, la Bandeja y el lote de `/trabajadores` — coherencia ejemplar de plataforma. |
| Escalabilidad | 6 | Lista paginada en servidor ✓; el drawer con 5.000 trabajadores serán 250 páginas de checkboxes (H2). |
| Madurez | 7 | Preflight con top-5 + "y N más" y botón que cambia a "Asignar igualmente" (`Asignaciones.razor:165-189`) — diseño de detalle poco común en un MVP. |
| Competitividad | 6 | El alta masiva con aviso de qué documento faltará en cada combinación ya supera al Excel y a buena parte del mercado; la baja 1×1 y la falta de fecha de baja lo frenan. |

## Hallazgos priorizados

### H1 — La baja es 1×1 aunque el Command de lote existe `[OBSERVADO]`
Cada fila tiene su "Dar de baja" individual; no hay selección múltiple ni `BarraAccionesLote` en la página (grep: única checkbox es la de la Matriz, `Asignaciones.razor:151`), y `DarDeBajaAsignacionesCommand` (lote) no tiene ningún caller en `src/CaeManager.Web`. El caso real — una contrata termina la obra y hay que dar de baja 40 asignaciones de un centro — son 40 diálogos.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Medio | S/M (el Command ya existe) | Medio | Quick Win |

### H2 — Multiselect paginado sin "seleccionar todos los filtrados" `[OBSERVADO]`
Captura: "Página 1 de 14 — 268 elemento(s)" dentro del drawer; tras buscar "Construcciones McPato" no hay un solo click que marque todos los resultados — hay que marcar checkbox a checkbox, página a página.
| Medio | Medio | M | Medio | Medio plazo |

### H3 — Filtrando "De baja" no se ve cuándo fue la baja `[OBSERVADO]`
Columnas: Trabajador / Centro / Cliente / **Alta** / Estado / Acciones — `FechaBaja` existe en el modelo (es el mecanismo de baja) pero no hay columna; la vista histórica no responde su única pregunta.
| Medio | Bajo | S | Bajo | Quick Win |

### H4 — Sin export `[OBSERVADO]`
La foto "quién está asignado a qué centro hoy" — el listado que una titular o un auditor pide — no sale de la pantalla (mismo hueco que sesiones 03-05).
| Medio | Medio | S | Medio | Quick Win |

### Positivo verificado (patrón de referencia interno)
- **Matriz N×M con exclusión por celda** ("Desmarca una celda para excluir esa combinación", `Asignaciones.razor:129-153`).
- **Preflight documental no bloqueante** con top-5 faltantes y botón "Asignar igualmente" (`:165-189`) — avisa sin impedir el registro administrativo, exactamente `UX_PATTERNS.md:62`.
- Fecha de alta con default hoy; "Solo mostrar relacionados" para acotar; cross-links de cada celda al panel de su entidad.

## Riesgos futuros
- H2 escala mal justo donde el producto quiere vender (tenants grandes); el drawer es hoy el techo del caso "paran 3 cuadrillas completas el lunes".
- Pro-Inbound (§ 4.3): el preflight sobre requisitos que bloquean acceso es la semilla del control de acceso Inbound — mantener `IDocumentosFaltantesService` como servicio único (hoy se cumple).

## Propuestas

1. **Baja en lote** (H1): checkboxes + `BarraAccionesLote` con "Dar de baja seleccionadas" conectada al Command existente, y atajo "Dar de baja todas las de este centro" desde el `CentroWorkspacePanel`. — S/M, Quick Win, la de más valor.
2. **"Seleccionar los N filtrados"** en `SelectorMultiple` (H2) — mejora también Trabajadores y cualquier futuro lote. — M, Medio plazo.
3. **Columna Fecha de baja** visible al filtrar "De baja" (H3). — S, Quick Win.
4. **Export de asignaciones activas por centro/cliente** (H4). — S, Quick Win.

## Referencias de principios
- **Linear**: la operación inversa de un bulk es otro bulk — si crear en lote existe, deshacer/cerrar en lote es la misma feature, no una distinta (H1).
- **Gmail**: "select all matching" tras una búsqueda (H2).
- **Stripe**: las vistas históricas llevan su marca temporal de cierre (H3) — un registro "de baja" sin fecha es un dato a medias.
