# Sesión 01 — Dashboard, Visión de cartera y Dashboard Ejecutivo

> Auditado el 2026-08-05 **en ejecución** (servidor local, datos de demo: 268 trabajadores, 2.252 documentos, 34 centros en el tenant demo; usuario `prueba.direccioncae1`, tema oscuro). Las valoraciones visuales provienen de capturas tomadas en esa sesión → `[OBSERVADO]`. Marco de alcance: § 0 de `00-INVENTARIO.md` (vara Outbound).
>
> Archivos: `Features/Dashboard/Pages/Dashboard.razor(.cs)`, `Features/VisionCartera/Pages/VisionCartera.razor(.cs)`, `Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor(.cs)`, `Application/Dashboard/Queries/*`.

## Puntuaciones

| Eje | Nota | Justificación (una frase) |
|---|---|---|
| UX | 6 | Jerarquía crítico→atención→actividad correcta y con cross-links reales, pero el vacío engañoso del workspace delegado y la ausencia total de la pregunta operativa Outbound ("¿qué queda por subir a qué portal?") le impiden ser la pantalla donde arranca el día. |
| UI | 6 | Tarjetas, badges y semáforo consistentes con el Design System; la leyenda del donut de ApexCharts es casi ilegible en tema oscuro y el panel "Personalizar" ocupa el primer viewport completo. |
| Usabilidad | 6 | Todo está a un clic y los estados loading/empty/error existen; falta indicador de frescura/refresco y la personalización estorba el uso diario. |
| Consistencia | 7 | Componentes del DS en las tres pantallas y el mismo cálculo de estado documental que las tablas; grietas menores: navegación por nombre vs. por Id y logging de errores dispar. |
| Escalabilidad | 4 | Cada carga trae **todas** las fechas de vencimiento a memoria y la Visión de cartera itera tenants secuencialmente — funciona hoy, no a 50 clientes × 100k documentos. |
| Madurez | 6 | Escalera de visibilidad por rol, preferencia de KPIs por usuario y estados completos; sin caché, sin freshness, y un estado vacío que miente. |
| Competitividad | 5 | El semáforo agregado ya gana al Excel, pero el Gestor Outbound sigue arrancando el día dentro de los portales destino porque Hydra no le dice qué queda pendiente en cada uno. |

## Hallazgos priorizados

### H1 — Cartera vacía se muestra como "todo perfecto" (SLA 100%) `[OBSERVADO]`
Con el Delegated Workspace activo y un operador sin cartera asignada (admin de plataforma con rol efectivo GestorCae), el Dashboard muestra todos los KPI a 0 y **SLA documental 100% en verde** (captura 2026-08-05; causa: `ObtenerKpisDashboardQuery.cs:80` — `totalConVigencia == 0 ? 100`, con el alcance vacío de `IAlcanceDatosService`). Un dashboard sin datos visibles es indistinguible de un cliente impecable.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo de no hacerlo | Horizonte |
|---|---|---|---|---|
| Alto | Alto | S | Alto — decisiones sobre un "verde" falso | Quick Win |

### H2 — El Dashboard no responde la pregunta Outbound del día `[OBSERVADO]` (ausencia, sin decisión registrada)
Ningún KPI ni tarjeta menciona plataformas destino: el flujo diario real (regla 4 del marco) es "qué falta subir/renovar en Dokify/Twind/Nalanda por cliente", y esa vista no existe (`Dashboard.razor:28-77` cubre semáforo interno y actividad). Depende del hueco 4.1.3 del inventario (no hay registro del estado en plataforma destino).
| Alto | Alto | L (requiere modelo) | Alto — el producto no captura su job central | Medio plazo |

### H3 — KPIs calculados trayendo todas las fechas a memoria `[OBSERVADO]`
`ObtenerKpisDashboardQuery.cs:65-73` materializa la lista completa de `FechaVencimiento` y clasifica en memoria en cada carga de Dashboard; coherencia garantizada (misma `CalculadoraEstadoDocumento` que las tablas — bien), coste O(documentos) por vista y sin caché.
| Medio (latencia creciente) | Medio | M | Medio — degradación silenciosa con volumen | Medio plazo |

### H4 — Visión de cartera: N+1 por tenant, secuencial y sin límite de filas `[OBSERVADO]`
`ObtenerKpisGlobalesQuery.cs:47-54` ejecuta el KPI completo por cada cliente autorizado en un `foreach` secuencial (cada uno con su O(documentos) de H3), y `:63-68` lista todos los clientes sin `Take` — con 50 delegaciones la página se vuelve inviable.
| Medio hoy / Alto a escala | Alto (es la pantalla de venta a consultoras) | M | Alto | Medio plazo |

### H5 — Leyenda del donut ilegible en tema oscuro `[OBSERVADO]`
Captura 2026-08-05: las etiquetas Vigente/Próximo/Urgente/Vencido de la leyenda ApexCharts se renderizan en gris de bajo contraste sobre fondo oscuro (colores por defecto de la librería, no tokens del DS) — `DashboardEjecutivo.razor:78-83` no pasa opciones de tema. `DESIGN_SYSTEM.md` declara WCAG AA "no negociable".
| Medio | Bajo | S | Medio — accesibilidad y percepción de acabado | Quick Win |

### H6 — "Personalizar" permanentemente desplegado antes del contenido `[OBSERVADO]`
`DashboardEjecutivo.razor:15-34`: el panel de checkboxes (acción de frecuencia mensual) precede a los KPI (uso diario) y no es colapsable — en la captura ocupa todo el primer viewport. `SeccionColapsable` ya existe en el DS.
| Medio | Bajo | S | Bajo | Quick Win |

### H7 — Navegación por nombre en vez de por Id `[OBSERVADO]`
`Dashboard.razor.cs:44-46`: "Centros con más riesgo"/"Empresas con más riesgo" navegan con `?q=<nombre>` (búsqueda por texto) mientras que los documentos navegan por Id (`:42`); dos centros homónimos o un rename rompen el enlace.
| Bajo | Bajo | S | Bajo | Quick Win |

### H8 — Microcopy "Delegated Workspace" expuesto al usuario `[OBSERVADO]`
Captura del vacío de Visión de cartera: "Cuando tengas al menos un **Delegated Workspace** activo…" (`VisionCartera.razor:28`) — término interno de negocio en inglés, cuando el propio selector se llama "Cliente activo" (`SelectorClienteActivo.razor:26`) y ADR-004 prohíbe "tenant" de cara al usuario por la misma razón.
| Bajo | Bajo | S | Bajo | Quick Win |

### H9 — Errores del Dashboard se tragan sin log `[OBSERVADO]`
`Dashboard.razor.cs:72-75` (`catch (Exception) { _error = true; }`) frente a `VisionCartera.razor.cs:35-38` que sí registra — diagnosticar un dashboard caído en producción exige reproducirlo.
| Bajo | Medio (soporte) | S | Medio | Quick Win |

## Riesgos futuros

- **Volumen**: H3+H4 son el mismo patrón (clasificación en memoria sin agregados); cuando un tenant pase de ~50k documentos la home será la página más lenta del producto — la primera impresión de cada sesión.
- **Pro-Inbound** (checklist § 4.3): el alcance por Ids visibles (`IAlcanceDatosService`) es un buen cimiento para un futuro rol de contratista externo; nada bloqueante detectado en este módulo. H1 es la otra cara: ese mismo alcance necesita distinguir "vacío por permiso" de "vacío por datos".

## Propuestas (priorizadas)

1. **Estado "sin cartera asignada"** (H1): si el alcance devuelve conjunto vacío, render de `EstadoVacio` explicando la causa y quién puede asignar cartera — nunca KPIs a cero con SLA verde. — S, Quick Win.
2. **Tarjeta "Pendiente por plataforma destino"** (H2): por cliente, cuántos documentos válidos en Hydra faltan por subir/actualizar en cada portal; exige el registro del hueco 4.1.3 — diseñarlo como la futura tarjeta nº 1 del Dashboard. — L, Medio plazo (la pieza de mayor valor de esta ficha).
3. **Agregado SQL para el semáforo** (H3/H4): un `GROUP BY` por estado calculado con los umbrales en SQL (o vista materializada por tenant invalidada al escribir Documento), y la cartera en una sola consulta agrupada por tenant en vez del bucle. — M, Medio plazo.
4. **Tema del DS en ApexCharts** (H5): pasar tokens de color/leyenda según tema activo. — S, Quick Win.
5. **Colapsar "Personalizar"** (H6) con `SeccionColapsable`, cerrado por defecto cuando ya hay selección guardada. — S, Quick Win.
6. **Ids en los cross-links** (H7) reutilizando el deep-link del Context Workspace en cuanto exista (`?ctx=`, hueco conocido del plan). — S, Quick Win.
7. **"Actualizado hace X min" + refresco manual** en cabecera de las tres pantallas. — S/M, Medio plazo.
8. **Microcopy** (H8): "cliente delegado" en vez de "Delegated Workspace"; revisar el resto de vacíos de las tres pantallas de paso. — S, Quick Win.
9. **Log en el catch del Dashboard** (H9), mismo patrón que Visión de cartera. — S, Quick Win.

## Referencias de principios

- **Stripe Dashboard**: frescura explícita de los datos ("updated X ago") — un número sin marca de tiempo invita a decidir sobre datos viejos (H3/propuesta 7).
- **Linear**: la configuración de vista vive detrás de "Display options", nunca como bloque permanente sobre el contenido (H6).
- **Dokify/Nalanda como principio, no como comparativa**: su home responde "qué tengo pendiente de resolver hoy en esta plataforma" — el principio (el dashboard responde la pregunta operativa del día) es exactamente lo que falta en H2.
