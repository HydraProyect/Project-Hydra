# Sesión 02 — Clientes (lista, alta guiada, lectura IA por cliente)

> Auditado el 2026-08-05 en ejecución (datos demo: 9 Clientes; usuario `prueba.direccioncae1`, tema oscuro, capturas propias). Las páginas de importación se auditan en la sesión 13. Archivos: `Features/Clientes/Pages/Clientes.razor(.cs)`, `AltaGuiada.razor(.cs)`, `ConfiguracionIaCliente.razor(.cs)`, `Components/ClienteWorkspacePanel.razor`, `FormularioRapidoCliente.razor(.cs)`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | CRUD impecable (drawer, deshacer, alta encadenada, alta guiada con guardado incremental), pero la lista es un directorio que no responde "¿qué cliente está en rojo y de quién es?" — la pregunta diaria de una consultora. |
| UI | 7 | Design System aplicado con densidad correcta y badges semánticos; lo desluce el paginador en inglés y el label "Notas" descolocado en Alta guiada. |
| Usabilidad | 7 | Atajos j/k/x/Enter, filtros guardados, búsqueda persistida en URL y deep-link `?accion=crear`; pero los filtros guardados no se pueden borrar y la selección múltiple muere al cambiar de página. |
| Consistencia | 6 | Paginador QuickGrid sin localizar, filas no clicables (en Dashboard sí), validación al enviar en vez de al salir del campo — tres desvíos del propio `UX_PATTERNS.md`. |
| Escalabilidad | 6 | Paginación y orden reales en servidor (lista blanca + desempate por Id) ✓; acciones de lote confinadas a la página de 20 y export único global. |
| Madurez | 7 | Concurrencia optimista con `Version`, soft delete con deshacer, reasignación de cartera con aviso a ambos gestores y permisos por rol — nivel alto para un MVP. |
| Competitividad | 5 | Gana al Excel en integridad (CIF validado, concurrencia, papelera), no aún en visión de cartera: el Excel del gestor tiene columnas de estado y dueño que aquí faltan. |

## Hallazgos priorizados

### H1 — La lista no muestra salud documental ni dueño de la cartera `[OBSERVADO]`
Columnas: checkbox, Razón social, CIF, Crítico, Acciones (`Clientes.razor:69-101`). El estado documental derivado del cliente no existe como columna ni filtro (el patrón está definido en `UX_PATTERNS.md:45` y `FiltroEstado` existe), y el Gestor CAE asignado solo es visible abriendo Editar cliente a cliente (`Clientes.razor:138-148`).
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto | M | Alto — la operación de cartera se hace fuera (Dashboard/Excel) | Medio plazo |

### H2 — Paginador en inglés en toda la plataforma `[OBSERVADO]`
Captura: "9 items", "Page 1 of 1" — el `Paginator` de QuickGrid no está localizado; rompe la regla de microcopy nº 1 (`UX_PATTERNS.md:7`, "todo en español") en la práctica en cada lista del producto.
| Medio | Medio (percepción de acabado en demos) | S | Medio | Quick Win |

### H3 — Selección múltiple confinada a la página `[OBSERVADO]`
"Seleccionar todo" marca solo los 20 visibles (`Clientes.razor.cs:378-384`) y la selección se limpia en cada recarga de página (`:149`); única acción de lote: eliminar. No existe "seleccionar los N resultados del filtro".
| Medio | Medio | M | Medio — los lotes reales (50+) se hacen en tandas | Medio plazo |

### H4 — Filtros: sin chips, persistencia parcial y filtros guardados imborrables `[OBSERVADO]`
Solo `q` va a la URL (`Clientes.razor.cs:169`), `soloCriticos` no; no hay chips removibles (`UX_PATTERNS.md:43`); `EliminarFiltroGuardadoAsync` (`Clientes.razor.cs:509-519`) no tiene ningún punto de entrada en el markup — un filtro guardado es para siempre.
| Medio | Bajo | S | Bajo | Quick Win |

### H5 — Fila no clicable `[OBSERVADO]`
Abrir un cliente exige el botón "Ver" o j/k+Enter; el click en fila que `UX_PATTERNS.md:29` da por sentado y que el Dashboard sí implementa (`fila-clicable`) aquí no existe — el objetivo más grande de la fila (toda la fila) no hace nada.
| Medio | Bajo | S | Bajo | Quick Win |

### H6 — Workspace panel: pestañas desbordadas y sin acción de edición `[OBSERVADO]`
Captura: las pestañas (Información/Empresas/Subcontratas/Documentación/Actividad…) desbordan con scrollbar horizontal, y la ficha Información (4 campos) no ofrece "Editar" — hay que cerrar el panel y volver a la fila.
| Medio | Bajo | S/M | Bajo | Quick Win |

### H7 — Validación solo al enviar `[OBSERVADO]`
Los errores de campo aparecen al capturar `ValidationException` del Command (`Clientes.razor.cs:304-309`), nunca al salir del campo (`UX_PATTERNS.md:86`). Un CIF mal tecleado se descubre tras pulsar Guardar.
| Bajo | Bajo | M (patrón compartido) | Bajo | Medio plazo |

### H8 — Errores de carga sin log `[OBSERVADO]`
`ProveerElementosAsync` traga la excepción sin registrarla (`Clientes.razor.cs:154-157`) — mismo patrón que el Dashboard (sesión 01 H9); diagnóstico en producción a ciegas.
| Bajo | Medio | S | Medio | Quick Win |

### H9 — Import solo para Administrador `[OBSERVADO]` (decisión implícita sin registrar)
`Clientes.razor:24-27` oculta Importar/Importación combinada a DireccionCae; ni `DECISION_LOG.md` ni ADRs registran por qué la dirección no puede importar. Consta como decisión implícita a validar.
| Bajo | Bajo | S | Bajo | Quick Win (registrar o abrir) |

### Positivo verificado (para no perderlo en la consolidación)
- Alta guiada Cliente→Empresa→Centro con `IndicadorPasos` y **guardado incremental real**, explicado en el copy (captura) — exactamente el patrón de `UX_PATTERNS.md:24`.
- "Continuar con la empresa" en el drawer (`Clientes.razor:156-158`) — alta encadenada conforme al patrón.
- Concurrencia optimista con `Version` (`Clientes.razor.cs:222,272`) y "no encontrado" amable si otro lo borró (`:204`).
- Deshacer tras eliminar vía toast con acción (`:345`).
- Export a Excel en la propia lista (`Clientes.razor:23`) — **corrige el hueco 4.1.1 del inventario para Clientes** (verificar cobertura en el resto de listas, sesión por sesión).
- Lectura IA por cliente con herencia de dos niveles (global→cliente) verificada en ejecución.

## Riesgos futuros
- El toast de errores de borrado en lote concatena mensajes en un string (`Clientes.razor.cs:405`) — con 20 fallos será ilegible; migrar a resumen en modal cuando crezcan los lotes.
- Pro-Inbound (§ 4.3): nada bloqueante — la jerarquía Cliente→Empresa y el alcance por rol encajan con un futuro portal de contratista.

## Propuestas

1. **Columna + filtro de estado documental derivado del Cliente** (H1) con el semáforo estándar y `FiltroEstado` — convierte la lista en la vista de cartera. — M, Medio plazo, la de más valor.
2. **Columna "Gestor CAE" + filtro "mi cartera"** (H1) — S/M, Quick Win amplio.
3. **Paginador localizado** (H2), una vez para todas las listas. — S, Quick Win.
4. **Selección de todos los resultados del filtro** con contador "Seleccionados: 120 de 120" (H3). — M, Medio plazo.
5. **Chips de filtros activos + `soloCriticos` en URL + borrar filtros guardados** (H4). — S, Quick Win.
6. **Fila clicable → Workspace** (H5), mismo comportamiento que Dashboard. — S, Quick Win.
7. **"Editar" en la cabecera del panel Workspace** (H6). — S, Quick Win.
8. **Validación al salir del campo** para CIF/Razón social, patrón compartido para todos los drawers (H7). — M, Medio plazo.
9. **Log en catch de proveedores de grid** (H8), patrón único compartido. — S, Quick Win.
10. **Buscador en la tabla de Lectura IA** (45 tipos de documento, captura) — S, Quick Win.

## Referencias de principios
- **Linear**: una lista es una herramienta, no un directorio — estado y owner como columnas de primera clase, filtros como chips componibles (H1/H4).
- **Gmail**: "Select all N conversations matching this search" (H3) — el patrón exacto que falta.
- **Stripe**: exportar respeta el filtro activo y lo dice ("Export 23 filtered rows") — aplicable al enlace de export cuando la lista gane filtros ricos.
