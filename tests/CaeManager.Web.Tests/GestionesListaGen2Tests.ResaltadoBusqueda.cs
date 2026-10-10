using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class GestionesListaGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /gestiones. <c>ObtenerGestionesQuery</c> busca en «Nombre
    /// Apellidos» del Trabajador (concatenado, tal como se pinta), en el nombre del Centro y en el
    /// del Tipo de documento: se marcan los tres. La columna «Tipo de documento» pasó de
    /// <c>PropertyColumn</c> a <c>TemplateColumn</c> para poder marcarla; su título y su orden los
    /// fijan los tests de orden de esta misma clase.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_trabajador_Centro_y_tipo_de_documento_y_no_cambia_nada_mas()
    {
        const string trabajador = "Javier Salas Moreno"; // de fábrica: «Centro Norte», «Formación PRL específica»
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion(trabajador), Gestion("Ana Ruiz", centro: "Planta Sur", documento: "Reconocimiento médico") }
        };
        var cut = Renderizar(mediador);
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = cut.FilaDeNombre("tbody tr", trabajador).Foto();
        antes.Texto.Should().Contain(trabajador).And.Contain("Centro Norte").And.Contain("Formación PRL específica");

        await cut.BuscarEnLaBarraAsync("salas mor");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", trabajador).DebeMarcarSolo(antes, "Salas Mor"));

        await cut.BuscarEnLaBarraAsync("norte");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", trabajador).DebeMarcarSolo(antes, "Norte"));

        await cut.BuscarEnLaBarraAsync("prl");
        cut.WaitForAssertion(() => cut.FilaDeNombre("tbody tr", trabajador).DebeMarcarSolo(antes, "PRL"));
        cut.Find("td.gestion-tipo-documento mark").TextContent.Should().Be("PRL", "la marca está en la celda del tipo de documento, que conserva su clase");
    }
}
