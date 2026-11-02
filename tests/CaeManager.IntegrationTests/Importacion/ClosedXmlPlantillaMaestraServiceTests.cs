using CaeManager.Application.Importacion.Maestra;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Lector de la plantilla maestra (Paso 1): columnas por nombre de cabecera, validación por hoja y fila con número de
/// fila real del Excel, fila de ejemplo ignorada y «nada se descarta en silencio» (hojas y columnas aún no importadas
/// se anuncian). Datos ficticios. Los CIF y DNI de los ejemplos pasan la validación de control.
/// </summary>
public class ClosedXmlPlantillaMaestraServiceTests
{
    private const string CifGestionada = "B12345674";
    private const string CifCliente = "A58818501";

    [Fact]
    public async Task Un_libro_valido_se_lee_por_nombre_de_cabecera_y_enlaza_por_CIF()
    {
        var plan = await AnalizarAsync(LibroValido());

        plan.Omitidos.Should().BeEmpty();
        plan.Empresas.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new EmpresaMaestraDto(3, "Industrias Ficticias, S.L.", CifGestionada, "4321", "Convenio de ejemplo", null, false));
        plan.Clientes.Should().ContainSingle().Which.CifEmpresaGestionada.Should().Be(CifGestionada);
        plan.Centros.Should().ContainSingle().Which.CifCliente.Should().Be(CifCliente);
        plan.Trabajadores.Should().ContainSingle().Which.Identidad.Should().Be("12345678Z");
    }

    [Fact]
    public async Task Las_columnas_se_localizan_por_nombre_aunque_cambie_el_orden()
    {
        var libro = LibroValido();
        var hoja = libro.Worksheets.Worksheet("Empresas");
        hoja.Column(1).InsertColumnsBefore(1); // desplaza todo una columna a la derecha

        var plan = await AnalizarAsync(libro);

        plan.Empresas.Should().ContainSingle().Which.Cif.Should().Be(CifGestionada);
    }

    [Fact]
    public async Task La_fila_de_ejemplo_se_ignora_sin_registrar_nada()
    {
        var libro = LibroValido();
        libro.Worksheets.Worksheet("Empresas").Row(2).InsertRowsAbove(1);
        var hoja = libro.Worksheets.Worksheet("Empresas");
        hoja.Cell(2, 1).Value = "EJEMPLO — Industrias Ejemplo, S.L.";
        hoja.Cell(2, 2).Value = CifGestionada;

        var plan = await AnalizarAsync(libro);

        plan.Empresas.Should().ContainSingle().Which.Fila.Should().Be(4, "el número de fila es el del Excel, ejemplo incluido");
        plan.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_CIF_invalido_se_omite_con_su_fila_real_y_el_resto_sigue()
    {
        var libro = LibroValido();
        var hoja = libro.Worksheets.Worksheet("Empresas");
        hoja.Cell(4, 1).Value = "Otra Ficticia, S.L.";
        hoja.Cell(4, 2).Value = "B12345670"; // dígito de control incorrecto

        var plan = await AnalizarAsync(libro);

        plan.Empresas.Should().ContainSingle();
        plan.Omitidos.Should().ContainSingle().Which.Should().Match<CaeManager.Application.Importacion.ItemImportacionDto>(
            o => o.Hoja == "Empresas" && o.Fila == 4 && o.Motivo.Contains("no es válida"));
    }

    [Fact]
    public async Task Un_Cliente_empresarial_que_cita_una_Empresa_gestionada_inexistente_se_omite()
    {
        var libro = LibroValido();
        libro.Worksheets.Worksheet("Clientes empresariales").Cell(3, 4).Value = "A28017895";

        var plan = await AnalizarAsync(libro);

        plan.Clientes.Should().BeEmpty();
        plan.Omitidos.Should().Contain(o => o.Hoja == "Clientes empresariales" && o.Fila == 3 && o.Motivo.Contains("Empresa gestionada"));
    }

    [Fact]
    public async Task El_tipo_de_identidad_declarado_debe_coincidir_con_el_numero()
    {
        var libro = LibroValido();
        libro.Worksheets.Worksheet("Trabajadores").Cell(3, 3).Value = "NIE";

        var plan = await AnalizarAsync(libro);

        plan.Trabajadores.Should().BeEmpty();
        plan.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("corresponde a DNI");
    }

    [Fact]
    public async Task Un_Trabajador_sin_los_obligatorios_de_la_plantilla_se_omite_nombrando_las_columnas()
    {
        var libro = LibroValido();
        libro.Worksheets.Worksheet("Trabajadores").Cell(3, 9).Clear(); // Nacionalidad

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Nacionalidad");
    }

    [Fact]
    public async Task Las_hojas_y_columnas_que_todavia_no_se_importan_se_anuncian_no_se_pierden_en_silencio()
    {
        var libro = LibroValido();
        var subcontratas = libro.Worksheets.Add("Subcontratas");
        subcontratas.Cell(1, 1).Value = "Razón social de la Subcontrata *";
        subcontratas.Cell(3, 1).Value = "Montajes Ficticios, S.L.";

        var plan = await AnalizarAsync(libro);

        plan.HojasPendientes.Should().ContainSingle().Which.Should().Be(new HojaPendienteDto("Subcontratas", 1));
        plan.Advertencias.Should().Contain(a => a.Hoja == "Trabajadores" && a.Motivo.Contains("Nacionalidad"));
    }

    [Fact]
    public async Task Una_hoja_sin_sus_columnas_obligatorias_se_omite_entera_con_motivo()
    {
        var libro = LibroValido();
        libro.Worksheets.Worksheet("Centros").Cell(1, 4).Clear(); // Dirección *

        var plan = await AnalizarAsync(libro);

        plan.Centros.Should().BeEmpty();
        plan.Omitidos.Should().Contain(o => o.Hoja == "Centros" && o.Motivo.Contains("Dirección"));
    }

    private static XLWorkbook LibroValido()
    {
        var libro = new XLWorkbook();

        var empresas = libro.Worksheets.Add("Empresas");
        Cabecera(empresas, "Razón social *", "CIF *", "CNAE", "Convenio aplicable");
        Fila(empresas, 3, "Industrias Ficticias, S.L.", CifGestionada, "4321", "Convenio de ejemplo");

        var clientes = libro.Worksheets.Add("Clientes empresariales");
        Cabecera(clientes, "Razón social *", "CIF *", "Crítico (C/N) *", "CIF de la Empresa gestionada *");
        Fila(clientes, 3, "Distribuciones Ficticias, S.A.", CifCliente, "N", CifGestionada);

        var centros = libro.Worksheets.Add("Centros");
        Cabecera(centros, "Nombre del Centro *", "CIF del Cliente empresarial *", "CIF de la Empresa gestionada *", "Dirección *",
            "Modalidad de gestión CAE *");
        Fila(centros, 3, "Planta Norte", CifCliente, CifGestionada, "Calle Ficticia 1, 28000 Ciudad", "Con gestión CAE");

        var trabajadores = libro.Worksheets.Add("Trabajadores");
        Cabecera(trabajadores, "Nombre *", "Apellidos *", "Tipo de identidad *", "Número de identidad *", "CIF de la Empresa *",
            "Carpeta actual *", "Categoría / Puesto *", "Fecha de nacimiento *", "Nacionalidad *", "Tipo de contratación *",
            "Fecha de alta en la Empresa *");
        Fila(trabajadores, 3, "Nombre Ficticio", "Apellido Ejemplo", "DNI", "12345678Z", CifGestionada, "Apellido Ejemplo Nombre",
            "Oficial 1ª", "", "Española", "Cuenta ajena", "2022-01-10");
        trabajadores.Cell(3, 8).Value = new DateTime(1985, 6, 15);
        return libro;
    }

    private static void Cabecera(IXLWorksheet hoja, params string[] columnas)
    {
        for (var i = 0; i < columnas.Length; i++) hoja.Cell(1, i + 1).Value = columnas[i];
    }

    private static void Fila(IXLWorksheet hoja, int fila, params string[] valores)
    {
        for (var i = 0; i < valores.Length; i++) hoja.Cell(fila, i + 1).Value = valores[i];
    }

    private static async Task<PlanImportacionMaestraDto> AnalizarAsync(XLWorkbook libro)
    {
        var servicio = new ClosedXmlPlantillaMaestraService(
            new CentrosQueryContextFalso(), new EmpresasQueryContextFalso(), new TrabajadoresQueryContextFalso());
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }
}
