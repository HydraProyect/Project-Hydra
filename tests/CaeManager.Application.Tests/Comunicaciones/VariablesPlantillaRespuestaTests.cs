using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class VariablesPlantillaRespuestaTests
{
    public static IEnumerable<object[]> Plantillas => PlantillasRespuestaPredefinidas.Crear(Guid.Parse("d81a15bc-42ac-4cf9-bf49-36487809d30c"))
        .Select(p => new object[] { p.Titulo, p.CuerpoHtml });

    [Theory]
    [MemberData(nameof(Plantillas))]
    public void Cada_plantilla_rellena_sus_variables_y_conserva_las_que_faltan(string titulo, string cuerpo)
    {
        titulo.Should().NotBeEmpty();
        var valores = new Dictionary<string, string?>
        {
            ["Empresa"] = "Empresa Norte",
            ["Centro"] = "Centro Sur",
            ["Cliente empresarial"] = "Empresa destinataria",
            ["Trabajador"] = "Ana Pérez",
            ["fecha de la visita"] = "20/10/2026",
            ["documentos pendientes"] = "Certificado A1"
        };
        var resultado = VariablesPlantillaRespuesta.Rellenar(cuerpo, valores);
        VariablesPlantillaRespuesta.Pendientes(resultado).Should().BeEmpty();
        resultado.Should().Contain("Empresa Norte").And.Contain("Centro Sur");
        VariablesPlantillaRespuesta.Pendientes(VariablesPlantillaRespuesta.Rellenar(cuerpo, new Dictionary<string, string?>()))
            .Should().Contain("Empresa").And.Contain("Centro");
    }

    [Fact]
    public void Los_datos_se_escapan_sin_interpretarlos_como_HTML_ni_como_otras_variables()
    {
        var resultado = VariablesPlantillaRespuesta.Rellenar("{{Empresa}} {{Centro}} {{desconocida}}",
            new Dictionary<string, string?> { ["Empresa"] = "<img src=x> {{Centro}}", ["Centro"] = "A & B" });
        resultado.Should().Be("&lt;img src=x&gt; {{Centro}} A &amp; B {{desconocida}}");
    }

    [Theory]
    [InlineData("{{Empresa}}", false)]
    [InlineData("<p>{{ documentos pendientes }}</p>", false)]
    [InlineData("Respuesta completada", true)]
    public void Ningun_canal_envia_variables_pendientes(string texto, bool valido)
    {
        var id = Guid.NewGuid();
        new ResponderConversacionCommandValidator().Validate(new ResponderConversacionCommand(id, texto)).IsValid.Should().Be(valido);
        new ResponderConversacionWhatsAppCommandValidator().Validate(new ResponderConversacionWhatsAppCommand(id, texto)).IsValid.Should().Be(valido);
        new MigrarConversacionACorreoCommandValidator().Validate(new MigrarConversacionACorreoCommand(id, "equipo@example.com", texto)).IsValid.Should().Be(valido);
    }
}
