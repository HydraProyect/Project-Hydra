using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

public partial class Configuracion : ComponentBase
{
    [SupplyParameterFromQuery(Name = "entry")]
    private string? EntradaActual { get; set; }

    private string EntradaEfectiva => EntradaActual ?? "params";

    private sealed record EntradaConfiguracion(string Id, string Icono, string Nombre, string Descripcion, string? Ruta);

    private sealed record GrupoConfiguracion(string Titulo, IReadOnlyList<EntradaConfiguracion> Entradas);

    /// <summary>
    /// Estructura y copy exactos del array GROUPS del mockup (Configuracion
    /// TALVEG.dc.html). De las 14 entradas, el mockup solo especifica el
    /// contenido de 2 ("params" y "automatizaciones" — showParams/showAutomations
    /// en su script embebido); las otras 12 caen en su propia rama
    /// showPlaceholder ("Pantalla pendiente de especificación en este
    /// prototipo"). En vez de reproducir ese placeholder aquí — sería un
    /// retroceso funcional, esas 12 pantallas ya existen y funcionan — cada
    /// una enlaza a su página real (Ruta no nula). La única excepción es
    /// "2fa": el mockup tampoco la especifica, y a diferencia de las otras
    /// 12 no hay ninguna página real de política de doble factor a nivel de
    /// administrador (solo existe el alta personal de cada usuario en
    /// /cuenta/configurar-2fa, que no es lo mismo). Construir esa política
    /// desde cero — qué métodos, cómo se fuerza en el login — no lo pide ni
    /// el mockup ni ningún requisito existente, así que Ruta se deja en null
    /// y esa entrada mantiene el propio placeholder del mockup.
    /// </summary>
    private static readonly IReadOnlyList<GrupoConfiguracion> Grupos =
    [
        new("Acceso e identidad",
        [
            new("usuarios", "US", "Usuarios", "Cuentas y carteras asignadas", "/usuarios"),
            new("roles", "RL", "Roles", "Permisos por perfil", "/roles"),
            new("2fa", "2F", "Verificación en dos pasos", "Obligatoriedad y métodos", null)
        ]),
        new("Plataforma y conexiones",
        [
            new("delegaciones", "DL", "Delegaciones", "Sedes de la consultora", "/delegaciones"),
            new("api", "AP", "Claves API", "Acceso programático", "/configuracion/claves-api"),
            new("comercial", "CM", "Estado comercial", "Plan y consumo", "/configuracion/comercial"),
            new("integraciones", "IN", "Conexiones de integración", "M365, portales, webhooks", "/integraciones"),
            new("importar", "IM", "Importar datos", "Cuadro de Control CAE (Excel)", "/importacion")
        ]),
        new("Catálogos y datos",
        [
            new("tipos", "TD", "Tipos de documento", "Catálogo y vigencias", "/tipos-documento"),
            new("ia", "IA", "Lectura IA por cliente", "Qué se extrae y con qué umbral", "/configuracion/lectura-ia"),
            new("macros", "MA", "Macros de respuesta", "Plantillas de comunicación", "/comunicaciones/macros"),
            new("params", "PS", "Parámetros del sistema", "Umbrales del semáforo", null),
            new("retencion", "RT", "Retención de datos", "Plazos de borrado", "/retencion")
        ]),
        new("Auditoría",
        [
            new("auditoria", "AU", "Auditoría", "Quién hizo qué y cuándo", "/auditoria"),
            new("auditoria-ia", "AI", "Auditoría IA", "Lecturas y decisiones automáticas", "/auditoria-ia"),
            new("automatizaciones", "AT", "Automatizaciones", "Trabajos del sistema", null)
        ])
    ];

    private static EntradaConfiguracion? Buscar(string id) =>
        Grupos.SelectMany(g => g.Entradas).FirstOrDefault(e => e.Id == id);
}
