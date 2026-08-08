# 05 — Workspace Patterns

**Estado**: Normativo · **Implementado hasta**: parcialmente. **Entity Workspace** existe en
Centro y Empresa; **Context Panel** existe con huecos declarados (§ 3.6); **Unified Timeline** y
**Action Center** existen solo en Comunicaciones, este último con una sola acción; **Flow
Surface** existe en varias instancias sin estar declarado como patrón. Nada de lo que sigue debe
leerse como descripción del estado del software.

**Autoridad**: define **cómo se estructura y se compone una superficie de trabajo**. No define
cómo se comporta cada interacción (`04`), ni dónde vive cada pantalla (`03`), ni con qué tokens
o componentes se construye (`06`/`08`), ni cómo se mueve (`07`).

| Pregunta | Documento |
|---|---|
| ¿Qué tipo de superficie es y dónde encaja? | `03` |
| ¿Cómo se comporta la interacción? | `04` |
| ¿Cómo se estructura y compone la superficie? | **`05`** (este) |
| ¿Cómo se mueve? | `07` |
| ¿Cómo es *esta* pantalla concreta? | `docs/blueprints/` |

**Límite explícito**: este documento define **patrones**. La especificación de una superficie
concreta —Retención, Claves API, el Communication Workspace— vive en su blueprint. `05` dice qué
es un Flow Surface; el blueprint dice qué pasos tiene *ese* flujo.

---

## 1. Qué resuelve toda superficie de trabajo

Cualquiera de los patrones de este documento debe cumplir cuatro condiciones. Si una superficie
falla alguna, no está terminada:

1. **El contexto es visible sin buscarlo.** Sobre qué entidad, qué cartera y qué periodo se está
   trabajando se lee sin abrir nada.
2. **El estado precede al detalle.** Primero qué tal va esto, después los datos que lo componen.
3. **La acción vive donde aparece el problema.** Si una fila revela algo que hay que resolver,
   la acción que lo resuelve está en esa fila, no en otra pantalla.
4. **La procedencia es visible.** Todo dato que no introdujo una persona lleva su marca, y esa
   marca abre su detalle bajo demanda (`01` § 5.5).

## 2. Entity Workspace

La superficie desde la que se **opera** una entidad compleja. Patrón maestro: **Centro 360**
(DDL-005). Entidades con Entity Workspace: Centro, Empresa y Trabajador (DDL-042).

### 2.1 Anatomía

```
Cabecera de entidad     identidad · estado agregado · acciones sobre el conjunto
────────────────────────────────────────────────────────────────────────────
Barra de trabajo        búsqueda · filtros · modos de la lista · acción de alta
────────────────────────────────────────────────────────────────────────────
Cuerpo operativo        filas de sujeto, expandibles hacia el detalle
────────────────────────────────────────────────────────────────────────────
Pie                     paginación y totales
```

La cabecera responde *"¿qué tal va esto?"* antes de que el usuario lea una sola fila: estado
agregado, indicador de cumplimiento y lo que esté programado.

### 2.2 Regla de profundidad: tres niveles, no más

```
Nivel 1   la entidad          (un centro)
Nivel 2   los sujetos         (empresa · trabajadores con actividad)
Nivel 3   lo exigido a cada sujeto   (documentos, con su estado)
```

**Un cuarto nivel no se anida: se navega.** Si hace falta profundizar más, se abre Context Panel
o se va al Entity Workspace de esa entidad. Anidar sin límite convierte la superficie en un árbol
que nadie puede recorrer.

Los niveles 2 y 3 se cargan **de forma perezosa, al expandir**: una superficie no paga por lo que
no se ve.

> **Reserva vigente (DDL-005)**: el comportamiento del nivel 3 con volúmenes reales de producción
> está pendiente de validar. Si la densidad no lo sostiene, la alternativa ya identificada es
> degradarlo a resumen por sujeto con enlace a su ficha.

### 2.3 Ámbitos dentro de la entidad (DDL-031)

Cuando una entidad agrupa sujetos de **naturaleza distinta**, se separan en bloques declarados,
no se mezclan en una lista única.

En Centro conviven dos ámbitos: **Empresa** y **Trabajadores**. Reglas:

- El bloque de un ámbito **secundario** aparece **solo cuando tiene algo que resolver**. Una fila
  permanente que casi siempre está al día es ruido.
- Cuando aparece parcialmente, **lo dice**: si un bloque lista solo lo que tiene incidencia, debe
  declararlo, o la ausencia se leerá como inexistencia.
- El ámbito que **se opera desde aquí** se muestra completo; el que tiene superficie propia se
  muestra por excepción.

### 2.4 Recuentos y agregación (DDL-047)

**Cada nivel suma exactamente el ámbito que declara.** Centro = sus ámbitos; sujeto = lo que se
le exige en este contexto.

Todo recuento **abre su desglose** (DDL-033): qué elementos lo componen y de qué ámbito. Un
número que no se puede desglosar es un número en el que no se puede confiar.

Regla derivada: si dos niveles muestran cifras del mismo dato, **deben poder cuadrar**. Si no
cuadran, es que uno de los dos declara mal su ámbito.

### 2.5 Varios eventos del mismo tipo no se fusionan (DDL-035)

Cuando una entidad tiene **varios eventos programados** del mismo tipo, la superficie muestra el
**recuento** y el detalle de cada uno por separado. **Nunca se fusionan en un rango.**

Dos visitas del 21 al 23 y del 29 al 30 **no son** "del 21 al 30": ese rango afirma una presencia
que no existe entre medias, y los cálculos que dependen de la ventana —qué caduca durante la
visita— darían falsos positivos. **La unión de dos rangos no es el rango de la unión.**

Regla general: una superficie puede resumir, pero no puede **inventar continuidad** que el dato
no tiene.

### 2.6 Estructura de la lista operativa

- **Columnas fijas** en los niveles con datos comparables. Una lista donde los indicadores bailan
  de posición obliga a releer cada fila.
- **Ranuras por tipo de indicador**: cada tipo cae siempre en la misma vertical, esté presente o
  no en esa fila.
- **Acción propia por fila**, en todos los niveles. Una fila que muestra un problema y no ofrece
  cómo resolverlo obliga a salir de la superficie.
- **Densidad compacta única** (DDL-041), sin modos por usuario.

### 2.7 Edición

La edición ocurre **in situ dentro de la superficie**, no en un formulario aparte. El formulario
en overlay queda para el **alta**.

Los campos que forman la identidad de la entidad —los que se fijan al crear— permanecen visibles
en modo lectura dentro del mismo bloque; no desaparecen ni se vuelven editables.

## 3. Context Panel

La superficie para **consultar sin abandonar el trabajo**. Se abre desde cualquier lista o
referencia y devuelve al usuario exactamente donde estaba.

### 3.1 Regla que lo separa del Entity Workspace (DDL-006)

> **Panel para consultar; workspace para operar.**

Ante la duda: si la tarea implica varias acciones encadenadas sobre la misma entidad, es
workspace. Si es responder una pregunta y volver, es panel.

### 3.2 Instancia única

Existe **una sola instancia** en todo el árbol de la aplicación, montada en el layout raíz. No
es una convención: es la garantía estructural de que **nunca hay paneles anidados**. Ninguna
pantalla instancia el suyo.

### 3.3 La pila es el camino recorrido, no la jerarquía

El panel apila **por dónde ha pasado el usuario**, no cómo se relacionan los datos. Es la única
estructura correcta para un dominio que no es un árbol (`03` § 6).

Si el usuario vuelve a una entidad que ya está en la pila, **se trunca y se reutiliza el nivel**
en lugar de duplicarlo: un recorrido en ciclo no puede hacer crecer la pila sin límite.

### 3.4 Dos ejes independientes

- **Pestañas** (lateral, dentro de una entidad): cambiar de pestaña **no** empuja la pila. No es
  navegar, es mirar otra faceta de lo mismo.
- **Navegación** (profundidad, entre entidades): siempre empuja la pila y **reinicia la pestaña
  activa**, salvo que el origen pida explícitamente una pestaña concreta.

Confundir los dos ejes es lo que produce breadcrumbs absurdos con quince entradas del mismo
registro.

### 3.5 Qué puede y qué no puede hacer un panel

| Puede | No puede |
|---|---|
| Abrir un overlay de confirmación o de formulario | Abrir otro Context Panel |
| Navegar a una entidad relacionada (empujando la pila) | Sustituir a un Entity Workspace para operar en serie |
| Ofrecer las acciones de la entidad que muestra | Convertirse en una página a base de crecer |

**Cada pestaña revalida el alcance de datos al cargar.** El panel puede aterrizar en una entidad
fuera de la cartera del usuario si se llegó desde una lista de alcance más amplio; si no es
visible, se muestra el estado de sin-acceso, nunca los datos.

### 3.6 Deuda declarada

Tres piezas **decididas y no construidas**. No se dan por hechas:

- **No serializa su estado en la URL**: sin deep-link, y recargar lo pierde entero.
- **No se cierra al navegar por el menú principal**, pese a ser decisión cerrada desde
  2026-07-25 — depende del mismo mecanismo de URL.
- **Teclado incompleto**: solo `Escape` está implementado.

Consecuencia: ningún patrón nuevo puede apoyarse todavía en enlaces que aterricen directamente
en un panel.

## 4. Action Center

El patrón que convierte lo que Hydra detecta en **decisiones que una persona toma**.

### 4.1 Es un panel de decisiones, no de información

Si una tarjeta no puede terminar en Confirmar, Editar o Descartar, no pertenece al Action
Center: pertenece al contexto.

### 4.2 Anatomía de una propuesta

```
Qué propone Hydra            (acción concreta, no observación)
Con qué confianza            (por propuesta y, si aplica, por campo)
Sobre qué datos              (los extraídos, verificables)
Confirmar · Editar · Descartar
```

- **La confianza es visible siempre**, y los campos por debajo del umbral alto se marcan para
  verificación en vez de presentarse como hechos.
- **Editar no es una excepción**: es el camino normal cuando la propuesta es casi correcta.
- **Descartar deja rastro.** Una propuesta rechazada es información sobre la calidad del sistema.

### 4.3 Agencia visual

La propuesta lleva marca de sistema; la confirmación es acción humana (DDL-009). Es el ejemplo
canónico de la regla de agencia y no se altera: **quien propone y quien decide deben distinguirse
sin leer**.

### 4.4 La IA nunca ejecuta

Sin confirmación no hay efecto (`01` § 5.4). "Automáticamente" en la interfaz significa *con todo
preparado, a un clic*.

Cada acción confirmada desde una propuesta **deja su evento** en la historia de la situación
(§ 5), para que la trazabilidad no dependa de recordar quién pulsó qué.

### 4.5 Contrato no congelado (DDL-007)

El **patrón** es normativo desde ya. El **contrato técnico reutilizable** no se congela hasta que
exista un segundo consumidor real. Hasta entonces, cada superficie que lo use sigue esta
anatomía sin extraer una abstracción compartida.

## 5. Unified Timeline

El patrón que reconstruye **la historia de una situación**, no de una entidad.

### 5.1 Una sola cronología, entradas tipadas

Todo lo que le ocurrió a una situación va en la misma línea temporal, ordenado por tiempo y con
el tipo declarado: mensajes entrantes y salientes, eventos del sistema, extracciones y
sugerencias de la máquina, notas internas.

El canal, el origen o el autor son **atributos de la entrada**, no ejes de separación. Partir la
historia por canal obliga al usuario a reconstruir mentalmente el orden real.

### 5.2 Reglas estructurales

- **Separadores por fecha**, no por tipo.
- **Los eventos del sistema son entradas de primera clase**, con enlace al módulo donde
  ocurrieron.
- **Procedencia marcada** en toda entrada generada por el sistema (DDL-032).
- **Filtros por tipo**, nunca vistas separadas: filtrar es reducir la misma línea, no cambiar de
  pantalla.
- **El proveedor o la conexión de origen no se repite por entrada** (DDL-051): es atributo de la
  conexión, no del mensaje. Vive en la cabecera de la situación y en el selector de respuesta.
  Repetirlo en cada línea son dos marcas por entrada en la superficie más densa del módulo.

### 5.3 Reutilización

El patrón no pertenece a Comunicaciones. Es aplicable a cualquier superficie que necesite
responder *"¿qué ha pasado aquí y en qué orden?"* — historial de entidad, actividad, auditoría.
Su primera implementación no debe asumir nada exclusivo de un módulo.

## 6. Flow Surface

El patrón para un **proceso secuencial con resultado**.

### 6.1 Estructura

Pasos declarados con progreso visible, validación al final de cada paso y un resultado explícito.
El usuario sabe siempre en qué paso está, cuántos quedan y qué ocurrirá al terminar.

### 6.2 Reglas

- **Guardado incremental, o declaración explícita de que no lo hay.** Nunca una transacción larga
  que se pierda a medio camino sin avisar.
- **Abandonable sin dejar datos a medias.**
- **El punto de no retorno se señala antes de cruzarlo**, no después. Si el paso final es
  irreversible —destruir datos, revelar un secreto una sola vez—, el flujo lo dice y pide
  confirmación con la consecuencia escrita.
- **Ensayo previo cuando el efecto es masivo**: si el resultado afecta a muchos registros, el
  flujo muestra qué va a pasar antes de hacerlo.

### 6.3 Instancias conocidas

Alta guiada, importaciones, flujos del Action Center, **Retención de datos** y **Claves API**
(reclasificadas en DDL-053). **La especificación de cada una vive en su blueprint**, no aquí.

## 7. Reglas de composición

Qué superficie puede invocar a cuál. Esta matriz es la que impide que la aplicación se convierta
en capas apiladas sin salida:

| Desde ↓ / Abre → | Entity Workspace | Context Panel | Action Center | Flow Surface | Overlay |
|---|---|---|---|---|---|
| **Operational Home** | sí (navega) | sí | — | sí | sí |
| **Entity Workspace** | sí (navega) | sí | sí (embebido) | sí | sí |
| **Context Panel** | sí (navega y cierra) | **no** | — | sí | sí |
| **Action Center** | — | — | — | sí | sí |
| **Flow Surface** | — | — | — | **no** | sí |

Reglas que se derivan:

1. **Nunca un panel dentro de un panel** ni un flujo dentro de un flujo. Dos niveles de
   overlay es el máximo, y el segundo solo puede ser una confirmación.
2. **Navegar a un Entity Workspace cierra el panel**: se ha dejado de consultar para pasar a
   operar.
3. **El Action Center vive embebido** en la superficie que lo necesita; no es una pantalla.
4. **Un overlay siempre se cierra solo** y devuelve a quien lo abrió, sin alterar la pila de
   navegación.

## 8. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-005 · DDL-042 | Entity Workspace y sus entidades (§ 2) |
| DDL-031 | Ámbitos separados dentro de una entidad (§ 2.3) |
| DDL-047 · DDL-033 | Recuentos y su desglose (§ 2.4) |
| DDL-041 | Densidad única (§ 2.6) |
| DDL-006 · DDL-044 | Context Panel: frontera y nombre (§ 3) |
| DDL-007 | Action Center como patrón, contrato sin congelar (§ 4) |
| DDL-009 · DDL-032 | Agencia y procedencia (§ 4.3, § 5.2) |
| DDL-046 | El Home consume la cola, no la sustituye (§ 7) |
| DDL-053 | Retención y Claves API son Flow Surface (§ 6.3) |
| DDL-034 | Estructura del tercer nivel (§ 2.2) |
| DDL-035 | Los eventos múltiples no se fusionan en un rango (§ 2.5) |
| DDL-051 | El origen de la conexión no se repite por entrada (§ 5.2) |

## 9. Alcance decidido, pendiente de construir

| Decisión | Qué falta |
|---|---|
| DDL-042 | Entity Workspace de **Trabajador** |
| DDL-050 | **Resumen de ausencia** en el Home, consumiendo la misma cola |
| — | Los tres huecos del Context Panel (§ 3.6) |
| DDL-005 | Validar el nivel 3 con volúmenes reales (§ 2.2) |

## 10. Preguntas abiertas

Ninguna.
