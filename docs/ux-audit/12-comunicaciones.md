# Sesión 12 — Comunicaciones (bandeja, buzón M365, chat WhatsApp, macros)

> Auditado el 2026-08-05 en ejecución con el flag `Comunicaciones:Activo` encendido en desarrollo y datos demo (`ComunicacionesDatosPruebaSeeder`). **En producción el módulo está apagado a propósito** por falta de ingesta real (`NavMenu.razor:13-16`, P2 #26 de `MATURITY_REVIEW.md`) — se audita como pre-lanzamiento, sin penalizar la decisión de flag (decisión registrada). Archivos: `Features/Comunicaciones/Pages/Bandeja.razor`, `Buzon.razor`, `Chat.razor`, `Macros.razor`, `Components/FilaConversacion.razor`.

## Puntuaciones (pre-lanzamiento)

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | La bandeja agrupa por Cliente con "Sin cliente asignado (Triage)" primero y contadores por grupo — el modelo mental correcto para un buzón CAE compartido. |
| UI | 6 | Filas de conversación con estado, antigüedad y preview; consistente con el DS. |
| Usabilidad | 6 | Filtros Estado/Mes/Cliente + "Asignado a mí"/"Sin asignar" cubren la operación diaria; sin búsqueda de texto en conversaciones. |
| Consistencia | 7 | Estados vacíos ejemplares en Chat y Buzón que explican la causa y el siguiente paso ("Conecta un buzón en Conexiones de integración…"). |
| Escalabilidad | 5 | La bandeja agrupada sin paginación visible correrá la suerte de la Bandeja del gestor (sesión 10 H1) con volumen real de correo. |
| Madurez | 6 | Triage, asignación de gestor, macros genéricas y por cliente, sugerencias IA de gestión/visita desde correo — diseño completo; sin ingesta real aún no está probado en batalla. |
| Competitividad | 6 | Un buzón compartido que entiende "cliente CAE" y sugiere gestiones/visitas desde el correo es diferencial frente a Outlook a pelo; el valor depende por completo de encender la ingesta. |

## Hallazgos priorizados

### H1 — El módulo que resuelve la reclamación documental (hueco 4.1.2) está apagado y es solo reactivo `[OBSERVADO]`
Las macros semilla ("Solicitud de documentación pendiente", "Aviso de vencimiento próximo") y `EnviarMensajeNuevoCommand` existen; pero no hay ningún disparo desde los módulos de operación — desde un documento vencido o la Bandeja no se puede "enviar reclamación con esta macro". El único puente operación→correo es "Pedir prioridad" del Centro (Fase G). La reclamación proactiva sigue viviendo fuera de Hydra.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto | M | Alto — es el hueco 4.1.2 del inventario | Medio plazo |

### H2 — Sin búsqueda de texto en conversaciones `[OBSERVADO]`
La bandeja filtra por estado/mes/cliente/asignación pero no busca ("el correo donde nos mandaron el ITA de marzo") — en correo, la búsqueda es el 80% de la recuperación.
| Medio | Medio | M | Medio | Medio plazo |

### H3 — Doble bandeja Comunicaciones vs Bandeja del gestor `[HIPÓTESIS]`
Conviven `/bandeja` (cola documental) y `/comunicaciones` (cola de correo) como dos "bandejas" distintas en el menú; hipótesis de confusión de vocabulario para el usuario nuevo — requiere validación con usuarios cuando el módulo se encienda.
| Medio | Bajo | — (naming) | Bajo | Quick Win (renombrar) |

### Positivo verificado
- **Triage primero**: "Sin cliente asignado (Triage)" como primer grupo con contador — lo no clasificado no se pierde.
- **Macros por cliente además de genéricas** — el matiz que un helpdesk genérico no tiene.
- **Buzón completo separado de la bandeja gestionada**, con explicación de por qué existen ambos (`Buzon.razor` copy) — evita el clásico "¿dónde están mis Enviados?".
- Estados vacíos con causa y acción en Chat/Buzón.

## Riesgos futuros
- Cuando se encienda la ingesta, el volumen convertirá H2 y la falta de paginación en bloqueantes el primer mes.
- Pro-Inbound: la conversación ligada a Cliente/Empresa es exactamente el canal que el modelo Inbound necesitará con contratas — el diseño actual no lo bloquea.

## Propuestas

1. **"Enviar reclamación" desde la operación** (H1): acción en documento vencido/faltante y en la Bandeja que abra el compositor con la macro sugerida y el destinatario de la Empresa — cierre del hueco 4.1.2 reutilizando piezas existentes (macros + `EnviarMensajeNuevoCommand` + patrón "Pedir prioridad"). — M, Medio plazo, la de más valor.
2. **Búsqueda de texto en conversaciones** (H2). — M, Medio plazo.
3. **Revisar el naming "Bandeja" vs "Comunicaciones"** antes del lanzamiento (H3). — S, Quick Win.

## Referencias de principios
- **Front/Intercom como principio**: el correo compartido vale cuando la entidad de negocio (cliente) es la unidad de agrupación — Hydra ya lo hace; y cuando cualquier objeto del sistema puede iniciar un mensaje con plantilla — eso es H1.
