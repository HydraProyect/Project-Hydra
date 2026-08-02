# POLITICA_SUPRESION_RETENCION — Política de Supresión y Retención de Datos

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 9 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1 y § 2.8. No es texto legal final.
**Propósito**: Fijar, de forma pública, los plazos y el procedimiento de supresión de los Datos de Servicio de un Tenant — durante la suscripción, al terminarla, y en el caso excepcional de bloqueo legal por prescripción de responsabilidades. Es la traducción operativa y contractual del compromiso comercial de `docs/business/DATA_OWNERSHIP.md`, y del ciclo de retención técnico ya implementado en la plataforma (ver nota § 3).

## Qué pertenece aquí

- Qué ocurre con los datos de un Tenant mientras la suscripción está activa, cuando el propio Tenant los borra.
- Qué ocurre al terminar el contrato: ventana de exportación y plazo de supresión definitiva.
- La excepción de bloqueo legal por prescripción de responsabilidades.

## Qué NO pertenece aquí

- El compromiso comercial de propiedad y portabilidad en sí, y su función como argumento de venta → `docs/business/DATA_OWNERSHIP.md` (esta política lo contractualiza, no lo sustituye).
- El mecanismo técnico de retención y anonimización ya implementado en la plataforma → documentación técnica del repositorio (citada por referencia).

---

## 1. Durante la suscripción activa

El Tenant puede borrar sus propios Datos de Servicio en cualquier momento a través de la propia plataforma. El borrado realizado por el Tenant tiene efecto real: `[PENDIENTE — confirmar si existe un plazo de gracia/papelera antes del borrado definitivo, según la funcionalidad de producto vigente en cada momento]`.

> Nota de coherencia con producto: la plataforma ya implementa un ciclo de retención y anonimización de datos de trabajadores dados de baja, activable por configuración y sujeto a autorización expresa con fecha — sin camino posible a "ejecutada" sin esa autorización. Este documento describe el compromiso público frente al Tenant; el mecanismo técnico y sus invariantes no se reproducen aquí (regla de `DOCUMENT_STANDARDS.md` § 6).

## 2. Al terminar el contrato

- **Ventana de exportación**: durante un plazo de `[PENDIENTE — mercado de referencia: 30-60 días, cuestión abierta de validación en `LEGAL_FRAMEWORK.md` § 5.5]` desde la fecha de terminación, el Tenant puede solicitar y obtener una copia completa de sus Datos de Servicio en el formato estándar documentado previsto en `TERMINOS_Y_CONDICIONES.md` § 10.
- **Supresión definitiva**: transcurrida la ventana de exportación sin que el Tenant haya solicitado una prórroga justificada, Hydra suprimirá los Datos de Servicio activos y las copias de seguridad que los contengan, en un plazo máximo de `[PENDIENTE — plazo a fijar, coherente con el ciclo de rotación de copias de seguridad efectivamente en uso]` desde el fin de la ventana de exportación.

## 3. Excepción de bloqueo legal

Cuando la normativa aplicable exija la conservación de determinados datos más allá de los plazos anteriores (por ejemplo, para la prescripción de responsabilidades derivadas de la relación contractual o de obligaciones sectoriales), dichos datos quedarán **bloqueados** — conservados exclusivamente a ese fin, sin ningún otro tratamiento, hasta que la obligación de conservación decaiga, momento en el que se suprimirán conforme al régimen general de este documento.

## 4. Relación con el DPA

Esta política desarrolla, a efectos de cara al público, la obligación de Hydra como encargado del tratamiento prevista en `DPA.md` § 9 ("Fin del encargo") — ambos documentos deben leerse de forma coherente; si en algún momento entraran en conflicto, prevalece lo pactado específicamente en el DPA vigente con cada Tenant.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.8 — esqueleto y benchmark de esta pieza.
- `docs/business/DATA_OWNERSHIP.md` — compromiso comercial de propiedad y portabilidad que esta política contractualiza.
- `docs/business/legal/DPA.md` § 9 — obligación de Hydra como encargado al fin del encargo.
- `docs/business/legal/TERMINOS_Y_CONDICIONES.md` § 10 — derecho de portabilidad en la terminación del contrato.
- `docs/business/legal/README.md` — estado del paquete legal completo.
