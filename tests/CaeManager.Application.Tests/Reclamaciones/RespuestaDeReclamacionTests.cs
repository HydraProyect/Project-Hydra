using CaeManager.Application.Reclamaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

public class RespuestaDeReclamacionTests
{
    private static readonly DateTime Envio = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_Conversacion_no_se_sabe_si_contestaron()
    {
        RespuestaDeReclamacion.SinRespuesta(null, Envio, [Envio.AddDays(1)]).Should().BeNull();
    }

    [Fact]
    public void Con_Conversacion_y_ningun_entrante_posterior_sigue_sin_respuesta()
    {
        var conversacion = Guid.NewGuid();

        RespuestaDeReclamacion.SinRespuesta(conversacion, Envio, []).Should().BeTrue();
        RespuestaDeReclamacion.SinRespuesta(conversacion, Envio, [Envio.AddDays(-1), Envio])
            .Should().BeTrue("un entrante anterior al envío, o del mismo instante, no lo contesta");
    }

    [Fact]
    public void Un_entrante_posterior_al_envio_es_respuesta()
    {
        RespuestaDeReclamacion.SinRespuesta(Guid.NewGuid(), Envio, [Envio.AddDays(-1), Envio.AddMinutes(1)]).Should().BeFalse();
    }
}
