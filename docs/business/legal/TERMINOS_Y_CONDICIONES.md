# TERMINOS_Y_CONDICIONES — Términos y Condiciones de Uso (contrato SaaS)

**Tipo**: Operativo
**Estado**: Draft — pendiente de revisión legal. Borrador nº 5 del paquete de Lanzamiento (MVP1) de `LEGAL_FRAMEWORK.md` § 1, el más extenso del paquete (§ 2.4, 14 cláusulas). No es texto legal final ni sustituye la consulta con un especialista mercantil/RGPD.
**Propósito**: Fijar el contrato de licencia de uso del SaaS Hydra entre la Empresa y cada Cliente — el documento que un cliente acepta al contratar y que rige la relación durante toda la suscripción. Contiene la cláusula de neutralidad contractual (§ 5), núcleo de la Opción B de posicionamiento legal del sector CAE.

## Qué pertenece aquí

- Definiciones, objeto y ámbito subjetivo del contrato.
- Figuras de acceso a la plataforma y sus condiciones.
- La cláusula de neutralidad: quién responde de qué respecto al cumplimiento CAE/RGPD del contenido introducido.
- Obligaciones del cliente, disponibilidad, cambios en el servicio, duración, terminación, responsabilidad, confidencialidad.

## Qué NO pertenece aquí

- Las obligaciones de Hydra como encargado del tratamiento → `DPA.md` (referenciado, no reproducido).
- Precios y condiciones económicas concretas → `PRICING.md` y la orden de pedido (este documento solo remite).
- SLA de soporte por plan → `PROFESSIONAL_SERVICES.md`.

---

## 1. Definiciones

Este contrato usa el vocabulario oficial de `docs/business/UBIQUITOUS_LANGUAGE.md` sin traducción ni redefinición. Se reproducen aquí solo a efectos de lectura contractual — la definición normativa vive en ese documento:

- **Tenant**: la organización que compra y usa Hydra; frontera absoluta de aislamiento (`docs/MULTITENANCY.md` § 1).
- **Cliente**: cualquier organización que contrata Hydra (`docs/business/UBIQUITOUS_LANGUAGE.md`).
- **Cliente Directo**: Cliente que gestiona su propia operación CAE dentro de Hydra.
- **Cliente Delegante**: Cliente que delega la operación de su CAE en una Consultora.
- **Consultora**: organización que compra Hydra y gestiona la CAE de varios Clientes Delegantes por delegación.
- **Delegated Workspace**: el espacio en el que una Consultora opera en nombre de un Cliente Delegante (`ADR-004-delegacion-consultoras-cae.md`).
- **Datos de Servicio**: el contenido que el Cliente introduce en la plataforma (documentos, datos de trabajadores, y mensajes cuando el módulo de mensajería esté disponible) — distinto de los datos de cuenta y de los datos de uso. Ver `docs/business/UBIQUITOUS_LANGUAGE.md`.

> Disciplina adoptada frente al benchmark: estas definiciones se usan de forma consistente en todo el contrato — la lección negativa observada en el sector (mezcla de LICENCIANTE/USUARIA/PROVEEDOR/CLIENTE por acumulación de versiones) se evita usando siempre el mismo término para el mismo concepto.

## 2. Objeto

Hydra concede al Cliente una licencia de uso **no exclusiva, revocable en los términos de este contrato, e intransferible/no sublicenciable**, sobre la plataforma SaaS Hydra, durante el plazo de vigencia de la suscripción contratada. Esta licencia no implica cesión alguna de derechos de propiedad intelectual sobre el software, que permanece en todo momento titularidad de Hydra.

## 3. Ámbito subjetivo

El servicio se presta **exclusivamente a usuarios profesionales en el marco de su actividad empresarial o profesional (relación B2B)**. Queda excluido cualquier uso por consumidores en los términos del texto refundido de la Ley General para la Defensa de los Consumidores y Usuarios. Esta delimitación simplifica el régimen aplicable al contrato y es coherente con la naturaleza del servicio (gestión de coordinación de actividades empresariales entre organizaciones).

## 4. Figuras de acceso a la plataforma

- **Usuario del Tenant**: persona autorizada por el Cliente para acceder a la plataforma en nombre de su organización, con el rol de autorización que el Cliente le asigne.
- **Tercero Autorizado**: contratista o subcontratista al que el Cliente autoriza a acceder a la plataforma con el único fin de aportar documentación relativa a su propia coordinación de actividades empresariales.
- **Operador Delegado**: usuario de una Consultora autorizado, mediante el mecanismo de Delegación (`ADR-004-delegacion-consultoras-cae.md`), a operar un Delegated Workspace en nombre de un Cliente Delegante.

> **Cuestión abierta — no resuelta aquí (ver `LEGAL_FRAMEWORK.md` § 5.1)**: el encaje contractual exacto de la figura de Operador Delegado, y de la Consultora como parte del contrato, depende de la calificación legal de la Consultora frente al RGPD (encargado, responsable o corresponsable, según el criterio funcional de la AEPD). Este apartado deja el hueco señalado y no propone una redacción hasta que la consulta legal resuelva esa cuestión.

## 5. Responsabilidad del cumplimiento CAE/RGPD del contenido — cláusula de neutralidad

**Esta es la cláusula central del posicionamiento legal de Hydra**, con triple respaldo de práctica de mercado (`LEGAL_FRAMEWORK.md` § 2.4.5):

1. El cumplimiento de las obligaciones derivadas de la coordinación de actividades empresariales (incluida la normativa de prevención de riesgos laborales aplicable) corresponde al Cliente, a sus contratistas y a los Terceros Autorizados que introducen datos en la plataforma — **no a Hydra**, salvo en su condición de encargado del tratamiento conforme al `DPA.md`.
2. El Cliente garantiza que dispone de base legítima suficiente para introducir en la plataforma los datos de terceros (trabajadores propios, de contratistas o de subcontratistas) que aporte.
3. El Cliente es responsable de informar a sus propios trabajadores y a los de sus contratistas del tratamiento de sus datos conforme a la normativa aplicable.

**Hydra no verifica la validez de las autorizaciones ni de la documentación introducida por el Cliente, ni impone fricción de interfaz por cada operador que accede a la plataforma** — esta es una práctica confirmada como uniforme en el sector (CTAIMA, Nalanda) y coherente con el diseño de producto ya establecido: el sistema calcula y muestra el estado documental que el Cliente introduce, sin arrogarse una función de verificación legal que corresponde al propio Cliente y a los mecanismos de coordinación de actividades empresariales previstos por la normativa aplicable.

## 6. Obligaciones del Cliente

El Cliente se compromete a:

- Hacer un uso lícito y adecuado de la plataforma, conforme a este contrato y a la normativa aplicable.
- Custodiar sus credenciales de acceso y las de los usuarios que autorice, y notificar a Hydra sin demora injustificada cualquier compromiso de seguridad de dichas credenciales.
- No realizar ingeniería inversa, descompilación o cualquier intento de acceder al código fuente de la plataforma salvo en los casos permitidos por la ley.
- No sobrecargar deliberadamente el servicio ni realizar pruebas de intrusión sin autorización expresa de Hydra.
- Mantener actualizados sus datos de contacto y de facturación.

## 7. Disponibilidad y mantenimiento

Hydra no garantiza la disponibilidad absoluta e ininterrumpida del servicio. Los niveles de servicio (SLA) concretos por plan de suscripción se definen en `docs/business/PROFESSIONAL_SERVICES.md` y, cuando aplique, en la orden de pedido correspondiente.

Hydra podrá realizar ventanas de mantenimiento programado, que procurará que sean breves, justificadas y preferentemente fuera del horario laboral habitual, notificándolas con antelación razonable salvo en casos de urgencia (seguridad, disponibilidad crítica).

## 8. Cambios en el servicio

- Los cambios en la plataforma impuestos por modificaciones normativas (incluidas las derivadas de RD 171/2004, criterios de la Inspección de Trabajo y Seguridad Social, o normativa de protección de datos) no constituyen incumplimiento contractual por parte de Hydra, aunque supongan una modificación de funcionalidades existentes.
- Los cambios sustanciales no derivados de una obligación normativa se notificarán al Cliente con una antelación mínima de un mes. Si el Cliente no está conforme, podrá rescindir el contrato durante ese plazo sin penalización; transcurrido el plazo sin oposición expresa, el cambio se entiende aceptado.

## 9. Duración, renovación y precios

La duración inicial y las condiciones de renovación se fijan en la orden de pedido correspondiente. Salvo pacto distinto, el contrato se renueva tácitamente por periodos sucesivos, pudiendo cualquiera de las partes oponerse a la renovación con un preaviso mínimo de `[PENDIENTE — mercado de referencia: 2 meses, a confirmar]`.

Los precios podrán actualizarse anualmente conforme a `docs/business/PRICING.md`. **Las condiciones particulares pactadas expresamente con un Cliente (por ejemplo, compromisos de precio por un plazo determinado) prevalecen sobre las condiciones generales de este documento** — cláusula de prevalencia necesaria para que este contrato conviva con acuerdos comerciales específicos ya asumidos con clientes concretos.

Las cifras y la mecánica de precios viven en `docs/business/PRICING.md` y en la orden de pedido; este contrato solo las referencia.

## 10. Terminación y devolución/portabilidad de datos

A la finalización del contrato, por cualquier causa, el Cliente tiene derecho a obtener una copia completa de sus Datos de Servicio, en un formato estándar documentado, **sin coste adicional**. Transcurrida la ventana de exportación fijada en `docs/business/legal/POLITICA_SUPRESION_RETENCION.md`, resulta de aplicación el régimen de supresión previsto en esa misma política.

Este derecho de portabilidad es la traducción contractual del compromiso comercial descrito en `docs/business/DATA_OWNERSHIP.md`.

## 11. Limitación de responsabilidad

Sin perjuicio de la responsabilidad que resulte inexcusable conforme a la normativa aplicable, la responsabilidad total de Hydra frente al Cliente derivada de este contrato queda limitada al importe efectivamente abonado por el Cliente durante la anualidad en que se produzca el hecho causante.

Quedan excluidos los daños indirectos, el lucro cesante y, en particular, cualquier daño derivado de fallos, indisponibilidad o cambios en servicios de terceros vinculados por decisión del propio Cliente (p. ej. su tenant de Microsoft 365 — ver `ANEXO_SERVICIOS_TERCEROS_M365.md`, aplicable cuando el módulo de mensajería esté disponible).

## 12. Confidencialidad

Ambas partes se obligan a mantener confidencial la información de carácter confidencial de la otra parte a la que tengan acceso con motivo de la ejecución de este contrato, obligación que subsistirá tras la finalización del contrato durante `[PENDIENTE — plazo a fijar, mercado de referencia: 2-5 años]`.

## 13. Referencias comerciales

El Cliente autoriza a Hydra a mencionar su nombre y logotipo como referencia comercial (por ejemplo, en materiales de marketing o casos de éxito), autorización revocable en cualquier momento mediante comunicación escrita. Esta cláusula formaliza como condición estándar lo ya negociado de forma ad hoc con el cliente fundador del producto.

## 14. Notificaciones, ley aplicable y fuero

> **Pieza jurisdiccional desacoplable — ver `LEGAL_FRAMEWORK.md` § 4.** El siguiente texto aplica al lanzamiento en España.

Las notificaciones entre las partes se realizarán por escrito a las direcciones de contacto indicadas en la orden de pedido o, en su defecto, a los datos de contacto identificados en `AVISO_LEGAL.md`.

Este contrato se rige por la legislación española. Para la resolución de cualquier controversia, las partes se someten a los juzgados y tribunales de `[PENDIENTE — domicilio/fuero a determinar]`, con renuncia expresa a cualquier otro fuero que pudiera corresponderles.

## Documentos relacionados

- `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.4 — esqueleto y benchmark de esta pieza.
- `docs/business/legal/DPA.md` — obligaciones de Hydra como encargado del tratamiento.
- `docs/business/legal/POLITICA_SUPRESION_RETENCION.md` — régimen de datos tras la terminación.
- `docs/business/DATA_OWNERSHIP.md` — compromiso comercial de propiedad y portabilidad de datos.
- `docs/business/PRICING.md` — condiciones económicas referenciadas, no fijadas aquí.
- `docs/business/PROFESSIONAL_SERVICES.md` — SLA de soporte por plan.
- `ADR-004-delegacion-consultoras-cae.md` — modelo de delegación cuyo encaje contractual queda pendiente de la cuestión abierta § 5.1.
- `docs/business/legal/README.md` — estado del paquete legal completo.
