using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Web.Features.Clientes.Pages;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// El mismo asistente /importacion contra los mockups «Importar datos
/// TALVEG.dc.html» e «Importar Combinado TALVEG.dc.html». Este último declara
/// que /clientes/importar-combinado solo redirige y que lo dibujado es el
/// asistente con la plantilla Combinada: lo que pide se prueba aquí con
/// ?plantilla=combinada, y la redirección, sin convertirla en página.
///
/// <para>
/// Mismo arnés y mismos límites que la otra mitad de esta clase: observa lo
/// que llega al mediador y lo que se pinta con lo que vuelve; no el aspecto
/// (bUnit no evalúa CSS), ni que el lector y el handler reales hagan lo que el
/// doble imita. Las hojas del paso 2 sí se comparan con la plantilla real que
/// genera <see cref="ClosedXmlPlantillaCombinadaService"/>.
/// </para>
/// </summary>
public partial class ImportarClientesGen2Tests
{
    private const string ContenidoCombinada = "combinada";

    /// <summary>
    /// El plan del mockup «Importar Combinado»: 9 altas y 4 reutilizados. Lo
    /// que cada nodo prueba del árbol:
    /// Montajes Ebro se asocia a «instalaciones vidal s.l.» en minúsculas (el
    /// lector compara sin mayúsculas) y cuelga además de Refrielectric por su
    /// Centro, así que sale en los dos con su Trabajador; Grupo Previo y
    /// Empresa Previa no vienen en su hoja, luego ya existían; Talleres
    /// Sueltos no tiene Cliente empresarial y Contratas Previas solo la cita
    /// un Trabajador.
    /// </summary>
    private static PlanImportacionCombinadaDto PlanDelMockupCombinado(
        IEnumerable<ItemImportacionDto>? advertencias = null, IEnumerable<ItemImportacionDto>? omitidos = null) =>
        new(
            [
                new ClienteImportadoDto("Instalaciones Vidal S.L.", "B98765432", false, YaExiste: false),
                new ClienteImportadoDto("Refrielectric S.L.", "B12345674", true, YaExiste: true)
            ],
            [
                new EmpresaCombinadaImportadaDto("Vidal Montajes S.L.", ["Instalaciones Vidal S.L."], YaExiste: false),
                new EmpresaCombinadaImportadaDto("Montajes Ebro S.A.", ["instalaciones vidal s.l."], YaExiste: true),
                new EmpresaCombinadaImportadaDto("Aislamientos Nervión S.L.", ["Refrielectric S.L."], YaExiste: false),
                new EmpresaCombinadaImportadaDto("Talleres Sueltos S.L.", [], YaExiste: false)
            ],
            [
                new CentroImportadoDto("Planta Sagunto", "Instalaciones Vidal S.L.", "Vidal Montajes S.L.", "VS-01", null, null, new DateOnly(2026, 12, 31), YaExiste: false),
                new CentroImportadoDto("Centro Logístico Sur", "Refrielectric S.L.", "Montajes Ebro S.A.", null, null, null, null, YaExiste: true),
                new CentroImportadoDto("Nave Previa", "Grupo Previo S.A.", "Empresa Previa S.L.", null, null, null, null, YaExiste: false)
            ],
            [
                new TrabajadorImportadoDto("Vidal Montajes S.L.", "Iker", "Mena Ruíz", "12345678Z", null, null, YaExiste: false),
                new TrabajadorImportadoDto("Montajes Ebro S.A.", "Laura", "Ortiz Gil", "87654321X", null, null, YaExiste: true),
                new TrabajadorImportadoDto("Talleres Sueltos S.L.", "Marco", "Vila Sanz", "11223344A", null, null, YaExiste: false),
                new TrabajadorImportadoDto("Contratas Previas S.L.", "Ana", "Cid Puente", "55667788B", null, null, YaExiste: false)
            ],
            [.. advertencias ?? []],
            [.. omitidos ?? []]);

    private (IRenderedComponent<PaginaImportacion> Cut, MediadorControlado Mediador) RenderizarCombinada(PlanImportacionCombinadaDto plan)
    {
        var escenario = new Escenario();
        escenario.PlanesCombinados[ContenidoCombinada] = plan;
        return Renderizar(escenario, url: "importacion?plantilla=combinada");
    }

    private static async Task LlevarCombinadaAlPlanAsync(IRenderedComponent<PaginaImportacion> cut)
    {
        await Pulsar(cut, "Continuar con Combinada");
        await Subir(cut, "alta-grupo-vidal.xlsx", ContenidoCombinada);
        await Pulsar(cut, "Ver plan de importación");
    }

    /// <summary>
    /// Una línea por nodo, con sus piezas (nombre, dato, estado) separadas por
    /// un espacio: en el DOM van pegadas —las separa el gap del flex—, así que
    /// TextContent las juntaría.
    /// </summary>
    private static IReadOnlyList<string> DescribirArbol(IRenderedComponent<PaginaImportacion> cut) =>
        cut.FindAll("[data-nodo-cliente], [data-nodo-empresa], [data-nodo-hijo]")
            .Select(nodo =>
            {
                var (sangria, fila) = nodo.HasAttribute("data-nodo-hijo") ? ("    ", nodo)
                    : nodo.HasAttribute("data-nodo-empresa") ? ("  ", nodo.QuerySelector(".arbol-plan-fila")!)
                    : ("", nodo.QuerySelector(".arbol-plan-fila")!);
                return sangria + string.Join(" ", fila.Children.Select(Texto).Where(t => t.Length > 0));
            })
            .ToList();

    private static IReadOnlyList<string> Badges(IRenderedComponent<PaginaImportacion> cut) =>
        cut.FindAll(".badges-resumen-plan .badge").Select(Texto).ToList();

    // ---------------------------------------------------------------- Importar datos: paso 1

    [Theory]
    [InlineData("cae", "Importación CAE completa", "Importación CAE completa (multi-hoja)")]
    [InlineData("clientes", "Plantilla de Clientes", "Plantilla de Clientes")]
    [InlineData("combinada", "Combinada", "Combinada: Cliente + Empresas + Centros + Trabajadores")]
    [InlineData("documentos", "Documentos", "Documentos")]
    public async Task Continuar_lleva_el_nombre_corto_del_mockup_y_la_zona_de_soltar_el_titulo_entero(
        string plantilla, string nombreCorto, string titulo)
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");

        await OpcionPlantilla(cut, plantilla).ClickAsync(new MouseEventArgs());
        await Pulsar(cut, $"Continuar con {nombreCorto}");

        TituloDeLaSeccion(cut).Should().Be("Sube el archivo");
        Normalizar(cut.Markup).Should().Contain($"Arrastra el archivo de {titulo}",
            "la zona de soltar conserva el título entero, como el mockup «Importar Combinado»");
    }

    [Fact]
    public void Cada_tarjeta_pinta_su_icono_del_catalogo_y_no_unas_siglas()
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");

        var dibujos = OrdenPlantillas.Select(id =>
        {
            var hueco = cut.Find($"[data-plantilla='{id}'] .icono-tarjeta-plantilla");
            Texto(hueco).Should().BeEmpty($"«{id}» ya no pinta siglas");
            var svg = hueco.QuerySelector("svg.icono");
            svg.Should().NotBeNull($"«{id}» pinta un Icono del catálogo");
            svg!.InnerHtml.Trim().Should().NotBeEmpty($"un nombre que el catálogo no conoce da un svg vacío («{id}»)");
            return svg.InnerHtml;
        }).ToList();

        dibujos.Should().OnlyHaveUniqueItems("cada plantilla lleva el icono del mockup: importar, clientes, empresas, documentos");
    }

    // ---------------------------------------------------------------- Importar Combinado: ruta y cabecera

    [Fact]
    public void La_ruta_de_la_Combinada_sigue_siendo_solo_una_redireccion_y_exige_Administrador()
    {
        Render<ImportarCombinado>();

        Services.GetRequiredService<NavigationManager>().Uri
            .Should().EndWith("/importacion?plantilla=combinada", "el mockup declara que /clientes/importar-combinado solo redirige (H-1)");
        typeof(ImportarCombinado).GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Should().ContainSingle().Which.Roles.Should().Be(Roles.Administrador);
    }

    [Fact]
    public async Task Desde_Clientes_con_la_Combinada_la_cabecera_es_la_del_mockup_Importar_Combinado()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importación combinada");
        Texto(cut.Find(".cabecera-pagina-kicker")).Should().Be("Configuración");
        cut.Find("a.enlace-volver-importacion").GetAttribute("href").Should().Be("/clientes");
        Texto(cut.Find(".miga-importacion")).Should().Be("Negocio → Clientes → Importación combinada");
        var entradilla = Texto(cut.Find(".cabecera-pagina-descripcion"));
        entradilla.Should().StartWith("Estructura organizativa completa sin documentos").And.Contain("cuatro hojas");
        Regex.Matches(entradilla, @"\b[Cc]lientes?\b(?! empresarial)").Should()
            .BeEmpty("la hoja «Clientes» crea Clientes empresariales: nunca «cliente» a secas en un texto nuevo");

        await OpcionPlantilla(cut, "cae").ClickAsync(new MouseEventArgs());

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importar datos", "con otra plantilla ya no es la importación combinada");
        cut.FindAll(".cabecera-pagina-descripcion").Should().BeEmpty("la entradilla es de la Combinada");
        Texto(cut.Find("a.enlace-volver-importacion")).Should().Be("Volver a Clientes", "se sigue habiendo llegado desde Clientes");
    }

    [Fact]
    public async Task Elegir_la_Combinada_sin_venir_de_Clientes_no_cambia_la_cabecera_de_Importar_datos()
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");

        await OpcionPlantilla(cut, "combinada").ClickAsync(new MouseEventArgs());

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importar datos");
        cut.FindAll(".cabecera-pagina-descripcion").Should().BeEmpty();
        cut.FindAll("a.enlace-volver-importacion").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Importar Combinado: paso 2

    [Fact]
    public async Task Las_hojas_del_paso_2_son_las_de_la_plantilla_Combinada_que_se_descarga_y_en_su_orden()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());
        await Pulsar(cut, "Continuar con Combinada");

        using var libro = new XLWorkbook(new MemoryStream(new ClosedXmlPlantillaCombinadaService(null!, null!, null!).GenerarPlantilla()));
        var hojas = cut.FindAll("li[data-hoja-combinada]");

        hojas.Select(h => h.GetAttribute("data-hoja-combinada")).Should()
            .Equal(libro.Worksheets.Select(w => w.Name), "son las hojas del libro que se descarga, en el orden en que se leen");
        hojas.Select(h => Texto(h.QuerySelector(".numero-hoja-combinada")!)).Should().Equal("1", "2", "3", "4");
        foreach (var (hoja, elemento) in libro.Worksheets.Zip(hojas))
        {
            var cabecera = hoja.Row(1).CellsUsed().Select(c => c.GetString());
            Texto(elemento.QuerySelector(".columnas-hoja-combinada")!).Should()
                .Be(string.Join(" · ", cabecera), $"las columnas de «{hoja.Name}» son las de la cabecera que escribe GenerarPlantilla");
        }

        Texto(cut.Find("[data-hoja-combinada='Clientes'] .regla-hoja-combinada")).Should().Contain("Sin CIF válido, la fila se omite entera");
    }

    [Fact]
    public async Task Con_otra_plantilla_el_paso_2_no_enseña_las_hojas_de_la_Combinada()
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");
        await Pulsar(cut, "Continuar con Importación CAE completa");

        cut.FindAll(".hojas-combinada-importacion").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Importar Combinado: paso 3

    [Fact]
    public async Task El_plan_de_la_Combinada_cuenta_aparte_lo_que_ya_existe()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());
        await LlevarCombinadaAlPlanAsync(cut);

        Badges(cut).Should().Equal(
            "9 se crearán", "4 ya existen · se reutilizan", "0 con aviso", "0 se omitirán");
    }

    [Fact]
    public async Task Las_plantillas_simples_no_cuentan_reutilizados_porque_no_actualizan_nada()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.", yaExisteCliente: true, yaExisteCentro: true)]);
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "a.xlsx", "A");
        await Pulsar(cut, "Ver plan de importación");

        Badges(cut).Should().NotContain(b => b.Contains("se reutilizan"));
        cut.FindAll(".vistas-plan-importacion").Should().BeEmpty("el árbol es de la Combinada");
        cut.FindAll(".tabla-plan-importacion-envoltorio table").Should().ContainSingle();
    }

    [Fact]
    public async Task El_arbol_cuelga_cada_Centro_y_Trabajador_de_su_Empresa_y_su_Cliente_empresarial()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());
        await LlevarCombinadaAlPlanAsync(cut);

        DescribirArbol(cut).Should().Equal(
            "Instalaciones Vidal S.L. CIF B98765432 Se creará",
            "  Vidal Montajes S.L. Se creará",
            "    Planta Sagunto Código VS-01 · contrato hasta 31/12/2026 Se creará",
            "    Iker Mena Ruíz 12345678Z Se creará",
            "  Montajes Ebro S.A. Ya existe · se reutiliza",
            "    Laura Ortiz Gil 87654321X Ya existe · se reutiliza",
            "Refrielectric S.L. CIF B12345674 Ya existe · se reutiliza",
            "  Aislamientos Nervión S.L. Se creará",
            "  Montajes Ebro S.A. Ya existe · se reutiliza",
            "    Centro Logístico Sur Sin código Ya existe · se reutiliza",
            "    Laura Ortiz Gil 87654321X Ya existe · se reutiliza",
            "Grupo Previo S.A. No viene en la hoja Clientes Ya existe · se reutiliza",
            "  Empresa Previa S.L. Ya existe · se reutiliza",
            "    Nave Previa Sin código Se creará",
            "Sin Cliente empresarial asociado",
            "  Talleres Sueltos S.L. Se creará",
            "    Marco Vila Sanz 11223344A Se creará",
            "  Contratas Previas S.L. Ya existe · se reutiliza",
            "    Ana Cid Puente 55667788B Se creará");

        cut.Find("[data-nodo-cliente='Instalaciones Vidal S.L.']").ClassList.Should().Contain("arbol-plan-rama-nueva");
        cut.Find("[data-nodo-cliente='Refrielectric S.L.']").ClassList.Should().NotContain("arbol-plan-rama-nueva");
    }

    [Fact]
    public async Task El_arbol_es_la_vista_por_defecto_y_la_lista_plana_de_siempre_sigue_a_un_clic()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());
        await LlevarCombinadaAlPlanAsync(cut);

        Boton(cut, "Árbol de lo que se va a crear").GetAttribute("aria-pressed").Should().Be("true");
        Boton(cut, "Lista plana").GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll(".arbol-plan-importacion").Should().ContainSingle();

        await Pulsar(cut, "Lista plana");

        Boton(cut, "Lista plana").GetAttribute("aria-pressed").Should().Be("true");
        Boton(cut, "Árbol de lo que se va a crear").GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll(".arbol-plan-importacion").Should().BeEmpty();
        var acciones = cut.FindAll(".tabla-plan-importacion-envoltorio tbody tr").Select(f => Texto(f.QuerySelectorAll("td")[1])).ToList();
        acciones.Should().HaveCount(9, "la lista plana, como hoy, solo trae las altas").And.OnlyContain(a => a.StartsWith("Crear "));
    }

    [Fact]
    public async Task En_el_arbol_los_avisos_y_omitidos_del_analisis_se_siguen_viendo()
    {
        var aviso = new ItemImportacionDto("Empresas", 3, "Vidal Montajes S.L. — Grupo Vidal", "No se encontró el cliente \"Grupo Vidal\".");
        var omitido = new ItemImportacionDto("Centros", 7, "Nave Berriz", "No se encontró la empresa \"Talleres Berriz\".");
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado([aviso], [omitido]));
        await LlevarCombinadaAlPlanAsync(cut);

        cut.FindAll(".arbol-plan-importacion").Should().ContainSingle("sigue en la vista árbol");
        cut.FindAll("h3.titulo-items-plan").Select(Texto).Should().Equal("Avisos (1)", "Omitidos (1)");
        var filas = cut.FindAll(".tabla-plan-importacion-envoltorio tbody tr").Select(Texto).ToList();
        filas.Should().HaveCount(2);
        filas[0].Should().Contain("Vidal Montajes S.L. — Grupo Vidal").And.Contain("3");
        filas[1].Should().Contain("Nave Berriz").And.Contain("Talleres Berriz");
    }

    [Fact]
    public async Task Sin_nada_nuevo_que_crear_lo_dice_y_deja_seguir_a_confirmar_para_reemplazar()
    {
        var (cut, _) = RenderizarCombinada(new PlanImportacionCombinadaDto(
            [new ClienteImportadoDto("Refrielectric S.L.", "B12345674", false, YaExiste: true)],
            [new EmpresaCombinadaImportadaDto("Montajes Ebro S.A.", ["Refrielectric S.L."], YaExiste: true)],
            [], [], [], []));
        await LlevarCombinadaAlPlanAsync(cut);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("El archivo se leyó entero, pero no hay nada nuevo que crear");
        Texto(cut.Find(".estado-vacio p")).Should().StartWith("Los 2 registros del archivo ya existen en el sistema")
            .And.Contain("«Reemplazar los campos ya rellenados»");
        cut.FindAll(".badges-resumen-plan").Should().BeEmpty();
        cut.FindAll(".arbol-plan-importacion, .tabla-plan-importacion-envoltorio").Should().BeEmpty();

        await Pulsar(cut, "Continuar a confirmar");

        TituloDeLaSeccion(cut).Should().Be("Confirmar importación");
        cut.FindAll("label.opcion-reemplazar-importacion").Should().ContainSingle();
    }

    // ---------------------------------------------------------------- Importar Combinado: pasos 4 y 5

    [Fact]
    public async Task El_aviso_de_Reemplazar_solo_sale_con_la_casilla_marcada_y_dice_su_alcance_real()
    {
        var (cut, _) = RenderizarCombinada(PlanDelMockupCombinado());
        await LlevarCombinadaAlPlanAsync(cut);
        await Pulsar(cut, "Continuar a confirmar");

        cut.FindAll(".aviso-reemplazar-importacion").Should().BeEmpty("por defecto solo se completa lo vacío");

        await cut.Find("label.opcion-reemplazar-importacion input").ChangeAsync(new ChangeEventArgs { Value = true });

        var aviso = Texto(cut.Find(".aviso-reemplazar-importacion"));
        aviso.Should().Contain("los 4 registros que ya existían toman lo que traiga el archivo")
            .And.Contain("se cierran, también si la celda viene vacía")
            .And.Contain("Un texto o una fecha que el archivo deje en blanco no borra lo que había");

        await cut.Find("label.opcion-reemplazar-importacion input").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.FindAll(".aviso-reemplazar-importacion").Should().BeEmpty();
    }

    [Fact]
    public async Task El_reporte_de_la_Combinada_ofrece_ir_a_los_Clientes_empresariales()
    {
        var (cut, mediador) = RenderizarCombinada(PlanDelMockupCombinado());
        await LlevarCombinadaAlPlanAsync(cut);
        await Pulsar(cut, "Continuar a confirmar");
        await MarcarRevisado(cut);
        await Pulsar(cut, "Importar ahora");
        await BotonDelDialogo(cut, "Sí, importar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => TituloDeLaSeccion(cut).Should().Be("Reporte"));
        mediador.Enviados.OfType<EjecutarImportacionCombinadaCommand>().Should().ContainSingle();
        cut.FindAll("a").Should().ContainSingle(a => Texto(a) == "Ver Clientes empresariales")
            .Which.GetAttribute("href").Should().Be("/clientes");
    }

    [Fact]
    public async Task El_reporte_de_otra_plantilla_no_ofrece_ir_a_los_Clientes_empresariales()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.", yaExisteCliente: true, yaExisteCentro: true)]);
        escenario.ClientesExistentes.Add("Alfa S.L.");
        escenario.CentrosExistentes.Add("Alfa S.L.");
        var (cut, _) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "a.xlsx", "A");
        await MarcarRevisado(cut);
        await Pulsar(cut, "Importar ahora");
        await BotonDelDialogo(cut, "Sí, importar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => TituloDeLaSeccion(cut).Should().Be("Reporte"));
        cut.FindAll("a").Should().NotContain(a => Texto(a) == "Ver Clientes empresariales");
    }
}
