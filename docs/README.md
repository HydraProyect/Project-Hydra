# Gobernanza documental

Este documento fija **de dónde viene la autoridad** de un documento en este repositorio. Aplica
a toda la documentación, no solo a la de diseño.

## 1. La regla de autoridad

> **Un documento no obtiene autoridad por ser más antiguo, más detallado, más cercano al código
> ni por contener una especificación más concreta. La autoridad viene exclusivamente de su
> posición en la cadena normativa vigente.**

Esta regla existe porque el problema que motivó el reset documental de 2026-08 no fue falta de
documentación: fue que varios documentos decían cosas distintas y **nadie sabía cuál mandaba**.

## 2. La cadena normativa

```
DESIGN_DECISION_LOG.md      qué se decidió y por qué          ← autoridad sobre decisiones
        ↓
01 – 08                     qué significa normativamente      ← autoridad sobre especificación
        ↓
docs/blueprints/            cómo se aplica a una superficie
        ↓
código                      cómo está implementado
```

**Nunca al revés.** Consecuencias que no son negociables:

- **Si el código contradice la normativa, el código no modifica la normativa.** Se registra como
  divergencia. Cambiar la norma exige una decisión nueva que sustituya explícitamente a la
  anterior.
- **Si un blueprint contradice `01`–`08`**, hay conflicto documental: se registra, no se resuelve
  en silencio (DDL-024).
- **Si `01`–`08` contradice el Decision Log**, manda el Log sobre *qué se decidió*.
- **Si un documento histórico contradice el sistema actual, no hay conflicto**: es exactamente lo
  esperado. Por eso conservar la historia es seguro.

## 3. Documentos históricos: se conservan, no gobiernan

Los documentos superados **no se borran**. Tienen valor: muestran qué se pensó, qué problemas
aparecieron y por qué una decisión cambió.

Pero:

> **Un documento histórico no puede utilizarse como fuente para una decisión de diseño o de
> implementación.** Si una regla necesaria no está en la cadena normativa vigente, **no se
> recupera del histórico**. Hay que: (1) localizar la decisión correspondiente en el Decision
> Log, o (2) registrar una decisión nueva antes de incorporarla a la normativa.

Copiar una regla de un documento archivado a código es exactamente el fallo que este esquema
existe para impedir, y ninguna cabecera lo evita por sí sola: por eso la ubicación, la cabecera y
la verificación automática actúan juntas.

### Dónde vive lo histórico

```
docs/archive/            registro histórico — NO NORMATIVO
  design/                sistema de diseño anterior al reset de 2026-08
  ...                    informes y runbooks ya ejecutados
docs/ux-audit/           auditoría UX de 2026-08 — evidencia, no normativa
```

Todo documento archivado declara en su cabecera, sin ambigüedad: su estado, por qué documento
fue sustituido, qué decisiones lo reemplazan y por qué se conserva.

## 4. Migración ejecutada (2026-08-08)

| Documento | Estado | Sustituido por | Acción |
|---|---|---|---|
| `DESIGN_SYSTEM.md` | Histórico | `02`, `06`, `07`, `08` | Archivado en `docs/archive/design/` |
| `UX_PATTERNS.md` | Histórico | `04` | Archivado en `docs/archive/design/` |
| `PLAN-CONTEXT-WORKSPACE.md` | Parcialmente absorbido | `05` § 3 | Archivado; su § 0 se conserva como evidencia |
| `PLAN-MASTER-DETAIL-WORKSPACE.md` | Histórico (ya superado antes) | `05` | Archivado; su § 2 se conserva como evidencia |
| `docs/ux-audit/**` | Evidencia histórica | — | Se queda en su sitio, marcado no normativo |
| `docs/COMUNICACIONES.md` | Mixto | Parte I vigente (dominio); Parte II es un blueprint | Pendiente de alinear con `docs/blueprints/` |

## 5. Verificación automática

`scripts/validar-gobernanza-docs.py` comprueba en CI que la frontera se mantiene:

1. Ningún documento normativo cita un documento archivado **como fuente**.
2. Todo documento archivado declara su cabecera de **no normativo**.
3. Ningún documento fuera de la cadena se autodenomina normativo, canónico o fuente de verdad.
4. Los valores visuales concretos solo aparecen en `02` y `06`.
5. Todo documento normativo declara **estado** y **límite de implementación** (DDL-023).
6. Toda decisión citada existe en el Decision Log.

Ejecutar en local:

```bash
python scripts/validar-gobernanza-docs.py
```

Un fallo de esta comprobación **no es un problema de formato**: significa que la frontera de
autoridad se ha roto en alguna parte, y eso es lo que degradó la documentación antes del reset.

## 6. Qué hacer cuando falta una regla

1. ¿Está en `01`–`08`? Se aplica.
2. ¿Está en el Decision Log pero no escrita en la normativa? Se escribe en el documento que
   corresponda y se anota.
3. ¿No está en ninguno de los dos? **Se decide**: entrada nueva en el Decision Log con motivo,
   impacto y qué reemplaza; después se escribe en la normativa.
4. ¿Aparece en un documento histórico? Ver el punto 3. Encontrarla ahí es **contexto**, no
   autorización.
