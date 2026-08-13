# MACRO PLAN 2026-08-13 — Hydra (CAE Manager)

**Tipo**: Plan — insumo para `ROADMAP_BUSINESS.md`, `PRODUCT_STRATEGY.md` y `ROADMAP.md`. Deriva directamente de `MATURITY_REVIEW_2026-08-13.md` (mismo HEAD `c0e2284`). No es normativo: cada decisión que se adopte de aquí se registra donde corresponda.
**Alcance**: qué hacer, qué optimizar, qué automatizar con IA y qué construir para el usuario experto — con secuencia, dependencias, esfuerzo estimado y criterio de corte. Sin código.
**Convención de esfuerzo**: S = 1 sesión · M = 2-4 sesiones · L = 1-2 semanas de sesiones · XL = >2 semanas. Una "sesión" es una jornada de trabajo con agente al ritmo demostrado en Fases 73-93.

---

## 0. Principios rectores (leer antes de ejecutar nada)

1. **La secuencia es el plan.** El error disponible más caro hoy no es técnico: es ejecutar el Horizonte 2 (optimización) o el 3 (expansión) antes que el 0 (supervivencia) y el 1 (validación). Cada horizonte desbloquea al siguiente; saltárselo convierte trabajo bueno en inventario.
2. **Regla de la señal externa.** A partir del Horizonte 1, ninguna feature nueva de producto entra al roadmap sin una de estas tres señales: un cliente de pago la pide, un piloto formal la necesita para cerrar, o bloquea una demo de venta concreta. La maquinaria de construcción demostrada (20 fases en 12 días) se redirige, no se frena: deuda, operación y automatización interna no requieren señal externa.
3. **Lo escrito no puntúa; lo operado sí.** Sentry integrado sin DSN, RLS migrado sin activar, multi-réplica construida sin encender, ensayo de restauración con plantilla y tabla vacía: el patrón dominante de la deuda restante es "instrumento listo, interruptor sin pulsar". El Horizonte 0 es casi todo interruptores.
4. **La IA se automatiza donde ya hay rastro.** Toda automatización propuesta en § 6 se apoya en datos que el sistema ya captura (timeline de conversaciones, estados de documento, reclamaciones con rastro, telemetría). No se propone ninguna que exija instrumentación nueva previa — primero rastro, luego automatización, nunca al revés.
5. **Anti-plan explícito** (§ 9): lo que NO se hace es parte del plan.

---

## Horizonte 0 — Supervivencia operativa (semanas 1-2) — TODO bloqueante

> Ningún ítem de este horizonte es de código. Todos tienen la instrumentación lista en el repo. El criterio de salida del horizonte es binario: los cinco hechos, o no se pasa al 1.

### 0.1 Mudanza de hosting (reloj externo — lo primero de todo) — M
- Terminar y mergear PR #174 (ya en verde, solo falta salir de draft tras revisión final).
- Ejecutar `RUNBOOK-DESPLIEGUE-LOCAL.md` completo: dominio propio, Cloudflare Tunnel, stack compose (app + postgres:18 + cloudflared), reapunte de las integraciones (Entra ID, webhooks Graph, WhatsApp, `AlertasPorCorreo__UrlBase`).
- Alta del Hetzner Storage Box y primer backup Borg real (dump + `dataprotection-keys/` + `/data/documentos` en el mismo backup — invariante de `RUNBOOK-CLAVES.md`).
- Verificación end-to-end en navegador contra el dominio nuevo (checklist del runbook).
- **Escribir la fecha de salida del puente**: una línea en `DEPLOY.md` con el criterio que dispara el salto a VPS Hetzner (propuesto: primer cliente de pago firmado, o 90 días, lo que llegue antes). Sin fecha escrita, el puente se vuelve permanente.

### 0.2 Primer ensayo de restauración real — S
- Sobre el stack nuevo (`ensayo-restauracion-borg.sh`), no sobre el S3 saliente.
- Registrar en la tabla de `docs/ENSAYO-RESTAURACION.md`: fecha, backup usado, resultado del dump, resultado de claves (credencial legible tras restaurar — la prueba de fuego de Data Protection), duración total.
- **Ratificar RPO/RTO** (los propuestos siguen "pendientes de ratificar" desde el 01-08). Decisión de propietario, 10 minutos, escribirla.
- Añadir al runbook la cadencia de re-ensayo (propuesto: trimestral, o tras cualquier cambio del mecanismo de backup).

### 0.3 Encender el monitoreo ya escrito — S
- Cuenta Sentry → `Sentry:Dsn`. Cuenta/instancia Seq → `Serilog:Seq:ServerUrl`. Ambos enchufes existen en `Program.cs` y quedan operativos con dos variables.
- Uptime check externo (UptimeRobot o equivalente) contra `/salud` — que ya es un health check real contra PostgreSQL — con alerta al móvil.
- Verificar el circuito completo provocando un error controlado: que llegue a Sentry, que el log correlacionado con TenantId llegue a Seq, que la caída simulada dispare la alerta. Sin esta verificación, el monitoreo sigue siendo hipótesis.

### 0.4 Paquete legal ante abogado — M (coste externo, no sesiones)
- Encargar la revisión de los 16 borradores de `docs/business/legal/` a un abogado con RGPD sanitario (el DPA ya declara soporte y M365; verificar que declara también Anthropic/Mistral/Gemini como subencargados de IA con el kill-switch como mitigación).
- Presupuestarlo como coste de venta, no como "algún día": sin DPA firmable no hay primer tenant real posible, y con datos de salud el riesgo es sancionador.
- Preparar el "kit de firma": qué documentos firma un tenant nuevo, en qué orden, y dónde se archiva la firma (hoy: aceptación de términos en producto ya implementada — falta el circuito del DPA en papel/firma electrónica).

### 0.5 Decisión escrita sobre el repositorio público — S
- Completar el paso 2 de la limpieza pendiente (refs/pull con identificadores) **o** documentar formalmente la aceptación del riesgo con fecha de revisión.
- Evaluar la alternativa intermedia que no existía cuando se decidió: con la mudanza fuera de Railway, medir el consumo real de CI y re-presupuestar GitHub privado o runners self-hosted en la propia máquina del despliegue (que estará encendida 24/7 de todos modos).

---

## Horizonte 1 — Validación y preparación comercial (semanas 3-6)

> Objetivo único del horizonte: **un usuario real de pago (o piloto formal con carta de intención) usando el producto**, con la seguridad y la evidencia mínimas para sostener esa relación.

### 1.1 Piloto real — L (mayormente trabajo comercial)
- Elegir el segmento de entrada con lo ya construido: la consultora de PRL (el alta de delegaciones funciona, el Delegated Workspace funciona, el escenario GESEME ya tiene oferta comprometida).
- Definir el paquete de piloto: alcance (qué módulos), duración (60-90 días), precio simbólico pero no cero (validar disposición a pagar es el dato), criterios de éxito medibles (p. ej. "el gestor tramita el 100% de su cartera desde Hydra durante 30 días").
- **Instrumentar el feedback**: sesión semanal grabada, registro de fricciones en un documento vivo, y la regla de la señal externa (§ 0, principio 2) activada desde el día 1 — el backlog del piloto manda sobre cualquier idea interna.
- La entidad `Telemetria` existente: definir los 5-10 eventos de uso que responden "¿qué módulos usa de verdad el piloto?" antes de que empiece, no después.

### 1.2 Congelación selectiva de features — S (decisión, no trabajo)
- Congelar: gamificación (no crecer más), Dashboard BPO fase 2+, cualquier módulo nuevo.
- No congelar: fixes del piloto, deuda del Horizonte 0-1, automatización IA sobre rastro existente (§ 6) — esa es la diferenciación que el piloto puede validar.

### 1.3 Pentest externo — M (coste externo)
- Alcance mínimo: autenticación, aislamiento multi-tenant (que ataquen la frontera con las FKs compuestas y el filtro por reflexión — es el mejor momento para gastarse el dinero: la defensa está completa), API pública, subida de archivos.
- Momento: después de 0.1 (que ataquen el despliegue real, no el que se va a apagar).
- El informe resultante alimenta el kit de venta (los cuestionarios de seguridad piden "fecha del último pentest").

### 1.4 Activar RLS en runtime — S
- Ejecutar la rotación de credencial de `RUNBOOK-RLS.md` (rol restringido en `CaeManagerDbRuntime`).
- Verificar con la prueba adversarial existente que la segunda línea de defensa está viva (una query sin `set_config` debe devolver cero filas, no filas de otro tenant).
- A partir de aquí, "defensa en profundidad" deja de ser diseño y pasa a ser afirmación verificable en el kit de venta.

### 1.5 Cerrar las verificaciones pendientes por regla propia — M
- Verificación end-to-end en navegador de bulk actions / atajos / filtros guardados (P3-31, aún 🟡).
- Validación en blur: extender de Centros al resto de formularios con `CampoTexto`/`CampoTextarea` (mecanismo ya construido, es adopción).
- Value objects `Dni`/`Cif`: **decidir** — adoptarlos en Trabajador/Cliente/Empresa (M) o borrarlos (S). Cualquiera de las dos es mejor que el estado actual (código muerto que miente sobre el modelo).

### 1.6 E2E de los flujos de demo — M
- Subir de 12 E2E a cubrir los flujos que se enseñarán en venta: alta guiada completa (cascada Cliente→Empresa→Centro), ciclo documental entero (subida → IA → validación → vencimiento → reclamación → renovación), delegación (alta + operación en Delegated Workspace + revocación), bandeja priorizada de la mañana.
- Criterio: si se rompe en la demo, tiene que romperse antes en CI.

### 1.7 Billing mínimo viable — M
- No construir un motor de facturación: alta manual de suscripción + Stripe (o GoCardless para SEPA, más natural en España) en modo "link de pago + registro manual del estado en el tenant".
- Lo único de producto: un campo de estado de suscripción por tenant y el gate correspondiente (aviso → solo lectura → suspendido), sobre el patrón de flags por tenant que ya existe para otras cosas.
- El billing automatizado completo (metering, prorrateos, autoservicio) es Horizonte 3 — con un piloto y pocos tenants, lo manual es correcto y YAGNI manda.

---

## Horizonte 2 — Optimización técnica y operativa (meses 2-3)

> Se ejecuta en paralelo al piloto, con la mitad del ancho de banda como máximo. Ordenado por (riesgo que elimina × esfuerzo).

### 2.1 Examen escrito de capacidad de Blazor Server — S de examen + decisión
- El ADR pendiente más importante: cuántos circuitos concurrentes soporta el despliegue actual, con qué RAM por circuito, qué latencia añade el semáforo por circuito bajo carga, y qué umbral dispara el encendido de la multi-réplica ya construida.
- Método: k6 ya está en CI para HTTP; añadir un escenario de carga sobre circuitos (usuarios sintéticos manteniendo sesión interactiva) contra el entorno local — no hace falta tooling nuevo, hace falta el número.
- Resultado: o "el techo es X usuarios y sobra para 2 años de ventas" (documentado, se cierra el tema) o "el techo está cerca" (se planifica 2.2 y el encendido de multi-réplica). Hoy la decisión de render más importante del frontend sigue sin número.
- Incluir `CircuitOptions` explícitas (retención de circuitos desconectados, buffers) — hoy se opera con los defaults sin haberlos examinado.

### 2.2 Rediseño de lifetimes → retirar `PuertaAccesoDatos` — L
- La investigación cerrada de P1-11 es el punto de partida: el bloqueo era que ~31 repositorios y ~8 servicios Scoped capturan el DbContext por constructor.
- Plan por etapas que la investigación ya insinúa: (1) inventario de capturas por constructor; (2) migrar los repositorios a resolución por operación (factory), feature a feature, con las interfaces ya segregadas de PR #60 como frontera natural de cada lote; (3) retirar el semáforo cuando el último consumidor Scoped desaparezca.
- Beneficio: elimina el cuello de botella por circuito (la causa raíz de "Escalabilidad 6" en Arquitectura), ~150 líneas de complejidad accidental fuera, y desbloquea paralelismo real dentro de un circuito.
- Riesgo controlado: los 375 tests de integración + el patrón de verificación adversarial existente son la red. No empezar hasta tener 2.1 (el número dice cuánta prisa hay).

### 2.3 OpenTelemetry sobre el logging correlacionado — M
- Trazas (MediatR + HTTP + EF) y métricas (latencia por comando, profundidad de cola de IA, circuitos activos, documentos procesados/hora) exportadas a un backend (el propio Seq soporta trazas; alternativa autohospedable en el mismo stack).
- El `LoggingBehavior` ya captura comando/duración/tenant — esto lo convierte de "logs que se leen" a "curvas que se vigilan", y alimenta las alertas de 2.4.
- Dashboards mínimos: salud de la cola de IA, latencia p95 por comando, errores por tenant, uso por módulo (este último alimenta directamente la validación de producto de 1.1).

### 2.4 Alertas operativas — S
- Sobre 0.3 + 2.3: cola de IA estancada, tasa de error por encima de umbral, latencia degradada, backup diario ausente (el cron de Borg debe reportar éxito a un dead-man's-switch, no solo fallar en silencio).
- Runbook de guardia de una persona: qué alerta significa qué, qué se mira primero (honesto con el bus factor 1: el objetivo es que el único operador se entere en minutos, no montar un on-call de mentira).

### 2.5 Tests de arquitectura de fronteras reales — M
- Los 8 actuales atrapan la regresión literal del god-interface. Extender a: (1) ningún handler de una feature referencia interfaces de persistencia de otra feature sin una lista blanca justificada; (2) ningún servicio nuevo usa `IgnoreQueryFilters()`/SQL crudo fuera de los usos revisados (la regla de `CLAUDE.md`, hoy solo convención, pasa a gate); (3) todo Command de edición declara `Version` (la regla de concurrencia, hoy revisión manual).
- Es la conversión de las tres reglas escritas más importantes del repo en gates que no dependen de que el revisor se acuerde.

### 2.6 Deep-links y rutas de detalle — M
- Rutas reales para las entidades troncales (Trabajador, Centro, Documento, Conversación) que abran la vista con su panel/drawer restaurado. Cierra a la vez: el hueco declarado del Context Workspace (deep-link, cierre al navegar), la fricción de soporte ("pásame el enlace"), y es prerequisito de las notificaciones accionables de § 7 (una notificación sin enlace profundo es media notificación).

### 2.7 Optimizaciones de coste y rendimiento menores detectadas — S-M
- Export de listas: sustituir `TamanoPagina: int.MaxValue` + `MemoryStream` por streaming paginado (vector de presión de memoria señalado en la auditoría del 01-08, sin evidencia de haberse tocado).
- Búsqueda global y buscadores por lista: verificar que las 32 búsquedas `Contains` usan de verdad los índices trigram creados en P1-14 (verificación con `EXPLAIN`, no asunción).
- Revisión de N+1 en las vistas nuevas de la ventana 73-93 (Centro 360, bandeja priorizada, Dashboard BPO agregan datos de varias fuentes; son las candidatas naturales a queries por fila).
- Presupuesto de tokens IA por tenant: la telemetría de coste existe como entidad; añadir el límite blando por tenant (aviso al operador) antes de que un piloto con mil documentos lo descubra por la factura de Anthropic.

---

## Horizonte 3 — Expansión (meses 3-6, condicionado a señal del piloto)

### 3.1 API pública completa — L
- Escrituras sobre los Commands existentes (el transporte es lo único que falta): alta/edición de Trabajadores, Documentos (subida incluida), Asignaciones — exactamente los recursos que un ERP de cliente quiere empujar.
- Idempotencia real: clave de idempotencia por petición de escritura (almacenada con TTL), respuesta repetible — el estándar que los integradores esperan.
- **Webhooks salientes** (la mitad que falta de "integración"): suscripciones por tenant a eventos de dominio que ya existen como eventos (documento validado/rechazado/vencido, reclamación respondida, visita creada), con reintentos exponenciales, firma HMAC y panel de entregas fallidas. El diseño por capacidades de `ARQUITECTURA-INTEGRACIONES.md` ya prevé el encaje.
- Filtrado y ordenación por campo en los GET (hoy: solo `busqueda` + un flag); `filter`/`sort` homogéneos documentados en OpenAPI.
- Autoservicio de claves API por tenant (hoy solo Administrador de plataforma) con scopes lectura/escritura — se convierte en argumento de venta.

### 3.2 Primer conector bidireccional de plataforma CAE — XL (solo con cliente que lo exija)
- `docs/INTEGRATION_GUIDELINES.md` existe para este momento. No elegir proveedor (Dokify/6Coordina/CTAIMA) por especulación: el primer conector lo decide el primer cliente que lo necesite para decir sí.

### 3.3 Portal de terceros (subcontratista sube su propia documentación) — XL
- El mayor hueco de cobertura CAE restante y probablemente la feature con más apalancamiento comercial del backlog: convierte a cada cliente en prescriptor (sus subcontratas entran a Hydra). Enorme superficie nueva de seguridad (usuarios externos de bajo contexto) — exige su propio ADR y el checklist de seguridad como gate, con el historial de "módulo nuevo reintroduce fallo conocido" en mente.
- Solo con señal del piloto; si el piloto BPO gestiona él mismo la documentación, puede no ser lo siguiente.

### 3.4 SSO federado por tenant, feature flags por tenant, billing automatizado — L cada uno
- En este orden y solo cuando un cliente concreto los pida (SSO es la petición enterprise más frecuente; los flags por tenant desbloquean pilotos de features sin ramas; el billing automatizado espera a tener >5 tenants de pago).

---

## § 6. Automatización con IA — el plan específico

> Estado de partida honesto: Hydra ya tiene más IA aplicada que la mayoría de SaaS del sector (extracción de datos de documentos con router multi-proveedor, detección de trabajadores en documentos, prellenado de subida masiva, detección de solicitud de visita en correos con sugerencia nunca automática, asistente). El principio que ya gobierna — **la IA propone, el humano dispone, todo con confianza visible** — es correcto y no se cambia: se extiende. Y el límite ya decidido se respeta: kill-switch sobre datos médicos apagado por defecto hasta DPA de subencargado firmado (Horizonte 0.4 lo desbloquea).

### 6.1 El ciclo documental, de asistido a casi-autónomo (la automatización de mayor valor)
- **Validación automática con umbral de confianza**: hoy la IA extrae y el gestor valida todo. Propuesta: documentos donde la extracción supera un umbral de confianza Y el tipo de documento es de bajo riesgo (recibos ITA, TC2...) pasan a "validado automáticamente, revisable", con muestreo aleatorio del X% hacia revisión humana para vigilar la deriva. Los de categoría sensible (médicos) quedan excluidos por el kill-switch. Métrica objetivo: % de documentos que no tocan a un humano, publicada en el Dashboard BPO (que por fin tendría su dato estrella real).
- **Triage del documento entrante por cualquier canal**: un adjunto llega por correo/WhatsApp/portal → la IA propone tipo de documento, propietario (trabajador/empresa/vehículo, casando contra el censo del tenant), fecha de vencimiento y a qué exigencia responde — el gestor confirma con un clic desde la propia conversación (la acción "actualizar documentación desde adjunto" ya existe; esto la precarga entera).
- **Detección de incoherencias, no solo extracción**: fecha de emisión posterior al vencimiento, DNI del PDF que no casa con el trabajador declarado, empresa del documento distinta del propietario — cada una es una regla barata sobre datos ya extraídos, y es el tipo de error que hoy se cuela hasta la visita.

### 6.2 Reclamación documental autónoma (el "agente cobrador" — sobre el rastro de #190)
- El outbound con rastro, seguimiento y `ObtenerReclamacionesSinRespuestaQuery` ya existen. La pieza que falta es la **cadencia autónoma**: política por cliente (p. ej. reclamar a T-30 del vencimiento, recordar a los 7 días sin respuesta, escalar a otro contacto a los 14, marcar para gestión humana a los 21), ejecutada por un job que redacta cada mensaje con la plantilla + contexto específico (qué documento, de quién, para qué centro, qué pasa si no llega) y lo deja en el rastro existente.
- **Lectura de la respuesta**: cuando el reclamado contesta con adjunto, enlaza directo con 6.1 (triage automático) — el ciclo reclamar→recibir→clasificar→validar se cierra sin humano en el caso feliz. Esto es exactamente el pitch BPO: "el 70% de tu persecución documental ocurre sola".
- Guardarraíles: volumen máximo de envíos por tenant/día, horario laboral, opt-out por destinatario, y todo visible en el timeline — nada se envía que el gestor no pueda auditar después.

### 6.3 La bandeja del gestor con priorización aprendida
- La bandeja única priorizada (Fase 88) hoy prioriza por reglas. Capa siguiente: **scoring con contexto** — urgencia real de la visita asociada, historial de respuesta del reclamado (los lentos, antes), criticidad del cliente, y el patrón de la ventana de validación. Empezar con score explicable por reglas ponderadas (no ML): cada elemento de la cola dice *por qué* está arriba.
- **Resumen de conversación al abrirla**: hilo largo de correo/WhatsApp → tres líneas de situación + la acción pendiente detectada. Ya hay proveedor de IA y timeline; es prompt + caché por conversación, y ahorra el minuto de re-lectura mil veces al día.
- **Borrador de respuesta con macro + contexto**: el sistema de macros existe; la IA elige la macro probable y la instancia con los datos del caso (documentos que faltan, fechas), el gestor edita y envía. Medir tasa de aceptación del borrador — si es <50%, apagarlo (regla de honestidad: una sugerencia mala es peor que ninguna).

### 6.4 Onboarding de cliente asistido (el momento de máxima fricción BPO)
- La importación combinada ya existe con plantillas Excel. Capa IA: aceptar el zip/carpeta caótica real de un cliente nuevo (excels heterogéneos, PDFs sueltos) y producir la propuesta de importación completa — censo de empresas/trabajadores/centros deducido, documentos casados por 6.1, huecos marcados como "falta X de Y" que alimentan directamente la primera tanda de reclamaciones de 6.2.
- Es la automatización que más acorta el ciclo de venta ("¿cuánto tardo en migrar?" → "tráete el zip a la demo").

### 6.5 IA operando la plataforma (interno, sin exposición a cliente)
- **Análisis de incidentes**: con Seq + Sentry encendidos (0.3), una pasada de agente sobre los errores de la semana con hipótesis de causa raíz y enlace al código — el formato auditoría-adversarial que este repo ya domina, en cadencia semanal automática.
- **Vigilancia de deriva documental ampliada**: el validador de gobernanza es determinista; una pasada de agente periódica que compare afirmaciones de docs contra el código (el tipo de hallazgo "CLAUDE.md dice X y es falso" que las dos auditorías encontraron a mano) y abra issue con evidencia.
- **Generación de tests dirigida**: los 12 E2E son el punto débil declarado; los flujos de 1.6 se especifican en lenguaje de dominio y el agente los implementa sobre el arnés Playwright existente — es el trabajo más mecanizable del backlog de calidad.
- **Cuestionarios de seguridad**: con el corpus de `docs/` (MULTITENANCY, RGPD, runbooks, pentest de 1.3), un agente responde el primer borrador de cualquier cuestionario de seguridad de cliente — tarea que en venta B2B consume días y aquí el material fuente es inusualmente bueno.

### 6.6 Gobernanza de toda la capa IA (condición para vender lo anterior)
- Registro por operación (ya existe auditoría IA): modelo, versión de prompt, confianza, coste, decisión humana posterior — completar lo que falte para poder responder "¿qué hizo la IA y quién lo confirmó?" por documento.
- **Suite de regresión de prompts**: corpus fijo de documentos de prueba con extracciones esperadas; cada cambio de prompt/modelo corre contra el corpus en CI como cualquier otro test. Sin esto, 6.1 (validación automática) no es responsable.
- Los umbrales de confianza y el % de muestreo son configuración por tenant, no constantes: cada cliente BPO decide su apetito de riesgo, y eso mismo es un argumento de venta ("tú eliges cuánto delega tu equipo").
- El DPA de subencargados de IA (0.4) precede a todo lo de cara al cliente de esta sección. Orden no negociable.

---

## § 7. Usuario pro — el kit que falta

> Ya construido y bien: j/k/x/Enter, ⌘K con comandos de creación, bulk actions en 3 rejillas, filtros guardados, filtros en URL, deshacer al eliminar, ordenación por columna, asignaciones en lote con preflight. Lo siguiente, en orden de valor para el gestor de 8h/día:

1. **Renovación en lote asistida** (la operación reina del CAE): seleccionar N documentos próximos a vencer → un solo flujo que encadena reclamación (6.2) o subida múltiple con prellenado (ya existe) → validación en cadena sin volver a la lista entre documentos. Hoy las piezas existen sueltas; el flujo continuo no.
2. **Edición inline en las rejillas** para los campos de ciclo rápido (fecha de vencimiento, estado, notas) — el drawer completo para edición profunda, la celda para el retoque. Con la concurrencia optimista visible ya resuelta, el riesgo clásico de la edición inline está cubierto.
3. **Deep-links + "copiar enlace" en cada entidad** (2.6): la moneda de colaboración interna ("mira este trabajador") y el prerequisito de notificaciones útiles.
4. **Notificaciones accionables configurables por usuario**: qué eventos, por qué canal (in-app/correo/digest), con enlace profundo y acción rápida donde aplique (validar/reasignar desde el correo del digest). El digest diario de alertas ya existe (Fase 82) — esto lo generaliza y lo hace configurable en vez de uniforme.
5. **Vistas de lista configurables y compartibles**: columnas visibles, densidad compacta, y promover un filtro guardado a "vista del equipo" (el paso de personal a operativo que convierte los filtros guardados en procedimiento de trabajo).
6. **Split view / trabajo en dos paneles**: bandeja a la izquierda, entidad afectada a la derecha — el patrón de superficie de trabajo que la gestión de conversaciones ya insinúa, generalizado a la operativa documental. (Cambio de superficie: pasa por la cadena de decisión de diseño, no se improvisa.)
7. **Teclado de segundo nivel**: atajos dentro del drawer/panel (guardar, siguiente/anterior elemento de la lista sin cerrar el panel, saltar entre pestañas) — es lo que convierte "atajos en la lista" en "jornada entera sin ratón". Auditar contra el flujo real del piloto, no contra una lista teórica.
8. **Export programado**: los exports existen; programarlos (semanal al correo del cliente, el informe mensual BPO de 6.5) los convierte de acción en servicio.
9. **Búsqueda global con operadores**: `tipo:documento estado:vencido cliente:X` sobre el buscador existente — barato encima del ⌘K actual y es el atajo definitivo del usuario que ya sabe lo que busca.
10. **Panel "mi jornada"**: no otro dashboard — la lista ordenada de 6.3 con contadores de compromiso (qué vence hoy en mi cartera, qué respondió anoche, qué desbloqueé ayer). El Dashboard BPO mide el negocio; esto opera la mañana. Solo si el piloto lo valida.

---

## § 8. Mejoras transversales restantes detectadas

- **Onboarding de desarrollador humano**: guía "primer día" de 1 página (levantar, sembrar, usuarios de prueba y sus roles, dónde está cada cosa) — el repo está optimizado para agentes con contexto; un humano nuevo (o un futuro colaborador, la mitigación del bus factor) necesita la versión corta.
- **Entorno de staging real**: con la mudanza (0.1), un segundo compose con dominio de staging en la misma máquina — hoy el gap entre "verde en CI" y "producción" se cruza sin escala intermedia. Barato una vez existe el stack local.
- **Ensayo de encendido de multi-réplica**: una vez, en staging, encender el modo multi-réplica construido (backplane, advisory lock, llavero S3) y verificar que funciona — para que el día que haga falta sea un interruptor probado y no un estreno.
- **Checklist de seguridad como gate duro**: el checklist de Fase 70 existe pero #190 demostró que los módulos nuevos aún reintroducen clases de fallo conocidas; convertir los ítems mecanizables en tests de arquitectura (2.5) y dejar en checklist solo lo no mecanizable.
- **Métrica de cobertura con umbral**: la cobertura se mide en CI pero no tiene suelo; fijar el umbral al valor actual (ratchet: nunca bajar) cuesta una línea y evita la erosión silenciosa.
- **Sucesión y continuidad**: documento operativo sellado (dónde están las claves, cómo se accede a qué, qué pagar cada mes) para el escenario "el único operador no está disponible 2 semanas". Incómodo de escribir, imprescindible con bus factor 1, y un cliente enterprise lo preguntará como "plan de continuidad".

---

## § 9. Anti-plan — lo que NO se hace y por qué

1. **No más módulos nuevos de producto sin señal externa** (regla § 0.2). Incluye: no Fase 2 de gamificación, no más paneles BPO, no un tercer canal de comunicaciones.
2. **No microservicios, no Kubernetes, no colas externas** (RabbitMQ/Kafka): el monolito modular con cola en Postgres es correcto para 2 órdenes de magnitud más de carga que la actual. La frontera de módulos se endurece con tests (2.5), no con red.
3. **No reescritura del frontend** (ni Blazor WASM, ni SPA JS): la decisión de Blazor Server se examina con números (2.1), no se revierte por moda. Si el número dice que aguanta, se documenta y se acabó el debate.
4. **No data lake / warehouse / BI externo**: los reportes salen de Postgres con las queries existentes hasta que un cliente pida algo que no pueda salir de ahí.
5. **No SOC 2 todavía**: para el mercado español con clientes PYME/consultora, el pentest (1.3) + RGPD sólido rinde más por euro; certificación cuando un cliente concreto la condicione.
6. **No marketplace de integraciones ni SDK público**: la API v1 completa (3.1) primero, con un consumidor real; el resto es especulación.
7. **No ampliar el catálogo de modelos IA por ampliarlo**: el router multi-proveedor existente basta; cada proveedor nuevo es un subencargado nuevo en el DPA (coste legal real por beneficio marginal).

---

## § 10. Mapa de secuencia y dependencias

```
Semana 1-2   H0: [0.1 Mudanza] → [0.2 Ensayo restauración]
             [0.3 Monitoreo] · [0.4 Legal→abogado] · [0.5 Repo]     ← todo en paralelo, nada depende de nada
Semana 3-6   H1: [1.1 Piloto] ← depende de 0.1 y 0.4
             [1.3 Pentest] ← depende de 0.1
             [1.4 RLS] · [1.5 Cierres] · [1.6 E2E] · [1.7 Billing min] ← paralelos
Mes 2-3      H2: [2.1 Examen Blazor] → [2.2 Lifetimes (si el número lo exige)]
             [2.3 OTel] → [2.4 Alertas]
             [2.5 Tests arquitectura] · [2.6 Deep-links] · [2.7 Menores]
             § 6.1-6.3 IA documental/reclamación/bandeja ← validadas contra el piloto de 1.1
Mes 3-6      H3: [3.1 API completa] → [3.2 Conector] (solo con cliente)
             [3.3 Portal terceros] (solo con señal) · § 7 kit pro según fricción real del piloto
```

**Los cinco primeros movimientos, en orden estricto**: mudanza (0.1) → ensayo de restauración (0.2) → monitoreo (0.3) → legal al abogado (0.4, en paralelo desde el día 1 porque su reloj es externo) → piloto (1.1). Todo lo demás de este documento es condicional a que esos cinco existan.

**Criterio de éxito del plan a 6 meses**: 1 cliente de pago renovando tras el piloto, restauración ensayada 2 veces, cero incidentes descubiertos por el cliente antes que por el monitoreo, el 50% del ciclo documental del tenant piloto ejecutándose sin intervención humana (§ 6.1-6.2), y las notas de `MATURITY_REVIEW` de DevOps y Producción por encima de 7. Si en 6 meses hay más fases nuevas de producto que ítems de este plan cerrados, el plan habrá fallado por la razón que su § 0 predijo.
