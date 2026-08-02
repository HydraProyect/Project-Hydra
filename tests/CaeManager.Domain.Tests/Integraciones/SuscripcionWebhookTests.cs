using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class SuscripcionWebhookTests
{
    [Fact]
    public void Se_crea_con_los_datos_informados()
    {
        var expiracion = DateTime.UtcNow.AddDays(3);
        var suscripcion = new SuscripcionWebhook(Guid.NewGuid(), "graph-sub-1", "secreto-compartido", expiracion);

        suscripcion.GraphSubscriptionId.Should().Be("graph-sub-1");
        suscripcion.ClientState.Should().Be("secreto-compartido");
        suscripcion.FechaExpiracionUtc.Should().Be(expiracion);
    }

    [Fact]
    public void Rechaza_una_conexion_vacia()
    {
        var accion = () => new SuscripcionWebhook(Guid.Empty, "graph-sub-1", "secreto", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_client_state_vacio()
    {
        var accion = () => new SuscripcionWebhook(Guid.NewGuid(), "graph-sub-1", " ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarTrasRenovacion_reemplaza_id_y_expiracion()
    {
        var suscripcion = new SuscripcionWebhook(Guid.NewGuid(), "graph-sub-1", "secreto", DateTime.UtcNow.AddDays(1));
        var nuevaExpiracion = DateTime.UtcNow.AddDays(4);

        suscripcion.ActualizarTrasRenovacion("graph-sub-2", nuevaExpiracion);

        suscripcion.GraphSubscriptionId.Should().Be("graph-sub-2");
        suscripcion.FechaExpiracionUtc.Should().Be(nuevaExpiracion);
    }
}
