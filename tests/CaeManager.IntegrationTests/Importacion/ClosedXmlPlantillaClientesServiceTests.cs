using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B) sobre <see cref="ClosedXmlPlantillaClientesService.AnalizarAsync"/> —
/// auditada por REC-129 junto con los otros dos analizadores de plantilla.
/// Este servicio, a diferencia del analizador de referencia, no tiene
/// ninguna celda de fecha: su única rama silenciosa es la fila de ejemplo.
///
/// Desde el hallazgo de que esta plantilla nunca puede crear Cliente ni
/// Centro (Fase 10 exige CIF/Empresa que esta hoja no recoge), el análisis
/// tiene que anticipar exactamente lo que la escritura hará: una fila cuyo
/// Cliente o Centro no exista todavía se omite AQUÍ, no se cuenta como "se
/// creará" para acabar omitida recién al confirmar.
/// </summary>
public class ClosedXmlPlantillaClientesServiceTests
{
    private const string ClienteExistente = "Cliente Norte S.A.";

    [Fact]
    public async Task Fila_de_ejemplo_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 2).Value = "N";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.ClientesCentros.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_cuyo_Cliente_y_Centro_ya_existian_se_importa_como_ClienteCentro()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = ClienteExistente;
        hoja.Cell(2, 2).Value = "C";
        hoja.Cell(2, 3).Value = "Calle Norte 1";
        hoja.Cell(2, 4).Value = "Ana García";

        var plan = await AnalizarAsync(libro, ConEmpresaClienteYCentro(ClienteExistente));

        plan.Omitidos.Should().BeEmpty();
        var clienteCentro = plan.ClientesCentros.Should().ContainSingle().Subject;
        clienteCentro.Nombre.Should().Be(ClienteExistente);
        clienteCentro.EsCritico.Should().BeTrue();
        clienteCentro.YaExisteCliente.Should().BeTrue();
        clienteCentro.YaExisteCentro.Should().BeTrue();
    }

    /// <summary>
    /// El defecto reportado: ninguno de los dos formatos de Excel simplificados
    /// puede crear un Cliente o Centro nuevo (Fase 10), así que el análisis no
    /// puede prometer "se creará" para una fila que la escritura omitirá. Antes
    /// de este fix, esta fila entraba en <c>ClientesCentros</c> con
    /// <c>YaExisteCliente</c>/<c>YaExisteCentro</c> en <c>false</c> y la pantalla
    /// la pintaba en verde como "Crear cliente"/"Crear centro".
    /// </summary>
    [Fact]
    public async Task Fila_con_Cliente_y_Centro_nuevos_no_se_promete_crear_va_omitida()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "Cliente Nuevo S.A.";
        hoja.Cell(2, 2).Value = "N";

        var plan = await AnalizarAsync(libro);

        plan.ClientesCentros.Should().BeEmpty("esta plantilla nunca puede crear un Cliente o Centro nuevo (Fase 10)");
        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(2);
        omitido.Descripcion.Should().Be("Cliente Nuevo S.A.");
        omitido.Motivo.Should().Contain("Este cliente no existe todavía").And.Contain("CIF");
    }

    /// <summary>
    /// El Cliente existe pero el Centro no (Fase 10 exige una Empresa que esta
    /// plantilla tampoco recoge para Centro) — causal distinta de la de arriba,
    /// no un motivo genérico (DCR-12 B).
    /// </summary>
    [Fact]
    public async Task Fila_con_Cliente_existente_pero_Centro_nuevo_se_omite_nombrando_el_Centro()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = ClienteExistente;

        var plan = await AnalizarAsync(libro, ConEmpresaCliente(ClienteExistente));

        plan.ClientesCentros.Should().BeEmpty();
        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("Este centro no existe todavía").And.Contain("Empresa");
    }

    /// <summary>
    /// El defecto gemelo: el análisis consideraba "existente" cualquier Empresa
    /// con ese nombre, pero la escritura (y ObtenerClientesQuery) solo reconoce
    /// Cliente empresarial (<c>EsCritico != null</c>). Una Empresa homónima que
    /// no es Cliente (p. ej. una Subcontrata) no puede hacer que la fila se dé
    /// por "ya existente".
    /// </summary>
    [Fact]
    public async Task Empresa_homonima_que_no_es_Cliente_empresarial_no_cuenta_como_existente()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "Subcontrata Homónima S.L.";

        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(new Empresa("Subcontrata Homónima S.L.")); // EsCritico == null: no es Cliente.

        var plan = await AnalizarAsync(libro, empresas: empresas);

        plan.ClientesCentros.Should().BeEmpty();
        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("Este cliente no existe todavía");
    }

    [Fact]
    public async Task Nombre_duplicado_dentro_del_archivo_queda_omitido_con_su_motivo()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = ClienteExistente;
        hoja.Cell(3, 1).Value = ClienteExistente;

        var plan = await AnalizarAsync(libro, ConEmpresaClienteYCentro(ClienteExistente));

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(3);
        omitido.Motivo.Should().Be("Nombre duplicado dentro del propio archivo.");
        plan.ClientesCentros.Should().ContainSingle();
    }

    [Fact]
    public async Task Hoja_ausente_queda_omitida_completa()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("OtraHoja"); // Sin la hoja "Clientes" — ClosedXML exige al menos una hoja para guardar.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Clientes");
        omitido.Motivo.Should().Be("No se encontró la hoja \"Clientes\" en el archivo.");
        plan.ClientesCentros.Should().BeEmpty();
    }

    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        return libro;
    }

    private static EmpresasQueryContextFalso ConEmpresaCliente(string razonSocial)
    {
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(Empresa.CrearComoCliente(razonSocial, cif: "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null));
        return empresas;
    }

    private static (EmpresasQueryContextFalso Empresas, CentrosQueryContextFalso Centros) ConEmpresaClienteYCentro(string razonSocial)
    {
        var empresas = ConEmpresaCliente(razonSocial);
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), razonSocial));
        return (empresas, centros);
    }

    private static Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(
        XLWorkbook libro, (EmpresasQueryContextFalso Empresas, CentrosQueryContextFalso Centros) contexto) =>
        AnalizarAsync(libro, contexto.Empresas, contexto.Centros);

    private static async Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(
        XLWorkbook libro, EmpresasQueryContextFalso? empresas = null, CentrosQueryContextFalso? centros = null)
    {
        var servicio = new ClosedXmlPlantillaClientesService(centros ?? new CentrosQueryContextFalso(), empresas ?? new EmpresasQueryContextFalso());

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }
}
