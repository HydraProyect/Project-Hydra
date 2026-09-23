using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.VisionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.VisionCartera.Pages;

/// <summary>
/// Visión de cartera. El alcance de cada organización lo resuelve
/// <see cref="ObtenerKpisGlobalesQuery"/> —una vuelta por tenant con
/// <c>AmbitoTenantExplicito</c>, y dentro de cada una el rol efectivo y la
/// Asignación de Cartera de ESE tenant—; la pantalla no calcula ninguno por su
/// cuenta. Lo único que sabe del alcance es lo que la consulta le devuelve por
/// organización (<see cref="ClienteRiesgoDto.SinCarteraAsignada"/>): el rol
/// del contexto activo no vale para las demás, porque un Administrador de su
/// organización puede ser Gestor CAE en la de otro Tenant propietario.
///
/// <para>
/// Cargas: la vigente es la última (<see cref="_versionCarga"/>) y la retirada
/// cancela la que siga en vuelo (<see cref="_ciclo"/>); lo que vuelve de una
/// carga que ya no es la vigente no toca el estado.
/// </para>
/// </summary>
public partial class VisionCartera : ComponentBase, IDisposable
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
    [Inject] private ILogger<VisionCartera> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosVisionCartera> Textos { get; set; } = default!;

    private KpisGlobalesDto? _kpis;
    private IReadOnlySet<Guid> _tenantsDeOrigen = new HashSet<Guid>();
    private bool _error;
    private AntiforgeryRequestToken? _token;

    /// <summary>
    /// Número de la carga vigente. Cada <see cref="CargarAsync"/> se queda con
    /// uno nuevo; lo que vuelve de una carga que ya no es la vigente no toca el
    /// estado — ni datos, ni error.
    /// </summary>
    private int _versionCarga;

    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    protected override Task OnInitializedAsync()
    {
        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
        return CargarAsync();
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool EsVigente(int version) => !_desechado && version == _versionCarga;

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var version = ++_versionCarga;
        var token = _ciclo.Token;
        _error = false;
        _kpis = null;
        StateHasChanged();

        IReadOnlyList<ClienteAutorizadoDto> autorizados;
        KpisGlobalesDto kpis;
        try
        {
            autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery(), token);
            kpis = await Mediator.Send(new ObtenerKpisGlobalesQuery(), token);
        }
        catch (Exception ex)
        {
            if (!EsVigente(version)) return;
            Logger.LogError(ex, "Error al cargar los KPIs globales de la visión de cartera.");
            _error = true;
            return;
        }

        if (!EsVigente(version)) return;

        _tenantsDeOrigen = autorizados.Where(c => c.EsOrigen).Select(c => c.TenantId).ToHashSet();
        _kpis = kpis;
    }

    private IReadOnlyList<ClienteRiesgoDto> Organizaciones => _kpis?.ClientesConMasRiesgo ?? [];

    /// <summary>
    /// Las que tienen algo que evaluar para quien mira. En una organización sin
    /// ninguna Asignación de Cartera suya, la tasa es 100 porque no hay nada que
    /// contar, no porque esté al día: no entra en verdes ni en la media.
    /// </summary>
    private IReadOnlyList<ClienteRiesgoDto> ConCartera => Organizaciones.Where(o => !o.SinCarteraAsignada).ToList();

    private IReadOnlyList<ClienteRiesgoDto> SinCartera => Organizaciones.Where(o => o.SinCarteraAsignada).ToList();

    private bool HayMediaQueMostrar => ConCartera.Count > 0;

    private int ConVencidos => Organizaciones.Count(o => o.DocumentosVencidos > 0);

    private int EnVerde => ConCartera.Count(o => o.TasaCumplimientoDocumental >= UmbralVerde);

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
                ? Textos["LineaAmbitoTodasDelegadas", total].Value
                : delegadas == 1
                    ? Textos["LineaAmbitoConPropiaUna", total, delegadas].Value
                    : Textos["LineaAmbitoConPropiaVarias", total, delegadas].Value;
        }
    }

    private string LineaAlcance => SinCartera.Count == 0
        ? Textos["LineaAlcance"].Value
        : Textos["LineaAlcanceConExcluidas", SinCartera.Count].Value;

    /// <summary>Solo cuando alguna organización llega sin Asignación de Cartera de quien mira.</summary>
    private string? AvisoSinCartera => SinCartera.Count switch
    {
        0 => null,
        1 => Textos["AvisoSinCarteraUna", SinCartera[0].Nombre].Value,
        _ => Textos["AvisoSinCarteraVarias", Enumerar(SinCartera.Select(o => o.Nombre))].Value
    };

    private string TextoSinRiesgo => SinCartera.Count == 0
        ? Textos["SinRiesgoTodas"].Value
        : Textos["SinRiesgoEnAlcance"].Value;

    // ---------------------------------------------------------------- cifras

    private string PistaVencidos => ConVencidos == 0
        ? Textos["PistaVencidosNinguna"].Value
        : Textos["PistaVencidos", ConVencidos, Organizaciones.Count].Value;

    private string ValorMedia => HayMediaQueMostrar && _kpis is not null ? $"{_kpis.TasaCumplimientoDocumentalPromedio}%" : "—";

    private string PistaMedia => HayMediaQueMostrar
        ? Textos["PistaMediaPonderada"].Value
        : Textos["PistaMediaSinCartera"].Value;

    private string DetalleMedia
    {
        get
        {
            var tasas = ConCartera.Select(o => o.TasaCumplimientoDocumental).OrderByDescending(t => t).ToList();
            var media = Textos["DetalleMedia", tasas.Count, Enumerar(tasas.Select(t => t.ToString()))].Value;
            return SinCartera.Count == 0 ? media : $"{media} {DetalleExcluidas}";
        }
    }

    private string DetalleExcluidas => SinCartera.Count == 1
        ? Textos["DetalleExcluidaUna", SinCartera[0].Nombre].Value
        : Textos["DetalleExcluidasVarias", Enumerar(SinCartera.Select(o => o.Nombre))].Value;

    private string FrasePulso => ConVencidos switch
    {
        0 => Textos["FrasePulsoNinguna", Organizaciones.Count].Value,
        1 => Textos["FrasePulsoUna", Organizaciones.Count].Value,
        _ => Textos["FrasePulsoVarias", ConVencidos, Organizaciones.Count].Value
    };

    private string DetalleEnVerde
    {
        get
        {
            var enVerde = ConCartera.Where(o => o.TasaCumplimientoDocumental >= UmbralVerde)
                .Select(o => $"{o.Nombre} {o.TasaCumplimientoDocumental}%").ToList();
            var hayExcluidas = SinCartera.Count > 0;
            var frase = (enVerde.Count == 0, hayExcluidas) switch
            {
                (true, false) => Textos["DetalleEnVerdeNinguna", ConCartera.Count].Value,
                (true, true) => Textos["DetalleEnVerdeNingunaConCartera", ConCartera.Count].Value,
                (false, false) => Textos["DetalleEnVerde", enVerde.Count, ConCartera.Count, Enumerar(enVerde)].Value,
                (false, true) => Textos["DetalleEnVerdeConCartera", enVerde.Count, ConCartera.Count, Enumerar(enVerde)].Value
            };
            return hayExcluidas ? $"{frase} {DetalleExcluidas}" : frase;
        }
    }

    private string DetalleEnRiesgo => _kpis is null ? string.Empty
        : Textos["DetalleEnRiesgo", DocumentosEnRiesgo, _kpis.DocumentosVencidos, _kpis.DocumentosUrgentes].Value;

    private static TonoBadge TonoCumplimiento(int tasa) => tasa switch
    {
        >= UmbralVerde => TonoBadge.Exito,
        >= UmbralAmbar => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private TonoBadge TonoMedia => HayMediaQueMostrar && _kpis is not null
        ? TonoCumplimiento(_kpis.TasaCumplimientoDocumentalPromedio)
        : TonoBadge.Neutro;

    private static string ClaseTono(int tasa) => $"tono-{TonoCumplimiento(tasa).ToString().ToLowerInvariant()}";

    // ---------------------------------------------------------------- tabla

    private string MetaOrganizacion(ClienteRiesgoDto organizacion) =>
        EsDeOrigen(organizacion) ? Textos["MetaTuOrganizacion"].Value : Textos["MetaDelegada"].Value;

    private string TituloVencidos(ClienteRiesgoDto o) => o.DocumentosVencidos == 0
        ? Textos["TituloVencidosNinguno", o.Nombre].Value
        : DocumentosVencidos(o);

    private string TituloUrgentes(ClienteRiesgoDto o) => o.DocumentosUrgentes == 0
        ? Textos["TituloUrgentesNinguno", o.Nombre].Value
        : DocumentosUrgentes(o);

    private string TituloCumplimiento(ClienteRiesgoDto o)
    {
        if (o.SinCarteraAsignada)
            return Textos["TituloCumplimientoSinCartera", o.Nombre].Value;

        var tramo = o.TasaCumplimientoDocumental >= UmbralVerde ? Textos["TramoVerde"].Value
            : o.TasaCumplimientoDocumental >= UmbralAmbar ? Textos["TramoAmbar"].Value
            : Textos["TramoRojo"].Value;
        return Textos["TituloCumplimiento", o.Nombre, o.TasaCumplimientoDocumental, tramo].Value;
    }

    /// <summary>«X: 1 documento vencido» / «X: N documentos vencidos» (también con N = 0).</summary>
    private string DocumentosVencidos(ClienteRiesgoDto o) => o.DocumentosVencidos == 1
        ? Textos["TituloVencidosUno", o.Nombre].Value
        : Textos["TituloVencidosVarios", o.Nombre, o.DocumentosVencidos].Value;

    /// <summary>«X: 1 documento urgente, …» / «X: N documentos urgentes, …» (también con N = 0).</summary>
    private string DocumentosUrgentes(ClienteRiesgoDto o) => o.DocumentosUrgentes == 1
        ? Textos["TituloUrgentesUno", o.Nombre].Value
        : Textos["TituloUrgentesVarios", o.Nombre, o.DocumentosUrgentes].Value;

    // ---------------------------------------------------------------- reparto

    private sealed record BarraRiesgo(
        string Nombre, string EtiquetaCorta, int X, int Centro,
        int AltoVencidos, int YVencidos, string TituloVencidos,
        int AltoUrgentes, int YUrgentes, string TituloUrgentes,
        string TextoSinBarra)
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
                    altoVencidos, yUrgentes - altoVencidos, DocumentosVencidos(o),
                    altoUrgentes, yUrgentes, DocumentosUrgentes(o),
                    o.SinCarteraAsignada ? Textos["BarraSinCartera"].Value : Textos["BarraSinRiesgo"].Value);
            }).ToList();
        }
    }

    /// <summary>Alto proporcional al mayor total; nunca por debajo de 2 si hay algo, para que un 1 no desaparezca.</summary>
    private static int Escalar(int valor, int maximo) =>
        valor <= 0 || maximo <= 0 ? 0 : Math.Max(2, (int)Math.Round(valor * (double)AltoMaximoBarra / maximo));

    private static string Acortar(string nombre) =>
        nombre.Length <= LargoEtiqueta ? nombre : string.Concat(nombre.AsSpan(0, LargoEtiqueta - 1), "…");

    /// <summary>«a, b y c»: la enumeración literal que el mockup usa en sus desgloses.</summary>
    private string Enumerar(IEnumerable<string> partes)
    {
        var lista = partes.ToList();
        return lista.Count switch
        {
            0 => string.Empty,
            1 => lista[0],
            _ => Textos["EnumeracionUltimo", string.Join(", ", lista.Take(lista.Count - 1)), lista[^1]].Value
        };
    }
}
