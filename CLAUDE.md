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

---

# Protocolo de ingeniería autónoma

Aplica cuando el usuario autorice explícitamente trabajo autónomo. Entonces se
trabaja de forma continua hasta completar el objetivo o alcanzar una **Stop
Condition**, sin pedir confirmación entre pasos ya determinados por el contrato,
la documentación o las instrucciones existentes.

## 1. Bucle de ingeniería

Por cada incremento: **DISCOVER** (inspeccionar código, tests, documentación, git,
dependencias y estado real; identificar contrato, supuestos, dependencias y
riesgos) → **PLAN** (definir el resultado esperado, cómo se demostrará, y dividir
en incrementos pequeños y verificables) → **EXECUTE** (el cambio mínimo necesario
dentro del alcance autorizado) → **VERIFY** (build/tests relevantes; confirmar que
ejecutan el árbol actual; revisar el diff incluyendo untracked; comprobar
invariantes y efectos reales) → **ADVERSARIAL** (intentar falsar la conclusión;
buscar explicaciones alternativas; probar sensibilidad por mutación cuando
corresponda) → **CLOSE** (clasificar la evidencia, documentar huecos, revisar
diff, commit, actualizar checkpoint, continuar).

No se pide confirmación entre etapas.

## 2. Disciplina de evidencia

Clasifica siempre: **FACT · INFERENCE · HYPOTHESIS · DECISION · UNKNOWN**.

No confundir: `compila ≠ ejecuta ≠ termina ≠ pasa ≠ está probado ≠ está
demostrado`.

**Un resultado vacío no es una ausencia** hasta comprobar que el instrumento podía
observar lo que se buscaba.

Cuando una conclusión dependa de un instrumento, preguntar: ¿qué observa?, ¿qué
excluye?, ¿puede dar un falso negativo?, ¿un falso positivo?, ¿está evaluando el
árbol correcto?

No cerrar recuentos importantes de memoria: verificarlos contra el repositorio. La
regla vale para **cualquier afirmación sobre un estado que no estés mirando**:
número de tests, estado de un PR, contenido de una rama, qué está haciendo otra
sesión, si un cambio llegó a producción. Si no lo has medido en esta sesión, no lo
afirmes — mídelo, o dilo como incierto. Una afirmación sin medir cuesta más cara
cuando es correcta por casualidad, porque enseña a confiar en el método que la
produjo.

## 4. Verificación por capas

Cada propiedad se prueba en la capa que realmente la garantiza: **Domain**
invariantes · **Application** reglas, autorización y orquestación ·
**Infrastructure** Identity, BD y sistemas externos · **PostgreSQL/RLS**
enforcement real · **Architecture** fronteras y prohibiciones · **Integration**
composición · **E2E** comportamiento completo.

Una suite superior no sustituye la prueba de una propiedad inferior, y ninguna
capa debe "prestar" evidencia a otra.

## 5. Diagnóstico de fallos

Ante un fallo: parar de cambiar → reproducir → aislar → **clasificar**
(`PRODUCTO · CÓDIGO · TEST · FIXTURE · ARNÉS · INFRAESTRUCTURA · INSTRUMENTO · CI
· DOCUMENTACIÓN`) → causa raíz → corregir **solo después** de identificarla →
verificación mínima afectada → gate correspondiente.

No parchear síntomas. **No cambiar RLS, autorización, arquitectura ni seguridad
solo para conseguir un verde.**

## 8. Alcance autónomo

**Se puede** modificar autónomamente: código, tests, fixtures, arneses, ratchets,
documentación, guiones, ramas, commits y PRs.

**No se puede sin autorización explícita**: modificar o desplegar producción;
manipular secretos o credenciales; tocar `.env` sensibles; cambiar roles
PostgreSQL de producción; ejecutar operaciones irreversibles; mergear cuando
requiere aprobación humana.

**No usar credenciales existentes para acceder a sistemas que no puedas
identificar inequívocamente.**

## 9. Stop Conditions

Interrumpir **solo** cuando falte una decisión de producto, negocio, seguridad o
arquitectura; falte acceso, secreto o credencial; haya que tocar producción o
ejecutar algo irreversible; exista contradicción entre decisiones fijadas;
continuar exija inventar una regla crítica; o haya consecuencias comerciales no
definidas.

**No interrumpir** para arreglar compilación, tests, fixtures o arnés; investigar
fallos; corregir ratchets; actualizar documentación derivada; ejecutar tests o
gates; crear commits o PRs — si están dentro del alcance autorizado.

Ante una Stop Condition, informar en este formato y detenerse ahí:
`DECISIÓN NECESARIA · EVIDENCIA · OPCIONES · CONSECUENCIAS · RECOMENDACIÓN`.

## 10. Checkpoints y recuperación

Mantener actualizado: `OBJECTIVE · CURRENT_INCREMENT · CONTRACT ·
DECISIONS_FIXED · CHANGES · EVIDENCE_PROVEN · UNKNOWN · NEXT_ACTION`.

Tras un fallo, conservar la evidencia válida y modificar **solo** la hipótesis
afectada: no reiniciar la investigación entera porque un paso posterior falle.

Tras experimentos que alteren estado externo: restaurar, **verificar la
restauración**, y solo entonces continuar. **Revertir código no revierte estado de
infraestructura ni de PostgreSQL.**

## 14. Seguridad y multi-tenancy

No mezclar: `identidad ≠ contexto ≠ tenant ≠ capacidad ≠ autorización ≠ alcance ≠
sesión privilegiada`. **Una coordenada de contexto no es autoridad.**

En cualquier operación multi-tenant, comprobar explícitamente: usuario, tenant de
origen, tenant objetivo, workspace, capacidad, alcance, vigencia, RLS e identidad
efectiva de PostgreSQL.

**No debilitar RLS para acomodar código.** Si una operación cruza tenants de
verdad, debe existir un contrato o identidad que lo permita explícitamente.

## 18. Criterio de cierre

Un incremento está cerrado cuando el contrato está definido, la implementación
completa, las propiedades críticas demostradas, los tests relevantes realmente
ejecutaron, los instrumentos son sensibles, el gate corresponde al árbol final y
**no hay huecos conocidos disfrazados de propiedades demostradas**.

Entonces se informa: `CHANGES · EVIDENCE · TESTS · MUTATIONS · CI · RISKS · GAPS ·
NEXT ACTION`.

**No declarar "baseline" solo porque CI esté verde.**

## 19. Optimización

Optimizar por: calidad de evidencia, reducción de incertidumbre, seguridad, mínimo
retrabajo, y cambios pequeños y bisectables.

**No** por cantidad de código, tests o commits, ni por velocidad aparente, ni por
conseguir un verde rápido. Si una solución simple satisface el contrato, se
prefiere a una arquitectura más compleja.

## 20. Aprendizaje persistente

Cuando una clase de fallo se repita o revele una debilidad del proceso: identificar
la regla general, comprobar que no sea específica de un incremento e incorporarla
aquí. No duplicar reglas equivalentes.

Cuando una premisa aceptada resulte falsa, **decirlo explícitamente** y actualizar
el plan.

## Protocolo detallado bajo demanda

Las secciones situacionales del protocolo viven como skills de usuario (fuente versionada en
`Project-Hydra-Negocio/tecnico/protocolo-skills/`). Son normativas igual que este fichero: cárgalas
**antes** de la situación que cubren, no después.

Anclas que aplican siempre, aunque la skill no se haya activado sola:
- **Antes de afirmar evidencia** obtenida con diff, búsqueda, conteo, log, cobertura, test, CI o
  gate, carga `protocolo-hydra-verificacion` (§ 3).
- **Antes de la primera edición que cambie comportamiento**, determina si afecta a un contrato
  (DTO, API, esquema, autorización, un lector que acabe escribiendo). Si afecta, carga
  `protocolo-hydra-contratos` antes de editar (§ 6).
- **Todo cambio de comportamiento exige buscar** el contrato, el documento o el comentario normativo
  que describa la regla anterior y corregirlo en el mismo incremento (§ 16).
- **Al abrir cualquier PR**, pásale sus metadatos en la misma llamada:
  `gh pr create --label "Type: …" --label "Priority: …" --milestone "…"`. El check
  «Gobernanza — metadatos de PR» exige, leído por API en el momento de evaluar, un
  milestone y al menos una etiqueta `Type:` y una `Priority:` — añadirlos después de
  crear la PR obliga a relanzar ese check y gasta una segunda ejecución de CI que
  la primera llamada ya podía evitar. Milestones vigentes:
  `gh api repos/HydraProyect/Project-Hydra/milestones --jq '.[].title'` (§ 22).

**Si la sesión no tiene la skill** (en la nube o en otra máquina), no reconstruyas su contenido de
memoria. Puedes descubrir en solo lectura, pero antes de editar, abrir una PR, dar por buena una
evidencia o actualizar una dependencia dentro de su ámbito, **detente e informa «skill normativa
ausente: <nombre>»**.

| Skill | Secciones | Cárgala cuando |
|---|---|---|
| `protocolo-hydra-verificacion` | 3, 11, 12, 13, 15, 17 | ejecutes o interpretes tests, gates, CI, ratchets, mutaciones o SQL de siembra |
| `protocolo-hydra-sesiones-paralelas` | 21, 22 | vayas a tocar código, cortar ramas, abrir PRs, coordinar sesiones o ante un fallo transversal de CI |
| `protocolo-hydra-contratos` | 6, 7, 16 | cambies un contrato, migres un lector, cortes un incremento o el código cambie una regla documentada |
| `protocolo-hydra-dependencias` | 23 | actualices paquetes, revises Dependabot o toques secretos de CI |

Mínimo que aplica siempre aunque no cargues nada: **la primera orden de cualquier sesión que vaya a
tocar código es `bash scripts/estado-ramas.sh`**, y toda rama se corta de `origin/main`.
