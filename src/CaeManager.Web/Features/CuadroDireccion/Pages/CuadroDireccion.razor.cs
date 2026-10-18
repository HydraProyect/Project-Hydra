using System.Globalization;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Facturacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.CuadroDireccion.Components;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.CuadroDireccion.Pages;

/// <summary>
/// Cuadro de dirección (<c>/direccion</c>) contra su maqueta. Lee <see cref="ObtenerCuadroDireccionQuery"/>
/// de un mes natural y, aparte, la serie de los últimos meses, las conversaciones y las gestiones por día;
/// pinta lo que esas consultas miden, por Empresa (Tenant beneficiario) o todas juntas.
///
/// <para>
/// <b>Qué no hace</b>: no mide a ninguna persona (ni «Ocupación» ni «Cartera por Gestor CAE»). El «Responde» de
/// cada Empresa es quien lleva la marca de principal en la cartera (<see cref="PrincipalDeCarteraDto"/>), no una
/// medida de actividad. Y no enseña ninguna medida apoyada en menos de 3 personas o 20 conversaciones
/// (<see cref="UmbralAnonimatoCuadro"/>). Lo que ningún modelo guarda todavía (contrato y cuota, vacaciones,
/// tiempos de respuesta, intención CAE, equipos) sale como el estado vacío de la maqueta, sin cifras inventadas.
/// </para>
///
/// <para>
/// <b>Reabrir un mes cerrado</b> solo se ve y se usa con el rol Dirección CAE, y exige un motivo de hasta 500
/// caracteres; el servidor lo exige otra vez (<see cref="ReabrirMesCuadroDireccionCommandHandler"/>).
/// </para>
///
/// <para>
/// <b>Preferencias</b> (tarjetas ocultas, orden, etiquetas de datos, avisos de cierre vistos) se recuerdan en este
/// navegador (<see cref="PreferenciasCuadro"/>); son presentación y la pantalla funciona igual sin ellas.
/// </para>
/// </summary>
public partial class CuadroDireccion : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private const int LongitudMaximaMotivo = 500;
    private const string ClavePreferencias = "cuadro-direccion:preferencias";
    private const int MesesSerie = 6;
    private const int MesesAnual = 24;

    private static readonly string[] Paleta =
    [
        "var(--color-primary-500)", "var(--color-warning-500)", "var(--color-info-500)", "var(--color-success-500)",
        "var(--color-secondary-500)", "var(--color-danger-500)", "var(--color-primary-300)",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TarjetasPorPestana = new Dictionary<string, IReadOnlyList<string>>
    {
        ["general"] = ["k-fact", "k-ext", "k-trab", "k-centros", "k-revisar", "k-sintarifa", "k-cumpl", "k-gestiones", "g-alertas", "g-evolucion"],
        ["facturacion"] = ["f-apilado", "f-sintarifa", "f-simulador", "f-borrador"],
        ["empresas"] = ["cl-rendimiento", "cl-pareto", "cl-contratos", "cl-tabla"],
        ["demanda"] = ["p-franja", "p-heatmap", "p-calendario", "p-estacional"],
        ["vacaciones"] = ["v-barras", "v-cobertura", "v-tabla"],
        ["comunicaciones"] = ["cm-kpis", "cm-equipos", "cm-estados", "cm-intencion", "cm-canal", "cm-criterio"],
        ["comparativa"] = ["cmp-linea", "cmp-tabla"],
    };

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<CuadroDireccion> Logger { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private CuadroDireccionDto? _cuadro;
    private int _anyo;
    private int _mes;
    private string _ambito = string.Empty;
    private string _pestana = "general";
    private bool _error;
    private bool _puedeReabrir;

    private SerieCuadroDireccionDto? _serie;
    private bool _cargandoSerie;
    private bool _errorSerie;
    private ComunicacionesCuadroDireccionDto? _comunicaciones;
    private bool _cargandoComunicaciones;
    private GestionesPorDiaCuadroDireccionDto? _gestionesDia;
    private SerieCuadroDireccionDto? _anual;
    private bool _cargandoAnual;
    private SerieCuadroDireccionDto? _anualFacturacion;
    private bool _cargandoAnualFacturacion;
    private string _metrica = "gestiones";

    private PreferenciasCuadro _preferencias = new();
    private IJSObjectReference? _modulo;
    private bool _personalizando;
    private string? _arrastrando;

    private string _ordenColumna = "facturacion";
    private bool _ordenAscendente;
    private readonly HashSet<Guid> _expandidas = [];
    private string _precioSimulado = "50";

    /// <summary>Las medidas por equipo (Outbound, Validación, Fincas) aún no existen en el servidor: ver <see cref="TablaEquiposComunicaciones"/>.</summary>
    private readonly IReadOnlyList<FilaEquipoComunicaciones> _equipos = [];

    private bool _reaperturaAbierta;
    private bool _reabriendo;
    private string _reabrirEmpresa = string.Empty;
    private string _motivo = string.Empty;
    private string? _errorReapertura;
    private bool _motivoTocado;

    private int _versionCarga;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    protected override async Task OnInitializedAsync()
    {
        var hoy = DiaDeNegocio.Hoy();
        _anyo = hoy.Year;
        _mes = hoy.Month;
        if (EstadoAutenticacion is not null)
            _puedeReabrir = (await EstadoAutenticacion).User.IsInRole(Roles.DireccionCae);
        await CargarAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _desechado) return;

        try
        {
            _modulo = await Js.InvokeAsync<IJSObjectReference>("import", "./js/cuadro-direccion.js");
            _preferencias = PreferenciasCuadro.Leer(await _modulo.InvokeAsync<string?>("leer", ClavePreferencias));
            StateHasChanged();
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            // Sin almacenamiento en este navegador: la pantalla sigue con los valores por defecto.
            _modulo = null;
        }
    }

    public void Dispose()
    {
        if (_desechado) return;
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool EsVigente(int version) => !_desechado && version == _versionCarga;

    private async Task CargarAsync()
    {
        if (_desechado) return;

        var version = ++_versionCarga;
        _error = false;
        _cuadro = null;
        _serie = null;
        _comunicaciones = null;
        _gestionesDia = null;
        _anual = null;
        _anualFacturacion = null;
        StateHasChanged();

        CuadroDireccionDto cuadro;
        try
        {
            cuadro = await Mediator.Send(new ObtenerCuadroDireccionQuery(_anyo, _mes), _ciclo.Token);
        }
        catch (Exception ex)
        {
            if (!EsVigente(version)) return;
            Logger.LogError(ex, "Error al cargar el Cuadro de dirección.");
            _error = true;
            return;
        }

        if (!EsVigente(version)) return;
        _cuadro = cuadro;
        if (_ambito.Length > 0 && !cuadro.Tenants.Any(t => t.TenantId.ToString() == _ambito))
            _ambito = string.Empty;

        if (cuadro.SinCifras is null)
            await CargarSecundariosAsync(version);
    }

    /// <summary>
    /// Lo que el mes no trae por sí solo: la serie de los últimos meses con facturación estimada, las conversaciones
    /// y las gestiones por día. Cada pieza falla sola (un aviso en su tarjeta) sin tirar el cuadro del mes.
    /// </summary>
    private async Task CargarSecundariosAsync(int version)
    {
        _cargandoSerie = true;
        _cargandoComunicaciones = true;
        _errorSerie = false;
        StateHasChanged();

        try
        {
            var serie = await Mediator.Send(new ObtenerSerieCuadroDireccionQuery(_anyo, _mes, MesesSerie, ConFacturacion: true), _ciclo.Token);
            if (!EsVigente(version)) return;
            _serie = serie;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error al cargar la serie del Cuadro de dirección.");
            _errorSerie = true;
        }
        finally
        {
            _cargandoSerie = false;
        }

        try
        {
            var comunicaciones = await Mediator.Send(new ObtenerComunicacionesCuadroDireccionQuery(_anyo, _mes), _ciclo.Token);
            if (!EsVigente(version)) return;
            _comunicaciones = comunicaciones;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error al cargar las conversaciones del Cuadro de dirección.");
        }
        finally
        {
            _cargandoComunicaciones = false;
        }

        try
        {
            var dias = await Mediator.Send(new ObtenerGestionesPorDiaCuadroDireccionQuery(_anyo, _mes, MesesSerie), _ciclo.Token);
            if (!EsVigente(version)) return;
            _gestionesDia = dias;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Error al cargar las gestiones por día del Cuadro de dirección.");
        }

        if (EsVigente(version))
            StateHasChanged();
    }

    /// <summary>Los 24 meses de la Demanda (estacionalidad) y de la Comparativa se piden al abrir la pestaña, no antes.</summary>
    private async Task CargarAnualSiHaceFaltaAsync()
    {
        if (_cuadro is not { SinCifras: null } || _desechado) return;

        var version = _versionCarga;
        if (_pestana == "demanda" && _anual is null && !_cargandoAnual)
        {
            _cargandoAnual = true;
            try
            {
                var serie = await Mediator.Send(new ObtenerSerieCuadroDireccionQuery(_anyo, _mes, MesesAnual), _ciclo.Token);
                if (EsVigente(version)) _anual = serie;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Error al cargar los 24 meses de la Demanda.");
            }
            finally
            {
                _cargandoAnual = false;
            }
        }
        else if (_pestana == "comparativa" && _anualFacturacion is null && !_cargandoAnualFacturacion)
        {
            _cargandoAnualFacturacion = true;
            try
            {
                var serie = await Mediator.Send(new ObtenerSerieCuadroDireccionQuery(_anyo, _mes, MesesAnual, ConFacturacion: true), _ciclo.Token);
                if (EsVigente(version)) _anualFacturacion = serie;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Error al cargar la Comparativa anual.");
            }
            finally
            {
                _cargandoAnualFacturacion = false;
            }
        }
    }

    // ---------------------------------------------------------------- mes y ámbito

    private bool PuedeIrAlAnterior => _anyo > MesCuadroDireccion.AnyoMinimo || _mes > 1;

    private bool PuedeIrAlSiguiente
    {
        get
        {
            var hoy = DiaDeNegocio.Hoy();
            return _anyo < hoy.Year || (_anyo == hoy.Year && _mes < hoy.Month);
        }
    }

    private Task IrAlMesAnteriorAsync() => MoverMesAsync(-1);

    private Task IrAlMesSiguienteAsync() => MoverMesAsync(1);

    private async Task MoverMesAsync(int meses)
    {
        var destino = new DateOnly(_anyo, _mes, 1).AddMonths(meses);
        _anyo = destino.Year;
        _mes = destino.Month;
        await CargarAsync();
        await CargarAnualSiHaceFaltaAsync();
    }

    private void CambiarAmbito(string valor) => _ambito = valor;

    private async Task CambiarPestanaAsync(string id)
    {
        _pestana = id;
        _personalizando = false;
        await CargarAnualSiHaceFaltaAsync();
    }

    private string EtiquetaMes => CuadroDireccionUi.EtiquetaMes(_anyo, _mes);

    private string NombreMesLargo
    {
        get
        {
            var mes = _cuadro is { SinCifras: null } ? _cuadro.Mes : _mes;
            var nombre = CuadroDireccionUi.Castellano.DateTimeFormat.GetMonthName(Math.Clamp(mes, 1, 12));
            return char.ToUpper(nombre[0], CuadroDireccionUi.Castellano) + nombre[1..];
        }
    }

    /// <summary>El «1/9» del aviso de cierre: el día 1 del mes siguiente.</summary>
    private string DiaCierre => $"1/{(_mes % 12) + 1}";

    private string NombreAmbito => _cuadro?.Tenants.FirstOrDefault(t => t.TenantId.ToString() == _ambito)?.TenantNombre ?? string.Empty;

    private IReadOnlyList<PestanaDefinicion> Definiciones =>
    [
        new("general", Textos["PestanaGeneral"].Value),
        new("facturacion", Textos["PestanaFacturacion"].Value),
        new("empresas", Textos["PestanaEmpresas"].Value),
        new("demanda", Textos["PestanaDemanda"].Value),
        new("vacaciones", Textos["PestanaVacaciones"].Value),
        new("comunicaciones", Textos["PestanaComunicaciones"].Value),
        new("comparativa", Textos["PestanaComparativa"].Value),
    ];

    // ---------------------------------------------------------------- cifras

    private bool HayCifras => _cuadro is { SinCifras: null, Tenants.Count: > 0 };

    private CuadroDireccionTenantDto? EmpresaElegida => _ambito.Length == 0
        ? null
        : _cuadro?.Tenants.FirstOrDefault(t => t.TenantId.ToString() == _ambito);

    private CifrasCuadroDireccionDto Cifras => EmpresaElegida?.Cifras ?? _cuadro!.Total;

    private IReadOnlyList<CuadroDireccionTenantDto> EmpresasVisibles =>
        EmpresaElegida is { } una ? [una] : _cuadro!.Tenants;

    private EstadoCifraMensual EstadoVigente => _cuadro is null ? EstadoCifraMensual.Parcial : Cifras.TrabajadoresActivos.Estado;

    private string EstadoDato => DataEstado(EstadoVigente);

    private string EstadoTexto => TextoEstado(EstadoVigente);

    private static string DataEstado(EstadoCifraMensual estado) => estado switch
    {
        EstadoCifraMensual.Existe => "cerrado",
        EstadoCifraMensual.Estimado => "estimado",
        _ => "parcial",
    };

    private string TextoEstado(EstadoCifraMensual estado) => estado switch
    {
        EstadoCifraMensual.Existe => Textos["EstadoCerrado"].Value,
        EstadoCifraMensual.Estimado => Textos["EstadoEstimado"].Value,
        _ => Textos["EstadoParcial"].Value,
    };

    private string EstadoTitulo => EstadoVigente switch
    {
        EstadoCifraMensual.Existe => Textos["EstadoCerradoTitulo"].Value,
        EstadoCifraMensual.Estimado => Textos["EstadoEstimadoTitulo"].Value,
        _ => Textos["EstadoParcialTitulo"].Value,
    };

    private static readonly int[] DiasLaborables = [1, 2, 3, 4, 5];

    private string NombreDia(int dia) => dia switch
    {
        1 => Textos["DiaAbreviado1"].Value,
        2 => Textos["DiaAbreviado2"].Value,
        3 => Textos["DiaAbreviado3"].Value,
        4 => Textos["DiaAbreviado4"].Value,
        _ => Textos["DiaAbreviado5"].Value,
    };

    private static IReadOnlyList<int> HorasDe(IReadOnlyList<CeldaSolicitudes> celdas) =>
        Enumerable.Range(celdas.Min(x => x.Hora), celdas.Max(x => x.Hora) - celdas.Min(x => x.Hora) + 1).ToList();

    private static double MaximoDe(IReadOnlyList<CeldaSolicitudes> celdas) => celdas.Max(x => x.PesoPorcentaje);

    private static CeldaSolicitudes? CeldaDe(IReadOnlyList<CeldaSolicitudes> celdas, int dia, int hora) =>
        celdas.FirstOrDefault(x => x.Dia == dia && x.Hora == hora);

    private static int Intensidad(CeldaSolicitudes? celda, double maximo) =>
        celda is null ? 0 : (int)Math.Round(celda.PesoPorcentaje / maximo * 100);

    private static string Numero(int valor) => valor.ToString("N0", CuadroDireccionUi.Castellano);

    private static string Porcentaje(double valor) => valor.ToString("N1", CuadroDireccionUi.Castellano) + " %";

    private string Responde(PrincipalDeCarteraDto? principal) => principal is null || string.IsNullOrWhiteSpace(principal.Nombre)
        ? "—"
        : $"{principal.Nombre} ({RolLegible(principal.Rol)})";

    private string RolLegible(string rol) => rol switch
    {
        Roles.CoordinadorCae => Textos["RolCoordinadorCae"].Value,
        Roles.GestorCae => Textos["RolGestorCae"].Value,
        _ => rol,
    };

    private static string ClaseCumplimiento(int porcentaje) => porcentaje switch
    {
        >= 90 => "cuadro-chip-ok",
        >= 80 => "cuadro-chip-aviso",
        _ => "cuadro-chip-malo",
    };

    // ---------------------------------------------------------------- serie (últimos meses, facturación estimada)

    private IReadOnlyList<PuntoSerieCuadroDto> PuntosSerie => PuntosDe(_serie);

    private IReadOnlyList<PuntoSerieCuadroDto> PuntosDe(SerieCuadroDireccionDto? serie) =>
        serie is null
            ? []
            : EmpresaElegida is { } una
                ? serie.Tenants.FirstOrDefault(t => t.TenantId == una.TenantId)?.Meses ?? []
                : serie.Total;

    private PuntoSerieCuadroDto? PuntoActual => PuntosSerie.LastOrDefault(p => p.Anyo == _anyo && p.Mes == _mes);

    private PuntoSerieCuadroDto? PuntoDeEmpresa(Guid tenantId) =>
        _serie?.Tenants.FirstOrDefault(t => t.TenantId == tenantId)?.Meses.LastOrDefault(p => p.Anyo == _anyo && p.Mes == _mes);

    private IReadOnlyList<PuntoSerieCuadroDto> SerieDeEmpresa(Guid tenantId) =>
        _serie?.Tenants.FirstOrDefault(t => t.TenantId == tenantId)?.Meses ?? [];

    private PuntoSerieCuadroDto? PuntoPrevio => PuntoActual is null || PuntosSerie.Count < 2 ? null : PuntosSerie[^2];

    private IReadOnlyList<string> EtiquetasSerie => PuntosSerie.Select(p => CuadroDireccionUi.EtiquetaMes(p.Anyo, p.Mes)).ToList();

    private bool HayTarifas => PuntosSerie.Any(p => p.Facturacion is { Count: > 0 });

    /// <summary>La moneda con más importe estimado; las demás no se suman a ella.</summary>
    private string Moneda => PuntosSerie
        .SelectMany(p => p.Facturacion ?? [])
        .GroupBy(l => l.MonedaIso)
        .OrderByDescending(g => g.Sum(l => l.Importe))
        .Select(g => g.Key)
        .FirstOrDefault() ?? "EUR";

    private string? OtraMoneda => PuntosSerie
        .SelectMany(p => p.Facturacion ?? [])
        .Select(l => l.MonedaIso)
        .Distinct()
        .FirstOrDefault(m => m != Moneda);

    private static string Importe(decimal valor, string moneda) =>
        moneda == "EUR" ? valor.ToString("N0", CuadroDireccionUi.Castellano) + " €" : valor.ToString("N0", CuadroDireccionUi.Castellano) + " " + moneda;

    private string Importe(decimal valor) => Importe(valor, Moneda);

    private decimal ImporteDe(PuntoSerieCuadroDto? punto) => punto?.ImporteEn(Moneda) ?? 0m;

    private static string Paleta_(int i) => Paleta[i % Paleta.Length];

    private IReadOnlyList<SerieGrafico> SeriesFacturacionPorConcepto()
    {
        var moneda = Moneda;
        var lineas = PuntosSerie.SelectMany(p => p.Facturacion ?? []).Where(l => l.MonedaIso == moneda).ToList();
        return lineas
            .Select(l => l.Concepto)
            .Distinct()
            .OrderBy(c => c)
            .Select((concepto, i) => new SerieGrafico(
                lineas.First(l => l.Concepto == concepto).ConceptoNombre,
                Paleta_(i),
                PuntosSerie
                    .Select(p => (double?)p.Facturacion!.Where(l => l.Concepto == concepto && l.MonedaIso == moneda).Sum(l => (double)l.Importe))
                    .ToList()))
            .ToList();
    }

    private IReadOnlyList<SerieGrafico> BarraFacturacion() =>
        [new(Textos["SerieFacturacion"].Value, Paleta_(0), PuntosSerie.Select(p => (double?)ImporteDe(p)).ToList())];

    private SerieGrafico LineaGestiones() =>
        new(Textos["SerieGestiones"].Value, Paleta_(1), PuntosSerie.Select(p => p.Cifras.Gestiones is { } g ? (double?)g.Valor : null).ToList());

    private SerieGrafico Minigrafico(Func<PuntoSerieCuadroDto, double?> valor) =>
        new(string.Empty, Paleta_(0), PuntosSerie.Select(valor).ToList());

    private string VariacionFacturacion
    {
        get
        {
            var variacion = PuntoPrevio is null ? null : CalculosCuadro.Variacion((double)ImporteDe(PuntoActual), (double)ImporteDe(PuntoPrevio));
            return variacion is { } v
                ? (v >= 0 ? "+" : string.Empty) + v.ToString("N1", CuadroDireccionUi.Castellano) + " %"
                : Textos["VariacionSinPrevio"].Value;
        }
    }

    private int AltasCentroSinTarifa => PuntoActual?.Cifras.AltasCentros.Valor ?? Cifras.AltasCentros.Valor;

    private bool AltaCentroTieneTarifa => PuntosSerie.Any(p => p.UnidadesDe(ConceptoFacturable.AltaCentro) > 0 || (p.Facturacion?.Any(l => l.Concepto == ConceptoFacturable.AltaCentro) ?? false));

    private string ValorExtranjeros
    {
        get
        {
            var visitas = PuntoActual?.Facturacion?.Where(l => l.Concepto == ConceptoFacturable.VisitaTrabajadorExtranjero).ToList();
            return visitas is { Count: > 0 } ? Numero(visitas.Sum(l => l.Unidades)) : Textos["SinDato"].Value;
        }
    }

    private string ValorGestiones => Cifras.Gestiones is { } g ? Numero(g.Valor) : Textos["SinDato"].Value;

    // ---------------------------------------------------------------- simulador

    private decimal? PrecioSimulado => decimal.TryParse(_precioSimulado.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var precio) && precio >= 0
        ? precio
        : null;

    private string ImporteSimuladoMes => Importe((PrecioSimulado ?? 0) * AltasCentroSinTarifa);

    private string ImporteSimuladoVentana => Importe((PrecioSimulado ?? 0) * PuntosSerie.Sum(p => p.Cifras.AltasCentros.Valor));

    // ---------------------------------------------------------------- alertas

    private sealed record AlertaCuadro(string Empresa, int Porcentaje, int Prioridad);

    private IReadOnlyList<AlertaCuadro> Alertas => EmpresasVisibles
        .Where(e => !e.Cifras.Cumplimiento.SinDatos && e.Cifras.Cumplimiento.Porcentaje < 80)
        .Select(e => new AlertaCuadro(e.TenantNombre, e.Cifras.Cumplimiento.Porcentaje, e.Cifras.Cumplimiento.Porcentaje < 70 ? 1 : 2))
        .OrderBy(a => a.Prioridad).ThenBy(a => a.Porcentaje)
        .Take(8)
        .ToList();

    // ---------------------------------------------------------------- Empresas (tabla, dispersión, concentración)

    private sealed record FilaEmpresa(
        CuadroDireccionTenantDto Empresa, double? Rotacion, decimal Facturacion, double? IngresoPorGestion, int? Gestiones);

    private IReadOnlyList<FilaEmpresa> FilasEmpresas()
    {
        var filas = EmpresasVisibles.Select(e =>
        {
            var punto = _serie?.Tenants.FirstOrDefault(t => t.TenantId == e.TenantId)?.Meses.LastOrDefault(p => p.Anyo == _anyo && p.Mes == _mes);
            var facturacion = punto?.ImporteEn(Moneda) ?? 0m;
            var gestiones = e.Cifras.Gestiones?.Valor;
            return new FilaEmpresa(
                e, CalculosCuadro.Rotacion(e.Cifras.AltasTrabajadores.Valor, e.Cifras.BajasTrabajadores.Valor, e.Cifras.TrabajadoresActivos.Valor),
                facturacion, HayTarifas ? CalculosCuadro.IngresoPorGestion(facturacion, gestiones) : null, gestiones);
        }).ToList();

        double? Clave(FilaEmpresa f) => _ordenColumna switch
        {
            "trabajadores" => f.Empresa.Cifras.TrabajadoresActivos.Valor,
            "centros" => f.Empresa.Cifras.CentrosActivos.Valor,
            "rotacion" => f.Rotacion,
            "facturacion" => (double)f.Facturacion,
            "ingreso" => f.IngresoPorGestion,
            "cumplimiento" => f.Empresa.Cifras.Cumplimiento.SinDatos ? null : f.Empresa.Cifras.Cumplimiento.Porcentaje,
            _ => null,
        };

        if (_ordenColumna is "empresa")
            return (_ordenAscendente ? filas.OrderBy(f => f.Empresa.TenantNombre, StringComparer.CurrentCulture) : filas.OrderByDescending(f => f.Empresa.TenantNombre, StringComparer.CurrentCulture)).ToList();

        var conClave = filas.Where(f => Clave(f) is not null);
        var sinClave = filas.Where(f => Clave(f) is null);
        return (_ordenAscendente ? conClave.OrderBy(Clave) : conClave.OrderByDescending(Clave)).Concat(sinClave).ToList();
    }

    private void Ordenar(string columna)
    {
        if (_ordenColumna == columna)
            _ordenAscendente = !_ordenAscendente;
        else
        {
            _ordenColumna = columna;
            _ordenAscendente = columna == "empresa";
        }
    }

    private string AriaOrden(string columna) => _ordenColumna != columna ? "none" : _ordenAscendente ? "ascending" : "descending";

    private void AlternarFila(Guid tenantId)
    {
        if (!_expandidas.Remove(tenantId)) _expandidas.Add(tenantId);
    }

    private IReadOnlyList<PuntoDispersion> PuntosDispersion()
    {
        var filas = FilasEmpresas().Where(f => f.IngresoPorGestion is not null && f.Gestiones is > 0).ToList();
        if (filas.Count == 0) return [];
        var mediana = filas.Select(f => f.IngresoPorGestion!.Value).OrderBy(v => v).ElementAt(filas.Count / 2);
        return filas
            .Select(f => new PuntoDispersion(
                f.Empresa.TenantNombre, f.Gestiones!.Value, f.IngresoPorGestion!.Value, (double)f.Facturacion, f.IngresoPorGestion < mediana * 0.8))
            .ToList();
    }

    private string DescribirPunto(PuntoDispersion punto) =>
        Textos["RendimientoPunto", punto.Nombre, Numero((int)punto.X), Importe((decimal)punto.Y), Importe((decimal)punto.Tamano)].Value;

    private IReadOnlyList<ItemPareto> Concentracion() =>
        CalculosCuadro.Pareto(FilasEmpresas().Select(f => (f.Empresa.TenantNombre, f.Facturacion)));

    // ---------------------------------------------------------------- Demanda (mapa de calor, franja, calendario, estacionalidad)

    private IReadOnlyList<FranjaSolicitudesDto> FranjasPeriodo => PuntosSerie
        .SelectMany(p => p.Cifras.Solicitudes.PorFranja)
        .GroupBy(f => (f.Dia, f.Hora))
        .Select(g => new FranjaSolicitudesDto(g.Key.Dia, g.Key.Hora, g.Sum(f => f.Cantidad)))
        .ToList();

    private int SolicitudesPeriodo => PuntosSerie.Sum(p => p.Cifras.Solicitudes.Total.Valor);

    private IReadOnlyList<IReadOnlyList<CeldaDia>> SemanasCalendario()
    {
        if (PuntosSerie.Count == 0) return [];
        var primero = PuntosSerie[0];
        var desde = new DateOnly(primero.Anyo, primero.Mes, 1);
        var hasta = new DateOnly(_anyo, _mes, 1).AddMonths(1).AddDays(-1);
        var porDia = (_gestionesDia?.Sumar(EmpresaElegida?.TenantId) ?? []).ToDictionary(d => d.Dia, d => d.Cantidad);
        return CalculosCuadro.Semanas(desde, hasta, porDia);
    }

    private static int IntensidadCalendario(CeldaDia celda, int maximo) => maximo <= 0 ? 0 : celda.Cantidad * 100 / maximo;

    private IReadOnlyList<double?> GestionesPorDiaSegunMes()
    {
        var puntos = PuntosDe(_anual);
        return CalculosCuadro.GestionesPorDiaLaborableSegunMes(
            puntos.Where(p => p.Estado != EstadoCifraMensual.Parcial).Select(p => (p.Anyo, p.Mes, p.Cifras.Gestiones is { } g ? (int?)g.Valor : null)));
    }

    private string NombreMesCorto(int mes) => CuadroDireccionUi.EtiquetaMes(2000, mes)[..3];

    private string NombreMesDelAnyo(int indice) => NombreMesCorto(indice + 1);

    private IReadOnlyList<string> MesesDelAnyo => Enumerable.Range(1, 12).Select(NombreMesCorto).ToList();

    private string HorasFranja(FranjaRefuerzo franja) =>
        Textos["FranjaTexto", franja.HoraInicio, franja.HoraFin, string.Join(" y ", franja.DiasPico.Select(NombreDia))].Value;

    // ---------------------------------------------------------------- Comunicaciones

    private bool HayComunicaciones => _comunicaciones is not null;

    private IReadOnlyList<ConteoPorEstadoDto> EstadosAbiertos => EmpresaElegida is { } una
        ? _comunicaciones?.Tenants.FirstOrDefault(t => t.TenantId == una.TenantId)?.PorEstado ?? []
        : _comunicaciones?.TotalPorEstado ?? [];

    private IReadOnlyList<ConteoPorCanalDto> Canales => EmpresaElegida is { } una
        ? _comunicaciones?.Tenants.FirstOrDefault(t => t.TenantId == una.TenantId)?.PorCanal ?? []
        : _comunicaciones?.TotalPorCanal ?? [];

    private int ConversacionesAbiertas => EstadosAbiertos.Sum(e => e.Conversaciones);

    private int SolicitudesPorCanal => Canales.Sum(c => c.Solicitudes);

    private int ConteoEstado(EstadoConversacion estado) => EstadosAbiertos.Where(e => e.Estado == estado).Sum(e => e.Conversaciones);

    private int ConteoCanal(CanalConversacion canal) => Canales.Where(c => c.Canal == canal).Sum(c => c.Solicitudes);

    private static int AnchoBarra(int valor, int maximo) => maximo <= 0 ? 0 : valor * 100 / maximo;

    private string NombreEstado(EstadoConversacion estado) => estado switch
    {
        EstadoConversacion.Abierta => Textos["EstadoAbierta"].Value,
        EstadoConversacion.Pendiente => Textos["EstadoPendiente"].Value,
        EstadoConversacion.EsperandoRespuestaExterna => Textos["EstadoEsperandoExterna"].Value,
        _ => Textos["EstadoEnEsperaTercero"].Value,
    };

    // ---------------------------------------------------------------- Comparativa anual

    private SerieCuadroDireccionDto? FuenteAnual => _anualFacturacion;

    private double? ValorMetrica(PuntoSerieCuadroDto punto) => _metrica switch
    {
        "facturacion" => punto.Facturacion is null ? null : (double)(punto.ImporteEn(MonedaAnual) ?? 0m),
        "altas" => punto.Cifras.AltasTrabajadores.Valor,
        "documentos" => punto.Cifras.DocumentosGestionados.Valor,
        _ => punto.Cifras.Gestiones?.Valor,
    };

    private string MonedaAnual => PuntosDe(FuenteAnual)
        .SelectMany(p => p.Facturacion ?? [])
        .GroupBy(l => l.MonedaIso)
        .OrderByDescending(g => g.Sum(l => l.Importe))
        .Select(g => g.Key)
        .FirstOrDefault() ?? "EUR";

    private IReadOnlyList<int> AnyosAnual => PuntosDe(FuenteAnual).Select(p => p.Anyo).Distinct().OrderByDescending(a => a).Take(3).ToList();

    private IReadOnlyList<SerieGrafico> LineasAnuales()
    {
        var puntos = PuntosDe(FuenteAnual);
        return AnyosAnual
            .Select((anyo, i) => new SerieGrafico(
                anyo.ToString(CultureInfo.InvariantCulture), Paleta_(i),
                Enumerable.Range(1, 12)
                    .Select(mes => puntos.FirstOrDefault(p => p.Anyo == anyo && p.Mes == mes) is { } p ? ValorMetrica(p) : null)
                    .ToList()))
            .ToList();
    }

    /// <summary>Hay un mismo mes en dos años distintos con algo medido: sin eso no hay nada que comparar.</summary>
    private bool ComparaDosAnyos => LineasAnuales().Count >= 2
        && Enumerable.Range(0, 12).Any(i => LineasAnuales().Count(l => l.Valores[i] is > 0) >= 2);

    private IReadOnlyList<(int Anyo, PuntoSerieCuadroDto[] Meses)> AcumuladoPorAnyo()
    {
        var puntos = PuntosDe(FuenteAnual);
        return new[] { _anyo, _anyo - 1 }
            .Select(anyo => (anyo, puntos.Where(p => p.Anyo == anyo && p.Mes <= _mes).ToArray()))
            .ToList();
    }

    private bool HayAcumuladoComparable => AcumuladoPorAnyo() is [{ Meses.Length: > 0 }, { Meses.Length: > 0 }];

    private string TituloAcumulado => Textos["CmpTablaTitulo", NombreMesLargo.ToLowerInvariant()].Value;

    private string SumaGestiones(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.Gestiones?.Valor ?? 0));

    private string SumaAltasTrabajadores(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.AltasTrabajadores.Valor));

    private string SumaAltasCentros(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.AltasCentros.Valor));

    /// <summary>Visitas de Trabajador extranjero facturadas en el mes de la Empresa; sin tarifa de visita no hay dato.</summary>
    private string ExtranjerosDe(Guid tenantId) =>
        PuntoDeEmpresa(tenantId)?.UnidadesDe(ConceptoFacturable.VisitaTrabajadorExtranjero) is > 0 and var visitas
            ? Numero(visitas)
            : Textos["SinDato"].Value;

    private int MesesTerminadosEnHistorico => PuntosDe(_anual).Count(p => p.Estado != EstadoCifraMensual.Parcial);

    private string NombresMesesPico(IEnumerable<int> picos) => string.Join(", ", picos.OrderBy(i => i).Select(NombreMesDelAnyo));

    private string NombreMesTranquilo(IReadOnlyList<double?> cargas) =>
        NombreMesDelAnyo(cargas.Select((v, i) => (v, i)).Where(x => x.v is not null).OrderBy(x => x.v).First().i);

    private int MaximoEstadoAbierto => EstadosAbiertos.Select(x => x.Conversaciones).DefaultIfEmpty(0).Max();

    private int ConteoCorreo => ConteoCanal(CanalConversacion.Correo);

    private int ConteoWhatsApp => ConteoCanal(CanalConversacion.WhatsApp);

    private int MaximoCanal => Math.Max(ConteoCorreo, ConteoWhatsApp);

    private int AnchoCorreo => AnchoBarra(ConteoCorreo, MaximoCanal);

    private int AnchoWhatsApp => AnchoBarra(ConteoWhatsApp, MaximoCanal);

    private decimal ImporteAnual(PuntoSerieCuadroDto[] meses) => meses.Sum(m => m.ImporteEn(MonedaAnual) ?? 0m);

    private string FacturacionAcumulada(PuntoSerieCuadroDto[] meses) =>
        meses.All(m => m.Facturacion is { Count: > 0 }) || meses.Any(m => m.Facturacion is { Count: > 0 })
            ? Importe(ImporteAnual(meses), MonedaAnual)
            : Textos["SinDato"].Value;

    private string VariacionAcumulada(PuntoSerieCuadroDto[] actual, PuntoSerieCuadroDto[] previo)
    {
        var v = CalculosCuadro.Variacion((double)ImporteAnual(actual), (double)ImporteAnual(previo));
        return v is { } valor ? (valor >= 0 ? "+" : string.Empty) + valor.ToString("N1", CuadroDireccionUi.Castellano) + " %" : Textos["SinDato"].Value;
    }

    private string IngresoPorEmpresaMes(PuntoSerieCuadroDto[] meses)
    {
        var empresas = Math.Max(EmpresasVisibles.Count, 1);
        return meses.Length == 0 || ImporteAnual(meses) == 0 ? Textos["SinDato"].Value : Importe(ImporteAnual(meses) / (empresas * meses.Length), MonedaAnual);
    }

    private string ExtranjerosAcumulados(PuntoSerieCuadroDto[] meses)
    {
        var visitas = meses.Sum(m => m.UnidadesDe(ConceptoFacturable.VisitaTrabajadorExtranjero));
        return visitas > 0 ? Numero(visitas) : Textos["SinDato"].Value;
    }

    private void CambiarMetrica(string metrica) => _metrica = metrica;

    // ---------------------------------------------------------------- tarjetas, preferencias y exportación

    private IReadOnlyList<string> TarjetasDe(string pestana) => TarjetasPorPestana[pestana];

    private IReadOnlyList<string> TarjetasVisibles => _preferencias.Ordenar(_pestana, TarjetasDe(_pestana)).Where(_preferencias.EsVisible).ToList();

    private IReadOnlyList<string> TarjetasOrdenadas => _preferencias.Ordenar(_pestana, TarjetasDe(_pestana));

    private static bool EsKpi(string id) => id.StartsWith("k-", StringComparison.Ordinal);

    private string TituloTarjeta(string id) => id switch
    {
        "k-fact" => Textos["KpiFacturacion"].Value,
        "k-ext" => Textos["KpiExtranjeros"].Value,
        "k-trab" => Textos["KpiTrabajadores"].Value,
        "k-centros" => Textos["KpiCentros"].Value,
        "k-revisar" => Textos["KpiRevisar"].Value,
        "k-sintarifa" => Textos["KpiSinTarifa"].Value,
        "k-cumpl" => Textos["KpiCumplimiento"].Value,
        "k-gestiones" => Textos["KpiGestiones"].Value,
        "g-alertas" => Textos["AlertasTitulo"].Value,
        "g-evolucion" => Textos["EvolucionTitulo"].Value,
        "f-apilado" => Textos["ApiladoTitulo"].Value,
        "f-sintarifa" => Textos["SinTarifaTitulo"].Value,
        "f-simulador" => Textos["SimuladorTitulo"].Value,
        "f-borrador" => Textos["BorradorTitulo"].Value,
        "cl-rendimiento" => Textos["RendimientoTitulo"].Value,
        "cl-pareto" => Textos["ParetoTitulo"].Value,
        "cl-contratos" => Textos["ContratosTitulo"].Value,
        "cl-tabla" => Textos["EmpresasTitulo"].Value,
        "p-franja" => Textos["FranjaTitulo"].Value,
        "p-heatmap" => Textos["MapaCalorTitulo"].Value,
        "p-calendario" => Textos["CalendarioTitulo"].Value,
        "p-estacional" => Textos["EstacionalTitulo"].Value,
        "v-barras" => Textos["VacacionesBarrasTitulo"].Value,
        "v-cobertura" => Textos["VacacionesCoberturaTitulo"].Value,
        "v-tabla" => Textos["VacacionesSaldoTitulo"].Value,
        "cm-kpis" => Textos["CmKpisTitulo"].Value,
        "cm-equipos" => Textos["EquiposTitulo"].Value,
        "cm-estados" => Textos["CmEstadosTitulo"].Value,
        "cm-intencion" => Textos["CmIntencionTitulo"].Value,
        "cm-canal" => Textos["CmCanalTitulo"].Value,
        "cm-criterio" => Textos["CriterioTitulo"].Value,
        "cmp-linea" => Textos["CmpLineaTitulo"].Value,
        _ => TituloAcumulado,
    };

    private async Task GuardarPreferenciasAsync()
    {
        if (_modulo is null) return;
        try
        {
            await _modulo.InvokeVoidAsync("guardar", ClavePreferencias, _preferencias.Serializar());
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            // El navegador no deja guardar: la preferencia vale hasta que se cierre la pantalla.
        }
    }

    private void AlternarPersonalizar() => _personalizando = !_personalizando;

    private async Task AlternarTarjetaAsync(string id)
    {
        _preferencias.Alternar(id);
        await GuardarPreferenciasAsync();
    }

    private async Task MoverTarjetaAsync(string id, int delta)
    {
        _preferencias.Mover(_pestana, TarjetasDe(_pestana), id, delta);
        await GuardarPreferenciasAsync();
    }

    private void EmpezarArrastre(string id) => _arrastrando = id;

    private async Task SoltarArrastreAsync(string antesDe)
    {
        if (_arrastrando is { } id)
        {
            _preferencias.Colocar(_pestana, TarjetasDe(_pestana), id, antesDe);
            await GuardarPreferenciasAsync();
        }

        _arrastrando = null;
    }

    private async Task RestablecerPestanaAsync()
    {
        _preferencias.Restablecer(_pestana, TarjetasDe(_pestana));
        await GuardarPreferenciasAsync();
    }

    private async Task AlternarEtiquetasAsync()
    {
        _preferencias.Etiquetas = !_preferencias.Etiquetas;
        await GuardarPreferenciasAsync();
    }

    private string ClaveAviso => $"{_anyo}-{_mes}";

    private bool AvisoCierreVisto => _preferencias.AvisosVistos.Contains(ClaveAviso);

    private async Task CerrarAvisoAsync()
    {
        if (!_preferencias.AvisosVistos.Contains(ClaveAviso))
            _preferencias.AvisosVistos.Add(ClaveAviso);
        await GuardarPreferenciasAsync();
    }

    private async Task DescargarCsvAsync(string tabla, IEnumerable<IReadOnlyList<string>> filas)
    {
        if (_modulo is null) return;
        try
        {
            await _modulo.InvokeVoidAsync("descargar", Textos["NombreFichero", tabla, $"{_anyo}-{_mes:00}"].Value, CalculosCuadro.Csv(filas), "text/csv;charset=utf-8");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            Logger.LogWarning(ex, "No se pudo descargar la tabla {Tabla} del Cuadro de dirección.", tabla);
        }
    }

    private Task ExportarEmpresasAsync() => DescargarCsvAsync("empresas",
        new[]
        {
            (IReadOnlyList<string>)[
                Textos["ColumnaEmpresa"].Value, Textos["ColumnaResponde"].Value, Textos["ColumnaTrabajadores"].Value,
                Textos["ColumnaCentros"].Value, Textos["ColumnaRotacion"].Value, Textos["ColumnaFacturaMes"].Value,
                Textos["ColumnaIngresoGestion"].Value, Textos["ColumnaCumplimiento"].Value],
        }.Concat(FilasEmpresas().Select(f => (IReadOnlyList<string>)[
            f.Empresa.TenantNombre, Responde(f.Empresa.PrincipalDeCartera), Numero(f.Empresa.Cifras.TrabajadoresActivos.Valor),
            Numero(f.Empresa.Cifras.CentrosActivos.Valor), f.Rotacion is { } r ? Porcentaje(r) : "—", ImporteOSinDato(f.Facturacion),
            f.IngresoPorGestion is { } i ? Importe((decimal)i) : "—",
            f.Empresa.Cifras.Cumplimiento.SinDatos ? "—" : f.Empresa.Cifras.Cumplimiento.Porcentaje + " %"])));

    private Task ExportarBorradorAsync() => DescargarCsvAsync("borrador",
        new[] { (IReadOnlyList<string>)[Textos["ColumnaEmpresa"].Value, Textos["ColumnaFacturable"].Value, Textos["ColumnaAltasCentro"].Value] }
            .Concat(FilasEmpresas().Select(f => (IReadOnlyList<string>)[
                f.Empresa.TenantNombre, ImporteOSinDato(f.Facturacion), Numero(f.Empresa.Cifras.AltasCentros.Valor)])));

    /// <summary>Sin tarifas visibles (o sin autoridad sobre ellas) el importe no se inventa: sale «—».</summary>
    private string ImporteOSinDato(decimal importe) => HayTarifas ? Importe(importe) : Textos["SinDato"].Value;

    private Task ExportarComparativaAsync() => DescargarCsvAsync("comparativa",
        new[]
        {
            (IReadOnlyList<string>)[
                Textos["ColumnaAnio"].Value, Textos["MetricaFacturacion"].Value, Textos["ColumnaGestiones"].Value,
                Textos["ColumnaAltasTrab"].Value, Textos["ColumnaAltasCentro"].Value, Textos["ColumnaDocumentosAcumulados"].Value],
        }.Concat(AcumuladoPorAnyo().Select(a => (IReadOnlyList<string>)[
            a.Anyo.ToString(CultureInfo.InvariantCulture), FacturacionAcumulada(a.Meses),
            SumaGestiones(a.Meses), SumaAltasTrabajadores(a.Meses),
            SumaAltasCentros(a.Meses), Numero(a.Meses.Sum(m => m.Cifras.DocumentosGestionados.Valor))])));

    // ---------------------------------------------------------------- reapertura

    /// <summary>Las Empresas cuyo mes visible tiene cierre persistido: son las únicas que se pueden reabrir.</summary>
    private IReadOnlyList<CuadroDireccionTenantDto> EmpresasCerradas => _cuadro is { SinCifras: null }
        ? _cuadro.Tenants.Where(t => t.Cifras.TrabajadoresActivos.Estado == EstadoCifraMensual.Existe).ToList()
        : [];

    private string? ErrorMotivo => !_motivoTocado ? null : MotivoValido ? null : Textos["ReabrirMotivoObligatorio", LongitudMaximaMotivo].Value;

    private bool MotivoValido => !string.IsNullOrWhiteSpace(_motivo) && _motivo.Length <= LongitudMaximaMotivo;

    private Task AbrirReaperturaAsync()
    {
        if (!_puedeReabrir) return Task.CompletedTask;
        _reabrirEmpresa = EmpresaElegida is { } una && EmpresasCerradas.Any(t => t.TenantId == una.TenantId)
            ? una.TenantId.ToString()
            : EmpresasCerradas.FirstOrDefault()?.TenantId.ToString() ?? string.Empty;
        _motivo = string.Empty;
        _motivoTocado = false;
        _errorReapertura = null;
        _reaperturaAbierta = true;
        return Task.CompletedTask;
    }

    private void CerrarReapertura(bool visible)
    {
        if (!visible && !_reabriendo) _reaperturaAbierta = false;
    }

    private async Task ReabrirAsync()
    {
        if (!_puedeReabrir || _reabriendo || _desechado) return;

        _motivoTocado = true;
        if (!MotivoValido || !Guid.TryParse(_reabrirEmpresa, out var tenantId))
        {
            _errorReapertura = Textos["ReabrirMotivoObligatorio", LongitudMaximaMotivo].Value;
            return;
        }

        _reabriendo = true;
        _errorReapertura = null;
        try
        {
            var resultado = await Mediator.Send(new ReabrirMesCuadroDireccionCommand(tenantId, _anyo, _mes, _motivo.Trim()), _ciclo.Token);
            if (resultado.EsFallido)
            {
                _errorReapertura = resultado.Error.Mensaje;
                return;
            }

            _reaperturaAbierta = false;
            ToastService.Mostrar(Textos["ReabrirOk"], TonoToast.Exito);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al reabrir un mes del Cuadro de dirección.");
            _errorReapertura = Textos["ReabrirError"].Value;
            return;
        }
        finally
        {
            _reabriendo = false;
        }

        await CargarAsync();
    }
}
