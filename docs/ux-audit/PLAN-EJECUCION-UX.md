# Plan de ejecución del roadmap UX (post-auditoría) + bloque Acreditación MVP1

> **Tipo**: Operativo — es el plan que consumen las sesiones de implementación de los arreglos
> de la auditoría (`ROADMAP-UX.md`). Decisión de alcance registrada en
> `docs/business/DECISION_LOG.md` (entrada 2026-08-05). Actualizar el estado aquí y en
> `ROADMAP-UX.md` (✅ + nº de PR) al completar cada ítem.

## Contexto (no re-auditar)

La auditoría integral está terminada y mergeada (`docs/ux-audit/`: `00-INVENTARIO.md` con el
marco de alcance § 0 — MVP = CAE Outbound, vara "¿más rápido y fiable que Excel + operar los
portales?" —, fichas `01..16-*.md` con evidencia `archivo:línea`, y `ROADMAP-UX.md`). Ya
resuelto en main — no repetir:

- Bug P0 de `/centros` (`ObtenerCentrosQuery`) arreglado con tests de integración.
- Quick win "Sin cartera asignada" (Dashboard/workspace delegado) ejecutado y mergeado (PR #100).

La ficha de cada hallazgo es la fuente de verdad; si el código contradice a la ficha,
verificar en ejecución antes de decidir.

## Modo de trabajo (innegociable)

1. **Una sesión = un lote coherente**, en rama nueva desde main, con PR propio. **Merge en
   verde antes de abrir el siguiente** — nunca apilar PRs.
2. **Los transversales se arreglan una vez como pieza compartida** y se aplican a todas las
   pantallas en el mismo PR (paginador único, patrón de export, overflow de acciones). No
   parchear pantalla a pantalla.
3. No mezclar refactors independientes en un PR. YAGNI: lo que pide la ficha, nada más.
4. Seguir `UX_PATTERNS.md`, `DESIGN_SYSTEM.md`, `CODING_STANDARDS.md`. Si un arreglo cambia o
   crea un patrón, **actualizar el documento normativo en el mismo PR**.
5. Reglas de repo de siempre (CLAUDE.md): `Version` en Commands de edición, sin SQL crudo ni
   `IgnoreQueryFilters()`, ninguna tabla nueva sin `TenantId`, cargar Ids con el filtro de
   tenant activo.
6. **Verificación antes de cerrar cada ítem**: build + tests (añadir test si el hallazgo era
   una rotura) y verificación end-to-end en navegador con datos de demo.
7. Decisiones de producto o con componente legal: **preguntar antes** (la excepción ya
   decidida es el bloque Acreditación de la Parte 2).

## Entorno de verificación

- `dotnet build` + perfil `hydra-web` (`.claude/launch.json`, puerto 5186).
- Datos de demo: `"DatosPrueba": { "Activo": true }` en
  `src/CaeManager.Web/appsettings.Development.json` (local, fuera de git) y reiniciar.
- Usuarios: `admin@caemanager.local` / `CaeManager#2026` (TOTP dev `JBSWY3DPEHPK3PXP`);
  con datos: `prueba.direccioncae1@caemanager.local` / `Prueba#2026` (tenant demo Dexter).

## Parte 1 — Horizonte 1, quick wins (en orden salvo indicación del propietario)

| # | Ítem | Ficha | Estado |
|---|---|---|---|
| 2 | Restaurar desde Auditoría (los `Restaurar*Command` existen) o, mínimo inmediato, corregir el copy del borrado en lote (`Empresas.razor:181`) | 14-H1 | Pendiente |
| 3 | Página Forbidden propia (`AccessDeniedPath` + pantalla con siguiente paso) | 13-H1/16-H1 | Pendiente |
| 4 | Paginador único localizado (12 listas QuickGrid + Usuarios) | 02-H2/14-H3 | Pendiente |
| 5 | Overflow menu (⋯) en Acciones — densidad de una línea por fila | 05-H2 | Pendiente |
| 6 | Export en Empresas, Centros, Asignaciones, Incidencias y Auditoría + resumen de facturación (patrón `/clientes/exportar.xlsx`) | 03-H7·06-H4·08-H4·11-H2·14-H2 | Pendiente |
| 7 | Detecciones de personal visibles: badge en Empresas + tipo nuevo en Bandeja | 03-H2 | Pendiente |
| 8 | Bandeja: contadores por tipo · Calendario: tema oscuro de celdas + leyenda · Dashboard Ejecutivo: colapsar "Personalizar" + tema DS en ApexCharts | 10-H2/10-H3/01-H5/01-H6 | Pendiente |
| 9 | Lote de remates (un PR): filtros completos en URL + chips + borrar filtros guardados · placeholder "—" · header "RazonSocial" · label Notas del alta guiada · quitar pestaña "Citas" · fila clicable en Clientes · selector de tamaño de página · catch `JSDisconnectedException` (3 Dispose) · atribuir/resolver error CSP · baja en lote de Asignaciones (`DarDeBajaAsignacionesCommand`) · columna Fecha de baja | 02·03·05·06·16 | Pendiente |

## Parte 2 — Bloque Acreditación por plataforma destino (alcance MVP1)

Decisión registrada en `docs/business/DECISION_LOG.md` (2026-08-05). Se ejecuta **después**
de los quick wins. Referencias obligatorias: `docs/business/inbound/` (MARKET_CATALOG,
INBOUND_DOMAIN_GLOSSARY — estados/sinónimos y la colisión "Incidencia" —,
CANONICAL_MODEL_DRAFT § equivalencias) y `ARQUITECTURA-INTEGRACIONES.md`. Motivación: un
mismo documento puede estar vigente en Hydra, aceptado en Dokify y pendiente en Nalanda —
sin visión por plataforma, Hydra no puede ser la única pantalla del gestor.

### (a) Catálogo `ProveedorIntegracion`

Es la entidad ya diseñada en `ARQUITECTURA-INTEGRACIONES.md` — **no crear catálogo paralelo**.
Global + extensión por tenant (documentar en `MULTITENANCY.md` § 7, mismo patrón que
TipoDocumento), con dominios para identificación por URL. Migrar
`CanalGestionDocumental.NombrePlataforma` (texto libre) a referencia del catálogo con matching
sugerido de los strings existentes. **CTAIMACAE (legacy), Twind y e-coordina son TRES
proveedores separados** (hay empresas que hoy operan solo en una), unidos por el grupo
"Twind (CTAIMA Group)"; el campo "grupo empresarial" es solo para analítica, nunca lógica
operativa.

**Semilla de dominios (verificada por el propietario, 2026-08-05).** La resolución matchea
por dominio y sufijo (subdominios incluidos); multi-match ⇒ elegir entre candidatos; sin
match ⇒ selección manual / alta por tenant. Dominios editables desde el catálogo.

| Proveedor | Dominios | Grupo | Notas |
|---|---|---|---|
| Nalanda | nalandaglobal.com | Once For All | |
| Dokify | dokify.net | Once For All | |
| Twind | app.twind.io, ctaima.com | Twind (CTAIMA Group) | Marca unificada CTAIMA + e-coordina; destino esperado de migraciones |
| CTAIMACAE (legacy) | ctaimacae.net | Twind (CTAIMA Group) | Nunca objetivo de conector (`ARQUITECTURA-INTEGRACIONES.md` § 5.1) |
| e-coordina | e-coordina.es | Twind (CTAIMA Group) | Empresas aún operando aquí; migración esperada a Twind |
| Metacontratas | metacontratas.com | — | |
| CoordinaPlus | adding-plus.com, coordinaplus.net | Addingplus | |
| UCAE | ucae.es | — | |
| Validate | validate.es, validate.network | — | |
| eGestiona | egestiona.com, egestiona.es | — | |
| SmartOSH | smartosh.com | Prevencontrol | Suite HSE |
| EcoGestor | ecogestor.com | Eurofins | Suite HSE |
| Sabentis | sabentis.com, quironprevencion.com | — | ⚠️ dominio compartido con Quirón ⇒ multi-match |
| Unifikas | unifikas.com | — | |
| Quirón Prevención | quironprevencion.com | Quirónprevención | ⚠️ dominio compartido con Sabentis ⇒ multi-match |
| Previntegral | previntegral.com | — | |
| Norprevención | vithas.es | Vithas | Canales migrados tras integración empresarial |
| Ergasia | ergasia.es + subdominios de cliente | — | Match por sufijo |
| Valora | valoraprevencion.es + subdominios de cliente | — | Match por sufijo |
| PlayCAE | playcae.com + subdominios de cliente | — | Match por sufijo |
| DocuPRL | (sin dominio genérico) | — | Solo subdominios de cliente ⇒ alta manual |
| Arch | archbus.com | — | Foco real: mantenimiento de activos — sembrar inactivo |
| Opground | opground.com | — | Foco real: reclutamiento — sembrar inactivo |

### (b) Entidad `AcreditacionDocumentoPlataforma`

Documento × plataforma, estados: **Pendiente de subir / Subida (en validación) / Aceptada /
Rechazada / No requerida**. El estado Rechazada exige **siempre** causa tipificada (Ilegible,
Documento equivocado, Datos erróneos, Caducado al presentar, Falta firma/sello, Formato no
admitido, Otro) + **motivo literal de la plataforma** (texto libre). Invariantes: renovar
Documento ⇒ acreditaciones a "Pendiente de subir"; los rechazos anteriores se conservan como
**historial**, nunca se sobreescriben. Qué plataformas aplican a un documento se deriva de la
operación real (trabajador → asignaciones activas → centros → canal; documento de Empresa →
centros con actividad de esa empresa — reutilizar la query de `EmpresaWorkspacePanel`). Alta
del término "Acreditación" en `UBIQUITOUS_LANGUAGE.md`; **jamás "Incidencia"** para lo
documental (colisión real, `INBOUND_DOMAIN_GLOSSARY.md`).

### (c) Superficies de edición

Badges por plataforma en la lista de Documentos y en `PestanaDocumentacion`
(Trabajador/Empresa) con edición manual del estado. En **Eliminar y Renovar** de un Documento
con acreditaciones: preguntar si es a raíz de un rechazo y capturar causa+motivo antes del
soft delete. Historial de rechazos visible en el panel del Documento.

### (d) Pestaña de acreditación en el Centro

"De lo que este centro exige, qué está acreditado en su plataforma."

### (e) Dashboard y uso por plataforma

Tarjeta "Pendiente por plataforma" + vista de uso (centros/empresas/pendientes por
proveedor) — insumo de la decisión de conector, que sigue rigiéndose por
`ARQUITECTURA-INTEGRACIONES.md` § 5.1 (Twind primero).

### (f) Registro de la decisión

Hecho — `docs/business/DECISION_LOG.md`, entrada 2026-08-05 (commiteada junto a este plan).

### (g) Query de calidad documental

Agregación "rechazos por causa × plataforma × Empresa" (solo query, sin pantalla nueva) —
insumo del futuro KPI de calidad (¿falló Hydra o el documento vino mal de origen?) y de la
reclamación saliente con motivo precargado.

### (h) Acción "Migrar a [plataforma]"

En el canal de gestión del Centro: repunta el canal al proveedor destino con su nueva URL,
opción de **conservar o sustituir credenciales** (caso típico: solo cambia el link), y
pregunta "¿la plataforma destino migró la documentación presentada?" — Sí ⇒ transferir
estados de acreditación; No ⇒ todo a "Pendiente de subir". Las acreditaciones de la
plataforma origen quedan como historial. Cada migración persiste un registro (Centro,
Cliente, origen→destino, fecha, quién, qué se conservó) y se deja preparada la query
"migraciones por plataforma destino × periodo" — inteligencia interna para priorizar
conectores. **Límite**: "migrar" = repuntar canal y re-etiquetar acreditaciones en Hydra;
nunca mover documentación entre plataformas (eso es Fase 2 "Orquestador") — el copy debe
dejarlo claro.

## Límites

- El resto del Horizonte 2 de `ROADMAP-UX.md` (reclamación saliente, reportes parametrizados,
  bandeja agregada, agregados SQL) requiere petición explícita del propietario.
- Nada de conectores/scraping contra plataformas externas — Fase 2, fuera de este alcance.
