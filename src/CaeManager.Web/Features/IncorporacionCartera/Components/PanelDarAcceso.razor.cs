using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Panel «Dar acceso»: las operaciones de las que quien mira es hoy Gestor CAE principal, con sus
/// apoyos y sus propuestas sin responder, y el formulario para proponer un apoyo nuevo.
///
/// <para>
/// «Soy el principal» se lee aquí solo para decidir qué se enseña: se compara el usuario de la
/// sesión con el principal que devuelve <see cref="ObtenerPersonasConCarteraQuery"/>. No es la
/// autorización: <see cref="ProponerApoyoCarteraCommand"/> la vuelve a comprobar.
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

    private IReadOnlyList<CarterasDeOperacion> _operaciones = [];
    private IReadOnlyList<PropuestaApoyoDto> _enviadas = [];
    private Guid? _tenantCargado;
    private bool _cargado;

    private CarterasDeOperacion? _operacionDelDrawer;
    private IReadOnlyList<DestinatarioDeApoyoDto>? _destinatarios;
    private string _destinatarioId = string.Empty;
    private string? _mensajeErrorFormulario;
    private PropuestaApoyoDto? _aRetirar;
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
            return;
        }

        _operaciones = (await Mediator.Send(new ObtenerPersonasConCarteraQuery(TenantId)))
            .Where(o => o.Principal?.UsuarioId == usuarioId.Value)
            .ToList();
        _enviadas = _operaciones.Count == 0
            ? []
            : (await Mediator.Send(new ObtenerPropuestasApoyoPendientesQuery())).Enviadas;
    }

    private IEnumerable<PropuestaApoyoDto> PropuestasDe(Guid asignacionOperacionId) =>
        _enviadas.Where(p => p.AsignacionOperacionId == asignacionOperacionId);

    private async Task AbrirDrawerAsync(CarterasDeOperacion operacion)
    {
        _operacionDelDrawer = operacion;
        _destinatarioId = string.Empty;
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

        _enCurso = true;
        try
        {
            var resultado = await Mediator.Send(
                new ProponerApoyoCarteraCommand(operacion.AsignacionOperacionId, destinatario.UsuarioId));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = TextosApoyoCartera.MensajeDeError(Textos, resultado.Error);
                return;
            }

            Toasts.Mostrar(Textos["ToastPropuesta", destinatario.Nombre, operacion.NombreTenant], TonoToast.Exito);
            _operacionDelDrawer = null;
            _destinatarios = null;
            _destinatarioId = string.Empty;
            await CargarAsync();
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
        }
        finally
        {
            _enCurso = false;
            _aRetirar = null;
        }
    }
}
