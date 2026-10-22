using Bunit;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class VisitasGen2Tests
{
    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Vencido")]
    [InlineData(EstadoDocumento.Faltante, "Pendiente")]
    [InlineData(EstadoDocumento.Proximo, "Por vencer")]
    [InlineData(EstadoDocumento.Vigente, "Sin incidencias")]
    public void La_fila_usa_el_estado_documental_de_la_consulta(EstadoDocumento estado, string texto)
    {
        var mediator = new MediatorVisitas
        {
            Visitas = { Visita("Centro Norte") with { PeorEstadoDocumental = estado, TrabajadoresListos = 1, HoraEstimadaAcceso = new TimeOnly(8, 30) } }
        };
        var cut = Renderizar(mediator);
        var fila = Fila(cut, "Centro Norte");
        fila.QuerySelector(".col-estado")!.TextContent.Should().Contain(texto);
        fila.TextContent.Should().Contain("1/3").And.Contain("Entrada 08:30");
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Design_conserva_los_enlaces_y_limita_las_acciones_por_rol(string rol, bool escritura)
    {
        this.ConRolDeEscritura(rol);
        var visita = Visita("Centro Norte");
        var documento = new DocumentoVisitaItemDto(null, Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Faltante, null, null);
        var mediator = new MediatorVisitas
        {
            Visitas = { visita },
            Documentacion = new(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
                [new(documento.TrabajadorId!.Value, "Ana López", "Empresa", new(EstadoDocumento.Faltante, [documento]))])
        };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => Fila(cut, "Centro Norte"));
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "+ Nueva visita").Should().Be(escritura);
        Fila(cut, "Centro Norte").QuerySelector($"a[href='/visitas/{visita.Id}']").Should().NotBeNull();
        Fila(cut, "Centro Norte").QuerySelectorAll(".visitas-identidad button").Should().HaveCount(2);
        var panel = Services.GetRequiredService<ContextWorkspaceService>();
        await Fila(cut, "Centro Norte").QuerySelectorAll(".visitas-identidad button")[0].ClickAsync(new MouseEventArgs());
        panel.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Cliente);
        panel.FrameActual.EntidadId.Should().Be(visita.ClienteId);
        await Fila(cut, "Centro Norte").QuerySelectorAll(".visitas-identidad button")[1].ClickAsync(new MouseEventArgs());
        panel.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Empresa);
        panel.FrameActual.EntidadId.Should().Be(visita.EmpresaId);
        await Fila(cut, "Centro Norte").QuerySelector(".visitas-expandir")!.ClickAsync(new MouseEventArgs());
        var detalle = Fila(cut, "Centro Norte").NextElementSibling!;
        detalle.TextContent.Should().Contain("Ana López").And.Contain("Aptitud médica").And.Contain("Centro: Centro Norte");
        detalle.QuerySelectorAll("[data-accion=gestionar]").Should().HaveCount(escritura ? 1 : 0);
        Interruptor(cut, "Centro Norte").HasAttribute("disabled").Should().Be(!escritura);
        await detalle.QuerySelector(".visitas-datos-conservados > button")!.ClickAsync(new MouseEventArgs());
        panel.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Centro);
        panel.FrameActual.EntidadId.Should().Be(visita.CentroId);
        mediator.Comandos.Should().BeEmpty("abrir una fila no notifica ni envía correo");
        await BotonVistaRapida(cut, "Centro Norte").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => PanelAbierto(cut).Should().BeTrue());
        cut.FindAll(SelectorLapiz).Should().HaveCount(escritura ? 1 : 0);
    }

    [Fact]
    public async Task Agrupar_pide_el_orden_global_al_servidor_y_no_envia_comandos()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        FiltrosVisitasDePrueba.Pastilla(cut, "Agrupar").Click();
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por Cliente").ClickAsync(new MouseEventArgs());
        mediator.ConsultasDeLista.Last().AgruparPorCliente.Should().BeTrue();
        cut.Find(".visitas-grupo").TextContent.Should().Contain("Iberojet S.A.");
        mediator.Comandos.Should().BeEmpty();
    }

    /// <summary>
    /// Humo del bloque 1 de CAMBIOS-SESION-2026-10-21 en Visitas: el grupo nace plegado (sin filas), su cabecera es un botón con
    /// aria-expanded, dice «· N», agrupada se pide todo (el tamaño de página no corta los grupos) y un clic lo abre.
    /// </summary>
    [Fact]
    public async Task Agrupar_nace_plegado_con_cabecera_boton_y_se_abre_con_un_clic()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        FiltrosVisitasDePrueba.Pastilla(cut, "Agrupar").Click();
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por Cliente").ClickAsync(new MouseEventArgs());

        mediator.ConsultasDeLista.Last().TamanoPagina.Should().BeGreaterThan(100, "agrupada se piden todas las filas filtradas");
        var cabecera = cut.Find(".visitas-grupo button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".visitas-grupo .grupo-lista-contador").TextContent.Trim().Should().Be("· 1");
        cut.FindAll("tr[data-id]").Should().BeEmpty("todos los grupos empiezan plegados");

        await cabecera.ClickAsync(new MouseEventArgs());

        cut.Find(".visitas-grupo button.grupo-lista-cabecera").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("tr[data-id]").Should().ContainSingle();
    }

    [Fact]
    public async Task Ventana_se_aplica_en_consulta_y_se_quita_con_su_chip()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Ventana", "Próximos 7 días");
        mediator.ConsultasDeLista.Last().VentanaDias.Should().Be(7);
        await cut.Find("button[aria-label='Quitar filtro de Ventana']").ClickAsync(new MouseEventArgs());
        mediator.ConsultasDeLista.Last().VentanaDias.Should().BeNull();
    }

    [Fact]
    public async Task La_ventana_admite_varias_opciones_y_quitar_un_chip_conserva_la_otra()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Ventana", "Hoy");
        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Ventana", "Próximos 7 días");
        cut.FindAll("[role=menuitemcheckbox][aria-checked=true]").Should().HaveCount(2);
        mediator.ConsultasDeLista.Last().VentanaDias.Should().Be(7);
        await cut.FindAll(".chip-filtro").Single(c => c.TextContent.Contains("Próximos 7 días"))
            .QuerySelector("button")!.ClickAsync(new MouseEventArgs());
        mediator.ConsultasDeLista.Last().VentanaDias.Should().Be(0);
    }

    [Fact]
    public async Task Expandir_y_contraer_todo_solo_consulta_la_preparacion()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte"), Visita("Planta Sur") } };
        var cut = Renderizar(mediator);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Expandir todo").ClickAsync(new MouseEventArgs());
        cut.FindAll(".visitas-expandir[aria-expanded=true]").Should().HaveCount(2);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Contraer todo").ClickAsync(new MouseEventArgs());
        cut.FindAll(".visitas-fila-desplegada").Should().BeEmpty();
        mediator.Comandos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_error_de_preparacion_se_puede_reintentar_sin_ninguna_escritura()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") }, FallarDocumentacion = true };
        var cut = Renderizar(mediator);
        await Fila(cut, "Centro Norte").QuerySelector(".visitas-expandir")!.ClickAsync(new MouseEventArgs());
        cut.Find(".visitas-fila-desplegada [role=alert]").TextContent.Should().Contain("No pudimos cargar la preparación");
        mediator.FallarDocumentacion = false;
        mediator.Documentacion = new(Guid.NewGuid(), new(EstadoDocumento.Vigente, []), []);
        await cut.FindAll(".visitas-fila-desplegada button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());
        cut.Find(".visitas-fila-desplegada").TextContent.Should().Contain("No hay trabajadores en esta visita.");
        mediator.Comandos.Should().BeEmpty();
    }

    [Fact]
    public async Task La_carga_antigua_de_preparacion_no_pisa_la_fila_tras_cambiar_de_filtro()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") }, DiferirDocumentacion = true };
        var cut = Renderizar(mediator);
        var primera = Fila(cut, "Centro Norte").QuerySelector(".visitas-expandir")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediator.CargasDocumentacionPendientes.Should().HaveCount(1));
        cut.Find(".visitas-fila-desplegada [role=status]").TextContent.Should().Contain("Cargando");
        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Solo urgentes", "Sí");
        var segunda = Fila(cut, "Centro Norte").QuerySelector(".visitas-expandir")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediator.CargasDocumentacionPendientes.Should().HaveCount(2));
        static DocumentacionVisitaDto Documentacion(string nombre) => new(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
            [new(Guid.NewGuid(), nombre, "Empresa", new(EstadoDocumento.Vigente, []))]);
        await cut.InvokeAsync(() => mediator.CargasDocumentacionPendientes[1].SetResult(Documentacion("Preparación actual")));
        await segunda;
        await cut.InvokeAsync(() => mediator.CargasDocumentacionPendientes[0].SetResult(Documentacion("Preparación antigua")));
        await primera;
        cut.Find(".visitas-fila-desplegada").TextContent.Should().Contain("Preparación actual").And.NotContain("Preparación antigua");
    }
}
