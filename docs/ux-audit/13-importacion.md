# Sesión 13 — Importación desde Excel (Cuadro de Control, clientes, combinada, documentos)

> Auditado el 2026-08-05: código de las 4 páginas + verificación en ejecución del control de acceso (la ejecución completa del flujo exige rol Administrador y un Cuadro de Control real; el flujo se audita sobre `Importacion.razor(.cs)` y variantes). Archivos: `Features/Importacion/Pages/Importacion.razor(.cs)`, `TablaItemsImportacion.razor`, `Features/Clientes/Pages/ImportarClientes.razor(.cs)`, `ImportarCombinado.razor(.cs)`, `Features/Documentos/Pages/ImportarDocumentos.razor(.cs)`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 7 | Dry-run obligatorio ("primero se analiza sin escribir nada"), plan con métricas de qué se creará, advertencias y omitidos **con su porqué**, confirmación explícita y resumen final — el mejor flujo de importación que puede pedirse a un MVP. |
| UI | 7 | Tarjetas de resumen con `TarjetaMetrica`, `ProgresoConMensajes` durante análisis e importación, secciones de advertencia/omitido con tono semántico. |
| Usabilidad | 6 | El copy orienta ("¿No tienes el Cuadro de Control? usa Importar clientes / combinada" con plantilla descargable); el informe de omitidos no se puede descargar para corregir el Excel fuera. |
| Consistencia | 7 | Mismo patrón analizar→revisar→confirmar en las variantes. |
| Escalabilidad | 6 | El plan renderiza todas las advertencias/omitidos en tablas sin paginar — un Excel de 5.000 filas con 800 omitidos será una página muy larga. |
| Madurez | 7 | La separación "advertencia (se importa) / omitido (no se importa)" y el resumen post-importación son señales de un flujo que ya tragó Excels reales. |
| Competitividad | 7 | La migración desde el Cuadro de Control CAE es el puente de onboarding exacto del ICP — pocos competidores digieren el Excel del cliente tal cual. |

## Hallazgos priorizados

### H1 — Acceso sin permiso acaba en la pantalla de login `[OBSERVADO]`
Con sesión iniciada como DireccionCae, navegar a `/importacion` (solo Administrador, `Importacion.razor:3`) redirige a "Iniciar sesión" — verificado en ejecución (y la sesión seguía viva: `/clientes` renderizaba). Viola el estado obligatorio Forbidden del propio producto (`UX_PATTERNS.md:98`: "mensaje claro de falta de permiso, nunca una pantalla en blanco o un 403 crudo") y hace creer al usuario que su sesión caducó. Afecta previsiblemente a toda página con `[Authorize(Roles=…)]` — hallazgo transversal, se consolida en la sesión 16.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Medio | Medio | S | Medio | Quick Win |

### H2 — El informe de omitidos no se descarga `[OBSERVADO]`
Advertencias/omitidos se muestran en pantalla (`Importacion.razor:53-63,85-95`) pero no hay "descargar informe" — el ciclo real es corregir el Excel de origen y reintentar, y para eso el usuario copia a mano.
| Medio | Bajo | S | Bajo | Quick Win |

### H3 — Sin rastro posterior de la importación `[OBSERVADO]` (a confirmar en Auditoría, sesión 14)
Tras salir de la pantalla, el resumen desaparece ("Nueva importación" resetea); si la importación de 3.000 filas creó algo mal, no hay vista "importaciones pasadas" con su detalle para deshacer o revisar.
| Medio | Medio | M | Medio | Medio plazo |

### Positivo verificado
- **Dry-run como paso obligatorio** con el copy exacto correcto ("revisa qué se va a crear, qué se omite y por qué antes de confirmar") — el patrón que evita el desastre clásico de import.
- Distinción advertencia/omitido con contadores y tono.
- Redirección honesta entre las tres puertas de importación según el punto de partida del usuario.

## Propuestas
1. **Descarga del plan y del resultado** (H2) como xlsx (una hoja por sección). — S, Quick Win.
2. **Historial de importaciones** (H3) con resumen, autor y fecha — y a futuro, deshacer lote. — M, Medio plazo.
3. **Paginar/agrupar advertencias y omitidos** cuando pasen de ~100. — S, Medio plazo.

## Referencias de principios
- **Stripe (imports/migrations)**: todo import es un objeto con historial, autor y resultado consultable después (H3).
- **Airtable CSV import**: el informe de filas rechazadas se descarga para corregir en origen (H2).
