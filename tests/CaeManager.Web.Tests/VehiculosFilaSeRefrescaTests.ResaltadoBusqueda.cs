using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class VehiculosFilaSeRefrescaTests
{
    /// <summary>
    /// Resaltado de la búsqueda en /vehiculos. <c>ObtenerVehiculosQuery</c> busca en nombre, modelo
    /// y matrícula: se marcan los tres. El empleador no se busca y no se marca. El doble de
    /// mediador solo filtra por nombre, así que modelo y matrícula se prueban con un término que
    /// también está en el nombre.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_nombre_modelo_y_matricula_y_no_cambia_nada_mas()
    {
        // De fábrica: modelo «Transit», matrícula «1234-ABC», empleador «Montajes Ebro S.L.».
        const string nombre = "Transit 1234 Ebro";
        const string matricula = "1234-ABC";
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo(nombre), Vehiculo("Grúa") } });
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre("tbody tr", nombre).Foto();
        antes.Texto.Should().Contain("Montajes Ebro S.L.", "control: la columna del empleador también contiene «Ebro»");
        antes.Atributos.Should().Contain(nombre).And.Contain(matricula, "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("ebro");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", nombre).DebeMarcarSolo(antes, "Ebro"));
        cut.DebeConservarElIdentificadorCopiable(matricula, conMarca: false);

        await cut.BuscarEnLaBarraAsync("transit");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", nombre).DebeMarcarSolo(antes, "Transit", "Transit"));

        await cut.BuscarEnLaBarraAsync("1234");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", nombre).DebeMarcarSolo(antes, "1234", "1234"));
        cut.DebeConservarElIdentificadorCopiable(matricula, conMarca: true);
    }
}
