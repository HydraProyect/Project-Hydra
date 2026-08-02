# DATA_OWNERSHIP — Propiedad y portabilidad de los datos por tenant

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Definir, desde el ángulo comercial y contractual, de quién son los datos que un tenant introduce en Hydra, qué puede hacer Hydra con ellos, y qué ocurre con ellos si el tenant cancela — el argumento de confianza que se comunica a clientes potenciales y la base de negocio sobre la que se redactan el DPA y los Términos de Uso mencionados en `ADR-003-saas-multitenant.md`.

## Qué pertenece aquí

- Compromiso comercial de propiedad de los datos: el tenant es propietario de sus datos, Hydra actúa como encargado del tratamiento.
- Política de portabilidad: qué puede exportar un tenant y en qué formato.
- Qué ocurre con los datos de un tenant al cancelar (plazo de retención comercial, borrado, exportación final) — a nivel de compromiso de negocio, no de mecanismo técnico.
- Cómo se comunica esta política a clientes potenciales como argumento de venta (confianza, cumplimiento).
- Relación con el DPA y los Términos de Uso por tenant que `ADR-003-saas-multitenant.md` marca como condición de salida a producción SaaS — sin redactar aquí esos documentos legales, que requieren revisión legal explícita (regla de `CLAUDE.md`).

## Qué NO pertenece aquí

- Implementación técnica del aislamiento por tenant (Global Query Filter, `TenantId`, partición de almacenamiento) → `docs/MULTITENANCY.md`.
- Base legal, categorías de datos personales y de salud tratados, subencargados → `RGPD-TRATAMIENTO-DATOS.md`.
- La redacción final del DPA y los Términos de Uso — son documentos legales independientes que requieren revisión legal antes de cualquier implementación (regla ya establecida en `CLAUDE.md`).

## Arquitectura de correo y garantías de continuidad (Draft, 2026-07)

- **Identidad emisora de correo (decisión de diseño)**: la comunicación operativa CAE sale
  siempre desde el buzón del propio SPA (conexión OAuth a su Microsoft 365 vía Graph API), nunca
  desde un dominio propio de Hydra. Hydra es la interfaz sobre el correo del cliente, no el
  remitente — evita erosión de entregabilidad/reputación del cliente y mantiene sus
  comunicaciones dentro de su propio tenant.
- **Implicación de cumplimiento**: conectar el buzón de un cliente vía OAuth es acceso a datos
  personales a escala. Refuerza — no sustituye — la necesidad de DPA antes de producción ya
  prevista como condición de salida en `ADR-003-saas-multitenant.md`.
- **Garantías de continuidad ofrecidas al Cliente Fundador**: portabilidad total de datos
  exportable en cualquier momento; posibilidad de depósito de código en escrow, liberable solo
  si la sociedad cesa actividad — presentada como concesión reservada (no ofrecida por defecto,
  solo si el cliente la solicita).
- **Decisión de infraestructura con impacto directo en este documento**: alojamiento de
  producción en la Unión Europea (proveedor tipo Hetzner/OVH u equivalente) como argumento de
  confianza y simplificación del DPA. Entornos de desarrollo/pruebas pueden seguir en
  proveedores no-UE (p. ej. Railway) sin este requisito.

*Draft — pendiente de desarrollo completo de este documento y de redacción legal del DPA.*

## Documentos relacionados

- `RGPD-TRATAMIENTO-DATOS.md` — tratamiento de datos personales, base legal, subencargados.
- `docs/MULTITENANCY.md` — mecanismo técnico de aislamiento que hace cumplible este compromiso.
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción SaaS" — DPA y Términos de Uso como bloqueantes.
- `docs/business/legal/` — borradores completos del DPA y los Términos de Uso derivados de este compromiso comercial (`docs/business/legal/LEGAL_FRAMEWORK.md` § 1), pendientes de revisión legal.
