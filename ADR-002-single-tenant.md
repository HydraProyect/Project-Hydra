# ADR-002 — Vuelta a single-tenant: uso interno, no SaaS multi-cliente (por ahora)

**Estado**: Decidido (2026-07-18). **Supersede parcialmente** a `ADR-001-multitenant.md`.

## Decisión

CAE Manager (Project Hydra) deja de tratarse como un producto en camino a venderse como SaaS multi-cliente. Pasa a ser **software de uso interno de una única organización** (la propia empresa PRL que lo opera, gestionando su cartera real de Clientes/Centros/Trabajadores). `ADR-001-multitenant.md` no se anula ni se borra — documenta una decisión real que se tomó y se analizó a fondo (incluida la auditoría de las 39 queries), y sigue siendo la referencia técnica correcta **si algún día se retoma** la vía multi-cliente. Lo que cambia es el destino de la construcción: no se implementa `TenantId` en este repositorio mientras el uso sea interno.

**Si en el futuro se decide explotar el modelo SaaS multi-cliente**, se hace en un **repositorio distinto** (fork/duplicado de este, en el punto del historial que se decida), nunca añadiendo `TenantId` a este repo mientras sirve datos reales de producción de una sola organización — evita el riesgo de una migración de esquema arriesgada sobre datos reales por una necesidad que todavía no existe (YAGNI, mismo criterio que ya rige el resto del proyecto, ver `PROJECT.md`).

## Por qué este cambio no es "menos trabajo de cumplimiento"

Uso interno **no** significa que el RGPD/LOPDGDD dejen de aplicar. La empresa sigue siendo responsable del tratamiento de datos personales —incluida categoría especial de salud— de los trabajadores de sus clientes reales (Cobega, Mahou, Heineken, etc., según el dominio ya modelado). Lo único que desaparece es lo que dependía específicamente de vender el software a terceros como producto: aislamiento entre organizaciones distintas, DPA con clientes externos, términos de uso de un SaaS, alta de tenants con aceptación de contrato. Todo lo demás de la auditoría de cumplimiento sigue vigente sin cambios (retención, derecho al olvido, DPIA, RAT, DPA con subencargados como AWS/Anthropic/Microsoft/Railway, auditoría de accesos de lectura, cifrado en reposo, etc.).

---

## Puntos detallados para implementar esta decisión

### 1. Formalizar la decisión en la documentación de arquitectura

- [ ] **`ADR-001-multitenant.md`**: añadir una nota al principio del documento (bajo el `Estado`) del tipo: *"Nota (2026-07-18): la vía multi-cliente queda en pausa — ver `ADR-002-single-tenant.md`. Este documento se conserva como referencia técnica válida para cuando/si se retome."* No reescribir el resto del ADR — sigue siendo el análisis correcto de cómo se haría.
- [ ] **`ARCHITECTURE.md`**: no menciona hoy "SaaS multi-cliente" directamente (describe capas técnicas), pero revisar cualquier referencia a "el destino del producto" y alinearla con `PROJECT.md` tras el punto siguiente.
- [ ] **`PROJECT.md`** § "A quién sirve": ya dice *"Modelo de despliegue v1: single-tenant... si en el futuro se necesita servir a varias organizaciones independientes... se aborda como evolución explícita"* — esta frase ya es coherente con la decisión. Confirmar que sigue siéndolo tras los cambios de roadmap del punto 2 (no hace falta reescribirla, pero si `ROADMAP.md` llegó a implicar lo contrario en algún punto, `PROJECT.md` es la fuente de verdad a la que alinear el resto).
- [ ] **`RGPD-TRATAMIENTO-DATOS.md`** § 6 (subencargados) y § 7: quitar o marcar como "no aplica mientras sea uso interno" cualquier frase que dé por hecho una relación cliente↔proveedor SaaS con terceros (ej. el DPA "entre Project Hydra y sus clientes" del Issue #13 pasa a no aplicar; el DPA con los subencargados AWS/Anthropic/Microsoft/Railway **sigue aplicando igual**, no se toca).

### 2. Re-priorizar el backlog de hardening (`ROADMAP.md` § Iniciativa de hardening)

- [ ] **Punto 3 (Decisión multi-tenant)**: cambiar el estado de "✅ decidida y documentada" a "⏸️ en pausa — ver `ADR-002-single-tenant.md`". El trabajo de implementación (columna `TenantId`, Global Query Filter, interceptor de sellado, 7 índices únicos compuestos) **no se planifica** mientras el uso sea interno.
- [ ] **Issue #8** (GitHub, "implementar TenantId"): re-etiquetar de `bug`/`feature` pendiente a algo como `on-hold` o `future-fork`, con un comentario que enlace a este ADR. No cerrarlo como "no se va a hacer" — cerrarlo como "en pausa, con criterio de reapertura explícito" (ver punto 5).
- [ ] **Punto 8 (Umbral de calidad antes del primer cliente corporativo real)**: esa frase entera de `ROADMAP.md` ("`TenantId` implementado y auditado... es un bloqueante real, no opcional, para vender a más de un cliente sin fugas de datos entre ellos") sigue siendo cierta **para el escenario de venta**, pero deja de ser un bloqueante para el uso actual (interno, un solo cliente real: la propia empresa). Reescribir esa frase para distinguir explícitamente "umbral para uso interno" de "umbral para retomar venta a terceros" (ver tabla del punto 6).

### 3. Corregir el hallazgo que sigue vigente aunque no haya multi-tenant: control de acceso por cartera dentro de la propia organización

Aunque no haya organizaciones distintas que aislar, el modelo de roles (Fase 31, `IAlcanceDatosService`) sí distingue carteras dentro de la misma empresa (un Gestor CAE solo debería ver sus Clientes asignados) — y hoy ese control se rompe por accesos directos por Id (`ObtenerDocumentoPorIdQuery`, `ObtenerClientePorIdQuery`, el endpoint `/documentos/{id}/archivo`, etc., que no comprueban `IAlcanceDatosService`). Este arreglo **no depende de si hay multi-tenant o no** — es la prioridad técnica real que sí corresponde a este repo ahora mismo.

- [ ] Priorizar en el backlog inmediato (no en el fork futuro): behavior/verificación de alcance en las consultas `*PorId*`, ver detalle ya identificado en la auditoría de cumplimiento de esta sesión.
- [ ] Extender lo mismo al endpoint de descarga de archivo de Documento.

### 4. Confirmar qué del cumplimiento normativo NO cambia (para no perderlo de vista)

Estos puntos de la auditoría de cumplimiento siguen siendo aplicables sin ninguna rebaja, uso interno o no, y no deben archivarse junto con el punto multi-tenant:

| Punto | Por qué sigue aplicando igual |
|---|---|
| Retención real / derecho al olvido (Issues #10/#11) | El trabajador conserva su derecho Art. 17 frente a la empresa, la venda o no como SaaS |
| DPIA (tratamiento de salud a escala) | La obligación nace del volumen/categoría del dato, no del modelo comercial |
| RAT (Art. 30) y valoración de DPO | Aplica a cualquier responsable del tratamiento |
| DPA con subencargados (AWS, Anthropic, Microsoft, Railway) — Issue #13, mitad "subencargados" | Siguen siendo encargados del tratamiento de la empresa, se venda o no el software |
| Auditoría de lectura de datos sensibles (Issue #12) | Responsabilidad proactiva (Art. 5.2 RGPD) no depende de terceros pagando |
| Cifrado en reposo, backups, MFA, cabeceras HTTP, gestión de incidentes | Higiene de seguridad básica, independiente del modelo comercial |

- [ ] Revisar que ningún Issue de este bloque se haya cerrado o despriorizado por error al re-triar el backlog junto con el punto 3.

### 5. Definir el criterio de reapertura (para cuando/si se retome la vía SaaS)

- [ ] Documentar en el propio `ADR-002` (sección siguiente de este mismo archivo, a completar cuando aplique) o en un ADR-003 posterior: qué señal de negocio dispara la decisión de construir el fork multi-tenant (ej. "un segundo cliente real interesado en contratar el software como servicio").
- [ ] Checklist explícito de qué hay que llevarse al fork y qué no:
  - Código y estructura del proyecto: sí.
  - Datos reales de producción (clientes, trabajadores, documentos): **no**, nunca — el fork parte de una base de datos vacía o con datos sintéticos.
  - Historial de Auditoría real: no.
  - Claves de cifrado (`dataprotection-keys/`) de producción: no — el fork genera las suyas propias desde el principio.
  - `ADR-001-multitenant.md` como guía técnica de implementación: sí, íntegro.
- [ ] Reconfirmar en el momento de crear el fork que la regla de `ADR-001` ("no autoservicio ni facturación sin `TenantId` implementado y auditado") sigue siendo la condición de salida a producción del fork, no solo una nota histórica.

### 6. Tabla resumen — qué bloquea qué, a partir de ahora

| | Uso interno (este repo, ahora) | Venta SaaS a terceros (fork futuro, si aplica) |
|---|---|---|
| `TenantId` / aislamiento multi-organización | No requerido | Bloqueante — ver `ADR-001` |
| Control de acceso por cartera dentro de la empresa (Hallazgo IDOR) | **Bloqueante, ya** | Bloqueante (con blast radius mayor) |
| Retención / derecho al olvido | Bloqueante | Bloqueante |
| DPIA / RAT / DPO | Bloqueante | Bloqueante |
| DPA con subencargados (AWS/Anthropic/Microsoft/Railway) | Bloqueante | Bloqueante |
| DPA con clientes externos / Términos de Uso SaaS | No aplica | Bloqueante |
| Alta de tenant + aceptación de contrato | No aplica | Bloqueante |

---

## Checklist de validación de que este ADR quedó bien implementado

- [ ] `ADR-001-multitenant.md` anotado con la nota de pausa, sin reescribir su contenido técnico.
- [ ] `ROADMAP.md` § Iniciativa de hardening, punto 3 y punto 8, actualizados para reflejar la pausa.
- [ ] Issue #8 (GitHub) re-etiquetado, no cerrado como descartado.
- [ ] `RGPD-TRATAMIENTO-DATOS.md` § 6/7 revisado para no dar por hecho una relación SaaS con terceros.
- [ ] Ningún Issue de cumplimiento normativo (retención, DPIA, RAT, DPA con subencargados, auditoría de lectura) despriorizado por confusión con el punto multi-tenant.
- [ ] Corrección del control de acceso por cartera en consultas `*PorId*` (punto 3) priorizada de forma independiente, con su propio criterio de aceptación.
