using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pestaña «Requisitos» de Centro 360 según la maqueta Centro360.dc.html: dos bloques (Documentación de la Empresa y del
/// Trabajador) con su recuento, 5 a la vista con los que bloquean delante, «Ver los N restantes», «Filtrar requisitos» y
/// «Gestionar requisitos».
///
/// Lo que tenía la pestaña antes del rediseño, y qué fija cada elemento ahora: una sola lista (agrupación por ámbito),
/// el ámbito como texto de la fila «Empresa empleadora · Tolerancia» (ahora lo dice el bloque; la tolerancia va en su línea),
/// «Bloquea el acceso» / «No bloquea el acceso» por fila, el botón 360 hacia el Tipo de documento (ahora el nombre es un enlace
/// a él), el estado vacío, la recarga al guardar el panel (Centro360Gen2Tests.RefrescoRequisitos) y que lo no aplicable no sale.
/// </summary>
public partial class Centro360Gen2Tests
{
    private static DocumentacionRequeridaCentroDto Requisito(string nombre, AmbitoAplicacion ambito, bool bloquea = false,
        int? tolerancia = null, int heredada = 0, bool archivado = false, string? sustituto = null, bool aplica = true, Guid? tipo = null) =>
        new(tipo ?? Guid.NewGuid(), nombre, ambito, true, aplica, aplica, null, bloquea, null, null, tolerancia, heredada, archivado, sustituto);

    private IRenderedComponent<CaeManager.Web.Features.Centros.Pages.CentroDetalle> PestanaRequisitosCon(Guid id, params DocumentacionRequeridaCentroDto[] requisitos)
    {
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Requisitos[id] = [.. requisitos];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=requisitos");
        return Renderizar(id);
    }

    private static IElement Bloque(IRenderedComponent<CaeManager.Web.Features.Centros.Pages.CentroDetalle> cut, string ambito) =>
        cut.Find($"details[data-bloque={ambito}]");

    private static List<string> NombresDe(IElement bloque) =>
        bloque.QuerySelectorAll(".centro-requisito-nombre").Select(n => n.TextContent.Trim()).ToList();

    [Fact]
    public void Requisitos_se_agrupan_en_Documentacion_de_la_Empresa_y_del_Trabajador_con_su_recuento()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("Seguro RC", AmbitoAplicacion.Empresa),
            Requisito("Mutua", AmbitoAplicacion.Empresa),
            Requisito("Formación PRL", AmbitoAplicacion.Trabajador),
            Requisito("Aptitud médica", AmbitoAplicacion.Trabajador),
            Requisito("DNI", AmbitoAplicacion.Trabajador),
            Requisito("No aplicable", AmbitoAplicacion.Empresa, aplica: false));

        var bloques = cut.FindAll("details.centro-requisitos-bloque");
        bloques.Select(b => b.QuerySelector("h3")!.TextContent.Trim()).Should().Equal("Documentación de la Empresa", "Documentación del Trabajador");
        var empresa = Bloque(cut, "empresa");
        var trabajador = Bloque(cut, "trabajador");
        empresa.QuerySelector(".centro-requisitos-recuento")!.TextContent.Trim().Should().Be("2", "lo que no aplica al Centro no cuenta");
        trabajador.QuerySelector(".centro-requisitos-recuento")!.TextContent.Trim().Should().Be("3");
        NombresDe(empresa).Should().BeEquivalentTo("Seguro RC", "Mutua");
        NombresDe(trabajador).Should().BeEquivalentTo("Formación PRL", "Aptitud médica", "DNI");
        cut.Markup.Should().NotContain("No aplicable");
        cut.FindAll(".centro-requisitos-bloques > details").Should().HaveCount(2, "los dos bloques son hermanos en la rejilla de dos columnas");
    }

    [Fact]
    public async Task Cada_bloque_muestra_5_y_Ver_los_N_restantes_despliega_el_resto()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            [.. Enumerable.Range(1, 8).Select(i => Requisito($"Empresa {i}", AmbitoAplicacion.Empresa))
                .Concat(Enumerable.Range(1, 3).Select(i => Requisito($"Trabajador {i}", AmbitoAplicacion.Trabajador)))]);

        NombresDe(Bloque(cut, "empresa")).Should().Equal("Empresa 1", "Empresa 2", "Empresa 3", "Empresa 4", "Empresa 5");
        Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.TextContent.Trim().Should().Be("Ver los 3 restantes");
        Bloque(cut, "empresa").QuerySelector(".centro-requisitos-recuento")!.TextContent.Trim().Should().Be("8", "el recuento es el total, no los visibles");
        Bloque(cut, "trabajador").QuerySelector("[data-accion=ver-restantes]").Should().BeNull("3 caben en los 5 de la vista");
        NombresDe(Bloque(cut, "trabajador")).Should().HaveCount(3);

        await Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.ClickAsync(new MouseEventArgs());

        NombresDe(Bloque(cut, "empresa")).Should().HaveCount(8).And.EndWith("Empresa 8");
        var boton = Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!;
        boton.TextContent.Trim().Should().Be("Ver menos");
        boton.GetAttribute("aria-expanded").Should().Be("true");

        await boton.ClickAsync(new MouseEventArgs());
        NombresDe(Bloque(cut, "empresa")).Should().HaveCount(5, "«Ver menos» vuelve a los 5 primeros");
    }

    [Theory]
    [InlineData(5, null)]
    [InlineData(6, "Ver el 1 restante")]
    [InlineData(7, "Ver los 2 restantes")]
    public void Ver_los_restantes_solo_aparece_a_partir_del_sexto_y_nunca_dice_1_restantes(int total, string? texto)
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            [.. Enumerable.Range(1, total).Select(i => Requisito($"R{i}", AmbitoAplicacion.Empresa))]);

        var boton = Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]");
        if (texto is null) boton.Should().BeNull();
        else boton!.TextContent.Trim().Should().Be(texto);
    }

    [Fact]
    public async Task El_despliegue_de_un_bloque_no_abre_el_otro_y_es_estado_local()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            [.. Enumerable.Range(1, 7).Select(i => Requisito($"E{i}", AmbitoAplicacion.Empresa))
                .Concat(Enumerable.Range(1, 7).Select(i => Requisito($"T{i}", AmbitoAplicacion.Trabajador)))]);

        await Bloque(cut, "trabajador").QuerySelector("[data-accion=ver-restantes]")!.ClickAsync(new MouseEventArgs());

        NombresDe(Bloque(cut, "trabajador")).Should().HaveCount(7);
        NombresDe(Bloque(cut, "empresa")).Should().HaveCount(5);
    }

    [Fact]
    public void Los_que_bloquean_el_acceso_van_delante_aunque_lleguen_los_ultimos()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("A", AmbitoAplicacion.Empresa),
            Requisito("B", AmbitoAplicacion.Empresa),
            Requisito("C", AmbitoAplicacion.Empresa),
            Requisito("D", AmbitoAplicacion.Empresa),
            Requisito("E", AmbitoAplicacion.Empresa),
            Requisito("F bloquea", AmbitoAplicacion.Empresa, bloquea: true),
            Requisito("G bloquea", AmbitoAplicacion.Empresa, bloquea: true));

        var empresa = Bloque(cut, "empresa");
        // Aunque lleguen los últimos de la lectura, los que bloquean salen en los 5 primeros y el resto conserva su orden.
        NombresDe(empresa).Should().Equal("F bloquea", "G bloquea", "A", "B", "C");
        empresa.QuerySelectorAll("[data-estado=bloquea]").Should().HaveCount(2);
        empresa.QuerySelectorAll(".centro-requisito-nombre-bloquea").Select(n => n.TextContent.Trim())
            .Should().Equal("F bloquea", "G bloquea").And.NotContain("A", "solo los que bloquean llevan el nombre en negrita");
        empresa.QuerySelector(".centro-requisito [data-estado=bloquea]")!.TextContent.Trim().Should().Be("Bloquea el acceso");
        empresa.QuerySelector("[data-accion=ver-restantes]")!.TextContent.Trim().Should().Be("Ver los 2 restantes");
    }

    [Fact]
    public async Task Filtrar_requisitos_filtra_los_dos_bloques_sin_distinguir_mayusculas_ni_tildes()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("Formación de Prevención", AmbitoAplicacion.Empresa),
            Requisito("Seguro RC", AmbitoAplicacion.Empresa),
            Requisito("Formación PRL del puesto", AmbitoAplicacion.Trabajador),
            Requisito("DNI", AmbitoAplicacion.Trabajador));
        cut.FindAll("input[placeholder='Filtrar requisitos']").Should().ContainSingle("control positivo: el campo existe");

        await cut.Find("input[placeholder='Filtrar requisitos']").InputAsync(new ChangeEventArgs { Value = "FORMACION" });

        cut.WaitForAssertion(() =>
        {
            NombresDe(Bloque(cut, "empresa")).Should().Equal("Formación de Prevención");
            NombresDe(Bloque(cut, "trabajador")).Should().Equal("Formación PRL del puesto");
        });
        Bloque(cut, "empresa").QuerySelector(".centro-requisitos-recuento")!.TextContent.Trim().Should().Be("1");

        await cut.Find("input[placeholder='Filtrar requisitos']").InputAsync(new ChangeEventArgs { Value = "zzz" });
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".centro-requisito").Should().BeEmpty();
            cut.FindAll(".centro-requisitos-vacio").Select(p => p.TextContent.Trim()).Should().Equal(
                "Ningún requisito coincide con el filtro", "Ningún requisito coincide con el filtro");
        });

        await cut.Find("input[placeholder='Filtrar requisitos']").InputAsync(new ChangeEventArgs { Value = "" });
        cut.WaitForAssertion(() => cut.FindAll(".centro-requisito").Should().HaveCount(4));
    }

    [Fact]
    public async Task El_filtro_recalcula_los_restantes_de_cada_bloque()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            [.. Enumerable.Range(1, 9).Select(i => Requisito($"Curso {i}", AmbitoAplicacion.Empresa))
                .Concat([Requisito("Otro", AmbitoAplicacion.Empresa)])]);
        Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.TextContent.Trim().Should().Be("Ver los 5 restantes");

        await cut.Find("input[placeholder='Filtrar requisitos']").InputAsync(new ChangeEventArgs { Value = "curso" });

        cut.WaitForAssertion(() =>
            Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.TextContent.Trim().Should().Be("Ver los 4 restantes"));
    }

    [Fact]
    public async Task El_filtro_y_el_despliegue_de_un_Centro_no_pasan_al_siguiente()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var cut = PestanaRequisitosCon(a,
            [.. Enumerable.Range(1, 8).Select(i => Requisito($"Curso {i}", AmbitoAplicacion.Empresa))]);
        var mediador = (MediatorFalso)Services.GetRequiredService<IMediator>();
        mediador.Detalles[b] = Detalle(b, "Centro Norte");
        mediador.Requisitos[b] = [.. Enumerable.Range(1, 8).Select(i => Requisito($"Curso B{i}", AmbitoAplicacion.Empresa))];
        await Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.ClickAsync(new MouseEventArgs());
        await cut.Find("input[placeholder='Filtrar requisitos']").InputAsync(new ChangeEventArgs { Value = "curso 1" });
        cut.WaitForAssertion(() => NombresDe(Bloque(cut, "empresa")).Should().Equal("Curso 1")); // control positivo: el filtro de A está activo

        cut.Render(p => p.Add(x => x.CentroId, b));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Centro Norte");
            NombresDe(Bloque(cut, "empresa")).Should().Equal("Curso B1", "Curso B2", "Curso B3", "Curso B4", "Curso B5");
        });
        cut.Find("input[placeholder='Filtrar requisitos']").GetAttribute("value").Should().BeNullOrEmpty();
        Bloque(cut, "empresa").QuerySelector("[data-accion=ver-restantes]")!.TextContent.Trim().Should().Be("Ver los 3 restantes");
    }

    [Fact]
    public void Un_bloque_sin_requisitos_lo_dice_en_vez_de_desaparecer()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(), Requisito("Seguro", AmbitoAplicacion.Empresa));

        Bloque(cut, "trabajador").QuerySelector(".centro-requisitos-recuento")!.TextContent.Trim().Should().Be("0");
        Bloque(cut, "trabajador").QuerySelector(".centro-requisitos-vacio")!.TextContent.Trim().Should().Be("Sin requisitos en este bloque");
    }

    [Fact]
    public void La_tolerancia_sale_en_su_propia_linea_bajo_el_nombre_heredada_o_propia()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("Formación", AmbitoAplicacion.Trabajador, bloquea: true, heredada: 5),
            Requisito("Aptitud", AmbitoAplicacion.Trabajador, tolerancia: 0, heredada: 5),
            Requisito("Reciclaje", AmbitoAplicacion.Trabajador, tolerancia: 1),
            Requisito("Una denominación muy larga que tiene que partirse en varias líneas sin cortarse", AmbitoAplicacion.Empresa, tolerancia: 10));

        var textos = cut.FindAll(".centro-requisito").ToDictionary(
            f => f.QuerySelector(".centro-requisito-nombre")!.TextContent.Trim(),
            f => f.QuerySelector(".centro-requisito-texto")!);
        textos["Formación"].QuerySelector(".centro-requisito-meta")!.TextContent.Trim().Should().Be("Tolerancia: 5 días");
        textos["Aptitud"].QuerySelector(".centro-requisito-meta")!.TextContent.Trim().Should().Be("Tolerancia: 0 días");
        textos["Reciclaje"].QuerySelector(".centro-requisito-meta")!.TextContent.Trim().Should().Be("Tolerancia: 1 día");
        foreach (var texto in textos.Values)
        {
            var nombre = texto.QuerySelector(".centro-requisito-nombre")!;
            var meta = texto.QuerySelector(".centro-requisito-meta")!;
            meta.ParentElement.Should().BeSameAs(texto, "la tolerancia no va dentro del nombre ni en su misma línea");
            meta.PreviousElementSibling.Should().BeSameAs(nombre, "va justo debajo del nombre");
            nombre.TextContent.Should().NotContain("Tolerancia");
        }
    }

    [Fact]
    public void Los_nombres_largos_se_parten_en_lineas_en_la_hoja_de_estilo()
    {
        var css = LeerCssCentroDetalle();

        // bUnit no calcula estilos: se congela la regla que evita el recorte, con control positivo de que la regla existe.
        CuerpoCss(css, ".centro-requisito-nombre").Should().MatchRegex(@"overflow-wrap\s*:\s*anywhere")
            .And.NotContain("text-overflow").And.NotContain("nowrap");
        CuerpoCss(css, ".centro-requisito-meta").Should().MatchRegex(@"overflow-wrap\s*:\s*anywhere");
        CuerpoCss(css, ".centro-requisito-texto").Should().Contain("min-width: 0");
        CuerpoCss(css, ".centro-requisitos-bloque").Should().Contain("min-width: 0");
        CuerpoCss(css, ".centro-requisitos-bloques").Should().Contain("grid-template-columns: repeat(auto-fit, minmax(min(100%, 20rem), 1fr))",
            "dos columnas en escritorio y apilados cuando no caben dos de 20 rem");
    }

    [Fact]
    public void Un_tipo_archivado_ya_exigido_no_se_muestra_como_Bloquea_el_acceso()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("Tipo antiguo", AmbitoAplicacion.Trabajador, bloquea: true, archivado: true, sustituto: "Tipo nuevo"),
            Requisito("Sin sustituto", AmbitoAplicacion.Trabajador, bloquea: true, archivado: true),
            Requisito("Vigente", AmbitoAplicacion.Trabajador, bloquea: true),
            Requisito("Otro", AmbitoAplicacion.Trabajador));

        var trabajador = Bloque(cut, "trabajador");
        var filas = trabajador.QuerySelectorAll(".centro-requisito").ToDictionary(f => f.QuerySelector(".centro-requisito-nombre")!.TextContent.Trim());
        filas["Tipo antiguo"].QuerySelector("[data-estado]")!.GetAttribute("data-estado").Should().Be("sustituido");
        filas["Tipo antiguo"].TextContent.Should().Contain("Sustituido").And.Contain("Sustituido por Tipo nuevo").And.NotContain("Bloquea el acceso");
        // Sin sustituto nada lo cubre: se dice «Archivado», no «Sustituido».
        filas["Sin sustituto"].QuerySelector("[data-estado]")!.GetAttribute("data-estado").Should().Be("archivado");
        filas["Sin sustituto"].QuerySelector("[data-estado]")!.TextContent.Trim().Should().Be("Archivado");
        filas["Sin sustituto"].TextContent.Should().NotContain("Bloquea el acceso").And.NotContain("Sustituido");
        filas["Vigente"].QuerySelector("[data-estado]")!.GetAttribute("data-estado").Should().Be("bloquea", "control positivo: el mismo requisito sin archivar sí bloquea");
        filas["Otro"].QuerySelector("[data-estado]")!.GetAttribute("data-estado").Should().Be("no-bloquea");
        NombresDe(trabajador).Should().Equal("Vigente", "Otro", "Tipo antiguo", "Sin sustituto");
        trabajador.QuerySelectorAll("[data-estado=bloquea]").Should().ContainSingle();
    }

    [Fact]
    public void Cada_fila_dice_si_bloquea_el_acceso_y_el_nombre_lleva_al_Tipo_de_documento()
    {
        var bloqueante = Guid.NewGuid();
        var libre = Guid.NewGuid();
        var cut = PestanaRequisitosCon(Guid.NewGuid(),
            Requisito("Seguro", AmbitoAplicacion.Empresa, bloquea: true, tipo: bloqueante),
            Requisito("Mutua", AmbitoAplicacion.Empresa, tipo: libre));

        var filas = cut.FindAll(".centro-requisito");
        filas[0].QuerySelector("[data-estado]")!.TextContent.Trim().Should().Be("Bloquea el acceso");
        filas[1].QuerySelector("[data-estado]")!.TextContent.Trim().Should().Be("No bloquea el acceso");
        filas[0].QuerySelector("a.centro-requisito-nombre")!.GetAttribute("href").Should().Be($"/documentos/tipos/{bloqueante}");
        filas[1].QuerySelector("a.centro-requisito-nombre")!.GetAttribute("href").Should().Be($"/documentos/tipos/{libre}");
    }

    [Fact]
    public async Task Gestionar_requisitos_abre_el_panel_de_requisitos_del_Centro()
    {
        var id = Guid.NewGuid();
        var cut = PestanaRequisitosCon(id, Requisito("Seguro", AmbitoAplicacion.Empresa));

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Gestionar requisitos").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().Be(new WorkspaceFrame(EntidadWorkspace.Centro, id, "Centro Sur", "requisitos"));
    }

    [Fact]
    public void Gestionar_requisitos_no_se_ofrece_al_rol_Consulta_pero_el_filtro_si()
    {
        this.ConRolDeEscritura("Consulta");
        var cut = PestanaRequisitosCon(Guid.NewGuid(), Requisito("Seguro", AmbitoAplicacion.Empresa));

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Gestionar requisitos");
        cut.FindAll("input[placeholder='Filtrar requisitos']").Should().ContainSingle("la lectura se conserva");
        cut.FindAll(".centro-requisito").Should().ContainSingle();
    }

    [Fact]
    public void La_pestana_conserva_la_introduccion_del_maquetado()
    {
        var cut = PestanaRequisitosCon(Guid.NewGuid(), Requisito("Seguro", AmbitoAplicacion.Empresa));

        cut.Find(".centro-requisitos-intro").TextContent.Trim()
            .Should().Be("Documentación que este Centro exige. En cada bloque, primero la que bloquea el acceso.");
    }

    private static string LeerCssCentroDetalle()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilo");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Centros", "Pages", "CentroDetalle.razor.css"));
    }

    private static string CuerpoCss(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {selector}");
        return m.Groups["cuerpo"].Value;
    }
}
