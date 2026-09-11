using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.VisionCartera.Pages;

public partial class VisionCartera : ComponentBase
{
    /// <summary>Mismos cortes que el tono del cumplimiento en el resto de la pantalla: verde desde 90, ámbar desde 70.</summary>
    private const int UmbralVerde = 90;
    private const int UmbralAmbar = 70;

    // Geometría del reparto del riesgo, en unidades del viewBox (enteros: un
    // double interpolado saldría con la coma decimal de es-ES y el navegador
    // descartaría el atributo — ver AnilloCumplimiento.RadioSvg).
    private const int AnchoPorOrganizacion = 92;
    private const int AnchoBarra = 34;
    private const int AltoMaximoBarra = 70;
    private const int BaseBarras = 96;
    private const int AltoGrafico = 120;
    private const int LargoEtiqueta = 10;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ILogger<VisionCartera> Logger { get; set; } = default!;

    private KpisGlobalesDto? _kpis;
    private IReadOnlySet<Guid> _tenantsDeOrigen = new HashSet<Guid>();
    private AlcanceVista _alcance;
    private bool _error;
    private AntiforgeryRequestToken? _token;

    /// <summary>
    /// Número de la carga vigente. Cada <see cref="CargarAsync"/> se queda con
    /// uno nuevo; lo que vuelve de una carga que ya no es la vigente no toca el
    /// estado — ni datos, ni error.
    /// </summary>
    private int _versionCarga;

    /// <summary>
    /// Qué acota los recuentos de cada organización. Sale del rol EFECTIVO en el
    /// contexto actual (<see cref="ICurrentUserService.ObtenerRolActualAsync"/>),
    /// que es el mismo dato con el que <c>AlcanceDatosService</c> decide el
    /// alcance dentro de cada organización que recorre la consulta — no del
    /// claim de sesión: operando un workspace delegado, un Administrador de
    /// origen puede tener ahí rol de Gestor CAE, y entonces cuenta su cartera.
    /// </summary>
    private enum AlcanceVista
    {
        TodaLaOrganizacion,
        CarteraDeSusGestores,
        CarteraPropia
    }

    protected override Task OnInitializedAsync()
    {
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
        return CargarAsync();
    }

    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        _error = false;
        _kpis = null;
        StateHasChanged();

        string? rol;
        IReadOnlyList<ClienteAutorizadoDto> autorizados;
        KpisGlobalesDto kpis;
        try
        {
            // ObtenerRolActualAsync consulta la base cuando hay un workspace
            // delegado activo: fuera de MediatR, así que pasa por la puerta
            // igual que el resto de accesos directos (ver PuertaAccesoDatos).
            rol = await PuertaAccesoDatos.EjecutarAsync(CurrentUserService.ObtenerRolActualAsync);
            autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery());
            kpis = await Mediator.Send(new ObtenerKpisGlobalesQuery());
        }
        catch (Exception ex)
        {
            if (version != _versionCarga) return;
            Logger.LogError(ex, "Error al cargar los KPIs globales de la visión de cartera.");
            _error = true;
            return;
        }

        if (version != _versionCarga) return;

        _alcance = Roles.AlcanzaTodaLaOrganizacion(rol) ? AlcanceVista.TodaLaOrganizacion
            : rol == Roles.CoordinadorCae ? AlcanceVista.CarteraDeSusGestores
            : AlcanceVista.CarteraPropia;
        _tenantsDeOrigen = autorizados.Where(c => c.EsOrigen).Select(c => c.TenantId).ToHashSet();
        _kpis = kpis;
    }

    private IReadOnlyList<ClienteRiesgoDto> Organizaciones => _kpis?.ClientesConMasRiesgo ?? [];

    private int ConVencidos => Organizaciones.Count(o => o.DocumentosVencidos > 0);

    private int EnVerde => Organizaciones.Count(o => o.TasaCumplimientoDocumental >= UmbralVerde);

    private int DocumentosEnRiesgo => _kpis is null ? 0 : _kpis.DocumentosVencidos + _kpis.DocumentosUrgentes;

    private bool EsDeOrigen(ClienteRiesgoDto organizacion) => _tenantsDeOrigen.Contains(organizacion.TenantId);

    // ---------------------------------------------------------------- cabecera

    private string LineaAmbito
    {
        get
        {
            var total = Organizaciones.Count;
            var delegadas = Organizaciones.Count(o => !EsDeOrigen(o));
            return delegadas == total
                ? $"{total} organizaciones que os han delegado su gestión CAE"
                : $"{total} organizaciones: la tuya y {delegadas} que os {(delegadas == 1 ? "ha" : "han")} delegado su gestión CAE";
        }
    }

    private string LineaAlcance => _alcance switch
    {
        AlcanceVista.TodaLaOrganizacion => "Tu rol ve cada organización completa, sin acotar por cartera",
        AlcanceVista.CarteraDeSusGestores => "Cada organización cuenta solo lo que alcanza la cartera de los Gestores CAE que coordinas",
        _ => "Cada organización cuenta solo lo que alcanza tu cartera"
    };

    private string DescripcionCartera => _alcance == AlcanceVista.CarteraDeSusGestores
        ? "la cartera de los Gestores CAE que coordinas"
        : "tu cartera";

    /// <summary>
    /// Solo para roles acotados por cartera. ObtenerKpisGlobalesQuery descarta
    /// el <c>SinCarteraAsignada</c> que ObtenerKpisDashboardQuery sí calcula
    /// por organización, así que una sin cartera llega como cero documentos y
    /// 100%: la pantalla no puede distinguirla de una al día, y lo dice en vez
    /// de afirmar lo segundo.
    /// </summary>
    private string? AvisoSinCartera => _alcance == AlcanceVista.TodaLaOrganizacion
        ? null
        : $"Donde {DescripcionCartera} no alcance nada, la organización sale con cero documentos y un 100% de cumplimiento: esta vista todavía no distingue «sin cartera» de «todo al día».";

    private string TextoSinRiesgo => _alcance == AlcanceVista.TodaLaOrganizacion
        ? "Ninguna organización tiene documentación vencida ni urgente."
        : $"Ninguna organización tiene documentación vencida ni urgente dentro de {DescripcionCartera}.";

    // ---------------------------------------------------------------- cifras

    private string PistaVencidos => ConVencidos == 0
        ? "En ninguna organización"
        : $"En {ConVencidos} de {Organizaciones.Count} organizaciones";

    private string DetalleMedia
    {
        get
        {
            var tasas = Organizaciones.Select(o => o.TasaCumplimientoDocumental).OrderByDescending(t => t).ToList();
            return $"Media simple, sin ponderar por volumen, de las tasas de las {tasas.Count} organizaciones: {Enumerar(tasas.Select(t => t.ToString()))}%.";
        }
    }

    private string FrasePulso => ConVencidos switch
    {
        0 => $"Ninguna de las {Organizaciones.Count} organizaciones tiene documentación vencida.",
        1 => $"1 de {Organizaciones.Count} organizaciones tiene documentación vencida.",
        _ => $"{ConVencidos} de {Organizaciones.Count} organizaciones tienen documentación vencida."
    };

    private string DetalleEnVerde
    {
        get
        {
            var enVerde = Organizaciones.Where(o => o.TasaCumplimientoDocumental >= UmbralVerde)
                .Select(o => $"{o.Nombre} {o.TasaCumplimientoDocumental}%").ToList();
            return enVerde.Count == 0
                ? $"Ninguna de las {Organizaciones.Count} organizaciones llega al 90% de cumplimiento documental."
                : $"{enVerde.Count} de {Organizaciones.Count} organizaciones con el cumplimiento documental en el 90% o más: {Enumerar(enVerde)}.";
        }
    }

    private string DetalleEnRiesgo => _kpis is null ? string.Empty
        : $"{DocumentosEnRiesgo} documentos en riesgo: {_kpis.DocumentosVencidos} vencidos más {_kpis.DocumentosUrgentes} urgentes.";

    private static TonoBadge TonoCumplimiento(int tasa) => tasa switch
    {
        >= UmbralVerde => TonoBadge.Exito,
        >= UmbralAmbar => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private static string ClaseTono(int tasa) => $"tono-{TonoCumplimiento(tasa).ToString().ToLowerInvariant()}";

    // ---------------------------------------------------------------- tabla

    private string MetaOrganizacion(ClienteRiesgoDto organizacion) =>
        EsDeOrigen(organizacion) ? "Tu organización" : "Os ha delegado su gestión CAE";

    private static string TituloVencidos(ClienteRiesgoDto o) => o.DocumentosVencidos == 0
        ? $"{o.Nombre}: ningún documento vencido"
        : $"{o.Nombre}: {Documentos(o.DocumentosVencidos, "vencido", "vencidos")}";

    private static string TituloUrgentes(ClienteRiesgoDto o) => o.DocumentosUrgentes == 0
        ? $"{o.Nombre}: ningún documento dentro de su umbral urgente"
        : $"{o.Nombre}: {Documentos(o.DocumentosUrgentes, "urgente", "urgentes")}, dentro de su umbral urgente";

    private static string TituloCumplimiento(ClienteRiesgoDto o)
    {
        var tramo = o.TasaCumplimientoDocumental >= UmbralVerde ? "en verde, 90% o más"
            : o.TasaCumplimientoDocumental >= UmbralAmbar ? "en ámbar, entre el 70 y el 89%"
            : "en rojo, por debajo del 70%";
        return $"{o.Nombre}: {o.TasaCumplimientoDocumental}% de cumplimiento documental ({tramo})";
    }

    // ---------------------------------------------------------------- reparto

    private sealed record BarraRiesgo(
        string Nombre, string EtiquetaCorta, int X, int Centro,
        int AltoVencidos, int YVencidos, string TituloVencidos,
        int AltoUrgentes, int YUrgentes, string TituloUrgentes)
    {
        public bool SinRiesgo => AltoVencidos == 0 && AltoUrgentes == 0;
    }

    private int AnchoGrafico => Math.Max(1, Organizaciones.Count) * AnchoPorOrganizacion;

    private IReadOnlyList<BarraRiesgo> Barras
    {
        get
        {
            var maximo = Organizaciones.Select(o => o.DocumentosVencidos + o.DocumentosUrgentes).DefaultIfEmpty(0).Max();
            return Organizaciones.Select((o, i) =>
            {
                var x = i * AnchoPorOrganizacion + (AnchoPorOrganizacion - AnchoBarra) / 2;
                var altoUrgentes = Escalar(o.DocumentosUrgentes, maximo);
                var altoVencidos = Escalar(o.DocumentosVencidos, maximo);
                var yUrgentes = BaseBarras - altoUrgentes;
                return new BarraRiesgo(
                    o.Nombre, Acortar(o.Nombre), x, x + AnchoBarra / 2,
                    altoVencidos, yUrgentes - altoVencidos, $"{o.Nombre}: {Documentos(o.DocumentosVencidos, "vencido", "vencidos")}",
                    altoUrgentes, yUrgentes, $"{o.Nombre}: {Documentos(o.DocumentosUrgentes, "urgente", "urgentes")}, dentro de su umbral urgente");
            }).ToList();
        }
    }

    private string EtiquetaGrafico =>
        "Documentos vencidos y urgentes por organización: "
        + Enumerar(Organizaciones.Select(o => $"{o.Nombre} {o.DocumentosVencidos} y {o.DocumentosUrgentes}"));

    /// <summary>Alto proporcional al mayor total; nunca por debajo de 2 si hay algo, para que un 1 no desaparezca.</summary>
    private static int Escalar(int valor, int maximo) =>
        valor <= 0 || maximo <= 0 ? 0 : Math.Max(2, (int)Math.Round(valor * (double)AltoMaximoBarra / maximo));

    private static string Acortar(string nombre) =>
        nombre.Length <= LargoEtiqueta ? nombre : string.Concat(nombre.AsSpan(0, LargoEtiqueta - 1), "…");

    private static string Documentos(int cantidad, string singular, string plural) =>
        cantidad == 1 ? $"1 documento {singular}" : $"{cantidad} documentos {plural}";

    /// <summary>«a, b y c»: la enumeración literal que el mockup usa en sus desgloses.</summary>
    private static string Enumerar(IEnumerable<string> partes)
    {
        var lista = partes.ToList();
        return lista.Count switch
        {
            0 => string.Empty,
            1 => lista[0],
            _ => $"{string.Join(", ", lista.Take(lista.Count - 1))} y {lista[^1]}"
        };
    }
}
