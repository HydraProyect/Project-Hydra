using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Lista de la alerta «sin principal» con la acción «Asumir». Autocontenido: hace su propia
/// consulta y su propio comando, y entrega la alerta a la página para que no le diga a la vez
/// «no tienes acceso» a quien sí la ve. Asumir no pide confirmación: es inmediato por contrato y
/// se deshace designando a otra persona.
/// </summary>
public partial class PanelOperacionesSinPrincipal
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosIncorporacionCartera> Textos { get; set; } = default!;

    /// <summary>Se invoca tras cada carga con la alerta leída; <c>LaVe</c> dice si quien mira es Coordinador CAE, Dirección CAE o Administrador.</summary>
    [Parameter] public EventCallback<AlertaDePrincipal> AlCargar { get; set; }

    private AlertaDePrincipal? _alerta;
    private Guid? _enCurso;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _alerta = await Mediator.Send(new ObtenerOperacionesSinPrincipalQuery());
        await AlCargar.InvokeAsync(_alerta);
    }

    private string Situacion(OperacionEnAlertaDePrincipal operacion) => operacion.Situacion switch
    {
        SituacionDePrincipal.SinNadieAsignado => Textos["SinPrincipalSinNadie"],
        _ when operacion.PersonasAsignadas == 1 => Textos["SinPrincipalConApoyoUna", 1],
        _ => Textos["SinPrincipalConApoyoVarias", operacion.PersonasAsignadas],
    };

    private const string PrefijoCodigo = "AsumirPrincipal.";

    /// <summary>Cada código estable <c>AsumirPrincipal.X</c> tiene su clave <c>ErrorAsumirX</c>; el resto cae al genérico.</summary>
    private string MensajeDeError(CaeManager.Domain.Common.Error error)
    {
        if (error.Codigo.StartsWith(PrefijoCodigo, StringComparison.Ordinal))
        {
            var texto = Textos[string.Concat("ErrorAsumir", error.Codigo.AsSpan(PrefijoCodigo.Length))];
            if (!texto.ResourceNotFound)
                return texto.Value;
        }

        return Textos["ErrorGenerico"].Value;
    }

    private async Task AsumirAsync(OperacionEnAlertaDePrincipal operacion)
    {
        if (_enCurso is not null)
            return;

        _enCurso = operacion.AsignacionOperacionId;
        try
        {
            var resultado = await Mediator.Send(new AsumirPrincipalDeOperacionCommand(operacion.AsignacionOperacionId));
            if (resultado.EsFallido)
                Toasts.Mostrar(MensajeDeError(resultado.Error), TonoToast.Error);
            else
                Toasts.Mostrar(Textos["SinPrincipalAsumida", operacion.NombreTenant], TonoToast.Exito);

            // Siempre: si otra persona la asumió antes, el error lo dice y la fila desaparece.
            await CargarAsync();
        }
        finally
        {
            _enCurso = null;
        }
    }
}
