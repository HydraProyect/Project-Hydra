# LISTA_SUBENCARGADOS — Lista pública de subencargados del tratamiento

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. La verificación de infraestructura que bloqueaba este documento ya se resolvió (2026-08-02, ver nota abajo). Borrador nº 8 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1 y § 2.7. Anexo II del `DPA.md`. No es texto legal final.
**Propósito**: Listar de forma pública, nombrada y versionada a los subencargados del tratamiento que Hydra utiliza para prestar el servicio — pieza inexistente hoy en todo el sector CAE (`LEGAL_FRAMEWORK.md` § 2.7), y requisito directo del régimen de subencargados del `DPA.md` § 5.

## Qué pertenece aquí

- Tabla versionada de subencargados: nombre, servicio prestado, ubicación del tratamiento.
- Mecanismo de suscripción a avisos de cambio.

## Qué NO pertenece aquí

- El régimen contractual de autorización y notificación de subencargados → `DPA.md` § 5 (este documento es su anexo).
- El registro completo de tratamientos → `RGPD-TRATAMIENTO-DATOS.md`.

---

## ⚠️ Nota de verificación pendiente — leer antes de usar este documento

Este borrador se construye a partir de las decisiones de infraestructura **documentadas como objetivo** en `docs/business/DATA_OWNERSHIP.md` (hosting de producción en la UE, tipo Hetzner/OVH) y de los proveedores técnicos identificados en la documentación del repositorio (almacenamiento compatible con S3, PostgreSQL gestionado, AWS KMS para cifrado de claves).

**Parcialmente resuelto (2026-08-02)**: las dos filas de AWS quedan confirmadas, no solo documentadas como objetivo — el log real de arranque en producción del mismo día registra "Data Protection: cifrado con AWS KMS operativo (clave alias/caemanager-dataprotection, región **eu-south-2**)", que es Zaragoza, España — dentro del EEE. La región de `AlmacenamientoS3`/`Backups__Aws` sigue el mismo patrón de configuración (`eu-south-2` como valor de ejemplo en `DEPLOY.md`); confirmar que coincide con la del log de KMS antes de dar también esas dos filas por cerradas al 100%.

**Resuelto (confirmado por el propietario del producto, 2026-08-02)**: el proyecto de Railway (aplicación y PostgreSQL) se migró a la región de Ámsterdam — dentro del EEE. Con esto, **las cinco filas de la tabla quedan dentro del compromiso EU-only** de `DATA_OWNERSHIP.md`. Queda como única tarea de detalle, no bloqueante para la consulta legal, confirmar el nombre exacto que Railway usa para esa región (p. ej. `europe-west4`/`eu-west1` u otro identificador propio de Railway) al completar la tabla en firme.

## 1. Tabla de subencargados

| Subencargado | Servicio prestado | Ubicación del tratamiento | Estado de verificación |
|---|---|---|---|
| Railway | Hosting de la aplicación en producción | Ámsterdam (Países Bajos) | ✅ Confirmado por el propietario del producto, 2026-08-02 — pendiente solo el nombre exacto de región que use Railway internamente |
| Railway (PostgreSQL) | Base de datos de producción | Ámsterdam (Países Bajos) | ✅ Confirmado por el propietario del producto, 2026-08-02 — pendiente solo el nombre exacto de región que use Railway internamente |
| Amazon Web Services (AWS KMS) | Cifrado de las claves de protección de datos | eu-south-2 (Zaragoza, España) | ✅ Confirmado por log real de producción, 2026-08-02 |
| Amazon Web Services (AWS S3) | Almacenamiento de copias de seguridad y, según configuración, de documentos adjuntos | `eu-south-2` (a confirmar que coincide exactamente con la región configurada del bucket real, no solo con el valor de ejemplo de `DEPLOY.md`) | 🟡 Probable, confirmar región exacta del bucket |
| `[Proveedor de correo transaccional, si aplica]` | Envío de notificaciones/correos del sistema | `[PENDIENTE]` | ⚠️ No verificado |

Ninguna fila queda ya como bloqueante de infraestructura. El detalle pendiente (nombre exacto de región de Railway, confirmación de que el bucket de S3 usa realmente `eu-south-2`) es de acabado, no de fondo — puede resolverse en paralelo al envío de este documento a la consulta legal.

## 2. Régimen de cambios

Conforme a `DPA.md` § 5, cualquier incorporación o sustitución de un subencargado se notificará a los Tenants con la antelación mínima allí fijada, mediante `[PENDIENTE — mecanismo de suscripción a avisos: email a los administradores de cada Tenant, changelog público, u otro medio a decidir]`.

## 3. Historial de versiones

| Versión | Fecha | Cambio |
|---|---|---|
| 0.1 (borrador) | `[fecha de este borrador]` | Primera versión — pendiente de verificación de infraestructura antes de publicarse como v1.0. |

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.7 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/DPA.md` § 5 — régimen contractual de subencargados.
- `docs/business/legal/ANEXO_MEDIDAS_SEGURIDAD.md` § 3 — residencia de datos, misma nota de verificación pendiente.
- `docs/business/DATA_OWNERSHIP.md` — decisión de infraestructura EU-only para producción que esta lista debe reflejar con exactitud.
- `RGPD-TRATAMIENTO-DATOS.md` — registro de tratamientos.
- `docs/business/legal/README.md` — estado del paquete legal completo.
