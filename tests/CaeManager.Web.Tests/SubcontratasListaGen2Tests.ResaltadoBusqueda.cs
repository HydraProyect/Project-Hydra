using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class SubcontratasListaGen2Tests
{
    private const string FilaDeSubcontrata = ".tarjeta-fila-acordeon";

    /// <summary>
    /// Resaltado de la búsqueda en /subcontratas. <c>ObtenerSubcontratasQuery</c> busca en razón
    /// social y CIF: se marcan los dos. El doble de mediador solo filtra por razón social, así que
    /// el CIF se prueba con un término que está en los dos campos.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_razon_social_y_CIF_y_no_cambia_nada_mas()
    {
        const string nombre = "Andamios B-20 Iparra";
        const string cif = "B-20.774.115"; // el de fábrica
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata(nombre), Subcontrata("Grúas Aldapa S.L.")] });
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeSubcontrata, nombre));
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre(FilaDeSubcontrata, nombre).Foto();
        antes.Atributos.Should().Contain(nombre).And.Contain(cif, "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("iparra");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeSubcontrata, nombre).DebeMarcarSolo(antes, "Iparra"));
        cut.DebeConservarElIdentificadorCopiable(cif, conMarca: false);

        await cut.BuscarEnLaBarraAsync("b-20");
        cut.WaitForAssertion(() => cut.FilaDeNombre(FilaDeSubcontrata, nombre).DebeMarcarSolo(antes, "B-20", "B-20"));
        cut.DebeConservarElIdentificadorCopiable(cif, conMarca: true);
    }
}
