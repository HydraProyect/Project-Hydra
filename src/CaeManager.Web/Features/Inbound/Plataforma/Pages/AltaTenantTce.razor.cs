using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma.Queries.ObtenerIdentidadPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Plataforma.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.Plataforma.Pages;

/// <summary>
/// Tarjeta 31, «Alta del Tenant TCE», del Actor de Plataforma TALVEG. Contrata la línea Inbound del Producto Contratado
/// (B-04a, <see cref="ContratarLineaInboundCommand"/>) sobre el Tenant activo de la Sesión Privilegiada de Aprovisionamiento,
/// que es el Tenant beneficiario, y la corrige o la da de baja (#2256). El Tenant se crea antes por la vía vigente (D-A); el
/// Administrador y la Asignación de Operación Inbound van a B-04b (D-D) y aquí son shortcut.
///
/// <para>
/// <b>Autorización</b>: la pantalla lee la identidad de plataforma (<see cref="ObtenerIdentidadPlataformaQuery"/>) para no
/// ofrecer acciones que el comando va a denegar, igual que <c>Plataforma.razor</c> con el Encargo de administración. Eso no
/// autoriza nada: la autoridad es <c>AutoridadSobreLaLineaInbound</c> en cada comando. Con impersonación no se ofrece nada:
/// el Actor real no actúa como el Usuario simulado.
/// </para>
/// </summary>
public partial class AltaTenantTce
{
    internal enum AccesoPantalla { SinSesion, Impersonacion, Permitida }

    private static readonly string[] ClavesPasos = ["tenant", "operacion", "producto", "administrador", "resumen"];

    [Inject] private IMediator Mediator { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private IdentidadPlataformaDto? _identidad;
    private ClienteAutorizadoDto? _tenant;

    private int _paso = 1;
    private bool _cancelado;
    private bool _enviando;
    private string? _error;
    private Guid? _productoContratadoId;
    private string _pagador = string.Empty;
    private TramoTrabajadoresActivos _tramo = TramoTrabajadoresActivos.De1A10;
    private ModalidadSuscripcionAlta _modalidad = ModalidadSuscripcionAlta.Mensual;

    private bool _editandoLinea;
    private bool _enviandoLinea;
    private bool _confirmarBaja;
    private string? _errorLinea;
    private string? _exitoLinea;
    private string _pagadorCorr = string.Empty;
    private TramoTrabajadoresActivos _tramoCorr = TramoTrabajadoresActivos.De1A10;
    private ModalidadSuscripcionAlta _modalidadCorr = ModalidadSuscripcionAlta.Mensual;

    internal AccesoPantalla Acceso => _identidad switch
    {
        { UsuarioSimuladoId: not null } => AccesoPantalla.Impersonacion,
        { CapacidadActiva: CapacidadPrivilegio.Aprovisionamiento } => AccesoPantalla.Permitida,
        _ => AccesoPantalla.SinSesion,
    };

    private string NombreTenant => _tenant?.Nombre ?? Textos["TenantSinNombre"];

    private IReadOnlyList<PasoDefinicion> Pasos =>
    [
        new(ClavesPasos[0], Textos["PasoTenant"]),
        new(ClavesPasos[1], Textos["PasoOperacion"]),
        new(ClavesPasos[2], Textos["PasoProducto"]),
        new(ClavesPasos[3], Textos["PasoAdministrador"]),
        new(ClavesPasos[4], Textos["PasoResumen"]),
    ];

    private IReadOnlySet<string> PasosHechos => ClavesPasos.Take(_paso - 1).ToHashSet();

    private static string PasoClave(int paso) => ClavesPasos[paso - 1];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            _identidad = await Mediator.Send(new ObtenerIdentidadPlataformaQuery());
            _tenant = Acceso == AccesoPantalla.Permitida
                ? await Mediator.Send(new ObtenerTenantObjetivoSesionQuery())
                : null;
        }
        catch
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private string? FaltaEnPaso(int paso) =>
        paso == 3 && !ProductoContratado.EsPagadorStripeCustomerIdValido(_pagador.Trim())
            ? Textos["ErrorPagador"].Value
            : null;

    private void Continuar()
    {
        if (FaltaEnPaso(_paso) is { } falta)
        {
            _error = falta;
            return;
        }

        _error = null;
        _cancelado = false;
        _paso = Math.Min(5, _paso + 1);
    }

    private void Atras()
    {
        _error = null;
        _paso = Math.Max(1, _paso - 1);
    }

    private void IrA(int paso)
    {
        _error = null;
        _paso = paso;
    }

    private void Cancelar()
    {
        _paso = 1;
        _pagador = string.Empty;
        _tramo = TramoTrabajadoresActivos.De1A10;
        _modalidad = ModalidadSuscripcionAlta.Mensual;
        _error = null;
        _cancelado = true;
    }

    private async Task DarDeAltaAsync()
    {
        if (_enviando)
            return;
        if (FaltaEnPaso(3) is { } falta)
        {
            _paso = 3;
            _error = falta;
            return;
        }

        _enviando = true;
        _error = null;
        try
        {
            var resultado = await Mediator.Send(new ContratarLineaInboundCommand(_pagador.Trim(), _tramo, _modalidad));
            if (resultado.EsExitoso)
                _productoContratadoId = resultado.Valor;
            else
                _error = resultado.Error.Codigo == ErroresContratarLineaInbound.YaContratada.Codigo
                    ? $"{resultado.Error.Mensaje} {Textos["YaContratadaPista"]}"
                    : resultado.Error.Mensaje;
        }
        catch
        {
            _error = Textos["ErrorInesperado"];
        }
        finally
        {
            _enviando = false;
        }
    }

    private void EmpezarEdicion()
    {
        _errorLinea = null;
        _exitoLinea = null;
        _pagadorCorr = string.Empty;
        _tramoCorr = TramoTrabajadoresActivos.De1A10;
        _modalidadCorr = ModalidadSuscripcionAlta.Mensual;
        _editandoLinea = true;
    }

    private void CancelarEdicion()
    {
        _errorLinea = null;
        _editandoLinea = false;
    }

    private async Task GuardarCorreccionAsync()
    {
        if (_enviandoLinea)
            return;
        if (!ProductoContratado.EsPagadorStripeCustomerIdValido(_pagadorCorr.Trim()))
        {
            _errorLinea = Textos["ErrorPagador"];
            return;
        }

        _enviandoLinea = true;
        _errorLinea = null;
        try
        {
            var resultado = await Mediator.Send(new CorregirLineaInboundCommand(_pagadorCorr.Trim(), _tramoCorr, _modalidadCorr));
            if (resultado.EsExitoso)
            {
                _editandoLinea = false;
                _exitoLinea = Textos["CorreccionHecha"];
            }
            else
            {
                _errorLinea = resultado.Error.Mensaje;
            }
        }
        catch
        {
            _errorLinea = Textos["ErrorInesperado"];
        }
        finally
        {
            _enviandoLinea = false;
        }
    }

    private async Task DarDeBajaAsync()
    {
        if (_enviandoLinea)
            return;

        _enviandoLinea = true;
        _errorLinea = null;
        _exitoLinea = null;
        try
        {
            var resultado = await Mediator.Send(new DarDeBajaLineaInboundCommand());
            if (resultado.EsExitoso)
                _exitoLinea = Textos["BajaHecha"];
            else
                _errorLinea = resultado.Error.Mensaje;
        }
        catch
        {
            _errorLinea = Textos["ErrorInesperado"];
        }
        finally
        {
            _enviandoLinea = false;
            _confirmarBaja = false;
        }
    }

    private string TextoTramo(TramoTrabajadoresActivos tramo) => TextoTramo(Textos, tramo);

    private string TextoModalidad(ModalidadSuscripcionAlta modalidad) => TextoModalidad(Textos, modalidad);

    internal static string TextoTramo(IStringLocalizer<TextosAltaTenantTce> textos, TramoTrabajadoresActivos tramo)
    {
        var (minimo, maximo) = TramosTrabajadoresActivos.Rango(tramo);
        return string.Format(textos["TramoRango"].Value, minimo, maximo);
    }

    internal static string TextoModalidad(IStringLocalizer<TextosAltaTenantTce> textos, ModalidadSuscripcionAlta modalidad) =>
        modalidad == ModalidadSuscripcionAlta.Anual ? textos["ModalidadAnual"] : textos["ModalidadMensual"];
}
