# LISTA_SUBENCARGADOS — Lista pública de subencargados del tratamiento

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal **y de verificación de infraestructura antes de publicarse**. Borrador nº 8 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1 y § 2.7. Anexo II del `DPA.md`. No es texto legal final.
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

**Existe una discrepancia real que debe resolverse antes de publicar esta lista**: la documentación de arquitectura permite explícitamente que los entornos de desarrollo/pruebas usen proveedores fuera de la UE (p. ej. Railway) "sin este requisito" de residencia UE, reservando el estándar EU-only para producción. Antes de publicar esta lista como parte del DPA público, es necesario **confirmar cuál es el proveedor de hosting efectivamente en uso en el entorno de producción real** (no el de desarrollo) y su región concreta, y corregir la tabla siguiente en consecuencia. Publicar un compromiso de residencia UE sin esa verificación sería una afirmación de cumplimiento no verificada — precisamente el tipo de error que este documento existe para evitar.

## 1. Tabla de subencargados

| Subencargado | Servicio prestado | Ubicación del tratamiento | Estado de verificación |
|---|---|---|---|
| `[PENDIENTE DE CONFIRMAR]` | Hosting de la aplicación en producción | `[PENDIENTE — confirmar región real del proveedor en uso]` | ⚠️ No verificado |
| `[Proveedor gestionado de PostgreSQL, a confirmar cuál]` | Base de datos de producción | `[PENDIENTE]` | ⚠️ No verificado |
| Amazon Web Services (AWS S3) | Almacenamiento de copias de seguridad y, según configuración, de documentos adjuntos | `[PENDIENTE — confirmar región AWS concreta en uso]` | ⚠️ No verificado |
| Amazon Web Services (AWS KMS) | Cifrado de las claves de protección de datos | `[PENDIENTE — confirmar región AWS concreta en uso]` | ⚠️ No verificado |
| `[Proveedor de correo transaccional, si aplica]` | Envío de notificaciones/correos del sistema | `[PENDIENTE]` | ⚠️ No verificado |

Cada fila marcada "No verificado" es un bloqueante para publicar este documento — no una aproximación aceptable para una lista que forma parte de un DPA público firmado frente a clientes.

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
