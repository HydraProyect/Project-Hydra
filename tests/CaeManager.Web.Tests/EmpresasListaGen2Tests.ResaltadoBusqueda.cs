using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class EmpresasListaGen2Tests
{
    private const string FilaDeEmpresa = ".tarjeta-fila-acordeon";

    /// <summary>
    /// Resaltado de la búsqueda en /empresas. <c>ObtenerEmpresasQuery</c> busca en razón social y
    /// CIF: se marcan los dos. El doble de mediador solo filtra por razón social, así que el CIF se
    /// prueba con un término que está en los dos campos.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_razon_social_y_CIF_y_no_cambia_nada_mas()
    {
        const string cif = "B-48.220.917"; // el de fábrica
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("B-48 Montajes Ibáñez"), Empresa("Refrielectric S.A.", cif: "A-01.000.000") } });
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre(FilaDeEmpresa, "B-48 Montajes Ibáñez").Foto();
        antes.Atributos.Should().Contain("B-48 Montajes Ibáñez").And.Contain(cif, "control: la fila tiene nombres accesibles que comparar");

        // Escrito sin acento: la marca lleva el texto original de la celda.
        await cut.BuscarEnLaBarraAsync("ibanez");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeEmpresa, "B-48 Montajes Ibáñez").DebeMarcarSolo(antes, "Ibáñez"));
        cut.DebeConservarElIdentificadorCopiable(cif, conMarca: false);

        await cut.BuscarEnLaBarraAsync("b-48");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeEmpresa, "B-48 Montajes Ibáñez").DebeMarcarSolo(antes, "B-48", "B-48"));
        cut.DebeConservarElIdentificadorCopiable(cif, conMarca: true);
    }
}
