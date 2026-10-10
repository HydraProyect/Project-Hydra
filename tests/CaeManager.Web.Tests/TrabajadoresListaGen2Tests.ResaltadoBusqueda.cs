using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class TrabajadoresListaGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /trabajadores. <c>ObtenerTrabajadoresQuery</c> busca en Nombre,
    /// Apellidos, DNI y Alias: se marcan nombre, apellidos y DNI (el alias no se pinta en la fila).
    /// La Empresa empleadora no se busca y no se marca, aunque contenga lo escrito.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_nombre_apellidos_y_DNI_y_no_cambia_nada_mas()
    {
        var ana = Trabajador("Ana", "Montajes"); // su empleador de fábrica es «Montajes Ebro S.L.»
        var dni = ana.Dto.Dni!;
        var cut = Renderizar(new MediatorFalso { Almacen = { ana, Trabajador("Bea", "Alonso") } });
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre("tbody tr", "Ana Montajes").Foto();
        antes.Texto.Should().Contain("Montajes Ebro S.L.", "control: la columna Empresa también contiene «Montajes»");
        antes.Atributos.Should().Contain("Ana Montajes").And.Contain(dni, "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("montajes");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", "Ana Montajes").DebeMarcarSolo(antes, "Montajes"));
        cut.DebeConservarElIdentificadorCopiable(dni, conMarca: false);

        await cut.BuscarEnLaBarraAsync("ANA");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", "Ana Montajes").DebeMarcarSolo(antes, "Ana"));

        await cut.BuscarEnLaBarraAsync(dni.ToLowerInvariant());
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", "Ana Montajes").DebeMarcarSolo(antes, dni));
        cut.DebeConservarElIdentificadorCopiable(dni, conMarca: true);
    }
}
