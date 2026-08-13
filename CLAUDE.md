# Instrucciones para cualquier sesión de Claude en este repositorio

Este repositorio es **público**, y desde 2026-08-13 solo contiene lo estrictamente
necesario para que el código compile, los tests pasen y el CI/despliegue funcionen.
**Toda** la documentación —arquitectura, dominio, ADRs, gobernanza de agentes,
sistema de diseño, planes, runbooks, informes de auditoría, roadmap— vive en un
repositorio local sin remoto, `C:\Users\chris\Project-Hydra-Negocio`, bajo `tecnico/`
(documentación de negocio y legal en la raíz de ese mismo repo, informes de
seguridad en `seguridad/`).

## Regla operativa, para cualquier tarea futura

**No añadas documentación nueva a este repositorio** — ni un `.md` de arquitectura,
ni un ADR, ni notas de planificación, ni un informe de auditoría. Si una tarea
genera documentación de ese tipo, va al repositorio local de arriba. La pregunta
que decide dónde vive algo no es "¿es sensible?" — es **"¿hace falta que exista
aquí para que el repositorio compile, pase CI o despliegue?"**. Si la respuesta es
no, no entra.

**Si esta sesión no tiene acceso a `Project-Hydra-Negocio`**, no reconstruyas de
memoria la arquitectura, las decisiones o las convenciones — pregunta al usuario
antes de asumir nada, y desde luego antes de crear un documento nuevo aquí para
"rellenar el hueco". Un documento nuevo en este repositorio, aunque sea correcto,
incumple la regla de arriba igual que uno con datos sensibles.

## Lo mínimo para trabajar sin el repositorio local

- El código es la fuente de verdad de cómo está construido el sistema hoy — léelo
  directamente en vez de buscar un documento que lo explique.
- `.github/workflows/ci.yml` es la fuente de verdad de qué debe pasar antes de
  mergear.
- Convenciones de código: sigue el patrón ya presente en archivos vecinos del
  mismo tipo (Command/Query, componente Blazor, configuración de EF...) en vez de
  inventar uno nuevo.
- No autoricéis, no aprobéis y no deis por buena ninguna decisión de arquitectura,
  dominio o negocio que no puedas verificar en el código mismo — para eso hace
  falta el repositorio local.
