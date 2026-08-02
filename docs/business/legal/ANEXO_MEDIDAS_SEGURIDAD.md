# ANEXO_MEDIDAS_SEGURIDAD — Anexo público de medidas técnicas y organizativas (TOMs)

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 7 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1 y § 2.6. Anexo I del `DPA.md`. No es texto legal final.
**Propósito**: Describir, como activo de confianza público, las medidas técnicas y organizativas que Hydra aplica para proteger los Datos de Servicio — pieza inexistente hoy en todo el sector CAE (`LEGAL_FRAMEWORK.md` § 2.6). **Regla de oro seguida en este borrador: describir solo controles reales y verificables de Hydra, nunca promesas de una organización de otro tamaño.**

## Qué pertenece aquí

- Descripción, a nivel de garantía verificable (no de detalle de implementación), de los controles técnicos y organizativos realmente vigentes en la plataforma.
- Compromiso de que las medidas solo pueden reforzarse, nunca degradarse materialmente, durante una suscripción activa.

## Qué NO pertenece aquí

- El detalle técnico de implementación (nombres de clases, mecanismos internos) → documentación técnica del repositorio (`docs/MULTITENANCY.md`, `ARCHITECTURE.md`), citada aquí solo por referencia, nunca reproducida (regla de `DOCUMENT_STANDARDS.md` § 6).
- El listado de subencargados que operan la infraestructura → `LISTA_SUBENCARGADOS.md` (Anexo II del DPA).

---

## 1. Cifrado

- **En tránsito**: todas las comunicaciones entre el usuario y la plataforma, y entre la plataforma y sus proveedores de infraestructura, se cifran mediante TLS.
- **En reposo**: los datos sensibles almacenados (credenciales de acceso a portales externos, claves de cifrado del propio sistema) se cifran en reposo. `[Nota de verificación pendiente: confirmar antes de publicar que el cifrado de las claves de cifrado en sí — no solo de las credenciales que protegen — está activo en el entorno de producción concreto, y no solo disponible como opción configurable.]`

## 2. Aislamiento por tenant

El control central de la plataforma. Cada Tenant tiene sus Datos de Servicio lógicamente separados del resto — ningún dato de un Tenant es visible ni accesible para otro por diseño, no por configuración incidental. El detalle técnico de este mecanismo (filtrado global por tenant, y una segunda capa de aislamiento a nivel de base de datos) vive en `docs/MULTITENANCY.md`, que este anexo no reproduce.

Este control equivale al patrón de "separación lógica" (*Logical Separation*) que proveedores SaaS maduros exigen a sus propios subencargados — en Hydra es un principio de diseño de primera clase, no una capa añadida después.

## 3. Residencia de datos

**Compromiso adoptado: tratamiento exclusivamente dentro del Espacio Económico Europeo.** Los proveedores de hosting y almacenamiento concretos, y su ubicación, se listan de forma nombrada y transparente en `LISTA_SUBENCARGADOS.md`.

> **Nota de verificación pendiente, no resuelta en este borrador**: este anexo no puede publicarse en firme hasta confirmar que el proveedor de hosting efectivamente en uso en el entorno de **producción** cumple este compromiso. Ver la nota correspondiente en `LISTA_SUBENCARGADOS.md` § "Nota de verificación pendiente" antes de dar este apartado por cerrado.

## 4. Control de acceso

- Acceso a los sistemas de producción bajo el principio de mínimo privilegio.
- Credenciales de acceso individuales, no compartidas, para el personal con acceso a sistemas de producción.
- Revocación de accesos al cese de la relación con el personal correspondiente.
- Autenticación reforzada (segundo factor) para los roles de mayor privilegio dentro de la propia plataforma, aplicable a los usuarios de los Tenants.

## 5. Copias de seguridad

Copias de seguridad periódicas de la base de datos y de las claves de cifrado del sistema, cifradas, con un procedimiento de recuperación verificado. `[Nota: la frecuencia y el plazo de retención de las copias, y la fecha del último ensayo de recuperación documentado, se completan aquí una vez esa información quede fijada operativamente — no se estima en este borrador.]`

## 6. Gestión de vulnerabilidades

- Actualización periódica de dependencias de software para incorporar correcciones de seguridad conocidas.
- Escaneo automatizado de vulnerabilidades como parte del proceso de construcción del software antes de cada despliegue.
- **En términos realistas para el tamaño actual de Hydra**: no se compromete una auditoría de seguridad externa periódica ni un equipo de seguridad dedicado a tiempo completo — ver `docs/business/legal/LEGAL_FRAMEWORK.md` § 1, pieza nº 25 del inventario ("pentest externo"), pendiente como actividad futura, no como compromiso ya asumido.

## 7. Evolución de las medidas

Las medidas descritas en este anexo pueden reforzarse en cualquier momento sin necesidad de notificación previa. Hydra se compromete a no degradar materialmente estas medidas durante la vigencia de una suscripción activa sin informar previamente al Cliente.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.6 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/DPA.md` — Acuerdo de Encargo de Tratamiento del que este documento es el Anexo I.
- `docs/business/legal/LISTA_SUBENCARGADOS.md` — Anexo II del DPA, listado nombrado de proveedores.
- `docs/MULTITENANCY.md` — mecanismo técnico de aislamiento por tenant, citado por referencia.
- `docs/business/legal/README.md` — estado del paquete legal completo.
