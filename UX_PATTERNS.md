# Patrones de UX y Microcopy — CAE Manager

Todo el sistema usa exactamente los mismos patrones para las mismas acciones. Un usuario que aprende a crear un Cliente ya sabe crear un Trabajador o un Centro.

## Regla de microcopy

- Todo en español, tono directo y humano, nunca técnico.
- El mensaje explica qué pasó y, si aplica, qué puede hacer el usuario ahora.
- Nunca se expone jerga técnica (stack traces, códigos HTTP, nombres de excepción) al usuario final; eso va al log.

| Situación | ❌ No | ✅ Sí |
|---|---|---|
| Error de guardado | "Error inesperado" | "No pudimos guardar los cambios. Intenta nuevamente en unos segundos." |
| Validación | "Field required" | "Este campo es obligatorio." |
| Conflicto | "409 Conflict" | "Alguien más modificó este registro mientras lo editabas. Revisa los cambios antes de guardar." |
| Sin permiso | "403 Forbidden" | "No tienes permiso para ver esta sección. Si crees que es un error, contacta a un administrador." |
| Éxito | "OK" | "Cliente creado correctamente." |

## Patrones de acción

### Crear
Botón primario arriba a la derecha de la tabla/lista ("+ Nuevo cliente"). Abre formulario en **Drawer** lateral (no navega a otra página) para acciones simples de un solo agregado; usa página completa solo cuando el formulario tiene múltiples secciones/pestañas (p. ej. Trabajador con documentos y asignaciones, o un asistente de varios pasos como `/clientes/alta-guiada`). Al guardar: toast de éxito + la tabla se actualiza sin recargar página + el drawer se cierra.

**Alta encadenada**: cuando crear una entidad casi siempre implica crear la siguiente de la jerarquía (Cliente → Empresa → Centro), el Drawer ofrece un botón secundario ("Continuar con la empresa", "Continuar con el centro") junto al primario "Guardar" — guarda igual, pero en vez de cerrar navega a la creación de la siguiente entidad con el padre ya fijado en el selector (`?clienteId=…`). El texto del botón nunca repite la palabra "Guardar": conviven en el mismo pie del Drawer y un locator de test por texto (`GetByText`, coincidencia por subcadena) no debe poder confundir uno con otro. Un asistente de página completa con `IndicadorPasos` es la versión que hace los tres saltos seguidos sin salir de una pantalla, con guardado incremental real en cada paso — nunca una transacción larga que se pueda perder a medio camino.

**Crear inline en un selector**: cuando el registro que hace falta no existe todavía, el selector (`SelectorEntidad`) ofrece una fila "+ Crear «texto»" que abre un formulario mínimo en **Modal** (nunca un Drawer anidado — un Drawer dentro de otro rompe el focus trap de ambos). Al guardar, el nuevo registro queda seleccionado sin que el usuario pierda lo que ya llevaba escrito en el formulario que lo necesitaba.

### Editar
**Edición in situ en el Context Workspace** (Centro 360 § 0.10) — no un Drawer. La fila de la tabla ya no tiene botón "Editar", solo **"Detalles"** (abre el Workspace en la pestaña "Información") y "Eliminar". Dentro de "Información", un icono de lápiz junto al título de la cabecera (`aria-label="Editar información de…"`) convierte los `CampoInfo` de esa pestaña en campos editables (`CampoTexto`/`CampoSelect`/`SelectorMultiple`, según el campo), con "Cancelar"/"Guardar" bajo el bloque — mismo `Editar*Command` que antes disparaba el Drawer, versión optimista incluida (`Version` ya cargada por la pestaña, sin fetch adicional al entrar en modo edición). Al guardar: toast + vuelve a modo lectura con los datos recargados; al cancelar, descarta los cambios locales sin tocar el servidor. Un campo que no forma parte del `Editar*Command` (identidad fijada al crear — DNI de Trabajador, Empleador de Trabajador/Vehículo, Cliente/Empresa de Centro) se queda en `CampoInfo` de solo lectura dentro del mismo bloque, nunca desaparece. El Drawer de la lista queda **solo para "Nuevo X"** (alta) — título fijo, sin rama de edición. Si la entidad tiene un satélite que solo se gestionaba desde ese Drawer en modo edición (credenciales de acceso a plataforma externa de Empresa/Subcontrata), se traslada al mismo panel del Workspace como sección independiente con su propio guardado, en vez de perder la funcionalidad al retirar el Drawer.

Autoguardado **no** se usa en formularios con relaciones de negocio críticas (documentos, vigencias) — se guarda explícitamente para que el usuario tenga control sobre cuándo un cambio de vigencia se aplica. Sí se permite autoguardado en campos de notas/comentarios libres.

### Eliminar
Siempre **soft delete**. Confirmación obligatoria vía **Dialog** modal (no un `confirm()` de navegador): título claro ("¿Eliminar a Juan Pérez?"), cuerpo con la consecuencia real ("Se ocultará de las listas activas."), botón destructivo en rojo con la acción explícita ("Eliminar"), botón secundario "Cancelar". Nunca "¿Estás seguro?" como único texto.

**Deshacer (Fase D)**: en Cliente/Empresa/Centro/Trabajador/Documento, el toast de éxito tras eliminar incluye un botón "Deshacer" que restaura la entidad in situ (`Restaurar*Command`) — no hace falta ir a Auditoría ni recrearla a mano. Un toast con acción vive 8s en vez de 5 (ver "Toasts"). Asignación no tiene este patrón: nunca se "elimina", se da de baja (`FechaBaja`), y ese ciclo ya es reversible por otra vía (volver a asignar).

### Duplicar
Disponible en entidades con mucha repetición estructural (Centro, RequisitoDocumental). Abre el formulario de creación precargado con los datos del original, campo de nombre vacío/resaltado para forzar que el usuario lo revise antes de guardar.

### Buscar
Buscador global fijo en la barra superior (`Cmd/Ctrl+K`), busca por nombre/DNI/código across Clientes, Centros, Trabajadores. Resultados agrupados por tipo de entidad, con navegación por teclado. Este es el mecanismo que reemplaza la hoja "Filtros" manual del Excel — cumple el objetivo de "menos de tres clics": abrir buscador → escribir → clic en resultado.

### Filtrar
Panel de filtros junto a cada tabla, nunca oculto en un menú de tres puntos. Filtros activos se muestran como chips removibles sobre la tabla. Estado de filtros persiste en la URL (query string) para que se pueda compartir/recargar sin perder el contexto.

Toda lista con un estado ofrece su **filtro de estado** en la barra de filtros, con el componente compartido `FiltroEstado` y las opciones ordenadas **de peor a mejor**: al filtrar, lo que el usuario busca es lo que le urge. Las entidades que no tienen estado en el modelo (Trabajador, Empresa, Vehículo) usan el **estado documental derivado** — el peor estado de vigencia de sus documentos, con el mismo semáforo que el resto del sistema —, y "sin documentos" es siempre una opción propia, nunca se muestra como si estuviera al día.

### Ordenar
Toda columna con un criterio de orden con sentido es ordenable, y ordena **de verdad en el servidor** — nunca solo reordenando la página ya cargada. Cada Query acepta el nombre de columna contra una lista blanca: un valor desconocido cae al orden por defecto, jamás llega a la consulta. El orden se cierra siempre con un desempate estable por Id, porque sin un criterio total la paginación puede repetir o perder filas. El orden no se persiste en la URL (los filtros sí).

### Paginar (H2, docs/ux-audit/02-clientes.md)
Un único paginador visual y de copy en español para toda la plataforma — `PaginadorSimple` ("← Anterior · Página X de Y — N elemento(s) · Siguiente →") —, tanto en listas con paginación manual (`PaginaActual`/`TotalPaginas` como enteros) como en las que usan `QuickGrid` con `PaginationState` para el fetch en servidor: en este segundo caso, `PaginaActual` se deriva de `_paginacion.CurrentPageIndex + 1` y `PaginaActualChanged` delega en `_paginacion.SetCurrentPageIndexAsync(pagina - 1)` para que sea `QuickGrid` quien siga pidiendo los datos — nunca el `Paginator` propio de QuickGrid, que no está localizado ("Page 1 of 1"). `TotalPaginas` se recalcula de `_totalElementos` (ya trackeado por el `ItemsProvider` de cada lista) con `Math.Max(1, Ceiling(_totalElementos / ItemsPerPage))`.

### Subir documentos
Zona de drag-and-drop + botón explícito "Seleccionar archivo", solo PDF, tamaño máximo indicado antes de intentar subir. Barra de progreso durante la subida. Tras subir: vista previa inmediata (no hace falta recargar para confirmar que se adjuntó bien).

### Descargar documentos
Un clic descarga directo; para PDFs, opción adicional de "Ver" que abre el visor integrado (PDF Preview) sin salir de la aplicación.

### Cambiar estado
El estado de un Documento (vigente/próximo/urgente/vencido) **nunca se edita manualmente** — es siempre calculado (ver `DATABASE.md`). Lo único editable es la fecha de emisión, de la que se deriva el estado. Esto evita la inconsistencia que hoy tiene el Excel entre la celda "Estado" y la fórmula real.

### Asignar trabajador a centro / cliente
Desde el detalle del Trabajador o del Centro: selector con búsqueda, fecha de alta obligatoria (default hoy), fecha de baja opcional. Al asignar, si el Centro tiene `RequisitoDocumental` con `BloqueaAcceso = true` y el trabajador no cumple ese requisito, se muestra una advertencia visible (no bloqueante para el registro administrativo, sí bloqueante conceptualmente para el acceso físico) — replica el comportamiento real observado en el Excel ("el sistema BLOQUEA el acceso del trabajador").

**Asignación en lote (Fase 87, reubicada en Centro 360 § 0.1)**: la matriz N Trabajadores × M Centros vive ahora en el drawer "+ Asignar trabajador" del acordeón de cada fila de `/centros` (`AcordeonAsignacionesCentro.razor` — `/asignaciones` como página independiente se retiró) — dos `SelectorMultiple` en vez de dos `<select>`, un único `CrearAsignacionesCommand` que calcula el producto cartesiano y omite en silencio lo ya activo, con el Centro de esa fila pre-marcado al abrir. Pestañas "Lista"/"Matriz" sobre la misma selección: "Matriz" es la cuadrícula Trabajador × Centro con una casilla por celda, para cuando el alta no es un rectángulo completo — desmarcar una celda la excluye del lote sin tocar el resto. El preflight de documentos que le faltarían a cada combinación (`ObtenerDocumentosFaltantesParaAsignacionQuery`, misma lógica `IDocumentosFaltantesService` que usa `/alertas`) se recalcula en cada cambio de selección y se muestra como aviso — **nunca bloquea el guardado**, solo cambia el texto del botón a "Asignar igualmente" cuando hay huecos. Mismo Command reutilizado como acción de lote "Asignar a centro…" desde `/trabajadores` (`BarraAccionesLote.AccionesExtra`).

### Badge de visita y ventana de riesgo (Centro 360 § 0.3)
Cada Centro con una Visita activa (`FechaFin >= hoy`) muestra en la cabecera de su acordeón un badge clicable "Visita dd/mm–dd/mm" que lleva a `/visitas?q=<nombre del centro>` — proyección de datos, sin modelo nuevo (`ObtenerProximaVisitaPorCentroQuery`, batch por página, no por fila). Dentro del tercer nivel, un documento `Vigente` que caducaría antes de que termine esa visita gana el modificador "vigente con riesgo en ventana" (ver `DESIGN_SYSTEM.md` § Color): se calcula reevaluando `CalculadoraEstadoDocumento` con fecha de referencia = fin de la visita en vez de hoy — mismo patrón reutilizable para cualquier pantalla que necesite "¿aguanta este documento hasta tal fecha?" sin inventar un estado nuevo. **Asignación rápida desde visita**: si `VisitaTrabajador` indica un Trabajador sin Asignación activa en ese Centro, aviso con un botón "Asignar" por nombre (el trabajador ya está identificado — no hace falta `SelectorEntidad`) que dispara `CrearAsignacionCommand` con el mismo preflight no bloqueante de arriba.

### Selección múltiple tras toggle + densidad de fila (Centro 360 § 0.9)
Los checkboxes de fila **no están visibles por defecto** en ninguna lista: son ruido permanente para una acción ocasional. Un botón "Selección múltiple" en la `BarraHerramientasLista` (bajo la barra de filtros, componente compartido por las 9 listas — no markup repetido) los muestra y los oculta, junto con el "Seleccionar todos" de cabecera. **Apagar el modo limpia la selección**: dejar filas marcadas que ya no se ven dejaría `BarraAccionesLote` apuntando a algo invisible.

En las listas con acordeón (`/centros`, `/empresas`), la posición donde vivía el checkbox la ocupa el control de expandir/colapsar de la fila (`.boton-expandir-fila`: contraído = flecha a la derecha, expandido = flecha abajo). Al activar selección múltiple el checkbox **se añade a su izquierda sin desplazarlo ni ocultarlo** — ambos conviven. La misma barra ofrece "Expandir todos"/"Colapsar todos", y por eso la expansión la lleva la página (un `HashSet` de Ids) y no el estado interno de cada `SeccionColapsable`: nadie puede decidir por todas desde fuera si cada una guarda el suyo. El contenido del desplegable se carga perezosamente por fila, al expandir (no al pintar la página) — "Expandir todos" dispara la carga de todas las filas pendientes en paralelo (`Task.WhenAll`), no una tras otra.

### Drill-down entre listas con filtro exacto por Id (Centro 360 § 0.11)
Cuando un elemento del desplegable de una fila (p. ej. un Centro dentro del acordeón de una Empresa) lleva a otra lista para verlo en su propio contexto, el destino se prefiltra **por Id exacto vía query string** (`/centros?centroId=…`), no por texto libre (`?q=<nombre>`) — un nombre parecido entre dos filas de la entidad destino haría el filtro de texto ambiguo. La Query de esa lista gana un parámetro opcional adicional (`CentroId` en `ObtenerCentrosQuery`) que se combina con el resto de filtros ya existentes, y la página expone un `[SupplyParameterFromQuery]` propio — mismo mecanismo que ya usan `q`/`estado`, solo que exacto en vez de subcadena.

**Densidad de fila**: las filas de lista priorizan cuántas caben por pantalla sobre el aire — padding vertical y gap reducidos, y `Badge Tamano="TamanoBadge.Pequeno"` (más el `.badge-visita`, que replica esa métrica) para que un badge no marque la altura de la fila entera. Solo cambia métrica, nunca color: el semáforo de vigencia se lee igual. El padding horizontal no se toca — apretar los lados no gana filas.

### Bandeja del gestor (Fase C)
`/bandeja` es una única cola priorizada — sugerencias de visita sorpresa sin confirmar, faltantes, vencidos, visitas próximas dentro de la ventana de validación, requisitos documentales que bloquean el acceso a un Centro, urgentes y revisiones IA pendientes, en ese orden — para no tener que visitar cuatro pantallas distintas para saber qué atender primero. Cada tarjeta tiene **una sola acción primaria** (nunca varias compitiendo), navegación `j`/`k`/`Enter` (mismo `AtajosListaTeclado` que ya usan las 10 listas con selección múltiple) y filtro por tipo persistido en la URL. El mismo panel, en miniatura (las primeras 5 filas + enlace "Ver la bandeja completa"), se monta también en `/alertas` dentro de una `SeccionColapsable` — `/alertas` no pierde ninguna fila (sigue mostrando también "Próximo", que la Bandeja excluye a propósito por no ser todavía urgente).

### Gestiones urgentes de visita (Fase F)
Una Visita cuyo inicio cae dentro de la ventana mínima de validación de la plataforma del cliente (`ParametroSistema.HorasAvisoVisita`/`HorasCriticasVisita`, 48h/24h por defecto) se marca "Urgente"/"Crítica" — badge en `/visitas`, filtro "Solo urgentes", y entra en la Bandeja del gestor. Una sugerencia de visita (correo o WhatsApp) sin resolver dentro de esa misma ventana es **la prioridad más alta de toda la Bandeja**: sin confirmarla no hay ni Visita ni documentación que verificar todavía. Sin fecha detectada por la IA, se trata como "sorpresa del mismo día" en vez de descartarse por falta de dato — es la interpretación más segura ante ambigüedad. Aviso adicional por hora (solo la campana, `NotificacionUsuario`, sin correo en v1) a Administrador/DireccionCae mientras haya gestiones urgentes pendientes.

### Pedir prioridad de validación (Fase G)
Botón "Pedir prioridad" en la cabecera de `CentroWorkspacePanel`: abre un Drawer con un correo **precargado y editable** — destinatario sugerido, asunto y cuerpo agrupado por Empresa/Subcontrata → Trabajador con los documentos pendientes de ese Centro (`ObtenerBorradorPedirPrioridadQuery`, misma fuente `IDocumentosFaltantesService` que la Bandeja y el preflight de asignación). "Plantilla adaptable" es literal — nada sale sin que el Gestor lo revise y, si quiere, lo reescriba antes de confirmar. Avisa (sin bloquear) si no hay ningún buzón conectado o si ya se pidió prioridad recientemente para ese Centro — insistir puede tener un motivo real. El envío sale siempre del buzón de Microsoft 365 configurado en la plataforma (nunca uno personal) — misma regla que ya rige las respuestas de Comunicaciones.

### Confirmaciones
Solo para acciones destructivas o irreversibles en la práctica (eliminar, dar de baja, sobrescribir). Nunca para guardar un formulario normal — eso añade fricción sin proteger nada.

### Toasts
Esquina superior derecha, auto-descartables a los 5s (excepto errores, que requieren descarte manual). Un toast con acción (p. ej. "Deshacer", Fase D) vive 8s — el usuario necesita un instante extra para decidir, no solo para leer. Un toast por acción, nunca apilar más de 3 visibles simultáneamente. Siempre con icono + color semántico (éxito=verde, error=rojo, info=azul).

### Revisión IA (Fase D)
En `/documentos/revision-ia`, cada fila tiene dos acciones independientes, nunca confundidas entre sí: "Marcar como revisado" (`ResolverRevisionIaDocumentoCommand`) cierra el aviso sin tocar el Documento — para cuando el Gestor ya corrigió el dato a mano por la vía normal; "Aceptar detección" (`AplicarDeteccionIaDocumentoCommand`, solo visible si la IA detectó una fecha de emisión) renueva el Documento directamente con lo que la IA leyó. "Confirmar todos los ≥85%" en la cabecera aplica la segunda acción en lote a las revisiones con confianza alta y fecha detectada — el umbral es el mismo verde del badge de confianza, ninguna revisión se confirma en lote si no cumple ambas condiciones.

### Atajos de teclado (Fase D)
Tres capas, sin solaparse: `Ctrl/Cmd+K` abre el buscador global desde cualquier pantalla; `g` + una letra (`c`/`e`/`t`/`d`/`a`/`b`) navega a Clientes/Empresas/Trabajadores/Documentos/Asignaciones/Bandeja, con una ventana de menos de un segundo entre las dos teclas; `n` añade `?accion=crear` a la página actual si esa página soporta creación rápida (Clientes/Empresas/Centros/Trabajadores/Documentos), y no hace nada en las que no; `j`/`k`/`x`/`Enter` navegan una lista concreta (ya documentado en cada patrón de arriba). `?` abre un chuleta con los tres. Todos ignoran el evento si el foco está en un campo de texto, para no interceptar mientras el usuario escribe.

### Errores
Ver tabla de microcopy arriba. Errores de validación de formulario aparecen inline, junto al campo, en el momento en que el usuario sale del campo (no solo al enviar).

## Estados obligatorios por página

Toda página/vista de datos contempla, sin excepción:

| Estado | Qué se muestra |
|---|---|
| **Loading** | Skeleton con la forma real del contenido final (no un spinner genérico centrado) |
| **Empty** | Ilustración/ícono simple + mensaje contextual + acción primaria ("Aún no hay clientes. Crea el primero.") |
| **Offline** | Banner persistente indicando pérdida de conexión, reintento automático |
| **Error** | Mensaje en español + botón "Reintentar" |
| **Forbidden** | Mensaje claro de falta de permiso, nunca una pantalla en blanco o un 403 crudo |
| **Success** | El propio contenido cargado correctamente |

## Objetivo de navegación

Cualquier dato debe alcanzarse en ≤3 clics desde el Dashboard: (1) entrar al módulo o usar el buscador global, (2) localizar el registro en la tabla/resultado, (3) abrir el detalle. Si un flujo real requiere más pasos, es una señal de que falta un atajo (buscador global, filtro guardado, o enlace cruzado entre entidades relacionadas) — no se acepta como "así es como es".
