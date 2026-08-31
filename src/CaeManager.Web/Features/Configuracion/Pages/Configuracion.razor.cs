using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

public partial class Configuracion : ComponentBase
{
    [Parameter]
    public string? EntradaRuta { get; set; }

    [SupplyParameterFromQuery(Name = "entry")]
    private string? EntradaActual { get; set; }

    private string EntradaEfectiva
    {
        get
        {
            var candidata = EntradaRuta ?? EntradaActual ?? "params";
            return Buscar(candidata) is null ? "params" : candidata;
        }
    }

    private string TituloDocumento => Buscar(EntradaEfectiva) is { } entrada
        ? $"{entrada.Nombre} — Configuración"
        : "Configuración";

    private sealed record EntradaConfiguracion(
        string Id,
        string Icono,
        string Nombre,
        string Descripcion,
        Type? TipoPanel,
        bool EsPaginaIntegrable = true);

    private sealed record GrupoConfiguracion(string Titulo, IReadOnlyList<EntradaConfiguracion> Entradas);

    /// <summary>
    /// Estructura y copy del array GROUPS del mockup. Las pantallas funcionales
    /// existentes se renderizan dentro del hub mediante su modo integrado; sus
    /// rutas históricas continúan disponibles como puntos de entrada directos.
    /// 2FA conserva el estado no disponible porque no existe un contrato ni una
    /// pantalla administrativa equivalente que se pueda reutilizar con seguridad.
    /// </summary>
    private static readonly IReadOnlyList<GrupoConfiguracion> Grupos =
    [
        new("Acceso e identidad",
        [
            new("usuarios", "US", "Usuarios", "Cuentas y carteras asignadas", typeof(Features.Usuarios.Pages.Usuarios)),
            new("roles", "RL", "Roles", "Permisos por perfil", typeof(Features.GestionRoles.Pages.Roles)),
            new("2fa", "2F", "Verificación en dos pasos", "Obligatoriedad y métodos", null)
        ]),
        new("Plataforma y conexiones",
        [
            new("delegaciones", "DL", "Delegaciones", "Sedes de la consultora", typeof(Features.Delegaciones.Pages.Delegaciones)),
            new("api", "AP", "Claves API", "Acceso programático", typeof(Features.ApiKeys.Pages.ClavesApi)),
            new("estado-comercial", "CM", "Estado comercial", "Plan y consumo", typeof(Features.Comercial.Pages.EstadoComercial)),
            new("integraciones", "IN", "Conexiones de integración", "M365, portales, webhooks", typeof(Features.Integraciones.Pages.Conexiones)),
            new("importar", "IM", "Importar datos", "Cuadro de Control CAE (Excel)", typeof(Features.Importacion.Pages.Importacion))
        ]),
        new("Catálogos y datos",
        [
            new("tipos", "TD", "Tipos de documento", "Catálogo y vigencias", typeof(Features.TiposDocumento.Pages.TiposDocumento)),
            new("ia", "IA", "Lectura IA por cliente", "Qué se extrae y con qué umbral", typeof(SeleccionarClienteLecturaIa)),
            new("macros", "MA", "Macros de respuesta", "Plantillas de comunicación", typeof(Features.Comunicaciones.Pages.Macros)),
            new("params", "PS", "Parámetros del sistema", "Umbrales del semáforo", typeof(Components.ParametrosSistemaPanel), false),
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
