# ANEXO_SERVICIOS_IA — Anexo de servicios de Inteligencia Artificial

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 14 del inventario de `LEGAL_FRAMEWORK.md` § 1, pieza nº 14, fase **post-MVP2 (agentes)**. Esqueleto base en `LEGAL_FRAMEWORK.md` § 2.11.
**Propósito**: Regular las funcionalidades de la plataforma que usan Inteligencia Artificial, conforme al Reglamento (UE) de IA, y fijar el compromiso diferencial de no usar los Datos de Servicio del Tenant para entrenar modelos de propósito general sin su consentimiento expreso. La plataforma ya usa IA hoy (extracción de campos y verificación de documentos) — este anexo debe revisarse contra el uso real de IA ya implementado, no solo contra agentes futuros.

## Qué pertenece aquí

- Transparencia sobre qué funcionalidades usan IA y con qué propósito.
- Compromiso sobre el uso (o no uso) de los Datos de Servicio para entrenamiento de modelos.
- Referencia al cumplimiento del Reglamento de IA de la UE aplicable.

## Qué NO pertenece aquí

- El régimen general de servicios experimentales no relacionados con IA → `ANEXO_SERVICIOS_EVOLUCION.md`.
- El detalle técnico de qué proveedores de IA se usan como subencargados → `LISTA_SUBENCARGADOS.md` (este anexo remite, no repite).

---

## 1. Alcance

Este anexo aplica a toda funcionalidad de la plataforma que emplee modelos de Inteligencia Artificial para procesar Datos de Servicio del Tenant — incluida la extracción y verificación automática de campos de documentos ya disponible en el producto, y cualquier funcionalidad de agentes que se incorpore en el futuro.

> Nota de coherencia con producto: la plataforma ya trata datos de reconocimientos médicos mediante funcionalidad de IA documental, sujeta hoy a un interruptor de activación apagado por defecto hasta resolver su cobertura contractual — ver `LEGAL_FRAMEWORK.md` § 1, pieza nº 4 del inventario ("DPA + Términos de Uso... o desactivar la IA sobre reconocimientos médicos hasta resolverlo"). Este anexo es, en parte, lo que permite reactivar esa funcionalidad con cobertura contractual.

## 2. Transparencia

Para cada funcionalidad de la plataforma que use IA, Hydra informará al Tenant, de forma accesible desde la propia aplicación o desde esta documentación, de:

- Qué función usa IA y con qué propósito.
- Si el procesamiento se realiza con un modelo propio o de un proveedor tercero (identificado en `LISTA_SUBENCARGADOS.md` cuando aplique).
- El grado de intervención humana en el resultado (p. ej. si una extracción automática requiere confirmación manual antes de tener efecto).

## 3. No uso de Datos de Servicio para entrenamiento de modelos de propósito general

**Compromiso adoptado**: Hydra no utiliza los Datos de Servicio de ningún Tenant para entrenar o mejorar modelos de IA de propósito general (los que un proveedor de IA pudiera reutilizar fuera del contexto de Hydra), salvo consentimiento expreso y específico del Tenant para ese fin. Este compromiso es más estricto que el estándar mínimo observado en el sector (la cláusula RIA de CTAIMA, genérica) y se alinea con el estándar esperable del mercado europeo.

## 4. Cumplimiento del Reglamento de IA de la UE

Las funcionalidades de IA de la plataforma se diseñan para cumplir el Reglamento (UE) de Inteligencia Artificial aplicable según el nivel de riesgo de cada funcionalidad concreta. `[PENDIENTE — clasificación de riesgo específica de cada funcionalidad de IA ya implementada o prevista, a determinar con la consulta legal antes de publicar este anexo en firme.]`

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.11 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/ANEXO_SERVICIOS_EVOLUCION.md` — régimen general de servicios experimentales.
- `docs/business/legal/DPA.md` — tratamiento de datos personales por las funcionalidades de IA.
- `docs/business/legal/LISTA_SUBENCARGADOS.md` — proveedores de IA como subencargados, cuando aplique.
- `docs/business/legal/README.md` — estado del paquete legal completo.
