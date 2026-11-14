using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>
/// Administración de los buzones de Microsoft 365 (maqueta <c>Buzones.dc.html</c>): el listado con su Responsable y el
/// interruptor del acuse automático. Conectar, desconectar y reactivar siguen en Conexiones. La página no es una frontera de
/// autorización: <c>FijarAcuseAutomaticoBuzonCommand</c> y <c>FijarResponsableBuzonCommand</c> exigen Administrador y buzón
/// visible por su cuenta; aquí solo se decide qué se enseña (quien no es Administrador ve el estado, sin controles).
/// </summary>
public partial class Buzones : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    private const string RolAdministrador = Roles.Administrador;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Buzones> Logger { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;

    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private IReadOnlyList<CuentaVisibleEnRol> _gestores = [];
    private IReadOnlyDictionary<Guid, string> _nombres = new Dictionary<Guid, string>();
    private bool _cargando = true;
    private bool _cargaFallida;
    private Guid? _procesandoId;
    private readonly CancellationTokenSource _cancelacion = new();
    private long _generacionCarga;
    private bool _dispuesto;

    // El acuse envía correo externo: activarlo pide confirmar, con las reglas a la vista. Desactivarlo no.
    private ConexionIntegracionListaDto? _acuseAActivar;
    private bool _guardandoAcuse;

    /// <summary>El texto del acuse es fijo y mientras sea borrador (pendiente de aprobación) se dice al activarlo.</summary>
    private static bool TextoDelAcuseEsBorrador => PlantillaAcuseBuzon.EsBorrador;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await CargarAsync(_cancelacion.Token);
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _cargaFallida = true;
            Logger.LogError(ex, "Error al cargar los buzones.");
        }
        finally
        {
            if (!_dispuesto) _cargando = false;
        }
    }

    /// <summary>
    /// Carga el listado y los nombres de quien responde. Una carga que falla no deja a la vista el listado anterior: sus
    /// estados pueden estar obsoletos y sus controles operarían sobre ellos, así que se vacía y la página avisa.
    /// </summary>
    private async Task CargarAsync(CancellationToken token)
    {
        var generacion = Interlocked.Increment(ref _generacionCarga);
        try
        {
            var conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(), token);
            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae, token);
            var ids = conexiones.SelectMany(c => new[] { c.ResponsableUsuarioId, c.GestorPropietarioId })
                .OfType<Guid>().Distinct().ToList();
            var nombres = await DirectorioUsuarios.ObtenerNombresVisiblesAsync(ids, token);
            if (generacion != _generacionCarga || token.IsCancellationRequested) return;
            _conexiones = conexiones;
            _gestores = gestores;
            _nombres = nombres;
            _cargaFallida = false;
        }
        catch (Exception) when (generacion == _generacionCarga && !token.IsCancellationRequested)
        {
            _conexiones = [];
            _cargaFallida = true;
            throw;
        }
    }

    private async Task RecargarSinPropagarAsync()
    {
        try
        {
            await CargarAsync(_cancelacion.Token);
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al recargar los buzones.");
        }
    }

    private string NombreDe(Guid usuarioId) => _nombres.TryGetValue(usuarioId, out var nombre) ? nombre : "—";

    /// <summary>Gestores CAE visibles y, si el responsable actual ya no lo es (cambió de rol), él también para no mostrar otro.</summary>
    private IEnumerable<(Guid Id, string NombreCompleto)> OpcionesDeResponsable(ConexionIntegracionListaDto conexion)
    {
        var opciones = _gestores.Select(g => (g.Id, g.NombreCompleto)).ToList();
        if (conexion.ResponsableUsuarioId is { } actual && opciones.All(o => o.Id != actual))
            opciones.Add((actual, NombreDe(actual)));
        return opciones;
    }

    private string DescribirTipo(ConexionIntegracionListaDto conexion) =>
        conexion.GestorPropietarioId is not null ? Textos["TipoPersonal"]
        : conexion.EsBuzonCompartido ? Textos["TipoCompartido"]
        : Textos["TipoConectado"];

    /// <summary>Solo lectura: no hay comando que cambie a qué Empresa enruta un buzón.</summary>
    private string DescribirEnruta(ConexionIntegracionListaDto conexion) =>
        conexion.GestorPropietarioId is { } propietarioId ? Textos["EnrutaPersonal", NombreDe(propietarioId)]
        : conexion.ClienteNombre ?? Textos["EnrutaOrganizacionPropia"];

    private string EtiquetaAcuse(ConexionIntegracionListaDto conexion) =>
        conexion.AcuseAutomaticoActivo ? Textos["AcuseActivado"] : Textos["AcuseDesactivado"];

    private static TonoBadge ObtenerTonoEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => TonoBadge.Exito,
        EstadoConexionIntegracion.ConError => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private string ObtenerEtiquetaEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => Textos["EstadoHabilitada"],
        EstadoConexionIntegracion.ConError => Textos["EstadoConError"],
        _ => Textos["EstadoDeshabilitada"]
    };

    // ---- Acuse automático ----

    private async Task AlternarAcuseAsync(ConexionIntegracionListaDto conexion)
    {
        if (_procesandoId is not null || _guardandoAcuse) return;

        if (!conexion.AcuseAutomaticoActivo)
        {
            _acuseAActivar = conexion;
            return;
        }

        await FijarAcuseAsync(conexion, activo: false);
    }

    private Task CerrarModalAcuse(bool visible)
    {
        if (visible || _guardandoAcuse) return Task.CompletedTask;
        _acuseAActivar = null;
        return Task.CompletedTask;
    }

    private async Task ConfirmarActivacionAcuseAsync()
    {
        if (_acuseAActivar is not { } conexion || _guardandoAcuse) return;
        _guardandoAcuse = true;
        try
        {
            await FijarAcuseAsync(conexion, activo: true);
        }
        finally
        {
            _guardandoAcuse = false;
            _acuseAActivar = null;
        }
    }

    private async Task FijarAcuseAsync(ConexionIntegracionListaDto conexion, bool activo)
    {
        _procesandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, activo));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(
                    Textos[activo ? "ToastAcuseActivado" : "ToastAcuseDesactivado", conexion.Nombre], TonoToast.Exito);

            // Con éxito o sin él se recarga: el estado que se enseña es siempre el guardado.
            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cambiar el acuse automático de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorAcuse"], TonoToast.Error);
        }
        finally
        {
            _procesandoId = null;
        }
    }

    // ---- Responsable ----

    private async Task CambiarResponsableAsync(ConexionIntegracionListaDto conexion, string? valor)
    {
        if (_procesandoId is not null) return;
        Guid? responsable = Guid.TryParse(valor, out var id) ? id : null;
        if (responsable == conexion.ResponsableUsuarioId) return;

        _procesandoId = conexion.Id;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new FijarResponsableBuzonCommand(conexion.Id, responsable));
            if (resultado.EsFallido)
                ToastService.MostrarError(resultado.Error);
            else
                ToastService.Mostrar(Textos["ToastResponsableActualizado", conexion.Nombre], TonoToast.Exito);

            await RecargarSinPropagarAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cambiar el responsable de la conexión {ConexionId}.", conexion.Id);
            ToastService.Mostrar(Textos["ErrorResponsable"], TonoToast.Error);
        }
        finally
        {
            _procesandoId = null;
        }
    }

    public void Dispose()
    {
        _dispuesto = true;
        _cancelacion.Cancel();
        _cancelacion.Dispose();
    }
}
