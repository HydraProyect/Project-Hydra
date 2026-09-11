using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

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
                ? $"{total} organizaciones que os han delegado su gestión CAE"
                : $"{total} organizaciones: la tuya y {delegadas} que os {(delegadas == 1 ? "ha" : "han")} delegado su gestión CAE";
        }
    }

    private string LineaAlcance => SinCartera.Count == 0
        ? "Cada organización cuenta solo lo que tu rol alcanza en ella"
        : $"Cada organización cuenta solo lo que tu rol alcanza en ella; en {SinCartera.Count} no tienes ninguna Asignación de Cartera";

    /// <summary>Solo cuando alguna organización llega sin Asignación de Cartera de quien mira.</summary>
    private string? AvisoSinCartera => SinCartera.Count switch
    {
        0 => null,
        1 => $"En {SinCartera[0].Nombre} no tienes ninguna Asignación de Cartera: no cuentas ningún documento suyo y su tasa no entra en la media.",
        _ => $"En {Enumerar(SinCartera.Select(o => o.Nombre))} no tienes ninguna Asignación de Cartera: no cuentas ningún documento suyo y sus tasas no entran en la media."
    };

    private string TextoSinRiesgo => SinCartera.Count == 0
        ? "Ninguna organización tiene documentación vencida ni urgente."
        : "Ninguna organización tiene documentación vencida ni urgente en lo que tu rol alcanza.";

    // ---------------------------------------------------------------- cifras

    private string PistaVencidos => ConVencidos == 0
        ? "En ninguna organización"
        : $"En {ConVencidos} de {Organizaciones.Count} organizaciones";

    private string ValorMedia => HayMediaQueMostrar && _kpis is not null ? $"{_kpis.TasaCumplimientoDocumentalPromedio}%" : "—";

    private string PistaMedia => HayMediaQueMostrar
        ? "Ponderada por volumen de documentos"
        : "Sin Asignación de Cartera en ninguna organización";

    private string DetalleMedia
    {
        get
        {
            var tasas = ConCartera.Select(o => o.TasaCumplimientoDocumental).OrderByDescending(t => t).ToList();
            var media = $"Media ponderada por el volumen de documentos con vencimiento de cada organización; tasas de las {tasas.Count} que la forman: {Enumerar(tasas.Select(t => t.ToString()))}%.";
            return SinCartera.Count == 0 ? media : $"{media} {DetalleExcluidas}";
        }
    }

    private string DetalleExcluidas => SinCartera.Count == 1
        ? $"No entra {SinCartera[0].Nombre}: sin Asignación de Cartera tuya."
        : $"No entran {Enumerar(SinCartera.Select(o => o.Nombre))}: sin Asignación de Cartera tuya.";

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
            var enVerde = ConCartera.Where(o => o.TasaCumplimientoDocumental >= UmbralVerde)
                .Select(o => $"{o.Nombre} {o.TasaCumplimientoDocumental}%").ToList();
            var conCartera = SinCartera.Count == 0 ? string.Empty : " con cartera";
            var frase = enVerde.Count == 0
                ? $"Ninguna de las {ConCartera.Count} organizaciones{conCartera} llega al 90% de cumplimiento documental."
                : $"{enVerde.Count} de {ConCartera.Count} organizaciones{conCartera} con el cumplimiento documental en el 90% o más: {Enumerar(enVerde)}.";
            return SinCartera.Count == 0 ? frase : $"{frase} {DetalleExcluidas}";
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

    private TonoBadge TonoMedia => HayMediaQueMostrar && _kpis is not null
        ? TonoCumplimiento(_kpis.TasaCumplimientoDocumentalPromedio)
        : TonoBadge.Neutro;

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
        if (o.SinCarteraAsignada)
            return $"{o.Nombre}: no tienes ninguna Asignación de Cartera aquí, así que no hay cumplimiento que medir";

        var tramo = o.TasaCumplimientoDocumental >= UmbralVerde ? "en verde, 90% o más"
            : o.TasaCumplimientoDocumental >= UmbralAmbar ? "en ámbar, entre el 70 y el 89%"
            : "en rojo, por debajo del 70%";
        return $"{o.Nombre}: {o.TasaCumplimientoDocumental}% de cumplimiento documental ({tramo})";
    }

    // ---------------------------------------------------------------- reparto

    private sealed record BarraRiesgo(
        string Nombre, string EtiquetaCorta, int X, int Centro,
        int AltoVencidos, int YVencidos, string TituloVencidos,
        int AltoUrgentes, int YUrgentes, string TituloUrgentes,
        bool SinCartera)
    {
        public bool SinRiesgo => AltoVencidos == 0 && AltoUrgentes == 0;

        public string TextoSinBarra => SinCartera ? "sin cartera" : "sin riesgo";
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
                    altoUrgentes, yUrgentes, $"{o.Nombre}: {Documentos(o.DocumentosUrgentes, "urgente", "urgentes")}, dentro de su umbral urgente",
                    o.SinCarteraAsignada);
            }).ToList();
        }
    }

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
