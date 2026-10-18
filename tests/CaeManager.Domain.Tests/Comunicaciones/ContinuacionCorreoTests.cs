using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class ContinuacionCorreoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conserva_el_ancla_y_la_asignacion_del_hilo_anterior(bool anclaEmpresa)
    {
        var empresaId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var anterior = new Conversacion("Anterior", anclaEmpresa ? null : empresaId, "Documentación",
            anclaEmpresa ? empresaId : null);
        anterior.Asignar(personaId);
        anterior.AsociarConexion(Guid.NewGuid(), "hilo");
        anterior.CambiarEstado(EstadoConversacion.Cerrada);

        var nueva = Conversacion.ContinuarCorreo(anterior, "Respuesta");

        nueva.ClienteId.Should().Be(anterior.ClienteId);
        nueva.EmpresaId.Should().Be(anterior.EmpresaId);
        nueva.EjecutivoAsignadoId.Should().Be(personaId);
        nueva.Etiquetas.Should().Be("Documentación");
        nueva.ConversacionAnteriorId.Should().Be(anterior.Id);
        nueva.Estado.Should().Be(EstadoConversacion.Abierta);
        nueva.Mensajes.Should().BeEmpty();
    }
}
