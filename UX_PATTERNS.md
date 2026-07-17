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
Botón primario arriba a la derecha de la tabla/lista ("+ Nuevo cliente"). Abre formulario en **Drawer** lateral (no navega a otra página) para acciones simples de un solo agregado; usa página completa solo cuando el formulario tiene múltiples secciones/pestañas (p. ej. Trabajador con documentos y asignaciones). Al guardar: toast de éxito + la tabla se actualiza sin recargar página + el drawer se cierra.

### Editar
Mismo formulario que crear, precargado. Se accede desde la fila de la tabla (icono de lápiz o click en la fila) o desde el detalle. Autoguardado **no** se usa en formularios con relaciones de negocio críticas (documentos, vigencias) — se guarda explícitamente para que el usuario tenga control sobre cuándo un cambio de vigencia se aplica. Sí se permite autoguardado en campos de notas/comentarios libres.

### Eliminar
Siempre **soft delete**. Confirmación obligatoria vía **Dialog** modal (no un `confirm()` de navegador): título claro ("¿Eliminar a Juan Pérez?"), cuerpo con la consecuencia real ("Se ocultará de las listas activas. Podrás recuperarlo desde Auditoría."), botón destructivo en rojo con la acción explícita ("Eliminar"), botón secundario "Cancelar". Nunca "¿Estás seguro?" como único texto.

### Duplicar
Disponible en entidades con mucha repetición estructural (Centro, RequisitoDocumental). Abre el formulario de creación precargado con los datos del original, campo de nombre vacío/resaltado para forzar que el usuario lo revise antes de guardar.

### Buscar
Buscador global fijo en la barra superior (`Cmd/Ctrl+K`), busca por nombre/DNI/código across Clientes, Centros, Trabajadores. Resultados agrupados por tipo de entidad, con navegación por teclado. Este es el mecanismo que reemplaza la hoja "Filtros" manual del Excel — cumple el objetivo de "menos de tres clics": abrir buscador → escribir → clic en resultado.

### Filtrar
Panel de filtros junto a cada tabla, nunca oculto en un menú de tres puntos. Filtros activos se muestran como chips removibles sobre la tabla. Estado de filtros persiste en la URL (query string) para que se pueda compartir/recargar sin perder el contexto.

### Subir documentos
Zona de drag-and-drop + botón explícito "Seleccionar archivo", solo PDF, tamaño máximo indicado antes de intentar subir. Barra de progreso durante la subida. Tras subir: vista previa inmediata (no hace falta recargar para confirmar que se adjuntó bien).

### Descargar documentos
Un clic descarga directo; para PDFs, opción adicional de "Ver" que abre el visor integrado (PDF Preview) sin salir de la aplicación.

### Cambiar estado
El estado de un Documento (vigente/próximo/urgente/vencido) **nunca se edita manualmente** — es siempre calculado (ver `DATABASE.md`). Lo único editable es la fecha de emisión, de la que se deriva el estado. Esto evita la inconsistencia que hoy tiene el Excel entre la celda "Estado" y la fórmula real.

### Asignar trabajador a centro / cliente
Desde el detalle del Trabajador o del Centro: selector con búsqueda, fecha de alta obligatoria (default hoy), fecha de baja opcional. Al asignar, si el Centro tiene `RequisitoDocumental` con `BloqueaAcceso = true` y el trabajador no cumple ese requisito, se muestra una advertencia visible (no bloqueante para el registro administrativo, sí bloqueante conceptualmente para el acceso físico) — replica el comportamiento real observado en el Excel ("el sistema BLOQUEA el acceso del trabajador").

### Confirmaciones
Solo para acciones destructivas o irreversibles en la práctica (eliminar, dar de baja, sobrescribir). Nunca para guardar un formulario normal — eso añade fricción sin proteger nada.

### Toasts
Esquina superior derecha, auto-descartables a los 5s (excepto errores, que requieren descarte manual). Un toast por acción, nunca apilar más de 3 visibles simultáneamente. Siempre con icono + color semántico (éxito=verde, error=rojo, info=azul).

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
