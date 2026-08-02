# LEGAL_FRAMEWORK — Esqueleto del paquete legal público de Hydra

**Tipo**: Operativo
**Estado**: Draft — esqueleto de trabajo construido por benchmark de mercado (CTAIMA, Dokify, Nalanda, 6conecta, Zendesk). **Ningún contenido de este documento es texto legal final ni ha pasado revisión legal.** Su función es que la redacción legal no empiece en blanco y que arquitectura conozca las piezas que el producto tendrá que soportar.
**Propósito**: Inventariar los documentos legales públicos que Hydra necesita para lanzar comercialmente, con el esqueleto de cláusulas de cada uno y su fuente de benchmark, como insumo de la consulta legal (especialista RGPD/LOPDGDD + mercantil) exigida por `CLAUDE.md` para todo documento legal. Alimenta `DATA_OWNERSHIP.md` (compromisos comerciales) y `RGPD-TRATAMIENTO-DATOS.md` (registro de tratamientos), y hereda las condiciones de salida a producción de `ADR-003-saas-multitenant.md` (DPA y Términos de Uso como bloqueantes).

## Qué pertenece aquí

- El inventario del paquete legal público objetivo, por fase de producto (MVP1 documental, MVP2 mensajería, post-MVP2 agentes/IA).
- El esqueleto de cláusulas de cada documento, con la práctica de mercado que lo respalda y las adaptaciones propias de Hydra.
- Los principios transversales adoptados como hipótesis de diseño legal (neutralidad contractual, EU-only, minimización).
- Las cuestiones abiertas que **bloquean** la redacción de cláusulas concretas y que se resuelven en la consulta legal — señaladas como tales, nunca resueltas aquí.

## Qué NO pertenece aquí

- La redacción final de ningún documento legal — requiere revisión legal explícita (regla de `CLAUDE.md`). Los borradores completos derivados de este esqueleto viven en los archivos hermanos de esta misma carpeta (ver `README.md` de `docs/business/legal/`).
- El compromiso comercial de propiedad y portabilidad de datos en sí → `DATA_OWNERSHIP.md` (este documento le aporta el esqueleto contractual, no lo sustituye).
- Base legal, categorías de datos y subencargados en detalle → `RGPD-TRATAMIENTO-DATOS.md`.
- Precios, renovaciones y condiciones económicas → `PRICING.md` (aquí solo se señala dónde el contrato las referencia).
- Análisis competitivo de las plataformas citadas → `COMPETITOR_ANALYSIS.md` (aquí se usan solo como benchmark de práctica legal).

---

## 1. Inventario del paquete legal objetivo

Convergencia observada en el sector CAE (CTAIMA, Dokify, Nalanda, 6conecta) más la arquitectura documental de un SaaS de comunicaciones maduro (Zendesk). La columna "Fase" indica cuándo debe existir, no cuándo se redacta — la consulta legal puede anticipar piezas.

| # | Documento | Fase | Benchmark principal | Estado sector CAE |
|---|---|---|---|---|
| 1 | Aviso Legal | Lanzamiento (MVP1) | CTAIMA, Dokify | Universal |
| 2 | Política de Privacidad (web + plataforma) | Lanzamiento | Dokify (doble ámbito), CTAIMA (por canal) | Universal |
| 3 | Política de Cookies del sitio web | Lanzamiento | Todas | Universal |
| 4 | Política de cookies en producto (separada) | Lanzamiento | Zendesk, 6conecta (área privada) | Solo 6conecta parcialmente |
| 5 | Términos y Condiciones de Uso (contrato SaaS) | Lanzamiento | CTAIMA, Dokify, Zendesk (MSA) | Universal salvo 6conecta |
| 6 | Acuerdo de Encargo de Tratamiento (DPA) **público y descargable** | Lanzamiento | CTAIMA (público), Dokify (anexo) | Solo CTAIMA lo publica |
| 7 | Anexo público de medidas de seguridad (TOMs) | Lanzamiento | Zendesk ("How We Protect Your Service Data") | Nadie en CAE lo tiene |
| 8 | Lista pública de subencargados, versionada | Lanzamiento | Zendesk | Nadie en CAE lo tiene |
| 9 | Política de supresión y retención de datos | Lanzamiento | Zendesk, Dokify (bloqueo por prescripción) | Implícita en DPAs |
| 10 | Condiciones de prueba gratuita / piloto | Fase comercial | Zendesk | Ausente en CAE |
| 11 | Política de contenido y conducta del usuario | **MVP2 (mensajería)** | Zendesk | Ausente en CAE — la mensajería la exige |
| 12 | Anexo de servicios vinculados de terceros (M365) | **MVP2 (mensajería)** | Zendesk (Non-Zendesk Services) | Ausente en CAE |
| 13 | Régimen de "servicios en evolución" + lista publicada | Post-MVP2 | Zendesk (Innovation Services) | Ausente en CAE |
| 14 | Anexo de servicios de IA | Post-MVP2 (agentes) | Zendesk (AI Addendum), CTAIMA (cláusula RIA) | Solo CTAIMA, genérica |
| 15 | Canal de denuncias (Ley 2/2023) | Según obligación legal aplicable | CTAIMA | Solo CTAIMA |
| 16 | Política de solicitudes gubernamentales de datos | Madurez | Zendesk | Ausente en CAE — activo de confianza futuro |

> Nota de alcance: el nº 15 depende del tamaño de la empresa y de si aplica a Hydra como obligada o solo como proveedora de sus clientes obligados — pregunta para la consulta legal, no se asume.

---

## 2. Esqueleto por documento

### 2.1 Aviso Legal

Estructura estándar del sector, sin sorpresas:

- Identificación completa del titular: razón social, NIF, domicilio, registro mercantil, email de contacto. *(Pendiente de la constitución como autónomo/sociedad — bloqueado por la formalización legal en curso.)*
- Propiedad intelectual e industrial: titularidad del software, marca, prohibición de reproducción. *(Recordatorio: "Hydra" es codename interno; el nombre comercial pendiente condiciona la cláusula de marca.)*
- Condiciones de acceso al sitio web.
- Ley aplicable y fuero. *(España para el lanzamiento; pieza a desacoplar por jurisdicción, ver § 4.)*

**Evitar** (debilidades observadas): la aceptación "por navegación" del aviso clásico de CTAIMA — aceptación expresa en registro y contratación.

### 2.2 Política de Privacidad

**Patrón estructural adoptado — doble ámbito (Dokify):** una sola política que distingue explícitamente (a) tratamiento como *responsable* en el sitio web y captación comercial, y (b) tratamiento como *encargado* en la plataforma por cuenta de los tenants, remitiendo al DPA.

Esqueleto del ámbito web (patrón CTAIMA por canal, formato de capas AEPD):

- Responsable del tratamiento y contacto de privacidad (buzón dedicado tipo `privacidad@` desde el día uno — patrón Nalanda; la designación formal de DPO es pregunta abierta, ver § 5).
- Por cada canal de captación — formulario de contacto, solicitud de demo/presupuesto, newsletter, alta de cuenta: finalidad, categorías de datos, legitimación, plazo de conservación.
- Destinatarios y transferencias: **compromiso EU-only** — sin transferencias fuera del EEE ni cláusulas contractuales tipo, porque no hacen falta. Diferencial directo frente a CTAIMA (filiales extra-EEE con CCT) y Zendesk (matriz en EE. UU.).
- Derechos RGPD y vía de ejercicio; referencia a la autoridad de control.
- Minimización como principio declarado (patrón 6conecta: solo los datos imprescindibles, conservados solo el tiempo necesario).

Esqueleto del ámbito plataforma:

- Hydra actúa como encargado del tratamiento del tenant; los interesados (p. ej. trabajadores de contratistas) ejercen derechos ante el responsable; Hydra intermedia y traslada la solicitud al responsable de forma inmediata (patrón CTAIMA: comunicación al responsable no más tarde del día laborable siguiente).
- Remisión al DPA público (§ 2.5).

### 2.3 Políticas de cookies — dos documentos, no uno

Decisión de arquitectura legal adoptada del patrón Zendesk (y parcialmente 6conecta):

- **Cookies del sitio web comercial**: banner de consentimiento, categorías, tabla de cookies con titular/finalidad/duración. **Posicionamiento deliberado**: analítica mínima, sin píxeles publicitarios ni cross-device — lo contrario del perfil de CTAIMA (Facebook/TikTok/Bing/Clarity) y Zendesk (perfilado publicitario). Coherencia con el argumento central de confianza; coste de oportunidad de marketing asumido y revisable en `GO_TO_MARKET.md`.
- **Cookies en producto**: dentro de la aplicación, solo cookies técnicas de sesión y funcionales propias (patrón 6conecta en su área privada). Documento corto, propio, enlazado desde la app.

### 2.4 Términos y Condiciones de Uso (contrato SaaS)

Esqueleto de cláusulas, con la fuente de cada patrón:

1. **Definiciones** — usar literalmente el vocabulario de `UBIQUITOUS_LANGUAGE.md` (Tenant, Cliente, Cliente Directo, Cliente Delegante, Consultora, Delegated Workspace). Lección negativa de CTAIMA: sus T&C mezclan LICENCIANTE/USUARIA/PROVEEDOR/CLIENTE por acumulación de versiones. Incorporar el concepto **"Datos de Servicio"** (patrón Zendesk *Service Data*): el contenido que el tenant introduce (documentos, mensajes, datos de trabajadores) es del tenant; se distingue de los datos de cuenta y de uso. *(Término candidato a alta en `UBIQUITOUS_LANGUAGE.md` como Draft antes de usarse en un segundo documento.)*
2. **Objeto**: licencia de uso no exclusiva, revocable, no sublicenciable del SaaS; sin cesión de propiedad intelectual (estándar CTAIMA/Dokify).
3. **Ámbito subjetivo**: servicio **exclusivamente B2B para usuarios profesionales** (patrón Nalanda) — excluye consumidores y simplifica el régimen aplicable.
4. **Figuras de acceso**: usuario del tenant; **Tercero Autorizado** (contratista/subcontratista que accede solo para aportar documentación — patrón CTAIMA); y **Operador Delegado** de una Consultora sobre un Delegated Workspace. La tercera figura no existe en ningún benchmark: su encaje contractual exacto depende de la calificación legal de la Consultora (pregunta abierta, § 5) — **se deja el hueco, no se redacta**.
5. **Responsabilidad del cumplimiento CAE/RGPD del contenido** — la cláusula de neutralidad, núcleo de la Opción B, con triple respaldo de mercado: el cumplimiento derivado de la coordinación empresarial corresponde al cliente, contratistas y terceros autorizados, no a la plataforma salvo como encargado (patrón CTAIMA); el cliente garantiza la legitimidad de los datos de terceros que introduce (patrón Nalanda); el cliente informa a sus trabajadores del tratamiento (patrón CTAIMA). Sin verificación por Hydra de la validez de autorizaciones ni fricción de UI por operador — confirmado como práctica uniforme del mercado.
6. **Obligaciones del cliente**: uso lícito, custodia de credenciales, notificación inmediata de compromisos de acceso, prohibición de ingeniería inversa y de sobrecarga del servicio (estándar sector).
7. **Disponibilidad y mantenimiento**: sin garantía de continuidad absoluta; ventanas de mantenimiento justificadas, breves y preferentemente nocturnas (patrón CTAIMA). Los SLA concretos por plan → `PROFESSIONAL_SERVICES.md`.
8. **Cambios en el servicio** (patrón Dokify, superior al de CTAIMA): cambios impuestos por normativa no constituyen incumplimiento; cambios sustanciales no normativos se notifican con un mes para que el cliente pueda rescindir; el silencio es aceptación. Especialmente valioso en un sector donde RD 171/2004 o criterios de la ITSS pueden forzar cambios de producto.
9. **Duración, renovación y precios**: renovación tácita con preaviso (mercado: 2 meses, CTAIMA) y actualización anual — **con cláusula de prevalencia de condiciones particulares**, imprescindible para que conviva con compromisos tipo el rate lock de 24 meses ofrecido a Geseme. Las cifras y mecánica viven en `PRICING.md` y en la orden de pedido; el contrato solo las referencia.
10. **Terminación y datos**: derecho del cliente a copia completa de sus Datos de Servicio en formato estándar documentado **sin coste** (patrón CTAIMA, a mejorar con export documentado/API — argumento de `DATA_OWNERSHIP.md`); después, régimen de la política de supresión (§ 2.8).
11. **Limitación de responsabilidad**: tope en una anualidad pagada (estándar CTAIMA/mercado SaaS); exclusión de daños indirectos y lucro cesante; exclusión de fallos de servicios de terceros vinculados por el cliente (ver § 2.10).
12. **Confidencialidad** recíproca, supervivencia post-contrato (estándar).
13. **Referencias comerciales**: autorización de uso de marca del cliente como referencia, revocable (patrón CTAIMA) — formaliza como cláusula estándar lo negociado ad hoc con el cliente fundador (case study rights).
14. **Notificaciones, ley aplicable y fuero** — pieza jurisdiccional desacoplable (§ 4).

### 2.5 DPA público (Acuerdo de Encargo de Tratamiento, art. 28 RGPD)

**Decisión de formato adoptada**: público y descargable (solo CTAIMA lo hace en el sector) — elimina una negociación por cliente y es señal de madurez. Esqueleto sobre la base art. 28, siguiendo el DPA de CTAIMA como referencia estructural:

- Partes y roles: tenant = responsable; Hydra = encargado. *(La posición de la Consultora en el triángulo queda expresamente fuera de este esqueleto hasta la consulta legal — § 5.)*
- Objeto, duración (la del contrato), naturaleza y finalidad del tratamiento.
- **Categorías de datos por módulo** (patrón CTAIMA de desglose por servicio, a desarrollar en `RGPD-TRATAMIENTO-DATOS.md`): módulo documental — identificativos, laborales y de PRL de trabajadores de contratistas (ITA/RNT/RLC, aptos, formación); usuarios de la aplicación — identificativos y de contacto; **módulo de mensajería (MVP2) — contenido de comunicaciones con categorías potencialmente abiertas**, entrada nueva del registro de tratamientos.
- Instrucciones documentadas del responsable; deber de secreto del personal; formación.
- **Subencargados**: autorización general con lista pública versionada (§ 2.7) y notificación previa de cambios con plazo de oposición (mercado: 1 mes, CTAIMA).
- Asistencia al responsable: derechos de los interesados (traslado inmediato, máximo día laborable siguiente — CTAIMA), consultas previas, evaluaciones de impacto.
- **Notificación de violaciones de seguridad al responsable**: objetivo de compromiso en **48 horas desde la verificación** (patrón Zendesk; mejora las 72h de CTAIMA) — *cifra a validar con la consulta legal y con la capacidad operativa real antes de comprometerla*.
- Auditorías: información de cumplimiento y auditoría por el responsable o tercero independiente, con exclusión expresa de competidores y bajo confidencialidad (patrón Zendesk — protege frente a "auditorías" de plataformas rivales).
- Fin del encargo: supresión o devolución a elección del responsable; conservación **bloqueada** durante los plazos de prescripción de responsabilidades (patrón Dokify/CTAIMA).
- Anexo I: medidas técnicas y organizativas (§ 2.6). Anexo II: lista de subencargados (§ 2.7).

### 2.6 Anexo público de medidas de seguridad (TOMs)

Patrón Zendesk: el anexo técnico del DPA publicado como activo de confianza. Nadie en el sector CAE lo tiene — pieza diferencial barata. **Regla de oro: describir solo controles reales de Hydra; no copiar promesas de una empresa de 5.000 empleados** (Zendesk compromete monitorización 24×7 y equipo de seguridad a tiempo completo — Hydra no debe).

Esqueleto honesto para el estado actual de la plataforma:

- Cifrado en tránsito (TLS) y en reposo.
- **Aislamiento por tenant** como control central (referencia a `docs/MULTITENANCY.md`, sin reproducir el detalle técnico — regla de `DOCUMENT_STANDARDS.md` § 6): separación lógica de los Datos de Servicio de cada tenant (el patrón "Logical Separation" que Zendesk exige a sus proveedores, que en Hydra es diseño de primera clase).
- Residencia de datos: **EEE exclusivamente**, proveedores de hosting y almacenamiento nombrados (patrón de transparencia CTAIMA, que nombra Azure/AWS Irlanda).
- Control de acceso: mínimo privilegio, credenciales únicas, revocación al cese.
- Copias de seguridad y su cifrado; procedimiento de recuperación.
- Gestión de vulnerabilidades y actualización de dependencias, en términos realistas.
- Compromiso de evolución: las medidas pueden reforzarse sin previo aviso, nunca degradarse materialmente durante la suscripción (patrón Zendesk, invertido a favor del cliente).

### 2.7 Lista pública de subencargados

Patrón Zendesk, inexistente en CAE. Tabla versionada con fecha: subencargado, servicio prestado, ubicación del tratamiento (todo EEE), y mecanismo de suscripción a avisos de cambio. El contenido concreto sale de las decisiones de infraestructura ya tomadas (hosting EU, S3 compatible, PostgreSQL gestionado) y vive operativamente ligado a `RGPD-TRATAMIENTO-DATOS.md`.

### 2.8 Política de supresión y retención

Patrón Zendesk (política pública con plazos) + Dokify (bloqueo por prescripción). Esqueleto:

- Durante la suscripción: el tenant borra sus propios datos con efecto real (con papelera/plazo de gracia si se decide en producto).
- Al terminar: ventana de exportación (plazo concreto a fijar — mercado razonable: 30-60 días), después supresión de datos activos y de copias de seguridad en un plazo máximo declarado.
- Excepción de bloqueo legal: conservación bloqueada solo de lo exigible mientras prescriben responsabilidades.
- Esta política es la traducción operativa del compromiso de `DATA_OWNERSHIP.md` — se desarrolla allí como compromiso comercial y aquí solo se contractualiza.

### 2.9 Política de contenido y conducta del usuario — *nueva necesidad de MVP2*

Ninguna plataforma CAE la tiene porque ninguna aloja comunicaciones libres; Zendesk la tiene porque las aloja. En cuanto Hydra active mensajería, la necesita. Esqueleto mínimo: usos prohibidos del canal (contenido ilícito, acoso, spam), posición de neutralidad de Hydra sobre el contenido de las comunicaciones entre las partes, mecanismo de suspensión ante abuso manifiesto, y remisión al DPA para el tratamiento del contenido como Datos de Servicio.

### 2.10 Anexo de servicios vinculados de terceros (Microsoft 365) — *MVP2*

Adaptación de la cláusula "Non-Zendesk Services" al modelo OAuth de Hydra. Es la pieza donde la decisión de arquitectura (sin buzones propios) se convierte en ventaja de riesgo contractual:

- El buzón y el tenant Microsoft 365 son del cliente; su configuración, seguridad, retención y disponibilidad son responsabilidad del cliente y de Microsoft.
- Hydra accede mediante autorización OAuth otorgada y **revocable en todo momento** por el cliente; el alcance de permisos solicitado se documenta.
- Qué almacena Hydra y qué no (p. ej. metadatos/estado de ticket vs. cuerpo del mensaje) — **depende de decisiones de producto de MVP2 aún no tomadas; el anexo se esqueletiza y se completa cuando el diseño técnico esté cerrado**.
- Exclusión de responsabilidad por fallos, límites o cambios del servicio de Microsoft.
- Argumento comercial derivado: Hydra custodia estructuralmente menos datos de comunicaciones que un Zendesk que los aloja todos — y puede decirlo.

### 2.11 Régimen de "servicios en evolución" y anexo de IA — *post-MVP2*

Patrón Zendesk (Enterprise vs. Innovation Services + AI Addendum), coherente con la disciplina interna de separar lo confirmado de lo hipotético:

- Dos regímenes: garantías plenas para el producto GA (documental, mensajería una vez estable) y condiciones propias para una **lista publicada y versionada de servicios en evolución** (conectores, agentes) — así lo experimental no diluye las garantías del core.
- Anexo de IA cuando lleguen los agentes: cumplimiento del Reglamento de IA de la UE (la cláusula de CTAIMA de dic-2025 marca el estándar sectorial mínimo, redactada de forma genérica — superable), transparencia sobre qué funciones usan IA, no uso de Datos de Servicio para entrenar modelos de propósito general salvo consentimiento expreso del tenant (compromiso diferencial esperable como estándar del mercado europeo).
- Condiciones de prueba gratuita/beta separadas y cortas (patrón Zendesk) para pilotos comerciales.

---

## 3. Principios transversales adoptados (hipótesis de diseño legal)

1. **Neutralidad por garantía contractual, no por fricción de producto.** Confirmado en cuatro fuentes (CTAIMA, Nalanda, Dokify implícito, y el modelo Service Data de Zendesk): la legitimidad del tratamiento la garantiza el cliente por contrato; la plataforma no verifica autorizaciones ni impone checkboxes por operador. Cierra en lo contractual lo ya fijado como regla en el modelo de delegación (Hydra nunca inicia una delegación).
2. **EU-only como diferencial estructural**, no como eslogan: sin transferencias internacionales que justificar, frente a CTAIMA (filiales extra-EEE con CCT) y Zendesk (matriz EE. UU.).
3. **Austeridad de rastreo** coherente con el posicionamiento: web con analítica mínima, producto sin cookies de terceros.
4. **Transparencia como producto**: DPA, TOMs, subencargados y política de supresión públicos — el "paquete Zendesk" aplicado a un sector donde solo CTAIMA publica una pieza.
5. **Terminología única**: los documentos legales usan el vocabulario de `UBIQUITOUS_LANGUAGE.md` sin traducción interna; todo término nuevo (candidato: "Datos de Servicio") se da de alta allí antes de usarse en un segundo documento.
6. **Núcleo UE + anexos por jurisdicción** (§ 4): nada específicamente español dentro del núcleo.

## 4. Desacoplamiento jurisdiccional (diseño para expansión)

Piezas identificadas como específicas de España, a mantener en anexo/documento separado del núcleo RGPD (que viaja tal cual a Portugal, Italia u otros mercados EEE):

- LOPDGDD como norma de desarrollo nacional.
- Canal de denuncias (Ley 2/2023) — y la propia cuestión de si obliga a Hydra (§ 5).
- Referencias sectoriales: RD 171/2004, criterios ITSS, terminología ITA/RNT/RLC.
- Fuero y ley aplicable.
- Prevalencia de idioma: el español como versión auténtica en el lanzamiento; la política de idioma prevalente al internacionalizar es decisión futura de `GO_TO_MARKET.md` (Zendesk impone el inglés; Hydra decidirá con criterio propio).

Para LatAm el núcleo RGPD no viaja: se sustituye por el marco local — fuera de alcance hasta que `GO_TO_MARKET.md` lo priorice.

## 5. Cuestiones abiertas que bloquean redacción — para la consulta legal

Ninguna de estas se resuelve ni se codifica antes de la consulta. Se añaden a las seis áreas del briefing legal ya preparado:

| # | Cuestión | Cláusulas bloqueadas |
|---|---|---|
| 1 | Calificación de la Consultora (encargado vs. responsable/corresponsable, criterio funcional AEPD) — ya en el briefing | T&C § figuras de acceso (Operador Delegado); posición de la Consultora en el DPA |
| 2 | **Nueva — mensajería**: figura bajo la que un Operador Delegado envía comunicaciones desde el tenant M365 de un Cliente Delegante; implicaciones sobre secreto de las comunicaciones y registro de tratamientos | Anexo M365; política de contenido; entrada de mensajería del DPA |
| 3 | ¿Obligación de designar DPO? (volumen y categorías — los datos de PRL incluyen aptitud médica) | Política de privacidad § contacto; DPA |
| 4 | ¿Aplica a Hydra el canal de denuncias de la Ley 2/2023 como obligada propia? | Pieza nº 15 del inventario |
| 5 | Validación de las cifras de compromiso: notificación de brechas en 48h, ventana de exportación, plazos de supresión | DPA; política de supresión; TOMs |
| 6 | **Nueva — benchmark**: revisión del paquete estándar del sector con el DPA público de CTAIMA, los T&C de Dokify y el MSA/DPA de Zendesk como material de referencia, para partir de práctica de mercado y abaratar la redacción | Todo el paquete |

## 6. Qué necesita saber arquitectura (implicaciones de producto, no de código aún)

Requisitos que este marco legal impondrá al producto cuando se apruebe — identificados para su futuro ADR o plan, conforme a la regla de que negocio no decide arquitectura:

- **Exportación completa por tenant** en formato estándar documentado, sin coste (T&C § 10, `DATA_OWNERSHIP.md`) — candidata a requisito de MVP1 tardío o condición de salida a producción.
- **Supresión efectiva con plazos declarados**, incluidas copias de seguridad, y bloqueo selectivo por prescripción (§ 2.8) — afecta a diseño de backups y ciclo de vida del dato.
- **Registro de auditoría** suficiente para las obligaciones de demostración del art. 28 (ya en el briefing legal como área 5).
- **Alcance mínimo de permisos OAuth M365** documentado y revocable, y decisión explícita de qué se persiste en Hydra vs. qué permanece solo en el tenant del cliente (§ 2.10) — decisión de diseño central de MVP2.
- **Sin cookies de terceros en producto** (§ 2.3) — restricción para cualquier herramienta de analítica de producto que se considere.

## Documentos relacionados

- `DATA_OWNERSHIP.md` — compromisos comerciales de propiedad, portabilidad y retención que este esqueleto contractualiza.
- `RGPD-TRATAMIENTO-DATOS.md` — registro de tratamientos, categorías por módulo, subencargados.
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción SaaS" — DPA y Términos de Uso como bloqueantes heredados.
- `ADR-004-delegacion-consultoras-cae.md` — modelo de delegación cuyo encaje contractual queda pendiente de la cuestión abierta nº 1.
- `UBIQUITOUS_LANGUAGE.md` — vocabulario obligatorio; alta pendiente del término candidato "Datos de Servicio".
- `PRICING.md` — condiciones económicas que el contrato referencia sin fijar.
- `PROFESSIONAL_SERVICES.md` — SLAs de soporte por plan.
- `GO_TO_MARKET.md` — trade-off de la austeridad de rastreo; política de idioma e internacionalización.
- `COMPETITOR_ANALYSIS.md` — las plataformas aquí citadas como benchmark legal se analizan allí como competencia.
- `docs/business/legal/README.md` — índice de los 16 borradores derivados de este esqueleto, con su estado de redacción y las preguntas de § 5 que bloquean cada uno.
