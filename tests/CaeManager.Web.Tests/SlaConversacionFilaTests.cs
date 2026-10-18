using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class SlaConversacionFilaTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void La_fila_muestra_el_reloj_y_distingue_un_SLA_vencido(bool vencido)
    {
        Services.AddLogging().AddLocalization();
        var conversacion = new ConversacionListaDto(Guid.NewGuid(), null, null, "Asunto", EstadoConversacion.Abierta,
            null, "contacto@ejemplo.test", "Texto", DateTime.UtcNow, 1, CanalConversacion.Correo, null,
            DireccionMensaje.Entrante, Prioridad: PrioridadConversacion.Critica,
            VencimientoSlaUtc: DateTime.UtcNow.AddHours(vencido ? -2 : 2), SlaRestante: TimeSpan.FromHours(vencido ? -2 : 2));

        var fila = Render<FilaConversacion>(p => p.Add(c => c.Conversacion, conversacion));

        fila.Markup.Should().Contain("SLA").And.Contain("2 h 0 min");
        fila.Markup.Contains("vencido", StringComparison.Ordinal).Should().Be(vencido);
    }
}
