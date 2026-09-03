using CaeManager.Application.Importacion;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B) sobre <see cref="ClosedXmlPlantillaCombinadaService.AnalizarAsync"/> —
/// auditada por REC-129. Las cuatro hojas comparten el salto silencioso de
/// la fila de ejemplo (probado una vez por hoja); "Contrato vigente hasta"
/// y "Fecha de nacimiento" son las dos pérdidas de dato reales que
/// encontró la medición: antes de este incremento, una fecha presente pero
/// ilegible se colapsaba en <c>null</c> exactamente igual que una celda
/// vacía, sin ningún registro — la fila se importaba igual, pero el dato
/// desaparecía sin traza.
/// </summary>
public class ClosedXmlPlantillaCombinadaServiceTests
{
    private const string DniValido = "12345678Z";

    [Fact]
    public async Task Fila_de_ejemplo_de_Clientes_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 2).Value = "B12345674";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Clientes.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_ejemplo_de_Empresas_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empresas");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Empresas.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_ejemplo_de_Centros_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Centros");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 2).Value = "Cliente Ejemplo S.A.";
        hoja.Cell(2, 3).Value = "Empresa Ejemplo S.L.";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Centros.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_ejemplo_de_Trabajadores_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Trabajadores");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 3).Value = DniValido;
        hoja.Cell(2, 4).Value = "Empresa Ejemplo S.L.";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Contrato_vigente_hasta_ilegible_no_bloquea_el_centro_pero_queda_omitido_nombrando_el_valor_bruto()
    {
        var libro = NuevoLibroBase();
        EscribirClienteValido(libro, fila: 2, razonSocial: "Cliente Norte S.A.");
        EscribirEmpresaValida(libro, fila: 2, razonSocial: "Empresa Sur S.L.");
        var hojaCentros = libro.Worksheets.Worksheet("Centros");
        hojaCentros.Cell(2, 1).Value = "Centro Norte";
        hojaCentros.Cell(2, 2).Value = "Cliente Norte S.A.";
        hojaCentros.Cell(2, 3).Value = "Empresa Sur S.L.";
        hojaCentros.Cell(2, 7).Value = "sin determinar"; // Columna 7: texto, no una fecha.

        var plan = await AnalizarAsync(libro);

        var centro = plan.Centros.Should().ContainSingle().Subject;
        centro.ContratoVigenteHasta.Should().BeNull();

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Centros").Subject;
        omitido.Fila.Should().Be(2);
        omitido.Motivo.Should().Be("La fecha «sin determinar» en \"Contrato vigente hasta\" no se pudo interpretar; el centro se importó sin ese dato.");
    }

    [Fact]
    public async Task Contrato_vigente_hasta_vacio_no_genera_ninguna_entrada_y_el_centro_se_importa()
    {
        var libro = NuevoLibroBase();
        EscribirClienteValido(libro, fila: 2, razonSocial: "Cliente Norte S.A.");
        EscribirEmpresaValida(libro, fila: 2, razonSocial: "Empresa Sur S.L.");
        var hojaCentros = libro.Worksheets.Worksheet("Centros");
        hojaCentros.Cell(2, 1).Value = "Centro Norte";
        hojaCentros.Cell(2, 2).Value = "Cliente Norte S.A.";
        hojaCentros.Cell(2, 3).Value = "Empresa Sur S.L.";
        // Columna 7 (Contrato vigente hasta) queda sin valor a propósito.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var centro = plan.Centros.Should().ContainSingle().Subject;
        centro.ContratoVigenteHasta.Should().BeNull();
    }

    [Fact]
    public async Task Fecha_de_nacimiento_ilegible_no_bloquea_al_trabajador_pero_queda_omitida_nombrando_el_valor_bruto()
    {
        var libro = NuevoLibroBase();
        EscribirEmpresaValida(libro, fila: 2, razonSocial: "Empresa Sur S.L.");
        var hojaTrabajadores = libro.Worksheets.Worksheet("Trabajadores");
        hojaTrabajadores.Cell(2, 1).Value = "Marta";
        hojaTrabajadores.Cell(2, 2).Value = "Ruiz";
        hojaTrabajadores.Cell(2, 3).Value = DniValido;
        hojaTrabajadores.Cell(2, 4).Value = "Empresa Sur S.L.";
        hojaTrabajadores.Cell(2, 5).Value = "hace treinta años"; // Columna 5: texto, no una fecha.

        var plan = await AnalizarAsync(libro);

        var trabajador = plan.Trabajadores.Should().ContainSingle().Subject;
        trabajador.FechaNacimiento.Should().BeNull();

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Trabajadores").Subject;
        omitido.Fila.Should().Be(2);
        omitido.Motivo.Should().Be("La fecha de nacimiento «hace treinta años» no se pudo interpretar; el trabajador se importó sin ese dato.");
    }

    [Fact]
    public async Task Fecha_de_nacimiento_vacia_no_genera_ninguna_entrada_y_el_trabajador_se_importa()
    {
        var libro = NuevoLibroBase();
        EscribirEmpresaValida(libro, fila: 2, razonSocial: "Empresa Sur S.L.");
        var hojaTrabajadores = libro.Worksheets.Worksheet("Trabajadores");
        hojaTrabajadores.Cell(2, 1).Value = "Marta";
        hojaTrabajadores.Cell(2, 2).Value = "Ruiz";
        hojaTrabajadores.Cell(2, 3).Value = DniValido;
        hojaTrabajadores.Cell(2, 4).Value = "Empresa Sur S.L.";
        // Columna 5 (fecha de nacimiento) queda sin valor a propósito.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var trabajador = plan.Trabajadores.Should().ContainSingle().Subject;
        trabajador.FechaNacimiento.Should().BeNull();
    }

    private static void EscribirClienteValido(XLWorkbook libro, int fila, string razonSocial)
    {
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(fila, 1).Value = razonSocial;
        hoja.Cell(fila, 2).Value = "B12345674";
    }

    private static void EscribirEmpresaValida(XLWorkbook libro, int fila, string razonSocial)
    {
        var hoja = libro.Worksheets.Worksheet("Empresas");
        hoja.Cell(fila, 1).Value = razonSocial;
    }

    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        libro.Worksheets.Add("Empresas");
        libro.Worksheets.Add("Centros");
        libro.Worksheets.Add("Trabajadores");
        return libro;
    }

    private static async Task<PlanImportacionCombinadaDto> AnalizarAsync(XLWorkbook libro)
    {
        var servicio = new ClosedXmlPlantillaCombinadaService(
            new CentrosQueryContextFalso(), new EmpresasQueryContextFalso(), new TrabajadoresQueryContextFalso());

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }
}
