# INBOUND_DOMAIN_GLOSSARY — Dominio funcional y glosario de plataformas Inbound externas

**Tipo**: Operativo
**Estado**: Draft — describe cómo funcionan plataformas *externas* al mercado español, no el dominio de Hydra.
**Propósito**: Vocabulario y modelo funcional neutral que la investigación original usa para comparar Nalanda/CTAIMA/Dokify/etc. entre sí. Útil como referencia al diseñar mapeos de conectores (`docs/INTEGRATION_GUIDELINES.md`), **nunca** como fuente de vocabulario de negocio de Hydra — eso ya lo fija `docs/business/UBIQUITOUS_LANGUAGE.md`.

## Qué pertenece aquí

- Vocabulario funcional usado por plataformas Inbound externas y sus sinónimos observados en el mercado.
- Catálogo documental y clasificación de documentos por titular/finalidad/vigencia, tal como lo organizan esas plataformas.
- Flujos funcionales y estados típicos observados en el mercado (altas, presentación documental, validación, incidencias, renovación).
- Modelo conceptual de entidades de una plataforma Inbound genérica, para comparar contra el dominio real de Hydra.

## Qué NO pertenece aquí

- Vocabulario de negocio de Hydra (Cliente, Empresa, Trabajador, Documento...) → `docs/business/UBIQUITOUS_LANGUAGE.md` y `PROJECT.md` § "Glosario de dominio". Este documento no lo redefine — ver tabla de colisiones abajo.
- Modelo de dominio implementado en Hydra → `DOMAIN.md`.
- Diseño de conectores/mapeo técnico real → `ARQUITECTURA-INTEGRACIONES.md`.

## ⚠️ Colisiones de nombre con el vocabulario oficial de Hydra

Antes de usar cualquier término de este documento en otro contexto, revisar esta tabla. **El término oficial de Hydra siempre gana** — los de la columna izquierda son cómo llama el *mercado* al mismo concepto, no alternativas válidas.

| Término usado en este documento (mercado) | Término oficial de Hydra | Nota |
|---|---|---|
| Cliente Principal | **Cliente** (`UBIQUITOUS_LANGUAGE.md`, `DOMAIN.md`) | Ya es un término `Approved` en Hydra con significado propio (empresa dueña de los Centros). No renombrar. |
| Empresa Contratista / Contrata | **Empresa** (`DOMAIN.md`) | Idéntico concepto — la contratista cuyos trabajadores realizan los trabajos. |
| Incidencia (documental: rechazo, caducidad, dato incompleto) | — | **Colisión real, no solo de nombre**: Hydra ya tiene una entidad `Incidencia` (`DOMAIN.md`), pero significa algo distinto — accidente o incumplimiento operativo de un Centro (`TipoIncidencia`, `GravedadIncidencia`), no un problema de validación documental. Si en el futuro se construye un concepto de "incidencia documental" (rechazo/caducidad/dato incompleto), **necesita un nombre distinto** para no colisionar con la entidad ya implementada — p. ej. algo derivado del estado `Rechazado`/`Vencido` de `Documento`, no una entidad nueva llamada igual. |
| Requisito (condición configurable con `ExpirationPolicy`/`ValidationPolicy`/`Target`) | `RequisitoDocumental` (`DOMAIN.md`) | Existe en Hydra pero como **texto libre** por Centro, no como motor de reglas configurable. La versión de este documento es más rica de lo implementado — ver `MARKET_GAPS_AND_POSITIONING.md` para si merece la pena ampliarlo. |
| Actividad | — (no existe) | `DOMAIN.md` confirma que Hydra no modela `Actividad` como entidad. Candidata a futura funcionalidad, no una decisión. |
| Maquinaria | — (no existe) | Igual que `Actividad`: no existe en `DOMAIN.md`. Candidata a futura funcionalidad. |
| Centro de Trabajo | **Centro** (`DOMAIN.md`) | Mismo concepto, nombre más corto en Hydra. |
| Validación | — | En Hydra el resultado de validar un `Documento` es un estado calculado (`CalculadoraEstadoDocumento`), no una entidad `Validation` separada como en algunos modelos de plataforma externa. |

## Alcance del dominio Inbound (según la investigación original)

Cubre: coordinación de actividades empresariales, gestión documental de contratistas, homologación y validación documental, plataformas externas, flujo documental, requisitos, estados documentales, incidencias, cumplimiento.

No cubre (explícitamente fuera de la investigación): PRL completa, vigilancia de la salud, evaluaciones de riesgo, planificación preventiva, gestión ambiental/calidad, RRHH, nóminas, ERP.

## Cuatro problemas que el mercado Inbound intenta resolver

1. **Fragmentación** — una misma empresa contratista opera en varias plataformas simultáneamente, cada una con nombres/estados/requisitos distintos.
2. **Duplicidad documental** — el mismo documento (seguro RC, formación, aptitud médica, ITV) se sube repetidamente en cada plataforma.
3. **Falta de estandarización** — no existe un estándar nacional de requisitos, estados, validaciones ni incidencias.
4. **Sobrecarga administrativa** — los gestores dedican gran parte de su jornada a tareas repetitivas (subir, revisar, consultar, comprobar caducidades).

Estos cuatro puntos son la base del ángulo "Hydra como capa de agregación" desarrollado en `MARKET_GAPS_AND_POSITIONING.md`.

## Modelo conceptual de una plataforma Inbound genérica

```
Cliente Principal
      │
      ├── Centro de Trabajo ── Requisitos
      │         │
      │    Actividad
      │         │
      ├── Empresa ── Trabajador / Vehículo / Maquinaria
      │
      └── Documentos ── Validaciones ── Incidencias ── Cumplimiento
```

Diagrama puramente funcional (no implica cardinalidad ni estructura física). Comparar contra el grafo real de `DOMAIN.md` para ver qué está construido, qué es distinto y qué falta.

### Entidades y su equivalente real en Hydra

| Entidad del mercado | Descripción funcional | Equivalente en `DOMAIN.md` |
|---|---|---|
| Cliente Principal | Define requisitos, gestiona centros, supervisa cumplimiento | `Cliente` |
| Empresa Contratista | Presta servicios, aporta trabajadores/vehículos, gestiona documentación | `Empresa` |
| Subcontrata | Contratada por otra contratista, requisitos adicionales posibles | `Subcontrata` (N:N con Cliente y Empresa) |
| Centro de Trabajo | Ubicación física con requisitos propios | `Centro` |
| Trabajador | Persona física que ejecuta actividades | `Trabajador` |
| Vehículo | Elemento móvil con documentación asociada (ITV, seguro) | `Vehiculo` |
| Maquinaria | Equipo técnico con documentación (marcado CE, revisiones) | **No existe** |
| Actividad | Trabajo concreto dentro de un centro, puede añadir requisitos | **No existe** |
| Documento | Evidencia que satisface uno o varios requisitos | `Documento` + `TipoDocumento` |
| Requisito | Condición configurable (obligatoriedad, vigencia, validación) | `RequisitoDocumental` (más simple: texto libre por Centro) |
| Validación | Proceso de aceptar/rechazar un documento | Estado calculado de `Documento` (`CalculadoraEstadoDocumento`), no entidad propia |
| Incidencia (documental) | Situación que impide el cumplimiento | **No existe con este significado** — ver tabla de colisiones |
| Cumplimiento | Resultado agregado de evaluar requisitos | No existe como entidad agregada; hoy se deriva visualmente del estado de documentos |

## Glosario de términos y sinónimos observados en el mercado

Cada entrada es cómo el *mercado* nombra el concepto — no vocabulario a adoptar en Hydra.

| Concepto de mercado | Sinónimos observados en distintas plataformas |
|---|---|
| Cliente Principal | Empresa Principal, Contratante, Titular del Centro |
| Empresa Contratista | Contrata, Empresa Externa |
| Centro de Trabajo | Centro, Instalación, Planta, Obra, Sede |
| Actividad | Trabajo, Servicio, Intervención, Actuación, Operación |
| Documento | Evidencia, Archivo, Registro |
| Requisito | Exigencia, Condición, Obligación |
| Validación | Revisión, Comprobación, Verificación |
| Incidencia | Observación, No conformidad, Defecto, Requerimiento, Corrección |
| Cumplimiento | Estado, Conformidad |
| Homologación | Evaluación de proveedores, proceso de acreditación (a veces incluye requisitos no-PRL) |

## Catálogo documental (clasificación funcional, no por nombre comercial)

Familias observadas de forma consistente en el mercado:

1. **Documentación Corporativa** — CIF/NIF, escrituras, poderes, Registro Mercantil, certificados fiscales/AEAT/TGSS, seguros (RC, profesional).
2. **Documentación Laboral** — contratos, altas/afiliación Seguridad Social, registros de jornada.
3. **Documentación Preventiva** — formación (inicial, específica, por puesto/riesgo), aptitud médica (solo existencia del documento, nunca dato clínico), procedimientos, evaluaciones de riesgos, planificación preventiva.
4. **Documentación Técnica** — certificados de conformidad, marcado CE, manuales, revisiones/inspecciones periódicas.
5. **Documentación de Vehículos** — permiso de circulación, ITV, seguro, permisos especiales.
6. **Documentación de Maquinaria** — número de serie/ficha técnica, marcado CE, revisiones, historial de mantenimiento.
7. **Documentación Ambiental** — gestores de residuos, autorizaciones, certificados ambientales.
8. **Documentación de Calidad** — ISO, auditorías, certificaciones, procedimientos internos.
9. **Documentación Contractual** — contrato principal, anexos, NDA, condiciones particulares.
10. **Documentación Operativa** — partes de trabajo, actas, checklists, permisos de trabajo, evidencias fotográficas (generada durante la prestación del servicio, no en el alta).

### Reglas de clasificación observadas

- Un documento pertenece a una única categoría principal, pero puede tener subcategorías.
- La relación Documento↔Requisito **no es 1:1**: un documento puede satisfacer varios requisitos (ej. una Aptitud Médica cubre aptitud sanitaria + vigilancia de la salud + requisito específico de acceso); un requisito puede necesitar varios documentos (ej. Seguro RC = póliza + recibo + certificado de cobertura). Esta es la relación de red, no de jerarquía, ya asumida en Hydra por el diseño de `TipoDocumento` (catálogo configurable) frente a `RequisitoDocumental` (texto libre por Centro) — si se decide enriquecer `RequisitoDocumental`, esta cardinalidad muchos-a-muchos es el patrón a replicar.
- La obligatoriedad de un documento depende del Cliente/sector/centro/actividad, nunca del documento en sí mismo — ya coherente con el enfoque de `TipoDocumento` configurable por tenant.

## Flujos funcionales observados (genéricos, no de una plataforma concreta)

Patrón común a la mayoría de plataformas analizadas:

```
Configuración → Registro → Presentación documental → Validación → Corrección → Cumplimiento → Mantenimiento
```

### Ciclo de vida funcional de un Documento (modelo de mercado)

```
Creado → Pendiente de presentación → Presentado → En validación
  → Aprobado | Rechazado → (si rechazado) Corrección → nueva presentación → En validación → Aprobado
  → Vigente hasta caducidad → Caducado → Sustituido/Archivado
```

Comparar con Hydra: el estado de `Documento` se **calcula, nunca se almacena** (`Vigente`/`Proximo`/`Urgente`/`Vencido`/`NoAplica`, ver `DOMAIN.md` § "Regla de negocio central") — un modelo más simple que el ciclo de arriba porque Hydra no modela un flujo de presentación/validación con intervención humana explícita; asume el documento ya aportado y calcula vigencia. Si Hydra construyera un flujo de validación con estados intermedios (Pendiente/En revisión/Rechazado), este ciclo de mercado es la referencia funcional a seguir — hoy no existe.

### Modelos de validación observados en el mercado

| Modelo | Descripción |
|---|---|
| Manual | Toda la documentación revisada por personas |
| Externalizada | Delegada en un equipo especializado (frecuente en Servicios de Prevención) |
| Automatizada | Reglas simples aplicadas sin intervención humana |
| Mixta | Combina automatización con revisión humana — el enfoque más extendido en el mercado |

### Modelos de operación observados

| Modelo | Descripción |
|---|---|
| Cliente Principal como Administrador | El más habitual — el Cliente Principal configura todo, la contratista solo responde |
| Validación Externalizada | Un tercero valida, el Cliente Principal solo consulta el resultado |
| Servicio de Prevención como Gestor | Un SPA administra la plataforma para varios Clientes Principales y contratistas — frecuente en consultoras de PRL, relevante para el modelo de `ADR-004-delegacion-consultoras-cae.md` |
| Plataforma como Servicio | El proveedor gestiona casi todo el proceso, validación incluida en el servicio contratado |

### Niveles de configuración de requisitos observados

| Nivel | Descripción |
|---|---|
| Catálogo fijo | El proveedor define los requisitos disponibles, poca personalización |
| Catálogo parametrizable | El cliente activa/desactiva requisitos — el modelo más frecuente |
| Completamente configurable | El cliente construye su propio modelo documental — típico de plataformas enterprise |

## Estados funcionales de referencia (vocabulario de mercado, no de Hydra)

| Dominio | Estados observados |
|---|---|
| Documento | Borrador, Pendiente, Presentado, En Validación, Aprobado, Rechazado, Corrección Requerida, Caducado, Archivado |
| Requisito | No Aplicable, Pendiente, Cumplido, Incumplido, Suspendido |
| Empresa | Activa, Pendiente, Suspendida, Inactiva |
| Trabajador | Activo, Pendiente, Apto, Restringido, No Apto, Inactivo |
| Incidencia (documental, mercado) | Abierta, En Gestión, Pendiente de Información, Resuelta, Cerrada |

## Documentos relacionados

- `docs/business/UBIQUITOUS_LANGUAGE.md` / `PROJECT.md` § "Glosario de dominio" — vocabulario oficial de Hydra, prevalece sobre este documento.
- `DOMAIN.md` — modelo de dominio real, verificado contra código.
- `docs/INTEGRATION_GUIDELINES.md` — dónde se usará este vocabulario al mapear un conector real.
- `MARKET_GAPS_AND_POSITIONING.md` — qué huecos de este modelo son oportunidades reales para Hydra.
