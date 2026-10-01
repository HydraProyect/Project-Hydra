using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>D-32: el parámetro <c>Pestana</c> de /documentos acepta el id canónico y el nombre de la pestaña; un valor desconocido no cambia nada.</summary>
public class DocumentosPestanaDeUrlTests
{
    [Theory]
    [InlineData("plataforma", "plataforma")]
    [InlineData("plataformas", "plataforma")]
    [InlineData("Plataformas", "plataforma")]
    [InlineData("sugerencias", "sugerencias")]
    [InlineData("preventivo", "sugerencias")]
    [InlineData("revision-ia", "revision-ia")]
    [InlineData("revision", "revision-ia")]
    [InlineData("estado", "listado")]
    [InlineData("reclamaciones", "reclamaciones")]
    [InlineData("plantillas", "plantillas")]
    public void Acepta_el_id_canonico_y_el_nombre_de_la_pestana(string valor, string esperado) =>
        CaeManager.Web.Features.Documentos.Pages.Documentos.IdDePestanaDeUrl(valor).Should().Be(esperado);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noexiste")]
    public void Un_valor_desconocido_o_vacio_no_elige_pestana(string? valor) =>
        CaeManager.Web.Features.Documentos.Pages.Documentos.IdDePestanaDeUrl(valor).Should().BeNull();
}
