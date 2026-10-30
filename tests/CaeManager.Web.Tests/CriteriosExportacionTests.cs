using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Exportacion;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class CriteriosExportacionTests
{
    [Theory]
    [InlineData("Vencido", "Vencido")]
    [InlineData("SinDocumentos", "SinDocumentos")]
    [InlineData(" Vencido, texto libre,4, Vigente, Vencido, SinDocumentos ", "Vencido,Vigente,SinDocumentos")]
    [InlineData("Faltante,EnTolerancia", "Faltante,EnTolerancia")]
    [InlineData("texto libre,4,-1,999", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void El_rastro_registra_los_estados_que_aplica_el_filtro(string? filtro, string? esperado)
    {
        var registrado = CriteriosExportacion.EstadosDocumentalesAplicados(filtro);
        registrado.Should().Be(esperado);

        var criterios = CriteriosExportacion.Desde(("estado", registrado));
        criterios.TryGetValue("estado", out var estadoRegistrado).Should().Be(esperado is not null);
        estadoRegistrado.Should().Be(esperado);

        foreach (var estado in Enum.GetValues<EstadoDocumento>().Cast<EstadoDocumento?>().Append(null))
            EstadoDocumentalFiltro.Coincide(estado, estadoRegistrado)
                .Should().Be(EstadoDocumentalFiltro.Coincide(estado, filtro));

        foreach (var incluirFaltantes in new[] { false, true })
            EstadoDocumentalFiltro.ClavesDeOrden(estadoRegistrado, incluirFaltantes)
                .Should().BeEquivalentTo(EstadoDocumentalFiltro.ClavesDeOrden(filtro, incluirFaltantes));
    }
}
