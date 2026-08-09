# 04 — UX Patterns

**Estado**: Normativo · **Implementado hasta**: mayoritariamente. La mayor parte de estos
patrones existe en el producto; lo que cambia respecto al código actual está señalado en cada
sección con la decisión que lo respalda. Los patrones de superficies administrativas (§ 10) son
los menos consolidados.

**Autoridad**: define **cómo se comporta la interacción**. Un mismo gesto produce el mismo
resultado en toda la plataforma.

**Este documento no decide**:

| Pregunta | Documento |
|---|---|
| Qué estructura tiene una pantalla | `03` / `05` |
| Qué colores, tamaños o duraciones usa | `06` |
| Qué hace un módulo concreto | `docs/blueprints/` |
| Qué se decidió y por qué | `DESIGN_DECISION_LOG.md` |

**Alcance especial**: los catálogos y la administración no tienen arquetipo propio (DDL-053). Su
consistencia **depende enteramente de este documento** — por eso § 3, § 5 y § 10 cubren lista,
formulario y confirmación destructiva con el mismo detalle que los patrones operativos.

---

## 1. Principios de interacción

1. **Consistencia absoluta.** Quien aprende a crear un Cliente ya sabe crear un Trabajador. Un
   patrón nuevo solo se justifica cuando el existente falla, y entonces cambia en todas partes.
2. **La acción vive donde aparece el problema.** Si una fila revela algo que resolver, la acción
   está en esa fila.
3. **Nada se pierde sin aviso.** Ningún cambio del usuario desaparece por navegar, recargar o
   por un guardado ajeno.
4. **Confirmación solo donde hay consecuencia.** Confirmar un guardado normal añade fricción sin
   proteger nada.
5. **Un destino, un verbo.** Dos etiquetas distintas para la misma acción son dos conceptos en la
   cabeza del usuario (DDL-036).

## 2. Acciones sobre entidades

### 2.1 Crear
Acción primaria arriba a la derecha de la lista. Abre **formulario en overlay lateral** para un
alta simple de un solo agregado. Usa **página completa** cuando el formulario tiene varias
secciones o es un asistente de pasos.

Al guardar: aviso de éxito, la lista se actualiza sin recargar, el overlay se cierra.

**Alta encadenada**: cuando crear una entidad implica casi siempre crear la siguiente de la
jerarquía, el pie ofrece una acción secundaria que guarda y continúa con la siguiente, con el
padre ya fijado. Esa etiqueta **nunca repite la palabra "Guardar"**: conviven en el mismo pie y
deben distinguirse sin leer entero.

**Crear desde un selector**: cuando el registro que hace falta no existe, el selector ofrece
crearlo en un **modal** — nunca un segundo overlay lateral, que rompería el atrapado de foco de
ambos. Al guardar, el registro nuevo queda seleccionado sin que el usuario pierda lo escrito.

### 2.2 Editar
**Edición in situ dentro de la superficie**, no en un formulario aparte. La fila ofrece
"Detalles", no "Editar": el detalle es donde se edita.

Los campos que forman la identidad de la entidad —los que se fijan al crear— permanecen en modo
lectura dentro del mismo bloque. No desaparecen ni se vuelven editables.

Al guardar: aviso y vuelta a lectura con los datos recargados. Al cancelar: se descartan los
cambios locales sin tocar el servidor.

### 2.3 Eliminar
Siempre **borrado lógico**. Confirmación obligatoria en diálogo modal, nunca con el diálogo del
navegador. El cuerpo declara **la consecuencia real**, no una fórmula: qué deja de verse, qué se
conserva y si se puede deshacer.

Nunca "¿Estás seguro?" como único texto: no informa de nada.

**Deshacer**: el aviso de éxito ofrece restaurar in situ donde el dominio lo permita. Un aviso
con acción vive más tiempo que uno informativo — el usuario necesita un instante para decidir,
no solo para leer.

Cuando el borrado **no** es reversible, el copy no puede prometer que lo sea. Prometer una
recuperación que no existe es peor que no ofrecerla.

### 2.4 Un destino, un verbo (DDL-036)
Una misma acción se llama igual en toda la plataforma aunque el estado de partida cambie. Un
documento que falta y uno vencido se **gestionan** los dos; no se "sube" uno y se "gestiona" el
otro.

### 2.5 Acción siempre disponible, atenuada cuando no hace falta (DDL-036)
Un elemento que no requiere intervención **conserva su acción**, en tono atenuado. Guía sin
prohibir. **Nunca se deshabilita de verdad**: un control deshabilitado no explica por qué lo
está, y el usuario que sí necesita usarlo se queda sin camino.

## 3. Listas

### 3.1 Buscar
**Buscador global** con atajo, siempre accesible desde la barra superior: busca por nombre,
identificador o código a través de las entidades principales, con resultados agrupados por tipo y
navegación por teclado. Es el sustituto real del filtrado manual.

Dentro de una lista, la búsqueda filtra esa lista y nada más.

### 3.2 Filtrar
Panel de filtros **junto a la tabla**, nunca escondido tras un menú. Los filtros activos se
muestran como elementos removibles sobre la tabla.

**El estado de filtros persiste en la URL** para poder compartirlo y recargarlo (`03` § 5.2).

Toda lista con estado ofrece su filtro de estado con las opciones **de peor a mejor**: al
filtrar, lo que se busca es lo que urge. "Sin documentos" es siempre una opción propia — jamás
se presenta como si estuviera al día.

### 3.3 Ordenar
Toda columna con un criterio con sentido es ordenable, y ordena **en el servidor**, nunca
reordenando la página ya cargada. La columna llega contra una lista blanca: un valor desconocido
cae al orden por defecto.

El orden se cierra con un **desempate estable**: sin criterio total, la paginación repite o
pierde filas.

El orden **no** persiste en la URL: es preferencia de lectura, no contexto compartible.

Las listas de entidades con cumplimiento pueden ordenarse **por porcentaje de cumplimiento**,
para atacar lo peor sin depender de que haya una fecha próxima (DDL-036).

### 3.4 Paginar
Un **único paginador** en toda la plataforma, con el mismo aspecto y el mismo texto en español,
tanto en listas con paginación propia como en las que delegan el fetch en el componente de
tabla. Los totales se muestran siempre: saber cuántos hay es parte del contexto.

### 3.5 Selección múltiple
Los selectores de fila **no están visibles por defecto**: son ruido permanente para una acción
ocasional. Un control en la barra de herramientas los muestra y los oculta.

**Apagar el modo limpia la selección.** Dejar filas marcadas que ya no se ven deja las acciones
en lote apuntando a algo invisible.

En listas expandibles, el control de expandir y el selector **conviven** sin desplazarse: el
selector se añade, no sustituye.

### 3.6 Acciones en lote
La barra de acciones aparece al haber selección, indica **cuántos elementos** afecta y **nunca
ejecuta directamente**: cada pantalla confirma antes. Las acciones destructivas en lote siguen
§ 2.3, con el recuento en el texto de confirmación.

### 3.7 Exportar
Toda lista operativa exporta. Es el gesto más extendido del sector y no se considera una función
avanzada. El export respeta **los filtros activos**, no la página visible: exportar veinte filas
cuando el filtro selecciona seiscientas es un error silencioso.

### 3.8 Estructura de fila (DDL-036)
- **Ranuras fijas por tipo de indicador** — la regla estructural vive en `05` § 2.6; aquí solo
  su consecuencia de interacción: el usuario puede recorrer una columna con la vista sin releer
  cada fila.
- **Guía de lectura**: al apuntar una fecha o una acción, la fila entera se resalta. No es
  adorno — en filas anchas evita recorrer el renglón dos veces (`07`, Tier A).
- **Densidad compacta única** (DDL-041).

### 3.9 Drill-down entre listas
Cuando un elemento lleva a otra lista para verlo en su contexto, el destino se prefiltra **por
identificador exacto**, nunca por texto libre: dos nombres parecidos harían el filtro ambiguo.

## 4. Recuentos, estados y procedencia

### 4.1 Badges de estado
El estado se nombra con el **léxico cerrado** de su eje, y los ejes **no se mezclan** (DDL-052).
La definición del eje documental —`Vigente` · `Proximo` · `Urgente` · `Vencido` · `NoAplica`— vive
en `DOMAIN.md` § 68, no en este documento ni en un diccionario paralelo (DDL-066); el eje de
**acreditación** vive en `UBIQUITOUS_LANGUAGE.md`. No se inventan variantes locales: un término
que significa dos cosas en dos pantallas es un error de datos esperando a ocurrir.

Reglas de forma (DDL-036):
- **El recuento se separa del estado**: el número es un recuento de elementos, no parte del
  nombre del estado.
- En superficies densas, el badge puede mostrar **solo el número** — pero entonces § 4.2 es
  obligatorio.
- Se nombra **el estado del sujeto**, no lo que el documento provoca: "Acceso bloqueado", no
  "Bloquea acceso".

### 4.2 Ventana de contexto
Todo recuento o marca que no se explique por sí solo **abre su desglose bajo demanda**:
qué elementos lo componen y de qué ámbito (DDL-033, DDL-047).

Obligatorio, sin excepción: **se abre con puntero y con foco de teclado**, y el elemento lleva
nombre accesible. Una ventana que solo responde al puntero deja fuera al teclado y al lector de
pantalla, y convierte el color en el único portador de significado.

### 4.3 Marca de procedencia (DDL-032)
Todo dato que no introdujo una persona lleva una **marca mínima** —icono, sin etiqueta de
texto—, y su detalle (origen, confianza, quién lo confirmó) vive en la ventana de contexto.

Distinción que evita el ruido: **por fila, marca; superficie de decisión, tarjeta**. Cuando la
procedencia es masiva, una etiqueta textual por fila sería ruido puro.

## 5. Formularios

### 5.1 Validación
Inline, junto al campo, **al salir del campo** — no solo al enviar. Un formulario que revela
todos sus errores al final obliga a recorrerlo dos veces.

### 5.2 Autoguardado
**No** en campos con relaciones de negocio críticas: el usuario debe controlar cuándo se aplica
un cambio con consecuencias. **Sí** en notas y comentarios libres.

### 5.3 Edición concurrente
Todo formulario de edición envía **la versión que el usuario vio**. Si otra persona modificó el
registro mientras tanto, el guardado se rechaza y se avisa con qué hacer, en vez de sobrescribir
en silencio.

Es un patrón de interacción, no solo técnico: el usuario debe entender que **su cambio no se ha
perdido**, sino que hay otro más reciente que revisar.

### 5.4 Campos resueltos automáticamente
Cuando un dato se puede **derivar** de otro ya obligatorio, no se pide como control aparte: se
resuelve solo y el control manual aparece **solo si la resolución no basta**.

Reglas: la resolución se dispara al **completar** el campo de origen, no en cada pulsación; un
único resultado se autoselecciona **mostrando cuál** y ofreciendo corregirlo; varios resultados
abren la elección entre candidatos; ninguno, la elección completa. Antes de resolver no se
muestra ningún control de ese dato.

## 6. Estados obligatorios de una vista

Sin excepción, toda vista de datos contempla:

| Estado | Qué se muestra |
|---|---|
| **Cargando** | Esqueleto con la forma del contenido final, no un indicador genérico |
| **Vacío** | Causa y siguiente paso, con la acción primaria disponible |
| **Sin permiso** | Explicación y qué hacer; nunca una pantalla en blanco ni un error crudo |
| **Error** | Qué falló en lenguaje humano y cómo reintentar |
| **Sin conexión** | Aviso persistente con reintento |
| **Con datos** | El contenido |

El estado **vacío** merece atención especial: un vacío que se muestra como éxito —cero
problemas, cumplimiento perfecto— es **engañoso**. Si no hay datos porque no hay cartera
asignada, se dice.

## 7. Feedback

### 7.1 Avisos
Esquina superior derecha, autodescartables salvo los de error, que requieren descarte manual. Un
aviso con acción vive más tiempo. **Nunca más de tres visibles** a la vez: a partir de ahí no se
leen, se cierran. Ejecutado en `ToastService` (`MaximoVisibles`), cierre de
`docs/business/MATURITY_REVIEW.md` P2 #28 (OD-29).

**Detalle no cubierto por esa entrada, sin decidir**: la implementación actual descarta el aviso
más antiguo para hacer sitio al nuevo **sin excepción por tono**, incluido un error. Ni el
histórico ni P2 #28 dicen qué debe pasar cuando la cola está llena y llega un error — es una
regla de código, no una decisión documentada.

Siempre con icono y color semántico coherentes con el resultado.

### 7.2 Confirmaciones
Solo para lo destructivo o irreversible en la práctica. El diálogo declara la consecuencia real y
la acción destructiva se nombra explícitamente ("Eliminar"), nunca "Aceptar".

## 8. Interacción con la IA

### 8.1 La IA propone; la persona confirma
Sin confirmación no hay efecto (`01` § 5.4). "Automáticamente" significa *con todo preparado, a
un clic*.

### 8.2 Los tres umbrales de confianza (DDL-064)

No existe un único "umbral alto". Son **tres condiciones distintas**, con nombre propio cada una,
y no se sustituyen entre sí:

| Umbral | Valor | Qué gobierna |
|---|---|---|
| **De revisión** | 70 % | Por debajo, el dato no se presenta como hecho: se marca para verificación |
| **De confirmación masiva** | ≥ 95 % | Junto con datos completos, habilita la acción en lote (§ 8.3) |
| **De confianza visual** | ≥ 95 % | La señal de confianza alta que ve el usuario en el badge |

Los dos últimos **comparten valor a propósito** (DDL-065): 95 es la frontera única de **actuar sin
revisión humana**, y la señal que el usuario ve debe coincidir con lo que el sistema se permite
hacer sin que él abra el elemento. Siguen siendo **dos reglas** —una gobierna una acción, la otra
una señal—; que compartan número es una decisión, no una fusión.

Nombrar la condición concreta es obligatorio. **"Umbral alto" no es un término válido** en ningún
documento: encubría estas tres y hacía que una regla de interfaz y un gate de ejecución
pareciesen la misma cosa.

### 8.2.1 Anatomía de una propuesta
Qué propone, con qué confianza, sobre qué datos, y tres salidas: **Confirmar · Editar ·
Descartar**. Los campos que no alcanzan el **umbral de revisión (70 %)** se marcan para
verificación en lugar de presentarse como hechos.

**Editar es el camino normal** cuando la propuesta es casi correcta, no una excepción.
**Descartar deja rastro**: una propuesta rechazada informa sobre la calidad del sistema.

### 8.3 Acciones que no se confunden
Cuando una revisión ofrece varias salidas, cada una hace **una sola cosa** y se nombra por su
efecto real. Cerrar un aviso porque ya se corrigió a mano y aceptar lo que la máquina propone son
acciones distintas y deben poder distinguirse sin leer dos veces.

Las acciones en lote sobre propuestas solo aplican a las que alcanzan el **umbral de confirmación
masiva (≥ 95 %)** **y** tienen todos los datos necesarios; nunca se confirma en lote lo que no
cumple ambas condiciones. La conjunción no es opcional: es lo que impide aprobar en bloque una
propuesta a la que le falta el dato que la hace aplicable.

## 9. Microcopy

Todo en español, tono directo y humano. Nunca jerga técnica de cara al usuario: los códigos, los
nombres de excepción y las trazas van al registro, no a la pantalla.

El mensaje explica **qué pasó** y, si aplica, **qué puede hacer ahora** la persona.

| Situación | ❌ | ✅ |
|---|---|---|
| Error al guardar | "Error inesperado" | "No pudimos guardar los cambios. Inténtalo de nuevo en unos segundos." |
| Validación | "Campo requerido" | "Este campo es obligatorio." |
| Conflicto de edición | "409 Conflict" | "Alguien más modificó este registro mientras lo editabas. Revisa los cambios antes de guardar." |
| Sin permiso | "403 Forbidden" | "No tienes permiso para ver esta sección. Si crees que es un error, contacta con un administrador." |
| Éxito | "OK" | "Cliente creado correctamente." |

**Reglas de forma** (DDL-036):
- `|` separa títulos; `·` se reserva para metadatos y para nombre · hora.
- Las fracciones van sin sufijo redundante.
- Las vigencias y descripciones empiezan en mayúscula.
- El sistema **no se atribuye certezas que no tiene**: si un dato lo leyó la máquina, se dice que
  lo leyó y con qué confianza (`01` § 7).

## 10. Superficies administrativas y catálogos

No tienen arquetipo (DDL-053). Su consistencia **es** este documento. Reglas específicas:

1. **Un catálogo es una lista** y cumple § 3 completo, incluidos estados obligatorios y export
   cuando el contenido lo justifique.
2. **Una configuración es un formulario** y cumple § 5, incluida la edición concurrente: los
   parámetros del sistema los tocan varias personas.
3. **Una consulta de solo lectura no finge tener acciones.** Si no hay nada que hacer sobre una
   fila, no se le añade un menú vacío.
4. **Lo irreversible se comporta como un flujo** (`05` § 6), no como un formulario con un botón
   rojo. Destruir datos o revelar un secreto una sola vez exige pasos, consecuencia escrita antes
   de cruzar el punto de no retorno, y confirmación explícita.
5. **La baja frecuencia de uso aumenta la carga de explicación**, no la reduce: quien entra dos
   veces al año no recuerda el contexto. El texto de estas pantallas asume menos que el de las
   operativas.

## 11. Teclado y accesibilidad de la interacción

- Todo elemento interactivo es **alcanzable y operable con teclado**.
- `Escape` cierra la capa superior; nunca salta dos capas de golpe.
- Los overlays **atrapan el foco** mientras están abiertos y lo devuelven al elemento que los
  abrió al cerrarse.
- Las listas con selección ofrecen navegación por teclado; los atajos globales **se ignoran
  mientras el foco está en un campo de texto**.
- **Ninguna información existe solo al pasar el puntero** (§ 4.2).

## 12. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-002 | El CRUD como capa que este documento gobierna |
| DDL-036 | Reglas de acción, badge, fila y microcopy (§ 2.4, § 2.5, § 3.3, § 3.8, § 4.1, § 9) |
| DDL-041 | Densidad única (§ 3.8) |
| DDL-032 · DDL-033 · DDL-047 | Procedencia, ventana de contexto y desglose (§ 4) |
| DDL-052 | Léxico cerrado de estados (§ 4.1) |
| DDL-007 | Interacción con propuestas de la IA (§ 8) |
| DDL-064 | Los tres umbrales de confianza, con nombre propio cada uno (§ 8.2, § 8.3) |
| DDL-053 | Las superficies administrativas dependen de este documento (§ 10) |
| DDL-045 · DDL-054 | Qué movimiento acompaña a estos patrones (§ 3.8, vía `07`) |

## 13. Preguntas abiertas

Ninguna.
