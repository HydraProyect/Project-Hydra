# Sesión 11 — Reportes + Facturación

> Auditado el 2026-08-05 en ejecución (exports verificados: `/reportes/documentos.xlsx` y `.pdf` responden 200 con content-type correcto, también `/clientes/exportar.xlsx`). Archivos: `Features/Reportes/Pages/Reportes.razor(.cs)`, `Features/Facturacion/Pages/Facturacion.razor(.cs)`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 4 | Reportes es un único informe fijo sin un solo filtro: el caso real de la consultora — "informe mensual de vigencia para el cliente X" — no se puede producir. |
| UI | 6 | Tabla clara con la nota honesta de que usa el mismo semáforo que Dashboard/Alertas/Calendario; pestañas de Facturación correctas. |
| Usabilidad | 5 | Exportar es un click y funciona (verificado); elegir qué exportar no existe. |
| Consistencia | 6 | El cálculo compartido de estado evita el clásico "el informe no cuadra con la pantalla" ✓; el resumen de facturación no se puede exportar mientras el informe documental sí. |
| Escalabilidad | 4 | El reporte es "todos los documentos del tenant" sin acotación — a 100k documentos será un xlsx gigante que nadie pidió entero. |
| Madurez | 5 | Facturación con tarifas por concepto y resumen mensual estimado es un buen primer paso de negocio; Reportes se quedó en prueba de concepto. |
| Competitividad | 4 | El informe al cliente es el entregable con el que una consultora justifica su cuota mensual; hoy Hydra no lo produce con nombre y apellidos de cliente. |

## Hallazgos priorizados

### H1 — Un único reporte, sin filtros ni parámetros `[OBSERVADO]`
`/reportes` muestra y exporta siempre "todos los documentos con su estado" (`Reportes.razor:14-16`); no hay filtro por Cliente, Empresa, estado ni rango — ni selección de reporte. El entregable mensual por cliente (el job de Reportes en una consultora) exige recortar el Excel a mano después de exportar.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto — es el entregable que factura | M | Alto | Medio plazo |

### H2 — El resumen de facturación no se exporta `[OBSERVADO]`
La pestaña "Resumen mensual" calcula líneas y total estimado (`Facturacion.razor:203-231`) pero no tiene Exportar — justo el documento que se adjunta a la factura o se discute con el cliente.
| Medio | Medio | S | Medio | Quick Win |

### H3 — Facturación y Reportes sin vista global `[OBSERVADO]`
Facturación es cliente-a-cliente (igual que Proyectos, sesión 09 H1); el total estimado del mes de toda la cartera solo existe como KPI suelto en el Dashboard Ejecutivo.
| Medio | Medio | M | Medio | Medio plazo |

### Positivo verificado
- Exportación a Excel y PDF **funcionando** y con el mismo cálculo de semáforo que las pantallas (nota explícita en la página — anti-inconsistencia de origen).
- Tarifas por concepto con moneda y resumen mensual con total — el modelo mínimo correcto para "cuánto te estoy ahorrando/cobrando".

## Riesgos futuros
- H1 crece con cada tenant: el reporte total será más lento y menos útil a la vez.
- Outbound (regla 4): cuando exista el estado-por-plataforma (hueco 4.1.3), el informe al cliente debería incluir "acreditado en tu plataforma sí/no" — diseñar H1 con esa columna en mente.

## Propuestas

1. **Parametrizar el reporte** (H1): Cliente (obligatorio para el entregable), Empresa opcional, estados, rango de vencimiento; el PDF con cabecera del cliente. — M, Medio plazo, la de más valor de negocio directo.
2. **Exportar el resumen de facturación** (H2) a PDF/Excel con membrete. — S, Quick Win.
3. **Vista de facturación de cartera** (H3): tabla clientes × total estimado del mes, click al detalle. — M, Medio plazo.
4. **Biblioteca de reportes** (evolución de H1): vigencia por cliente, incidencias por periodo (sesión 08 H4), asignaciones activas por centro (sesión 06 H4) — cada uno con su export. — L, Largo plazo.

## Referencias de principios
- **Stripe**: los exports son la vista filtrada actual, nunca "todo" (H1) — exportar es un verbo sobre lo que ves.
- **Notion**: un informe recurrente es una plantilla con parámetros, no una página fija — el principio detrás de la biblioteca de reportes.
