# Sesión 03 — Empresas + Subcontratas (incl. detección de personal y credenciales de portal)

> Auditado el 2026-08-05 en ejecución (24 Empresas, 8 Subcontratas de demo; usuario `prueba.direccioncae1`, capturas propias). Archivos: `Features/Empresas/Pages/Empresas.razor(.cs)`, `DeteccionTrabajadores.razor(.cs)`, `Components/EmpresaWorkspacePanel.razor`, `FormularioRapidoEmpresa`, `Features/Subcontratas/Pages/Subcontratas.razor(.cs)`, `Components/SubcontrataWorkspacePanel.razor`, `Domain/Empresas/CredencialAccesoEmpresa.cs`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | Empresas es la mejor lista del producto (estado documental, nombre clicable, alta encadenada), pero el flujo de detección de personal — diferencial real — es huérfano: solo se llega desde una notificación transitoria. |
| UI | 6 | DS aplicado; lo estropean el header crudo "RazonSocial" en Subcontratas y la columna CIF vacía sin placeholder en ambas. |
| Usabilidad | 6 | Filtro de estado peor→mejor y búsqueda al vuelo; sin export, sin lote en detección, credenciales solo dentro de Editar. |
| Consistencia | 5 | Tres listas hermanas (Clientes/Empresas/Subcontratas), tres patrones distintos: columnas, export, click en nombre y copy de recuperación divergen entre sí. |
| Escalabilidad | 5 | Paginación servidor ✓, pero la detección procesa fila a fila (un ITA con 50 altas = 100 clics) y la selección múltiple sigue confinada a la página. |
| Madurez | 6 | Human-in-the-loop explícito en detección ("nada se hace automáticamente") y derivación inteligente Empresa→Centros por actividad real; el modelo de credenciales se quedó corto. |
| Competitividad | 5 | Para el job Outbound la Empresa es el eje (es quien opera en los portales de las titulares) y Hydra solo le da un slot de credenciales sin concepto de plataforma — el Excel del gestor va por delante aquí. |

## Hallazgos priorizados

### H1 — Una única credencial de portal por Empresa, sin vínculo con la plataforma que la exige `[OBSERVADO]`
`CredencialAccesoEmpresa` es 1:1 con la Empresa (`Domain/Empresas/CredencialAccesoEmpresa.cs:22-35`; query singular `ObtenerCredencialAccesoEmpresa`) y su UI es un bloque URL/usuario/contraseña/notas dentro del drawer de Editar (`Empresas.razor:114-148`). Matiz descubierto en la sesión 04: el modelo Outbound principal vive en el **Centro** (`CanalGestionDocumental`, plataforma o correo por centro — ver sesión 04 H2), así que este slot de Empresa es un segundo sistema de credenciales paralelo, único por empresa y sin relación con aquellos canales. Una contrata que opera en N plataformas de M titulares solo puede guardar una aquí, y nada conecta ambas mundos. Además la credencial no se ve en el panel Ver (`EmpresaWorkspacePanel.razor:40-47`): el punto de uso real exige abrir Editar.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto | Alto — es el corazón del job Outbound | M (modelo + UI) | Alto — el Excel sigue vivo | Medio plazo |

### H2 — La detección de personal es inalcanzable desde la navegación `[OBSERVADO]`
`/empresas/{id}/deteccion-trabajadores` no tiene ningún enlace en la UI: la única referencia en todo el código es la notificación de campana (`Application/Trabajadores/Deteccion/DeteccionTrabajadoresService.cs:145`). Si la notificación se pierde o se descarta, las detecciones pendientes quedan invisibles — ni columna/badge en la lista de Empresas, ni pestaña en el panel, ni tarjeta en la Bandeja (los tipos de la Bandeja no la incluyen, `UX_PATTERNS.md:65`).
| Alto | Alto — el diferencial IA queda enterrado | S/M | Alto | Quick Win |

### H3 — Detección sin acciones en lote `[OBSERVADO]`
`DeteccionTrabajadores.razor:61-77` resuelve fila a fila (Dar de alta/Descartar por trabajador); un RNT/TC2 de 50 personas son 50 decisiones idénticas sin "seleccionar y aplicar" ni "dar de alta todos".
| Medio | Medio | M | Medio | Medio plazo |

### H4 — Subcontratas, la lista abandonada `[OBSERVADO]`
Header crudo "RazonSocial" (`Subcontratas.razor:56`, `PropertyColumn` sin `Title` — visible en captura), sin estado documental derivado (Empresas sí lo tiene), sin export, nombre no clicable, y sin columna que diga con qué Empresa/Cliente se relaciona.
| Medio | Medio | S/M | Medio | Quick Win |

### H5 — Columna CIF vacía sin placeholder `[OBSERVADO]`
33 de 34 Empresas demo sin CIF muestran celda vacía (captura) en vez del "—" que ya usa el Dashboard (`Dashboard.razor:61`); una columna entera en blanco parece un bug aunque sea dato ausente.
| Bajo | Bajo | S | Bajo | Quick Win |

### H6 — Dos promesas de recuperación distintas para el mismo soft delete `[OBSERVADO]`
Eliminar individual: "Podrás deshacerlo desde el aviso que aparecerá" (`Empresas.razor:170`); eliminar lote: "Podrás recuperarlas desde Auditoría" (`:181`). Mismo destino (soft delete), dos historias — y la segunda exige rol con acceso a `/auditoria`.
| Bajo | Bajo | S | Bajo | Quick Win |

### H7 — Export ausente en Empresas y Subcontratas `[OBSERVADO]`
Clientes exporta (`Clientes.razor:23`); sus dos hermanas no — el gestor que quiere la foto de contratas en Excel (para un titular, para una reunión) vuelve a copiar a mano.
| Medio | Medio | S | Medio | Quick Win |

## Riesgos futuros
- **Outbound**: sin H1 resuelto, cualquier módulo futuro de "pendiente por plataforma" (sesión 01-H2, hueco 4.1.3) no tendrá dónde anclar la relación Empresa×Plataforma — H1 es su prerequisito de modelo.
- **Pro-Inbound (§ 4.3)**: la derivación "Centros con actividad real" (`EmpresaWorkspacePanel.razor:5-9`) es exactamente la semántica que un portal de contratista necesitará; conservarla como query reutilizable.

## Propuestas

1. **Credenciales por plataforma destino** (H1): N credenciales por Empresa con nombre de plataforma, URL y notas; visibles (con `BotonCopiar`) en la pestaña Información del panel — no dentro de Editar. Prerequisito del roadmap Outbound. — M, Medio plazo, la de más valor.
2. **Badge "detecciones pendientes" en la lista de Empresas + tipo nuevo en la Bandeja** (H2): el contador existe en la query de detecciones; enlazarlo. — S/M, Quick Win.
3. **Lote en detección** (H3): checkboxes + "Dar de alta seleccionados"/"Descartar seleccionados", con el mismo `BarraAccionesLote` del DS. — M, Medio plazo.
4. **Nivelar Subcontratas** (H4): `Title="Razón social"`, estado documental derivado, nombre clicable al Workspace y columna de relación. — S/M, Quick Win.
5. **Placeholder "—" para valores ausentes en todas las tablas** (H5), regla de DS. — S, Quick Win.
6. **Unificar el copy de recuperación** (H6) con la promesa del deshacer. — S, Quick Win.
7. **Export en Empresas y Subcontratas** (H7), mismo endpoint pattern que Clientes. — S, Quick Win.

## Referencias de principios
- **1Password/gestores de credenciales**: la credencial se consume donde se usa (copiar desde la vista, no desde el formulario de edición) — H1.
- **Linear**: "inbox zero" con fuente persistente — una cola de trabajo (detecciones) nunca vive solo en una notificación efímera; siempre hay una vista que la lista (H2).
- **Dokify/Nalanda como principio**: el alta masiva desde documento (ITA/RNT) es su gancho de eficiencia; el principio — una decisión repetida N veces se decide una vez sobre N filas — es H3.
