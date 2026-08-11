# Plan — Gamificación v1: progreso y cierre + pulso del equipo (DDL-071)

> Rama de trabajo: `claude/hydra-gamification-discussion-feg5ob`. Decisión normativa:
> `DESIGN_DECISION_LOG.md` § DDL-071. Este documento es el plan y alcance acordados con el
> propietario del producto el 2026-08-09, para que cualquier sesión pueda retomarlo.

## Contexto y objetivo

El objetivo del propietario del producto es que el gestor/coordinador CAE sienta satisfacción
usando Hydra, baje su estrés y se reduzca el burnout. Se investigaron precedentes (ningún
competidor CAE gamifica; el sector EHS gamifica formación de trabajadores, no el trabajo del
gestor; Asana/LinkedIn/Gmail como precedentes B2B de progreso y cierre) y la evidencia académica
(la competición fabricada entre compañeros estresa al tercio inferior; las recompensas extrínsecas
sobre trabajo obligatorio erosionan la motivación intrínseca; lo que sostiene la evidencia es
competencia visible + autonomía).

Síntesis acordada: **apuntar la competitividad al blanco correcto** — las contratas y el propio
histórico del equipo, nunca entre compañeros — y hacer visible el progreso individual y colectivo.

## Alcance v1 (decidido por el propietario del producto)

### Capa 1 — Progreso y cierre individual
1. **Estado de cierre verificado** en el Dashboard: con cartera y cero vencidos/urgentes se
   enuncia el hecho y el siguiente vencimiento por delante ("Cumplimiento al día: 0 vencidos,
   0 urgentes. Próximo vencimiento: …"). Tono factual de `01` § 7; distingue "cero verificado"
   de "sin cartera" (`04` § 6).
2. **Trazado de confirmación Tier C** (`07` § 6) con su primer portador: el toast de Éxito.
   Se dibuja una vez por evento (`@key` en el anfitrión evita redibujos) y `prefers-reduced-motion`
   lo muestra ya trazado (DDL-020).
3. **Medidor de completitud por contrata/centro**: **ya existía** (`AnilloCumplimiento` por centro
   + faltantes en acordeón/preflight de Fase 87) — no se duplica; queda registrado en DDL-071.

### Capa 2 — Pulso del equipo (cooperativo)
4. **`ObtenerPulsoEquipoQuery`**: agregado semanal de verificaciones resueltas
   (`AprobacionDocumento`, automáticas + manuales), semana actual vs anterior vs mejor semana
   histórica cerrada. Mismo alcance de visibilidad que el resto del Dashboard
   (`IAlcanceDatosService`); sin desglose por persona, a propósito.
5. **Tarjeta "Pulso del equipo"** en el Dashboard: cifra semanal + comparación factual; si la
   semana en curso supera la mejor histórica, se enuncia ("Esta ya es la mejor semana del equipo
   hasta ahora."). Competición contra el propio histórico, no un ranking.

### Rechazado de forma permanente (DDL-071, mismo carácter que `07` § 7)
Puntos y niveles · badges · leaderboards entre usuarios o entre tenants · rachas/streaks con
pérdida · métricas de velocidad de validación. La racha diaria del Issue #3 queda rechazada
(el resto del widget de notificaciones sigue pendiente de definir).

### Fuera de v1, al backlog (`ROADMAP.md` § "Backlog — Gamificación dirigida")
**Ranking de cumplimiento de contratas visible para las propias contratas** ("puesto 12 de 40"):
la presión competitiva recae en quien debe aportar la documentación y reduce la persecución del
coordinador. Decidir antes de construir: ámbito (tenant/cliente), qué ven las contratas, opt-in.
Métrica: % de cumplimiento ya calculado — nunca velocidad ni volumen.

## Reconciliación con la Fase BPO — Operational Home (2026-08-11)

`main` avanzó 24 commits durante esta fase, incluida la Fase BPO (#188) que reescribió el Dashboard
entero como Operational Home (`docs/blueprints/OPERATIONAL-HOME.md`). Al fusionar:
- **DDL-068 se renombró a DDL-071** — el número ya lo usaba `main` para otra decisión de esa misma
  Fase BPO.
- El **estado de cierre verificado** cuelga ahora del vacío de "Requiere atención" (cola de
  `/bandeja`), en vez de la antigua tabla "Documentos que requieren atención" — mismo concepto,
  cola más amplia.
- **"Pulso del equipo"** se añadió como fila propia del Operational Home (decisión explícita del
  usuario: no migra a `/dashboard-ejecutivo` junto con "Gestiones automáticas vs manuales", que sí
  se trasladó ahí en la Fase BPO). Ver `docs/blueprints/OPERATIONAL-HOME.md` § 1, § 3, § 5, § 7, § 8.
- Nada de esto cambia el alcance ni las restricciones de más abajo — es reconciliación de
  estructura, no una decisión nueva.

## Restricciones que aplican
- Sin tablas nuevas en v1 (todo se calcula de datos existentes); si algún día se persisten
  snapshots semanales, la tabla lleva `TenantId` + índice compuesto.
- Sin SQL crudo ni `IgnoreQueryFilters()`; agregados siempre dentro del filtro de tenant y del
  alcance por cartera.
- Tono factual (`01` § 7), sin emojis (`02` § 9), sin vacíos presentados como éxito no verificado
  (`04` § 6), motion solo del catálogo (`07` § 6).

## Verificación
- Build + tests en CI (el entorno de la sesión de origen no permitía instalar el SDK).
- Antes de cerrar la fase: verificación end-to-end en navegador (patrón de `ROADMAP.md`) de los
  tres estados del Dashboard (al día / sin cartera / con pendientes), la tarjeta de pulso y el
  trazado del toast (incl. `prefers-reduced-motion`).
