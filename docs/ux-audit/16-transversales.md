# Sesión 16 — Transversales (navegación, Context Workspace, búsqueda global, asistente IA, atajos, cuenta)

> Auditado el 2026-08-05 combinando runtime (login+2FA, navegación por rol, Delegated Workspace, panel de Cliente/Centro abiertos, Forbidden, paginadores, logs del servidor) y código. Archivos: `Components/Layout/*`, `Components/Workspace/ContextWorkspace.razor`, `Features/BusquedaGlobal/*`, `Features/AsistenteIa/*`, `Features/AtajosGlobales/*`, `Features/Notificaciones/NotificacionesPopup.razor`, `Components/Account/Pages/*`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | Context Workspace + buscador global + atajos en 3 capas forman una columna vertebral de navegación mejor que la media del sector; sus huecos conocidos (deep-link, cierre, teclado) siguen abiertos. |
| UI | 6 | DS 3.2 aplicado con disciplina real en 30 componentes; las grietas visibles son puntuales (calendario blanco, leyenda ApexCharts, paginador EN). |
| Usabilidad | 6 | Ctrl+K con comandos y 5 entidades, `g+letra`, `?` con chuleta; pero no busca Documentos y el usuario sin permiso acaba en el login. |
| Consistencia | 6 | El sistema de patrones existe y se sigue en su mayoría — los desvíos detectados módulo a módulo son la distancia entre el patrón escrito y las 12 implementaciones. |
| Escalabilidad | 6 | La arquitectura de navegación aguanta; los límites están en las listas (sesiones 02-10), no aquí. |
| Madurez | 5 | Forbidden que expulsa al login, login sin recuperación de contraseña y ruido de excepciones en cada navegación son bordes sin pulir en el esqueleto que todo lo demás pisa. |
| Competitividad | 6 | Un SaaS CAE con palette, atajos y panel contextual ya se siente más moderno que el sector; los bordes de identidad/errores lo delatan como joven. |

## Hallazgos priorizados

### H1 — Usuario autenticado sin permiso → pantalla de login `[OBSERVADO]` (transversal)
Verificado en runtime (sesión 13 H1): `/importacion` como DireccionCae redirige a "Iniciar sesión" con la sesión viva. Afecta a toda página con `[Authorize(Roles=…)]`. Viola `UX_PATTERNS.md:98` (estado Forbidden obligatorio) y confunde ("¿me ha caducado la sesión?").
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Medio | Medio | S (AccessDeniedPath + página) | Medio | Quick Win |

### H2 — El login no tiene recuperación de contraseña `[OBSERVADO]`
La pantalla de login renderiza exactamente: email, contraseña y "Iniciar sesión" (árbol de accesibilidad en runtime) — sin "¿Olvidaste tu contraseña?". Todo olvido pasa por un Administrador (y el del Administrador, por soporte de plataforma). Sin decisión registrada.
| Alto (cuando pase) | Medio | M (flujo email) | Medio | Medio plazo |

### H3 — El buscador global no busca Documentos `[OBSERVADO]`
Resultados: Clientes, Empresas, Subcontratas, Centros, Trabajadores (`BuscadorGlobal.razor.cs:97`); Documentos solo existe como comando "Ir a Documentos" (`:52`). "El apto de Juan de marzo" — la búsqueda más frecuente de un gestor documental — no se resuelve desde Ctrl+K.
| Medio | Medio | M | Medio | Medio plazo |

### H4 — Context Workspace: huecos conocidos abiertos + pestañas desbordadas `[OBSERVADO]`
Los huecos que el propio `PLAN-CONTEXT-WORKSPACE.md` § 0 deja abiertos (deep-link `?ctx=`, cierre al navegar por el menú, navegación por teclado, modo acoplado) siguen sin cerrar; y en runtime las pestañas del panel desbordan con scrollbar horizontal (sesión 02 H6) y no hay acción Editar desde el panel. El deep-link ausente además bloquea los cross-links por Id (sesión 01 H7).
| Medio | Medio | M | Medio | Medio plazo |

### H5 — Ruido de excepciones en cada navegación `[OBSERVADO]`
Cada cambio de página dispara `JSDisconnectedException` sin capturar en 3 Dispose (`AtajosListaTeclado.razor:29`, `BuscadorGlobal.razor.cs:229`, `AtajosGlobales.razor.cs:53`) más un `AggregateException` de circuito — verificado en logs del servidor. Entierra errores reales bajo ruido conocido.
| Bajo (usuario) / Alto (operación) | Medio | S | Medio | Quick Win |

### H6 — Error de CSP en consola `[OBSERVADO]` (causa por atribuir)
La consola del navegador registró "Executing inline script violates CSP 'script-src self'" durante la sesión — algo intenta ejecutar script inline que la CSP (correctamente estricta) bloquea. Identificar el emisor (¿librería de gráficos?, ¿algún componente?) antes de que un feature silenciosamente no funcione en producción.
| Bajo | Medio | S (investigar) | Medio | Quick Win |

### Positivo verificado
- **Login + 2FA TOTP** limpios y en español; `PendienteDeRol` como aterrizaje explícito del usuario sin rol.
- **Menú por rol** con grupos colapsables persistidos y menú mínimo deliberado para el rol Cliente (`NavMenu.razor`).
- **Buscador global con comandos** ("Ir a…", crear) además de entidades — un command palette real, no solo un buscador.
- **Asistente IA honesto**: "No tiene acceso a tus Clientes… solo dudas normativas" — scoping explícito en el propio copy, markdown renderizado, accesible (`role="dialog"`, `aria-modal`).
- **Selector de tema** claro/oscuro/sistema que viaja con la cuenta; **ReconnectModal** con estados de reconexión en español.
- Aislamiento multi-tenant observado de facto en toda la sesión (datos, usuarios y selector de Cliente activo).

## Riesgos futuros
- H1+H2 son la primera impresión en cualquier piloto enterprise: accesos denegados confusos y un olvido de contraseña sin salida.
- Pro-Inbound: el buscador y el workspace por entidad son extensibles a nuevas entidades sin rediseño — nada bloqueante.

## Propuestas
1. **Página Forbidden propia** (H1): "No tienes permiso para esta sección" + a quién pedirla — `AccessDeniedPath` + una página. — S, Quick Win.
2. **Recuperación de contraseña por email** (H2) — el SMTP/M365 ya existe en la plataforma. — M, Medio plazo.
3. **Documentos en el buscador global** (H3), agrupados por tipo con su semáforo. — M, Medio plazo.
4. **Cerrar los 4 huecos del Context Workspace** (H4) empezando por el deep-link `?ctx=` (desbloquea cross-links por Id en todo el producto). — M, Medio plazo.
5. **Capturar `JSDisconnectedException` en los 3 Dispose** (H5). — S, Quick Win.
6. **Atribuir y resolver el error CSP** (H6). — S, Quick Win.

## Referencias de principios
- **Linear**: el palette es el sistema nervioso — todo lo que existe se encuentra y se hace desde ahí (H3).
- **Stripe**: los estados de auth (denied, expired, reset) son pantallas de primera clase con siguiente paso — nunca un redirect genérico (H1/H2).
