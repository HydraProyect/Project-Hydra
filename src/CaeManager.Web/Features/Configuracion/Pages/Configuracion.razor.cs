using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

public partial class Configuracion : ComponentBase
{
    [Parameter]
    public string? EntradaRuta { get; set; }

    [SupplyParameterFromQuery(Name = "entry")]
    private string? EntradaActual { get; set; }

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private string EntradaEfectiva
    {
        get
        {
            var candidata = EntradaRuta ?? EntradaActual ?? "params";
            return Buscar(candidata) is null ? "params" : candidata;
        }
    }

    /// <summary>
    /// Un enlace de salida puro (TipoPanel null, EsPaginaIntegrable false —
    /// hoy solo "plataforma") solo llega a este hub por "/configuracion?entry=…"
    /// o por deep-link forzado; el clic normal en la subnav ya va directo a su
    /// ruta literal (más específica que "/configuracion/{EntradaRuta?}") y
    /// nunca instancia este componente. Este redirect cubre las dos vías (la
    /// ruta y el parámetro de query pasan ambos por EntradaEfectiva). Mientras
    /// llega, el hub dice la verdad —la sección tiene pantalla propia— y ofrece
    /// el enlace; antes decía "Pantalla pendiente de especificación" sobre una
    /// pantalla que sí existe, el mismo defecto que motivó retirar "2fa"
    /// (A-08), pero invertido.
    /// </summary>
    protected override void OnParametersSet()
    {
        var actual = Buscar(EntradaEfectiva);
        if (actual is { TipoPanel: null, EsPaginaIntegrable: false })
            Navigation.NavigateTo(RutaDe(actual.Id));
    }

    private string TituloDocumento => Buscar(EntradaEfectiva) is { } entrada
        ? $"{entrada.Nombre} — Configuración"
        : "Configuración";

    /// <param name="Entradilla">
    /// Solo para paneles propios del hub que no pintan cabecera: el hub les
    /// pone el h2 (su Nombre) y esta entradilla. Las páginas integrables y
    /// los paneles con cabecera propia (Automatizaciones) la dejan en null,
    /// o saldrían dos títulos seguidos.
    /// </param>
    private sealed record EntradaConfiguracion(
        string Id,
        string Icono,
        string Nombre,
        string Descripcion,
        Type? TipoPanel,
        bool EsPaginaIntegrable = true,
        string? Entradilla = null);

    private sealed record GrupoConfiguracion(string Titulo, IReadOnlyList<EntradaConfiguracion> Entradas);

    /// <summary>
    /// Estructura y copy del array GROUPS del mockup. Las pantallas funcionales
    /// existentes se renderizan dentro del hub mediante su modo integrado; sus
    /// rutas históricas continúan disponibles como puntos de entrada directos.
    /// La entrada "2fa" del mockup se retira (H-4, DEC-3 opción a): no hay
    /// contrato de obligatoriedad ni pantalla administrativa que la respalde,
    /// y dejarla apuntando a null solo rendería una promesa navegable sin
    /// capacidad detrás ("Pantalla pendiente de especificación") en zona
    /// sensible. Se repone cuando exista esa política — no antes. El enlace
    /// del menú lateral a /cuenta/configurar-2fa es otra cosa (alta personal
    /// del propio usuario) y no se ve afectado por esta retirada.
    /// </summary>
    private static readonly IReadOnlyList<GrupoConfiguracion> Grupos =
    [
        new("Acceso e identidad",
        [
            new("usuarios", "US", "Usuarios", "Cuentas y carteras asignadas", typeof(Features.Usuarios.Pages.Usuarios)),
            new("roles", "RL", "Roles", "Permisos por perfil", typeof(Features.GestionRoles.Pages.Roles))
        ]),
        // Delegaciones y Estado comercial NO viven aquí (ver NavMenu.razor,
        // grupo "Plataforma"): su autoridad real es de CAPACIDAD
        // (AdminPlataforma, F2b-6), no del rol Administrador que gatea todo
        // este hub (Configuracion.razor, [Authorize(Roles=Administrador)]) —
        // meterlas en el hub las habría dejado invisibles para cualquier
        // sesión de capacidad AdminPlataforma que no lleve ese rol, por
        // diseño (Program.cs: "sesión privilegiada de plataforma no lleva
        // rol de negocio"). Sus rutas propias (/delegaciones,
        // /configuracion/comercial) siguen funcionando en modo standalone.
        new("Plataforma y conexiones",
        [
            new("api", "AP", "Claves API", "Acceso programático", typeof(Features.ApiKeys.Pages.ClavesApi)),
            new("integraciones", "IN", "Conexiones de integración", "M365, portales, webhooks", typeof(Features.Integraciones.Pages.Conexiones)),
            new("importar", "IM", "Importar datos", "Cuadro de Control CAE (Excel)", typeof(Features.Importacion.Pages.Importacion)),
            // "plataforma" es deliberadamente distinta a sus vecinas: TipoPanel
            // queda null a propósito — es lo único que evita el embedding (el
            // <DynamicComponent> de Configuracion.razor solo se renderiza si
            // TipoPanel no es null; EsPaginaIntegrable es ortogonal a esa
            // decisión, solo controla si se pasa IntegradaEnConfiguracion a un
            // panel que YA se va a embeber, así que aquí no hace nada por sí
            // sola — se deja en false solo para dejar constancia de la
            // intención). Motivo: Plataforma.razor lleva [Authorize] sin rol
            // —su autoridad es la IDENTIDAD RAÍZ del despliegue, no el rol
            // Administrador que gatea este hub entero (mismo motivo por el que
            // Delegaciones y Estado comercial ya NO viven aquí, ver arriba).
            // Embeberla habría colapsado platform privilege con el rol de
            // negocio Administrador. La entrada es solo un atajo de
            // descubrimiento (H-2/DEC-2): al pulsarla, la ruta literal
            // "/configuracion/plataforma" (más específica que
            // "/configuracion/{EntradaRuta?}") gana en el router de Blazor y
            // resuelve su propio gate. Si se llega por
            // "/configuracion?entry=plataforma", el hub resuelve la entrada por
            // el parámetro de query (ver EntradaEfectiva) y OnParametersSet
            // redirige igualmente a la ruta literal; ese acceso exige además el
            // rol Administrador del propio hub, así que no es fuga de
            // autorización.
            new("plataforma", "PL", "Administración de plataforma", "Inicialización e identidad raíz", null, false)
        ]),
        new("Catálogos y datos",
        [
            new("tipos", "TD", "Tipos de documento", "Catálogo y vigencias", typeof(Features.TiposDocumento.Pages.TiposDocumento)),
            // El mockup decía «Qué se extrae y con qué umbral»: no hay umbral
            // configurable, y el propio mockup pide corregir la promesa aquí y
            // en la entradilla de la pantalla a la vez (su nota «OJO»).
            new("ia", "IA", "Lectura IA por Cliente empresarial", "Restricción por tipo de documento", typeof(SeleccionarClienteLecturaIa)),
            new("macros", "MA", "Macros de respuesta", "Plantillas de comunicación", typeof(Features.Comunicaciones.Pages.Macros)),
            // El mockup pone de entradilla «Umbrales que gobiernan el semáforo
            // documental de toda la cartera» y promete que el cambio «recalcula
            // los estados en la próxima pasada nocturna». No hay pasada
            // nocturna (el estado se calcula en vivo, ver AutomatizacionesPanel)
            // y el panel tiene además jornada, medición de tiempo y
            // presupuesto de IA: la entradilla dice lo que el panel contiene.
            new("params", "PS", "Parámetros del sistema", "Umbrales del semáforo", typeof(Components.ParametrosSistemaPanel), false,
                "Umbrales del semáforo documental, franja de jornada y medición de tiempo, y aviso de gasto en IA."),
            new("retencion", "RT", "Retención de datos", "Plazos de borrado", typeof(Features.Retencion.Pages.Retencion))
        ]),
        new("Auditoría",
        [
            new("auditoria", "AU", "Auditoría", "Quién hizo qué y cuándo", typeof(Features.Auditoria.Pages.Auditoria)),
            new("auditoria-ia", "AI", "Auditoría IA", "Lecturas y decisiones automáticas", typeof(Features.AuditoriaIa.Pages.AuditoriaIa)),
            new("automatizaciones", "AT", "Automatizaciones", "Trabajos del sistema", typeof(Components.AutomatizacionesPanel), false)
        ])
    ];

    private static EntradaConfiguracion? Buscar(string id) =>
        Grupos.SelectMany(g => g.Entradas).FirstOrDefault(e => e.Id == id);

    private static string RutaDe(string id) => $"/configuracion/{id}";

    private static IDictionary<string, object>? ParametrosDelPanel(EntradaConfiguracion entrada) =>
        entrada.EsPaginaIntegrable
            ? new Dictionary<string, object>
            {
                [nameof(CaeManager.Web.Components.PaginaIntegrableConfiguracionBase.IntegradaEnConfiguracion)] = true
            }
            : null;
}
