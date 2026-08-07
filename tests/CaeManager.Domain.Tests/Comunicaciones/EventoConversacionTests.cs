using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class EventoConversacionTests
{
    [Fact]
    public void Crea_un_evento_valido()
    {
        var conversacionId = Guid.NewGuid();
        var visitaId = Guid.NewGuid();
        var fechaUtc = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc);

        var evento = new EventoConversacion(conversacionId, TipoEventoConversacion.VisitaCreada, visitaId, fechaUtc);

        evento.ConversacionId.Should().Be(conversacionId);
        evento.Tipo.Should().Be(TipoEventoConversacion.VisitaCreada);
        evento.ReferenciaId.Should().Be(visitaId);
        evento.FechaUtc.Should().Be(fechaUtc);
    }

    [Fact]
    public void Rechaza_pertenecer_a_una_conversacion_vacia()
    {
        var accion = () => new EventoConversacion(Guid.Empty, TipoEventoConversacion.VisitaCreada, Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_referencia_vacia()
    {
        var accion = () => new EventoConversacion(Guid.NewGuid(), TipoEventoConversacion.VisitaCreada, Guid.Empty, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }
}
