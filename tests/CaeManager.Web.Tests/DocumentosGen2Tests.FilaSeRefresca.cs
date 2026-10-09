using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /documentos se refresca en sitio tras guardar en la vista rápida (panel del Context
/// Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa por
/// <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO esa
/// fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado.
///
/// <para>
/// El almacén de estos tests APLICA lo que recibe de la consulta de lista: el filtro por
/// <c>DocumentoId</c>, la búsqueda por tipo de documento, el orden por vencimiento (con el tipo
/// de desempate) y la paginación, con el total de los coincidentes. No reproduce los demás
/// filtros ni el alcance de cartera: ningún test de aquí depende de ellos.
/// </para>
/// </summary>
public partial class DocumentosGen2Tests
{
    private sealed class AlmacenDeDocumentos
    {
        public List<DocumentoListaDto> Documentos { get; } = [];

        /// <summary>La consulta de una sola fila falla (la de página no).</summary>
        public bool FallarRelectura { get; set; }

        public MediadorControlado Mediador()
        {
            var mediador = new MediadorControlado();
            mediador.Interceptar = peticion => peticion switch
            {
                ObtenerDocumentosQuery { DocumentoId: not null } when FallarRelectura =>
                    Task.FromException<object?>(new InvalidOperationException("Fallo simulado de la relectura.")),
                ObtenerDocumentosQuery q => Task.FromResult<object?>(Filtrar(q)),
                _ => null
            };
            return mediador;
        }

        private ResultadoPaginado<DocumentoListaDto> Filtrar(ObtenerDocumentosQuery q)
        {
            var coincidentes = Documentos
                .Where(d => q.DocumentoId is null || d.Id == q.DocumentoId)
                .Where(d => string.IsNullOrWhiteSpace(q.Busqueda) || d.TipoDocumentoNombre.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.FechaVencimiento).ThenBy(d => d.TipoDocumentoNombre, StringComparer.Ordinal)
                .ToList();
            var pagina = coincidentes.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<DocumentoListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
        }

        /// <summary>Cambia lo guardado, como haría el comando de edición del panel (fechas del documento).</summary>
        public void CambiarVencimiento(Guid id, DateOnly vencimiento)
        {
            var indice = Documentos.FindIndex(d => d.Id == id);
            Documentos[indice] = Documentos[indice] with { FechaVencimiento = vencimiento };
        }
    }

    private static readonly DateOnly VencimientoNuevo = new(2031, 12, 24);
    private const string VencimientoNuevoPintado = "24/12/2031";

    /// <summary>«Documento NN», que vence el día NN de enero de 2027: el orden por vencimiento es el de su número.</summary>
    private static DocumentoListaDto DocumentoNumerado(int numero) => new(
        Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Javier Salas Moreno", $"Documento {numero:00}",
        new DateOnly(2026, 1, 15), new DateOnly(2027, 1, numero), EstadoDocumento.Vigente,
        ArchivoUrl: null, Acreditaciones: []);

    private static AlmacenDeDocumentos AlmacenCon(int cuantos)
    {
        var almacen = new AlmacenDeDocumentos();
        for (var i = 1; i <= cuantos; i++)
            almacen.Documentos.Add(DocumentoNumerado(i));
        return almacen;
    }

    private (IRenderedComponent<PaginaDocumentos> Cut, MediadorControlado Mediador) RenderizarAlmacen(
        AlmacenDeDocumentos almacen, string url = "documentos")
    {
        var (cut, mediador) = Renderizar(almacen.Mediador(), url);
        cut.WaitForAssertion(() => FilasConDocumento(cut).Should().NotBeEmpty("la lista terminó de cargar"));
        return (cut, mediador);
    }

    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<PaginaDocumentos> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    /// <summary>Filas con datos (QuickGrid rellena la página con filas vacías).</summary>
    private static List<IElement> FilasConDocumento(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll(".tabla-datos tbody tr").Where(tr => tr.QuerySelector("button.enlace-nombre-fila") is not null).ToList();

    /// <summary>El tipo de documento de cada fila, en el orden en que se pintan.</summary>
    private static List<string> TiposDeLasFilas(IRenderedComponent<PaginaDocumentos> cut) =>
        FilasConDocumento(cut).Select(tr => Regex.Match(tr.TextContent, @"Documento \d\d").Value).ToList();

    private static IElement FilaDelTipo(IRenderedComponent<PaginaDocumentos> cut, string tipo) =>
        FilasConDocumento(cut).Single(tr => tr.TextContent.Contains(tipo, StringComparison.Ordinal));

    private static int ConsultasDeLista(MediadorControlado mediador) => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();

    private static async Task IrALaPagina2DeDocumentosAsync(IRenderedComponent<PaginaDocumentos> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => TiposDeLasFilas(cut).Should().HaveCount(5).And.StartWith("Documento 21"));
    }

    /// <summary>
    /// El vencimiento nuevo mandaría la fila al final con el orden por vencimiento: que siga la
    /// primera es lo que distingue «sustituir en sitio» de «recargar la página».
    /// </summary>
    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var almacen = AlmacenCon(2);
        var editado = almacen.Documentos[0].Id;
        var (cut, mediador) = RenderizarAlmacen(almacen);
        TiposDeLasFilas(cut).Should().Equal(["Documento 01", "Documento 02"], "punto de partida");
        FilaDelTipo(cut, "Documento 01").TextContent.Should().Contain("01/01/2027").And.NotContain(VencimientoNuevoPintado);
        var consultasAntes = ConsultasDeLista(mediador);

        almacen.CambiarVencimiento(editado, VencimientoNuevo);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, editado);

        cut.WaitForAssertion(() => FilaDelTipo(cut, "Documento 01").TextContent.Should().Contain(VencimientoNuevoPintado,
            "la fila enseña el dato nuevo"));
        TiposDeLasFilas(cut).Should().Equal(["Documento 01", "Documento 02"], "y no cambia de sitio");
        UltimaConsultaDeDocumentos(mediador).DocumentoId.Should().Be(editado, "se pide solo esa fila");
        UltimaConsultaDeDocumentos(mediador).Busqueda.Should().BeNull();
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count(q => q.DocumentoId is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_y_la_fila_enfocada()
    {
        var almacen = AlmacenCon(25);
        var editado = almacen.Documentos.Single(d => d.TipoDocumentoNombre == "Documento 23").Id;
        var (cut, mediador) = RenderizarAlmacen(almacen);
        await IrALaPagina2DeDocumentosAsync(cut);
        await BotonPorTexto(cut, ".cabecera-pagina button[aria-label]", "Selección múltiple").ClickAsync(new MouseEventArgs());
        await FilaDelTipo(cut, "Documento 22").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await FilaDelTipo(cut, "Documento 23").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        FilasConDocumento(cut)[0].ClassList.Should().Contain("fila-enfocada", "punto de partida: la j enfocó la primera fila");
        var consultasAntes = ConsultasDeLista(mediador);

        almacen.CambiarVencimiento(editado, VencimientoNuevo);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, editado);

        cut.WaitForAssertion(() => FilaDelTipo(cut, "Documento 23").TextContent.Should().Contain(VencimientoNuevoPintado));
        TiposDeLasFilas(cut).Should().Equal("Documento 21", "Documento 22", "Documento 23", "Documento 24", "Documento 25");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 documento(s)");
        FilasConDocumento(cut).Select(tr => tr.QuerySelector("input[type=checkbox]")!.HasAttribute("checked")).Should().Equal(
            [false, true, true, false, false], "la selección sigue marcada, también la de la fila sustituida");
        FilasConDocumento(cut).Select(tr => tr.ClassList.Contains("fila-enfocada")).Should().Equal(true, false, false, false, false);
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "una carga de página habría limpiado selección y foco");
        UltimaConsultaDeDocumentos(mediador).DocumentoId.Should().Be(editado);
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, un Documento que no existe y uno que está en la página 1 mientras se mira la 2.
    /// El aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así
    /// que al volver de cada uno ya no queda nada pendiente; el control positivo del final
    /// demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var almacen = AlmacenCon(25);
        var dePagina1 = almacen.Documentos.Single(d => d.TipoDocumentoNombre == "Documento 01").Id;
        var dePagina2 = almacen.Documentos.Single(d => d.TipoDocumentoNombre == "Documento 21").Id;
        var (cut, mediador) = RenderizarAlmacen(almacen);
        await IrALaPagina2DeDocumentosAsync(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        almacen.CambiarVencimiento(dePagina1, VencimientoNuevo);
        almacen.CambiarVencimiento(dePagina2, VencimientoNuevo);

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        FilaDelTipo(cut, "Documento 21").TextContent.Should().NotContain(VencimientoNuevoPintado, "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, dePagina2);

        cut.WaitForAssertion(() => FilaDelTipo(cut, "Documento 21").TextContent.Should().Contain(VencimientoNuevoPintado));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var almacen = AlmacenCon(2);
        almacen.FallarRelectura = true;
        var editado = almacen.Documentos[0].Id;
        var (cut, mediador) = RenderizarAlmacen(almacen);
        var consultasAntes = ConsultasDeLista(mediador);

        almacen.CambiarVencimiento(editado, VencimientoNuevo);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, editado);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        FilaDelTipo(cut, "Documento 01").TextContent.Should().Contain("01/01/2027", "la fila conserva el dato anterior");
        Toasts().Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        almacen.FallarRelectura = false;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, editado);

        cut.WaitForAssertion(() => FilaDelTipo(cut, "Documento 01").TextContent.Should().Contain(VencimientoNuevoPintado));
    }
}
