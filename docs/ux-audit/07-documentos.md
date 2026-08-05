# Sesión 07 — Documentos (lista, subida múltiple, Revisión IA, visor)

> Auditado el 2026-08-05 en ejecución (2.252 Documentos demo; Revisión IA sin pendientes en demo — su flujo se audita sobre código y `UX_PATTERNS.md:79-80`). `/documentos/importar` se audita en la sesión 13. Archivos: `Features/Documentos/Pages/Documentos.razor(.cs)`, `SubidaMasiva.razor(.cs)`, `RevisionIa.razor(.cs)`, `Components/DocumentoWorkspacePanel.razor`, `VisorDocumento.razor`, regla de estado en `DATABASE.md`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 7 | El ciclo documental está muy bien cerrado — crear, renovar reutilizando el mismo drawer con subida, y el deep-link que crea el documento faltante ya precargado desde Alertas/Bandeja (`?trabajadorId=&tipoDocumentoId=`) — pero faltan los filtros con los que se opera a diario. |
| UI | 6 | Tabla densa correcta con semáforo consistente; "Sin adjunto" como texto plano en vez de un estado visual accionable. |
| Usabilidad | 7 | Búsqueda por propietario/tipo, filtros guardados, ámbitos (Trabajador/Cliente/Empresa/Vehículo/Proyecto) y subida múltiple con Ctrl+V; con 2.252 filas los filtros gruesos (solo ámbito+estado) se quedan cortos. |
| Consistencia | 7 | Mismo patrón de lista, drawer y lote que el resto; el estado se calcula con la misma `CalculadoraEstadoDocumento` que el Dashboard — coherencia garantizada por construcción. |
| Escalabilidad | 6 | Paginación/orden servidor ✓; selección por página y sin export, como todas. |
| Madurez | 7 | IA con human-in-the-loop en dos superficies (subida múltiple con umbral de confianza; Revisión IA con dos acciones separadas y lote solo ≥85%) — diseño maduro y documentado. |
| Competitividad | 6 | La subida múltiple con detección automática ya es diferencial frente a Excel+carpetas; sin "estado en la plataforma destino" el ciclo Outbound sigue sin cerrarse dentro de Hydra. |

## Hallazgos priorizados

### H1 — Sin filtro por Tipo de documento ni por Empresa/Cliente en la lista `[OBSERVADO]`
La barra ofrece texto + Ámbito + Estado (`Documentos.razor:43-59`); "todos los Aptos médicos vencidos de la Empresa X" — la consulta operativa típica — solo se aproxima con búsqueda de texto libre. `TipoDocumentoId` existe como parámetro pero solo como deep-link de creación (`Documentos.razor.cs:65,100-103`), no como filtro.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Medio | S/M | Medio | Quick Win |

### H2 — Sin filtro por rango de vencimiento `[OBSERVADO]`
"¿Qué vence este mes?" no se puede responder desde la lista (no hay rango de fechas); Alertas cubre los umbrales fijos, pero la planificación a N semanas — trabajo real de renovación Outbound — no tiene vista.
| Medio | Medio | M | Medio | Medio plazo |

### H3 — Documentos sin adjunto invisibles como categoría `[OBSERVADO]`
Todas las filas demo muestran "Sin adjunto" (captura de texto); un Documento puede existir como metadato sin archivo, pero no hay filtro/columna accionable "sin archivo". Para Outbound el PDF **es** el entregable que se sube al portal: un documento vigente sin adjunto es un pendiente disfrazado de verde.
| Alto | Alto | S | Alto | Quick Win |

### H4 — Estado calculado sin decisión registrada `[OBSERVADO]` (auditoría de la decisión, regla 3 § 0)
El estado del Documento nunca se edita — se deriva de fecha de emisión + umbrales (`DATABASE.md`, `UX_PATTERNS.md:56-57`); ni `DECISION_LOG.md` ni ADRs lo registran como decisión de modelo. **Veredicto de esta auditoría**: decisión correcta para el MVP Outbound (elimina la inconsistencia clásica del Excel y la única "validación" que le compete al gestor es la revisión IA, que existe); lo que falta no es un workflow aprobar/rechazar propio, sino registrar el **veredicto de la plataforma destino** (aceptado/rechazado por Dokify/Twind) — que pertenece al hueco 4.1.3, no al Documento. Registrar la decisión en `DECISION_LOG.md` y cerrar el hallazgo.
| Bajo (documental) | Medio (evita reabrir el debate) | S | Bajo | Quick Win |

### H5 — Lote y export, mismas carencias transversales `[OBSERVADO]`
`BarraAccionesLote` solo elimina (`Documentos.razor:348`); no hay "renovar recordatorio", "descargar seleccionados (zip)" ni export — descargar N archivos para subirlos a un portal (el gesto Outbound más literal) es fila a fila.
| Alto (uso diario Outbound) | Alto | M | Alto | Medio plazo |

### Positivo verificado
- **Crear-desde-faltante**: `?trabajadorId=&tipoDocumentoId=` abre el drawer con propietario y tipo ya fijados (`Documentos.razor.cs:100-103`) — el bucle Alertas→acción cerrado en un click.
- **Renovar** reutiliza el mismo drawer con `ZonaSoltarArchivo` (`Documentos.razor:148,301`) — un solo patrón que aprender.
- **Subida múltiple**: multi-archivo + ZIP + pegar con Ctrl+V, detección IA de trabajador y tipo con autocreación solo en confianza alta (verificado en ejecución).
- **Revisión IA** con dos acciones que no se confunden y lote solo ≥85% con fecha detectada (`UX_PATTERNS.md:79-80`).
- Visor PDF integrado (`VisorDocumento.razor`) — ver sin descargar.

## Riesgos futuros
- Pro-Inbound (§ 4.3, punto "entidad Documento"): el modelo actual (estado derivado + revisión IA separada) **no bloquea** añadir después un estado de validación de titular — la revisión IA ya demuestra el patrón "aviso resoluble sin tocar el Documento". Mantener ese principio (estados adicionales como entidades satélite, no como campos editables del Documento) es la línea a vigilar.
- H3+H5 son el mismo riesgo de negocio: Hydra puede decir "todo vigente" mientras nada está realmente acreditado en el portal.

## Propuestas

1. **Filtros de Tipo de documento y Empresa/Cliente** en la lista (H1). — S/M, Quick Win, la de más uso diario.
2. **Filtro/columna "Sin archivo adjunto"** (H3) con badge propio — el pendiente Outbound más barato de exponer. — S, Quick Win.
3. **Descarga en lote (ZIP) de seleccionados** (H5): el gesto literal de "preparar el paquete para el portal". — M, Medio plazo.
4. **Rango de vencimiento** (H2) con presets ("este mes", "próximos 60 días"). — M, Medio plazo.
5. **Registrar en `DECISION_LOG.md` la decisión de estado calculado** (H4) con el matiz del veredicto-de-portal. — S, Quick Win documental.
6. **Estado-en-portal por documento** (hueco 4.1.3; depende del catálogo de plataformas, sesión 04 H2): subido/aceptado/rechazado + fecha, editable por el gestor hasta que exista conector. Es la pieza que convierte Documentos en el centro de mando Outbound. — L, Medio plazo, la de más valor estratégico.

## Referencias de principios
- **Gmail/Drive**: los adjuntos como facet filtrable ("has:attachment") — H3 es su inverso exacto.
- **Linear**: filtros componibles por cualquier propiedad de primera clase (tipo, dueño, fecha) sin jerarquía fija (H1/H2).
- **Dokify como principio**: su "checklist por requisito con semáforo por plataforma" demuestra que el usuario piensa en "qué me falta dónde", no en "qué documentos tengo" — el principio detrás de la propuesta 6.
