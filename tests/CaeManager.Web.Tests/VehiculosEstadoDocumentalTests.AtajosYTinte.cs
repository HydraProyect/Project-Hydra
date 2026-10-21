using Bunit;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class VehiculosEstadoDocumentalTests
{
    // bUnit observa los controles que activa keytips.js, pero no ejecuta el teclado del navegador.
    [Fact]
    public async Task Los_atajos_T_y_P_apuntan_a_los_filtros_documental_y_de_Empresa()
    {
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("Furgoneta") } });

        var documental = cut.Find("summary[data-keytip='T']");
        documental.GetAttribute("aria-label").Should().StartWith("Documentación:");
        documental.ParentElement!.QuerySelector(".franja-estado").Should().NotBeNull();
        cut.FindAll("[data-keytip='T']").Should().ContainSingle();

        var empresa = cut.Find("button[data-keytip='P']");
        empresa.GetAttribute("aria-label").Should().StartWith("Empresa:");
        cut.FindAll("[data-keytip='P']").Should().ContainSingle();
        await empresa.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() =>
        {
            cut.Find("button[data-keytip='P']").GetAttribute("aria-expanded").Should().Be("true");
            cut.FindAll("[role='menu']").Should().ContainSingle();
        });
    }

    [Theory]
    [InlineData(EstadoDocumento.Proximo, "fila-tintada-aviso")]
    [InlineData(EstadoDocumento.Urgente, "fila-tintada-aviso")]
    [InlineData(EstadoDocumento.Faltante, "fila-tintada-peligro")]
    [InlineData(EstadoDocumento.Vencido, "fila-tintada-peligro")]
    [InlineData(EstadoDocumento.Vigente, null)]
    [InlineData(null, null)]
    public void El_tinte_de_la_fila_corresponde_a_su_estado_documental(EstadoDocumento? estado, string? tinte)
    {
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("Furgoneta", estado) } });

        var clases = Filas(cut).Single().ClassList;
        clases.Where(c => c.StartsWith("fila-tintada-", StringComparison.Ordinal))
            .Should().BeEquivalentTo(tinte is null ? Array.Empty<string>() : [tinte]);
    }
}
