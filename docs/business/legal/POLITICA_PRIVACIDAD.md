# POLITICA_PRIVACIDAD — Política de Privacidad de Hydra (web + plataforma)

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 2 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1, patrón estructural de doble ámbito (§ 2.2). No es texto legal final.
**Propósito**: Informar, conforme al RGPD y la LOPDGDD, sobre el tratamiento de datos personales que realiza Hydra tanto como **responsable** (sitio web, captación comercial) como **encargado** (la plataforma SaaS, por cuenta de cada tenant). Es la pieza que un visitante o cliente potencial lee para decidir si confía en Hydra con sus datos — coherente con el compromiso EU-only y de minimización que `LEGAL_FRAMEWORK.md` § 3 adopta como diferencial.

## Qué pertenece aquí

- Identificación del responsable del tratamiento (ámbito web) y remisión al DPA (ámbito plataforma).
- Finalidades, categorías de datos, legitimación y plazos de conservación por cada canal de captación del sitio web.
- Compromiso de no transferencia internacional (EU-only).
- Derechos RGPD y cómo ejercerlos, en ambos ámbitos.

## Qué NO pertenece aquí

- El detalle de categorías de datos por módulo de la plataforma (documental, mensajería) y los subencargados → `RGPD-TRATAMIENTO-DATOS.md` (esta política remite, no reproduce).
- Las obligaciones de Hydra como encargado del tratamiento frente al tenant responsable → `DPA.md`.
- Las cookies → `POLITICA_COOKIES_WEB.md` y `POLITICA_COOKIES_PRODUCTO.md` (documentos separados por decisión de arquitectura legal, ver `LEGAL_FRAMEWORK.md` § 2.3).

---

## 0. Cómo leer este documento

Hydra trata datos personales en dos papeles distintos, que esta política distingue en todo momento:

- **Como responsable del tratamiento**, cuando gestiona el propio sitio web y la relación comercial con quien lo visita o contrata (§ 1-4 de este documento).
- **Como encargado del tratamiento**, cuando presta el servicio SaaS y procesa, por instrucción y cuenta de cada tenant, los datos que ese tenant introduce en la plataforma — por ejemplo, datos de trabajadores de empresas contratistas (§ 5 de este documento).

Si tu organización usa la plataforma Hydra, el responsable de tus datos como trabajador o usuario de esa organización **es tu empleador o la empresa que gestiona tu coordinación de actividades empresariales, no Hydra** — Hydra actúa por instrucción suya. Ver § 5.

## 1. Responsable del tratamiento (ámbito web)

- **Identidad**: `[RAZÓN SOCIAL PENDIENTE]` — ver `AVISO_LEGAL.md` § 1 para los datos identificativos completos, pendientes de la formalización legal en curso.
- **Contacto de privacidad**: `[PENDIENTE — patrón propuesto: privacidad@ dominio comercial, activo desde el día uno]`.
- **Delegado de Protección de Datos (DPO)**: `[PENDIENTE — cuestión abierta nº 3 de `LEGAL_FRAMEWORK.md` § 5: si el volumen y las categorías de datos tratados (incluida aptitud médica en el módulo documental de PRL) obligan a designar DPO. No se asume ni se descarta aquí.]`

## 2. Tratamiento como responsable — por canal de captación

| Canal | Finalidad | Categorías de datos | Legitimación | Conservación |
|---|---|---|---|---|
| Formulario de contacto | Responder a la consulta | Nombre, email, empresa, contenido del mensaje | Interés legítimo en atender la consulta / consentimiento implícito en el envío | Mientras dure la relación precontractual + `[PENDIENTE — plazo a fijar]` |
| Solicitud de demo / presupuesto | Gestionar la solicitud comercial y preparar la propuesta | Nombre, email, teléfono, empresa, cargo | Medidas precontractuales a solicitud del interesado (art. 6.1.b RGPD) | Hasta la decisión comercial + `[PENDIENTE — plazo a fijar]` |
| Newsletter / comunicaciones comerciales | Envío de contenido comercial sobre Hydra | Nombre, email | Consentimiento expreso (opt-in), revocable en cualquier momento | Hasta la revocación del consentimiento |
| Alta de cuenta de prueba/piloto | Aprovisionar el tenant de prueba | Nombre, email, empresa, datos de la cuenta de Identity | Ejecución de medidas precontractuales / ejecución del contrato una vez formalizado | Ver `POLITICA_SUPRESION_RETENCION.md` |

Los plazos de conservación marcados como `[PENDIENTE]` corresponden a la cuestión abierta nº 5 de `LEGAL_FRAMEWORK.md` § 5 (validación de cifras de compromiso) — se completan en la consulta legal, no se estiman aquí.

## 3. Destinatarios y transferencias internacionales

**Compromiso adoptado (hipótesis de diseño legal, ver `LEGAL_FRAMEWORK.md` § 3.2): sin transferencias de datos personales fuera del Espacio Económico Europeo.** Los datos recogidos por el sitio web y por la plataforma se alojan y procesan íntegramente dentro del EEE, con proveedores nombrados en `RGPD-TRATAMIENTO-DATOS.md` y en la lista pública de subencargados (`LISTA_SUBENCARGADOS.md`).

Este compromiso no requiere cláusulas contractuales tipo ni evaluaciones de transferencia internacional porque no existe transferencia que evaluar — es la base del diferencial "EU-only" frente a proveedores del sector que sí transfieren fuera del EEE.

> Nota de coherencia con producción real: este compromiso debe verificarse contra el proveedor de hosting de producción efectivamente en uso antes de publicar esta política — ver la nota correspondiente en `LISTA_SUBENCARGADOS.md` § "Nota de verificación pendiente".

## 4. Derechos de las personas interesadas (ámbito web)

Cualquier persona cuyos datos trate Hydra como responsable puede ejercer, en los términos previstos por el RGPD:

- Derecho de acceso, rectificación y supresión.
- Derecho a la limitación y oposición al tratamiento.
- Derecho a la portabilidad de los datos.
- Derecho a retirar el consentimiento en cualquier momento, sin que ello afecte a la licitud del tratamiento previo.

El ejercicio de estos derechos se realiza mediante escrito dirigido al contacto de privacidad indicado en § 1, adjuntando copia de un documento identificativo. El interesado tiene también derecho a presentar una reclamación ante la Agencia Española de Protección de Datos (www.aepd.es) si considera que el tratamiento no se ajusta a la normativa.

## 5. Tratamiento como encargado (ámbito plataforma)

Cuando una organización contrata Hydra como plataforma SaaS, **Hydra actúa como encargado del tratamiento** de los datos que esa organización (el tenant) introduce en la plataforma — típicamente, datos identificativos, laborales y de coordinación de actividades empresariales de trabajadores de empresas contratistas y subcontratistas.

En este ámbito:

- **El responsable del tratamiento es el tenant**, no Hydra. Cualquier persona cuyos datos figuren en la plataforma (por ejemplo, un trabajador de una empresa contratista) debe dirigir el ejercicio de sus derechos al responsable — su empleador o la organización que gestiona su coordinación de actividades empresariales — no a Hydra directamente.
- Si una solicitud de ejercicio de derechos llega por error a Hydra, se traslada al responsable correspondiente sin demora injustificada, con el objetivo de hacerlo no más tarde del día laborable siguiente a su recepción.
- Las obligaciones de Hydra como encargado (medidas de seguridad, subencargados autorizados, notificación de brechas, asistencia al responsable) se detallan en el `DPA.md`, público y aplicable a todo tenant desde la contratación.
- Las categorías de datos tratadas por módulo (documental, y mensajería cuando esté disponible) se detallan en `RGPD-TRATAMIENTO-DATOS.md`.

> **Cuestión abierta — no resuelta aquí (ver `LEGAL_FRAMEWORK.md` § 5.1 y § 5.2)**: la posición de una Consultora de PRL que opera un Delegated Workspace en nombre de un Cliente Delegante, y la figura bajo la que un Operador Delegado envía comunicaciones desde el tenant Microsoft 365 de un cliente, quedan pendientes de calificación legal (encargado, responsable o corresponsable según el criterio funcional de la AEPD). Esta política no asume ninguna calificación mientras esa cuestión siga abierta.

## 6. Menores

El Sitio y la plataforma están dirigidos exclusivamente a un uso profesional B2B (ver `TERMINOS_Y_CONDICIONES.md` § 3, ámbito subjetivo) y no están destinados a menores de edad.

## 7. Modificaciones de esta política

Hydra podrá modificar esta política para adaptarla a novedades legislativas o cambios en las finalidades del tratamiento. Los cambios sustanciales se comunicarán con antelación razonable a través del sitio web o, para clientes activos, por los canales de notificación previstos en `TERMINOS_Y_CONDICIONES.md` § 8.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.2 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/DPA.md` — obligaciones de Hydra como encargado del tratamiento.
- `RGPD-TRATAMIENTO-DATOS.md` — registro de tratamientos, categorías de datos por módulo, subencargados.
- `docs/business/legal/LISTA_SUBENCARGADOS.md` — lista pública versionada de subencargados.
- `docs/business/legal/POLITICA_COOKIES_WEB.md` / `POLITICA_COOKIES_PRODUCTO.md` — cookies, tratadas aparte.
- `docs/business/DATA_OWNERSHIP.md` — compromiso comercial de propiedad de los datos que esta política contractualiza en parte.
- `docs/business/legal/README.md` — estado del paquete legal completo.
