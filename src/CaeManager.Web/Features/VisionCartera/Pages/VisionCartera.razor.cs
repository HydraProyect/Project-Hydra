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

    /// <summary>Con cartera pero sin Centros de Trabajo ni documentos con fecha: su 100% es «sin datos».</summary>
    private IReadOnlyList<ClienteRiesgoDto> SinDatos => ConCartera.Where(o => o.SinDatos).ToList();

    /// <summary>Las que tienen algún Centro de Trabajo bloqueado (D-7): nunca en verde.</summary>
    private IReadOnlyList<ClienteRiesgoDto> ConBloqueos => Organizaciones.Where(o => o.CentrosBloqueados > 0).ToList();

    /// <summary>
    /// La media solo existe si alguna organización pesa en ella (lo dice la
    /// consulta): con cartera pero sin ningún documento con fecha en ninguna,
    /// el 100 que queda es «nada que medir».
    /// </summary>
    private bool HayMediaQueMostrar => _kpis is { HayCumplimientoDocumentalQueMedir: true };

    private int ConVencidos => Organizaciones.Count(o => o.DocumentosVencidos > 0);

    /// <summary>
    /// Verde = 90% documental o más Y <see cref="ClienteRiesgoDto.AdmiteVeredictoVerde"/>:
    /// con cartera, con datos y sin ningún Centro de Trabajo bloqueado.
    /// </summary>
    private IReadOnlyList<ClienteRiesgoDto> OrganizacionesEnVerde =>
        Organizaciones.Where(o => o.AdmiteVeredictoVerde && o.TasaCumplimientoDocumental >= UmbralVerde).ToList();

    private int EnVerde => OrganizacionesEnVerde.Count;

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

    private string PistaMedia =>
        !HayMediaQueMostrar ? (ConCartera.Count == 0 ? Textos["PistaMediaSinCartera"].Value : Textos["PistaMediaSinDatos"].Value)
        : ConBloqueos.Count > 0 ? Textos["PistaMediaConBloqueos"].Value
        : Textos["PistaMediaPonderada"].Value;

    private string DetalleSinCumplimiento => ConCartera.Count == 0
        ? Textos["SinCumplimientoDetalle"].Value
        : Textos["SinCumplimientoDetalleSinDatos"].Value;

    private string DetalleMedia
    {
        get
        {
            var tasas = ConCartera.Where(o => !o.SinDatos).Select(o => o.TasaCumplimientoDocumental).OrderByDescending(t => t).ToList();
            var partes = new List<string> { Textos["DetalleMedia", tasas.Count, Enumerar(tasas.Select(t => t.ToString()))].Value };
            if (SinCartera.Count > 0) partes.Add(DetalleExcluidas);
            if (SinDatos.Count > 0) partes.Add(DetalleSinDatos);
            return string.Join(' ', partes);
        }
    }

    private string DetalleSinDatos => SinDatos.Count == 1
        ? Textos["DetalleSinDatosUna", SinDatos[0].Nombre].Value
        : Textos["DetalleSinDatosVarias", Enumerar(SinDatos.Select(o => o.Nombre))].Value;

    /// <summary>Debajo de la media: quiénes no están al día por un bloqueo, aunque su porcentaje documental sea alto.</summary>
    private string? DetalleBloqueos => ConBloqueos.Count switch
    {
        0 => null,
        1 => Textos["DetalleBloqueosUna", ConBloqueos[0].Nombre].Value,
        _ => Textos["DetalleBloqueosVarias", Enumerar(ConBloqueos.Select(o => o.Nombre))].Value
    };

    private string PistaCentrosBloqueados => ConBloqueos.Count == 0
        ? Textos["PistaCentrosBloqueadosNinguno"].Value
        : Textos["PistaCentrosBloqueados", ConBloqueos.Count, Organizaciones.Count].Value;

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
            var enVerde = OrganizacionesEnVerde.Select(o => $"{o.Nombre} {o.TasaCumplimientoDocumental}%").ToList();
            var hayExcluidas = SinCartera.Count > 0;
            var frase = (enVerde.Count == 0, hayExcluidas) switch
            {
                (true, false) => Textos["DetalleEnVerdeNinguna", ConCartera.Count].Value,
                (true, true) => Textos["DetalleEnVerdeNingunaConCartera", ConCartera.Count].Value,
                (false, false) => Textos["DetalleEnVerde", enVerde.Count, ConCartera.Count, Enumerar(enVerde)].Value,
                (false, true) => Textos["DetalleEnVerdeConCartera", enVerde.Count, ConCartera.Count, Enumerar(enVerde)].Value
            };
            var partes = new List<string> { frase };
            var bloqueadasConCartera = ConBloqueos.Where(o => !o.SinCarteraAsignada).ToList();
            if (bloqueadasConCartera.Count == 1)
                partes.Add(Textos["DetalleNoEnVerdeBloqueoUna", bloqueadasConCartera[0].Nombre].Value);
            else if (bloqueadasConCartera.Count > 1)
                partes.Add(Textos["DetalleNoEnVerdeBloqueoVarias", Enumerar(bloqueadasConCartera.Select(o => o.Nombre))].Value);
            if (SinDatos.Count > 0) partes.Add(DetalleSinDatos);
            if (hayExcluidas) partes.Add(DetalleExcluidas);
            return string.Join(' ', partes);
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

    /// <summary>Con algún Centro de Trabajo bloqueado en la cartera, la media documental nunca se pinta en verde.</summary>
    private TonoBadge TonoMedia => HayMediaQueMostrar && _kpis is not null
        ? SinVerdeSiHayBloqueos(TonoCumplimiento(_kpis.TasaCumplimientoDocumentalPromedio), ConBloqueos.Count)
        : TonoBadge.Neutro;

    private static TonoBadge SinVerdeSiHayBloqueos(TonoBadge tono, int bloqueos) =>
        bloqueos > 0 && tono == TonoBadge.Exito ? TonoBadge.Advertencia : tono;

    /// <summary>Tono de la tasa de una fila: la de una organización con un Centro de Trabajo bloqueado no sale en verde.</summary>
    private static string ClaseTono(ClienteRiesgoDto o, int tasa) =>
        $"tono-{SinVerdeSiHayBloqueos(TonoCumplimiento(tasa), o.CentrosBloqueados).ToString().ToLowerInvariant()}";

    private TonoBadge TonoCentrosBloqueados => _kpis is { CentrosBloqueados: > 0 } ? TonoBadge.Peligro : TonoBadge.Neutro;

    private string TextoBadgeBloqueados(ClienteRiesgoDto o) => o.CentrosBloqueados == 1
        ? Textos["BadgeBloqueadosUno"].Value
        : Textos["BadgeBloqueadosVarios", o.CentrosBloqueados].Value;

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
        if (o.SinDatos)
            return Textos["TituloCumplimientoSinDatos", o.Nombre].Value;
        if (o.CentrosBloqueados > 0)
            return o.CentrosBloqueados == 1
                ? Textos["TituloCumplimientoBloqueadaUno", o.Nombre, o.TasaCumplimientoDocumental].Value
                : Textos["TituloCumplimientoBloqueadaVarios", o.Nombre, o.TasaCumplimientoDocumental, o.CentrosBloqueados].Value;

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
                    TextoSinBarra(o));
            }).ToList();
        }
    }

    /// <summary>Sin barra no es «sin riesgo» si no hay cartera, no hay datos o hay un Centro de Trabajo bloqueado.</summary>
    private string TextoSinBarra(ClienteRiesgoDto o) =>
        o.SinCarteraAsignada ? Textos["BarraSinCartera"].Value
        : o.CentrosBloqueados > 0 ? Textos["BarraBloqueada"].Value
        : o.SinDatos ? Textos["BarraSinDatos"].Value
        : Textos["BarraSinRiesgo"].Value;

    private string FilaReparto(ClienteRiesgoDto o) =>
        o.SinCarteraAsignada ? Textos["RepartoFilaSinCartera", o.Nombre].Value
        : o.SinDatos ? Textos["RepartoFilaSinDatos", o.Nombre].Value
        : o.Nombre;

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
