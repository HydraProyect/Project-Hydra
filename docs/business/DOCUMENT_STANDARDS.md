# DOCUMENT_STANDARDS — Guía editorial de `docs/business/`

**Tipo**: Normativo (meta-documento: define cómo se escriben los demás, no describe negocio)
**Estado**: Draft — guía inicial, pendiente de revisión por el propietario del producto. Se aplica ya como convención de trabajo mientras se revisa.
**Propósito**: Que la documentación de negocio siga siendo consistente y fácil de navegar cuando esta carpeta pase de 12 documentos a 40-50. Fija la plantilla de cada documento, el vocabulario de estado, cómo se registran las decisiones, qué es normativo frente a exploratorio, cómo se referencia documentación técnica, y las convenciones de tablas/diagramas/glosario. Es el único lugar donde se definen estas reglas — ningún otro documento de `docs/business/` debe fijar su propia convención distinta.

## 1. Alcance

Esta guía aplica a todo documento dentro de `docs/business/`, incluida ella misma. Dos documentos quedan exceptuados de la plantilla de la sección 2 porque tienen una función distinta y ya declaran su propia estructura en su propio cuerpo:

- `README.md` — es el índice, no un documento temático.
- `DECISION_LOG.md` — es un registro cronológico, no un documento temático (ver § 4).

Todos los demás — los 11 documentos temáticos actuales y cualquiera que se añada — siguen la plantilla de § 2.

## 2. Estructura de cada documento temático

Todo documento temático nuevo empieza con este bloque de cabecera, en este orden, antes de cualquier sección propia:

```
# NOMBRE — Título descriptivo

**Tipo**: Estratégico | Operativo
**Estado**: Draft | In Progress | Approved | Deprecated
**Propósito**: una o dos frases — para qué existe este documento y de qué depende / qué alimenta.

## Qué pertenece aquí
## Qué NO pertenece aquí
## Documentos relacionados
```

Reglas de cada bloque:

- **Tipo**: Estratégico si cambia poco y otros documentos dependen de él; Operativo si se revisa con frecuencia y aplica una decisión estratégica a algo concreto y datable. Ver `README.md` § "Documentos estratégicos vs. operativos" para la clasificación vigente.
- **Estado**: uno de los cuatro valores de § 3, nunca texto libre.
- **Propósito**: no describe el contenido (eso ya lo dicen los títulos de sección); dice por qué existe el documento y su relación con los demás.
- **Qué pertenece aquí / Qué NO pertenece aquí**: la sección "NO" es la que evita duplicación — debe nombrar el documento correcto para cada tema colindante, no solo decir "aquí no va".
- **Documentos relacionados**: enlaces a otros documentos de `docs/business/` y, cuando aplique, a la documentación técnica de la que depende o a la que alimenta (ver § 5).

## 3. Niveles de estado

| Estado | Significa |
|---|---|
| **Draft** | En redacción. El contenido, si existe, no está confirmado y puede cambiar sustancialmente. Estado de partida de todo documento nuevo. |
| **In Progress** | En desarrollo activo. Incompleto, pero ya usable como referencia parcial — algunas secciones están decididas, otras no. |
| **Approved** | Aprobado por el propietario del producto/negocio. Fuente de verdad vigente para ese tema; cualquier otro documento (de negocio o técnico) puede apoyarse en su contenido como decidido. |
| **Deprecated** | Superseded o ya no vigente. Se conserva como registro histórico, igual que `ADR-002-single-tenant.md` en el lado técnico — nunca se borra, se marca. |

Un documento solo pasa a `Approved` cuando el propietario del producto lo confirma explícitamente — no es una transición que un cambio de contenido pueda darse a sí mismo. Este es el documento canónico para este vocabulario; `README.md` lo referencia en vez de repetirlo.

## 4. Cómo se registran las decisiones

Cada vez que un documento pasa de `Draft`/`In Progress` a `Approved` (o de `Approved` a `Deprecated` por una decisión posterior), se añade una entrada el mismo día en `docs/business/DECISION_LOG.md`, con el formato fijado allí (fecha, decisión, motivo, alternativas descartadas, impacto). El documento temático conserva el contenido completo de la decisión; `DECISION_LOG.md` conserva el resumen cronológico de *cuándo y por qué* — no se duplica el desarrollo completo en el registro, solo se enlaza al documento que lo contiene.

## 5. Qué es normativo y qué es exploratorio

El campo **Estado** de cada documento *es* la señal de qué tan normativo es su contenido — no hace falta ninguna marca adicional:

- **Normativo** = `Approved`. Otro documento, técnico o de negocio, puede citarlo y apoyarse en él sin volver a confirmarlo.
- **Exploratorio** = `Draft` o `In Progress`. Es hipótesis de trabajo. Ningún otro documento debe citarlo como si fuera una decisión tomada — puede mencionarlo como "en discusión, ver `X.md`", nunca como dato firme.
- **Histórico, ya no aplicable** = `Deprecated`. Ni normativo ni exploratorio; se cita solo para explicar por qué se cambió de rumbo (ver `DECISION_LOG.md`).

## 6. Cómo referenciar ADR y documentos técnicos

- Se enlaza por nombre de archivo entre backticks, p. ej. `` `ADR-003-saas-multitenant.md` `` o `` `docs/MULTITENANCY.md` ``. Si la referencia es a una parte concreta, se cita la sección: `` `docs/MULTITENANCY.md` § 2 ``.
- Un documento de negocio puede describir la **consecuencia comercial** de una decisión técnica (p. ej. "el aislamiento por tenant de `docs/MULTITENANCY.md` es lo que hace vendible el modelo multi-tenant de `BUSINESS_MODEL.md`"), pero nunca reproduce el contenido técnico — enlaza.
- La regla inversa ya está establecida y en uso (ver los `TODO` añadidos en `docs/PLATFORM.md` § 4, `docs/MULTITENANCY.md` § 2, `ARQUITECTURA-INTEGRACIONES.md` y `ADR-003-saas-multitenant.md`): un documento técnico que roza contenido de negocio no lo desarrolla in situ, añade una referencia al documento de `docs/business/` correspondiente. Cualquier sesión que edite documentación técnica y encuentre una cifra, plan o decisión comercial sin confirmar debe seguir ese mismo patrón, no inventar el contenido de negocio ni copiarlo desde el documento técnico hacia aquí.
- Nunca se cita un ADR o documento técnico como si decidiera algo de negocio que en realidad está `Draft` en `docs/business/` — la fuente de verdad de negocio es siempre el documento de esta carpeta, nunca el técnico, aunque el técnico lo mencione primero cronológicamente.

## 7. Convenciones de tablas, diagramas y glosario

- **Tablas**: formato Markdown estándar (`| Columna | Columna |`), cabeceras en español, una fila por concepto. Se prefieren a listas largas cuando hay más de dos atributos por elemento (ver el uso ya establecido en `PROJECT.md` § "Glosario de dominio" y en `docs/PLATFORM.md` § 3 "Catálogo del kernel").
- **Diagramas**: se evitan salvo que aporten algo que una tabla no pueda mostrar (jerarquías, flujos). Cuando hagan falta, se usa un árbol en bloque de código con caracteres ASCII (`├──`, `└──`, `│`), el mismo estilo que ya usa `docs/PLATFORM.md` § 1 — no herramientas externas ni imágenes, para que el diagrama viva y se versione como el resto del texto plano del repositorio.
- **Glosario**: todo término con entrada en `docs/business/GLOSSARY.md` se usa tal cual está definido allí. Ningún documento de `docs/business/` (ni, idealmente, técnico) redefine un término localmente. Si un documento necesita matizar un término para su contexto concreto, enlaza a la entrada (`GLOSSARY.md#término`) y añade la matización como nota aparte — nunca como una definición alternativa que compita con la oficial.

## 8. Aplicación retroactiva

Los 11 documentos temáticos ya creados en `docs/business/` siguen ya esta plantilla — esta guía la formaliza, no la cambia. Cualquier ajuste futuro a la plantilla se decide y se documenta primero aquí, y se propaga a los documentos existentes en un cambio aparte, no como efecto secundario de editar un documento temático concreto.

## Documentos relacionados

- `README.md` — índice de la carpeta; referencia esta guía en vez de repetir su contenido.
- `DECISION_LOG.md` — registro de decisiones, formato de entrada.
- `GLOSSARY.md` — vocabulario oficial de términos de negocio.
- `CLAUDE.md` — reglas de trabajo generales del repositorio, incluida la disciplina Dominio → Arquitectura → Plataforma → Implementación con la que esta guía se mantiene coherente.
