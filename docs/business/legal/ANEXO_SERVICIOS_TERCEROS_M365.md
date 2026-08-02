# ANEXO_SERVICIOS_TERCEROS_M365 — Anexo de servicios vinculados de terceros (Microsoft 365)

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal **y de decisiones de producto de MVP2 aún no tomadas**. Borrador nº 12 del inventario de `LEGAL_FRAMEWORK.md` § 1, pieza nº 12, fase **MVP2 (mensajería)**. Esqueleto base en `LEGAL_FRAMEWORK.md` § 2.10.
**Propósito**: Regular la relación entre Hydra y los servicios de Microsoft 365 que el Cliente vincula por su propia decisión (buzón, tenant M365) para habilitar la mensajería — adaptación del patrón "Non-Zendesk Services" al modelo de acceso por autorización OAuth de Hydra, sin buzones propios. Es la pieza donde la decisión de arquitectura (Hydra no aloja buzones) se convierte en ventaja de riesgo contractual frente a competidores que sí alojan las comunicaciones.

## Qué pertenece aquí

- Titularidad y responsabilidad del buzón/tenant Microsoft 365 vinculado.
- Naturaleza revocable de la autorización OAuth y el alcance de permisos solicitado.
- Exclusión de responsabilidad por fallos o cambios del servicio de Microsoft.

## Qué NO pertenece aquí

- Qué se almacena en Hydra frente a qué permanece solo en Microsoft 365 — decisión de producto todavía no tomada (ver § 3 más abajo), no se anticipa aquí.
- El comportamiento del usuario dentro del canal → `POLITICA_CONTENIDO_CONDUCTA.md`.

---

## 1. Titularidad del buzón y del tenant Microsoft 365

El buzón de correo y el tenant de Microsoft 365 utilizados para la mensajería son **propiedad y responsabilidad exclusiva del Cliente**. Su configuración, seguridad, retención y disponibilidad corresponden al Cliente y a Microsoft como proveedor de dicho servicio — no a Hydra.

## 2. Autorización OAuth

Hydra accede al buzón del Cliente exclusivamente mediante autorización OAuth otorgada expresamente por el Cliente (o por la persona que este designe con capacidad para ello dentro de su tenant Microsoft 365), **revocable por el Cliente en cualquier momento** desde su propia administración de Microsoft 365 o desde la plataforma Hydra.

El alcance de permisos solicitado por Hydra se documenta de forma explícita en el momento de la conexión, y se limita al mínimo necesario para prestar la funcionalidad de mensajería contratada.

## 3. Qué almacena Hydra y qué no

> **Pendiente de decisión de producto — no se anticipa en este borrador (ver `LEGAL_FRAMEWORK.md` § 2.10).** La distinción exacta entre lo que Hydra persiste (por ejemplo, metadatos o estado de una conversación) y lo que permanece exclusivamente en el tenant Microsoft 365 del Cliente (por ejemplo, el cuerpo íntegro de cada mensaje) depende de decisiones de diseño técnico de MVP2 todavía no cerradas. Este apartado se completa cuando esa decisión de producto exista, no antes — y su resultado tiene efecto directo sobre las categorías de datos de `RGPD-TRATAMIENTO-DATOS.md` y sobre el argumento comercial de § 5 más abajo.

## 4. Exclusión de responsabilidad

Hydra no responde por fallos, límites de servicio, cambios o interrupciones del servicio de Microsoft 365, al ser este un servicio de terceros vinculado por decisión del propio Cliente. Esta exclusión se incorpora también en `TERMINOS_Y_CONDICIONES.md` § 11 (limitación de responsabilidad).

## 5. Argumento comercial derivado

Al no alojar los buzones ni el contenido íntegro de las comunicaciones (según se determine en § 3), Hydra custodia estructuralmente menos datos de comunicaciones que una plataforma que aloja la totalidad del contenido — diferencial de riesgo comunicable frente a clientes potenciales una vez la decisión de § 3 esté cerrada.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.10 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/POLITICA_CONTENIDO_CONDUCTA.md` — conducta dentro del canal de mensajería.
- `docs/business/legal/TERMINOS_Y_CONDICIONES.md` § 11 — limitación de responsabilidad general.
- `docs/business/DATA_OWNERSHIP.md` § "Arquitectura de correo y garantías de continuidad" — decisión de diseño de la que este anexo es la traducción contractual.
- `docs/business/legal/README.md` — estado del paquete legal completo.
