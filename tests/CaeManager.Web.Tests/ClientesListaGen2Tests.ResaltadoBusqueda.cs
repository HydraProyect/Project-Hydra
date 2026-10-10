using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class ClientesListaGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /clientes. <c>ObtenerClientesQuery</c> busca SOLO en la razón
    /// social: se marca ella y no el CIF, aunque el CIF contenga lo escrito (marcarlo diría que la
    /// pantalla busca por CIF, y no lo hace).
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_la_razon_social_y_no_el_CIF_ni_nada_mas()
    {
        const string nombre = "A-48 Logística";
        const string cif = "A-48.010.615"; // el de fábrica
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente(nombre), Cliente("Montajes Ebro S.L.", cif: "B-01.000.000") } });
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = Fila(cut, nombre).Foto();
        antes.Atributos.Should().Contain(nombre).And.Contain(cif, "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("LOGÍSTICA");
        cut.WaitForAssertion(() => Fila(cut, nombre).DebeMarcarSolo(antes, "Logística"));

        await cut.BuscarEnLaBarraAsync("a-48");
        cut.WaitForAssertion(() => Fila(cut, nombre).DebeMarcarSolo(antes, "A-48"));
        cut.DebeConservarElIdentificadorCopiable(cif, conMarca: false);
    }
}
