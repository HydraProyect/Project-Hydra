using System.Net;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// La regla más cara del pipeline HTTP: decide si una petición de pago a un
/// proveedor de IA se repite. Antes el reintento estándar trataba igual un 429
/// que un 500 que un timeout —correcto para un servicio idempotente, que
/// ninguno de estos endpoints es— así que hasta tres intentos HTTP se
/// multiplicaban por los tres del trabajo durable: nueve ejecuciones
/// facturables posibles por un solo encargo, y nueve transmisiones del
/// documento.
/// </summary>
public class ReintentoProveedorIaTests
{
    [Fact]
    public void Reintenta_un_429_porque_dice_explicitamente_que_no_se_proceso()
    {
        using var respuesta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        ReintentoProveedorIa.EsSeguroReintentar(respuesta).Should().BeTrue();
    }

    /// <summary>
    /// Cada uno por su motivo, pero todos comparten el mismo: desde aquí no se
    /// puede distinguir "no se procesó" de "se procesó y se perdió la
    /// respuesta". Y en la duda, repetir cuesta dinero y vuelve a enviar el
    /// documento.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void No_reintenta_ninguna_otra_respuesta(HttpStatusCode estado)
    {
        using var respuesta = new HttpResponseMessage(estado);

        ReintentoProveedorIa.EsSeguroReintentar(respuesta).Should().BeFalse();
    }

    /// <summary>
    /// Respuesta nula es lo que llega cuando saltó una excepción: timeout de
    /// intento o fallo de red. El timeout es el caso más peligroso — cuanto más
    /// tarda un modelo, más probable es que esté trabajando de verdad, así que
    /// el timeout correlaciona con "ya se está cobrando".
    /// </summary>
    [Fact]
    public void No_reintenta_cuando_no_hubo_respuesta_timeout_o_fallo_de_red()
    {
        ReintentoProveedorIa.EsSeguroReintentar(null).Should().BeFalse();
    }
}
