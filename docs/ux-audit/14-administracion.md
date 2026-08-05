# Sesión 14 — Administración (Usuarios, Roles, Configuración, Tipos de Documento, Auditoría, Auditoría IA)

> Auditado el 2026-08-05. Usuarios verificado en ejecución (como Administrador del tenant principal); el resto sobre código — el panel del navegador quedó congelado a mitad de sesión (los hallazgos de código citan archivo:línea; lo no ejercitado en runtime queda como `[OBSERVADO]` en código o `[INFERIDO]` donde aplique). Archivos: `Features/Usuarios/Pages/Usuarios.razor(.cs)`, `GestionRoles/Pages/Roles.razor(.cs)`, `Configuracion/Pages/Configuracion.razor(.cs)`, `TiposDocumento/Pages/TiposDocumento.razor(.cs)`, `Auditoria/Pages/Auditoria.razor(.cs)`, `AuditoriaIa/Pages/AuditoriaIa.razor(.cs)`.

## Puntuaciones

| Eje | Nota | Justificación |
|---|---|---|
| UX | 6 | El ciclo de identidad está bien cerrado (alta de usuario → "Pendientes de asignar" con badge en Roles → `PendienteDeRol`), y Tipos de Documento hereda el modelo de dos niveles global/cliente. |
| UI | 6 | `[OBSERVADO]` en Usuarios: coherente con el DS y con paginador **en español** — que delata que el resto de listas usan otro paginador (QuickGrid, en inglés): dos implementaciones distintas del mismo control. |
| Usabilidad | 6 | Filtros razonables (Auditoría por tipo de entidad, Auditoría IA por proveedor incl. caché); llegar sin permiso a cualquiera de estas páginas te expulsa al login (sesión 13 H1). |
| Consistencia | 5 | La promesa de recuperación "desde Auditoría" que hace el borrado en lote de Empresas es falsa (H1) — el copy y la capacidad real divergen. |
| Escalabilidad | 6 | Auditoría con filtros y paginación; sin export del log (el único registro probatorio del sistema). |
| Madurez | 7 | Un MVP con auditoría de entidades + auditoría IA con proveedor/coste/caché y umbrales de semáforo configurables por tenant está por encima de su clase. |
| Competitividad | 6 | La Auditoría IA (coste por proveedor, caché) es un diferencial de transparencia que pocos SaaS del sector enseñan. |

## Hallazgos priorizados

### H1 — "Podrás recuperarlas desde Auditoría" es una promesa falsa `[OBSERVADO]`
El borrado en lote de Empresas promete recuperación desde Auditoría (`Empresas.razor:181`), pero `/auditoria` no tiene ninguna acción Restaurar (grep `Restaurar|Deshacer` en `Features/Auditoria` → 0 resultados); los `Restaurar*Command` solo están cableados al toast de deshacer (8 segundos de vida). Pasados esos 8s, la única recuperación real es tocar la base de datos.
| Impacto usuario | Impacto negocio | Esfuerzo | Riesgo | Horizonte |
|---|---|---|---|---|
| Alto — pérdida percibida de datos | Alto — confianza | M (acción Restaurar en Auditoría; los Commands existen) | Alto | Quick Win/Medio |

### H2 — Sin export del log de Auditoría `[OBSERVADO]`
El único registro probatorio (quién borró qué y cuándo) no sale de la pantalla — en un producto RGPD-sensible el log exportable es requisito de cliente enterprise.
| Medio | Medio | S | Medio | Quick Win |

### H3 — Dos paginadores, dos idiomas `[OBSERVADO]`
Usuarios pagina en español con control propio (runtime: "Página 1 de 1 — 1 usuario(s)"); las 12 listas QuickGrid en inglés ("Page 1 of 1"). Consolidar en un componente único resuelve además la sesión 02 H2.
| Medio | Bajo | S | Bajo | Quick Win |

### Positivo verificado
- **Roles → "Pendientes de asignar" con badge contador** (`Roles.razor:12-18`): el usuario nuevo sin rol no se pierde en el limbo — se le ve.
- **Auditoría IA con filtro por proveedor (anthropic/gemini/mistral-ocr/caché)** (`AuditoriaIa.razor:14-19`) — transparencia de costes IA de serie.
- **Umbrales del semáforo configurables por tenant** con referencia honesta a su origen ("hoja Parametros del Excel original", `Configuracion.razor:24-38`).
- **Tipos de Documento con filtro por cliente** y toggles IA de dos niveles, coherente con `/clientes/{id}/lectura-ia` (sesión 02).
- Aislamiento por tenant verificado en runtime: el directorio de Usuarios del tenant principal no muestra los usuarios del tenant demo (`DirectorioUsuariosTenant`).

## Riesgos futuros
- H1 es una bomba de confianza: el primer cliente que pierda 30 empresas por un borrado en lote y descubra que Auditoría no restaura, no se queda.
- Pro-Inbound: la gestión de roles por tenant está lista para un rol de contratista externo; el punto de vigilancia sigue siendo el catálogo de roles fijo (`Roles.Todos`) — añadir un rol nuevo es despliegue, no configuración (aceptable hoy, anotado para el consolidado).

## Propuestas
1. **Acción "Restaurar" en Auditoría** para entidades soft-deleted (H1) — o corregir el copy del borrado en lote mientras tanto (S inmediato). — M, Quick Win/Medio plazo.
2. **Export del log de Auditoría** (H2) con los filtros aplicados. — S, Quick Win.
3. **Paginador único localizado** (H3). — S, Quick Win.

## Referencias de principios
- **GitHub**: "Danger zone" con recuperación real documentada — nunca prometer un camino de restauración que no existe (H1).
- **Stripe**: logs exportables y filtrables como feature de confianza enterprise (H2).
