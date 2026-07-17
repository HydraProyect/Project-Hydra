using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

public class VisitaTrabajadorTests
{
    [Fact]
    public void Crea_una_asociacion_valida()
    {
        var visitaId = Guid.NewGuid();
        var trabajadorId = Guid.NewGuid();

        var asociacion = new VisitaTrabajador(visitaId, trabajadorId);

        asociacion.VisitaId.Should().Be(visitaId);
        asociacion.TrabajadorId.Should().Be(trabajadorId);
    }

    [Fact]
    public void Rechaza_una_visita_vacia()
    {
        var accion = () => new VisitaTrabajador(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_trabajador_vacio()
    {
        var accion = () => new VisitaTrabajador(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
