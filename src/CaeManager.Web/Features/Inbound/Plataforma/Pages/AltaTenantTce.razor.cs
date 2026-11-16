using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma.Queries.ObtenerIdentidadPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
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
/// <b>Autorización</b>: la pantalla lee la identidad de plataforma (<see cref="ObtenerIdentidadPlataformaQuery"/>) y el
/// Tenant activo (<see cref="ObtenerTenantObjetivoSesionQuery"/>) para no ofrecer acciones que el comando va a denegar, igual
/// que <c>Plataforma.razor</c> con el Encargo de administración. Eso no autoriza nada: la autoridad es
/// <c>AutoridadSobreLaLineaInbound</c> en cada comando, y ningún comando recibe un Tenant: actúan sobre el Tenant activo.
/// Con impersonación no se ofrece nada: el Actor real no actúa como el Usuario simulado.
/// </para>
/// </summary>
public partial class AltaTenantTce
{
    internal enum AccesoPantalla { SinSesion, Impersonacion, TenantNoResuelto, Permitida }

    private sealed record FilaResumen(string Clave, string Valor);

    private sealed record SeccionResumen(int Paso, string Titulo, IReadOnlyList<FilaResumen> Filas);

    private sealed record LineaContratada(string Pagador, TramoTrabajadoresActivos Tramo, ModalidadSuscripcionAlta Modalidad);

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
    private LineaContratada? _contratado;
    private string _pagador = string.Empty;
    private TramoTrabajadoresActivos? _tramo;
    private ModalidadSuscripcionAlta? _modalidad;

    private bool _editandoLinea;
    private bool _enviandoLinea;

    // Un solo envío a la vez en toda la pantalla: el alta y la línea comparten el Mediator y el DbContext del circuito.
    private bool Ocupado => _enviando || _enviandoLinea;
    private bool _confirmarBaja;
    private string? _errorLinea;
    private string? _exitoLinea;
    private string _pagadorCorr = string.Empty;
    private TramoTrabajadoresActivos? _tramoCorr;
    private ModalidadSuscripcionAlta? _modalidadCorr;

    /// <summary>
    /// Con Usuario simulado, nada. Sin la capacidad, o si el Tenant activo no se resuelve (no hay Tenant activo o la
    /// política no da acceso a él), tampoco: los tres comandos lo denegarían.
    /// </summary>
    internal AccesoPantalla Acceso => _identidad switch
    {
        { UsuarioSimuladoId: not null } => AccesoPantalla.Impersonacion,
        { CapacidadActiva: CapacidadPrivilegio.Aprovisionamiento } => _tenant is null ? AccesoPantalla.TenantNoResuelto : AccesoPantalla.Permitida,
        _ => AccesoPantalla.SinSesion,
    };

    private string NombreTenant => _tenant?.Nombre ?? string.Empty;

    private string PasoActualClave => ClavesPasos[_paso - 1];

    private string TextoPrimario => _paso < 5 ? Textos["Continuar"] : Textos["DarDeAlta"];

    private string TituloHecho => string.Format(Textos["HechoTitulo"].Value, NombreTenant);

    private string TextoHecho => _contratado is { } c
        ? string.Format(Textos["HechoTexto"].Value, NombreTenant, c.Pagador, TextoTramo(c.Tramo), TextoModalidad(c.Modalidad))
        : string.Empty;

    private string MensajeBaja => string.Format(Textos["BajaMensaje"].Value, NombreTenant);

    private IReadOnlyList<PasoDefinicion> Pasos =>
    [
        new(ClavesPasos[0], Textos["PasoTenant"]),
        new(ClavesPasos[1], Textos["PasoOperacion"]),
        new(ClavesPasos[2], Textos["PasoProducto"]),
        new(ClavesPasos[3], Textos["PasoAdministrador"]),
        new(ClavesPasos[4], Textos["PasoResumen"]),
    ];

    private IReadOnlySet<string> PasosHechos => ClavesPasos.Take(_paso - 1).ToHashSet();

    private IReadOnlyList<SeccionResumen> SeccionesResumen =>
    [
        new(1, Textos["SeccionTenant"], [new(Textos["CampoTenantTce"], NombreTenant)]),
        new(2, Textos["SeccionOperacion"], [new(Textos["ResumenOperador"], Textos["ResumenNoDisponible"])]),
        new(3, Textos["SeccionProducto"],
        [
            new(Textos["ResumenProducto"], Textos["ResumenProductoInbound"]),
            new(Textos["CampoPagador"], _pagador.Trim()),
            new(Textos["CampoTramo"], _tramo is { } t ? TextoTramo(t) : string.Empty),
            new(Textos["CampoModalidad"], _modalidad is { } m ? TextoModalidad(m) : string.Empty),
            new(Textos["CampoBeneficiario"], NombreTenant),
            new(Textos["CampoVigencia"], Textos["CampoVigenciaValor"]),
        ]),
        new(4, Textos["SeccionAdministrador"], [new(Textos["ResumenAdministrador"], Textos["ResumenNoDisponible"])]),
    ];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            _identidad = await Mediator.Send(new ObtenerIdentidadPlataformaQuery());
            _tenant = _identidad is { UsuarioSimuladoId: null, CapacidadActiva: CapacidadPrivilegio.Aprovisionamiento }
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

    private void CambiarPagador(string valor) { _pagador = valor; _error = null; }

    private void CambiarTramo(TramoTrabajadoresActivos? valor) { _tramo = valor; _error = null; }

    private void CambiarModalidad(ModalidadSuscripcionAlta? valor) { _modalidad = valor; _error = null; }

    private void CambiarPagadorCorr(string valor) { _pagadorCorr = valor; _errorLinea = null; }

    private void CambiarTramoCorr(TramoTrabajadoresActivos? valor) { _tramoCorr = valor; _errorLinea = null; }

    private void CambiarModalidadCorr(ModalidadSuscripcionAlta? valor) { _modalidadCorr = valor; _errorLinea = null; }

    /// <summary>Lo que falta para poder mandar la línea, o null. Lo repite el validador de cada comando.</summary>
    private string? FaltaEnLinea(string pagador, TramoTrabajadoresActivos? tramo, ModalidadSuscripcionAlta? modalidad)
    {
        if (!ProductoContratado.EsPagadorStripeCustomerIdValido(pagador.Trim()))
            return Textos["ErrorPagador"].Value;
        if (tramo is null || modalidad is null)
            return Textos["ErrorTramoModalidad"].Value;
        return null;
    }

    private Task AvanzarAsync()
    {
        if (_paso == 5)
            return DarDeAltaAsync();

        if (_paso == 3 && FaltaEnLinea(_pagador, _tramo, _modalidad) is { } falta)
        {
            _error = falta;
            return Task.CompletedTask;
        }

        _error = null;
        _cancelado = false;
        _paso++;
        return Task.CompletedTask;
    }

    private void Atras()
    {
        _error = null;
        _paso = Math.Max(1, _paso - 1);
    }

    private void IrA(int paso)
    {
        if (Ocupado)
            return;
        _error = null;
        _paso = paso;
    }

    private void Cancelar()
    {
        _paso = 1;
        _pagador = string.Empty;
        _tramo = null;
        _modalidad = null;
        _error = null;
        _cancelado = true;
    }

    private async Task DarDeAltaAsync()
    {
        if (Ocupado)
            return;
        if (FaltaEnLinea(_pagador, _tramo, _modalidad) is { } falta || _tramo is not { } tramo || _modalidad is not { } modalidad)
        {
            _paso = 3;
            _error = FaltaEnLinea(_pagador, _tramo, _modalidad);
            return;
        }

        // Lo enviado se fija antes de mandar: la confirmación cuenta esto, no lo que haya en los campos después.
        var enviado = new LineaContratada(_pagador.Trim(), tramo, modalidad);
        _enviando = true;
        _error = null;
        try
        {
            var resultado = await Mediator.Send(new ContratarLineaInboundCommand(enviado.Pagador, enviado.Tramo, enviado.Modalidad));
            if (resultado.EsExitoso)
                _contratado = enviado;
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
        _tramoCorr = null;
        _modalidadCorr = null;
        _editandoLinea = true;
    }

    private void CancelarEdicion()
    {
        _errorLinea = null;
        _editandoLinea = false;
    }

    private void PedirBaja()
    {
        _errorLinea = null;
        _exitoLinea = null;
        _confirmarBaja = true;
    }

    private async Task GuardarCorreccionAsync()
    {
        if (Ocupado)
            return;
        if (FaltaEnLinea(_pagadorCorr, _tramoCorr, _modalidadCorr) is { } falta || _tramoCorr is not { } tramo || _modalidadCorr is not { } modalidad)
        {
            _errorLinea = FaltaEnLinea(_pagadorCorr, _tramoCorr, _modalidadCorr);
            return;
        }

        _enviandoLinea = true;
        _errorLinea = null;
        try
        {
            var resultado = await Mediator.Send(new CorregirLineaInboundCommand(_pagadorCorr.Trim(), tramo, modalidad));
            if (resultado.EsExitoso)
            {
                _editandoLinea = false;
                _exitoLinea = Textos["CorreccionHecha"];
                // La confirmación del alta cuenta lo vigente, no lo que se envió al contratar.
                if (_contratado is not null)
                    _contratado = new LineaContratada(_pagadorCorr.Trim(), tramo, modalidad);
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
        if (Ocupado)
            return;

        _enviandoLinea = true;
        _errorLinea = null;
        _exitoLinea = null;
        try
        {
            var resultado = await Mediator.Send(new DarDeBajaLineaInboundCommand());
            if (resultado.EsExitoso)
            {
                _exitoLinea = Textos["BajaHecha"];
                // Tras la baja ya no hay Producto Contratado Inbound activo: la confirmación del alta deja de valer.
                _contratado = null;
                _paso = 1;
            }
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
