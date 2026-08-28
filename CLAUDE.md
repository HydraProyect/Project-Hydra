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

## 3. Validación del instrumento

Antes de confiar en `git diff`, grep, conteos, logs, cobertura, ratchets, tests,
CI o guiones de gate, comprobar: ficheros untracked, ficheros generados, filtros
de salida, ensamblados actuales, build incremental vs. limpio, procesos
concurrentes, worktree correcto y commit/rama correctos.

**Un test o ratchet que no puede observar la propiedad no cuenta como evidencia.**

Prueba de sensibilidad: verde → mutación válida **que siga compilando** → rojo
**por el motivo esperado** → revertir → verde. Un fallo de compilación no
demuestra sensibilidad.

**Una mutación que pasa cuando predijiste rojo es un hallazgo, no un contratiempo.**
El reflejo barato es encogerse de hombros y probar otra; el caro y correcto es
preguntarse por qué no falló, porque la respuesta suele ser que la mutación no
tocaba lo que creías o que el test no observa lo que dice observar. Y ojo con el
caso peor: si esa misma mutación llega a fallar **por un motivo distinto** del
esperado, habrías registrado una prueba de sensibilidad falsa sin enterarte
nunca. Por eso el rojo tiene que serlo **por el motivo previsto**, comprobado en
el mensaje, no solo en el color.

**"Pasa en local" y "pasa en local aislado" son afirmaciones distintas.** Un test
ejecutado con `--filter`, fuera de su suite, es un instrumento diferente del mismo
test dentro de ella: no comparten arranque, ni estado compartido, ni contención por
la máquina. Reproducir un fallo exige las condiciones que lo producen, no las más
cómodas de ejecutar. Y al usar un filtro, comprobar en la salida **cuántos tests
corrieron**: un filtro que no casa con nada da verde.

**Una diferencia de duración que contiene una espera agotada es consecuencia del
fallo, no su causa.** Antes de leer un tiempo como síntoma, resta lo que el propio
fallo añade — si no, se confunde "esta rama va más lenta" con "esta rama tarda lo
mismo más el timeout".

**Un vigía que agota su tiempo sin observar el suceso sale con código 0.** Esperar
no es medir: distinguir siempre "terminó y el resultado es X" de "dejé de mirar".

**Tras un squash-merge el SHA nunca coincide, así que `--is-ancestor` da negativo
tanto cuando de verdad falta integrar como cuando tu rama YA se mergeó.** El
mismo síntoma para dos situaciones opuestas. Distinguirlas exige `gh pr view` o
comparar el CONTENIDO de los ficheros; el SHA no puede hacerlo. Antes de avisar a
nadie de que su trabajo se va a perder, diff de contenido — un cero ahí cierra la
pregunta, un SHA distinto no dice nada.

**Y antes de leer una hora, comprueba en qué reloj está escrita.** Un commit local
en `+0200` y un merge en UTC son comparables solo tras convertirlos; confundirlos
inventa discrepancias de dos horas que parecen trabajo sin integrar.

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

## 6. Cambios de contrato

Al cambiar un contrato: identificar **productores, consumidores, tests, ratchets y
documentación normativa**; actualizar implementación, tests y
ratchets/documentación; y verificar sensibilidad.

**No mantengas tests verdes que codifiquen deliberadamente el contrato anterior**
—siguen en verde solo mientras nadie ejercite la condición nueva—. Y no inventes
contratos que faltan para cerrar una implementación.

**Antes de migrar un lector, determinar si su DTO alimenta después un comando de
escritura** (read-modify-write). Si es así, trazar la cadena completa —lector →
DTO → llamador → comando → diff → efecto— y migrar los dos lados con el MISMO
criterio de visibilidad: cuando un diff calcula bajas por ausencia, **lo que el
usuario no pudo ver (soft delete, alcance de cartera, filtros) no puede
interpretarse como eliminado** — las bajas se definen sobre lo que pudo
desmarcar, no sobre lo que existe. Probar específicamente soft delete, alcance,
orden de listas y eliminación por ausencia antes de dar el lector por migrado.
Migrar solo la lectura, o solo la escritura, convierte un guardado "sin cambios"
en una pérdida de datos silenciosa.

## 7. Fronteras de incremento

Un incremento debe tener una frontera funcional clara. **No declares independiente
un cambio que necesite otro para ser ejecutable o desplegable.**

Antes de cortar ramas o PRs, determina contrato, productores, consumidores,
dependencias y unidad funcional de entrega. Los commits pueden separarse cuando
sea útil, pero **la unidad de merge debe ser realmente desplegable**: nada llega a
`main` en un estado conocido como inalcanzable.

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

## 11. Concurrencia y procesos largos

Nunca dos gates sobre el mismo worktree. Antes de un gate largo: apagar build
servers, comprobar ausencia de `testhost` y de otro gate, worktree estable, y
build limpio si CI lo requiere. Mientras corre: no tocar el worktree, no lanzar
otra ejecución, no cambiar cobertura compartida.

Antes de esperar un proceso largo, definir la señal de progreso esperada y cómo
verificarla. Si deja de progresar: CPU, memoria, salida, procesos, locks,
conexiones. **No esperar indefinidamente a un proceso bloqueado.**

## 12. Estado compartido y tests de base de datos

Los roles de PostgreSQL son **objetos de clúster**; las migraciones pueden afectar
a varias bases; una mutación de rol puede contaminar tests paralelos; y revertir
código no revierte infraestructura.

Las mutaciones sobre estado compartido se ejecutan **aisladas**, se restauran y se
verifica la convergencia. Nunca una mutación de seguridad destructiva en paralelo
con otros tests.

## 13. Datos de test y SQL

Preferencia: **entidades + `DbContext`** → **mecanismos reales de seed** → SQL
directo solo si es imprescindible.

Si el SQL directo es inevitable, **consultar `information_schema` primero**: no
asumir columnas, defaults ni constraints. **No escribir SQL de siembra de
memoria** — el síntoma (`23502`, `42703`) nunca se parece a lo que el test
pretendía medir.

## 14. Seguridad y multi-tenancy

No mezclar: `identidad ≠ contexto ≠ tenant ≠ capacidad ≠ autorización ≠ alcance ≠
sesión privilegiada`. **Una coordenada de contexto no es autoridad.**

En cualquier operación multi-tenant, comprobar explícitamente: usuario, tenant de
origen, tenant objetivo, workspace, capacidad, alcance, vigencia, RLS e identidad
efectiva de PostgreSQL.

**No debilitar RLS para acomodar código.** Si una operación cruza tenants de
verdad, debe existir un contrato o identidad que lo permita explícitamente.

## 15. Seeders y bootstrap

Distinguir **runtime** de **bootstrap administrativo**. Un seeder que cruza
tenants, opera sobre estado global o necesita privilegios administrativos **no se
resuelve debilitando RLS ni fingiendo un tenant**: la frontera se expresa mediante
identidad, capacidad o contexto administrativo explícito.

## 16. Integridad documental

Cuando el código cambie una regla, buscar la documentación y los comentarios que
describan el contrato anterior, y corregir la documentación normativa obsoleta
**como parte del incremento**. Los comentarios normativos describen el
comportamiento **actual**, no el histórico. No mezclar cambios documentales no
relacionados.

## 17. CI y gates

**El resultado de CI solo es evidencia del árbol que realmente ejecutó.** Antes de
interpretarlo: commit/rama, evento que lo disparó, número de tests, jobs
esperados, presencia de los tests nuevos, cobertura, migraciones y ausencia de
procesos concurrentes locales cuando aplique.

*"No hay checks"* no significa *"no hay CI"* sin comprobar el disparador. Y un
gate local no sustituye a CI si son instrumentos distintos.

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

## 21. Trabajo en paralelo y sesiones múltiples

Aplica cuando varias sesiones trabajan a la vez sobre el mismo repositorio, cada
una en su rama y su worktree, coordinadas por una de ellas.

**Una línea de trabajo por rama y por worktree.** Nunca dos sesiones sobre el mismo
worktree, ni dos ramas para el mismo cambio.

**Toda sesión declara su base antes de tocar nada**: `git fetch` y después
`git merge-base --is-ancestor origin/main HEAD`. Sin el fetch previo la
comprobación mira una referencia local obsoleta y no significa nada. Una rama
cortada de una base vieja produce diffs de cien ficheros que no son suyos, y el
diagnóstico se va detrás del conflicto en vez de detrás del cambio.

**Una sesión que termina su encargo empuja su rama al remoto antes de cerrarse**,
aunque no abra PR. Empujar no es publicar: es no dejar el trabajo colgando de un
directorio local que desaparece con la sesión.

**Las dudas van a la sesión coordinadora, no al usuario.** En trabajo nocturno o
desatendido el usuario no puede contestar a cada rama. Quien coordina resuelve con
los criterios ya fijados y solo eleva lo que sea una Stop Condition real.

**Una sesión detenida por el sistema de permisos no es una sesión fallida.**
Registrar la diferencia: no trabajó porque una barrera de seguridad actuó, que es
el comportamiento correcto. Y nunca pedir a otra sesión que ejecute lo que a ti te
fue denegado.

**Quien coordina mide antes de afirmar** el estado de otra sesión, de su rama o de
su progreso — y antes de declarar que dos trabajos se duplican.

## 22. Economía de la cola de CI

**Cada PR abierto cuesta una ejecución completa de CI por cada merge que ocurra**,
si el repositorio reactiva las ramas abiertas. Con N PRs simultáneos el coste no
crece con N: crece con su cuadrado.

**No apilar PRs**: se mergea de uno en uno, en verde y completo.

**Ante un fallo transversal de CI** —uno que rompe todos los PRs a la vez— lo
primero es despejar el carril del arreglo. Dejar corriendo ejecuciones cuyo
resultado ya se conoce no aporta información: aporta congestión, y retrasa
justamente lo único que desbloquea.

## 23. Dependencias de terceros y condiciones de uso

**Un salto de versión mayor es una pregunta de licencia antes que técnica.**
Comprobar el fichero de licencia del tag exacto y los metadatos del paquete
publicado **antes** de leer las notas de la release. Un blog no es fuente primaria.

**Las condiciones pueden cambiar sin que cambie la versión.** Hay herramientas que
exigen clave o pago según el **tipo de cuenta propietaria** del repositorio, no
según lo que uses ni desde cuándo. Mover el repositorio de sitio puede romper el CI
sin que nadie haya tocado una línea de código.

**Cuando una herramienta cambie de condiciones, fijar la versión libre y bloquear
la propuesta automática de actualización** — un comodín de versión decide solo, y
decide sin leerse la licencia.

**Los almacenes de secretos no son intercambiables.** Un secreto disponible para un
disparador puede no estarlo para otro. Comprobar que lo tiene **quien va a leerlo**,
no que exista en algún sitio.

> El objetivo del modo autónomo no es producir más cambios: es producir cambios
> **correctos y demostrables**, minimizando el retrabajo y las decisiones que hay
> que volver a revisar.
