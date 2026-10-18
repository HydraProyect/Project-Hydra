using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Documentos;

public class PropagacionDocumentoTests
{
    [Fact]
    public void La_entrega_manual_guarda_quien_y_cuando_y_puede_volver_a_pendiente()
    {
        var fila = new PropagacionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var persona = Guid.NewGuid();
        var ahora = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        fila.MarcarHecha(persona, ahora);
        fila.Hecha.Should().BeTrue();
        fila.HechoPorUsuarioId.Should().Be(persona);
        fila.HechoEnUtc.Should().Be(ahora);
        fila.Deshacer(persona);
        fila.Hecha.Should().BeFalse();
        fila.HechoPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Un_correo_entregado_no_se_puede_deshacer()
    {
        var fila = new PropagacionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var persona = Guid.NewGuid();
        fila.MarcarHecha(persona, DateTime.UtcNow, Guid.NewGuid());
        fila.Invoking(p => p.Deshacer(persona)).Should().Throw<InvalidOperationException>();
        fila.Hecha.Should().BeTrue();
    }

    [Fact]
    public void Otra_persona_no_puede_deshacer_la_entrega()
    {
        var fila = new PropagacionDocumento(Guid.NewGuid(), Guid.NewGuid());
        fila.MarcarHecha(Guid.NewGuid(), DateTime.UtcNow);
        fila.Invoking(p => p.Deshacer(Guid.NewGuid())).Should().Throw<InvalidOperationException>();
        fila.Hecha.Should().BeTrue();
    }
}
