# Comunicaciones — bandeja compartida (correo + WhatsApp)

> Documento de referencia consolidado para el rediseño integral de la lógica de comunicación. **Parte I** (§ 1-9): estado actual implementado — diseño, modelo de dominio, flujo end-to-end, capas de código, configuración y pendientes conocidos. **Parte II** (§ 10-16): rediseño objetivo — el Communication Workspace definido en la sesión de diseño de producto (mockups 01-06 + estados), sus decisiones de lógica y el análisis de brecha contra lo ya construido.
>
> Fuentes primarias: [`ARQUITECTURA-INTEGRACIONES.md` § 12](../ARQUITECTURA-INTEGRACIONES.md), [`docs/ux-audit/12-comunicaciones.md`](ux-audit/12-comunicaciones.md), `ROADMAP.md` (Fases 59, 72-78, 84, 90-91), el código en `src/CaeManager.Domain/Comunicaciones/`, `src/CaeManager.Domain/Integraciones/`, `src/CaeManager.Application/Comunicaciones/`, `src/CaeManager.Infrastructure/Integraciones/`, `src/CaeManager.Web/Features/Comunicaciones/`, y el documento de la sesión de diseño "Contexto del rediseño con GPT" con sus 10 mockups.
>
> Estado a 2026-08-08: módulo implementado con dos canales reales (Microsoft 365 Graph y WhatsApp Cloud API de Meta), apagado en producción por falta de rodaje real (ver § 6). Rediseño de la Parte II — orden de ejecución § 16: **paso 0 (renombrado) y paso 1 (canal por mensaje) mergeados** (PRs #128/#129); **paso 2 (workspace unificado: fusión Bandeja+Chat, `UnifiedTimeline`, `ComposerBar` con fallback de canal) implementado y verificado end-to-end** — `/comunicaciones/chat` ya no existe como pantalla aparte, `Bandeja.razor` es ahora el único workspace para ambos canales; **rediseño visual sobre mockups mergeado** (PR #131); **paso 3 parcial — eventos del sistema en el timeline mergeados** (PRs #132 "Visita creada", #133 "Actualizar documentación desde adjunto"; el Action Center como patrón de UI genérico con tarjetas de confianza+Confirmar/Editar/Descartar sigue sin construir, ver § 14); **paso 4 — Conversation Matching Engine implementado con alcance honesto**: motor de score + `VincularConversacionCommand` (fusiona mensajes, nace un hilo mixto de verdad) + propuesta en el timeline, pero de los siete criterios de § 13.2 solo Mismo Cliente y Ventana temporal calculan con datos reales hoy — el resto queda en 0, documentado en `MotorCoincidenciaConversacionesService` (ver § 13.2 y § 14 para el detalle exacto de qué falta y por qué).

## 1. Qué es

Un único módulo de dominio, **`Comunicaciones`** (`src/CaeManager.Domain/Comunicaciones/`), implementa una bandeja compartida tipo Zendesk/Front con **dos canales sobre el mismo agregado**: correo (Microsoft 365 Graph) y WhatsApp (Meta Cloud API). Sustituye el uso de un buzón corporativo tipo Outlook para el trato con Clientes/Empresas/Trabajadores/Subcontratas, sin salir de Hydra.

**Decisión de diseño central (2026-08-04)**: WhatsApp se construyó como **extensión del agregado `ConversacionCorreo`**, no como agregado nuevo — "los nombres `*Correo` quedan como deuda nominal" (decisión explícita, documentada en el propio código). Esto es lo primero a decidir en un rediseño: mantener la deuda nominal o renombrar (`ConversacionCorreo`→`Conversacion`, `MensajeCorreo`→`Mensaje`), que sería un refactor propio, no mezclable con otros cambios (regla ya establecida en `CLAUDE.md`).

Por qué es un **Domain Module** y no una extensión del kernel de plataforma: una conversación tiene estado de negocio (Abierta/Pendiente/Resuelta/Cerrada), se asigna a un Ejecutivo CAE y cuelga de `Cliente` — no es infraestructura pura. Se construye **sobre** dos capacidades de plataforma (`docs/PLATFORM.md` § 4 Notifications, generalizada de envío unidireccional a canal bidireccional; e Integrations, reutilizando `ConexionIntegracion`/`CredencialIntegracion` en vez de inventar una cuarta clase de credenciales).

## 2. Modelo de dominio

### 2.1 `src/CaeManager.Domain/Comunicaciones/`

| Entidad | Capa | Rol |
|---|---|---|
| `ConversacionCorreo` | Agregado raíz, por tenant | Hilo de conversación. `Canal` (Correo/WhatsApp), `Estado` (Abierta/Pendiente/Resuelta/Cerrada), `ClienteId?` (null = triage), `EjecutivoAsignadoId?`, `ConexionIntegracionId?`, `HiloExternoId?` (conversationId de Graph, solo correo), `TelefonoContacto?` (solo WhatsApp — el "hilo" WhatsApp es `(ConexionIntegracionId, TelefonoContacto)` + estado no cerrado, no `HiloExternoId`, cuyo índice único impediría reabrir conversación con el mismo teléfono tras cerrar la anterior), `FechaUltimoMensajeEntranteUtc?` (ancla de la ventana de 24 h de Meta) |
| `MensajeCorreo` | Entidad hija, por tenant | `Direccion` (Entrante/Saliente), `RemitenteEmail` (en WhatsApp guarda el teléfono E.164 — mismo criterio de deuda nominal), `CuerpoHtml`, `MensajeExternoId?` (Message-ID de Graph o wamid de Meta — clave de idempotencia ante reintentos de webhook), `EstadoEntrega?`/`ErrorEntrega?` (solo salientes WhatsApp) |
| `AdjuntoMensajeCorreo` | Entidad hija | Reutiliza `IFileStorageService`, no storage paralelo |
| `ParticipanteConversacion` | Entidad hija, por tenant | De/Para/Cc polimórfico hacia Trabajador/Subcontrata/Empresa/Centro (`TipoOrigen` + `EntidadRelacionadaId?`, sin FK real a propósito — cada entidad relacionada vive en su propio agregado). Permite que un hilo con el Trabajador, la Subcontrata y el Centro de una visita en copia siga siendo una única conversación. **Solo lo puebla el flujo de correo** — `ConversacionCorreo.CrearWhatsApp` no agrega participantes; es un hueco si el rediseño quiere tratar ambos canales simétricamente |
| `ContactoWhatsApp` | Catálogo, por tenant | Teléfono→Cliente autoalimentado: se escribe la primera vez que un gestor resuelve el triage (`AsignarClienteConversacionCommand`), y enruta directo las siguientes conversaciones del mismo teléfono al gestor de cartera. No existe ningún otro catálogo de teléfonos en el dominio — nace vacío y aprende del uso |
| `MacroRespuesta` | Catálogo, por tenant | `ClienteId?` null = genérica del tenant, poblado = específica de ese cliente. Variables de sustitución simples (contacto/cliente/centro). Hoy **solo aplicable desde el compositor**, sin disparo desde otros módulos (ver Hueco H1, § 7) |

Enums: `CanalConversacion` (Correo/WhatsApp), `EstadoConversacion` (Abierta/Pendiente/Resuelta/Cerrada), `EstadoEntregaMensaje` (Enviado→Entregado→Leído→Fallido, progresión monótona — un "delivered" tardío no pisa un "read" ya registrado; Fallido es terminal), `RolParticipante`, `TipoParticipanteOrigen`.

Todas llevan `TenantId` NOT NULL, soft delete (`EntidadBase`) y quedan cubiertas por `AuditoriaInterceptor`.

### 2.2 `src/CaeManager.Domain/Integraciones/` (satélites de conexión)

| Entidad | Rol |
|---|---|
| `ConexionIntegracion` | Agregado, por tenant. Buzón/línea conectada. `Proveedor` (Microsoft365/WhatsApp), `ClienteId?` (**nuevo respecto al diseño original de § 3** — línea dedicada a un Cliente Delegante dentro de una Consultora, vs. `null` = del propio Tenant), `Estado` (Habilitada/ConError/Deshabilitada) |
| `LineaWhatsApp` | Satélite 1:1 de `ConexionIntegracion`, mismo patrón que `CredencialIntegracion` para M365. `PhoneNumberId` (índice único **global**, sin TenantId — resuelve el tenant del webhook, mismo criterio que `ClaveApi.HashClave`), `WabaId`, `NumeroTelefono` (solo informativo — la clave operativa es `PhoneNumberId`), `TokenAcceso` (System User token de larga duración de Meta, cifrado con el protector de integraciones; no rota solo, se sustituye a mano cuando el administrador lo renueva en Meta), `Modo` (GestorFijo/PoolInbound), `ComercialAsignadoId?` (requerido en GestorFijo), `MensajeAutoTriage?` (null = desactivado) |
| `MiembroPoolLinea` | Entidad hija. Un gestor que participa en el reparto equitativo de una línea en modo PoolInbound |
| `EventoWebhook` | Cola durable compartida entre proveedores. Payload crudo persistido antes de procesar — nunca se procesa dentro del request HTTP |

Enums: `ModoAsignacionLinea` (GestorFijo = todo a su comercial dueño, modelo outbound; PoolInbound = reparto equitativo por carga, modelo inbound), `ProveedorIntegracion` (Microsoft365 por defecto, WhatsApp).

## 3. Flujo end-to-end — WhatsApp entrante

1. **Meta POST** → `WebhookWhatsAppEndpoints` (`src/CaeManager.Web/Api/Integraciones/WebhookWhatsAppEndpoints.cs`, ruta `GET/POST /api/integraciones/webhooks/whatsapp`, única a nivel de app Meta — no lleva `conexionId`): valida `X-Hub-Signature-256` (HMAC-SHA256 del cuerpo crudo con el App Secret, comparación en tiempo constante) **antes** de saber a qué tenant pertenece (orden innegociable, `docs/MULTITENANCY.md` § 8).
2. Extrae los `phone_number_id` del payload → `WebhookWhatsAppTenantResolver` (`src/CaeManager.Infrastructure/Integraciones/`) resuelve el tenant vía `IgnoreQueryFilters()` justificado (revisión explícita, sin tenant resuelto todavía es exactamente lo que este método existe para resolver) sobre el índice único global de `LineaWhatsApp.PhoneNumberId`.
3. Persiste un `EventoWebhook` (payload crudo, sin procesar) dentro de `AmbitoTenantExplicito` y responde `200` en menos de 3 s — Meta reintenta agresivamente; nunca se llama a Meta ni se interpreta el mensaje dentro del request. Una línea desconocida (dada de baja) se ignora sin devolver error, para no forzar reintentos eternos de un payload que nunca se va a poder atender.
4. Dispara `ISenalIngestaWhatsApp` (señal en memoria, sin datos — no reintroduce el problema de un `Channel<T>`; perderla cuesta como máximo un tick) para despertar al consumidor.
5. `IngestaWebhookWhatsAppHostedService` (`src/CaeManager.Infrastructure/Integraciones/`, lock de líder propio `ingesta-webhook-whatsapp` para que un backlog de correo no retrase el chat, espera híbrida señal + tick de 10 s de red de seguridad) recoge el evento pendiente y llama a `IngestaWebhookWhatsAppService` (`src/CaeManager.Application/Integraciones/`):
   - Dedup por `wamid` sobre el índice `{TenantId, MensajeExternoId}`.
   - **Enrutamiento híbrido** de conversación nueva (§ 4).
   - Persiste el `MensajeCorreo` entrante, descarga adjuntos multimedia en dos saltos (metadatos → URL efímera de lookaside → binario; mejor esfuerzo, un fallo de descarga no pierde el mensaje ya persistido con su placeholder/caption), actualiza `FechaUltimoMensajeEntranteUtc`.
   - Aplica `statuses[]` (sent/delivered/read/failed) a mensajes salientes ya existentes, con progresión monótona; un status sobre un mensaje que no existe (enviado desde otra herramienta con la misma línea) se ignora en silencio.
6. Tras `SaveChanges`, publica `MensajeWhatsAppRecibidoEvent` (primer `MediatR.INotification` del repositorio, publicado DESPUÉS del commit) → `NotificadorMensajesTiempoReal` (`src/CaeManager.Infrastructure/Coordinacion/`, singleton en proceso, por tenant, suscripción desechable por circuito) → cualquier circuito Blazor suscrito (`/comunicaciones/chat`) se refresca sin recargar, verificado en navegador con latencia <1 s.

### 3.1 Saliente

`ResponderConversacionWhatsAppCommand` (`src/CaeManager.Application/Comunicaciones/Commands/ResponderConversacionWhatsApp/`):
1. Valida cartera (`IAlcanceDatosService.ClienteOpcionalVisibleAsync`) — mismo guard N-3 que el resto de handlers de Comunicaciones.
2. Confirma canal WhatsApp y que la conversación tiene `TelefonoContacto`/`ConexionIntegracionId`.
3. Bloquea si `VentanaServicioAbierta()` es falsa (más de 24 h desde el último entrante) — plantillas aprobadas **no soportadas en v1**, el envío libre se rechaza en servidor, no solo en la UI.
4. Envía vía `WhatsAppCloudApiClient.EnviarTextoAsync` (`src/CaeManager.Infrastructure/Integraciones/`) — solo persiste el mensaje si Meta aceptó (sin mensajes fantasma, mismo criterio que el handler de correo). El wamid devuelto se guarda como `MensajeExternoId` para casar los `statuses[]` posteriores.

## 4. Enrutamiento y asignación de conversaciones nuevas

Resuelto en `IngestaWebhookWhatsAppService.ResolverAsignacionAsync`:

1. **Leg 1 — contacto conocido**: el teléfono ya se resolvió alguna vez contra un Cliente (`ContactoWhatsApp`) → directo al gestor de cartera (`Cliente.EjecutivoUsuarioId`), modelo outbound.
2. **Leg 2 — modo de línea**: si el contacto es desconocido, decide el modo de la `LineaWhatsApp` — `GestorFijo` → su comercial dueño; `PoolInbound` → el miembro con menos conversaciones WhatsApp vivas asignadas (reparto equitativo por carga, empate por orden estable de Id). El `ClienteId` de la conexión (línea dedicada a un cliente) resuelve el cliente aunque el contacto sea desconocido.
3. **Leg 3 — triage**: si tras eso el Cliente sigue sin identificarse y la línea tiene `MensajeAutoTriage`, se envía el auto-mensaje pidiendo cliente y motivo (gratis: el entrante acaba de abrir la ventana de 24 h) y la conversación queda en `ClienteId = null` — cola de triage.

`AsignarClienteConversacionCommand` resuelve el triage manualmente y **autoalimenta** `ContactoWhatsApp` para que la próxima conversación del mismo teléfono no necesite triage.

**Fuera de alcance explícito de v1** (`ARQUITECTURA-INTEGRACIONES.md` § 12.7): multi-gestor por cliente y delegación por vacaciones — v1 usa la cartera existente tal cual.

### 4.1 Resolución de remitente desconocido en correo (para contraste)

Pipeline distinto al de WhatsApp, nunca una asignación automática ciega (`ARQUITECTURA-INTEGRACIONES.md` § 12.4):
1. Dominio del email del remitente contra dominios ya registrados de un Cliente → auto-asocia sin intervención humana.
2. Si no matchea ningún dominio: IA lee el cuerpo y **sugiere** un Cliente candidato — nunca decide ni asigna directamente.
3. Cae siempre en cola de triage (`ClienteId = null`, visible para el rol Supervisor) hasta que una persona confirma.

## 5. Diferencias deliberadas entre los dos conectores

El conector WhatsApp es un **clon estructural** del slice de Microsoft 365, no construido a través del framework genérico `IIntegrationProvider` de § 4-6 de `ARQUITECTURA-INTEGRACIONES.md` — ese framework está pensado para sincronización documental CAE (Dokify/CTAIMA), y forzar un canal de chat dentro de ese contrato habría sido "cumplir la letra violando el espíritu" (YAGNI). Lo que sí se reutilizó: § 6.4 completo (firma antes de tenant, cola durable, nunca procesar en el request) y § 6.5 (el patrón pub-sub job→circuito).

| Aspecto | Microsoft 365 | WhatsApp Cloud API |
|---|---|---|
| URL de webhook | Una por conexión (`/{conexionId}`) | Una por app de Meta — la línea se resuelve por `phone_number_id` contra el índice único global |
| Autenticación del webhook | Comparación de `clientState` (Graph no firma) | HMAC-SHA256 real (`X-Hub-Signature-256`) — más estricto que el precedente |
| Credencial | Refresh token OAuth rotativo (`CredencialIntegracion`) | System User token de larga duración (`LineaWhatsApp.TokenAcceso`), satélite del mismo agregado, mismo cifrado |
| Latencia del consumidor | Tick de 10 s | Híbrido señal-en-memoria + tick de 10 s |
| Modelo de conversación | `ConversacionCorreo` por `HiloExternoId` | Mismo agregado con `Canal = WhatsApp`; hilo = `(ConexionIntegracionId, TelefonoContacto)` + estado no cerrado; `HiloExternoId` queda null |

Piezas propias del canal WhatsApp: enrutamiento híbrido (§ 4), ventana de servicio de 24 h de Meta, estados de entrega (`statuses[]`), página `/comunicaciones/chat` con refresco instantáneo.

## 6. Capas de código (mapa completo)

### Application (`src/CaeManager.Application/`)
- **Comunicaciones**: `ObtenerConversacionesQuery`, `ObtenerConversacionPorIdQuery`, `ResponderConversacionCommand` (correo) / `ResponderConversacionWhatsAppCommand`, `AsignarClienteConversacionCommand`, `AsignarEjecutivoConversacionCommand`, `CambiarEstadoConversacionCommand`, `EnviarMensajeNuevoCommand`, `ObtenerSugerenciasVisitaCorreoPendientesQuery`, `MensajeWhatsAppRecibidoEvent`, `PublicarMensajeEnNotificadorHandler`.
- **Integraciones**: `IngestaWebhookWhatsAppService`, `CrearLineaWhatsAppCommand`/`ActualizarLineaWhatsAppCommand`, `ObtenerLineasWhatsAppQuery`, `ObtenerConexionesIntegracionQuery`, contratos `IWhatsAppCloudApiClient`/`IWebhookWhatsAppTenantResolver`/`ISenalIngestaWhatsApp`.

### Infrastructure (`src/CaeManager.Infrastructure/`)
- `WhatsAppCloudApiClient` (parseo de payload, envío de texto, descarga de media).
- `IngestaWebhookWhatsAppHostedService`, `WebhookWhatsAppTenantResolver`, `SenalIngestaWhatsApp`, `NotificadorMensajesTiempoReal`.
- Repositorios: `LineaWhatsAppRepository`, `ContactoWhatsAppRepository`, `ConversacionCorreoRepository`.
- `WhatsAppCloudApiOptions`, `ComunicacionesOptions` (config).
- `TenantSelladoInterceptor` (fix de frontera multi-tenant descubierto en la Fase 84, ver § 8).

### Web (`src/CaeManager.Web/`)
- `WebhookWhatsAppEndpoints` (`GET/POST /api/integraciones/webhooks/whatsapp`).
- `/comunicaciones/chat` (`Chat.razor`/`Chat.razor.cs`) — chat en vivo master-detail: burbujas, ticks de estado de entrega, banner de ventana de 24 h con hora de cierre, composer Enter-para-enviar (Shift+Enter = salto de línea), triage inline con selector de Cliente, filtros (mías/sin asignar/triage), suscripción a `INotificadorMensajesTiempoReal` en `OnInitializedAsync` con `Dispose()` de la suscripción.
- `/comunicaciones/bandeja` (`Bandeja.razor`/`Bandeja.razor.cs`) — cola de correo agrupada por Cliente, "Sin cliente asignado (Triage)" primero con contador, filtros Estado/Mes/Cliente + Asignado a mí/Sin asignar, macros genéricas y por cliente, banner de sugerencia de visita detectada.
- `/comunicaciones/buzon` (`Buzon.razor`) — navegador de solo lectura del buzón real M365 (árbol de carpetas + historial), separado de la Bandeja gestionada.
- `/integraciones` (`Conexiones.razor`/`Conexiones.razor.cs`) — alta/edición de líneas WhatsApp (modal: nombre, número, Phone Number ID, WABA ID, token, modo de asignación, comercial/pool, Cliente asociado, mensaje de auto-triage — restringido a rol Administrador) y conexión/desconexión de buzones M365.

## 7. Seguridad multi-tenant

- Firma HMAC-SHA256 verificada antes de resolver tenant (§ 3.1).
- `PhoneNumberId` con índice único **global** (sin TenantId) — es la única columna del módulo que se consulta con `IgnoreQueryFilters()`, justificada explícitamente porque es el mecanismo que *resuelve* el tenant, no una fuga del filtro.
- RLS habilitado para `LineasWhatsApp`, `MiembrosPoolLinea`, `ContactosWhatsApp`, `ConversacionesCorreo`/`MensajesCorreo` en las mismas migraciones que las crean.
- **Fix descubierto en la verificación de la Fase 84** (afectaba también a correo M365): EF clasificaba como `Modified` entidades hijas nuevas descubiertas por fixup de navegación con Guid de constructor — el segundo mensaje de cualquier hilo cargado moría en `TenantSelladoInterceptor` con "pertenece a otro tenant". Se corrigió reclasificando a `Added` cuando el `TenantId` original está vacío (la columna es NOT NULL en BD, señal inequívoca de entidad nueva). Cubierto por `AgregarMensajeAConversacionCargadaTests` en ambos canales.
- **Aislamiento del buzón personal (encontrado 2026-08-13, explorando el backlog nocturno)**: un buzón personal de un gestor (`ConexionIntegracion.GestorPropietarioId`) tiene `ClienteId` null, igual que la cola de triage genuina y que el buzón genérico del tenant — nada lo distinguía. Sin corregir, cualquier acción de negocio que resolviera "el buzón a usar" (`EnviarReclamacionCommand`, `PedirPrioridadValidacionCommand`, `ObtenerBorradorPedirPrioridadQuery`, `MigrarConversacionACorreoCommand`) podía elegir el buzón personal de un gestor cualquiera sin que él lo supiera, y la bandeja/detalle/acciones generales (`ObtenerConversacionesQuery`, `ObtenerConversacionPorIdQuery`, y los seis Commands que cargan una Conversacion existente) dejaban ver/responder correspondencia personal ajena. Cerrado con `IAlcanceDatosService.ConexionIntegracionVisibleAsync` + `AlcanceDatosServiceExtensions.ConversacionVisibleAsync`, aplicado en los ocho puntos de contacto.

## 8. Configuración y flag de activación

- `Integraciones__WhatsApp__AppSecret`/`VerifyToken`/`VersionApi` (opcional, por defecto `v23.0`) — ver `DEPLOY.md` § "WhatsApp Cloud API". Sin `AppSecret`+`VerifyToken`, el consumidor de ingesta no se registra y el webhook responde 401/403 a todo ("inerte por defecto").
- Token por línea (System User) se pega en `/integraciones` al dar de alta, cifrado en BD con el protector de integraciones — no rota solo, se sustituye a mano cuando se renueva en Meta.
- `ComunicacionesOptions.Activo` — **código por defecto `true`** desde 2026-08-03 (reactivado tras construir ingesta real completa para ambos canales), pero el UX audit de 2026-08-05 registra que en producción sigue **apagado explícitamente por configuración** (`NavMenu.razor:13-16`), por falta de rodaje real con datos/credenciales reales — no por el flag de código en sí.
- **Antes de usar con contactos/buzones reales**: el DPA debe declarar este canal (paquete legal en `docs/business/legal/` ya redactado pero pendiente de firma real por tenant — ver `CLAUDE.md`); la ingesta de mensajes y teléfonos de contacto es tratamiento de datos personales por cuenta del tenant, igual que el conector M365.

## 9. Pendientes y huecos ya identificados

### De la auditoría UX ([`docs/ux-audit/12-comunicaciones.md`](ux-audit/12-comunicaciones.md), 2026-08-05)

| Hallazgo | Impacto usuario | Impacto negocio | Horizonte |
|---|---|---|---|
| ~~**H1**~~ — **Parcialmente cerrado (2026-08-13)**: `EnviarReclamacionCommand` ya nace como `Conversacion` con rastro completo (hilo, timeline, "Ver en Comunicaciones"), y Centro 360 dispara la reclamación de un Centro concreto con un clic (`ObtenerLoteReclamacionQuery(CentroId:)`). Sigue sin disparo desde un documento individual vencido/faltante en la ficha de Documentos — eso queda abierto. | Alto → Medio | Alto → Medio | — |
| ~~**H2**~~ — **Cerrado (2026-08-13)**: `ObtenerConversacionesQuery.Busqueda` compara ahora Asunto Y el cuerpo de los mensajes del hilo (sin índice de texto completo — mismo nivel que el resto de búsquedas del repo). | — | — | — |
| **H3** (hipótesis, sin validar) — `/bandeja` (cola documental) y `/comunicaciones` (cola de correo/chat) conviven como dos "bandejas" en el menú; posible confusión de vocabulario | Medio | Bajo | Quick win (renombrar) |
| ~~Riesgo de escalabilidad~~ — **Cerrado (2026-08-13)**: la bandeja pagina en SQL (`ResultadoPaginado<ConversacionListaDto>`, 20 por página) en vez de cargar todas las conversaciones que cumplan los filtros. `SoloEsperandoCliente` pasó de filtro en memoria a filtro de servidor para que la paginación no deje páginas "vacías" con coincidencias más adelante. Trade-off aceptado: un Cliente con conversaciones a ambos lados del corte de página puede quedar partido entre dos páginas. | — | — | — |

### De `ARQUITECTURA-INTEGRACIONES.md` § 12.7/§ 13 (deuda de diseño ya reconocida, no accidental)

- **Plantillas aprobadas de Meta**: no soportadas — fuera de la ventana de 24 h el envío se bloquea sin alternativa.
- **Multi-gestor por cliente / delegación por vacaciones**: explícitamente diferido, v1 usa la cartera fija existente.
- **`ParticipanteConversacion`** no se puebla en WhatsApp — asimetría entre canales que un rediseño podría cerrar o formalizar como decisión.
- ~~**Nombres `*Correo`** sobre un agregado multicanal~~ — resuelta en el paso 0 del rediseño (PR #128, 2026-08-07): `ConversacionCorreo`/`MensajeCorreo` → `Conversacion`/`Mensaje`.
- El conector WhatsApp no pasa por el framework genérico `IIntegrationProvider`/`IIntegrationProviderFactory`/`IIntegrationOrchestrator` (§ 4-6) — un tercer canal de mensajería repetiría el mismo patrón a mano en vez de generalizar; el framework genérico se construye "cuando exista un segundo proveedor real priorizado con caso de uso confirmado por el negocio" (criterio ya fijado, sin decidir todavía si WhatsApp+M365 ya cuentan como ese caso).
- El zip de paquete documental automático (Fase 77, ligado a visitas por correo) **no se reenvía por Graph/WhatsApp** — queda adjunto en Hydra para reenvío manual (límite de 3 MB de adjunto inline de Graph, sin `createUploadSession` implementado).
- Sin verificación end-to-end contra credenciales reales de Meta en producción (checklist pendiente del PR de la Fase 84).

---

# PARTE II — Rediseño objetivo: Communication Workspace

> Fuente: sesión de diseño de producto (2026-08, "Contexto del rediseño con GPT" + 10 mockups de alta fidelidad: 01_LAYOUT, 03_WORKSPACE, 03.1_HEADER, 03.1_UNIFIED_TIMELINE, 03.2_ACTION_CENTER, 03.3/05_COMPOSER, 03.4_CREACIÓN_VISITA, 03.5_ACTUALIZACIÓN_DOCUMENTACIÓN, 06_CONTEXT_WORKSPACE, más la lámina de bandeja/estados). El módulo deja de llamarse conceptualmente "Comunicaciones" (un gestor de correo) y pasa a ser el **Communication Workspace / Centro de Comunicación Inteligente**: el orquestador de flujos CAE del Delegated Workspace activo.

## 10. Principios rectores del rediseño

1. **Toda comunicación debe terminar en una acción del dominio** (Conversation → Action). El flujo objetivo no es "leer → responder", es: mensaje entrante → clasificación IA → extracción de entidades → propuesta de acción → confirmación del gestor → ejecución. El gestor deja de ser un operador que copia datos entre módulos y pasa a ser un supervisor que valida propuestas ("leer → confirmar → continuar").
2. **El canal es un atributo del mensaje, no del módulo.** Para el gestor no existe "Correo" vs "WhatsApp": existe la conversación. Un mismo hilo mezcla cronológicamente emails, WhatsApp, eventos del sistema ("el certificado fue validado", "se creó la visita V-2026-0815"), detecciones/sugerencias de IA y notas internas. WhatsApp **nunca es otra pantalla** — la pantalla `/comunicaciones/chat` separada desaparece.
3. **IA como copiloto, no invitado**: nunca un chat flotante; acciones inteligentes con nivel de confianza en el momento adecuado, siempre con confirmación del gestor. (Coincide con la disciplina ya establecida en el proyecto — `DeteccionTrabajador`, `SugerenciaVisitaCorreo`: la IA propone, nunca decide.)
4. **El usuario entra a responder "¿qué conversaciones requieren mi atención?"**, no "¿qué correos tengo?".
5. **Menos clics, más contexto. Diseño adaptativo, no responsive.** Este módulo se diseña como Workspace adaptativo, no como grid fijo. *(Corrección 2026-08-08: la redacción anterior afirmaba que "todo el portal migró a Adaptive Layout", lo cual era falso — solo este módulo lo hizo. Era una de las contradicciones que motivaron el reset documental; ver DDL-023.)*
6. La **sidebar global de Hydra se mantiene** — el Workspace vive dentro de la entrada "Comunicaciones" del menú; la sidebar deja de ser protagonista pero nunca desaparece.

## 11. Arquitectura visual (blueprint 01)

```
Sidebar Hydra (existente)
  └─ Comunicaciones
     ┌────────────────────────────────────────────────────────────────┐
     │ Toolbar Global (72px, siempre visible):                        │
     │ Workspace Selector (Delegated Workspace) · Bandeja · Buscar    │
     │ (Ctrl+K) · Filtros · Actualizar · IA · Configuración           │
     ├──────────────┬──────────────────────────┬──────────────────────┤
     │ Inbox        │ Conversation Workspace   │ Context + Action     │
     │ 360px (XL)   │ flexible (min 760px)     │ Center 340px         │
     ├──────────────┴──────────────────────────┴──────────────────────┤
     │ Composer fijo (siempre visible)                                │
     ├────────────────────────────────────────────────────────────────┤
     │ Panel IA abatible (52px cerrado / 320–420px abierto,           │
     │ colapsado por defecto)                                         │
     └────────────────────────────────────────────────────────────────┘
```

**Breakpoints Adaptive** (comportamiento, no versiones móvil/escritorio):

| Breakpoint | Comportamiento |
|---|---|
| ≥1600px (XL) | Tres columnas visibles + panel IA colapsado |
| 1440px | Tres columnas con anchos reducidos (Inbox/Context 320px) |
| 1280px (laptop) | Context pasa a Drawer deslizante; Inbox + Conversation visibles |
| 1024px (tablet) | Solo Conversation visible; Inbox y Context en Drawers |
| ≤768px (mobile) | Una sola columna por pasos: Inbox → Conversación → Contexto/Acciones |

**Componentes nuevos** (solo cuatro; el resto reutiliza el Design System existente — QuickGrid, Badge, Drawer, Toast, EstadoVacio…):
- `WorkspaceSelector` — selector del Delegated Workspace activo (reutiliza la infraestructura ya implementada de ADR-004; toda la información del workspace cambia al conmutarlo).
- `ConversationCard` — tarjeta de conversación en la bandeja con indicadores de canal, estado, no-leídos y asignación.
- `UnifiedTimeline` — timeline que mezcla Email, WhatsApp, eventos del sistema, notas internas y acciones de IA. Reutilizable después en Historial/Auditoría/Actividad.
- `ComposerBar` — barra fija de respuesta con canal, macros, adjuntos, IA y selección del buzón remitente. Reutilizable en Incidencias/comentarios.

## 12. Piezas del workspace (mockups 03.x, 05, 06)

### 12.1 Inbox (columna izquierda)
- Bandejas por estado con contadores: **Todas / Pendientes / Sin asignar / Esperando cliente / Resueltas** — no hay bandeja "Correo" ni "WhatsApp"; dentro aparecen conversaciones, no canales.
- Agrupado por Cliente, con "Sin cliente asignado (Triage)" como grupo propio con contador (se conserva el patrón actual).
- Cada `ConversationCard`: icono del canal del último mensaje, asunto, preview, antigüedad, badge de no-leídos, estado. Búsqueda dentro de la bandeja. "Mostrar conversaciones resueltas" como toggle al pie.

### 12.2 Conversation Header (03.1)
Título + badges de **todos los canales involucrados** en el hilo (`Canal mixto` ✉️+📱), ID del hilo, estado; chips de contexto: Cliente / Centro / Trabajador principal / Responsable asignado / Prioridad; acciones primarias: Marcar como pendiente · Resolver conversación · Responder; pestañas: Resumen / Timeline / Participantes / Archivos / Acciones IA / Historial / Notas. Controles del hilo: Asignarme, Cambiar responsable, Añadir etiqueta, Silenciar, Cerrar. El resumen incluye: canales activos, etiquetas, origen, idioma detectado, confianza IA promedio y recuento de mensajes.

### 12.3 Unified Timeline (03.1/03.2)
Cronología única con tipos de entrada tipados:
- **Mensaje entrante/saliente** con badge de canal (WhatsApp/Email), participante, hora, adjuntos descargables.
- **Evento del sistema** ("Se ha creado la conversación desde WhatsApp", "Estado de la visita V-2026-0817: Confirmada" — con enlace al módulo).
- **IA Classifier** (clasificación de la conversación con confianza), **IA Extraction** (entidades detectadas con confianza por campo: Tipo/Cliente/Trabajador/Fecha/Centro), **IA Suggestion** (propuesta de acción con botón "Ver propuesta").
- **Nota interna** (visible solo para el equipo).
- Filtros del timeline: Todos / Mensajes / Eventos / IA / Adjuntos; separadores por fecha; indicador "escribiendo…"; estados de entrega salientes WhatsApp: Enviado → Entregado → Recibido → Leído → Fallido (requiere acción).

### 12.4 Composer Inteligente (05 / 03.3)
- **Responder como**: buzón/línea configurada del Delegated Workspace activo — el gestor siempre responde desde una identidad del cliente.
- **Canal de respuesta**: `Email` / `WhatsApp` / `Mixto` (ambos) — elegido por el gestor, nunca automático.
- **Macro/plantilla** (genéricas o por cliente, ya existen) + **variables dinámicas** insertables como chips: `{{Cliente}}`, `{{Centro}}`, `{{Trabajador}}`, `{{FechaPropuesta}}`, `{{HoraPropuesta}}`, `{{UsuarioActual}}`, `{{Firma}}`.
- Editor enriquecido (estilos, listas, tablas, enlaces, menciones `@participante`), adjuntos con drag&drop + "Insertar desde documentos" (adjuntar Documentos ya existentes en el sistema, máx. 25 MB por archivo), **firma** configurable, **marcar como interna** (nota de equipo, no se envía), **prioridad** (Baja/Media/Alta/Urgente), **programar envío**, **guardar borrador** (el borrador se guarda en el hilo), autosave visible.
- **IA — Sugerir respuesta**: borrador generado desde el hilo + contexto + tipo de acción detectada, con "Usar sugerencia / Editar".
- Atajos: Ctrl+Enter enviar, Ctrl+S borrador, Ctrl+@ mencionar, Ctrl+/ variables, Esc cerrar.

### 12.5 Context Workspace (06, columna derecha)
Deja de ser una ficha mínima: Cliente (cumplimiento %, criticidad, gestor, contadores de centros/empresas/trabajadores, ficha completa), centros relacionados al hilo, empresas y trabajadores mencionados/detectados, documentos recientes con vigencia, alertas y obligaciones activas (documentos por vencer/caducados, próxima visita sugerida, trabajadores sin formación), timeline del hilo, participantes con su canal, notas del cliente, historial de conversaciones. Todo contextual al hilo seleccionado y actualizado en tiempo real. En pantallas menores se convierte en Drawer.

### 12.6 Action Center (03.2 — el diferenciador)
Panel de **decisiones, no información**: tarjetas de acción sugerida por IA con confianza y botones Confirmar / Editar / Descartar:
- **Crear visita** (96%) → flujo 03.4.
- **Actualizar documentación mensual** (98%) → flujo 03.5, con opción "Actualizar automáticamente" o "Revisar".
- **Relacionar trabajador / participante** (91%) → vincular conversación a la entidad.
- **Relacionar conversación** (89%) → Conversation Matching (§ 13.2).
- **Asignar responsable**, **Generar borrador de respuesta**, **Solicitar documento faltante**.
- "Conversaciones similares" con su desenlace (Visita creada / Resuelta).
- Código de confianza: alta ≥90% / media 70–89% / baja <70%.
- El patrón Action Center está pensado como reutilizable fuera de Comunicaciones (Documentos, Visitas, Incidencias).

### 12.7 Flujos guiados de acción (03.4 / 03.5)
- **Crear visita desde conversación** (4 pasos: Extraer información → Revisar y confirmar → Completar detalles → Confirmar y crear): la IA extrae Cliente/Centro/Trabajador/fecha/hora con confianza por campo; los campos de baja confianza (Centro 81%) se marcan para verificación; si el Cliente tiene varios centros y no se especifica, se pregunta; muestra disponibilidad y participantes sugeridos; al crear, se registran las notificaciones y el evento en el timeline. *(Extiende el `SugerenciaVisitaCorreo` ya existente — hoy solo prellena el drawer de `/visitas`.)*
- **Actualizar documentación desde conversación** (4 pasos: Detectar y extraer → Revisar y confirmar → Validar cambios → Aplicar y registrar): adjuntos de un mensaje (TC2, Seguros RC…) → extracción IA (tipo de documento, entidad, centro, mes/vigencia, fechas) → propuesta de actualización lado-a-lado contra el estado actual del sistema → validación por reglas de negocio (vigencia por caducidad, unicidad por tipo y entidad, formato válido, mes actual o anterior) → confirmación del gestor y registro en el historial. *(Nuevo — hoy los adjuntos de conversación no alimentan el módulo Documentos; el pipeline IA de extracción `DetectarCamposDocumentoQuery` existe pero solo para la subida manual/masiva.)*

## 13. Decisiones de lógica del rediseño (las que cambian el dominio)

### 13.1 El canal baja de la conversación al mensaje
Hoy `Canal` vive en `ConversacionCorreo` (un hilo = un canal). El objetivo son **hilos mixtos**: `Canal` pasa a ser atributo de `MensajeCorreo`, y la conversación agrega los canales involucrados. Es el cambio de modelo más estructural del rediseño (migración incluida) y arrastra la identidad del hilo (§ 13.2).

### 13.2 Conversation Matching Engine — unir canales nunca es automático
Un WhatsApp **no** se une a un hilo de correo por tiempo ni "porque sí". La IA calcula un score de pertenencia:

| Criterio | Peso |
|---|---|
| Mismo Cliente | +40 |
| Similaridad semántica | +35 |
| Mismo Trabajador | +25 |
| Mismo Centro | +20 |
| Mismo Documento | +20 |
| Mismo Proyecto | +15 |
| Ventana temporal (<72 h) | +10 |

Con score alto, la UI propone: "La IA ha encontrado una conversación relacionada — ( ) Crear conversación nueva / (x) Añadir a conversación existente". **El gestor confirma siempre.** Mientras no confirme, el mensaje vive en su propia conversación (el enrutamiento actual de § 4 sigue siendo el fallback).

**Implementado (2026-08-08) con alcance honesto** — `MotorCoincidenciaConversacionesService` (`src/CaeManager.Application/Comunicaciones/Matching/`) calcula el desglose exacto de la tabla de arriba para cada conversación abierta del mismo Cliente, pero de los siete criterios **solo dos tienen hoy un dato real que los respalde**:
- **Mismo Cliente (+40)**: real — trivial, porque las candidatas ya vienen filtradas por Cliente.
- **Ventana temporal <72h (+10)**: real — comparación de timestamps.
- **Similaridad semántica (+35)**: en 0. No existe ningún servicio de embeddings/similaridad vectorial en el repo; construirlo es una decisión de proveedor de IA nueva, fuera de alcance de esta pieza.
- **Mismo Trabajador (+25) / Mismo Centro (+20)**: en 0. Un WhatsApp entrante no trae Trabajador/Centro resuelto — `ParticipanteConversacion` no se puebla en WhatsApp (hueco ya identificado más abajo en esta misma sección).
- **Mismo Documento (+20)**: en 0. No hay resolución adjunto→Documento antes de que el gestor confirme nada en este flujo.
- **Mismo Proyecto (+15)**: en 0. `Proyecto` (`src/CaeManager.Domain/Proyectos/`) es un agregado real pero de documentación de obra/instalación — un concepto de negocio distinto, sin ningún vínculo con `Conversacion`.

El umbral de propuesta (`UmbralPropuesta = 50`) exige Cliente + Ventana temporal a la vez — con las señales de hoy, "score alto" significa en la práctica "el cliente tiene otra conversación abierta con actividad en las últimas 72 h". Confirmar la propuesta (`VincularConversacionCommand`) fusiona de verdad los mensajes del hilo WhatsApp dentro del hilo elegido (cada mensaje conserva su propio `Canal` — nace un hilo mixto real, cerrando la deuda de § 13.1) y dispara un evento `ConversacionVinculada` en el timeline del hilo resultante. Sin migración nueva: no hay ninguna entidad de "sugerencia" persistida — se recalcula en cada lectura de `ObtenerConversacionPorIdQuery`, igual de barato que leer la conversación.

### 13.3 Las respuestas viajan por el canal de origen
Cada mensaje recuerda su canal, y la respuesta sale por defecto por el canal desde donde el contacto escribió — nunca se duplica por ambos canales automáticamente (genera confusión). El gestor puede cambiar el canal de respuesta explícitamente (Email / WhatsApp / Mixto) desde el composer. Si el destino incluye WhatsApp y la ventana de 24 h está cerrada, **la conversación migra a correo** — el composer fuerza el canal Email en el mismo hilo (decisión § 16.5); el bloqueo seco actual desaparece.

### 13.4 WhatsApp sigue siendo solo-respuesta
Las líneas de WhatsApp no inician conversaciones — solo contestan chats iniciados por el cliente (decisión de alcance explícita del usuario, coherente con la ventana de 24 h sin plantillas aprobadas). El buzón se autoasigna con el enrutamiento híbrido ya implementado (§ 4).

### 13.5 Estados del hilo
El flujo del mockup (Abierta → Pendiente → Resuelta → Cerrada) coincide con el enum `EstadoConversacion` actual. La bandeja "Esperando cliente" hay que mapearla (¿= Pendiente, o estado nuevo?) — decisión pendiente.

## 14. Análisis de brecha — qué existe ya vs. qué exige el rediseño

| Pieza del rediseño | Estado actual | Trabajo |
|---|---|---|
| Ingesta real de ambos canales (webhooks, firma, cola durable, idempotencia, tiempo real) | ✅ Completa (§ 3) | Reutilizar tal cual |
| Enrutamiento híbrido + triage + `ContactoWhatsApp` | ✅ Completo (§ 4) | Reutilizar como fallback del Matching |
| Ventana 24 h, estados de entrega, bloqueo servidor+UI | ✅ Completo | Reutilizar; el composer debe reflejarlo al elegir canal WhatsApp/Mixto |
| Estados del hilo, asignación de gestor, macros por cliente | ✅ Completo | Mapear "Esperando cliente"; variables dinámicas nuevas |
| Detección IA de solicitud de visita (`SugerenciaVisitaCorreo`) | ✅ Backend completo, UX = banner + prellenado del drawer | Evolucionar al flujo guiado 03.4 con confianza por campo y pregunta de centro |
| Workspace Selector (Delegated Workspace) | ✅ Implementado (ADR-004) | Integrarlo en el toolbar del módulo |
| **Canal por mensaje** | ✅ `Mensaje.Canal` (paso 1, PR #129) | Hilos mixtos de verdad siguen pendientes del Matching Engine (paso 4, § 13.2) — hoy cada conversación sigue siendo de un solo canal |
| **Conversation Matching Engine** | ⚠️ Motor + Command + UI implementados (§ 13.2), pero solo 2 de 7 criterios calculan con datos reales (Mismo Cliente, Ventana temporal) | Cerrar Mismo Trabajador/Centro requiere poblar `ParticipanteConversacion` en WhatsApp primero (fila de abajo); Similaridad semántica y Mismo Documento requieren decisiones de IA/extracción fuera de alcance de esta pieza |
| **UI unificada** (un workspace; hoy `/comunicaciones` y `/comunicaciones/chat` separados) | ✅ Fusionada (paso 2) — `/comunicaciones/chat` eliminada, `Bandeja.razor` es el único workspace | `UnifiedTimeline` con badge de canal por mensaje; pendiente: Adaptive Layout con Drawers (fila propia de esta tabla) |
| **Composer multicanal** (Responder como, canal Email/WhatsApp/Mixto, firma, variables, programar envío, prioridad, borradores, nota interna, menciones) | ⚠️ `ComposerBar` único con fallback de canal (§ 16.5) implementado — sigue sin selector Email/WhatsApp/Mixto manual (solo automático), sin firma/variables/programar envío/prioridad/borradores/notas internas/menciones | Ampliar `ComposerBar` con esas piezas cuando haga falta de verdad (YAGNI) |
| **Action Center generalizado** | ⚠️ Solo la acción "Crear visita" como banner | Catálogo de acciones tipadas con confianza (§ 12.6) |
| **Actualización documental desde conversación** (03.5) | ❌ No existe (la extracción IA existe solo en subida manual/masiva de `/documentos`) | Conectar adjuntos de conversación → pipeline de extracción → propuesta de actualización con reglas |
| Eventos del sistema en el timeline (visita creada, documento actualizado) | ✅ Implementado (PRs #132/#133) — `EventoConversacion`, tipo `ConversacionVinculada` añadido al confirmar el Matching Engine | Resto de módulos (§ 16.7 los deja para después de Visitas+Documentos) |
| `ParticipanteConversacion` en WhatsApp | ❌ Solo correo | Poblarlo también desde la ingesta WhatsApp (el mockup muestra participantes multicanal) |
| Búsqueda de conversaciones (H2 del audit) | ❌ No existe | Requisito del toolbar (Ctrl+K) |
| Clasificación IA de cada conversación (Consulta/Solicitud/…, idioma, confianza promedio) | ❌ No existe | Nuevo servicio de clasificación en la ingesta |
| Adaptive Layout con Drawers por breakpoint | ❌ Páginas fijas | Aplicar el patrón Adaptive ya adoptado por el portal |
| Deuda nominal `*Correo` | ✅ Resuelta (paso 0, PR #128 — `Conversacion`/`Mensaje`) | — |

## 15. Estrategia de implementación acordada en la sesión de diseño

- **No pedir "haz el módulo Comunicaciones"**: cada pieza tiene su blueprint (mockup de alta resolución + especificación implementable — medidas, grid, tipografía, tokens, ComponentTree, InteractionRules, BlazorMapping). Claude implementa, no interpreta.
- Antes de seguir generando pantallas de este módulo, la sesión concluyó que conviene consolidar el **Hydra Design Language** — los ~7 patrones reutilizables (Workspace, Data Table, Action Center, Dashboard, Document Review, Timeline, Drawer, Modal) — porque Comunicaciones/Clientes/Documentos pasan a ser *composiciones* de esos patrones. Con lo ya diseñado (~los 10 mockups) hay suficiente para empezar la implementación de este módulo.
- Orden de valor dentro del módulo: Conversation Workspace (timeline unificado) → Composer → Context/Action Center → Inbox → Estados.

## 16. Decisiones cerradas (con el usuario, 2026-08-07)

Las siete preguntas abiertas del rediseño quedaron decididas:

1. **Alcance: rediseño completo, de una vez.** Incluye Matching Engine, hilos mixtos (canal por mensaje + migración, § 13.1-13.2), clasificación IA de conversaciones y eventos del sistema — no una v1 acotada.
2. **Renombrado de dominio: refactor previo separado.** Un PR mecánico (`ConversacionCorreo`→`Conversacion`, `MensajeCorreo`→`Mensaje`, tablas y migración incluidas) **antes** de empezar el rediseño — cumple la regla de CLAUDE.md de no mezclar refactors y el rediseño ya nace hablando de "Conversación".
3. **Automatización: siempre confirmar.** La IA nunca ejecuta sin confirmación del gestor — se mantiene la disciplina "sugerencia, nunca automática" del proyecto. "Actualizar automáticamente"/"Responder automáticamente" en la UI significan "con todo prellenado, a un clic", no "sin supervisión".
4. **"Esperando cliente": estado derivado, no persistido.** Conversación `Abierta` cuyo último mensaje es saliente. Sin migración ni estado manual que se desincronice.
5. **Plantillas aprobadas de Meta (HSM): no entran. Regla de fallback de canal:** el envío por WhatsApp solo existe dentro de la ventana de 24 h; si el gestor intenta contestar una conversación WhatsApp con más de 24 h desde el último entrante, **la conversación migra a correo electrónico** — la respuesta sale por email en el mismo hilo (posible gracias a los hilos mixtos de § 13.1), y el composer debe forzar/preseleccionar el canal Email en ese caso. Esto sustituye el bloqueo seco actual ("no puedes responder") por una salida operativa.
6. **Multi-gestor por cliente / delegación por vacaciones: sigue en backlog.** El enrutamiento híbrido actual (§ 4) cubre el rediseño; el cambio de modelo de cartera afecta a más módulos y merece fase propia.
7. **Eventos del sistema en el timeline: Visitas + Documentos en v1.** Los dos módulos con acciones del Action Center detrás; cada acción confirmada desde una conversación deja su evento en el hilo. El resto de módulos se suma después.

**Orden de ejecución que se deriva**: (0) refactor de renombrado → (1) cambio de dominio canal-por-mensaje + migración → (2) workspace unificado (fusión Bandeja+Chat, UnifiedTimeline, ComposerBar con fallback de canal) → (3) Action Center con Crear visita y Actualizar documentación + eventos al hilo → (4) Matching Engine y clasificación IA.

**Estado a 2026-08-08**: (0)-(2) completos. (3): eventos del hilo completos (Visita creada, Documento actualizado); el Action Center como patrón de UI genérico (tarjetas con confianza + Confirmar/Editar/Descartar, reutilizable fuera de Comunicaciones) sigue sin construir — hoy cada sugerencia (visita, gestión, vinculación) tiene su propia tarjeta ad-hoc en `UnifiedTimeline`, no un componente compartido. (4): Matching Engine implementado con alcance honesto (ver § 13.2) — el score funciona y la vinculación fusiona hilos de verdad, pero solo 2 de 7 criterios de la tabla de pesos tienen datos reales hoy; clasificación IA de conversaciones (Consulta/Solicitud/…, idioma, confianza promedio) no se abordó, sigue pendiente.
