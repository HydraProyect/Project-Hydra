# Blueprint — Centro 360

**Superficie**: `/centros` · **Arquetipo**: Entity Workspace (`03` § 1.2)

**Estado**: Blueprint retroactivo · **Implementado hasta**: la superficie **existe y está en
producción**. Las divergencias con esta especificación están listadas en § 8 y son reales, no
aspiraciones pendientes de redacción.

**Por qué existe este documento**: Centro 360 es el **patrón maestro** del arquetipo (DDL-005).
Este blueprint es la primera prueba de integración de la normativa: si `01`–`08` no bastan para
especificar la superficie que los inspiró, es que falta algo en ellos.

---

## 1. Qué resuelve

Responde, en el orden de `01` § 3, todo lo que un gestor necesita saber de un centro **sin salir
de la pantalla**:

1. **¿Qué está pasando?** — cumplimiento del centro, visitas programadas, estado agregado.
2. **¿Qué requiere atención?** — quién tiene documentación vencida, faltante o en riesgo, y quién
   tiene el acceso bloqueado.
3. **¿Qué puedo hacer?** — gestionar el documento concreto, asignar o dar de baja trabajadores,
   pedir prioridad, editar el centro.
4. **¿Qué datos consulto?** — el tercer nivel y el Context Panel del centro.

Absorbió `/asignaciones`, que dejó de existir como página (`03` § 3.3).

## 2. Contratos que hereda

No se repiten aquí. Si algo de esta lista se incumple, el fallo es de la superficie, no del
contrato:

| Aspecto | Contrato |
|---|---|
| Anatomía, niveles, ámbitos, recuentos | `05` § 2 |
| Composición con Context Panel y overlays | `05` § 7 |
| Lista, filtros, orden, paginación, selección, export | `04` § 3 |
| Acciones sobre entidades y edición in situ | `04` § 2 |
| Badges, recuentos y ventana de contexto | `04` § 4 |
| Estados obligatorios | `04` § 6 |
| Color, superficies, tipografía | `02`, `06` |
| Movimiento | `07` |
| Componentes | `08` § 4 |

## 3. Anatomía

### 3.1 Cabecera y barra de trabajo

```
Título de la superficie                                    [+ Nuevo centro]
────────────────────────────────────────────────────────────────────────────
[buscar]  [Estado ▾]  [Cumplimiento ↑ ▾]  [Selección múltiple] [Expandir todos] [Exportar]
```

El orden por **cumplimiento** existe para atacar los peores centros sin depender de que haya una
visita próxima (`04` § 3.3).

### 3.2 Nivel 1 — el centro

Una fila por centro, con las ranuras fijas de `05` § 2.6:

```
▸  ◍ 41%   Planta Sagunto                     [3] [1] [12/08–14/08]   Detalles
           Iberojet S.L. · Cliente: Grupo Sagunto Industrial
```

- **Anillo de cumplimiento**: fracción de pares sujeto × documento exigido. **No se pinta si el
  centro no exige nada** — "sin requisitos" no es 0 % (`08` § 4.4).
- **Recuentos**: solo el número, con su ventana de contexto obligatoria. Agregan **los dos
  ámbitos** y el desglose declara cuántos son de cada uno (DDL-031, DDL-047).
- **Visitas**: recuento y detalle por visita. **Nunca un rango fusionado** (`05` § 2.5).
- **Detalles** abre el Context Panel del centro (§ 5).

### 3.3 Nivel 2 — los sujetos

Dos ámbitos declarados y separados (`05` § 2.3):

**Ámbito Empresa** — aparece **solo cuando tiene documentación con incidencia**. Lista únicamente
lo que hay que resolver, y **lo declara** con una nota; el resto se consulta desde "Detalles" de
la empresa. Sin esa nota, la ausencia se leería como inexistencia.

**Ámbito Trabajadores** — todos los que tienen asignación activa en el centro, con su fracción de
cumplimiento y sus recuentos. Se muestra completo: el centro es donde se opera su documentación.

Cada fila de sujeto tiene **acción propia** ("Detalles"), atenuada cuando no requiere intervención
(`04` § 2.5).

### 3.4 Nivel 3 — lo exigido a cada sujeto

Tabla de columnas fijas:

| Documento | Estado | Vigencia | Acción |
|---|---|---|---|

- El **estado** usa el léxico cerrado; "Riesgo en visita" es modificador, no estado (DDL-039,
  DDL-052).
- La **vigencia** lleva la marca de procedencia cuando el dato lo leyó el sistema (`04` § 4.3).
- La **acción** es siempre "Gestionar", atenuada cuando el documento está al día (`04` § 2.4,
  § 2.5).

**No hay cuarto nivel**: profundizar más se hace navegando (`05` § 2.2).

## 4. Reserva vigente sobre el nivel 3

DDL-005 mantiene abierta la validación del tercer nivel **con volúmenes reales de producción**.
Si la densidad no lo sostiene, la alternativa ya identificada es degradarlo a resumen por sujeto
con enlace a su ficha.

**Esta reserva es parte del blueprint**, no una nota al margen: la superficie se considera
validada cuando esa comprobación se haya hecho con datos reales, no con los de demostración.

## 5. Context Panel del centro

"Detalles" abre el panel con seis secciones: **Información · Requisitos del Centro ·
Trabajadores · Vehículos · Plataforma · Historial**.

- La edición ocurre **in situ** en Información; el overlay lateral queda solo para el alta
  (`04` § 2.2).
- **Plataforma** lista los N accesos del centro, cada uno con su proveedor, propósito y
  credencial, y uno marcado como principal.
- Cambiar de sección **no** empuja la pila de navegación (`05` § 3.4).

## 6. Datos

**Consulta**: centros paginados con su cumplimiento · próxima visita por centro, en lote por
página y nunca por fila · documentación por centro y asignación · documentos que faltarían para
una asignación · trabajadores de una visita sin asignación activa · selectores de cliente,
empresa y trabajador.

**Ejecuta**: crear centro · editar · eliminar (individual y en lote) · restaurar · crear
asignación individual y en lote · dar de baja asignaciones · gestionar canales de plataforma.

**Reglas heredadas del repositorio** (`CLAUDE.md`), no de la normativa de diseño: los comandos de
edición llevan la versión que vio el usuario; los identificadores ajenos se cargan con el filtro
de tenant activo; ninguna consulta usa SQL crudo.

## 7. Estados de la superficie

| Estado | Qué muestra |
|---|---|
| Cargando | Esqueleto con la forma de la lista |
| Sin centros | Causa y acción primaria de alta |
| **Sin cartera asignada** | Se declara explícitamente. **Un cero no se presenta como éxito** (`04` § 6) |
| Sin requisitos en un centro | El anillo no se pinta; el badge lo dice |
| Sin permiso · error · sin conexión | Según `04` § 6 |

## 8. Divergencias con el código actual

Honestas y verificadas el 2026-08-08. Ninguna es un fallo funcional; todas son deuda respecto a
la normativa recién cerrada:

| # | Divergencia | Origen | Impacto | Estado (2026-08-09) |
|---|---|---|---|---|
| 1 | Los recuentos de fila **no abren ventana de contexto** | `04` § 4.2 (DDL-033) — norma nueva | Alto: hoy el color es el único portador en los badges numéricos | **Parcialmente cerrada** (PR #161): `VentanaContexto.razor` existe y se aplica a los recuentos de fila de `Centros.razor`. No se ha extendido todavía a todas las superficies que `08` § 6 exige. |
| 2 | La marca de procedencia **no existe como componente** | `08` § 6 (DDL-032) | Alto: es la pieza que más superficies necesitan | **Parcialmente cerrada** (PR #161): `MarcaProcedencia.razor` existe (envuelve `VentanaContexto` con icono cian, sin etiqueta de texto). Aplicado donde lo consume el trabajo de Centro 360; no auditado su uso en el resto de superficies que `08` § 6 cubre. |
| 3 | El ámbito **Empresa no está separado** dentro del centro | `05` § 2.3 (DDL-031) | Medio: el recuento agrega sin declarar de quién | **Cerrada** (PR #161, OD-13): bloque `fila-sujeto-ambito` separa Empresa de Trabajadores dentro del acordeón del centro. |
| 4 | Las visitas múltiples **no se representan** con recuento y detalle | `05` § 2.5 (DDL-035) | Bajo hasta que aparezca el caso real | **Cerrada** (PR #161): `ObtenerProximaVisitaPorCentroQuery` deja de colapsar a una sola visita por centro; la fila muestra recuento + `VentanaContexto` cuando hay más de una. |
| 5 | El **orden por cumplimiento** no existe | `04` § 3.3 (DDL-036) | Medio | **Cerrada** (PR #161): ordenación por `CumplimientoPorcentaje` añadida (DB-side e in-memory), "sin requisitos" siempre al final en ambos sentidos. |
| 6 | Las tarjetas y filas **usan sombra** | `06` § 5 (DDL-013) | Alto en superficie de código; bajo en riesgo | Sin cambios en esta ronda — no auditada de nuevo. |
| 7 | El **ripple** se aplica a todos los botones, incluidos los de fila | `07` § 5 (DDL-045) | Bajo | Sin cambios en esta ronda — no auditada de nuevo. |
| 8 | El texto secundario usa el valor que **incumple contraste** | `06` § 2.5 (DDL-029) | Alto: es un incumplimiento objetivo | Sin cambios en esta ronda — no auditada de nuevo. |
| 9 | El Context Panel **no tiene deep-link ni cierre automático** | `05` § 3.6 | Medio: impide enlazar directamente a una sección | Sigue abierta — `05` § 3.6 la declara explícitamente como hueco real, no dar por hecha. |

**Orden recomendado de corrección**: 8 (incumplimiento objetivo, acotado) → completar 2 y 1 en el
resto de superficies → 6 caso por caso → 7 → 9 cuando toque.

## 9. Decisiones que gobiernan esta superficie

| Decisión | Qué fija |
|---|---|
| DDL-005 | Es el patrón maestro, con la reserva del nivel 3 (§ 4) |
| DDL-031 | Los dos ámbitos y su asimetría (§ 3.3) |
| DDL-047 · DDL-033 | Recuentos y desglose obligatorio (§ 3.2) |
| DDL-034 | Tercer nivel expandido con columnas fijas (§ 3.4) |
| DDL-035 | Las visitas no se fusionan (§ 3.2) |
| DDL-039 · DDL-052 | Léxico de estados y "Riesgo en visita" como modificador (§ 3.4) |
| DDL-036 | Acción única, acción atenuada, orden por cumplimiento (§ 3, § 8) |
| DDL-041 | Densidad compacta única |
| DDL-032 | Marca de procedencia (§ 3.4) |

## 10. Qué no define este blueprint

El comportamiento genérico de sus piezas (vive en `04`, `05`, `08`), los valores visuales (`06`),
el movimiento (`07`) y las superficies vecinas: `/empresas` reutiliza este patrón y merecerá su
propio blueprint si diverge; el Context Panel del centro es una instancia del patrón general, no
una superficie aparte.
