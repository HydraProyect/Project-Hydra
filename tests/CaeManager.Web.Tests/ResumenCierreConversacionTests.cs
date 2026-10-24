using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ResumenCierreConversacionTests : BunitContext
{
    public ResumenCierreConversacionTests() => Services.AddLocalization();

    private static ConversacionDetalleDto Detalle(EstadoConversacion estado = EstadoConversacion.Abierta) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Empresa de prueba", "Documentación", estado,
            null, null, DateTime.UtcNow, [], [], [], []);

    [Theory]
    [InlineData(EstadoConversacion.Abierta, "Revisar el hilo")]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, "Recibir la respuesta del Cliente")]
    [InlineData(EstadoConversacion.EnEsperaDeTercero, "Recibir la respuesta del tercero")]
    [InlineData(EstadoConversacion.Resuelta, "No quedan acciones de cierre")]
    [InlineData(EstadoConversacion.Cerrada, "No quedan acciones de cierre")]
    public void Explica_la_accion_segun_el_estado(EstadoConversacion estado, string esperado)
    {
        var cut = Render<ResumenCierreConversacion>(p => p.Add(c => c.Detalle, Detalle(estado)));
        cut.Find("[role=status]").TextContent.Should().Contain(esperado);
        cut.Find("strong").TextContent.Should().Be(estado switch
        {
            EstadoConversacion.Cerrada => "Cerrada",
            EstadoConversacion.Resuelta => "Resuelta",
            _ => "Para cerrarla falta"
        });
    }

    [Theory]
    [InlineData(EstadoConversacion.Resuelta)]
    [InlineData(EstadoConversacion.Cerrada)]
    public void Cuando_no_falta_nada_no_dice_que_falte(EstadoConversacion estado)
    {
        var cut = Render<ResumenCierreConversacion>(p => p.Add(c => c.Detalle, Detalle(estado)));
        cut.Markup.Should().NotContain("falta").And.Contain("No quedan acciones de cierre");
    }

    [Fact]
    public void Sin_cliente_empresarial_pide_identificar_antes_de_tramitar()
    {
        var cut = Render<ResumenCierreConversacion>(p => p.Add(c => c.Detalle, Detalle() with { ClienteId = null, ClienteRazonSocial = null }));
        cut.Markup.Should().Contain("Identificar al remitente");
    }

    [Fact]
    public void Las_propuestas_pendientes_se_muestran_sin_interpretar_html_ni_duplicar_el_resumen()
    {
        var resumen = "Revisar <script>documento</script>";
        var mensaje = new MensajeDetalleDto(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo,
            "persona@example.test", "", DateTime.UtcNow, [], null,
            [new(Guid.NewGuid(), null, null, null, null, resumen, 90, 90, 90),
             new(Guid.NewGuid(), null, null, null, null, resumen, 90, 90, 90)]);
        var cut = Render<ResumenCierreConversacion>(p => p.Add(c => c.Detalle, Detalle() with { Mensajes = [mensaje] }));
        cut.FindAll("li").Should().ContainSingle().Which.TextContent.Should().Be(resumen);
        cut.FindAll("script").Should().BeEmpty();
    }
}
