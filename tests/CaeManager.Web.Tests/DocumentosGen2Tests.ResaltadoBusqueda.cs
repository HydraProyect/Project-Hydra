using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class DocumentosGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /documentos. <c>ObtenerDocumentosQuery</c> busca en la entidad
    /// asociada (<c>PropietarioNombre</c>, ya compuesto: «Apellidos, Nombre», «Vehículo
    /// (matrícula)»…) y en el nombre del Tipo de documento: se marcan los dos. La columna «Tipo de
    /// documento» pasó de <c>PropertyColumn</c> a <c>TemplateColumn</c> para poder marcarla; su
    /// título y su orden los fijan los tests de orden de esta misma clase. El doble de mediador no
    /// filtra por texto: la fila es la misma con y sin búsqueda, y por eso aquí sí se ve que
    /// «medico» marca «médico».
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_la_entidad_asociada_y_el_tipo_de_documento_y_no_cambia_nada_mas()
    {
        const string entidad = "Salas Moreno, Javier"; // la de fábrica
        var (cut, _) = Renderizar(ConDocumentos(Documento("Reconocimiento médico")));
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", entidad));
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre("tbody tr", entidad).Foto();
        antes.Texto.Should().Contain(entidad).And.Contain("Reconocimiento médico");
        antes.Atributos.Should().Contain("Reconocimiento médico", "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("moreno, j");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", entidad).DebeMarcarSolo(antes, "Moreno, J"));

        await cut.BuscarEnLaBarraAsync("reconocimiento medico");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", entidad).DebeMarcarSolo(antes, "Reconocimiento médico"));
    }
}
