using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.ApoyoCartera;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Panel «Dar acceso»: las operaciones de las que quien mira es hoy el principal (como Gestor
/// CAE o como Coordinador CAE), con sus apoyos y sus propuestas sin responder, y el formulario para proponer un apoyo nuevo;
/// y los apoyos vivos que quien mira puede terminar: los suyos («Desasignarme»), los que concedió
/// («Retirar acceso») y los que puede revocar por su rol.
///
/// <para>
/// «Soy el principal» se lee aquí solo para decidir qué se enseña: se compara el usuario de la
/// sesión con el principal que devuelve <see cref="ObtenerPersonasConCarteraQuery"/>. No es la
/// autorización: <see cref="ProponerApoyoCarteraCommand"/> la vuelve a comprobar. Lo mismo vale
/// para el reparto de <see cref="ObtenerApoyosDeCarteraQuery"/>: los tres Commands de fin de
/// apoyo deciden por su cuenta.
/// </para>
/// </summary>
public partial class PanelDarAcceso
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ICurrentUserService UsuarioActual { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosApoyoCartera> Textos { get; set; } = default!;

    /// <summary>Solo este Tenant propietario; sin valor, todos aquellos de los que quien mira es principal.</summary>
    [Parameter] public Guid? TenantId { get; set; }

    /// <summary>
    /// Forma compacta para la cabecera «Gestor CAE» de la pantalla Empresas: solo los botones
    /// «+ Dar acceso» (uno por operación de la que quien mira es el principal) y «Desasignarme»
    /// (uno por apoyo suyo), sin las tarjetas. Qué botón se pinta sale de las mismas lecturas que
    /// la forma completa, y lo que hace cada uno, de los mismos Commands.
    /// </summary>
    [Parameter] public bool EnCabecera { get; set; }

    /// <summary>
    /// Una acción terminó (se propuso un apoyo, se retiró una propuesta o se terminó un apoyo) y el
    /// panel ya releyó lo suyo: quien lo monta refresca lo que enseña.
    /// </summary>
    [Parameter] public EventCallback AlCambiar { get; set; }

    private IReadOnlyList<CarterasDeOperacion> _operaciones = [];
    private IReadOnlyList<PropuestaApoyoDto> _enviadas = [];
    private ApoyosDeCarteraDto _apoyos = ApoyosDeCarteraDto.Vacio;
    private Guid? _tenantCargado;
    private bool _cargado;

    private CarterasDeOperacion? _operacionDelDrawer;
    private IReadOnlyList<DestinatarioDeApoyoDto>? _destinatarios;
    private string _destinatarioId = string.Empty;
    private string _ultimoDia = string.Empty;
    private string? _mensajeErrorFormulario;
    private PropuestaApoyoDto? _aRetirar;
    private (ApoyoDeCarteraDto Apoyo, ViaFinDeApoyo Via)? _aTerminar;
    private bool _enCurso;

    private string TituloDrawer => _operacionDelDrawer is null
        ? string.Empty
        : Textos["DrawerTitulo", _operacionDelDrawer.NombreTenant];

    private string? MotivoProponerDeshabilitado => _destinatarios is { Count: 0 }
        ? Textos["MotivoSinDestinatarios"].Value
        : null;

    private string MensajeConfirmarRetirar => _aRetirar is null
        ? string.Empty
        : Textos["ConfirmarRetirarMensaje", _aRetirar.NombreDestinatario, _aRetirar.NombreEmpresa];

    private static string HoyIso => DiaDeNegocio.Hoy().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string TituloConfirmarFinDeApoyo => _aTerminar?.Via switch
    {
        ViaFinDeApoyo.Desasignarme => Textos["ConfirmarDesasignarmeTitulo"],
        ViaFinDeApoyo.RetirarLoConcedido => Textos["ConfirmarRetirarApoyoTitulo"],
        ViaFinDeApoyo.Revocar => Textos["ConfirmarRevocarApoyoTitulo"],
        _ => string.Empty,
    };

    private string MensajeConfirmarFinDeApoyo => _aTerminar switch
    {
        ({ } apoyo, ViaFinDeApoyo.Desasignarme) => Textos["ConfirmarDesasignarmeMensaje", apoyo.NombreEmpresa],
        ({ } apoyo, _) => Textos["ConfirmarFinDeApoyoMensaje", apoyo.NombreApoyo, apoyo.NombreEmpresa],
        _ => string.Empty,
    };

    private string TextoConfirmarFinDeApoyo => _aTerminar?.Via switch
    {
        ViaFinDeApoyo.Desasignarme => Textos["Desasignarme"],
        ViaFinDeApoyo.Revocar => Textos["RevocarApoyo"],
        _ => Textos["RetirarApoyo"],
    };

    protected override async Task OnParametersSetAsync()
    {
        if (_cargado && _tenantCargado == TenantId)
            return;

        _cargado = true;
        _tenantCargado = TenantId;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        var usuarioId = await UsuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
        {
            _operaciones = [];
            _enviadas = [];
            _apoyos = ApoyosDeCarteraDto.Vacio;
            return;
        }

        _operaciones = (await Mediator.Send(new ObtenerPersonasConCarteraQuery(TenantId)))
            .Where(o => o.Principal?.UsuarioId == usuarioId.Value)
            .ToList();
        _enviadas = _operaciones.Count == 0
            ? []
            : (await Mediator.Send(new ObtenerPropuestasApoyoPendientesQuery())).Enviadas;
        _apoyos = await Mediator.Send(new ObtenerApoyosDeCarteraQuery(TenantId));
    }

    /// <summary>«Apoyo hasta <fecha>» si la cartera lleva fecha de fin; sin ella, «Apoyo» (D-5).</summary>
    private string Rotulo(DateOnly? ultimoDia) => ultimoDia is { } dia
        ? Textos["RotuloApoyoHasta", dia]
        : Textos["RotuloApoyo"];

    private string Rotulo(DateTime? vigenciaHastaUtc) =>
        Rotulo(vigenciaHastaUtc is { } hasta ? VigenciaDeApoyo.UltimoDia(hasta) : (DateOnly?)null);

    /// <summary>El apoyo de esa persona en esa operación, si lo concedió quien mira y puede retirarlo.</summary>
    private ApoyoDeCarteraDto? Concedido(Guid asignacionOperacionId, Guid apoyoUsuarioId) =>
        _apoyos.Concedidos.FirstOrDefault(a => a.AsignacionOperacionId == asignacionOperacionId
                                               && a.ApoyoUsuarioId == apoyoUsuarioId);

    private static string NombresDeApoyo(CarterasDeOperacion operacion) =>
        string.Join(", ", operacion.Apoyos.Select(a => a.Nombre));

    private IEnumerable<PropuestaApoyoDto> PropuestasDe(Guid asignacionOperacionId) =>
        _enviadas.Where(p => p.AsignacionOperacionId == asignacionOperacionId);

    private async Task AbrirDrawerAsync(CarterasDeOperacion operacion)
    {
        _operacionDelDrawer = operacion;
        _destinatarioId = string.Empty;
        _ultimoDia = string.Empty;
        _mensajeErrorFormulario = null;
        _destinatarios = null;
        _destinatarios = await Mediator.Send(new ObtenerDestinatariosDeApoyoQuery(operacion.AsignacionOperacionId));
    }

    private void CerrarDrawer(bool visible)
    {
        if (visible || _enCurso)
            return;

        _operacionDelDrawer = null;
        _destinatarios = null;
        _destinatarioId = string.Empty;
        _ultimoDia = string.Empty;
        _mensajeErrorFormulario = null;
    }

    private void AlElegirUltimoDia(string valor)
    {
        _ultimoDia = valor;
        _mensajeErrorFormulario = null;
    }

    private void AlElegirDestinatario(string valor)
    {
        _destinatarioId = valor;
        _mensajeErrorFormulario = null;
    }

    private async Task ProponerAsync()
    {
        if (_enCurso || _operacionDelDrawer is not { } operacion)
            return;

        var destinatario = Guid.TryParse(_destinatarioId, out var id)
            ? _destinatarios?.FirstOrDefault(d => d.UsuarioId == id)
            : null;
        if (destinatario is null)
        {
            _mensajeErrorFormulario = Textos["ErrorFaltaDestinatario"];
            return;
        }

        // La fecha es opcional; si viene, es el último día con acceso (día de negocio).
        DateOnly? ultimoDia = null;
        if (_ultimoDia.Length > 0)
        {
            if (!DateOnly.TryParseExact(_ultimoDia, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia)
                || dia < DiaDeNegocio.Hoy())
            {
                _mensajeErrorFormulario = Textos["ErrorFechaDeFinNoValida"];
                return;
            }

            ultimoDia = dia;
        }

        _enCurso = true;
        try
        {
            var resultado = await Mediator.Send(
                new ProponerApoyoCarteraCommand(operacion.AsignacionOperacionId, destinatario.UsuarioId, ultimoDia));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = TextosApoyoCartera.MensajeDeError(Textos, resultado.Error);
                return;
            }

            Toasts.Mostrar(Textos["ToastPropuesta", destinatario.Nombre, operacion.NombreTenant], TonoToast.Exito);
            _operacionDelDrawer = null;
            _destinatarios = null;
            _destinatarioId = string.Empty;
            _ultimoDia = string.Empty;
            await CargarAsync();
            await AlCambiar.InvokeAsync();
        }
        finally
        {
            _enCurso = false;
        }
    }

    // Retirar cierra la propuesta: pregunta antes, como el resto de acciones que no se deshacen.
    private void PedirRetirar(PropuestaApoyoDto propuesta) => _aRetirar = propuesta;

    private void CerrarConfirmacionRetirada(bool visible)
    {
        if (!visible && !_enCurso)
            _aRetirar = null;
    }

    private async Task RetirarAsync()
    {
        if (_enCurso || _aRetirar is not { } propuesta)
            return;

        _enCurso = true;
        try
        {
            var resultado = await Mediator.Send(new RetirarPropuestaApoyoCarteraCommand(propuesta.Id));
            if (resultado.EsFallido)
                Toasts.Mostrar(TextosApoyoCartera.MensajeDeError(Textos, resultado.Error), TonoToast.Error);
            else
                Toasts.Mostrar(Textos["ToastRetirada"], TonoToast.Exito);

            // Siempre: si el destinatario respondió antes, la recarga lo enseña.
            await CargarAsync();
            await AlCambiar.InvokeAsync();
        }
        finally
        {
            _enCurso = false;
            _aRetirar = null;
        }
    }

    private void PedirTerminar(ApoyoDeCarteraDto apoyo, ViaFinDeApoyo via) => _aTerminar = (apoyo, via);

    private void CerrarConfirmacionFinDeApoyo(bool visible)
    {
        if (!visible && !_enCurso)
            _aTerminar = null;
    }

    private async Task TerminarAsync()
    {
        if (_enCurso || _aTerminar is not var (apoyo, via))
            return;

        _enCurso = true;
        try
        {
            IRequest<Result> comando = via switch
            {
                ViaFinDeApoyo.Desasignarme => new DesasignarmeDeApoyoCommand(apoyo.PropuestaId),
                ViaFinDeApoyo.RetirarLoConcedido => new RetirarApoyoConcedidoCommand(apoyo.PropuestaId),
                _ => new RevocarApoyoCarteraCommand(apoyo.PropuestaId),
            };

            var resultado = await Mediator.Send(comando);
            if (resultado.EsFallido)
                Toasts.Mostrar(TextosApoyoCartera.MensajeDeError(Textos, resultado.Error), TonoToast.Error);
            else
                Toasts.Mostrar(
                    via == ViaFinDeApoyo.Desasignarme
                        ? Textos["ToastDesasignado", apoyo.NombreEmpresa]
                        : Textos["ToastApoyoTerminado", apoyo.NombreApoyo, apoyo.NombreEmpresa],
                    TonoToast.Exito);

            // Siempre: si otro lo terminó antes, la recarga lo enseña.
            await CargarAsync();
            await AlCambiar.InvokeAsync();
        }
        finally
        {
            _enCurso = false;
            _aTerminar = null;
        }
    }

    /// <summary>Por dónde se termina un apoyo: decide qué Command se envía, no quién puede.</summary>
    private enum ViaFinDeApoyo
    {
        Desasignarme,
        RetirarLoConcedido,
        Revocar,
    }
}
