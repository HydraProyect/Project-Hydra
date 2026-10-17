using Bunit;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerIntencionConversacion;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ChipIntencionCaeTests : BunitContext
{
    public ChipIntencionCaeTests() => Services.AddLocalization();

    [Fact]
    public void Confirmar_en_un_clic_envia_la_intencion_propuesta()
    {
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.UrgenciaAcceso, ConfianzaIntencion.Media);
        IntencionCae? confirmada = null;
        var cut = Render<ChipIntencionCae>(p => p.Add(c => c.Intencion, IntencionConversacionDto.Desde(intencion, true)).Add(c => c.OnConfirmar, i => confirmada = i));
        cut.Markup.Should().Contain("Urgencia de acceso").And.Contain("Confianza media");
        cut.Find("button").Click();
        confirmada.Should().Be(IntencionCae.UrgenciaAcceso);
        intencion.ConfirmadaPorPersona.Should().BeFalse("el componente solicita la escritura al comando");
    }

    [Fact]
    public void Cambiar_el_selector_envia_la_correccion_sin_otro_boton()
    {
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja);
        IntencionCae? confirmada = null;
        var cut = Render<ChipIntencionCae>(p => p.Add(c => c.Intencion, IntencionConversacionDto.Desde(intencion, true)).Add(c => c.OnConfirmar, i => confirmada = i));
        cut.Find("select").Change(nameof(IntencionCae.ConsultaRequisitos));
        confirmada.Should().Be(IntencionCae.ConsultaRequisitos);
        cut.FindAll("option").Should().HaveCount(7);
    }

    [Fact]
    public void Muestra_la_confirmacion_y_el_enlace_como_propuesta()
    {
        var id = Guid.NewGuid();
        var intencion = new IntencionConversacion(Guid.NewGuid(), Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja, TipoEntidadIntencion.Trabajador, id);
        intencion.Confirmar(IntencionCae.CoordinacionAcceso);
        var cut = Render<ChipIntencionCae>(p => p.Add(c => c.Intencion, IntencionConversacionDto.Desde(intencion, true)));
        cut.Markup.Should().Contain("Cambiada y confirmada");
        cut.FindAll("button").Should().BeEmpty();
        cut.Find("a").GetAttribute("href").Should().Be($"/trabajadores/{id}");
        cut.Markup.Should().Contain("Pendiente de validación humana");
    }
}
