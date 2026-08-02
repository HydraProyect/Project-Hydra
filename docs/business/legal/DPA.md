# DPA — Acuerdo de Encargo de Tratamiento (art. 28 RGPD), público y descargable

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 6 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1 y § 2.5. **Documento bloqueante para la salida a producción SaaS** según `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción". No es texto legal final.
**Propósito**: Regular, conforme al art. 28 RGPD, el tratamiento de datos personales que Hydra realiza por cuenta de cada Tenant en su condición de encargado del tratamiento. Publicado y descargable — decisión de formato que solo CTAIMA adopta hoy en el sector CAE (`LEGAL_FRAMEWORK.md` § 2.5), pensada para eliminar la negociación de un DPA por cliente.

## Qué pertenece aquí

- Partes, roles, objeto, duración, naturaleza y finalidad del tratamiento encargado.
- Categorías de datos por módulo (remitiendo al detalle de `RGPD-TRATAMIENTO-DATOS.md`).
- Régimen de subencargados, asistencia al responsable, notificación de brechas, auditorías.
- Régimen al fin del encargo.
- Anexo I (medidas técnicas y organizativas) y Anexo II (subencargados), por remisión a documentos propios.

## Qué NO pertenece aquí

- El detalle técnico de las medidas de seguridad → `ANEXO_MEDIDAS_SEGURIDAD.md` (Anexo I de este DPA).
- El listado versionado de subencargados → `LISTA_SUBENCARGADOS.md` (Anexo II de este DPA).
- El registro de tratamientos y bases legales detallado → `RGPD-TRATAMIENTO-DATOS.md`.
- La posición de la Consultora en el triángulo Hydra-Consultora-Cliente Delegante — cuestión abierta, ver § 1 más abajo.

---

## 1. Partes y roles

Este Acuerdo de Encargo de Tratamiento de Datos ("DPA") forma parte integrante y se incorpora por referencia a los `TERMINOS_Y_CONDICIONES.md` aceptados por cada Cliente al contratar la plataforma Hydra.

- El **Tenant** (Cliente) actúa como **responsable del tratamiento** de los datos personales que introduce en la plataforma.
- **Hydra** actúa como **encargado del tratamiento**, procesando dichos datos exclusivamente por cuenta e instrucciones documentadas del responsable.

> **Cuestión abierta — no resuelta aquí (ver `LEGAL_FRAMEWORK.md` § 5.1)**: la posición de una Consultora de PRL que opera un Delegated Workspace en nombre de un Cliente Delegante queda **expresamente fuera de este DPA** hasta que la consulta legal determine su calificación (encargado, responsable o corresponsable, según el criterio funcional de la AEPD). Este DPA regula, por ahora, exclusivamente la relación bilateral Hydra-Tenant.

## 2. Objeto, duración, naturaleza y finalidad

- **Objeto**: la prestación del servicio SaaS Hydra descrito en `TERMINOS_Y_CONDICIONES.md`, en la medida en que dicha prestación implique el tratamiento de datos personales por cuenta del responsable.
- **Duración**: la del contrato de suscripción vigente entre las partes, incluida cualquier prórroga.
- **Naturaleza del tratamiento**: alojamiento, almacenamiento, procesamiento, organización y puesta a disposición de los datos que el responsable introduce en la plataforma, mediante los medios técnicos descritos en el Anexo I.
- **Finalidad**: exclusivamente la prestación del servicio contratado — gestión documental de coordinación de actividades empresariales y, cuando esté disponible, mensajería asociada.

## 3. Categorías de datos por módulo

El detalle completo de categorías de datos, base legal y finalidad específica por módulo vive en `RGPD-TRATAMIENTO-DATOS.md` (regla de `DOCUMENT_STANDARDS.md` § 6: este DPA remite, no reproduce). A efectos de este Acuerdo, se identifican los siguientes bloques:

| Módulo | Categorías (resumen) |
|---|---|
| Documental (MVP1) | Datos identificativos, laborales y de PRL de trabajadores de empresas contratistas y subcontratistas (incluye, cuando aplique, datos de aptitud médica derivados de reconocimientos — categoría especial art. 9 RGPD); identificativos de vehículos y equipos. |
| Usuarios de la aplicación | Datos identificativos y de contacto de las personas autorizadas por el Tenant a usar la plataforma. |
| Mensajería (MVP2, cuando esté disponible) | Contenido de comunicaciones entre las partes de la coordinación de actividades — **categorías potencialmente abiertas, entrada nueva pendiente de desarrollo en `RGPD-TRATAMIENTO-DATOS.md` cuando el diseño de producto de MVP2 esté cerrado.** |

## 4. Instrucciones del responsable

Hydra tratará los datos personales únicamente siguiendo instrucciones documentadas del responsable, incluida la transferencia a un tercer país u organización internacional, salvo que resulte obligada a ello en virtud del Derecho de la Unión o de los Estados miembros. En este último caso, Hydra informará al responsable de esa exigencia legal previa al tratamiento, salvo que el Derecho aplicable lo prohíba por razones importantes de interés público.

Hydra garantiza que las personas autorizadas a tratar los datos personales se han comprometido a respetar la confidencialidad o están sujetas a una obligación de confidencialidad de naturaleza estatutaria, y han recibido la formación necesaria.

## 5. Subencargados

El responsable autoriza con carácter general la subcontratación por parte de Hydra de otros encargados del tratamiento (subencargados), siempre que:

- Figuren en la lista pública versionada `LISTA_SUBENCARGADOS.md` (Anexo II de este DPA).
- Hydra imponga a cada subencargado, mediante contrato, las mismas obligaciones de protección de datos que las establecidas en este DPA.
- Hydra notifique al responsable cualquier cambio previsto en la incorporación o sustitución de subencargados, con una antelación mínima de `[PENDIENTE — mercado de referencia: 1 mes, a validar en consulta legal — cuestión abierta § 5.5]`, dando al responsable la oportunidad de oponerse a dichos cambios por motivos justificados relacionados con la protección de datos.

## 6. Asistencia al responsable

Hydra asistirá al responsable, en la medida de lo posible, en el cumplimiento de su obligación de responder a las solicitudes de ejercicio de derechos de los interesados. Cualquier solicitud recibida directamente por Hydra se trasladará al responsable sin demora injustificada, con el objetivo de hacerlo no más tarde del día laborable siguiente a su recepción.

Hydra asistirá igualmente al responsable en el cumplimiento de las obligaciones relativas a la seguridad del tratamiento, notificación de violaciones de seguridad, evaluaciones de impacto relativas a la protección de datos y consultas previas a la autoridad de control, teniendo en cuenta la naturaleza del tratamiento y la información de que dispone Hydra.

## 7. Notificación de violaciones de seguridad

Hydra notificará al responsable **sin dilación indebida**, con el objetivo de compromiso de **48 horas desde su verificación** `[cifra a validar con la consulta legal y con la capacidad operativa real antes de comprometerla — cuestión abierta § 5.5]`, cualquier violación de la seguridad de los datos personales de la que tenga conocimiento, junto con la información razonablemente disponible para que el responsable pueda, si procede, cumplir con su obligación de notificación a la autoridad de control y, en su caso, a los interesados.

## 8. Auditorías

Hydra pondrá a disposición del responsable la información necesaria para demostrar el cumplimiento de las obligaciones establecidas en este DPA, y permitirá y contribuirá a la realización de auditorías por el propio responsable o por un tercero independiente autorizado por este, con las siguientes salvedades:

- Las auditorías se solicitarán con antelación razonable y se realizarán en horario laboral, minimizando la interrupción de la actividad de Hydra.
- Quedan expresamente excluidos como auditores terceros que sean competidores directos de Hydra en el mercado de plataformas de coordinación de actividades empresariales.
- La información obtenida en la auditoría queda sujeta a un compromiso de confidencialidad por parte de quien la realice.

## 9. Fin del encargo

A la terminación de la prestación del servicio, Hydra, a elección del responsable, suprimirá o devolverá todos los datos personales tratados por cuenta del responsable, y suprimirá las copias existentes, salvo que el Derecho de la Unión o de los Estados miembros exija la conservación de los datos personales — en cuyo caso la conservación queda bloqueada a ese único fin, durante el plazo de prescripción de las responsabilidades que la justifiquen. El régimen operativo completo de esta obligación vive en `POLITICA_SUPRESION_RETENCION.md`.

## 10. Anexos

- **Anexo I — Medidas técnicas y organizativas**: `ANEXO_MEDIDAS_SEGURIDAD.md`.
- **Anexo II — Lista de subencargados**: `LISTA_SUBENCARGADOS.md`.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.5 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/TERMINOS_Y_CONDICIONES.md` — contrato marco del que este DPA forma parte.
- `docs/business/legal/ANEXO_MEDIDAS_SEGURIDAD.md` — Anexo I.
- `docs/business/legal/LISTA_SUBENCARGADOS.md` — Anexo II.
- `RGPD-TRATAMIENTO-DATOS.md` — registro de tratamientos, categorías de datos por módulo.
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción" — este DPA como bloqueante.
- `ADR-004-delegacion-consultoras-cae.md` — modelo de delegación cuya posición en este DPA queda pendiente de la cuestión abierta § 1.
- `docs/business/legal/README.md` — estado del paquete legal completo.
