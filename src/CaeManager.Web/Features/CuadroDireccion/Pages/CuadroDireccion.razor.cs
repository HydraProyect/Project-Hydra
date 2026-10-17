using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.CuadroDireccion.Components;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.CuadroDireccion.Pages;

/// <summary>
/// Cuadro de dirección (<c>/direccion</c>) contra su maqueta. Lee <see cref="ObtenerCuadroDireccionQuery"/>
/// de un mes natural y pinta lo que esa consulta mide, por Empresa (Tenant beneficiario) o todas juntas.
///
/// <para>
/// <b>Qué no hace</b>: no mide a ninguna persona. El «Responde» de cada Empresa es quien lleva la marca de
/// principal en la cartera (<see cref="PrincipalDeCarteraDto"/>), no una medida de actividad. Y no enseña
/// ninguna medida apoyada en menos de 3 personas o 20 conversaciones (<see cref="UmbralAnonimatoCuadro"/>).
/// </para>
///
/// <para>
/// <b>Reabrir un mes cerrado</b> solo se ve y se usa con el rol Dirección CAE, y exige un motivo de hasta 500
/// caracteres; el servidor lo exige otra vez (<see cref="ReabrirMesCuadroDireccionCommandHandler"/>).
/// </para>
/// </summary>
public partial class CuadroDireccion : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private const int LongitudMaximaMotivo = 500;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<CuadroDireccion> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private CuadroDireccionDto? _cuadro;
    private int _anyo;
    private int _mes;
    private string _ambito = string.Empty;
    private string _pestana = "general";
    private bool _error;
    private bool _puedeReabrir;
    private bool _avisoCierreVisto;

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
        _avisoCierreVisto = false;
        await CargarAsync();
    }

    private void CambiarAmbito(string valor) => _ambito = valor;

    private void CambiarPestana(string id) => _pestana = id;

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
        new("empresas", Textos["PestanaEmpresas"].Value),
        new("demanda", Textos["PestanaDemanda"].Value),
        new("comunicaciones", Textos["PestanaComunicaciones"].Value),
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
