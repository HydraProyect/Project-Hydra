using Bunit;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Queries.RequisitosEvento;
using CaeManager.Domain.Proyectos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Alta de un Proyecto de tipo Evento (decisión 2026-10-15): el tipo se elige en el formulario, un evento exige fecha
/// de fin y sus requisitos documentales pueden copiarse de una plantilla o de otro evento del mismo Cliente
/// empresarial. Lo que se afirma es qué envía la pantalla en <see cref="CrearProyectoCommand"/>, no la copia, que
/// prueba el handler.
/// </summary>
public partial class ProyectosGen2Tests
{
    private static readonly Guid PlantillaId = Guid.Parse("abababab-abab-abab-abab-abababababab");
    private static readonly Guid EventoOrigenId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");

    private static readonly OrigenesRequisitosEventoDto OrigenesDeA = new(
        [new OrigenRequisitosDto(PlantillaId, "Partido estándar", 4)],
        [new OrigenRequisitosDto(EventoOrigenId, "Partido contra el líder", 3)]);

    private static Task ElegirEnAltaAsync(IRenderedComponent<Proyectos> cut, string etiqueta, string valor) =>
        SelectDelAlta(cut, etiqueta).ChangeAsync(new ChangeEventArgs { Value = valor });

    /// <summary>El valor de la opción del desplegable de origen cuyo texto es <paramref name="texto"/>.</summary>
    private static string ValorDeOrigen(IRenderedComponent<Proyectos> cut, string texto) =>
        SelectDelAlta(cut, "Requisitos documentales").QuerySelectorAll("option")
            .Single(o => o.TextContent.Trim() == texto).GetAttribute("value")!;

    private static Task EscribirFinAsync(IRenderedComponent<Proyectos> cut, string etiqueta, string fecha) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = fecha });

    private Task PulsarCrearAsync(IRenderedComponent<Proyectos> cut) =>
        BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

    private async Task<IRenderedComponent<Proyectos>> AbrirAltaDeEventoAsync()
    {
        _mediator.Origenes = OrigenesDeA;
        var cut = await AbrirNuevoProyectoAsync();
        await ElegirEnAltaAsync(cut, "Centro", CentroDeA.Id.ToString());
        await EscribirNombreDelProyectoAsync(cut, "Partido de liga — jornada 7");
        await ElegirEnAltaAsync(cut, "Tipo", nameof(TipoProyecto.Evento));
        return cut;
    }

    [Fact]
    public async Task El_alta_ofrece_el_tipo_y_solo_un_evento_pregunta_de_donde_copiar_requisitos()
    {
        _mediator.Origenes = OrigenesDeA;
        var cut = await AbrirNuevoProyectoAsync();

        OpcionesDelAlta(cut, "Tipo").Should().Equal("Obra o instalación", "Evento (partido, feria…)");
        cut.FindComponents<CampoSelect>().Select(c => c.Instance.Etiqueta).Should().NotContain("Requisitos documentales",
            "una obra no copia requisitos de evento");

        await ElegirEnAltaAsync(cut, "Tipo", nameof(TipoProyecto.Evento));

        OpcionesDelAlta(cut, "Requisitos documentales").Should().Equal(
            "Sin requisitos (los añades después)", "Partido estándar (4 requisitos)", "Partido contra el líder (3 requisitos)");
        cut.FindComponents<CampoTexto>().Select(c => c.Instance.Etiqueta).Should().Contain("Fin del evento");
        _mediator.Enviados.OfType<ObtenerOrigenesRequisitosEventoQuery>().Should().OnlyContain(q => q.ClienteId == ClienteId,
            "los orígenes son del Cliente empresarial del alta");
    }

    [Fact]
    public async Task Un_evento_sin_fecha_de_fin_no_se_envia()
    {
        var cut = await AbrirAltaDeEventoAsync();

        await PulsarCrearAsync(cut);

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().BeEmpty();
        cut.Find(".drawer-panel [role=alert]").TextContent.Trim().Should().Be("Un evento necesita fecha de fin.");
    }

    [Fact]
    public async Task Un_evento_copiado_de_una_plantilla_envia_la_plantilla_y_no_un_evento_de_origen()
    {
        var cut = await AbrirAltaDeEventoAsync();
        await EscribirFinAsync(cut, "Fin del evento", "2026-11-22");
        await ElegirEnAltaAsync(cut, "Requisitos documentales", ValorDeOrigen(cut, "Partido estándar (4 requisitos)"));

        await PulsarCrearAsync(cut);

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle().Which.Should().Match<CrearProyectoCommand>(c =>
            c.Tipo == TipoProyecto.Evento && c.FechaFinPrevista == new DateOnly(2026, 11, 22)
            && c.PlantillaRequisitosId == PlantillaId && c.CopiarRequisitosDeProyectoId == null);
    }

    [Fact]
    public async Task Un_evento_copiado_de_otro_evento_envia_el_evento_de_origen_y_no_una_plantilla()
    {
        var cut = await AbrirAltaDeEventoAsync();
        await EscribirFinAsync(cut, "Fin del evento", "2026-11-22");
        await ElegirEnAltaAsync(cut, "Requisitos documentales", ValorDeOrigen(cut, "Partido contra el líder (3 requisitos)"));

        await PulsarCrearAsync(cut);

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle().Which.Should().Match<CrearProyectoCommand>(c =>
            c.Tipo == TipoProyecto.Evento && c.CopiarRequisitosDeProyectoId == EventoOrigenId && c.PlantillaRequisitosId == null);
    }

    /// <summary>
    /// El origen elegido mientras era evento no viaja si se vuelve a obra: el handler rechazaría una obra con
    /// origen, y la pantalla no debe enviar lo que ya no enseña.
    /// </summary>
    [Fact]
    public async Task Volver_a_obra_descarta_el_origen_elegido_y_no_exige_fin()
    {
        var cut = await AbrirAltaDeEventoAsync();
        await ElegirEnAltaAsync(cut, "Requisitos documentales", ValorDeOrigen(cut, "Partido estándar (4 requisitos)"));
        await ElegirEnAltaAsync(cut, "Tipo", nameof(TipoProyecto.Obra));

        await PulsarCrearAsync(cut);

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle().Which.Should().Match<CrearProyectoCommand>(c =>
            c.Tipo == TipoProyecto.Obra && c.FechaFinPrevista == null
            && c.PlantillaRequisitosId == null && c.CopiarRequisitosDeProyectoId == null);
    }

    [Fact]
    public async Task La_fila_de_un_evento_lleva_su_pastilla_y_la_de_una_obra_no()
    {
        _mediator.Proyectos = [ProyectoAbierto with { Tipo = TipoProyecto.Evento }, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();

        cut.WaitForAssertion(() =>
        {
            var filas = cut.FindAll("tbody tr");
            var deEvento = filas.Single(f => f.TextContent.Contains(ProyectoAbierto.Nombre));
            var deObra = filas.Single(f => f.TextContent.Contains(ProyectoAbierto2.Nombre));
            deEvento.QuerySelectorAll("[data-pieza=pastilla]").Select(p => p.TextContent.Trim()).Should().Contain("Evento");
            deObra.QuerySelectorAll("[data-pieza=pastilla]").Select(p => p.TextContent.Trim()).Should().NotContain("Evento");
        }, Paciencia);
    }
}
