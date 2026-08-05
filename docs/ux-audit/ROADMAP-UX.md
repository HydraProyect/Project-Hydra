# Auditoría UX Hydra — Consolidación final y roadmap

> Cierra la Fase 1 (16 sesiones, `00-INVENTARIO.md` § 5) ejecutada el 2026-08-05 **sobre la aplicación corriendo** con datos de demo (3 tenants, 371 trabajadores, 3.114 documentos) y capturas propias; los módulos solo-Administrador finales se auditaron sobre código al congelarse el panel del navegador. Marco vinculante: § 0 del inventario (MVP = CAE Outbound; vara: "¿más rápido y fiable que Excel + operar los portales?"; benchmark Konvergia; Dokify/Nalanda/eCoordina/CTAIMA solo como principios).

## Nota global

Hydra tiene una **columna vertebral de plataforma por encima de su clase** (multi-tenant con delegaciones trazadas, retención RGPD operativa, auditoría IA con costes, un design system aplicado de verdad, IA con human-in-the-loop en todas sus superficies) y una **capa de operación diaria a medio rematar**: listas que divergen del propio patrón, capacidades de backend sin puerta en la UI, y — lo más importante — el job Outbound (dejar acreditada la documentación en la plataforma de cada titular) todavía termina fuera del producto. La distancia a "producto de referencia" no es de rediseño: es de rematar lo que ya está bien diseñado y de modelar la plataforma destino como entidad de primera clase.

## Los 10 problemas más graves de la plataforma

| # | Problema | Evidencia | Sesiones |
|---|---|---|---|
| 1 | **`/centros` no carga en su ruta por defecto** — regresión EF mergeada a main (commit `eb02739`); página muerta al entrar, también en producción. Bug confirmado y reproducido; derivado a tarea propia. | `ObtenerCentrosQuery.cs:64-93` + log servidor | 04 |
| 2 | **La plataforma destino no existe como entidad**: nombre libre por Centro, una credencial suelta por Empresa, ningún estado "subido/aceptado/rechazado" por documento. El corazón del job Outbound (¿qué queda pendiente en qué portal?) no se puede ni preguntar. | `CanalGestionDocumental.cs:43`, `CredencialAccesoEmpresa.cs:22`, hueco 4.1.3 | 01·03·04·07 |
| 3 | **El vacío miente en verde**: un operador delegado sin cartera ve KPIs a 0 con SLA 100%; el alta de delegación aterriza ahí sin explicación. Primera impresión de toda consultora nueva. | Captura runtime + `ObtenerKpisDashboardQuery.cs:80` | 01·15 |
| 4 | **Promesa de recuperación falsa**: el borrado en lote promete "recuperar desde Auditoría" y Auditoría no restaura nada; pasado el toast de 8s no hay camino. | `Empresas.razor:181` vs `Features/Auditoria` (0 resultados de Restaurar) | 03·14 |
| 5 | **La Bandeja no escala**: la pantalla que define el valor diario carga todos los items sin tope, paginación ni agregación — cientos de tarjetas ya en demo. | `Bandeja.razor.cs:55` | 10 |
| 6 | **La reclamación documental saliente no existe**: macros y envío proactivo están construidos pero apagados y sin disparo desde la operación — el trabajo de perseguir a las Empresas sigue en el correo personal. | Hueco 4.1.2; `Macros`, `EnviarMensajeNuevoCommand` sin puente | 12 |
| 7 | **Reportes no produce el entregable**: un único informe fijo de todo el tenant, sin filtro por Cliente — el informe mensual con el que la consultora justifica su cuota se recorta a mano. | `Reportes.razor:14-16` | 11 |
| 8 | **Identidad con bordes rotos**: sin permiso → pantalla de login (con sesión viva), y el login no tiene recuperación de contraseña. | Runtime verificado | 13·16 |
| 9 | **Un patrón de lista, cinco implementaciones**: export solo en Clientes, click en nombre solo en algunas, estado documental solo en otras, paginador en inglés (y otro en español en Usuarios), selección que muere al cambiar de página, acciones apiladas que rompen la densidad. | Sesiones 02-08, capturas | 02-08·14 |
| 10 | **Agregados que no escalan**: KPIs clasificando todas las fechas en memoria por vista y Visión de cartera iterando tenants en secuencia — la home será la página más lenta del producto al crecer. | `ObtenerKpisDashboardQuery.cs:65-73`, `ObtenerKpisGlobalesQuery.cs:47-54` | 01 |

## Patrones repetidos entre módulos

**Deudas** (cada una aparece en ≥3 módulos):
- Paginador QuickGrid sin localizar (12 listas) + un segundo paginador propio en español — consolidar en uno.
- Sin export salvo Clientes y Reportes; el gesto "llevármelo a Excel/PDF" es la lingua franca del sector.
- Selección múltiple confinada a la página (20 filas), sin "seleccionar los N filtrados"; única acción de lote: eliminar.
- Persistencia parcial de filtros en URL y cero chips de filtros activos (`UX_PATTERNS.md:43` incumplido en todas).
- **Capacidades fantasma**: Commands/patrones construidos sin puerta en la UI — `DarDeBajaAsignacionesCommand`, `EliminarFiltroGuardadoAsync`, el patrón "Duplicar" documentado (grep: 0 usos), la página de detección de personal sin ningún enlace de navegación.
- `catch` sin log en los providers de grid (el pipeline de Application sí loguea — la mitad buena ya existe).
- Excepciones `JSDisconnectedException` sin capturar ensuciando el log en cada navegación.

**Fortalezas de sistema** (protegerlas al tocar lo anterior):
- Una sola fuente de verdad: `CalculadoraEstadoDocumento` y `IDocumentosFaltantesService` alimentan Dashboard, listas, Bandeja, preflight y reportes — nada puede descuadrar.
- Estados vacíos con causa y siguiente paso en prácticamente todas las pantallas.
- IA siempre con human-in-the-loop y umbrales explícitos (subida masiva, revisión IA, detección de personal).
- Flujos de referencia internos: alta guiada con guardado incremental, asignación N×M con matriz y preflight, importación con dry-run, crear-desde-faltante.
- Plataforma disciplinada: delegaciones con revocación bilateral, soporte con motivo/caducidad/traza, retención sin atajos, claves API de un solo vistazo.

## Fuera de alcance del MVP actual (mecánica Inbound — registro único, regla 2 § 0)

La ausencia de estos flujos **no puntúa** en ninguna ficha; pertenecen al futuro MVP Inbound:
- Portal de autoservicio para que Empresas/Subcontratas suban su documentación y consulten su estado.
- Apto/no apto operativo por centro consumible en campo (tornos/QR/listado).

**Deuda pro-Inbound detectada** (lo único que sí se vigila hoy, § 4.3): ninguna decisión bloqueante encontrada. Puntos de vigilancia: catálogo de roles fijo en código (añadir un rol de contratista será despliegue, no configuración); mantener los estados adicionales del Documento como entidades satélite (el patrón de Revisión IA) y no como campos editables; conservar `IDocumentosFaltantesService` como fuente única cuando llegue el control de acceso.

## Roadmap en tres horizontes

### Horizonte 1 — Quick wins (semanas; casi todos S, independientes entre sí)
1. Arreglar `/centros` (ya derivado a tarea) y añadir un smoke E2E por página que cargue cada ruta con datos.
2. ✅ Estado "Sin cartera asignada" en Dashboard/workspace delegado (mata el verde falso, #3). — [PR #100](https://github.com/christopherjp1-jpg/Project-Hydra/pull/100)
3. Restaurar desde Auditoría o corregir el copy del borrado en lote (#4) — lo segundo es de hoy para mañana.
4. Página Forbidden propia (#8a).
5. Paginador único localizado (#9).
6. Overflow menu en Acciones de las listas (densidad, #9).
7. Export en Empresas/Centros/Asignaciones/Incidencias/Auditoría (#9) + export del resumen de facturación.
8. Badge de detecciones pendientes en Empresas + tipo nuevo en Bandeja (capacidad fantasma → visible).
9. Contadores por tipo en la Bandeja; tema oscuro y leyenda del Calendario; colapsar "Personalizar" del Dashboard Ejecutivo.
10. Lote de: filtros completos en URL, chips de filtros, borrar filtros guardados, placeholder "—", label Notas del alta guiada, quitar pestaña "Citas", fila clicable en Clientes, selector de tamaño de página, catch de `JSDisconnectedException`, investigar el error CSP, registrar en `DECISION_LOG.md` la decisión de estado calculado.

### Horizonte 2 — Medio plazo (1-2 trimestres; la apuesta estratégica Outbound en negrita)
1. **Catálogo de Plataformas por tenant** (normaliza los strings de `CanalGestionDocumental`) → **credenciales por Empresa×Plataforma en el punto de uso** → **estado-en-portal por documento (subido/aceptado/rechazado, manual hasta que haya conector)** → **tarjeta "Pendiente por plataforma" como primera del Dashboard**. Esta cadena (#2) es la que convierte Hydra en el lugar donde el día Outbound empieza y termina.
2. Reclamación documental saliente (#6): "Enviar reclamación" desde documento vencido/faltante y Bandeja, con macro sugerida y buzón M365 — el módulo de Comunicaciones ya lo tiene casi todo.
3. Reportes parametrizados por Cliente con membrete (#7) y biblioteca mínima (vigencia, incidencias, asignaciones activas).
4. Bandeja agregada y paginada (#5) con "N vencidos de la misma empresa".
5. Nivelar las listas al mejor patrón interno (#9): estado documental + gestor en Clientes, Subcontratas completa, baja en lote de Asignaciones (Command ya existe), "seleccionar todos los filtrados", pestaña Asignaciones en el panel del Trabajador.
6. Agregados en SQL para KPIs y cartera (#10).
7. Recuperación de contraseña (#8b); Documentos en el buscador global; cerrar los 4 huecos del Context Workspace (el deep-link `?ctx=` desbloquea los cross-links por Id); historial de importaciones; feed ICS de vencimientos/visitas.

### Horizonte 3 — Largo plazo (la secuencia ya faseada en `PRODUCT_STRATEGY.md` — se audita la secuencia, no se reinventa)
1. Conectores de subida automática a plataformas destino (Fase 2 "Orquestador"; Twind/CTAIMA primero) — el estado-en-portal manual del H2 se convierte en sincronizado.
2. Decisión Konvergia (pregunta abierta registrada) cuando el estado-en-portal exista y aporte datos reales de qué plataformas pesan en la cartera.
3. MVP Inbound (portal de contratista + validación de titular) reutilizando: `PestanaDocumentacion` por ámbito, requisitos con `BloqueaAcceso`, la Bandeja tipada y el patrón satélite de revisiones.

## Cómo leer las notas (recordatorio de rúbrica)
Ningún módulo alcanzó el 8: la media ronda el 5-6 — "correcto y usable, no vendible como ventaja" — con picos de 7 en los flujos de referencia (asignación N×M, importación, ciclo documental, plataforma/delegaciones) y mínimos de 3-4 donde el camino feliz está roto (`/centros`, Reportes). El veredicto agregado: **Hydra ya es mejor sistema de registro que el Excel; aún no es mejor sistema de operación que "Excel + los portales". La cadena del Horizonte 2.1 es exactamente lo que cambia eso.**
