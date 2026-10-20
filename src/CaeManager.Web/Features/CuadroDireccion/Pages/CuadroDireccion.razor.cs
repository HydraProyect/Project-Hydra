using System.Globalization;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.CuadroDireccion.Components;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.CuadroDireccion.Pages;

/// <summary>
/// Cuadro de dirección (<c>/direccion</c>) contra su maqueta. Lee <see cref="ObtenerCuadroDireccionQuery"/>
/// de un mes natural y, aparte, la serie de los últimos meses, las conversaciones y las gestiones por día;
/// pinta lo que esas consultas miden, por Empresa (Tenant beneficiario) o todas juntas.
///
/// <para>
/// <b>Qué no hace</b>: no mide la actividad de ninguna persona: la «Ocupación de Gestores CAE» queda fuera por
/// decisión de producto. La «Cartera por Gestor CAE» sí está: cuenta las Empresas que cada Gestor CAE lleva como
/// principal en la cartera (<see cref="PrincipalDeCarteraDto"/>), que es volumen de cartera y no actividad. Y no enseña ninguna medida apoyada en menos de 3 personas o 20 conversaciones
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
public partial class CuadroDireccion : CaeManager.Web.Components.PaginaInteractiva, IDisposable, IAsyncDisposable
{
    private const int LongitudMaximaMotivo = 500;
    private const string PrefijoClavePreferencias = "cuadro-direccion:preferencias";

    /// <summary>
    /// La clave de las preferencias lleva el identificador del usuario: dos personas en el mismo navegador no se
    /// pisan las tarjetas ni los avisos vistos. Sin usuario identificado no se recuerda nada.
    /// </summary>
    private string? _clavePreferencias;
    private bool _avisoExportar;
    /// <summary>Los gráficos de la serie enseñan hasta 36 meses de historial (el máximo de la consulta) cuando lo hay.</summary>
    private const int MesesSerie = ObtenerSerieCuadroDireccionQuery.MaximoMeses;

    /// <summary>La ventana corta de la Demanda, el calendario de gestiones y el simulador.</summary>
    private const int MesesVentana = 6;
    private const int MesesAnual = 24;

    private static readonly string[] Paleta =
    [
        "var(--color-primary-500)", "var(--color-warning-500)", "var(--color-info-500)", "var(--color-success-500)",
        "var(--color-secondary-500)", "var(--color-danger-500)", "var(--color-primary-300)",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TarjetasPorPestana = new Dictionary<string, IReadOnlyList<string>>
    {
        // Visión general: los seis KPI de la maqueta, «Facturación y gestiones por mes» y «Qué requiere una decisión».
        // «Gestiones del mes» vive en el gráfico de evolución y «Trabajadores extra sin tarifa» en Facturación.
        ["general"] = ["k-fact", "k-ext", "k-trab", "k-centros", "k-revisar", "k-cumpl", "g-evolucion", "g-alertas"],
        ["facturacion"] = ["f-apilado", "f-sintarifa", "f-simulador", "f-borrador"],
        ["empresas"] = ["cl-rendimiento", "cl-pareto", "cl-contratos", "cl-tabla"],
        // Cartera por Gestor CAE: volumen de cartera por persona (Empresas asignadas). La ocupación por persona no se construye.
        ["carga"] = ["c-indice", "c-tabla", "c-criterio"],
        ["demanda"] = ["p-franja", "p-estacional", "p-heatmap", "p-calendario"],
        ["vacaciones"] = ["v-cobertura", "v-barras", "v-tabla"],
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
    private string _precioExtranjero = "10";
    private string _comoSeFactura = "extra";

    /// <summary>Los precios de ejemplo de la maqueta para el simulador: por alta de centro y por gestión de extranjero.</summary>
    private const string ColorPrimer50 = "var(--color-primary-500)";
    private const string ColorResto = "var(--color-primary-300)";
    private static readonly IReadOnlyList<double> MarcasAcumulado = [0, 50, 80, 100];

    /// <summary>El primer 50 % de la factura en el color primario y el resto en el claro (maqueta).</summary>
    private static IReadOnlyList<string> ColoresPareto(IReadOnlyList<ItemPareto> pareto) =>
        pareto.Select(ColorDePareto).ToList();

    private static string ColorDePareto(ItemPareto item) => item.Acumulado - item.Porcentaje < 50 ? ColorPrimer50 : ColorResto;

    /// <summary>El máximo del eje del índice de cartera: 150 como mínimo, o el mayor índice con un 10 % de holgura.</summary>
    private double EjeMaximoIndice => Math.Max(150, CarteraConIndice.Max(f => f.Indice!.Value) * 1.1);

    private string DeltaTrabajadores(CuadroDireccionTenantDto empresa) =>
        Delta(empresa.Cifras.TrabajadoresActivos.Valor, PrevioDeEmpresa(empresa.TenantId, TrabajadoresDe));

    private string DeltaCentros(CuadroDireccionTenantDto empresa) =>
        Delta(empresa.Cifras.CentrosActivos.Valor, PrevioDeEmpresa(empresa.TenantId, CentrosDe));

    private static int TrabajadoresDe(PuntoSerieCuadroDto p) => p.Cifras.TrabajadoresActivos.Valor;

    private static int CentrosDe(PuntoSerieCuadroDto p) => p.Cifras.CentrosActivos.Valor;
    private static readonly int[] PreciosAlta = [15, 20, 25, 30, 40];
    private static readonly int[] PreciosExtranjero = [5, 8, 10, 12, 15];

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
        {
            var usuario = (await EstadoAutenticacion).User;
            _puedeReabrir = usuario.IsInRole(Roles.DireccionCae);
            if (usuario.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } usuarioId)
                _clavePreferencias = $"{PrefijoClavePreferencias}:{usuarioId}";
        }

        await CargarAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _desechado) return;

        try
        {
            _modulo = await Js.InvokeAsync<IJSObjectReference>("import", "./js/cuadro-direccion.js");
            if (_clavePreferencias is not null)
                _preferencias = PreferenciasCuadro.Leer(await _modulo.InvokeAsync<string?>("leer", _clavePreferencias));
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

    /// <summary>Blazor llama solo a esta cuando existe: cierra la página como <see cref="Dispose"/> y suelta el módulo de JavaScript.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_modulo is null) return;

        try
        {
            await _modulo.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // El circuito ya se cerró: no hay nada que soltar.
        }
        finally
        {
            _modulo = null;
        }
    }

    /// <summary>Mientras el mes se calcula (el cuadro o su serie), no se cambia de mes: dos cargas a la vez se pisarían.</summary>
    private bool Cargando => (_cuadro is null && !_error) || _cargandoSerie;

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
        if (_ambito.Length > 0 && EmpresasEnAmbito.Count == 0)
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
            FijarInicioHistorico(serie);
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
            var dias = await Mediator.Send(new ObtenerGestionesPorDiaCuadroDireccionQuery(_anyo, _mes, MesesVentana), _ciclo.Token);
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

    // ---------------------------------------------------------------- momento (Mes 1 / Mes 6 / Año 3) y presentación

    /// <summary>El primer mes con actividad de la operación, medido una vez sobre el historial al abrir la pantalla; null si aún no se conoce.</summary>
    private DateOnly? _inicioHistorico;

    private bool _presentando;

    private sealed record Momento(string Id, string Rotulo, string Mes, int Anyo, int MesNumero, bool Activo, bool Disponible);

    /// <summary>
    /// Mes 1, Mes 6 y Año 3 de la operación (maqueta), con los meses reales: el primer mes con actividad, cinco meses
    /// después y treinta y cinco meses después. Un momento que todavía no ha llegado queda deshabilitado.
    /// </summary>
    private IReadOnlyList<Momento> Momentos
    {
        get
        {
            Momento De(string id, string rotulo, int desplazamiento)
            {
                if (_inicioHistorico is not { } inicio)
                    return new Momento(id, rotulo, "—", _anyo, _mes, false, false);

                var destino = inicio.AddMonths(desplazamiento);
                var hoy = DiaDeNegocio.Hoy();
                var llegado = destino.Year < hoy.Year || (destino.Year == hoy.Year && destino.Month <= hoy.Month);
                return new Momento(
                    id, rotulo, CuadroDireccionUi.EtiquetaMes(destino.Year, destino.Month).ToLower(CuadroDireccionUi.Castellano), destino.Year, destino.Month,
                    destino.Year == _anyo && destino.Month == _mes, llegado);
            }

            return [De("mes1", Textos["MomentoMes1"].Value, 0), De("mes6", Textos["MomentoMes6"].Value, 5), De("anio3", Textos["MomentoAnio3"].Value, 35)];
        }
    }

    private async Task IrAMomentoAsync(Momento momento)
    {
        if (!momento.Disponible || (momento.Anyo == _anyo && momento.MesNumero == _mes)) return;

        _anyo = momento.Anyo;
        _mes = momento.MesNumero;
        await CargarAsync();
        await CargarAnualSiHaceFaltaAsync();
    }

    /// <summary>El primer mes con actividad, a partir de la serie: solo se fija la primera vez (la serie de otros meses empezaría más tarde).</summary>
    private void FijarInicioHistorico(SerieCuadroDireccionDto serie)
    {
        if (_inicioHistorico is not null) return;

        var primero = serie.Total.FirstOrDefault(HayActividad);
        if (primero is not null)
            _inicioHistorico = new DateOnly(primero.Anyo, primero.Mes, 1);
    }

    private async Task AlternarPresentacionAsync()
    {
        _presentando = !_presentando;
        _personalizando = false;
        await PantallaCompletaAsync(_presentando);
    }

    private async Task PantallaCompletaAsync(bool activar)
    {
        if (_modulo is null) return;

        try
        {
            await _modulo.InvokeVoidAsync("pantallaCompleta", activar);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            // Sin pantalla completa en este navegador: la presentación sigue en la ventana.
        }
    }

    /// <summary>En la presentación, ← y → recorren las pestañas y Escape sale (como en la maqueta).</summary>
    private async Task TeclaPresentacionAsync(KeyboardEventArgs tecla)
    {
        if (!_presentando) return;

        if (tecla.Key == "Escape")
        {
            await AlternarPresentacionAsync();
            return;
        }

        if (tecla.Key is not ("ArrowRight" or "ArrowLeft")) return;

        var pestanas = Definiciones;
        var actual = pestanas.ToList().FindIndex(p => p.Id == _pestana);
        var siguiente = (actual + (tecla.Key == "ArrowRight" ? 1 : pestanas.Count - 1)) % pestanas.Count;
        await CambiarPestanaAsync(pestanas[siguiente].Id);
    }

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

    private string NombreAmbito => Equipos.FirstOrDefault(e => e.Valor == _ambito) is { } equipo
        ? Textos["AmbitoEquipo", equipo.Nombre].Value
        : string.Empty;

    /// <summary>
    /// Las pestañas con un recuento de lo que pide mirar: las alertas de «Qué requiere una decisión» en Visión
    /// general y las Empresas sin Gestor CAE principal en Cartera por Gestor CAE (como los avisos de la maqueta).
    /// </summary>
    private IReadOnlyList<PestanaDefinicion> Definiciones =>
    [
        new("general", Textos["PestanaGeneral"].Value)
        {
            Contador = HayCifras && Alertas.Count > 0 ? new ContadorPestana(Alertas.Count, Textos["GlosaAlertas", Alertas.Count].Value, EnAlerta: true) : null,
        },
        new("facturacion", Textos["PestanaFacturacion"].Value),
        new("empresas", Textos["PestanaEmpresas"].Value),
        new("carga", Textos["PestanaCartera"].Value)
        {
            Contador = HayCifras && EmpresasSinPrincipal > 0 ? new ContadorPestana(EmpresasSinPrincipal, Textos["GlosaSinPrincipal", EmpresasSinPrincipal].Value, EnAlerta: true) : null,
        },
        new("demanda", Textos["PestanaDemanda"].Value),
        new("vacaciones", Textos["PestanaVacaciones"].Value),
        new("comunicaciones", Textos["PestanaComunicaciones"].Value),
        new("comparativa", Textos["PestanaComparativa"].Value),
    ];

    // ---------------------------------------------------------------- cifras

    private bool HayCifras => _cuadro is { SinCifras: null, Tenants.Count: > 0 };

    private string EtiquetaPestanaActiva => Definiciones.Single(p => p.Id == _pestana).Etiqueta;

    private const string PrefijoEquipo = "g:";

    /// <summary>Quien lleva la cartera principal de una o más Empresas: una opción «Equipo …» del selector de ámbito.</summary>
    private sealed record EquipoDeGestor(string Valor, string Nombre);

    /// <summary>El directorio devuelve «Nombre (Rol)»: el rol ya va en el encabezado de cada columna, así que aquí solo el nombre.</summary>
    private static string NombreDePersona(string nombre) => System.Text.RegularExpressions.Regex.Replace(nombre, @"\s*\([^)]*\)\s*$", string.Empty);

    /// <summary>
    /// Los equipos del selector de ámbito: un Gestor CAE (o Coordinador CAE en un relevo) por cada persona que lleva la
    /// marca de principal de alguna Empresa, por orden alfabético. Nunca se listan las Empresas.
    /// </summary>
    private IReadOnlyList<EquipoDeGestor> Equipos => _cuadro is null
        ? []
        : _cuadro.Tenants
            .Where(t => t.PrincipalDeCartera is { Nombre.Length: > 0 })
            .Select(t => t.PrincipalDeCartera!)
            .DistinctBy(p => p.UsuarioId)
            .OrderBy(p => p.Nombre, StringComparer.CurrentCulture)
            .Select(p => new EquipoDeGestor(PrefijoEquipo + p.UsuarioId, NombreDePersona(p.Nombre)))
            .ToList();

    /// <summary>Las Empresas del ámbito elegido: todas, o las de la cartera principal del Gestor CAE elegido.</summary>
    private IReadOnlyList<CuadroDireccionTenantDto> EmpresasEnAmbito =>
        _cuadro is null
            ? []
            : _ambito.StartsWith(PrefijoEquipo, StringComparison.Ordinal) && Guid.TryParse(_ambito[PrefijoEquipo.Length..], out var gestor)
                ? _cuadro.Tenants.Where(t => t.PrincipalDeCartera?.UsuarioId == gestor).ToList()
                : _cuadro.Tenants;

    private bool HayAmbito => _ambito.Length > 0;

    private CifrasCuadroDireccionDto Cifras => HayAmbito
        ? AgregadosCuadro.SumarCifras(EmpresasEnAmbito.Select(t => t.Cifras).ToList(), _cuadro!.CorteUtc)
        : _cuadro!.Total;

    private IReadOnlyList<CuadroDireccionTenantDto> EmpresasVisibles => EmpresasEnAmbito;

    /// <summary>
    /// Cartera por Gestor CAE: las Empresas (Tenants beneficiarios) que cada persona lleva como principal en la
    /// Asignación de Cartera, con el volumen que suman (centros, trabajadores). Es volumen de cartera, no actividad
    /// ni ocupación. Las Empresas sin principal salen juntas en una última fila.
    /// </summary>
    private IReadOnlyList<FilaCartera> CarteraPorGestor
    {
        get
        {
            if (_cuadro is not { SinCifras: null }) return [];

            FilaCartera Fila(Guid? usuarioId, string nombre, string rol, IReadOnlyList<CuadroDireccionTenantDto> empresas)
            {
                var trabajadores = empresas.Sum(t => t.Cifras.TrabajadoresActivos.Valor);
                // Personas con documento de extranjero (NIE, TIE u otro que no sea DNI) en las Empresas de la cartera; sin medida en alguna, sin dato.
                int? extranjeros = empresas.Count > 0 && empresas.All(t => t.Cifras.Extranjeros is not null) ? empresas.Sum(t => t.Cifras.Extranjeros!.Valor) : null;
                return new FilaCartera(
                    usuarioId, nombre, rol,
                    empresas.Select(t => t.TenantNombre).Order(StringComparer.CurrentCulture).ToList(),
                    empresas.Sum(t => t.Cifras.CentrosActivos.Valor), trabajadores,
                    empresas.Count > 0 && empresas.All(t => t.Cifras.Gestiones is not null) ? empresas.Sum(t => t.Cifras.Gestiones!.Valor) : null,
                    extranjeros,
                    trabajadores > 0 ? empresas.Max(t => t.Cifras.TrabajadoresActivos.Valor) * 100d / trabajadores : null,
                    null);
            }

            var filas = _cuadro.Tenants
                .Where(t => t.PrincipalDeCartera is { Nombre.Length: > 0 })
                .GroupBy(t => t.PrincipalDeCartera!.UsuarioId)
                .Select(g => Fila(g.Key, NombreDePersona(g.First().PrincipalDeCartera!.Nombre), RolLegible(g.First().PrincipalDeCartera!.Rol), g.ToList()))
                .OrderBy(f => f.Nombre, StringComparer.CurrentCulture)
                .ToList();

            var indices = CalculosCuadro.IndiceDeCartera(filas.Select(f => (f.Trabajadores, f.Centros, f.Gestiones)).ToList());
            filas = filas.Select((f, i) => f with { Indice = indices[i] }).ToList();

            var sinPrincipal = _cuadro.Tenants.Where(t => t.PrincipalDeCartera is not { Nombre.Length: > 0 }).ToList();
            if (sinPrincipal.Count > 0)
                filas.Add(Fila(null, Textos["CarteraSinResponsable"].Value, string.Empty, sinPrincipal));

            return filas;
        }
    }

    /// <summary>
    /// Una fila de la cartera: volumen que lleva un Gestor CAE como principal. «Gestiones» e «Índice» son null si no hay
    /// medida (un cierre anterior a las gestiones no las guardó); «Extranjeros» cuenta los Trabajadores
    /// con documento de extranjero (NIE, TIE u otro que no sea DNI) y es null si alguna Empresa no lo midió; «MayorEmpresa» es el peso de la Empresa con más
    /// Trabajadores en los de la cartera.
    /// </summary>
    private sealed record FilaCartera(
        Guid? UsuarioId, string Nombre, string Rol, IReadOnlyList<string> Empresas, int Centros, int Trabajadores,
        int? Gestiones, int? Extranjeros, double? MayorEmpresa, int? Indice);
    /// <summary>Las carteras con Gestor CAE principal y un índice calculable, de mayor a menor índice (como la maqueta).</summary>
    private IReadOnlyList<FilaCartera> CarteraConIndice => CarteraPorGestor.Where(f => f.UsuarioId is not null && f.Indice is not null).OrderByDescending(f => f.Indice).ThenBy(f => f.Nombre, StringComparer.CurrentCulture).ToList();

    /// <summary>El nivel del índice: alto desde 125, bajo hasta 75 (maqueta).</summary>
    private static string NivelIndice(int indice) => indice >= 125 ? "alto" : indice <= 75 ? "bajo" : "medio";

    /// <summary>Empresas sin Gestor CAE principal en su cartera: lo que «Cartera por Gestor CAE» pide mirar.</summary>
    private int EmpresasSinPrincipal => _cuadro is { SinCifras: null }
        ? _cuadro.Tenants.Count(t => t.PrincipalDeCartera is not { Nombre.Length: > 0 })
        : 0;

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

    private static double MaximoDe(IReadOnlyList<CeldaSolicitudes> celdas) =>
        celdas.Where(x => !Pocas(x)).Select(x => x.PesoPorcentaje).DefaultIfEmpty(1).Max();

    private static CeldaSolicitudes? CeldaDe(IReadOnlyList<CeldaSolicitudes> celdas, int dia, int hora) =>
        celdas.FirstOrDefault(x => x.Dia == dia && x.Hora == hora);

    /// <summary>Una celda con 1 a 4 solicitudes no enseña su recuento, ni su peso, ni su color: con tan pocas, la celda es de alguien.</summary>
    private static bool Pocas(CeldaSolicitudes? celda) => celda is not null && UmbralAnonimatoCuadro.EsPoco(celda.Cantidad);

    private static int Intensidad(CeldaSolicitudes? celda, double maximo) =>
        celda is null || Pocas(celda) ? 0 : (int)Math.Round(celda.PesoPorcentaje / maximo * 100);

    private string TituloCeldaCalor(int dia, int hora, CeldaSolicitudes? celda) => Pocas(celda)
        ? Textos["MapaCalorCeldaPocas", NombreDia(dia), hora, hora + 1, UmbralAnonimatoCuadro.MinimoPorCelda].Value
        : Textos["MapaCalorCelda", NombreDia(dia), hora, hora + 1, Porcentaje(celda?.PesoPorcentaje ?? 0), Numero(celda?.Cantidad ?? 0)].Value;

    private static string TextoLectorCelda(CeldaSolicitudes? celda) => Pocas(celda) ? Pocos : Porcentaje(celda?.PesoPorcentaje ?? 0);

    private const string Pocos = "<5";

    /// <summary>Recuento de una barra de estado o de canal: por debajo del mínimo por celda sale «&lt;5», nunca la cifra.</summary>
    private static string Recuento(int valor) => UmbralAnonimatoCuadro.EsPoco(valor) ? Pocos : Numero(valor);

    private static int AnchoReservado(int valor, int maximo) => UmbralAnonimatoCuadro.EsPoco(valor) ? 0 : AnchoBarra(valor, maximo);

    private static string Numero(int valor) => valor.ToString("N0", CuadroDireccionUi.Castellano);

    private static string Porcentaje(double valor) => valor.ToString("N1", CuadroDireccionUi.Castellano) + " %";

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

    private IReadOnlyList<PuntoSerieCuadroDto> PuntosSerie => SinMesesVaciosAlPrincipio(PuntosDe(_serie));

    /// <summary>
    /// La serie pide 36 meses; los gráficos enseñan desde el primer mes con actividad (o los últimos
    /// <see cref="MesesVentana"/>, si hay menos historia), para no pintar años vacíos antes de que existiera la operación.
    /// </summary>
    private static IReadOnlyList<PuntoSerieCuadroDto> SinMesesVaciosAlPrincipio(IReadOnlyList<PuntoSerieCuadroDto> puntos)
    {
        var primero = puntos.ToList().FindIndex(HayActividad);
        var corte = Math.Min(primero < 0 ? puntos.Count : primero, puntos.Count - MesesVentana);
        return corte <= 0 ? puntos : puntos.Skip(corte).ToList();
    }

    private static bool HayActividad(PuntoSerieCuadroDto p) =>
        p.Cifras.TrabajadoresActivos.Valor > 0 || p.Cifras.CentrosActivos.Valor > 0
        || p.Cifras.DocumentosGestionados.Valor > 0 || (p.Cifras.Gestiones?.Valor ?? 0) > 0;

    private IReadOnlyList<PuntoSerieCuadroDto> PuntosDe(SerieCuadroDireccionDto? serie)
    {
        if (serie is null) return [];
        if (!HayAmbito) return serie.Total;

        var ids = EmpresasEnAmbito.Select(t => t.TenantId).ToHashSet();
        return AgregadosCuadro.SumarPuntos(
            serie.Tenants.Where(t => ids.Contains(t.TenantId)).Select(t => t.Meses).ToList(),
            _cuadro?.CorteUtc ?? DateTime.UtcNow);
    }

    private PuntoSerieCuadroDto? PuntoActual => PuntosSerie.LastOrDefault(p => p.Anyo == _anyo && p.Mes == _mes);

    private PuntoSerieCuadroDto? PuntoDeEmpresa(Guid tenantId) =>
        _serie?.Tenants.FirstOrDefault(t => t.TenantId == tenantId)?.Meses.LastOrDefault(p => p.Anyo == _anyo && p.Mes == _mes);

    private IReadOnlyList<PuntoSerieCuadroDto> SerieDeEmpresa(Guid tenantId) =>
        _serie?.Tenants.FirstOrDefault(t => t.TenantId == tenantId)?.Meses ?? [];

    /// <summary>El valor de una Empresa en el mes anterior al visible; null si la serie no lo trae.</summary>
    private int? PrevioDeEmpresa(Guid tenantId, Func<PuntoSerieCuadroDto, int> valor)
    {
        var anterior = new DateOnly(_anyo, _mes, 1).AddMonths(-1);
        var punto = SerieDeEmpresa(tenantId).LastOrDefault(p => p.Anyo == anterior.Year && p.Mes == anterior.Month);
        return punto is null ? null : valor(punto);
    }

    /// <summary>Celda de variación de la maqueta: «99 +13 /86» (valor, diferencia con el mes anterior y ese mes anterior); «=» si no cambia.</summary>
    private string Delta(int actual, int? previo)
    {
        if (previo is not { } anterior) return Numero(actual);
        var diferencia = actual - anterior;
        var signo = diferencia == 0 ? "=" : (diferencia > 0 ? "+" : "-") + Numero(Math.Abs(diferencia));
        return $"{Numero(actual)} {signo} /{Numero(anterior)}";
    }

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

    /// <summary>Rótulo corto del eje Y de un importe (maqueta): «10 mil €», «500 €», «0 €».</summary>
    private string EjeImporteEn(double valor, string moneda)
    {
        var simbolo = moneda == "EUR" ? "€" : moneda;
        return valor >= 1000
            ? Textos["EjeMiles", (valor / 1000).ToString("0.#", CuadroDireccionUi.Castellano), simbolo].Value
            : valor.ToString("0.#", CuadroDireccionUi.Castellano) + " " + simbolo;
    }

    private string EjeImporte(double valor) => EjeImporteEn(valor, Moneda);

    private string EjeImporteAnual(double valor) => _metrica == "facturacion" ? EjeImporteEn(valor, MonedaAnual) : Numero((int)valor);

    /// <summary>Rótulo corto del eje Y de un porcentaje (maqueta): «0 %», «80 %».</summary>
    private static string EjePorcentaje(double valor) => valor.ToString("0.#", CuadroDireccionUi.Castellano) + " %";

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

    /// <summary>El minigráfico de un KPI: la serie desde el primer mes con actividad (antes de él no hay nada que dibujar).</summary>
    private SerieGrafico Minigrafico(Func<PuntoSerieCuadroDto, double?> valor)
    {
        var puntos = PuntosSerie;
        var primero = puntos.ToList().FindIndex(HayActividad);
        return new(string.Empty, Paleta_(0), puntos.Select((p, i) => primero < 0 || i < primero ? null : valor(p)).ToList());
    }

    /// <summary>La variación de la facturación frente al mes anterior; null si no hay mes anterior con importe con el que compararla.</summary>
    private string? VariacionFacturacion
    {
        get
        {
            var variacion = PuntoPrevio is null ? null : CalculosCuadro.Variacion((double)ImporteDe(PuntoActual), (double)ImporteDe(PuntoPrevio));
            return variacion is { } v ? (v >= 0 ? "+" : string.Empty) + v.ToString("N1", CuadroDireccionUi.Castellano) + " %" : null;
        }
    }
    private int AltasCentroSinTarifa => PuntoActual?.Cifras.AltasCentros.Valor ?? Cifras.AltasCentros.Valor;

    private bool AltaCentroTieneTarifa => PuntosSerie.Any(p => p.UnidadesDe(ConceptoTarifaOperadorCae.CentroNuevo) > 0 || (p.Facturacion?.Any(l => l.Concepto == ConceptoTarifaOperadorCae.CentroNuevo) ?? false));

    private string ValorExtranjeros
    {
        get
        {
            var visitas = PuntoActual?.Facturacion?.Where(l => l.Concepto == ConceptoTarifaOperadorCae.TrabajadorExtranjeroGestionado).ToList();
            return visitas is { Count: > 0 } ? Numero(visitas.Sum(l => l.Unidades)) : Textos["SinDato"].Value;
        }
    }

    // ---------------------------------------------------------------- simulador

    private decimal? PrecioSimulado => decimal.TryParse(_precioSimulado.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var precio) && precio >= 0
        ? precio
        : null;

    private decimal? PrecioExtranjero => decimal.TryParse(_precioExtranjero.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var precio) && precio >= 0 ? precio : null;

    private int ExtranjerosMes => PuntoActual?.Cifras.Extranjeros?.Valor ?? 0;

    private string ImporteExtranjeroMes => Importe((PrecioExtranjero ?? 0) * ExtranjerosMes);

    private string ImporteExtranjeroVentana => Importe((PrecioExtranjero ?? 0) * PuntosVentana.Sum(p => (p.Cifras.Extranjeros?.Valor ?? 0)));

    private string ImporteSimuladoMes => Importe((PrecioSimulado ?? 0) * AltasCentroSinTarifa);

    private string ImporteSimuladoVentana => Importe((PrecioSimulado ?? 0) * PuntosVentana.Sum(p => p.Cifras.AltasCentros.Valor));

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

    /// <summary>Los últimos <see cref="MesesVentana"/> meses de la serie: la ventana de la Demanda, del calendario y del simulador (los gráficos de la serie enseñan todo el historial).</summary>
    private IReadOnlyList<PuntoSerieCuadroDto> PuntosVentana => PuntosSerie.TakeLast(MesesVentana).ToList();

    private IReadOnlyList<FranjaSolicitudesDto> FranjasPeriodo => PuntosVentana
        .SelectMany(p => p.Cifras.Solicitudes.PorFranja)
        .GroupBy(f => (f.Dia, f.Hora))
        .Select(g => new FranjaSolicitudesDto(g.Key.Dia, g.Key.Hora, g.Sum(f => f.Cantidad)))
        .ToList();

    private int SolicitudesPeriodo => PuntosVentana.Sum(p => p.Cifras.Solicitudes.Total.Valor);

    private IReadOnlyList<IReadOnlyList<CeldaDia>> SemanasCalendario()
    {
        if (PuntosSerie.Count == 0) return [];
        var primero = PuntosVentana[0];
        var desde = new DateOnly(primero.Anyo, primero.Mes, 1);
        var hasta = new DateOnly(_anyo, _mes, 1).AddMonths(1).AddDays(-1);
        var dias = _gestionesDia is null
            ? []
            : HayAmbito
                ? AgregadosCuadro.SumarDias(EmpresasEnAmbito.Select(t => _gestionesDia.Sumar(t.TenantId)))
                : _gestionesDia.Sumar();
        var porDia = dias.ToDictionary(d => d.Dia, d => d.Cantidad);
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

    private IReadOnlyList<ConteoPorEstadoDto> EstadosAbiertos => !HayAmbito
        ? _comunicaciones?.TotalPorEstado ?? []
        : (_comunicaciones?.Tenants.Where(t => EmpresasEnAmbito.Any(e => e.TenantId == t.TenantId)).SelectMany(t => t.PorEstado) ?? [])
            .GroupBy(e => e.Estado).Select(g => new ConteoPorEstadoDto(g.Key, g.Sum(e => e.Conversaciones))).ToList();

    private IReadOnlyList<ConteoPorCanalDto> Canales => !HayAmbito
        ? _comunicaciones?.TotalPorCanal ?? []
        : (_comunicaciones?.Tenants.Where(t => EmpresasEnAmbito.Any(e => e.TenantId == t.TenantId)).SelectMany(t => t.PorCanal) ?? [])
            .GroupBy(c => c.Canal).Select(g => new ConteoPorCanalDto(g.Key, g.Sum(c => c.Solicitudes))).ToList();

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
        "extranjeros" => (punto.Cifras.Extranjeros?.Valor ?? 0),
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
    private bool ComparaDosAnyos => HistoricoParaComparar && LineasAnuales().Count >= 2
        && Enumerable.Range(0, 12).Any(i => LineasAnuales().Count(l => l.Valores[i] is > 0) >= 2);

    private IReadOnlyList<(int Anyo, PuntoSerieCuadroDto[] Meses)> AcumuladoPorAnyo()
    {
        var puntos = PuntosDe(FuenteAnual);
        return new[] { _anyo, _anyo - 1 }
            .Select(anyo => (anyo, puntos.Where(p => p.Anyo == anyo && p.Mes <= _mes).ToArray()))
            .ToList();
    }

    private bool HayAcumuladoComparable => HistoricoParaComparar && AcumuladoPorAnyo() is [{ Meses.Length: > 0 }, { Meses.Length: > 0 }];

    /// <summary>
    /// Hay al menos 13 meses de operación (del primer mes con actividad al mes visible): sin ellos no hay un mes igual de
    /// otro año con el que comparar y la Comparativa dice que falta historia, sin una fila de ceros.
    /// </summary>
    private bool HistoricoParaComparar
    {
        get
        {
            var puntos = PuntosDe(FuenteAnual);
            var primero = puntos.ToList().FindIndex(HayActividad);
            return primero >= 0 && puntos.Count - primero >= 13;
        }
    }

    private string TituloAcumulado => Textos["CmpTablaTitulo", NombreMesLargo.ToLowerInvariant()].Value;

    private string SumaGestiones(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.Gestiones?.Valor ?? 0));

    private string SumaAltasTrabajadores(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.AltasTrabajadores.Valor));

    private string SumaAltasCentros(PuntoSerieCuadroDto[] meses) => Numero(meses.Sum(m => m.Cifras.AltasCentros.Valor));

    /// <summary>Visitas de Trabajador extranjero facturadas en el mes de la Empresa; sin tarifa de visita no hay dato.</summary>
    private string ExtranjerosDe(Guid tenantId) =>
        PuntoDeEmpresa(tenantId)?.Cifras.Extranjeros?.Valor is > 0 and var visitas
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

    private int AnchoCorreo => AnchoReservado(ConteoCorreo, MaximoCanal);

    private int AnchoWhatsApp => AnchoReservado(ConteoWhatsApp, MaximoCanal);

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

    /// <summary>Gestiones de extranjeros del tramo (visitas facturables de Trabajador extranjero, la misma medida que el KPI); sin tarifa de visita no hay dato.</summary>
    private string ExtranjerosAcumulados(PuntoSerieCuadroDto[] meses)
    {
        var visitas = meses.Sum(m => (m.Cifras.Extranjeros?.Valor ?? 0));
        return visitas > 0 ? Numero(visitas) : Textos["SinDato"].Value;
    }

    private void CambiarMetrica(string metrica) => _metrica = metrica;

    // ---------------------------------------------------------------- tarjetas, preferencias y exportación

    private IReadOnlyList<string> TarjetasDe(string pestana) => TarjetasPorPestana[pestana];

    private IReadOnlyList<string> TarjetasVisibles => _preferencias.Ordenar(_pestana, TarjetasDe(_pestana)).Where(_preferencias.EsVisible).ToList();

    private IReadOnlyList<string> TarjetasOrdenadas => _preferencias.Ordenar(_pestana, TarjetasDe(_pestana));

    private static bool EsKpi(string id) => id.StartsWith("k-", StringComparison.Ordinal);

    private sealed record GrupoTarjetas(bool EsKpi, IReadOnlyList<string> Ids);

    /// <summary>
    /// Las tarjetas visibles en rachas: los KPI seguidos van en su propia fila (en móvil, con desplazamiento horizontal) y el resto
    /// en la rejilla de tarjetas. Si Personalizar mezcla el orden, cada racha conserva su sitio.
    /// </summary>
    private IReadOnlyList<GrupoTarjetas> GruposVisibles
    {
        get
        {
            var grupos = new List<GrupoTarjetas>();
            var actual = new List<string>();
            var actualEsKpi = false;
            foreach (var id in TarjetasVisibles)
            {
                if (actual.Count > 0 && EsKpi(id) != actualEsKpi)
                {
                    grupos.Add(new GrupoTarjetas(actualEsKpi, actual));
                    actual = [];
                }

                actualEsKpi = EsKpi(id);
                actual.Add(id);
            }

            if (actual.Count > 0) grupos.Add(new GrupoTarjetas(actualEsKpi, actual));
            return grupos;
        }
    }

    private string TituloTarjeta(string id) => id switch
    {
        "k-fact" => Textos["KpiFacturacion"].Value,
        "k-ext" => Textos["KpiExtranjeros"].Value,
        "k-trab" => Textos["KpiTrabajadores"].Value,
        "k-centros" => Textos["KpiCentros"].Value,
        "k-revisar" => Textos["KpiRevisar"].Value,
        "k-cumpl" => Textos["KpiCumplimiento"].Value,
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
        "c-indice" => Textos["CarteraIndiceTitulo"].Value,
        "c-tabla" => Textos["CarteraTitulo"].Value,
        "c-criterio" => Textos["CarteraCriterioTitulo"].Value,
        "v-barras" => Textos["VacacionesPorGestorTitulo"].Value,
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
        if (_modulo is null || _clavePreferencias is null) return;
        try
        {
            await _modulo.InvokeVoidAsync("guardar", _clavePreferencias, _preferencias.Serializar());
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
        _avisoExportar = false;
        if (_modulo is null)
        {
            _avisoExportar = true;
            return;
        }

        try
        {
            await _modulo.InvokeVoidAsync("descargar", Textos["NombreFichero", tabla, $"{_anyo}-{_mes:00}"].Value, CalculosCuadro.Csv(filas), "text/csv;charset=utf-8");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            Logger.LogWarning(ex, "No se pudo descargar la tabla {Tabla} del Cuadro de dirección.", tabla);
            _avisoExportar = true;
        }
    }

    private Task ExportarEmpresasAsync() => DescargarCsvAsync("empresas",
        new[]
        {
            (IReadOnlyList<string>)[
                Textos["ColumnaEmpresa"].Value, Textos["ColumnaTrabajadores"].Value,
                Textos["ColumnaCentros"].Value, Textos["ColumnaRotacion"].Value, Textos["ColumnaFacturaMes"].Value,
                Textos["ColumnaIngresoGestion"].Value, Textos["ColumnaCumplimiento"].Value],
        }.Concat(FilasEmpresas().Select(f => (IReadOnlyList<string>)[
            f.Empresa.TenantNombre, Numero(f.Empresa.Cifras.TrabajadoresActivos.Valor),
            Numero(f.Empresa.Cifras.CentrosActivos.Valor), f.Rotacion is { } r ? Porcentaje(r) : "—", ImporteOSinDato(f.Facturacion),
            f.IngresoPorGestion is { } i ? Importe((decimal)i) : "—",
            f.Empresa.Cifras.Cumplimiento.SinDatos ? "—" : f.Empresa.Cifras.Cumplimiento.Porcentaje + " %"])));

    private Task ExportarCarteraAsync() => DescargarCsvAsync("cartera",
        new[] { (IReadOnlyList<string>)[Textos["ColumnaGestorCae"].Value, Textos["ColumnaEquipo"].Value, Textos["ColumnaEmpresas"].Value, Textos["ColumnaEmpresasTitulares"].Value, Textos["ColumnaCentros"].Value, Textos["ColumnaTrabajadores"].Value, Textos["ColumnaExtranjeros"].Value, Textos["ColumnaGestiones"].Value, Textos["ColumnaMayorEmpresa"].Value, Textos["ColumnaIndice"].Value] }
            .Concat(CarteraPorGestor.Select(f => (IReadOnlyList<string>)[
                f.Nombre, SinDato, Numero(f.Empresas.Count), SinDato, Numero(f.Centros), Numero(f.Trabajadores),
                CeldaNumero(f.Extranjeros), CeldaNumero(f.Gestiones), f.MayorEmpresa is { } m ? Porcentaje(m) : SinDato, CeldaNumero(f.Indice)])));

    private string SinDato => Textos["SinDato"].Value;

    private string CeldaNumero(int? valor) => valor is { } v ? Numero(v) : SinDato;

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
        _reabrirEmpresa = EmpresasEnAmbito is [var una] && EmpresasCerradas.Any(t => t.TenantId == una.TenantId)
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
