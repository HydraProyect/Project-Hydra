# docs/business/legal — Paquete legal público de Hydra

**Tipo**: Índice
**Estado**: Draft — los 16 documentos del inventario de `LEGAL_FRAMEWORK.md` § 1 tienen ya un primer borrador completo. **Ninguno ha pasado revisión legal ni puede pasar a `Approved` sin ella** (regla de `CLAUDE.md` y de `docs/business/README.md`: ningún documento de negocio decide por sí solo cumplimiento normativo).

## Por qué existe esta carpeta

`LEGAL_FRAMEWORK.md` es el esqueleto de benchmark que inventaria las 16 piezas del paquete legal público que Hydra necesita para vender comercialmente. Esta carpeta contiene el **borrador completo de cada una de esas 16 piezas**, expandido desde ese esqueleto — el insumo concreto que la consulta legal (especialista RGPD/LOPDGDD + mercantil) revisa, corrige y convierte en texto final. Ningún archivo de esta carpeta es, por sí mismo, un documento legal utilizable frente a un cliente real todavía.

## Cómo leer la tabla de estado

- **Listo para consulta legal**: el borrador está completo y las cuestiones abiertas que le afectan están señaladas explícitamente dentro del propio texto (no resueltas) — puede enviarse al especialista tal cual.
- **Bloqueado**: hay algo, más allá de la propia revisión legal, que impide considerar el borrador completo — una verificación de infraestructura pendiente, o una cuestión de aplicabilidad que ni siquiera este borrador puede asumir.
- **No aplica todavía**: la fase de producto que este documento regula (MVP2, post-MVP2) no existe todavía. El borrador queda preparado, no urgente.

| # | Documento | Fase | Estado | Bloqueado por |
|---|---|---|---|---|
| 1 | `AVISO_LEGAL.md` | Lanzamiento | Listo para consulta legal | Formalización de la identidad jurídica (razón social, NIF) — hecho administrativo, no cuestión legal de fondo |
| 2 | `POLITICA_PRIVACIDAD.md` | Lanzamiento | Listo para consulta legal | Cuestión abierta § 5.3 (DPO) afecta solo al dato de contacto; § 5.1/5.2 (Consultora/mensajería) señaladas y acotadas, no bloquean el resto |
| 3 | `POLITICA_COOKIES_WEB.md` | Lanzamiento | Listo para consulta legal | Falta el inventario técnico real de cookies (tabla vacía, no es un bloqueo legal) |
| 4 | `POLITICA_COOKIES_PRODUCTO.md` | Lanzamiento | Listo para consulta legal | Igual que el nº 3 |
| 5 | `TERMINOS_Y_CONDICIONES.md` | Lanzamiento | Listo para consulta legal | Cuestión abierta § 5.1 deja sin resolver la figura del Operador Delegado/Consultora (§ 4 del documento), señalada, no bloquea el resto de cláusulas |
| 6 | `DPA.md` | Lanzamiento | Listo para consulta legal | Cuestión abierta § 5.1 (posición de la Consultora) y § 5.5 (validar el compromiso de 48h de notificación de brechas) |
| 7 | `ANEXO_MEDIDAS_SEGURIDAD.md` | Lanzamiento | Listo para consulta legal | Depende de la misma verificación de infraestructura que el nº 8 |
| 8 | `LISTA_SUBENCARGADOS.md` | Lanzamiento | **Bloqueado** | Verificación de infraestructura de producción real (proveedor de hosting y su región) — no puede enviarse a consulta legal con datos no verificados |
| 9 | `POLITICA_SUPRESION_RETENCION.md` | Lanzamiento | Listo para consulta legal | Cuestión abierta § 5.5 (validar plazos de exportación/supresión) |
| 10 | `CONDICIONES_PRUEBA_GRATUITA.md` | Fase comercial | Listo para consulta legal (borrador menos desarrollado) | `LEGAL_FRAMEWORK.md` no lo detalla en § 2 — este borrador tiene menos respaldo de benchmark que el resto |
| 11 | `POLITICA_CONTENIDO_CONDUCTA.md` | MVP2 | No aplica todavía | El módulo de mensajería no existe |
| 12 | `ANEXO_SERVICIOS_TERCEROS_M365.md` | MVP2 | No aplica todavía | Depende además de una decisión de producto de MVP2 (§ 3 del propio documento) |
| 13 | `ANEXO_SERVICIOS_EVOLUCION.md` | Post-MVP2 | No aplica todavía | Sin funcionalidades en este régimen hoy |
| 14 | `ANEXO_SERVICIOS_IA.md` | Post-MVP2 (agentes) | Listo para consulta legal, aunque la IA documental ya existe hoy | Clasificación de riesgo por funcionalidad conforme al Reglamento de IA de la UE, pendiente de la consulta legal |
| 15 | `CANAL_DENUNCIAS.md` | Según obligación legal | **Bloqueado** | Cuestión abierta § 5.4 (¿aplica la Ley 2/2023 a Hydra?) — deliberadamente sin redactar hasta resolverla |
| 16 | `POLITICA_SOLICITUDES_GUBERNAMENTALES.md` | Madurez | Diferido a propósito | Fase de madurez — publicar antes sería una promesa sin estructura operativa real detrás |

## Qué se puede cerrar ya con este avance

"Cerrar" aquí no puede significar pasar ningún documento a `Approved` — eso exige la revisión legal explícita que `CLAUDE.md` requiere para todo lo normativo, y ninguna sesión de este repositorio puede saltársela. Lo que sí queda cerrado con este avance:

- **12 de los 16 documentos** (todos salvo el nº 8, nº 15, y los tres de MVP2/post-MVP2 aún no aplicables) están **listos para enviarse a la consulta legal tal cual**, con sus huecos señalados explícitamente en el propio texto.
- El nº 8 (`LISTA_SUBENCARGADOS.md`) tiene un bloqueo identificado y accionable **antes** de la consulta legal: verificar el proveedor de hosting real de producción. Esto es una tarea de infraestructura, no de derecho — se puede resolver en paralelo a la consulta legal de los otros 12.
- El nº 15 (`CANAL_DENUNCIAS.md`) queda correctamente como pregunta para el especialista, no como documento a redactar.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` — esqueleto e inventario del que derivan los 16 borradores.
- `docs/business/DATA_OWNERSHIP.md` — compromisos comerciales que este paquete contractualiza.
- `RGPD-TRATAMIENTO-DATOS.md` — registro de tratamientos.
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción" — DPA y Términos de Uso como bloqueantes heredados por todo este paquete.
- `docs/business/UBIQUITOUS_LANGUAGE.md` — vocabulario oficial usado en los 16 documentos.
- `docs/business/README.md` — índice general de `docs/business/`.
