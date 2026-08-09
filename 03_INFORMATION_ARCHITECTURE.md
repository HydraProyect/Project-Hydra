# 03 — Information Architecture

**Estado**: Normativo · **Implementado hasta**: parcialmente. El shell, la navegación por grupos,
el buscador global, los atajos y el Delegated Workspace **existen**; los cuatro arquetipos no
están declarados como tales en el código, y la integración del Context Panel con la URL sigue
sin construirse (§ 5.3). Lo que este documento describe en presente es la norma, no el estado
del software.

**Autoridad**: define **dónde vive cada cosa** — arquetipos de superficie, mapa de módulos,
navegación y rutas. `05_WORKSPACE_PATTERNS.md` define **cómo se comporta por dentro** cada
superficie; `04_UX_PATTERNS.md`, cómo se comporta cada interacción. Frontera práctica: si la
pregunta es *"¿dónde encuentro esto y cómo llego?"*, es `03`; si es *"¿qué hace esta pantalla
cuando la abro?"*, es `05`.

---

## 1. Los cuatro arquetipos de superficie (DDL-004)

Toda superficie operativa de Hydra es uno de estos cuatro. El arquetipo se declara al diseñar la
pantalla, antes que su contenido.

### 1.1 Operational Home
La superficie de entrada. Responde el modelo mental completo de un vistazo: estado global →
qué requiere atención → qué viene → qué está haciendo el sistema.

- **Consume**, no sustituye: la cola priorizada vive en la Bandeja y el Home la presenta
  resumida (DDL-046). Una sola fuente, dos presentaciones.
- Cada elemento que requiere atención lleva **una sola acción primaria**.
- Es la única superficie donde tiene sentido mezclar entidades distintas: el día del gestor no
  se organiza por tabla.

### 1.2 Entity Workspace
La página desde la que se **opera** una entidad a fondo: su estado, lo que exige, quién y qué
cuelga de ella, y las acciones sobre todo eso sin salir de la pantalla.

- Patrón maestro: **Centro 360** (DDL-005).
- Entidades con Entity Workspace: **Centro, Empresa y Trabajador** (DDL-042 — Trabajador
  pendiente de construir).
- No toda entidad lo merece. Forzarlo donde no hay operación profunda es complejidad
  especulativa; el resto se consulta con Context Panel.

### 1.3 Context Panel
El panel lateral para **consultar sin perder el sitio**. Se abre desde cualquier lista o
referencia, apila el camino recorrido y se cierra dejando al usuario donde estaba.

- Regla que lo separa del anterior (DDL-006): **panel para consultar, workspace para operar**.
- Nunca anidado: existe una única instancia en el árbol, montada en el layout raíz.
- La columna contextual del Communication Workspace es un Context Panel y se llama así
  (DDL-044).

### 1.4 Flow Surface
Superficies de **proceso guiado**: pasos con estado, validación intermedia y un resultado al
final. Alta guiada de Cliente, importaciones con dry-run, flujos del Action Center (crear visita
desde una conversación, actualizar documentación desde adjuntos), generación de reportes.

- Un flujo guarda de forma incremental o declara explícitamente que no lo hace. Nunca una
  transacción larga que se pierda a medio camino.
- Un flujo siempre puede abandonarse sin dejar datos a medias.

## 2. Catálogos y administración: fuera del sistema de arquetipos (DDL-053)

Los arquetipos gobiernan las superficies **operativas**. Los catálogos, la configuración y la
administración **no reciben arquetipo**: se construyen con la capa CRUD (DDL-002) y los patrones
comunes de `04` — lista, formulario, drawer, modal, Context Panel.

"Administrativa" describe **quién usa la pantalla y con qué frecuencia**, no cómo se estructura.

Esta decisión se tomó con evidencia, no por criterio: al auditar las once pantallas del grupo
**no comparten patrón**. Conviven cinco comportamientos distintos —CRUD de catálogo, consulta de
solo lectura sin acciones, flujo con estado irreversible, máquina de estados y formulario
singleton—, solo cuatro usan la UI de lista compartida y solo tres un Drawer. Un arquetipo que
abarcase a la vez "tabla sin acciones" y "destruir datos definitivamente con autorización
previa" no sería una categoría, sería una carpeta.

**Consecuencia**: la consistencia de estas pantallas depende enteramente de `04`. Ese documento
debe cubrir bien lista, formulario y confirmación destructiva, no solo los patrones operativos.

### 2.1 Reclasificación derivada

Dos pantallas que vivían bajo "Administración" **sí tienen arquetipo**, y estaban mal colocadas:

| Pantalla | Arquetipo real | Por qué |
|---|---|---|
| **Retención de datos** | **Flow Surface** | Proceso guiado detectar → avisar → autorizar con fecha → ejecutar, con un resultado irreversible al final |
| **Claves API** | **Flow Surface** | Generar → revelar una sola vez → confirmar copia → revocar; el revelado no se repite |

Que estuvieran agrupadas con los catálogos era un accidente del menú, no una propiedad suya. El
grupo del menú **no cambia** (siguen siendo administración por frecuencia de uso); lo que cambia
es el patrón con el que se diseñan y se revisan.

## 3. Mapa de la plataforma

### 3.1 Shell

El shell se mantiene (DDL-015): **barra lateral** de navegación por grupos más **barra superior**
con buscador global, notificaciones, selector de Delegated Workspace, tema y cuenta. No se
sustituye por un dock flotante. Su colapso a iconos puede evolucionar.

### 3.2 Grupos de navegación

| Grupo | Contiene |
|---|---|
| **Dashboards** | Home operativo · Visión de cartera · Dashboard Ejecutivo |
| **Negocio** | Clientes · Empresas · Subcontratas · Centros |
| **Operación** | Trabajadores · Vehículos · Documentos · Visitas · Gestiones · Proyectos · Incidencias |
| **Comunicaciones** | Communication Workspace (tras su flag) |
| **Control** | Bandeja · Alertas · Calendario · Reportes · Facturación |
| **Administración** | Usuarios · Roles · Delegaciones · Claves API · Integraciones · Retención · Tipos de documento · Configuración · Auditoría · Auditoría IA · Importación |

El rol Cliente tiene un menú mínimo propio. La visibilidad por rol es una regla de autorización,
no de arquitectura de información: **este documento describe el mapa completo; qué ve cada
usuario lo decide el modelo de permisos.**

### 3.3 El menú encoge cuando los workspaces absorben

Cuando una entidad recibe Entity Workspace, las pantallas que solo existían para operarla
**desaparecen del menú**. Ya ocurrió: `/asignaciones` fue absorbida por Centro 360 y
`/evaluaciones` se retiró al sustituirse por el porcentaje de cumplimiento calculado.

Regla: una entrada de menú se justifica por un **trabajo** que alguien hace, no por una tabla
que existe. Si su único contenido es una relación entre dos entidades que ya tienen workspace,
sobra.

## 4. Reglas de navegación

### 4.1 Objetivo
La regla histórica de "≤3 clics desde el Dashboard hasta cualquier dato" se conserva (ver OD-29
para su tratamiento de procedencia).

**Retirado por OD-28** (2026-08-09): la afirmación de que ninguna situación que requiere atención
está a más de un clic de su resolución. Generalizaba dos veces algo más estrecho que sí está
decidido —`03` § 1.1: cada elemento del Home lleva una acción primaria, es decir, la acción
**existe en la fila**— sin que ninguna decisión extendiera eso a "cuesta un clic llegar" ni a
"desde cualquier situación, en cualquier superficie". Sin mecanismo que la garantizara.

### 4.2 Caminos transversales
Existen tres vías que atraviesan el mapa y **no** dependen del menú:

- **Buscador global** (`Ctrl/Cmd+K`) — por nombre, DNI o código, con resultados agrupados por
  tipo y navegación por teclado. Es el sustituto real del filtrado manual.
- **Atajos de teclado** — `g` + letra para navegar, `n` para crear en la pantalla actual si lo
  soporta, `?` para la chuleta. Se ignoran cuando el foco está en un campo de texto.
- **Enlaces cruzados entre entidades**, que abren Context Panel en vez de navegar a una lista
  filtrada.

Si un flujo real necesita más pasos de los previstos, la respuesta es **añadir un camino
transversal**, no aceptar la fricción.

### 4.3 Delegated Workspace como eje de contexto
Hydra es multi-tenant y una consultora puede operar sobre carteras ajenas por delegación
(`ADR-004`). El **cliente activo** es, por tanto, un eje de contexto tan real como la
navegación: cambia qué datos existen, no solo qué se muestra.

Reglas:
- El contexto activo está **siempre visible** en el shell; nunca se opera sin saber sobre qué
  cartera.
- Cambiar de contexto es una acción explícita del usuario, jamás un efecto lateral de navegar.
- Las decisiones que afectan a la delegación misma (revocar, retirar operadores) se resuelven
  con el tenant **de origen**, nunca con el Delegated Workspace activo — regla de
  `ADR-004` que este documento hereda sin modificar.

## 5. Rutas y URL

### 5.1 Nunca se renombra una ruta existente
`NotificacionUsuario.UrlAccion` se persiste en base de datos y los correos ya enviados contienen
enlaces que no se pueden editar. Cambiar **qué renderiza** una ruta es seguro; **cambiar la ruta**
rompe notificaciones entregadas.

Regla: solo se **añaden** segmentos o parámetros opcionales a lo que ya existe. Al plegar una
pantalla dentro de un workspace, la ruta antigua se conserva como redirección.

### 5.2 Qué persiste en la URL
- **Filtros: sí.** Un estado de lista debe poder compartirse y recargarse sin perder el contexto,
  y los filtros activos se muestran como chips removibles.
- **Orden: no.** Es preferencia de lectura momentánea, no contexto compartible.
- **Drill-down entre listas: por Id exacto**, nunca por texto libre. Un nombre parecido entre dos
  filas haría el filtro ambiguo.

### 5.3 Hueco conocido: el Context Panel no está en la URL
El Context Panel **no serializa su estado**: no hay deep-link, recargar lo pierde entero, y el
botón "atrás" del navegador no interactúa con él. La decisión de cerrarlo automáticamente al
navegar por el menú principal está tomada desde 2026-07-25 y **tampoco está implementada** —
depende del mismo mecanismo de URL.

Se declara aquí como deuda con nombre y no como comportamiento deseado: mientras no exista, no
se pueden dar por hechos los enlaces cruzados que aterrizan directamente en un panel.

## 6. La jerarquía de entidades no es un árbol

Cualquier propuesta de navegación que asuma "un padre por entidad" se romperá contra el dominio
real:

- **Documento** tiene cuatro propietarios posibles y mutuamente excluyentes: Trabajador,
  Cliente, Empresa o Vehículo.
- **Centro** cuelga a la vez de un Cliente y de una Empresa.
- **Trabajador** y **Vehículo** pertenecen a una Empresa **o** a una Subcontrata, nunca a ambas.

Consecuencia de arquitectura: la relación secundaria se representa como **enlace cruzado** —
chip, referencia, entrada de breadcrumb—, nunca forzando una jerarquía única. Por eso el
Context Panel apila **el camino recorrido**, no la jerarquía de datos.

## 7. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-003 | El orden que la navegación debe permitir recorrer (§ 4.1) |
| DDL-004 | Los cuatro arquetipos (§ 1) |
| DDL-005 · DDL-042 | Qué entidades tienen Entity Workspace (§ 1.2) |
| DDL-006 · DDL-044 | Frontera Context Panel / Entity Workspace y su nombre (§ 1.3) |
| DDL-015 | El shell se mantiene (§ 3.1) |
| DDL-046 | El Home consume la cola de la Bandeja (§ 1.1) |
| DDL-002 | El CRUD como capa, no como arquetipo (§ 2) |

## 8. Alcance decidido, pendiente de construir

| Decisión | Qué cambia en el mapa |
|---|---|
| DDL-042 | **Trabajador** pasa a Entity Workspace; las pantallas que solo servían para operarlo se revisan según § 3.3 |
| DDL-050 | El **resumen de ausencia** se presenta en el Operational Home y consume la misma cola (§ 1.1) |
| — | Cerrar el hueco de § 5.3: URL del Context Panel y cierre automático al navegar por el menú |

## 9. Preguntas abiertas

Ninguna. OD-21 —el arquetipo de los catálogos y la administración— se cerró con **DDL-053**
tras auditar las once pantallas del grupo (§ 2).
