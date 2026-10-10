using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class VisitasGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /visitas. <c>ObtenerVisitasQuery</c> busca en el nombre del
    /// Centro y en la razón social del Cliente y de la Empresa: se marcan los tres. Los nombres
    /// accesibles de la fila («Abrir la vista rápida de la visita a …») llevan el Centro entero.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_Centro_Cliente_y_Empresa_y_no_cambia_nada_mas()
    {
        // De fábrica: Cliente «Iberojet S.A.», Empresa «Instalaciones Arbeko S.L.».
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Estación Norte"));
        mediator.Visitas.Add(Visita("Almacén Sur"));
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => Fila(cut, "Estación Norte"));
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = Fila(cut, "Estación Norte").Foto();
        antes.Atributos.Should().Contain("Estación Norte", "control: la fila tiene nombres accesibles que comparar");

        // Esta pantalla no usa BarraFiltros: su buscador es un CampoTexto suelto (el de la tecla F).
        Task Buscar(string termino) => cut.InvokeAsync(() => cut.FindComponents<CampoTexto>()
            .Single(campo => campo.FindAll("input[data-keytip=F]").Count == 1)
            .Instance.ValorChanged.InvokeAsync(termino));

        // Escrito sin acento: la marca lleva el texto original de la celda.
        await Buscar("estacion n");
        cut.WaitForAssertion(() => Fila(cut, "Estación Norte").DebeMarcarSolo(antes, "Estación N"));

        await Buscar("IBEROJET");
        cut.WaitForAssertion(() => Fila(cut, "Estación Norte").DebeMarcarSolo(antes, "Iberojet"));

        await Buscar("arbeko");
        cut.WaitForAssertion(() => Fila(cut, "Estación Norte").DebeMarcarSolo(antes, "Arbeko"));
    }
}
