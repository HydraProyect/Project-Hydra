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
  inventar uno nuevo. Excepción: un formulario de creación o edición en un Drawer se
  escribe con `DrawerFormulario` (kit S12) y se copia de la pantalla de referencia
  `Features/Trabajadores/Pages/Trabajadores.razor` (alta de Trabajador), no de un vecino
  con `<Drawer>` artesanal: `FormulariosEnDrawerUsanElKitTests` congela los que quedan.
- No autoricéis, no aprobéis y no deis por buena ninguna decisión de arquitectura,
  dominio o negocio que no puedas verificar en el código mismo — para eso hace
  falta el repositorio local.

---

# Reglas de trabajo

## Alcance autónomo

Con autonomía autorizada, avanza sin pedir confirmación entre pasos ya fijados. Sin autorización explícita no modifiques ni despliegues producción, no manipules secretos ni credenciales, no toques `.env` sensibles, no cambies roles PostgreSQL de producción, no ejecutes operaciones irreversibles y no mergees lo que requiera aprobación humana. No uses credenciales existentes en sistemas que no identifiques con certeza.

## Cuándo parar

Para y pregunta solo ante una decisión de producto, negocio, seguridad o arquitectura; falta de acceso o de secretos; producción u operaciones irreversibles; contradicción entre decisiones fijadas; tener que inventar una regla crítica; consecuencias comerciales no definidas. Las dudas técnicas se resuelven con el agente `revisor-puente`, no con Chris. No interrumpas por compilación, tests, fixtures, arnés, ratchets o gates: son trabajo tuyo. Informa como DECISIÓN NECESARIA con evidencia y opciones.

## Seguridad y multi-tenancy

Identidad, contexto, tenant, capacidad, autorización, alcance y sesión privilegiada son cosas distintas: una coordenada de contexto no es autoridad. En toda operación multi-tenant comprueba usuario, tenant de origen, tenant objetivo, workspace, capacidad, alcance, vigencia, RLS e identidad efectiva de PostgreSQL. No debilites RLS para acomodar código: si una operación cruza tenants, debe existir un contrato o identidad que lo permita explícitamente. Ante un test en rojo busca la causa raíz; no cambies RLS, autorización ni seguridad para conseguir el verde.

Bucle completo, evidencia y cierre: skill `protocolo-hydra-autonomia`.

## Antes de tocar código

- Primera orden: `git fetch origin` y `git log --oneline origin/main..HEAD` en PowerShell, para confirmar que la rama sale de `origin/main`. Toda rama se corta de `origin/main`, para no heredar trabajo ajeno. `scripts/estado-ramas.sh` se usará cuando exista su versión PowerShell; la herramienta Bash está denegada en `~/.claude/settings.json`.
- Si el cambio toca un DTO, una API, el esquema o la autorización, carga `protocolo-hydra-contratos` antes de editar.
- Si cambias una regla, corrige en el mismo incremento el contrato, documento o comentario normativo que la describía.

## Antes de abrir una PR

- Revisión previa con el agente `revisor-puente`, pidiéndole refutarlo. Si toca autorización, RLS, multi-tenancy, criptografía o auditoría, o hay duda, la segunda opinión es obligatoria y la da un modelo distinto del que escribió el código: solo se encola con el veredicto FUSIONABLE de Codex sobre la cabeza actual; sin cuota, la PR no se fusiona hasta que Chris lo decida.
- El cuerpo lleva `## Revisión Codex` con `Revisor:`, `Refutación:` y el veredicto, cada uno en una línea y sin marcadores; si toca `deploy/bootstrap/roles-de-cluster.sql`, también `## Paso operativo en servidores` con staging y producción. Lo comprueba `scripts/verificar-gobernanza-pr.sh`, que es la autoridad, y solo comprueba la forma.
- `gh pr create` con `--label "Type: …" --label "Priority: …" --milestone "…"`; sin ellos la PR queda en rojo.
- Tras el tercer ciclo de revisión, reevalúa el enfoque. Un hallazgo que se repite en dos PRs se convierte en un check automático o en una issue.
- Procedimiento completo, con comandos: skill `abrir-pr-hydra`. Si la PR sale de la cola: skill `fallo-de-cola`.

## Skills

Si falta `protocolo-hydra-contratos` o `protocolo-hydra-verificacion`, no reconstruyas su contenido de memoria: cuando el cambio toque DTO, API, esquema, autorización o RLS, o tengas que dar por buena una evidencia, detente e informa «skill normativa ausente: <nombre>». En los demás casos, y sin cualquier otra skill, trabaja con este fichero y anótalo en la PR.

| Skill | Cárgala cuando |
|---|---|
| `protocolo-hydra-autonomia` | empieces trabajo autónomo o cierres un incremento |
| `protocolo-hydra-verificacion` | interpretes tests, gates, CI o mutaciones, o comuniques evidencia de diff, búsqueda o log |
| `protocolo-hydra-sesiones-paralelas` | vayas a tocar código, cortes ramas, abras PRs, coordines sesiones o veas un fallo transversal de CI |
| `protocolo-hydra-sesion` | seas la sesión principal (título, grupo, tablero, ENTREGA) |
| `protocolo-hydra-contratos` | cambies un contrato o una regla documentada |
| `protocolo-hydra-dependencias` | actualices paquetes, revises Dependabot o toques secretos de CI |
| `protocolo-hydra-multimodelo` | leas más de ~50.000 tokens (Gemini vía `agy`); sin ella no se invoca `agy` |
| `abrir-pr-hydra` | cortes una rama para una PR, escribas su cuerpo, la crees o la encoles |
| `fallo-de-cola` | una PR salga de la cola de fusión o falle en `merge_group` |
| `encargo-implementador` | lances un agente que edite código |
| `desplegar-produccion` | haya que aprobar un despliegue a producción |
