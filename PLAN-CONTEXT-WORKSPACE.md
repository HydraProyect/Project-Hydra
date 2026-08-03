# Arquitectura — Context Workspace

**Estado**: Diseño de arquitectura, decisiones cerradas (ver abajo). **Implementado en `main`** (`src/CaeManager.Web/Components/Workspace/` — `ContextWorkspace.razor`, `ContextWorkspaceService`, `WorkspaceFrame`, `EntidadWorkspace`, paneles por entidad `XxxWorkspacePanel.razor` en cada Feature). **Auditado contra el código real el 2026-08-03 — ver § 0 para la lista completa de correspondencias y divergencias verificadas.** Resumen: el modelo de estado (pila, breadcrumb, cambio de pestaña sin tocar la pila, instancia única) coincide con el diseño; lo que **no** se construyó es la integración con la URL del navegador (§ 10, por tanto tampoco el cierre automático de § 8.1), la mayoría del teclado de § 11 (solo `Escape` está implementado) y el comportamiento "acoplado/push" de escritorio de § 12 (el panel es overlay `position: fixed` en todos los tamaños). Las reglas (R1-R8) y el modelo conceptual (§ 1-§ 2) siguen siendo la referencia normativa de intención; § 6/§ 10/§ 11/§ 12/§ 13 tienen ahora notas de estado real junto a la especificación original — no asumir que todo lo escrito abajo ya está construido. Este documento define el sistema de navegación contextual pedido — un panel lateral único e "inteligente" que sustituye el concepto genérico de "Side Workspace" descrito en `PLAN-MASTER-DETAIL-WORKSPACE.md` § 4 por una especificación concreta y con reglas más estrictas. **Este documento sustituye la tabla de pestañas de `PLAN-MASTER-DETAIL-WORKSPACE.md` § 4** por la especificada más abajo (§ 6), corregida contra el código real en § 0.

**Decisiones cerradas con el usuario (2026-07-25)**: cierre automático del panel al navegar a otra pantalla por el menú principal (§ 8.1); Subcontrata sí tiene Context Workspace propio, panel mínimo igual que Empresa (§ 6); Centro/Trabajador → Vehículos se resuelve como vista transitiva vía Empresa/Subcontrata, sin relación de modelo nueva (§ 15); Documento → Versiones se resuelve reutilizando `Auditoria` (misma fuente que Historial) añadiendo `FechaSubida`/`FechaCambio`, sin entidad `VersionDocumento` nueva (§ 15). **Los 7 gaps de § 15 quedan cerrados**: Documento → Validación (§ 15.6) se resolvió confirmando que no es una pestaña de este Context Workspace — es trazabilidad de quién aprobó cada verificación IA (automática vs manual), implementada como `AprobacionDocumento` + gráfico de Dashboard, ver `ROADMAP.md` Fase 45. `Documento` se queda con Información · Versiones · Historial (sin Validación) en el registro de § 6.

---

## 0. Auditoría contra la implementación real (2026-08-03)

Comparación línea a línea de `src/CaeManager.Web/Components/Workspace/` (más los `XxxWorkspacePanel.razor` de cada Feature) contra este documento. Nombres de archivo/clase difieren del texto original en varios sitios porque el código usa nombres en español donde el documento usaba inglés técnico — eso no cuenta como divergencia, se lista solo para no buscar en vano un archivo que no existe con ese nombre exacto.

**Corresponde tal cual (solo cambia el nombre):**

| Documento | Código real |
|---|---|
| `IContextWorkspaceService` | `ContextWorkspaceService` (clase concreta, sin interfaz — se inyecta y usa directamente, sin abstracción) |
| `ContextWorkspaceEntry` | `WorkspaceFrame` (mismos 4 campos: Tipo/EntidadId/TituloVisible/PestañaActiva) |
| `TipoEntidadContexto` | `EntidadWorkspace` (mismas 7 entidades, mismo propósito de reutilizarse como `EntidadTipo` de `RegistroAuditoria`) |
| `NavegarARelacionadoAsync` | `NavegarAAsync` — con una mejora no prevista en el plan: si la entidad ya está en la pila (el usuario vuelve a ella desde dos sitios distintos), trunca y reutiliza el nivel en vez de duplicarlo, evitando que un ciclo A→B→A→B haga crecer el breadcrumb sin límite |
| `IrAAsync(indice)` | `IrABreadcrumbAsync(indice)` |
| `Cerrar()` | `CerrarAsync()` |
| `Tabs.razor` (§ 13, "aún no construido") | Ya existe como `Pestanas.razor` (`Components/DesignSystem/`) — construido y en uso, cada `XxxWorkspacePanel` declara su propia lista estática de pestañas y se la pasa a un `<Pestanas>` |
| `ContextWorkspaceBreadcrumb.razor` | Reutiliza el `Breadcrumb.razor`/`BreadcrumbElemento` genérico ya existente de Design System — no se construyó un breadcrumb propio del Workspace |
| R1 (nunca anidado) | Cumplida — instancia única montada en `MainLayout`, sin ningún segundo punto de entrada |
| Botón "Volver" junto a la cabecera (§ 8) | Presente, condicionado a `Pila.Count > 1` |
| Foco al abrir (§ 11, implícito) | `ContextWorkspace.razor.OnAfterRenderAsync` mueve el foco al panel solo en la transición cerrado→abierto, no en cada cambio de nivel/pestaña — coincide con el criterio de accesibilidad del documento |

**No se construyó como está especificado — divergencias reales, no solo de nombre:**

- **§ 3/§ 5, arquitectura de componentes**: no hay `ContextWorkspaceCabecera`/`ContextWorkspaceBreadcrumb`/`ContextWorkspaceContent` como componentes separados, ni `RegistroPestañasContexto` ni `<DynamicComponent>`. Todo vive en un único `ContextWorkspace.razor` con un `switch (frame.Tipo)` inline que instancia el panel concreto (`ClienteWorkspacePanel`, `SubcontrataWorkspacePanel`, ...) — más simple que la especificación, mismo resultado observable, pero **el registro estático tabular de § 5 no existe**: añadir una pestaña nueva a una entidad significa editar el `_pestanas` de ese panel, no una fila de una tabla central.
- **§ 8.1, cierre automático al navegar por el menú principal — decisión cerrada, NO implementada.** Ni `ContextWorkspaceService` ni `ContextWorkspace.razor` se suscriben a `NavigationManager.LocationChanged` (sí lo hacen otros componentes de la app — `TrazaSoporte.razor.cs`, `BuscadorGlobal.razor.cs` — así que el mecanismo existe en el código base, simplemente no se conectó aquí). Comportamiento real hoy: el panel se queda abierto si el usuario navega a otra pantalla por el menú lateral mientras lo tiene abierto — contradice la decisión documentada como cerrada con el usuario el 2026-07-25.
- **§ 10, URL y navegador — no implementado en absoluto.** Ninguna llamada a `NavigationManager.NavigateTo` en todo el Workspace: no hay `?ctx=...`, no hay deep-linking, recargar la página (F5) no reconstruye nada (el estado vive solo en el servicio *scoped*, se pierde entero al recargar, no solo el breadcrumb como preveía el documento), y el botón "Atrás" del navegador no interactúa con el Workspace de ninguna forma. Consecuencia directa: § 8.1 no puede depender del patrón de URL que describía, porque no hay URL que observar.
- **§ 11, teclado — solo `Escape` está implementado**, y con un matiz: el documento especifica que `Escape` siempre cierra el Workspace completo (salvo que haya un Drawer encima); el código real hace que `Escape` retroceda un nivel si `Pila.Count > 1` y solo cierre del todo si ya está en el nivel raíz — más parecido al `Alt+←`/`Backspace` que describía el documento para "un nivel atrás" que a su propio `Escape`. `Alt+←`/`Backspace`, navegación con flechas en el tablist/breadcrumb, `Home`/`End`, y el atrapado de foco (`Tab`/`Shift+Tab`) **no existen**.
- **§ 12, responsive — no hay modo "acoplado" (push).** El panel es `position: fixed` (overlay puro) en todos los anchos de pantalla; solo cambia el `max-width` por breakpoint (520px por defecto, 420px en ≤1023px, 100% en ≤767px). La lista maestra nunca se comprime como preveía Desktop/Laptop. El colapso del breadcrumb a "← {segmento anterior}" en mobile tampoco está construido — se sigue usando el mismo `Breadcrumb` genérico a cualquier ancho.
- **§ 6, registro de pestañas — dos entidades no coinciden con la tabla:**
  - **Documento** tiene una pestaña **"Validación" que sí existe en el código** (`DocumentoWorkspacePanel.razor`), pese a que la decisión cerrada del encabezado de este documento dice explícitamente "Documento se queda con Información · Versiones · Historial (sin Validación)". La pestaña real no muestra el flujo de validación — es un `EstadoVacio` con el texto "Próximamente: el flujo de validación (quién valida, fecha, motivo de rechazo, criterios incumplidos) todavía no está disponible" —, es decir, quedó como marcador de lugar visible al usuario en vez de retirarse del todo tras cerrar la decisión de construirlo como `AprobacionDocumento` + gráfico de Dashboard (Fase 45). Pendiente: decidir si se quita la pestaña (coherente con la decisión ya tomada) o se deja como acceso directo al gráfico de Dashboard.
  - **Subcontrata** tiene *Información · Trabajadores · Documentación · Centros · Historial* (5 pestañas) en vez de las 4 que especifica el documento (*Información · Trabajadores · Vehículos · Historial*) — no tiene pestaña Vehículos (pese a que § 6 la describe como "relación directa, no transitiva" para Subcontrata) y sí tiene Documentación (con un `EstadoVacio` explicando que Documento no está modelado a nivel de Subcontrata todavía) y Centros (centros donde la Subcontrata tiene actividad real, vía sus Trabajadores).
  - **Centro**: la pestaña "Formularios" del documento (§ 15 punto 3, marcado ⬜ "sin cerrar... `RequisitoDocumental` no tiene ningún Command/Query todavía") **ya se construyó** — existe como pestaña "Requisitos del Centro" con alta/listado completo de `RequisitoDocumental`. El gap de § 15.3 puede darse por cerrado.
  - El resto (Cliente, Empresa, Trabajador, Vehículo) coincide exactamente con la tabla de § 6, mismas pestañas y mismo orden.

**Conclusión de la auditoría**: la base estructural (R1-R8, modelo de dos ejes, pila como breadcrumb, instancia única) se construyó fielmente. Lo que falta es la capa de integración con el navegador (URL, historial, cierre automático) y la mayor parte del teclado — ninguna de las dos es un defecto silencioso (nada se comporta mal, simplemente esas piezas no se conectaron), pero si se van a dar por hechas en una fase futura hay que construirlas, no asumirlas.

---

## 1. Reglas de diseño (invariantes, no sugerencias)

| # | Regla pedida | Cómo se convierte en invariante arquitectónico |
|---|---|---|
| R1 | Nunca abrir Side Workspaces anidados | Solo existe **una instancia** de `ContextWorkspacePanel` en todo el árbol de componentes (vive en el layout raíz, no por página). No hay ningún mecanismo en el diseño para instanciar una segunda. Cualquier navegación "hacia dentro" muta el estado de la instancia existente. |
| R2 | El Workspace cambia de contenido internamente | El panel es un **shell fijo** (cabecera + breadcrumb + tabs + cuerpo). Navegar a una entidad relacionada no cambia el shell, solo el contenido que despacha `ContextWorkspaceContent` según el estado actual. |
| R3 | Cada entidad posee pestañas | Registro estático `RegistroPestañasContexto` (§ 6), una entrada por tipo de entidad, fija en tiempo de compilación — no configurable por el usuario, no generada dinámicamente. |
| R4 | Breadcrumb | Estructura de **pila** (`IReadOnlyList<ContextWorkspaceEntry>`), no de árbol — refleja el camino realmente recorrido, no la jerarquía de datos (que ya vimos en el plan anterior que no es un árbol único: Documento con 4 propietarios, Centro con 2 padres). |
| R5 | Volver atrás | Dos formas equivalentes: click en un segmento del breadcrumb (salta y trunca la pila) y un control "Volver" explícito (saca un nivel). Ver § 8. |
| R6 | Teclado | Ver tabla completa § 12. |
| R7 | Responsive | Ver tabla por breakpoint § 13. |
| R8 | Reutilizar componentes existentes | Ver tabla de reutilización § 14 — ningún componente de `DesignSystem/` se reimplementa. |

---

## 2. Modelo conceptual: dos ejes independientes

El diseño separa explícitamente dos conceptos que es fácil confundir:

- **Eje de pestañas** (horizontal, dentro de una misma entidad): "estoy viendo al Cliente X, pestaña Documentación". No genera entrada de breadcrumb nueva — cambiar de pestaña es lateral, no es "navegar".
- **Eje de navegación contextual** (breadcrumb, entre entidades): "vine del Cliente X, entré a la Empresa Y, entré al Centro Z". Cada salto genera una entrada nueva en la pila.

Cambiar de pestaña **nunca** empuja el breadcrumb. Navegar a una entidad relacionada **siempre** empuja el breadcrumb y resetea la pestaña activa a `Información` (punto de entrada consistente a cualquier entidad nueva, salvo que el enlace de origen pida una pestaña concreta explícitamente — p. ej. un enlace "Ver documentación de este Trabajador" desde otro lado sí puede abrir directo en la pestaña `Documentación`).

---

## 3. Arquitectura de componentes

```
WorkspaceLayout (MainLayout, ya existe/Fase 0 del plan anterior)
│
├── @Body (lista maestra de la página actual — Clientes, Trabajadores, ...)
│
└── ContextWorkspacePanel.razor          ← instancia ÚNICA, vive aquí, no en cada página
    ├── ContextWorkspaceCabecera.razor    (título de la entidad actual + botón cerrar)
    ├── ContextWorkspaceBreadcrumb.razor  (pila de navegación + botón "Volver")
    ├── Tabs.razor                        (componente nuevo del Plan anterior § Fase 0, reutilizado tal cual)
    └── ContextWorkspaceContent.razor     (despachador — renderiza la pestaña activa de la entidad activa)
        └── <DynamicComponent Type="@TipoComponentePestaña" Parameters="@Parametros" />
            └── p.ej. ClienteInformacionTab.razor / EmpresaCentrosTab.razor / etc. (§ 6)
```

Ningún componente de página (`Clientes.razor`, `Trabajadores.razor`, ...) instancia su propio panel de detalle. Todos comparten el mismo `ContextWorkspacePanel` a través de un servicio inyectado — así es como R1 queda garantizado por construcción, no por convención.

---

## 4. Servicio de estado: `ContextWorkspaceService`

Servicio **scoped** (uno por circuito de Blazor Server — un usuario, una pestaña de navegador, un estado de workspace). Es la única fuente de verdad; ningún componente guarda la pila localmente.

Contrato (diseño de interfaz, no implementación):

```csharp
public interface IContextWorkspaceService
{
    IReadOnlyList<ContextWorkspaceEntry> Pila { get; }
    ContextWorkspaceEntry? Actual { get; }          // tope de la pila, null = cerrado
    bool EstaAbierto { get; }

    event Action? OnCambio;

    // Abre desde cero (desde una lista maestra) — nueva pila de 1 elemento.
    Task AbrirAsync(TipoEntidadContexto tipo, Guid id, string? pestañaInicial = null);

    // Navega "hacia dentro" a una entidad relacionada — empuja la pila.
    Task NavegarARelacionadoAsync(TipoEntidadContexto tipo, Guid id, string? pestañaInicial = null);

    // Cambia de pestaña dentro de la entidad actual — NO toca la pila.
    void CambiarPestaña(string pestaña);

    // Breadcrumb: salta a un punto anterior de la pila, truncando lo posterior.
    Task IrAAsync(int indice);

    // Un nivel atrás (equivalente a IrAAsync(Pila.Count - 2)).
    Task VolverAsync();

    // Cierra completamente, limpia la pila.
    void Cerrar();
}
```

`ContextWorkspaceEntry` (record, inmutable):

```csharp
public sealed record ContextWorkspaceEntry(
    TipoEntidadContexto Tipo,
    Guid Id,
    string EtiquetaBreadcrumb,   // nombre a mostrar, resuelto una vez al navegar (evita refetch al pintar el breadcrumb completo)
    string PestañaActiva);
```

`TipoEntidadContexto` — enum cerrado con exactamente las 7 entidades con Context Workspace (Cliente, Empresa, Centro, Trabajador, Vehiculo, Documento, Subcontrata — 8→7 de `PLAN-MASTER-DETAIL-WORKSPACE.md` § 4 menos Visita, que **no** tiene pestañas propias en esta especificación; Subcontrata se incorporó como panel mínimo el 2026-07-25, ver § 6/§ 15).

**Por qué `EtiquetaBreadcrumb` se guarda en la entrada y no se recalcula**: pintar el breadcrumb completo (hasta 4-5 niveles típico: Cliente→Empresa→Centro→Trabajador→Documento) no debe disparar una query por segmento en cada render; se resuelve una vez al hacer el salto (la pantalla que origina la navegación ya tiene el nombre en memoria — viene de la fila de la tabla que el usuario clicó) y se congela en la entrada.

---

## 5. Despachador de contenido

`ContextWorkspaceContent.razor` no usa una cadena de `@if/else if` por entidad (no escala a 6 entidades × 4-6 pestañas = ~30 combinaciones). Usa un registro estático resuelto en `Program.cs`/`ApplicationServiceCollectionExtensions`:

```csharp
public static class RegistroPestañasContexto
{
    public static IReadOnlyDictionary<TipoEntidadContexto, IReadOnlyList<DefinicionPestaña>> Pestañas { get; }
}

public sealed record DefinicionPestaña(string Clave, string Etiqueta, Type TipoComponente, string Icono);
```

`ContextWorkspaceContent` resuelve `Pestañas[Actual.Tipo]` para pintar el `Tabs.razor`, y renderiza `Pestañas[Actual.Tipo].First(p => p.Clave == Actual.PestañaActiva).TipoComponente` vía `<DynamicComponent>` de Blazor (built-in, sin reflexión custom), pasando `EntidadId = Actual.Id` como parámetro. Es explícito y tabular — se lee como una tabla, no como lógica dispersa (mismo criterio de "Consistencia de patrones" de `PROJECT.md`).

Cada componente de pestaña (`ClienteInformacionTab.razor`, `CentroTrabajadoresTab.razor`, ...) es responsable de:
1. Cargar sus propios datos (Query específica, con `IAlcanceDatosService` aplicado — ver riesgo ya señalado en el plan anterior § 7.1).
2. Si es una pestaña de relación (lista), pintar la sub-lista con `tabla-datos`/`QuickGrid` y, al hacer click en una fila relacionada, llamar `ContextWorkspaceService.NavegarARelacionadoAsync(...)` — **nunca** abrir un `Drawer`/`Modal` propio para "ver el detalle", eso violaría R1.
3. Si es una pestaña de datos propios editables, mostrar los datos de solo lectura + un botón "Editar" que abre el `Drawer` existente (ver § 10) — la edición no es responsabilidad del Workspace, sigue siendo responsabilidad del patrón Crear/Editar ya establecido.

---

## 6. Registro de pestañas por entidad (autoritativo)

| Entidad | Pestañas (orden fijo) | Fuente de datos / notas de mapeo |
|---|---|---|
| **Cliente** | Información · Empresas · Subcontratas · Documentación · Actividad · Notas | Información = `Cliente` propio. Empresas = `EmpresaCliente` (N:N). Subcontratas = `SubcontrataCliente` (N:N). Documentación = `Documento` con `ClienteId = actual`. Actividad = `Auditoria` filtrada por `EntidadTipo=Cliente, EntidadId=actual` (reutiliza la Query de `/auditoria`, no una nueva). Notas = campo `Cliente.Notas` (string único hoy — ver gap § 15). |
| **Empresa** | Información · Centros · Documentación · Historial | Información = `Empresa` propio + `CredencialAccesoEmpresa`. Centros = `Centro` con `EmpresaId = actual`. Documentación = `Documento` con `EmpresaId = actual`. Historial = `Auditoria` filtrada por Empresa. |
| **Centro** | Información · Formularios · Trabajadores · Vehículos · Plataforma · Historial | Información = `Centro` propio. Formularios = `RequisitoDocumental` de este Centro + `TipoDocumentoCentro` (tipos exigidos) — **requiere construir su primer Command/Query, no existe hoy**, ver gap § 15. Trabajadores = `Asignacion` activa con `CentroId = actual` → `Trabajador`. Vehículos = **vista transitiva** (decisión 2026-07-25, § 15): vehículos de la Empresa que opera el Centro, sin relación de modelo nueva. Plataforma = `PlataformaAcceso` (1:1). Historial = `Auditoria` filtrada por Centro. |
| **Trabajador** | Información · Documentación · Citas · Vehículos · Historial | Información = `Trabajador` propio. Documentación = `Documento` con `TrabajadorId = actual`. Citas = `Visita` vía `VisitaTrabajador` donde `TrabajadorId = actual` (se muestra con la etiqueta "Citas", back con la entidad `Visita` existente — no es una entidad nueva). Vehículos = **vista transitiva** (decisión 2026-07-25, § 15): vehículos de la Empresa/Subcontrata que emplea al Trabajador, sin relación de modelo nueva. Historial = `Auditoria` filtrada por Trabajador. |
| **Vehículo** | Información · Documentación · Historial | Información = `Vehiculo` propio. Documentación = `Documento` con `VehiculoId = actual`. Historial = `Auditoria` filtrada por Vehículo. |
| **Subcontrata** | Información · Trabajadores · Vehículos · Historial | Panel mínimo, análogo a Empresa (decisión 2026-07-25, § 15) — evita que "Subcontratas" en las pestañas de relación de Cliente/Empresa deje un enlace muerto. Información = `Subcontrata` propio + `CredencialAccesoSubcontrata`. Trabajadores = `Trabajador` con `SubcontrataId = actual`. Vehículos = `Vehiculo` con `SubcontrataId = actual` (aquí sí es relación directa, no transitiva). Historial = `Auditoria` filtrada por Subcontrata. |
| **Documento** | Información · Versiones · Historial | Información = `Documento` propio + enlace "Ver propietario" (navega al Cliente/Empresa/Trabajador/Vehículo correspondiente según cuál FK esté poblada — `NavegarARelacionadoAsync`). Versiones = **resuelto sin entidad nueva ni columna nueva** (decisión 2026-07-25, § 15): `RegistroAuditoria` (`Domain/Auditoria/`) ya guarda `FechaUtc`/`Accion` por cada cambio del interceptor de EF Core — "fecha de subida" es el `FechaUtc` del primer registro (`Accion="Creado"`) y "fecha de cambio" el del más reciente para ese `EntidadId`, ambos derivados de la misma tabla que ya alimenta Historial, sin tocar el esquema. Sigue sin ser un historial de archivos reemplazados (eso seguiría siendo `VersionDocumento`, explícitamente no construido); es una lectura de dos fechas sobre datos que ya existen. Historial = `Auditoria` filtrada por Documento. **Sin pestaña Validación** (decisión 2026-07-25, § 15.6): no es un concepto de este Context Workspace — se resolvió como `AprobacionDocumento` (Automática/Manual) + gráfico "gestiones automáticas vs manuales" en el Dashboard, ver `ROADMAP.md` Fase 45. |

**Subcontrata y Visita**: Subcontrata ya tiene fila propia arriba (decisión 2026-07-25). Visita sigue sin Context Workspace propio en esta especificación (no aparece en el pedido original) — se llega a ella únicamente como destino de navegación desde la pestaña "Citas" de Trabajador o "Visitas" de Centro.

---

## 7. Regla "nunca anidado" — cómo se garantiza, no solo se pide

Tres mecanismos, no uno solo (defensa en profundidad):

1. **Estructural**: una sola instancia de `ContextWorkspacePanel` en el árbol de render (vive en el layout, no en cada página) — no hay forma de montar una segunda sin tocar el layout mismo.
2. **De servicio**: `IContextWorkspaceService` es *scoped*, no *transient* — inyectarlo en cualquier componente devuelve siempre la misma instancia con la misma pila. Un componente de pestaña que "navega" no crea estado nuevo, muta el único estado existente.
3. **De contrato de componentes de pestaña**: por convención de `CODING_STANDARDS.md` a añadir, ningún componente registrado en `RegistroPestañasContexto` puede declarar un `Drawer`/`Modal`/`ContextWorkspacePanel` propio para "mostrar más detalle de una entidad" — la única vía permitida para eso es `NavegarARelacionadoAsync`. Esto se documenta como regla de revisión de código (igual que ya existe la regla de no usar `IgnoreQueryFilters()` sin revisión en `CLAUDE.md`).

---

## 8. Breadcrumb — comportamiento

`ContextWorkspaceBreadcrumb.razor` pinta `Pila` como una lista de segmentos: `Cliente: Cadena Industrial Iberia > Empresa: Ibertec S.A. > Centro: Planta Sur`, el último en negrita/no clicable (es el actual). Comportamiento:

- **Click en un segmento intermedio** → `IrAAsync(indice)`: trunca la pila a ese punto (todo lo posterior se descarta, no se "recuerda" para un forward — mismo comportamiento que un breadcrumb de sistema de archivos, no el de un navegador con forward/back independiente; más simple y predecible).
- **Botón "← Volver"** (separado del breadcrumb, junto a la cabecera) → `VolverAsync()`: un nivel exacto, equivalente a click en el penúltimo segmento.
- **Botón cerrar (×)** en la cabecera → `Cerrar()`: vacía la pila entera, el panel desaparece y el layout vuelve a mostrar solo la lista maestra a ancho completo.
- La pila **no tiene límite artificial**, pero en la práctica el grafo de entidades (plan anterior § 2) hace improbable pasar de 4-5 niveles (Cliente→Empresa→Centro→Trabajador→Documento es el camino más largo posible).

### 8.1 Cierre automático al cambiar de pantalla (decisión cerrada, 2026-07-25)

El documento original no especificaba qué pasa con el panel si el usuario navega a otra pantalla por el **menú principal** (no por el propio Workspace) mientras hay un Context Workspace abierto — p. ej. viendo el detalle de un Cliente y haciendo click en "Trabajadores" del nav lateral.

**Decisión: el panel se cierra automáticamente.** Cambiar de pantalla por el menú principal es una señal inequívoca de "quiero ver otra cosa" — mantener abierto el detalle de una entidad mientras se navega a una lista no relacionada mezcla contextos y genera ambigüedad sobre a qué entidad se aplican las acciones visibles del panel. Es la misma regla de simplicidad que ya rige el resto del sistema (breadcrumb tipo pila sin "recordar" un forward, § 8).

Mecanismo: `IContextWorkspaceService` se suscribe a `NavigationManager.LocationChanged`. Toda navegación que **no** provenga de una llamada propia del servicio (`AbrirAsync`/`NavegarARelacionadoAsync`/`CambiarPestaña`/`IrAAsync`/`VolverAsync`, todas identificables porque son las únicas que producen el patrón de URL `?ctx=...` descrito en § 10) se trata como "el usuario salió por el menú" y dispara `Cerrar()` antes de que la nueva página termine de renderizar — mismo criterio de "no dejar estado fantasma" que ya aplica al cerrar con Escape o el botón ×.

---

## 9. Integración con `Drawer` existente (crear/editar)

El `Drawer` (`UX_PATTERNS.md` § Crear/Editar) **no es un Side Workspace** — es un overlay modal de formulario de un único agregado, ya existente, y sigue siéndolo. Abrir un `Drawer` de edición **desde dentro** del Context Workspace (p. ej. botón "Editar" en la pestaña Información de un Cliente) no viola R1: son dos capas de naturaleza distinta —

- El Context Workspace es **navegación** (ver/explorar entidades relacionadas).
- El Drawer es **mutación** (formulario de crear/editar un único agregado), siempre modal, siempre encima, siempre se cierra solo (nunca dos Drawers apilados — regla ya vigente hoy, sin cambios).

Al guardar en el Drawer, la pestaña de Información activa se refresca in situ (mismo patrón ya usado hoy: "la tabla se actualiza sin recargar página"), sin tocar la pila de navegación.

---

## 10. URL y navegador (deep-linking, recarga, botón atrás del navegador)

Se serializa en la URL **solo la entrada actual** (tope de la pila), no la pila completa:

```
/clientes?ctx=cliente:3fa8...:documentacion
```

- **Abrir desde una lista maestra** (`AbrirAsync`) → `NavigationManager.NavigateTo(..., replace: false)`: crea una entrada nueva en el historial del navegador. El botón "Atrás" del navegador, en este punto, cierra el Workspace y vuelve a la lista — comportamiento esperado por el usuario.
- **Navegar a un relacionado o cambiar de pestaña dentro del Workspace** (`NavegarARelacionadoAsync`, `CambiarPestaña`) → `NavigateTo(..., replace: true)`: actualiza la URL sin apilar historial. Evita que 4 saltos internos obliguen a pulsar "Atrás" 4 veces en el navegador para volver a la lista — la navegación **dentro** del Workspace usa su propio breadcrumb/botón Volver (§ 8), no el historial del navegador.
- **Recarga de página (F5) o enlace compartido**: reconstruye una pila de **un solo elemento** (la entrada de la URL) — el breadcrumb se resetea. Es un trade-off consciente y documentado: persistir la pila completa en la URL (o en `sessionStorage`) es una mejora futura, no necesaria para la v1 de este sistema (YAGNI, `PROJECT.md` § Principios) — el usuario recargando pierde el "camino recorrido" pero no pierde dónde estaba.
- Compatibilidad con las URLs heredadas del plan anterior (`/documentos?documentoId=X`, `/centros?q=X`) se mantiene igual — ese plan definía cómo entrar desde fuera; este documento define qué pasa una vez dentro.

---

## 11. Teclado

| Tecla | Contexto | Acción |
|---|---|---|
| `Escape` | Foco dentro del Workspace, sin Drawer abierto encima | Cierra el Workspace completo (`Cerrar()`) |
| `Escape` | Con un Drawer abierto encima del Workspace | Cierra primero el Drawer (comportamiento ya existente de `Drawer.razor`); un segundo `Escape` cierra el Workspace |
| `Alt+←` o `Backspace` (fuera de un campo de texto) | Foco dentro del Workspace | Equivalente a `VolverAsync()` |
| `Tab` / `Shift+Tab` | Foco dentro del Workspace | Recorrido normal; el foco queda **atrapado** dentro del panel mientras está abierto (mismo mecanismo que ya usan `Modal`/`Drawer`) |
| `←` / `→` | Foco sobre el tablist (`Tabs.razor`) | Mueve el foco entre pestañas (patrón ARIA `tablist` estándar) |
| `←` / `→` | Foco sobre el breadcrumb | Mueve el foco entre segmentos |
| `Enter` / `Espacio` | Foco sobre una pestaña, un segmento de breadcrumb, o una fila de una sub-lista relacionada | Activa (cambia pestaña / navega / entra al relacionado) |
| `Home` / `End` | Foco sobre breadcrumb | Salta al primer/último segmento |

Al cerrar el Workspace (`Escape` o botón ×), el foco vuelve al elemento que lo abrió (la fila/enlace clicado en la lista maestra) — mismo criterio de accesibilidad que ya exige `DESIGN_SYSTEM.md` para overlays.

---

## 12. Responsive

| Breakpoint | Comportamiento |
|---|---|
| Desktop ≥1280px | Panel **acoplado** (push, no overlay) a la derecha, ancho fijo (~480px), lista maestra se comprime. `shadow.overlay` en el borde izquierdo del panel. |
| Laptop 1024–1279px | Igual que desktop, columna de lista maestra más comprimida (ya es el comportamiento definido para sidebar en `DESIGN_SYSTEM.md`). |
| Tablet 768–1023px | Panel pasa a **overlay** (se superpone a la lista, no la comprime) — mismo criterio que ya usa `Drawer` en este rango. Cierra con click fuera (respetando la misma cautela ya implementada en `Drawer.razor` sobre no cerrar accidentalmente al seleccionar texto). |
| Mobile <768px | Panel a **pantalla completa** (100vw/100vh). El breadcrumb se colapsa visualmente a un único control "← {segmento anterior}" (patrón de navegación tipo stack de apps móviles) en vez de la fila completa de segmentos — con los segmentos completos accesibles vía un toque largo o un menú "..." si se necesitan más de 2 niveles (a validar con el usuario en la fase de implementación, no una decisión cerrada). Las pestañas se vuelven una tira horizontal deslizable (`overflow-x: auto`, mismo patrón que tablas → scroll horizontal ya definido). |

---

## 13. Reutilización de componentes existentes

| Componente existente | Rol nuevo en Context Workspace |
|---|---|
| `Tabs.razor` (nuevo del plan anterior, Fase 0 — aún no construido, pero **un solo componente**, no uno por entidad) | Pinta el eje de pestañas (§ 2) para cualquier entidad, parametrizado por `RegistroPestañasContexto` |
| `Drawer.razor` | Formularios de crear/editar lanzados desde una pestaña de Información (§ 9), sin cambios |
| `Modal.razor` / `DialogoConfirmacion.razor` | Confirmaciones destructivas (eliminar) lanzadas desde dentro de una pestaña, sin cambios |
| `QuickGrid` + tema `tabla-datos` | Todas las pestañas de relación (Empresas, Centros, Documentación, Trabajadores, Vehículos, Historial...) — mismas columnas/paginación que las listas maestras |
| `Badge` | Estado de vigencia en filas de Documentación, badges de rol en Historial/Auditoria |
| `EstadoVacio` / `EstadoCargando` | Estados obligatorios (`UX_PATTERNS.md`) dentro de cada pestaña — p. ej. "Este Cliente todavía no tiene Empresas relacionadas" |
| `ToastService` | Confirmaciones de guardado al volver de un Drawer lanzado desde el Workspace |
| `CampoTexto` | Pestaña "Notas" de Cliente (edición inline del campo `Notas`, con autoguardado — ya permitido para "campos de notas/comentarios libres" en `UX_PATTERNS.md`) |
| `Icono` | Iconos de pestaña en `Tabs.razor`, iconos de tipo de entidad en el breadcrumb |
| Lógica de `BuscadorGlobal` (no el componente completo) | Su acción de "ir a resultado" pasa a llamar `AbrirAsync(tipo, id)` en vez de `NavigationManager.NavigateTo` a una lista filtrada |

Ningún componente nuevo de `DesignSystem/` se necesita salvo `Tabs.razor` (ya previsto) — el resto de piezas nuevas (`ContextWorkspacePanel`, `ContextWorkspaceBreadcrumb`, `ContextWorkspaceContent`, `IContextWorkspaceService`) son específicas de este sistema, no genéricas de Design System.

---

## 14. Diagrama de estados

```mermaid
stateDiagram-v2
    [*] --> Cerrado
    Cerrado --> Abierto: AbrirAsync(tipo, id)
    Abierto --> Abierto: CambiarPestaña (no toca la pila)
    Abierto --> Abierto: NavegarARelacionadoAsync (push)
    Abierto --> Abierto: IrAAsync(i) / VolverAsync (pop / truncar)
    Abierto --> Cerrado: Cerrar() / Escape (sin Drawer abierto)
    Abierto --> AbiertoConDrawer: Editar (abre Drawer encima)
    AbiertoConDrawer --> Abierto: Guardar / Cancelar / Escape (cierra solo el Drawer)
```

---

## 15. Gaps de modelo de datos detectados

La especificación de pestañas pedida asumía relaciones que no existían todavía en el dominio verificado en el plan anterior. Se cerraron 6 de los 7 con el usuario el 2026-07-25 (queda 1 abierto, punto 6):

1. ✅ **Centro → Vehículos** — resuelto: vista transitiva ("vehículos de la Empresa que opera este Centro"), sin relación de modelo nueva. Ver § 6.
2. ✅ **Trabajador → Vehículos** — mismo criterio que el punto 1: vista transitiva vía la Empresa/Subcontrata que emplea al Trabajador. Ver § 6.
3. ⬜ **Centro → Formularios**: mapea a `RequisitoDocumental`, que **no tiene ningún Command/Query en `Application` todavía** (confirmado en el plan anterior) — la pestaña "Formularios" implica construir esa funcionalidad de cero, no solo una vista nueva de datos existentes. Sin cerrar — no es una decisión de UX, es alcance de implementación a presupuestar en la Fase 3 (Centro) de `PLAN-MASTER-DETAIL-WORKSPACE.md` § 9.
4. ✅ **Cliente → Notas** — resuelto: se mantiene el campo único `Cliente.Notas` editable inline con autoguardado (patrón ya permitido en `UX_PATTERNS.md`); no se construye `NotaCliente` como historial con autor/fecha en esta versión.
5. ✅ **Documento → Versiones** — resuelto sin entidad ni columna nueva: se deriva de `RegistroAuditoria` (`FechaUtc` del primer y del último registro para ese `EntidadId`). Ver § 6.
6. ✅ **Documento → Validación** — resuelto (2026-07-25): no es una pestaña de este Context Workspace. El pedido real, aclarado por el usuario, era trazabilidad de quién resolvió cada verificación IA de un Documento — automatización o humano — para poder auditarlo y compararlo (Issue de mitigación de errores). Se implementó como `AprobacionDocumento` (`Domain/Documentos/`, `Tipo`: Automática/Manual, `UsuarioId` cuando es manual) más un gráfico "gestiones automáticas vs manuales" en el Dashboard — no como parte de este documento de navegación. Ver `ROADMAP.md` Fase 45 para el detalle completo.
7. ✅ **Subcontrata sin Context Workspace propio** — resuelto: Subcontrata sí tiene panel propio, mínimo, análogo a Empresa. Ver § 6.

Los 7 gaps quedan cerrados — no bloquean ya nada de lo especificado en este documento.

---

## 16. Qué queda fuera de este documento (aún no implementado, por diseño)

- Código de `IContextWorkspaceService`, componentes `.razor`, o el registro `RegistroPestañasContexto` — son firmas de diseño, no implementación.
- Persistencia de la pila completa en `sessionStorage` (mencionada en § 10 como mejora futura, no de esta versión).

---

## 17. Revisión adicional (2026-07-25) — hallazgos no cubiertos en la versión original

Tres observaciones de diseño detectadas al cerrar el documento con el usuario, no eran gaps de modelo (§ 15) sino de comportamiento/UX no especificado:

1. **Densidad del panel a 480px** (Desktop/Laptop, § 12): las sub-listas dentro de una pestaña de relación (Documentos de un Trabajador, Trabajadores de un Centro...) van a mostrar bastantes menos columnas que las mismas entidades en su lista maestra a pantalla completa — el ancho fijo del panel no da para las mismas columnas que `tabla-datos` hoy. **Recomendación**: definir una variante "compacta" de `tabla-datos` (2-3 columnas clave + badge de estado, sin las columnas secundarias que sí caben en la lista maestra) antes de construir la primera pestaña de relación en la Fase 1 (Trabajador) — de lo contrario cada pestaña improvisa su propio recorte de columnas de forma inconsistente.
2. **Verificación de alcance al navegar a un relacionado**: `NavegarARelacionadoAsync` puede aterrizar en una entidad fuera de la cartera del usuario actual (p. ej. "Ver propietario" de un Documento cuyo Cliente no está en el alcance de ese usuario — situación posible si el Documento se ve desde una lista con alcance más amplio que el detalle). **Recomendación, no negociable dado `CLAUDE.md`**: cada componente de pestaña de Información debe volver a pasar por `IAlcanceDatosService` al cargar, exactamente igual que ya exige el Issue #18 para las Query `*PorId*` de hoy — si no es visible, la pestaña muestra `EstadoVacio`/mensaje de sin-acceso, nunca los datos. Esto ya estaba implícito en § 5 punto 1 ("con `IAlcanceDatosService` aplicado") pero merece quedar explícito como regla de revisión de código, igual que la de R1 en § 7.
3. **Breadcrumb en mobile, confirmado**: se confirma la propuesta de § 12 tal cual estaba escrita — colapsar a "← {segmento anterior}" con los niveles superiores accesibles vía un menú "···" (no long-press: no es un gesto descubrible en un producto de administración de datos, la base de usuarios no viene de apps consumer). Deja de ser "a validar en fase de implementación" y pasa a ser la especificación.
