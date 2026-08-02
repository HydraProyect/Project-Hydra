# ANEXO_SERVICIOS_EVOLUCION — Régimen de "servicios en evolución"

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 13 del inventario de `LEGAL_FRAMEWORK.md` § 1, pieza nº 13, fase **post-MVP2**. Esqueleto base en `LEGAL_FRAMEWORK.md` § 2.11.
**Propósito**: Separar dos regímenes de garantía dentro del mismo contrato — el producto ya estable (documental, y mensajería una vez consolidada) frente a funcionalidades experimentales publicadas en una lista versionada — para que lo experimental no diluya las garantías del núcleo del producto. Patrón adoptado de la distinción Zendesk entre "Enterprise Services" e "Innovation Services".

## Qué pertenece aquí

- Los dos regímenes de garantía y a qué funcionalidades aplica cada uno.
- El mecanismo de la lista publicada y versionada de servicios en evolución.

## Qué NO pertenece aquí

- El anexo específico de servicios de IA, que tiene régimen propio → `ANEXO_SERVICIOS_IA.md`.
- Las condiciones de prueba gratuita/beta para pilotos comerciales concretos → `CONDICIONES_PRUEBA_GRATUITA.md`.

---

## 1. Dos regímenes

- **Servicio General Disponible (GA)**: el producto ya estable — módulo documental desde el lanzamiento, y el módulo de mensajería una vez consolidado tras su periodo inicial. Sujeto a las garantías plenas de `TERMINOS_Y_CONDICIONES.md`.
- **Servicios en Evolución**: funcionalidades experimentales (por ejemplo, conectores de integración nuevos, agentes de IA) publicadas en una lista versionada y accesible al Cliente, sujetas a las condiciones específicas de este anexo, que pueden ser más limitadas que las del régimen GA.

## 2. Lista de servicios en evolución

`[PENDIENTE — se publica y mantiene actualizada cuando exista al menos un servicio en este régimen; hoy, sin funcionalidades post-MVP2 implementadas, esta lista está vacía.]`

## 3. Condiciones específicas de los Servicios en Evolución

- Pueden modificarse, suspenderse o retirarse con un preaviso menor al exigido para el régimen GA (`TERMINOS_Y_CONDICIONES.md` § 8), dado su carácter experimental.
- No están cubiertos por el SLA de disponibilidad aplicable al régimen GA.
- Su uso por el Cliente es opcional; el Cliente puede optar por no activarlos sin que ello afecte al resto del servicio contratado.

## 4. Paso de un servicio de "en evolución" a GA

Cuando un Servicio en Evolución alcanza estabilidad suficiente, pasa a formar parte del régimen GA mediante actualización de este documento y de la lista de § 2, sin que ello requiera una modificación del contrato general.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.11 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/ANEXO_SERVICIOS_IA.md` — régimen específico de servicios de IA.
- `docs/business/legal/TERMINOS_Y_CONDICIONES.md` § 8 — régimen general de cambios en el servicio.
- `docs/business/legal/README.md` — estado del paquete legal completo.
