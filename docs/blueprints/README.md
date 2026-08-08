# Blueprints

**Qué es un blueprint**: la especificación de **una superficie concreta** — qué muestra, con qué
piezas, con qué datos y con qué comportamiento propio.

**Qué no es**: una fuente de normativa. Un blueprint **hereda** contratos de `01`–`08` y solo
describe lo específico de su superficie. Si un blueprint necesita una regla que no existe en la
normativa, esa regla se decide primero — se registra en `DESIGN_DECISION_LOG.md` y se escribe en
el documento que corresponda — y luego el blueprint la consume.

## Autoridad

```
DESIGN_DECISION_LOG.md   qué se decidió y por qué
        ↓
01 – 08                  qué significa normativamente
        ↓
Blueprints               cómo se aplica a una superficie concreta   ← estás aquí
        ↓
Código                   cómo está implementado
```

**Nunca al revés**: un blueprint no puede contradecir la normativa, y el código no puede
contradecir al blueprint. Cuando se detecte una contradicción, se registra; no se resuelve en
silencio (DDL-024).

## Estructura de un blueprint

```
Estado y alcance          arquetipo, qué existe hoy, qué no
Contratos que hereda      tabla aspecto → documento (referencia, no copia)
Anatomía                  la superficie real, zona por zona
Comportamiento propio     solo lo que no está ya en 04/05
Datos                     qué consulta y qué ejecuta
Estados de la superficie
Divergencias con el código actual   ← obligatorio y honesto
Decisiones que la gobiernan
```

La sección de **divergencias es obligatoria**. Un blueprint que describe la superficie ideal sin
declarar en qué se separa de lo construido vuelve a crear el problema que este reset cerró:
documentación que se lee como descripción del software.

## Blueprints existentes

| Superficie | Arquetipo | Documento |
|---|---|---|
| **Centro 360** | Entity Workspace | [`CENTRO-360.md`](CENTRO-360.md) |
| Communication Workspace | Entity Workspace + Context Panel + Action Center | [`../COMUNICACIONES.md`](../COMUNICACIONES.md) — anterior al reset; pendiente de alinear con este formato |
