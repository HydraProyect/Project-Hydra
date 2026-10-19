using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Empresa 360, pestaña Trabajadores: pastillas «Delegación» y «Oficio» (las del catálogo de ESTA Empresa) y «Agrupar».
/// Los recuentos de los chips son los de lo que queda con el filtro; agrupada, la lista se pide ordenada por el grupo y
/// cada chip filtra por sus estados en la propia consulta.
/// </summary>
public partial class Empresa360PaginaTests
{
    private static readonly Guid DelegacionEmpresaMadrid = Guid.Parse("d0000000-0000-0000-0000-00000000000a");
    private static readonly Guid DelegacionEmpresaBilbao = Guid.Parse("d0000000-0000-0000-0000-00000000000b");
    private static readonly Guid OficioSoldador = Guid.Parse("0f000000-0000-0000-0000-00000000000a");
    private static readonly Guid OficioAndamiero = Guid.Parse("0f000000-0000-0000-0000-00000000000b");

    public static TheoryData<string> TodosLosRolesDeEmpresa360 =>
        new() { Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta, Roles.Cliente };

    private static TrabajadorListaDto ConCatalogo(TrabajadorListaDto t, Guid? delegacionId, string? delegacion, Guid? oficioId, string? oficio) =>
        t with { DelegacionEmpresaId = delegacionId, DelegacionEmpresaNombre = delegacion, OficioEmpresaId = oficioId, OficioNombre = oficio };

    /// <summary>
    /// Cuatro Trabajadores: Madrid-Soldador vigente, Bilbao-Andamiero vencido, Madrid-Andamiero próximo y uno sin catálogo
    /// vigente.
    /// </summary>
    private static MediatorFalso EmpresaConCatalogo()
    {
        var mediador = Empresa(
        [
            ConCatalogo(Trabajador(EstadoDocumento.Vigente, "Alonso"), DelegacionEmpresaMadrid, "Madrid", OficioSoldador, "Soldador"),
            ConCatalogo(Trabajador(EstadoDocumento.Vencido, "Blanco"), DelegacionEmpresaBilbao, "Bilbao", OficioAndamiero, "Andamiero"),
            ConCatalogo(Trabajador(EstadoDocumento.Proximo, "Cano"), DelegacionEmpresaMadrid, "Madrid", OficioAndamiero, "Andamiero"),
            Trabajador(EstadoDocumento.Vigente, "Dueñas"),
        ]);
        mediador.Catalogos[EmpresaId] = new CatalogoTrabajadoresEmpresaDto(
            [new DelegacionEmpresaDto(DelegacionEmpresaMadrid, "Madrid", null, null, null, Guid.NewGuid()),
             new DelegacionEmpresaDto(DelegacionEmpresaBilbao, "Bilbao", null, null, null, Guid.NewGuid())],
            [new OficioEmpresaDto(OficioSoldador, "Soldador", Guid.NewGuid()),
             new OficioEmpresaDto(OficioAndamiero, "Andamiero", Guid.NewGuid())]);
        return mediador;
    }

    private static List<string> PastillasDeCatalogo(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".empresa360-pastillas .menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label")!).ToList();

    private static async Task ElegirEnPastillaDeCatalogo(IRenderedComponent<EmpresaDetalle> cut, string etiqueta, string opcion)
    {
        await cut.FindAll(".empresa360-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") == etiqueta || b.GetAttribute("aria-label")!.StartsWith(etiqueta + ": "))
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".empresa360-pastillas [role=menuitemradio]")
            .Single(i => i.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private static List<string> TitulosDeGrupo(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".empresa360-grupo").Select(h => h.GetAttribute("data-grupo")!).ToList();

    private static List<string> ApellidosDeLasFilas(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".fila-relacion-nombre").Select(a => a.TextContent.Trim().Split(' ').Last()).ToList();

    private static List<ObtenerTrabajadoresQuery> ConsultasDeLista(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().ToList();

    [Theory]
    [MemberData(nameof(TodosLosRolesDeEmpresa360))]
    public void Una_Empresa_con_catalogo_ofrece_DelegacionEmpresa_Oficio_y_Agrupar_a_todos_los_roles(string rol)
    {
        Registrar(EmpresaConCatalogo(), rol);
        var cut = Renderizar();

        PastillasDeCatalogo(cut).Should().Equal("Delegación", "Oficio", "Agrupar");
        cut.Find(".empresa360-pastillas [data-keytip=A]").Should().NotBeNull("la pastilla «Agrupar» declara su letra de KeyTips");
    }

    [Fact]
    public void Una_Empresa_sin_catalogo_no_pinta_las_pastillas()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        cut.FindAll(".empresa360-pastillas").Should().BeEmpty();
    }

    [Fact]
    public async Task Elegir_una_DelegacionEmpresa_filtra_las_consultas_cuenta_los_chips_con_ella_y_la_deja_en_la_URL()
    {
        var mediador = Registrar(EmpresaConCatalogo());
        var cut = Renderizar();
        Chips(cut)[0].Should().Contain("4", "control: sin filtro hay cuatro");

        await ElegirEnPastillaDeCatalogo(cut, "Delegación", "Madrid");

        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().BeEquivalentTo("Alonso", "Cano"));
        ConsultasDeLista(mediador).Where(q => q.DelegacionEmpresaId == DelegacionEmpresaMadrid)
            .Should().HaveCountGreaterThanOrEqualTo(4, "la página y los tres recuentos por estado llevan el filtro");
        ConsultasDeLista(mediador).Last().EmpresaId.Should().Be(EmpresaId);
        Chips(cut)[0].Should().Contain("2");
        Navegacion.Uri.Should().Contain($"delegacion={DelegacionEmpresaMadrid}");
    }

    [Fact]
    public async Task DelegacionEmpresa_y_Oficio_se_componen_y_Quitar_filtros_los_suelta()
    {
        var mediador = Registrar(EmpresaConCatalogo());
        var cut = Renderizar();

        await ElegirEnPastillaDeCatalogo(cut, "Delegación", "Madrid");
        await ElegirEnPastillaDeCatalogo(cut, "Oficio", "Soldador");
        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().Equal("Alonso"));
        ConsultasDeLista(mediador).Last().OficioEmpresaId.Should().Be(OficioSoldador);

        await ElegirEnPastillaDeCatalogo(cut, "Delegación", "Bilbao");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguno con ese filtro."));
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().HaveCount(4));
        Navegacion.Uri.Should().NotContain("delegacion=").And.NotContain("oficio=");
        ConsultasDeLista(mediador).Last().DelegacionEmpresaId.Should().BeNull();
    }

    [Fact]
    public void La_URL_con_DelegacionEmpresa_y_agrupacion_se_adopta_al_montar()
    {
        var mediador = Registrar(EmpresaConCatalogo());

        var cut = Renderizar($"?delegacion={DelegacionEmpresaMadrid}&agrupar=oficio");

        ApellidosDeLasFilas(cut).Should().BeEquivalentTo("Alonso", "Cano");
        TitulosDeGrupo(cut).Should().Equal("Andamiero", "Soldador");
        ConsultasDeLista(mediador).Last().OrdenarPor.Should().Be(nameof(TrabajadorListaDto.OficioNombre));
    }

    [Fact]
    public async Task Agrupar_Por_DelegacionEmpresa_pone_un_titulo_por_DelegacionEmpresa_y_Sin_delegacion_al_final()
    {
        var mediador = Registrar(EmpresaConCatalogo());
        var cut = Renderizar();
        cut.FindAll(".empresa360-grupo").Should().BeEmpty("control: sin agrupar no hay títulos de grupo");

        await ElegirEnPastillaDeCatalogo(cut, "Agrupar", "Por Delegación");

        cut.WaitForAssertion(() => TitulosDeGrupo(cut).Should().Equal("Bilbao", "Madrid", "Sin delegación"));
        ConsultasDeLista(mediador).Last().OrdenarPor.Should().Be(nameof(TrabajadorListaDto.DelegacionEmpresaNombre));
        Navegacion.Uri.Should().Contain("agrupar=delegacion");
        ApellidosDeLasFilas(cut).Should().Equal("Blanco", "Alonso", "Cano", "Dueñas");
    }

    [Fact]
    public async Task Agrupado_cada_chip_filtra_por_sus_estados_en_la_propia_consulta()
    {
        var mediador = Registrar(EmpresaConCatalogo());
        var cut = Renderizar("?agrupar=oficio");

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.Contains("Por vencer")).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().Equal("Cano"));
        ConsultasDeLista(mediador).Last().EstadoDocumental.Should().Be("Urgente,Proximo");
        TitulosDeGrupo(cut).Should().Equal("Andamiero");

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.Contains("Al día")).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().BeEquivalentTo("Alonso", "Dueñas"));
        ConsultasDeLista(mediador).Last().EstadoDocumental.Should().Be("SinConfirmar,Vigente,SinCaducidad");
        TitulosDeGrupo(cut).Should().Equal("Soldador", "Sin oficio");
    }

    [Fact]
    public async Task Quitar_la_agrupacion_vuelve_a_la_lista_ordenada_por_estado_sin_titulos()
    {
        Registrar(EmpresaConCatalogo());
        var cut = Renderizar("?agrupar=delegacion");
        cut.FindAll(".empresa360-grupo").Should().NotBeEmpty();

        await ElegirEnPastillaDeCatalogo(cut, "Agrupar", "Sin agrupar");

        cut.WaitForAssertion(() => cut.FindAll(".empresa360-grupo").Should().BeEmpty());
        ApellidosDeLasFilas(cut).First().Should().Be("Blanco", "sin agrupar vuelve el orden por estado: el vencido primero");
        Navegacion.Uri.Should().NotContain("agrupar=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?agrupar=oficio")]
    public async Task El_chip_Al_dia_pinta_las_mismas_filas_agrupado_o_no_incluidos_los_Sin_confirmar(string consulta)
    {
        var mediador = Empresa(
        [
            ConCatalogo(Trabajador(EstadoDocumento.Vencido, "Blanco"), DelegacionEmpresaBilbao, "Bilbao", OficioAndamiero, "Andamiero"),
            ConCatalogo(Trabajador(EstadoDocumento.SinConfirmar, "Cano"), DelegacionEmpresaMadrid, "Madrid", OficioAndamiero, "Andamiero"),
            ConCatalogo(Trabajador(EstadoDocumento.Vigente, "Alonso"), DelegacionEmpresaMadrid, "Madrid", OficioSoldador, "Soldador"),
        ]);
        mediador.Catalogos[EmpresaId] = new CatalogoTrabajadoresEmpresaDto(
            [new DelegacionEmpresaDto(DelegacionEmpresaMadrid, "Madrid", null, null, null, Guid.NewGuid())],
            [new OficioEmpresaDto(OficioAndamiero, "Andamiero", Guid.NewGuid())]);
        Registrar(mediador);
        var cut = Renderizar(consulta);

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.Contains("Al día")).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ApellidosDeLasFilas(cut).Should().BeEquivalentTo("Alonso", "Cano"));
        Chips(cut).Single(c => c.Contains("Al día")).Should().Contain("2");
    }

    [Fact]
    public void Con_agrupacion_activa_y_sin_catalogo_la_pastilla_Agrupar_sigue_y_solo_deja_quitarla()
    {
        Registrar(Empresa());

        var cut = Renderizar("?agrupar=oficio");

        PastillasDeCatalogo(cut).Should().ContainSingle().Which.Should().StartWith("Agrupar");
        cut.Find(".empresa360-pastillas [data-keytip=A]").Should().NotBeNull();
    }

    [Fact]
    public async Task Sin_catalogo_la_agrupacion_de_la_URL_se_quita_desde_la_pastilla()
    {
        Registrar(Empresa());
        var cut = Renderizar("?agrupar=oficio");
        await cut.FindAll(".empresa360-pastillas .menu-acciones-disparador-pastilla").Single().ClickAsync(new MouseEventArgs());

        cut.FindAll(".empresa360-pastillas [role=menuitemradio]").Select(i => i.TextContent.Trim())
            .Should().Equal("Sin agrupar");
        await cut.FindAll(".empresa360-pastillas [role=menuitemradio]").Single().ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".empresa360-pastillas").Should().BeEmpty());
        Navegacion.Uri.Should().NotContain("agrupar=");
    }

    [Fact]
    public async Task Agrupar_solo_ofrece_Por_Delegacion_y_Por_Oficio_si_ese_catalogo_tiene_opciones()
    {
        var mediador = Empresa();
        mediador.Catalogos[EmpresaId] = new CatalogoTrabajadoresEmpresaDto(
            [], [new OficioEmpresaDto(OficioSoldador, "Soldador", Guid.NewGuid())]);
        Registrar(mediador);
        var cut = Renderizar();

        PastillasDeCatalogo(cut).Should().Equal("Oficio", "Agrupar");
        await cut.FindAll(".empresa360-pastillas .menu-acciones-disparador-pastilla").Single(b => b.GetAttribute("aria-label") == "Agrupar")
            .ClickAsync(new MouseEventArgs());

        cut.FindAll(".empresa360-pastillas [role=menuitemradio]").Select(i => i.TextContent.Trim())
            .Should().Equal("Sin agrupar", "Por Oficio");
    }

    [Fact]
    public void Un_valor_de_agrupacion_desconocido_en_la_URL_no_agrupa()
    {
        Registrar(EmpresaConCatalogo());

        var cut = Renderizar("?agrupar=sector");

        cut.FindAll(".empresa360-grupo").Should().BeEmpty();
    }
}
