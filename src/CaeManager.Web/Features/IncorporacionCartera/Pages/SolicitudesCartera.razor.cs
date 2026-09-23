using System.Globalization;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Pages;

/// <summary>
/// Bandeja de solicitudes de incorporación a cartera. El Coordinador CAE ve
/// las pendientes de su Operador CAE (acepta o rechaza, nunca la suya) y las
/// incorporaciones vigentes (revoca). El Gestor CAE ve las suyas, pide una
/// nueva y revoca las que tiene aceptadas. Qué ve cada uno lo decide la Query
/// por su rol en su propia organización, no esta página.
/// </summary>
public partial class SolicitudesCartera : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosIncorporacionCartera> Textos { get; set; } = default!;

    private BandejaIncorporacionCarteraDto? _bandeja;
    private bool _errorCarga;
    private bool _sinAcceso;
    private bool _dialogoVisible;
    private Guid? _enCurso;
    private SolicitudIncorporacionCarteraDto? _aRevocar;

    private string MensajeConfirmarRevocar => _aRevocar is null
        ? string.Empty
        : _bandeja is { EsCoordinadorCae: true }
            ? Textos["ConfirmarRevocarMensaje", _aRevocar.NombreSolicitante, _aRevocar.NombreEmpresa]
            : Textos["ConfirmarRevocarPropiaMensaje", _aRevocar.NombreEmpresa];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _errorCarga = false;
        var resultado = await Mediator.Send(new ObtenerSolicitudesIncorporacionCarteraQuery());
        if (resultado.EsFallido)
        {
            // Quien no es Gestor ni Coordinador CAE en su Operador CAE no tiene
            // nada que reintentar: se le dice, sin el botón de reintento.
            _sinAcceso = resultado.Error.Codigo == ErroresSolicitudCartera.SinPermiso.Codigo;
            _errorCarga = !_sinAcceso;
            return;
        }

        _bandeja = resultado.Valor;
    }

    private void AbrirDialogo() => _dialogoVisible = true;

    private Task AceptarAsync(SolicitudIncorporacionCarteraDto solicitud) =>
        EjecutarAsync(solicitud.Id, new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id),
            Textos["ToastAceptada", solicitud.NombreSolicitante, solicitud.NombreEmpresa]);

    private Task RechazarAsync(SolicitudIncorporacionCarteraDto solicitud) =>
        EjecutarAsync(solicitud.Id, new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), Textos["ToastRechazada"]);

    private void PedirRevocar(SolicitudIncorporacionCarteraDto solicitud) => _aRevocar = solicitud;

    private void CerrarConfirmacion(bool visible)
    {
        if (!visible && _enCurso is null)
            _aRevocar = null;
    }

    private async Task RevocarAsync()
    {
        if (_aRevocar is not { } solicitud)
            return;

        await EjecutarAsync(solicitud.Id, new RevocarIncorporacionCarteraCommand(solicitud.Id), Textos["ToastRevocada"]);
        _aRevocar = null;
    }

    /// <summary>
    /// Ejecuta el Command y recarga siempre: si otro Coordinador CAE la
    /// resolvió antes, el error lo dice y la fila desaparece con la recarga.
    /// </summary>
    private async Task EjecutarAsync(Guid solicitudId, IRequest<Result> command, string textoExito)
    {
        if (_enCurso is not null)
            return;

        _enCurso = solicitudId;
        try
        {
            var resultado = await Mediator.Send(command);
            if (resultado.EsFallido)
                Toasts.Mostrar(TextosIncorporacionCartera.MensajeDeError(Textos, resultado.Error), TonoToast.Error);
            else
                Toasts.Mostrar(textoExito, TonoToast.Exito);

            await CargarAsync();
        }
        finally
        {
            _enCurso = null;
        }
    }

    private static string FormatearFecha(DateTime utc) =>
        utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static TonoBadge TonoDe(EstadoSolicitudIncorporacionCartera estado) => estado switch
    {
        EstadoSolicitudIncorporacionCartera.Pendiente => TonoBadge.Info,
        EstadoSolicitudIncorporacionCartera.Aceptada => TonoBadge.Exito,
        EstadoSolicitudIncorporacionCartera.Rechazada => TonoBadge.Peligro,
        _ => TonoBadge.Neutro,
    };
}
