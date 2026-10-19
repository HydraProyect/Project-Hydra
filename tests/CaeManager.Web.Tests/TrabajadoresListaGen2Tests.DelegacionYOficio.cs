using System.Text.Json;
using Bunit;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Filtros «Delegación» y «Oficio», «Agrupar» por Delegación y por Oficio, y la etiqueta del Oficio en la fila, en la
/// lista de Trabajadores. Los dos catálogos son de cada Empresa (decisión de Chris, 2026-10-18): las pastillas ofrecen
/// el de la Empresa elegida, o el de la única que hay, y nunca mezclan catálogos de varias Empresas.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static readonly Guid DelegacionEmpresaMadrid = Guid.Parse("d0000000-0000-0000-0000-00000000000a");
    private static readonly Guid DelegacionEmpresaBilbao = Guid.Parse("d0000000-0000-0000-0000-00000000000b");
    private static readonly Guid DelegacionEmpresaMadridDeDexter = Guid.Parse("d0000000-0000-0000-0000-00000000000c");
    private static readonly Guid OficioSoldador = Guid.Parse("0f000000-0000-0000-0000-00000000000a");
    private static readonly Guid OficioAndamiero = Guid.Parse("0f000000-0000-0000-0000-00000000000b");

    private static Fila ConDelegacionEmpresaYOficio(Fila fila, Guid? delegacionId = null, string? delegacion = null,
        Guid? oficioId = null, string? oficio = null) =>
        fila with
        {
            Dto = fila.Dto with
            {
                DelegacionEmpresaId = delegacionId,
                DelegacionEmpresaNombre = delegacion,
                OficioEmpresaId = oficioId,
                OficioNombre = oficio
            }
        };

    private static void ConCatalogoDeEbro(MediatorFalso mediador) =>
        mediador.Catalogos[EmpresaEbro] = new CatalogoTrabajadoresEmpresaDto(
            [new DelegacionEmpresaDto(DelegacionEmpresaMadrid, "Madrid", null, null, null, Guid.NewGuid()),
             new DelegacionEmpresaDto(DelegacionEmpresaBilbao, "Bilbao", null, null, null, Guid.NewGuid())],
            [new OficioEmpresaDto(OficioSoldador, "Soldador", Guid.NewGuid()),
             new OficioEmpresaDto(OficioAndamiero, "Andamiero", Guid.NewGuid())]);

    /// <summary>Tres Trabajadores de Ebro (Madrid-Soldador, Bilbao-Andamiero, sin catálogo) y uno de Dexter con Madrid.</summary>
    private static MediatorFalso ConCatalogos()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                ConDelegacionEmpresaYOficio(Trabajador("Ana", "Alonso"), DelegacionEmpresaMadrid, "Madrid", OficioSoldador, "Soldador"),
                ConDelegacionEmpresaYOficio(Trabajador("Beto", "Blanco"), DelegacionEmpresaBilbao, "Bilbao", OficioAndamiero, "Andamiero"),
                Trabajador("Carla", "Cano"),
                ConDelegacionEmpresaYOficio(Trabajador("Dani", "Dueñas", empresaId: EmpresaDexter), DelegacionEmpresaMadridDeDexter, "Madrid"),
                Trabajador("Eva", "Esteban", subcontrataId: SubcontrataNervion),
            }
        };
        ConCatalogoDeEbro(mediador);
        return mediador;
    }

    private string UriActual => Services.GetRequiredService<NavigationManager>().Uri;

    // --- Pastillas: de qué Empresa son las opciones ---------------------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task Con_la_Empresa_elegida_las_pastillas_DelegacionEmpresa_y_Oficio_ofrecen_el_catalogo_de_esa_Empresa(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(ConCatalogos(), $"trabajadores?empresa={EmpresaEbro}");

        (await OpcionesDeLaPastilla(cut, "Delegación")).Should().Equal("Todas", "Madrid", "Bilbao");
        // El menú de Delegación sigue abierto al abrir el siguiente: las opciones del segundo son las tres últimas.
        (await OpcionesDeLaPastilla(cut, "Oficio")).TakeLast(3).Should().Equal("Todos", "Soldador", "Andamiero");
    }

    [Fact]
    public void Sin_Empresa_elegida_y_con_varias_Empresas_no_hay_pastillas_que_mezclarian_catalogos()
    {
        var mediador = ConCatalogos();
        mediador.Catalogos[EmpresaDexter] = new CatalogoTrabajadoresEmpresaDto(
            [new DelegacionEmpresaDto(DelegacionEmpresaMadridDeDexter, "Madrid", null, null, null, Guid.NewGuid())], []);

        var cut = Renderizar(mediador);

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Select(b => b.GetAttribute("aria-label")).Should().Equal("Empresa", "Centro", "Filtros guardados", "Agrupar");
        mediador.Enviadas.OfType<ObtenerCatalogoTrabajadoresEmpresaQuery>().Should().BeEmpty(
            "sin una Empresa en juego no se pide ningún catálogo");
    }

    [Fact]
    public void Si_una_sola_Empresa_emplea_a_Trabajadores_visibles_sus_pastillas_estan_sin_elegirla()
    {
        var mediador = ConCatalogos();
        mediador.EmpleadoresFiltro = new(
            [new CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles.EmpleadorDeTrabajadorDto(EmpresaEbro, "Montajes Ebro S.L.")], []);

        var cut = Renderizar(mediador);

        Pastilla(cut, "Delegación").Should().NotBeNull();
        Pastilla(cut, "Oficio").Should().NotBeNull();
        mediador.Enviadas.OfType<ObtenerCatalogoTrabajadoresEmpresaQuery>().Should().OnlyContain(c => c.EmpresaId == EmpresaEbro);
    }

    [Fact]
    public void Una_Empresa_sin_catalogo_no_ofrece_pastillas_vacias()
    {
        var mediador = ConCatalogos();
        mediador.Catalogos[EmpresaEbro] = CatalogoTrabajadoresEmpresaDto.Vacio;

        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Select(b => b.GetAttribute("aria-label")).Should().Equal("Empresa: Montajes Ebro S.L.", "Centro", "Filtros guardados", "Agrupar");
    }

    [Fact]
    public void Si_el_catalogo_no_carga_la_lista_se_pinta_igual_y_sin_las_pastillas()
    {
        var mediador = ConCatalogos();
        mediador.Retener = peticion => peticion is ObtenerCatalogoTrabajadoresEmpresaQuery
            ? Task.FromException<object>(new InvalidOperationException("Fallo simulado del catálogo."))
            : null;

        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        FilasDeDatos(cut).Should().HaveCount(3);
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Select(b => b.GetAttribute("aria-label")).Should().NotContain("Delegación");
    }

    [Fact]
    public async Task Un_catalogo_que_fallo_se_vuelve_a_pedir_en_la_siguiente_carga_de_la_misma_Empresa()
    {
        var mediador = ConCatalogos();
        var fallos = 0;
        mediador.Retener = peticion => peticion is ObtenerCatalogoTrabajadoresEmpresaQuery && fallos++ == 0
            ? Task.FromException<object>(new InvalidOperationException("Fallo simulado del catálogo."))
            : null;
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Select(b => b.GetAttribute("aria-label")).Should().NotContain("Delegación", "control: la primera carga falló");

        // Misma Empresa, otra carga de la lista (cambia la búsqueda en la URL).
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo($"trabajadores?empresa={EmpresaEbro}&q=a"));

        cut.WaitForAssertion(() => Pastilla(cut, "Delegación").Should().NotBeNull("el fallo no se queda anotado como catálogo cargado"));
        mediador.Enviadas.OfType<ObtenerCatalogoTrabajadoresEmpresaQuery>().Should().HaveCount(2);
    }

    // --- Filtrar ----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task Elegir_una_DelegacionEmpresa_filtra_en_el_servidor_la_pone_en_la_URL_y_lleva_su_chip(string rol)
    {
        this.ConRolDeEscritura(rol);
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        await ElegirEnLaPastilla(cut, "Delegación", "Madrid");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).DelegacionEmpresaId.Should().Be(DelegacionEmpresaMadrid));
        UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaEbro, "se compone con el filtro de Empresa");
        UriActual.Should().Contain($"delegacion={DelegacionEmpresaMadrid}");
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Alonso"));
        TextosDeLosChips(cut).Should().Contain("Delegación: Madrid");
        Pastilla(cut, "Delegación").GetAttribute("aria-label").Should().Be("Delegación: Madrid");
    }

    [Fact]
    public async Task DelegacionEmpresa_y_Oficio_se_componen_entre_si()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        await ElegirEnLaPastilla(cut, "Delegación", "Bilbao");
        await ElegirEnLaPastilla(cut, "Oficio", "Soldador");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).OficioEmpresaId.Should().Be(OficioSoldador));
        UltimaConsulta(mediador).DelegacionEmpresaId.Should().Be(DelegacionEmpresaBilbao);
        FilasDeDatos(cut).Should().BeEmpty("nadie es Soldador en Bilbao");
        cut.Markup.Should().Contain("Quitar filtros");
    }

    [Fact]
    public async Task Quitar_el_chip_de_Oficio_suelta_solo_ese_filtro()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}&delegacion={DelegacionEmpresaMadrid}&oficio={OficioSoldador}");
        UltimaConsulta(mediador).OficioEmpresaId.Should().Be(OficioSoldador, "control: la URL filtra al montar");

        await cut.Find("button[aria-label='Quitar filtro de oficio']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsulta(mediador).OficioEmpresaId.Should().BeNull());
        UltimaConsulta(mediador).DelegacionEmpresaId.Should().Be(DelegacionEmpresaMadrid);
        UriActual.Should().NotContain("oficio=").And.Contain($"delegacion={DelegacionEmpresaMadrid}");
    }

    [Fact]
    public async Task Limpiar_todo_quita_tambien_DelegacionEmpresa_y_Oficio_de_la_consulta_y_de_la_URL()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?delegacion={DelegacionEmpresaMadrid}&oficio={OficioSoldador}");

        await cut.Find("button.limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsulta(mediador).DelegacionEmpresaId.Should().BeNull());
        UltimaConsulta(mediador).OficioEmpresaId.Should().BeNull();
        UriActual.Should().NotContain("delegacion=").And.NotContain("oficio=");
    }

    [Fact]
    public async Task Cambiar_de_Empresa_suelta_DelegacionEmpresa_y_Oficio_porque_son_del_catalogo_de_la_anterior()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}&delegacion={DelegacionEmpresaMadrid}&oficio={OficioSoldador}");

        await ElegirEnLaPastilla(cut, "Empresa", "Dexter Industrial S.A.");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).EmpresaId.Should().Be(EmpresaDexter));
        UltimaConsulta(mediador).DelegacionEmpresaId.Should().BeNull();
        UltimaConsulta(mediador).OficioEmpresaId.Should().BeNull();
        UriActual.Should().NotContain("delegacion=").And.NotContain("oficio=");
    }

    [Fact]
    public void Una_URL_con_DelegacionEmpresa_de_otra_Empresa_filtra_igual_y_su_chip_se_puede_quitar()
    {
        // Sin Empresa elegida y con dos Empresas no hay catálogo cargado: el Id se aplica (es de una sola Empresa) y el
        // chip dice «Delegación» a secas, como el de Empresa cuando su Id ya no está entre las opciones.
        var mediador = ConCatalogos();

        var cut = Renderizar(mediador, $"trabajadores?delegacion={DelegacionEmpresaMadridDeDexter}");

        UltimaConsulta(mediador).DelegacionEmpresaId.Should().Be(DelegacionEmpresaMadridDeDexter);
        Columna(cut, 0).Should().Equal("Dueñas");
        TextosDeLosChips(cut).Should().Contain("Delegación");
    }

    [Fact]
    public void Un_Id_que_no_es_Guid_en_la_URL_no_filtra()
    {
        var mediador = ConCatalogos();

        Renderizar(mediador, "trabajadores?delegacion=no-es-un-id&oficio=tampoco");

        UltimaConsulta(mediador).DelegacionEmpresaId.Should().BeNull();
        UltimaConsulta(mediador).OficioEmpresaId.Should().BeNull();
    }

    // --- Filtros guardados y vista recordada -----------------------------------------------------------

    [Fact]
    public async Task Guardar_filtro_incluye_DelegacionEmpresa_y_Oficio_y_aplicarlo_los_repone()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}&delegacion={DelegacionEmpresaMadrid}&oficio={OficioSoldador}");

        await PulsarEnMasFiltros(cut, "Guardar filtro");
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = "Soldadores de Madrid" });
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var guardado = mediador.Enviadas.OfType<CaeManager.Application.Configuracion.Commands.GuardarFiltro.GuardarFiltroCommand>().Single();
        using var json = JsonDocument.Parse(guardado.ValoresJson);
        json.RootElement.GetProperty("DelegacionEmpresaId").GetString().Should().Be(DelegacionEmpresaMadrid.ToString());
        json.RootElement.GetProperty("OficioId").GetString().Should().Be(OficioSoldador.ToString());

        // Un filtro guardado ANTES de estas pastillas no lleva los dos campos y se aplica sin ellos.
        mediador.FiltrosGuardados.Add(new FiltroGuardadoDto(Guid.NewGuid(), "Antiguo", """{"Busqueda":null,"EmpresaId":null,"SubcontrataId":null}""", DateTime.UtcNow));
        mediador.FiltrosGuardados.Add(new FiltroGuardadoDto(Guid.NewGuid(), "Nuevo", guardado.ValoresJson, DateTime.UtcNow));
        var otra = Renderizar2(mediador);
        await PulsarEnMasFiltros(otra, "Antiguo");
        otra.WaitForAssertion(() => UltimaConsulta(mediador).DelegacionEmpresaId.Should().BeNull());
        await PulsarEnMasFiltros(otra, "Nuevo");
        otra.WaitForAssertion(() => UltimaConsulta(mediador).DelegacionEmpresaId.Should().Be(DelegacionEmpresaMadrid));
        UltimaConsulta(mediador).OficioEmpresaId.Should().Be(OficioSoldador);
    }

    /// <summary>Segundo montaje de la página sobre el mismo mediador (los filtros guardados se leen al montar).</summary>
    private IRenderedComponent<CaeManager.Web.Features.Trabajadores.Pages.Trabajadores> Renderizar2(MediatorFalso mediador)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("trabajadores");
        var cut = Render<CaeManager.Web.Features.Trabajadores.Pages.Trabajadores>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    [Fact]
    public void La_vista_recordada_recuerda_DelegacionEmpresa_y_Oficio()
    {
        CaeManager.Web.Features.Trabajadores.Pages.Trabajadores.ParametrosDeVista
            .Should().Contain(["delegacion", "oficio", "agrupar"]);
    }

    // --- Etiqueta del Oficio en la fila ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void La_fila_muestra_el_Oficio_como_etiqueta_y_sin_Oficio_no_hay_etiqueta(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(ConCatalogos());

        var filas = FilasDeDatos(cut);
        string? Etiqueta(string apellido) => filas.Single(f => TextoDelNombre(f).EndsWith(apellido))
            .QuerySelector(".etiqueta-oficio-trabajador")?.TextContent.Trim();

        Etiqueta("Alonso").Should().Be("Soldador");
        Etiqueta("Blanco").Should().Be("Andamiero");
        Etiqueta("Cano").Should().BeNull("sin Oficio no se pinta etiqueta");
        Etiqueta("Esteban").Should().BeNull("un Trabajador de Subcontrata no tiene Oficio");
    }

    // --- Agrupar ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agrupar_ofrece_Por_Empresa_Por_DelegacionEmpresa_y_Por_Oficio_y_su_letra_de_KeyTips_es_la_A()
    {
        var cut = Renderizar(ConCatalogos());

        (await OpcionesDeLaPastilla(cut, "Agrupar")).Should().Equal("Sin agrupar", "Por Empresa", "Por Delegación", "Por Oficio");
        Pastilla(cut, "Agrupar").GetAttribute("data-keytip").Should().Be("A");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task Agrupar_Por_DelegacionEmpresa_pone_un_titulo_por_DelegacionEmpresa_y_Sin_delegacion_al_final(string rol)
    {
        this.ConRolDeEscritura(rol);
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        await ElegirEnLaPastilla(cut, "Agrupar", "Por Delegación");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.DelegacionEmpresaNombre)));
        UriActual.Should().Contain("agrupar=delegacion");
        cut.WaitForAssertion(() => TitulosDeGrupo(cut).Should().Equal("Bilbao· 1", "Madrid· 1", "Sin delegación· 1"));
        cut.FindAll(".cabecera-grupo-trabajadores")[0].GetAttribute("data-agrupar").Should().Be("delegacion");
    }

    [Fact]
    public async Task Agrupar_Por_Oficio_pone_un_titulo_por_Oficio_y_Sin_oficio_al_final()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}");

        await ElegirEnLaPastilla(cut, "Agrupar", "Por Oficio");

        cut.WaitForAssertion(() => TitulosDeGrupo(cut).Should().Equal("Andamiero· 1", "Soldador· 1", "Sin oficio· 1"));
        UriActual.Should().Contain("agrupar=oficio");
    }

    [Fact]
    public void Con_varias_Empresas_el_titulo_de_la_DelegacionEmpresa_dice_de_cual_y_dos_DelegacionesEmpresa_homonimas_no_se_funden()
    {
        // «Madrid» existe en el catálogo de Ebro y en el de Dexter: son dos grupos, y el título dice de qué Empresa es.
        var cut = Renderizar(ConCatalogos(), "trabajadores?agrupar=delegacion");

        TitulosDeGrupo(cut).Should().Equal(
            "Bilbao · Montajes Ebro S.L.· 1",
            "Madrid · Dexter Industrial S.A.· 1",
            "Madrid · Montajes Ebro S.L.· 1",
            "Sin delegación· 2");
    }

    [Fact]
    public void Los_Trabajadores_de_Subcontrata_caen_en_Sin_delegacion_y_Sin_oficio()
    {
        var cut = Renderizar(ConCatalogos(), "trabajadores?agrupar=oficio");

        var sinOficio = cut.FindAll(".cabecera-grupo-trabajadores").Single(g => g.TextContent.StartsWith("Sin oficio"));
        sinOficio.TextContent.Should().Contain("3", "Carla, Dani y Eva, la de la Subcontrata");
        FilasDeDatos(cut).Select(f => TextoDelNombre(f)).Should().Contain("Eva Esteban");
    }

    [Fact]
    public void Agrupar_por_DelegacionEmpresa_ordena_en_el_servidor_y_manda_sobre_el_orden_de_columna()
    {
        var mediador = ConCatalogos();

        Renderizar(mediador, "trabajadores?agrupar=delegacion&orden=trabajador");

        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.DelegacionEmpresaNombre));
        UltimaConsulta(mediador).Descendente.Should().BeFalse();
    }

    [Fact]
    public async Task Ordenar_por_una_columna_deja_de_agrupar_por_DelegacionEmpresa()
    {
        var cut = Renderizar(ConCatalogos(), "trabajadores?agrupar=delegacion");
        cut.FindAll(".cabecera-grupo-trabajadores").Should().NotBeEmpty();

        await cut.FindAll(".cabecera-columnas-trabajadores button").First(b => b.TextContent.Contains("Trabajador")).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".cabecera-grupo-trabajadores").Should().BeEmpty());
        UriActual.Should().NotContain("agrupar=");
    }

    [Fact]
    public void Un_valor_de_agrupacion_desconocido_en_la_URL_no_agrupa()
    {
        var cut = Renderizar(ConCatalogos(), "trabajadores?agrupar=sector");

        cut.FindAll(".cabecera-grupo-trabajadores").Should().BeEmpty();
    }

    [Fact]
    public void La_exportacion_de_la_vista_filtrada_lleva_DelegacionEmpresa_y_Oficio()
    {
        var mediador = ConCatalogos();
        var cut = Renderizar(mediador, $"trabajadores?empresa={EmpresaEbro}&delegacion={DelegacionEmpresaMadrid}&oficio={OficioSoldador}");

        // Los criterios del enlace de exportación son los mismos que la consulta del listado.
        var enlace = cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href");
        enlace.Should().Contain($"delegacion={DelegacionEmpresaMadrid}").And.Contain($"oficio={OficioSoldador}");
    }
    private static List<string> TitulosDeGrupo(IRenderedComponent<CaeManager.Web.Features.Trabajadores.Pages.Trabajadores> cut) =>
        cut.FindAll(".cabecera-grupo-trabajadores").Select(g => g.TextContent.Trim()).ToList();
}
