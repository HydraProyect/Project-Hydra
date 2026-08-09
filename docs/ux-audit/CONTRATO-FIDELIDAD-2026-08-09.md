# Contrato de Fidelidad de Diseño — Centro 360 / Dashboard / Comunicaciones

**Tipo**: Operativo — gobierna una fase de corrección, no es un documento normativo del reset (`01`–`08`).
**Estado**: Draft — pendiente de aprobación del propietario del producto antes de implementar nada de lo que describe.
**Fecha**: 2026-08-09.
**Origen**: auditoría de fidelidad mockup→implementación (misma fecha, ver `REGISTRO-MIGRACION-DS-2026-08-09.md` para el registro de ODM/OD de la ronda de migración de tokens que este documento NO reabre).

## 0. Por qué existe este documento

La auditoría de esta misma fecha encontró que Centro 360 y el Dashboard divergen visiblemente del lámina de referencia (`https://claude.ai/code/artifact/e3881951-9cd1-4807-822d-82240f572017`, "Hydra — Banco Visual · Ronda 2") **no por color ni tipografía** — la paleta real del lámina coincide casi exactamente con `tokens.css` — sino porque la composición del lámina no se propagó a los componentes Razor reales. Dos causas concretas, verificadas con evidencia (HTML del lámina fetcheado directamente, comparado línea a línea contra el código):

1. Un patrón de diseño ya construido para un nivel de una pantalla (badges de conteo + ventana de contexto en la fila de Centro) no se replicó en el nivel inmediato inferior (fila de Trabajador), que en su lugar usa un componente (`SeccionColapsable`) que impone una composición distinta.
2. Un componente compartido (`BarraHerramientasLista`) fragmenta en varias regiones un toolbar que el lámina especifica como una sola fila.

Regla de fondo para todo lo que sigue, y para cualquier trabajo futuro que parta de un mockup:

```
DDL / OD ratificado
        >
Component Contract (este documento, o su equivalente por pantalla)
        >
Mockup / lámina
        >
Implementación existente
```

**La implementación existente es fuente de funcionalidad, datos y lógica — nunca de composición visual**, salvo que un componente esté explícitamente marcado abajo como reutilizable para ese propósito. "Se parece a lo que ya existe" o "ya hay un componente que hace expand/collapse" no es motivo suficiente para reutilizarlo si su composición visual no coincide con el patrón exigido.

## 1. Centro 360

### 1.1 Patrones obligatorios (del lámina `c360-tpl` / `L3`)

| Patrón | Especificación del lámina |
|---|---|
| Fila de Centro | Grid de 5 columnas fijas: `14px(chev) 30px(anillo) minmax(0,1fr)(nombre) 200px(badges) 76px(acción)`, gap 10px |
| Badges de fila (Centro **y** Trabajador) | Grid de 3 ranuras fijas: `26px(vencidos) 26px(próximos) minmax(0,1fr)(estado o visita, mutuamente excluyentes)`, gap 5px. Cada badge de conteo abre `VentanaContexto` con el desglose |
| Fila de Trabajador | Mismo lenguaje que la fila de Centro: grid de 5 columnas `14px(chev) minmax(0,1fr)(nombre) 46px(fracción) 200px(badges) 76px(acción)`, gap 10px — **sin anillo de cumplimiento**, el lámina solo pone anillo a nivel de Centro |
| Tabla de documentos (4º nivel) | Grid de 4 columnas: `minmax(0,1fr)(documento) 168px(estado) minmax(0,150px)(vigencia) 76px(acción)` |
| Acción por fila de Centro | Una sola: "Detalles" |
| Toolbar | Una fila: buscar · filtro de estado · orden por cumplimiento · selección múltiple · expandir todos · exportar · nuevo centro |
| Ámbito Empresa dentro del centro | Bloque separado antes de "Trabajadores" (ya implementado correctamente, OD-13 cerrada — no tocar) |

### 1.2 Componentes prohibidos como base visual

| Componente | Motivo | Hallazgo de la auditoría |
|---|---|---|
| `SeccionColapsable` (para la fila de Trabajador) | Impone anillo de cumplimiento + un único badge de "peor estado" en vez del grid de 3 ranuras de conteo que usa la fila de Centro. La misma información queda representada con dos sistemas visuales distintos dentro de la misma pantalla | C-4 |
| `BarraHerramientasLista` (como región de toolbar separada) | Fragmenta en una fila propia (por debajo de la barra de filtros) algo que el lámina especifica como parte de una única fila de controles | C-1 |

**Aclaración explícita, para no sobrecorregir**: no está prohibido reutilizar la *funcionalidad* de estos componentes (el toggle de selección múltiple, el toggle de expandir-todos, el expand/collapse en sí). Está prohibido que su *composición visual actual* (dónde se dibujan, en qué fila, con qué agrupación) se herede sin cambios en la superficie corregida.

### 1.3 Componentes/lógica reutilizables sin restricción

- `VentanaContexto`, `MarcaProcedencia`, `Badge`, `AnilloCumplimiento`, `Boton` (variante Fantasma) — primitivas del catálogo (`08`), ya construidas para este propósito exacto.
- Toda la capa de datos: `ObtenerCentrosQuery`, `RecuentosCentroDto`, `ObtenerProximaVisitaPorCentroQuery`, `AcordeonAsignacionesCentro.razor.cs` (los métodos, no necesariamente el markup de la fila de Trabajador).
- El patrón `.enlace-nombre-fila` (nombre de fila clickeable) — ratificado, usado en 9+ archivos, sin relación con esta corrección.
- El grid de la tabla de documentos (4º nivel) — ya coincide estructuralmente con el lámina, confirmado en la auditoría (sin hallazgo).

### 1.4 Componentes a crear o rediseñar desde su composición

- **Fila de Trabajador**: nueva composición que reutilice el mismo grid de 3 ranuras de badges que ya existe para la fila de Centro (`.ranuras-recuento` o su equivalente extendido a 3 ranuras), en vez de partir de `SeccionColapsable`. El expand/collapse en sí (mostrar/ocultar la tabla de documentos) sí puede reutilizar el mecanismo de `SeccionColapsable` *a nivel de comportamiento*, pero la cabecera de la fila no puede heredar su composición visual.
- **Toolbar de Centros**: una sola región que agrupe buscar/filtro/orden/selección-múltiple/expandir-todos/exportar/nuevo, en vez de las 3 regiones actuales (cabecera de página / barra de filtros / `BarraHerramientasLista`).
- **`.tarjeta-fila-acordeon-indicadores`**: la columna de 240px con 2 ranuras (`.ranuras-recuento`) más un Badge de Estado siempre visible más un badge de visita adicional es la causa directa del wrap visible en pantalla (C-2, ver 1.6). Necesita rediseñarse como las 3 ranuras mutuamente excluyentes del lámina, no solo ensancharse.

### 1.5 Elementos provisionales — decisión pendiente antes de implementar

- **Variante A vs B del tercer nivel** (OD-12 del propio lámina): el código usa hoy la variante A (documentos expandidos con columnas fijas), pero el lámina dice literalmente *"la elección A vs B es tuya"* — nunca se cerró formalmente. **No conviene reescribir la fila de Trabajador (1.4) sin cerrar antes esta decisión**, para no rehacer el trabajo si la respuesta resulta ser B (resumen por trabajador + ficha).

### 1.6 Bug objetivo (no depende de ninguna decisión de diseño)

- **C-2**: la columna de indicadores de 240px (`.tarjeta-fila-acordeon-indicadores`) renderiza simultáneamente un Badge de Estado *siempre* más, condicionalmente, un badge de visita — el propio código real le pide más contenido del que su ancho fijo soporta, y provoca que el badge de visita caiga a una segunda línea (visible en captura de pantalla real: "Visita 15/08–16/08" bajo "Bloqueado"). Esto es corregible de forma acotada sin esperar a 1.4/1.5.

### 1.7 Tokens aplicables

Ninguno nuevo. Todo lo anterior se resuelve con los tokens ya existentes (`--space-*`, `--color-danger-*`, `--color-warning-*`, `--color-system-*`, `--radius-*`) y los componentes de `08`. Esta corrección no reabre `tokens.css`.

## 2. Dashboard / Operational Home

**Advertencia que gobierna toda esta sección**: el propio lámina declara la composición del Home como **provisional (OD-11)** — *"aquí solo se valida identidad, no canoniza la estructura"*. Nada de lo que sigue se implementa sin decisión previa; esta sección separa qué es qué, no autoriza construir nada.

### 2.1 Lo que el lámina sí valida como identidad (no como estructura)

- Iconografía obligatoria en KPIs (outline, sin relleno) — **ya implementado** (PR #161), correcto, no tocar.
- Color de agencia cian en los bloques de actividad del sistema — implementado donde existe ese bloque.
- Un botón primario azul por tarjeta de atención — patrón de agencia, válido independientemente de si se construye la sección o no.

### 2.2 Elementos que existen en el lámina pero no en el código — pendientes de decisión, no de implementación

| Elemento | Lámina | Código actual | Clasificación |
|---|---|---|---|
| KPI "Cumplimiento global" (anillo grande) | Pieza central de `.kpis` | No existe | Funcionalidad ausente — **candidata a decisión**, ver 2.3 |
| Tarjetas de atención con botón de acción por ítem | `.att`: icono + texto + un botón primario | Tabla plana sin acción por fila | Patrón de composición ausente — **candidata a decisión** |
| "Próximamente" (visitas) | Sección completa con badges de riesgo | No existe | Funcionalidad ausente — cubierta por OD-11, no se construye sin decisión de producto |
| "Hydra está trabajando" (actividad del sistema) | Sección completa con pulso en vivo | No existe | Funcionalidad ausente — cubierta por OD-11, requiere además una fuente de datos real que hoy no existe |

### 2.3 Qué no haría este contrato todavía

No autoriza construir ninguna de las filas de 2.2. Antes de tocar el Home hace falta que el propietario del producto separe explícitamente, ítem por ítem: qué es composición provisional (OD-11, no se construye), qué es identidad ya validada (se puede propagar) y qué es funcionalidad nueva de producto (backlog, decisión aparte, posiblemente requiere una fuente de datos que hoy no existe — p. ej. "Hydra está trabajando" necesita un feed real de actividad del sistema).

## 3. Comunicaciones

Sin cambios respecto al backlog ya registrado en la ronda anterior de esta misma sesión: Action Center, sugerencia de respuesta IA, confianza por campo y chips de Centro/Responsable/Prioridad/nota interna siguen sin implementar y siguen siendo decisión de producto, no defecto de CSS. Este contrato no añade nada nuevo aquí — la auditoría no pudo verificar la superficie con una conversación abierta (la captura mostraba el estado vacío).

## 4. Método de verificación

Ninguna pantalla de esta corrección se da por cerrada solo con build+tests en verde. Cuatro puertas, en orden:

**Gate A — Arquitectura**
¿Se usó algún componente de la lista de 1.2 como base visual? ¿Los componentes nuevos necesarios (1.4) se crearon en vez de adaptarse desde uno prohibido? ¿Lo único reutilizado de un componente prohibido fue su *comportamiento*, no su *composición*?

**Gate B — Composición**
¿La jerarquía, el orden, las agrupaciones, la densidad y la distribución horizontal coinciden con la especificación de 1.1? (No con el mockup completo — con la especificación de este documento, que ya decidió qué del mockup aplica.)

**Gate C — Design System**
Tokens, tipografía, color, estados, spacing, motion, foco — contra `06`/`07`/`08`, no contra el lámina (el lámina no es la fuente de verdad de tokens, `tokens.css` lo es).

**Gate D — Visual**
Comparación real navegador vs. lámina, con captura de pantalla — no descripción textual. **Si no se puede obtener una captura fiable en la sesión, el estado del trabajo es `BLOCKED — visual verification unavailable`, no `done`.** Nota de esta sesión: el panel de navegador no compositó frames en este entorno durante la auditoría (limitación de sesión, no del código) — cualquier implementación de este contrato debe volver a intentar Gate D en un entorno donde el panel funcione antes de cerrarse.

## 5. Reglas de proceso para quien implemente este contrato

- **Antes de escribir código**, producir una tabla de trazabilidad elemento del diseño → componente previsto → componente existente evaluado → decisión (reutilizar / no reutilizar / crear nuevo), igual que la de las secciones 1.2–1.4 de este documento pero a nivel de implementación.
- **Auditoría negativa de reutilización legacy** antes de dar por cerrada la tarea: buscar deliberadamente (grep de nombres + imports + composición, no solo nombres literales) cualquier camino por el que se haya reutilizado composición de `SeccionColapsable`/`BarraHerramientasLista`/cualquier otro componente listado en 1.2 en la superficie corregida. Si aparece uno: la tarea no está terminada.
- Esta auditoría cubre también la **reutilización indirecta**: wrappers, herencia, composición de componentes, o extracción de markup/CSS de un componente de 1.2 hacia un componente nuevo que reproduzca su misma estructura visual. Que el nombre del componente prohibido ya no aparezca en el código (grep en cero) no es evidencia de que Gate A pase — hay que verificar que el nuevo componente no sea ese mismo componente con otro nombre.
- La similitud semántica no autoriza la reutilización visual: que un componente ya haga "expandir/colapsar" no lo habilita para representar la fila de Trabajador si su composición no coincide con el patrón exigido en 1.1.
- Este documento no se edita para cambiar lo que dice — si una decisión posterior lo sustituye (p. ej. se cierra OD-12 A vs B, o se decide qué partes del Home se construyen), se referencia esa decisión aquí y se actualiza la sección afectada con una nota de fecha, sin borrar el razonamiento anterior.

## 6. Fuera de alcance de este contrato

- `tokens.css` y cualquier token del sistema — la auditoría confirmó que esa capa funciona correctamente, no se reabre.
- Cualquier funcionalidad nueva de producto (Action Center, feed de actividad del sistema, KPI global) — quedan como decisiones de producto separadas, listadas pero no autorizadas aquí.
- La migración de diseño cerrada en PRs #160–#164 — este documento es la fase siguiente, no una reapertura de esa.
