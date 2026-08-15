using CaeManager.Application.Comercial.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Comercial;

public class PaymentProviderFalso(Result<SuscripcionProveedorDto>? resultadoSuscripcion = null) : IPaymentProvider
{
    public string Codigo => "falso";

    public int VecesConsultado { get; private set; }
    public string? UltimoIdConsultado { get; private set; }

    public Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma) =>
        throw new NotSupportedException("PaymentProviderFalso no simula webhooks — ver StripePaymentProviderTests para eso.");

    public Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default)
    {
        VecesConsultado++;
        UltimoIdConsultado = idSuscripcion;

        return Task.FromResult(resultadoSuscripcion
            ?? Result.Exito(new SuscripcionProveedorDto(idSuscripcion, "cus_falso", EstadoSuscripcionProveedor.Activa)));
    }
}
