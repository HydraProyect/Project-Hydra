# Blueprint — Operational Home

**Superficie**: `/` (menú: **Home operativo**, grupo Dashboards — `03` § 3.2) · **Arquetipo**:
Operational Home (`03` § 1.1)

**Estado**: **Implementado y en producción**, incluido "Qué llegó sin ver" (DDL-068) y "Pulso del
equipo" (DDL-071). Sustituyó directamente al Dashboard heredado de las Fases 2/25/57/63 — no
convivió en otra ruta.
**Implementado hasta**: todo lo descrito en este documento existe, salvo "qué avanzó el sistema"
(§ 6, mitad de DDL-050 explícitamente fuera de alcance).

**Por qué existe este documento**: `03_INFORMATION_ARCHITECTURE.md` § 1.1 define el arquetipo
Operational Home en abstracto (qué resuelve, qué no duplica) pero ningún blueprint lo aterriza
todavía a una superficie real — a diferencia de Entity Workspace, que ya tiene a Centro 360 como
patrón maestro. Este documento es esa primera aplicación, y su hallazgo principal es que **la
mayor parte de lo que necesita ya existe**: la Fase C construyó una cola priorizada con una acción
primaria por ítem (`ObtenerBandejaGestorQuery`, `/bandeja`) que este arquetipo debía "consumir, no
sustituir" (DDL-046) — este blueprint es sobre todo un ejercicio de composición, no de
construcción nueva.

---

## 1. Qué resuelve

Responde, en el orden de `03` § 1.1, el modelo mental completo de un vistazo:

1. **¿Qué está pasando?** — cumplimiento global, situaciones que requieren al gestor hoy.
2. **¿Qué requiere atención?** — la misma cola priorizada de `/bandeja`, resumida.
3. **¿Qué viene?** — visitas próximas y su riesgo documental.
4. **¿Qué llegó sin ver?** — resumen de ausencia (DDL-050, alcance cerrado en § 6).

Es la única superficie donde mezclar entidades distintas (documentos, visitas, revisiones IA,
detecciones de personal) está permitido — el resto de la app se organiza por entidad (`03` § 1.1).

Además, específico de esta implementación de Hydra y no parte del arquetipo genérico de `03` § 1.1:

5. **¿Cómo vamos como equipo esta semana?** — pulso agregado del tenant, sin desglose por persona
   (DDL-071). No responde "qué requiere atención" ni "qué viene"; responde a la satisfacción y el
   estrés del gestor, el objetivo que motivó DDL-071.

## 2. Contratos que hereda

| Aspecto | Documento |
|---|---|
| Arquetipo Operational Home | `03` § 1.1 |
| El Home consume la cola de la Bandeja, no la sustituye | DDL-046, `05` § 7 |
| Una acción primaria por ítem que requiere atención | `03` § 1.1, ya construido en `PanelResolverItem` |
| Agencia — quién actúa (azul) vs quién es Hydra (cian) | `02` § 3.1, DDL-009 |
| Iconografía obligatoria en KPIs | `06` § 5, DDL-036 |
| Superficies de 4 niveles, sombra solo en Overlay | `06` § 5, DDL-013 |

## 3. Anatomía

```
┌─────────────────────────────────────────────────────────┐
│ Cabecera: saludo + fecha + Cliente activo                │
├─────────────────────────────────────────────────────────┤
│ Qué llegó sin ver (solo si hay ausencia real, § 6)         │
├─────────────────────────────────────────────────────────┤
│ Cumplimiento global (anillo) │ KPIs secundarios (tiles)  │
├─────────────────────────────────────────────────────────┤
│ Requiere atención — 5 ítems de /bandeja, PanelResolverItem │
│   [Ver todo en Bandeja →]                                 │
├─────────────────────────────────────────────────────────┤
│ Pulso del equipo — resueltos esta semana vs. mejor semana │
├─────────────────────────────────────────────────────────┤
│ Próximamente — 3 visitas más próximas                     │
│   [Ver todas en Visitas →]                                 │
└─────────────────────────────────────────────────────────┘
```

"Pulso del equipo" solo se pinta junto a "Requiere atención" (mismo alcance de visibilidad,
`_mostrarRequiereAtencion`) — un Cliente no ve el rendimiento interno del equipo gestor que lo
atiende.

No incluye la tabla "Centros/Empresas con más riesgo" ni el gráfico "Automático vs manual" del
Dashboard actual — ambos pasan al futuro Dashboard de dirección y coordinación (§ 7). Tampoco
incluye ningún desglose por persona: es la restricción central de DDL-071, no una omisión.

## 4. Comportamiento propio

- **Sin filtros ni paginación** — a diferencia de `/bandeja`, el Home no es una cola de trabajo
  completa, es un resumen. Cualquier acción de filtrar/paginar pertenece a `/bandeja` o `/visitas`;
  el Home solo enlaza hacia ellas.
- **Cada ítem de "Requiere atención" reutiliza `PanelResolverItem` y `AccionesBandeja.AbrirAsync`
  tal cual** — el mismo componente que ya usan `/bandeja` y el panel de `/alertas`, para que un
  ítem abra exactamente el mismo sitio se mire desde donde se mire (mismo criterio que el propio
  comentario de `AccionesBandeja.cs`).
- **Sin cartera asignada**: mismo estado vacío que ya tiene el Dashboard actual (`_kpis.SinCarteraAsignada`).
- **"Qué llegó sin ver" solo se pinta si hay ausencia real** (§ 6) — no aparece en navegación
  intradía normal ni en la primera visita de un usuario nuevo.

## 5. Datos

| Sección | Query | Ya existe |
|---|---|---|
| Cumplimiento global + KPIs secundarios | `ObtenerKpisDashboardQuery` | ✅ (Dashboard actual) |
| Requiere atención (5 ítems) | `ObtenerBandejaGestorQuery` | ✅ (Fase C, `/bandeja`) |
| Próximamente (3 visitas) | `ObtenerVisitasQuery(SoloActivas: true)`, ordenada por `FechaInicio` | ✅ (`/visitas`) |
| Qué llegó sin ver | `ObtenerBandejaGestorQuery` + `UltimaActividadUtc` del usuario | ✅ `ActividadUsuarioService` |
| Pulso del equipo | `ObtenerPulsoEquipoQuery` — agrega `AprobacionDocumento` por semana (actual / anterior / mejor histórica cerrada), mismo alcance que `IAlcanceDatosService` | ✅ (nuevo, DDL-071) |

El único dato nuevo de todo el blueprint fue `UltimaActividadUtc` — el más costoso de los tres
prerrequisitos, con un punto de escritura en `MainLayout` (no en el Home) para cubrir toda la
plataforma. El resto es composición Blazor sobre queries ya existentes o, en el caso del Pulso,
una agregación nueva sobre una tabla que ya existía (`AprobacionDocumento`).

## 6. "Qué llegó sin ver" (DDL-050, alcance cerrado, implementado)

DDL-050 decidió construir un **resumen de ausencia** con dos partes: "qué llegó sin ver" y "qué
avanzó el sistema". Al diseñar sus tres prerrequisitos se encontró que solo la primera parte tiene
datos reales hoy — la segunda exigiría un log transversal de "acciones automáticas que Hydra
ejecutó" que no existe en ningún sitio del dominio (no hay equivalente de
`TipoEventoConversacion` fuera del ámbito de una conversación). Construir ese log es una pieza de
dominio nueva, no composición — **queda fuera de esta ronda**; este blueprint entrega una v1 más
estrecha que la decisión original de DDL-050.

Diseño de los tres prerrequisitos, dentro de ese alcance:

1. **Modelo de "visto"**: campo nuevo `UltimaActividadUtc` (`DateTime?`) en el usuario —
   deliberadamente **no** "última visita al Home", sino última interacción autenticada en
   cualquier punto de la plataforma (petición HTTP o evento de circuito Blazor), con un throttle
   razonable de escritura (no se reescribe por cada click, solo si ha pasado más de un minuto desde
   el valor guardado). Un usuario trabajando en `/visitas` sin pasar por el Home no debe volver a
   `/` y encontrarse "ausente" solo por no haber tocado esa pantalla en concreto. `null` significa
   "nunca tuvo actividad" — en ese caso no se muestra el bloque.
2. **Qué cuenta como "ausencia"**: el usuario no tiene sesión activa, **o** lleva más de **10
   minutos** inactivo en la plataforma. Con `UltimaActividadUtc` como único punto de referencia
   ambos casos son el mismo chequeo — una sesión cerrada dejó de generar actividad hace rato, así
   que el hueco ya es mayor a 10 minutos por definición: `ahora − UltimaActividadUtc > 10 minutos`.
   No hace falta distinguir "sin sesión" de "inactivo dentro de una sesión abierta" con dos reglas
   separadas.
3. **Misma cola que la Bandeja, filtrada por fecha real de aparición**: de los ítems que devuelve
   `ObtenerBandejaGestorQuery`, solo tres orígenes tienen un timestamp real de "cuándo apareció esto"
   (`CreadaEnUtc`): `SugerenciaVisitaCorreo` (vía `ObtenerSugerenciasVisitaCorreoPendientesQuery`),
   `DeteccionTrabajador` (vía `ObtenerDeteccionesPendientesQuery`) y `RevisionIaDocumento` (vía
   `ObtenerRevisionesIaPendientesQuery`). Los de estado derivado (Faltante/Vencido/Urgente) no
   tienen un momento de "creación" — un documento vencido no "apareció", su estado cambió — así que
   no participan en este resumen; siguen visibles en "Requiere atención" como siempre. `SugerenciaGestionCorreo`
   tampoco participa: no alimenta `ObtenerBandejaGestorQuery` hoy, vive solo en el Action Center de
   Comunicaciones. El resumen es: de los tres orígenes con timestamp, los que tengan
   `CreadaEnUtc > UltimaActividadUtc` (el valor justo antes de la ausencia detectada).

**Nota de implementación**: `@rendermode InteractiveServer` prerenderiza el árbol de componentes
una vez dentro de la petición HTTP normal, en un DI scope desechable, antes de que exista el
circuito interactivo real — y lo vuelve a ejecutar al conectar. Sin guardar la lectura y la
escritura de `UltimaActividadUtc` a la pasada interactiva (`ComponentBase.RendererInfo.IsInteractive`,
no `IHttpContextAccessor` — en este hosting model `HttpContext` no distingue las dos pasadas), el
prerender consume la ausencia y escribe "ahora" antes de que el circuito real llegue a leerla, y el
resumen nunca aparece.

## 7. Divergencias con el código actual

El Dashboard que hoy vive en `/` (Fases 2/25/57/63) **no es una versión parcial de este blueprint
— es una superficie distinta**, con piezas reales que este documento no absorbe:

| Pieza del Dashboard actual | Qué pasa con ella |
|---|---|
| 4 tiles críticos (Vencidos/Urgentes/Próximos/SLA) | Se sustituyen por el anillo de cumplimiento + KPIs secundarios de § 3 — mismo dato (`ObtenerKpisDashboardQuery`), presentación distinta |
| Tabla "Documentos que requieren atención" | Se sustituye por la sección "Requiere atención" (§ 3) — mismo concepto, ahora es la cola completa de `/bandeja`, no solo documentos |
| 4 tiles secundarios (Trabajadores/Centros/Vigentes/Visitas) | Se quedan en el Home, como hoy, enlazando a sus listados |
| "Gestiones automáticas vs manuales" (barra) | Pasa a **`/dashboard-ejecutivo`** (`DashboardEjecutivo.razor`, ya existe y ya está en el menú) — es una métrica de calidad de IA para dirección, no "qué requiere atención" ni "qué viene". Hoy ese dashboard no la tiene: hay que migrarla, no solo enlazarla |
| "Centros con más riesgo" (tabla) | Pasa al mismo `/dashboard-ejecutivo` — ya tiene un equivalente (`_centrosConMenorCumplimiento`, `ObtenerDashboardEjecutivoQuery`); verificar que cubre el mismo caso antes de dar la migración por completa |
| "Empresas con más riesgo" (tabla) | Pasa al mismo `/dashboard-ejecutivo` — a diferencia de Centros, hoy **no tiene equivalente** ahí (`ObtenerDashboardEjecutivoQuery` no expone nada de Empresas); hay que migrarla, no solo enlazarla |

**"Pulso del equipo" no sigue este mismo camino.** Aunque es, como "Gestiones automáticas vs
manuales", un agregado que no es "qué requiere atención" ni "qué viene", **se decidió que se queda
en el Home** (DDL-071) en vez de ir a `/dashboard-ejecutivo`: las tarjetas de la tabla de arriba son
métricas de calidad/riesgo para *dirección*; el Pulso es progreso visible para el propio *gestor*
— el objetivo que motivó DDL-071 (satisfacción, bajar el estrés) solo se cumple si vive donde el
gestor entra cada día, no en el dashboard que consulta dirección.

## 8. Decisiones que gobiernan esta superficie

| Decisión | Aporta |
|---|---|
| DDL-004 | Arquetipo Operational Home existe como categoría (`03` § 1) |
| DDL-046 | El Home consume la cola de la Bandeja, no la sustituye |
| DDL-050 | Resumen de ausencia — alcance cerrado en § 6, más estrecho que la decisión original |
| DDL-009 | Regla de agencia — quién actúa vs quién es Hydra |
| DDL-036 | Iconografía obligatoria en KPIs |
| DDL-013 | Sombra solo en Overlay |
| DDL-071 | Gamificación — progreso y cierre + pulso del equipo; fija que el Pulso vive en el Home, no en `/dashboard-ejecutivo`, y rechaza puntos/badges/leaderboards entre personas |

## 9. Pendiente fuera de este blueprint

- **"Qué avanzó el sistema"** (mitad de DDL-050 no cubierta, § 6): necesita un log transversal de
  acciones automáticas que hoy no existe en el dominio. Sin blueprint ni decisión de diseño propia.
- **`/dashboard-ejecutivo`**: ya recibió "Automático vs manual" y "Empresas con más riesgo" (§ 7),
  pero sigue sin blueprint propio (es un Entity/Operational Home retroactivo, como Centro 360 lo
  fue para su arquetipo) — pendiente si alguna vez hace falta gobernar su evolución con la misma
  disciplina que este documento.
