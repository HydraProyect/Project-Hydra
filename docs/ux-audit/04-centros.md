# Sesión 04 — Centros

> Auditado el 2026-08-05 en ejecución (48 Centros demo; usuario `prueba.direccioncae1`, capturas propias). Archivos: `Features/Centros/Pages/Centros.razor(.cs)`, `Components/CentroWorkspacePanel.razor` (867 líneas — el panel más rico del producto), `Application/Centros/Queries/ObtenerCentros/ObtenerCentrosQuery.cs`, `Domain/Centros/CanalGestionDocumental.cs`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 4 | El panel de Centro es el mejor diseño del producto (canal de gestión, requisitos, pedir prioridad), pero **la página no carga en su ruta por defecto** — todo lo demás queda detrás de un error. |
| UI | 6 | Columnas ricas (Cliente, Empresa, Código, Cumplimiento) y estado de error correcto con Reintentar; sin export y paginador sin localizar como el resto. |
| Usabilidad | 3 | Un usuario que entra a `/centros` ve "No pudimos cargar los centros" siempre; solo un usuario que ya sepa filtrar por estado revive la tabla — anclaje de rúbrica: el usuario nuevo se pierde, el frecuente sufre a diario. |
| Consistencia | 6 | Nombre clicable y filtro peor→mejor como Empresas ✓; el patrón "Duplicar" que `UX_PATTERNS.md:36-37` promete para Centro/Requisito no existe en ningún punto del código (`grep Duplicar` → 0 resultados). |
| Escalabilidad | 5 | La ruta SQL pagina/ordena en servidor (bien diseñada), pero está rota; la ruta que sí funciona (filtro por estado) materializa **todos** los centros en memoria (`ObtenerCentrosQuery.cs:104`). |
| Madurez | 4 | Una regresión mergeada a main dejó la página muerta sin que ningún test la detectara — el error queda bien logueado (`LoggingBehavior`), pero nadie lo estaba mirando. |
| Competitividad | 5 | El modelo de canal de gestión por centro (plataforma/correo) es exactamente el dato Outbound correcto — ningún Excel lo estructura así —, pero hoy ni siquiera se puede llegar a él por la vía normal. |

## Hallazgos priorizados

### H1 — `/centros` no carga: regresión EF en la consulta de lista `[OBSERVADO]` (bug confirmado, reproducible)
`ObtenerCentrosQuery` proyecta con constructor (`Select(x => new FilaCentro(...))`, `ObtenerCentrosQuery.cs:64-70`) y luego ordena/pagina sobre esa proyección (`:89-93`); EF Core no lo traduce (`InvalidOperationException: could not be translated`, verificado en log del servidor 2026-08-05) y la página muestra siempre "No pudimos cargar los centros". Introducido por `eb02739` ("ordenación real por columna… en las 12 listas"), **ya en origin/main**. La ruta con filtro/orden por Estado (`ResolverConEstadoAsync`, `:104`) funciona porque materializa en memoria — verificado filtrando por "Vencido".
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto — página muerta | Alto — y en producción | S | Alto | Quick Win (ya derivado a tarea propia) |

### H2 — La plataforma destino es texto libre por Centro, no un catálogo `[OBSERVADO]`
`CanalGestionDocumental` ancla el canal en el sitio correcto (el Centro) con tipo Plataforma/Email, pero `NombrePlataforma` es un string libre (`CanalGestionDocumental.cs:43,116-127`). Consecuencia Outbound (regla 4): imposible responder "¿qué tengo pendiente en Dokify?" o "¿qué centros gestiona Twind?" sin normalizar strings — el dato existe pero no es agregable ni navegable, y es el prerequisito del hueco 4.1.3 (registro del estado por plataforma) junto con el H1 de la sesión 03.
| Alto | Alto | M (catálogo + migración de textos) | Alto | Medio plazo |

### H3 — Credenciales del canal invisibles en el punto de uso `[OBSERVADO]`
La pestaña "Plataforma" del panel muestra solo un badge "Configuradas/Sin configurar" y remite al formulario de edición (`CentroWorkspacePanel.razor:260-266`); el momento real de uso ("voy a entrar al portal a subir esto") exige abrir Editar. No enseñar el valor es correcto; no ofrecer **copiar** (sin mostrar) o abrir la URL con el usuario a mano, no.
| Medio | Medio | S | Medio | Quick Win |

### H4 — "Duplicar" documentado pero inexistente `[OBSERVADO]`
`UX_PATTERNS.md:36-37` define Duplicar para Centro y RequisitoDocumental ("mucha repetición estructural"); no hay ninguna implementación en `src/CaeManager.Web` (grep sin resultados). Dar de alta 10 centros gemelos de un mismo cliente se hace a mano campo a campo.
| Medio | Medio | S/M | Medio | Quick Win |

### H5 — Sin export en Centros `[OBSERVADO]`
Mismo hueco que Empresas/Subcontratas (sesión 03 H7): la foto de centros con su cumplimiento — el informe que una titular pide — no sale de Hydra.
| Medio | Medio | S | Medio | Quick Win |

### Positivo verificado
- **Canal de gestión documental por Centro** (plataforma o correo) — la pieza de modelo Outbound más valiosa del producto; con nota contextual honesta sobre las credenciales.
- **Estado de cumplimiento rico** (Bloqueado / Falta documentación / Vencido / Urgente / Próximo / Vigente) ordenado de peor a mejor en el filtro ✓ (`UX_PATTERNS.md:45`).
- **"Pedir prioridad de validación"** desde la cabecera del panel (Fase G) con borrador editable — exactamente la asistencia Outbound correcta.
- Requisitos documentales con CRUD completo en el propio panel y estados vacíos específicos por pestaña.

## Riesgos futuros
- H2 se agrava con cada centro nuevo: cuantos más strings libres de plataforma existan, más cara la migración a catálogo. Migrar pronto es estrictamente más barato.
- La ruta en memoria (`ResolverConEstadoAsync`) será el siguiente cuello de botella cuando H1 se arregle y los tenants crezcan — mismo patrón que sesión 01 H3.

## Propuestas

1. **Arreglar la query de lista** (H1) — ya derivado a tarea independiente (ordenar antes de proyectar, test de traducción); desbloquea todo lo demás. — S, Quick Win.
2. **Catálogo de Plataformas** (H2): entidad/catálogo por tenant (nombre normalizado + URL base), `NombrePlataforma` pasa a referencia; los strings existentes se migran con sugerencia de matching. Prerequisito del "pendiente por plataforma" (sesión 01 propuesta 2). — M, Medio plazo, la de más valor.
3. **Copiar usuario/contraseña desde la pestaña Plataforma** sin mostrar el valor (H3), con el `BotonCopiar` existente + auditoría del acceso a la credencial. — S, Quick Win.
4. **Implementar Duplicar centro/requisito** (H4) según su propio patrón documentado. — S/M, Quick Win.
5. **Export de Centros con cumplimiento** (H5). — S, Quick Win.
6. **Vista "por plataforma"**: agrupar los centros por su canal (aunque sea por string hoy) como filtro rápido — puente barato hasta el catálogo. — S, Quick Win.

## Referencias de principios
- **Linear**: los valores estructurales (labels/projects) son entidades con catálogo, nunca texto libre — lo que hace posible agrupar, filtrar y automatizar (H2).
- **1Password**: copiar sin revelar es el default de credenciales en punto de uso (H3).
- **Stripe**: "test in prod parity" — una página cuyo camino feliz no está cubierto por un smoke test termina rota en producción sin que nadie lo vea (H1); el principio es el smoke E2E por página, no el fix puntual.
