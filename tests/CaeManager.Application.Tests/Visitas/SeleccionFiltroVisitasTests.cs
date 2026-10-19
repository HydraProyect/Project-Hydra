using CaeManager.Application.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Visitas;

public class SeleccionFiltroVisitasTests
{
    [Theory]
    [InlineData("Empresa, S.A.")]
    [InlineData("Empresa; Norte")]
    [InlineData("[Empresa]")]
    [InlineData("Empresa \"Norte\"")]
    public void Un_nombre_con_separadores_no_se_convierte_en_varias_empresas(string nombre)
    {
        SeleccionFiltroVisitas.Leer(SeleccionFiltroVisitas.Escribir([nombre])).Should().Equal(nombre);
        SeleccionFiltroVisitas.Leer(SeleccionFiltroVisitas.Escribir([nombre, "Otra empresa", nombre]))
            .Should().Equal(nombre, "Otra empresa");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[2]")]
    [InlineData("[no es JSON")]
    public void Una_seleccion_vacia_o_malformada_no_aplica_filtros(string valor) =>
        SeleccionFiltroVisitas.Leer(valor).Should().BeEmpty();

    [Fact]
    public void Varias_ventanas_se_unen_sin_contar_dos_veces_las_visitas_de_hoy()
    {
        SeleccionFiltroVisitas.Ventana(SeleccionFiltroVisitas.Escribir(["0", "7", "14"])).Should().Be(14);
        SeleccionFiltroVisitas.Ventana("0").Should().Be(0);
        SeleccionFiltroVisitas.Ventana("99").Should().BeNull();
    }
}
