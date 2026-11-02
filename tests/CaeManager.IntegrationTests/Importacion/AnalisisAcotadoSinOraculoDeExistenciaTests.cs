using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Con autoridad acotada, la vista previa de una carga masiva no debe distinguir «existe fuera de mi ámbito» de «no existe».
/// Aquí se pasa el ANALIZADOR REAL (con archivo .xlsx) por el recorte o por el ámbito, y se compara el plan de un mundo donde
/// lo existente está fuera del ámbito con el de un mundo donde no existe: deben ser iguales. Cada caso lleva su control
/// con el dato visible, para que el test no pase por vaciar todo.
/// </summary>
public class AnalisisAcotadoSinOraculoDeExistenciaTests
{
    private const string Dni = "12345678Z";
    private const string ClienteExistente = "Cliente Existente S.A.";
    private const string EmpresaExistente = "Empresa Existente S.L.";
    private const string CentroExistente = "Centro Existente";
    private const string TipoConocido = "Formación 20h";

    private sealed class Mundo
    {
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public Empresa Cliente { get; private set; } = null!;
        public Empresa Empresa { get; private set; } = null!;
        public Centro Centro { get; private set; } = null!;
        public Trabajador Trabajador { get; private set; } = null!;

        public static Mundo ConLoExistente()
        {
            var m = new Mundo();
            m.Cliente = Empresa.CrearComoCliente(ClienteExistente, cif: "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            m.Empresa = new Empresa(EmpresaExistente);
            m.Empresas.ListaEmpresas.Add(m.Cliente);
            m.Empresas.ListaEmpresas.Add(m.Empresa);
            m.Centro = new Centro(m.Cliente.Id, m.Empresa.Id, CentroExistente);
            m.Centros.ListaCentros.Add(m.Centro);
            m.Trabajador = Trabajador.DeEmpresa(m.Empresa.Id, "Marta", "Ruiz", Dni);
            m.Trabajadores.ListaTrabajadores.Add(m.Trabajador);
            return m;
        }
    }

    private static RecorteDeAnalisisAlAmbito Recorte(Mundo m, bool visible)
    {
        var alcance = visible
            ? new AlcanceDatosServiceFalso(
                clienteIds: [m.Cliente.Id], trabajadorIds: [m.Trabajador.Id], empresaIdsParaGestion: [m.Empresa.Id],
                centroIdsParaGestion: [m.Centro.Id])
            : new AlcanceDatosServiceFalso(clienteIds: [], trabajadorIds: [], empresaIdsParaGestion: [], centroIdsParaGestion: []);
        return new RecorteDeAnalisisAlAmbito(
            new AutoridadAcotadaFalsa(), alcance, m.Empresas, m.Centros, m.Trabajadores, new AsignacionesQueryContextFalso());
    }

    private static MemoryStream Flujo(XLWorkbook libro)
    {
        var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return flujo;
    }

    // ─── Plantilla de Documentos ──────────────────────────────────────────────────────────────

    private static XLWorkbook LibroDocumentos(string tipo, string fecha)
    {
        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Documentos");
        hoja.Cell(2, 1).Value = Dni;
        hoja.Cell(2, 2).Value = tipo;
        hoja.Cell(2, 3).Value = DateTime.Parse(fecha, System.Globalization.CultureInfo.InvariantCulture);
        hoja.Cell(3, 1).Value = Dni;
        hoja.Cell(3, 2).Value = TipoConocido;
        hoja.Cell(3, 3).Value = new DateTime(2026, 1, 1);
        hoja.Cell(4, 1).Value = Dni;
        hoja.Cell(4, 2).Value = TipoConocido;
        hoja.Cell(4, 3).Value = new DateTime(2026, 1, 1);
        return libro;
    }

    private static async Task<PlanImportacionDto> DocumentosAsync(Mundo m, bool visible, string tipo, string fecha)
    {
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(new TipoDocumento(
            TipoConocido, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambitoAplicacion: AmbitoAplicacion.Trabajador));
        var servicio = new ClosedXmlPlantillaDocumentosService(new DocumentosQueryContextFalso(), tipos, m.Trabajadores);
        using var flujo = Flujo(LibroDocumentos(tipo, fecha));
        return await Recorte(m, visible).RecortarAsync(await servicio.AnalizarAsync(flujo), default);
    }

    [Theory]
    [InlineData("Tipo que no está en el catálogo", "2026-01-01")] // tipo inexistente
    [InlineData(TipoConocido, "2099-01-01")] // fecha futura
    public async Task Documentos_un_DNI_fuera_de_ambito_da_el_mismo_plan_que_un_DNI_inexistente(string tipo, string fecha)
    {
        var fuera = await DocumentosAsync(Mundo.ConLoExistente(), visible: false, tipo, fecha);
        var inexistente = await DocumentosAsync(new Mundo(), visible: false, tipo, fecha);

        fuera.Should().BeEquivalentTo(inexistente, o => o.Excluding(p => p.OperacionId).WithStrictOrdering());
    }

    [Fact]
    public async Task Documentos_control_el_DNI_visible_conserva_su_motivo_propio()
    {
        var visible = await DocumentosAsync(Mundo.ConLoExistente(), visible: true, "Tipo que no está en el catálogo", "2026-01-01");

        visible.Omitidos.Should().Contain(o => o.Motivo.StartsWith("No existe este tipo de documento"));
        visible.Omitidos.Should().NotContain(o => o.Motivo.StartsWith("No existe ningún trabajador"));
    }

    // ─── Plantilla de Clientes ────────────────────────────────────────────────────────────────

    private static async Task<PlanImportacionDto> ClientesAsync(Mundo m, bool visible, string nombre)
    {
        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Clientes");
        hoja.Cell(2, 1).Value = nombre;
        var servicio = new ClosedXmlPlantillaClientesService(m.Centros, m.Empresas);
        using var flujo = Flujo(libro);
        return await Recorte(m, visible).RecortarAsync(await servicio.AnalizarAsync(flujo), default);
    }

    [Fact]
    public async Task Clientes_un_Cliente_fuera_de_ambito_sin_centro_da_el_mismo_plan_que_uno_inexistente()
    {
        // El Cliente existe pero ningún Centro se llama como él: el analizador dice «el centro no existe» solo si el Cliente existe.
        var m = Mundo.ConLoExistente();
        m.Centros.ListaCentros.Clear();

        var fuera = await ClientesAsync(m, visible: false, ClienteExistente);
        var inexistente = await ClientesAsync(new Mundo(), visible: false, ClienteExistente);

        fuera.Should().BeEquivalentTo(inexistente, o => o.Excluding(p => p.OperacionId).WithStrictOrdering());
    }

    [Fact]
    public async Task Clientes_control_el_Cliente_visible_sin_centro_conserva_el_motivo_del_centro()
    {
        var m = Mundo.ConLoExistente();
        m.Centros.ListaCentros.Clear();

        var visible = await ClientesAsync(m, visible: true, ClienteExistente);

        visible.Omitidos.Should().ContainSingle().Which.Motivo.Should().StartWith("Este centro no existe todavía");
    }

    // ─── Plantilla combinada ──────────────────────────────────────────────────────────────────

    private static XLWorkbook LibroCombinado()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        var empresas = libro.Worksheets.Add("Empresas");
        empresas.Cell(2, 1).Value = "Empresa Nueva S.L.";
        empresas.Cell(2, 2).Value = ClienteExistente;
        var centros = libro.Worksheets.Add("Centros");
        centros.Cell(2, 1).Value = CentroExistente;
        centros.Cell(2, 2).Value = ClienteExistente;
        centros.Cell(2, 3).Value = "Empresa Nueva S.L.";
        centros.Cell(3, 1).Value = "Centro Otro";
        centros.Cell(3, 2).Value = ClienteExistente;
        centros.Cell(3, 3).Value = EmpresaExistente;
        var trabajadores = libro.Worksheets.Add("Trabajadores");
        trabajadores.Cell(2, 1).Value = "Marta";
        trabajadores.Cell(2, 2).Value = "Ruiz";
        trabajadores.Cell(2, 3).Value = Dni;
        trabajadores.Cell(2, 4).Value = EmpresaExistente;
        return libro;
    }

    private static async Task<PlanImportacionCombinadaDto> CombinadaAsync(Mundo m, bool visible)
    {
        var servicio = new ClosedXmlPlantillaCombinadaService(m.Centros, m.Empresas, m.Trabajadores);
        using var flujo = Flujo(LibroCombinado());
        return await servicio.AnalizarAsync(flujo, await Recorte(m, visible).ObtenerExistentesAsync(default));
    }

    [Fact]
    public async Task Combinada_lo_existente_fuera_de_ambito_da_el_mismo_plan_que_lo_inexistente()
    {
        var fuera = await CombinadaAsync(Mundo.ConLoExistente(), visible: false);
        var inexistente = await CombinadaAsync(new Mundo(), visible: false);

        fuera.Should().BeEquivalentTo(inexistente, o => o.Excluding(p => p.OperacionId).WithStrictOrdering());
    }

    [Fact]
    public async Task Combinada_control_lo_visible_se_asocia_y_figura_como_existente()
    {
        var plan = await CombinadaAsync(Mundo.ConLoExistente(), visible: true);

        plan.Empresas.Single().ClientesAsociados.Should().Equal(ClienteExistente);
        plan.Centros.Should().Contain(c => c.Nombre == CentroExistente && c.YaExiste);
        plan.Trabajadores.Single().YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Combinada_el_CIF_de_un_Cliente_fuera_de_ambito_no_figura_como_existente()
    {
        var m = Mundo.ConLoExistente();
        var libro = new XLWorkbook();
        var clientes = libro.Worksheets.Add("Clientes");
        clientes.Cell(2, 1).Value = "Cliente Con El Mismo CIF S.A.";
        clientes.Cell(2, 2).Value = "B12345674"; // el CIF del Cliente existente
        libro.Worksheets.Add("Empresas");
        libro.Worksheets.Add("Centros");
        libro.Worksheets.Add("Trabajadores");
        var servicio = new ClosedXmlPlantillaCombinadaService(m.Centros, m.Empresas, m.Trabajadores);

        using var flujoFuera = Flujo(libro);
        var fuera = await servicio.AnalizarAsync(flujoFuera, await Recorte(m, visible: false).ObtenerExistentesAsync(default));
        using var flujoVisible = Flujo(libro);
        var visible = await servicio.AnalizarAsync(flujoVisible, await Recorte(m, visible: true).ObtenerExistentesAsync(default));

        fuera.Clientes.Single().YaExiste.Should().BeFalse("el CIF existe, pero en un Cliente fuera del ámbito");
        visible.Clientes.Single().YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Combinada_el_DNI_y_la_Empresa_fuera_de_ambito_no_figuran_como_existentes_con_el_resto_visible()
    {
        // Mundo mixto: la Empresa y el Cliente están en el ámbito; el Trabajador con ese DNI y la Empresa homónima de la hoja no.
        var m = Mundo.ConLoExistente();
        var alcance = new AlcanceDatosServiceFalso(
            clienteIds: [m.Cliente.Id], trabajadorIds: [], empresaIdsParaGestion: [m.Empresa.Id], centroIdsParaGestion: [m.Centro.Id]);
        var recorte = new RecorteDeAnalisisAlAmbito(
            new AutoridadAcotadaFalsa(), alcance, m.Empresas, m.Centros, m.Trabajadores, new AsignacionesQueryContextFalso());
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        var empresas = libro.Worksheets.Add("Empresas");
        empresas.Cell(2, 1).Value = EmpresaExistente;
        libro.Worksheets.Add("Centros");
        var trabajadores = libro.Worksheets.Add("Trabajadores");
        trabajadores.Cell(2, 1).Value = "Marta";
        trabajadores.Cell(2, 2).Value = "Ruiz";
        trabajadores.Cell(2, 3).Value = Dni;
        trabajadores.Cell(2, 4).Value = EmpresaExistente;
        var servicio = new ClosedXmlPlantillaCombinadaService(m.Centros, m.Empresas, m.Trabajadores);
        using var flujo = Flujo(libro);

        var plan = await servicio.AnalizarAsync(flujo, await recorte.ObtenerExistentesAsync(default));

        plan.Empresas.Single().YaExiste.Should().BeTrue("la Empresa está en el ámbito de gestión");
        plan.Trabajadores.Single().YaExiste.Should().BeFalse("el Trabajador con ese DNI está fuera del ámbito");
    }

    [Fact]
    public async Task Combinada_la_Empresa_existente_fuera_de_ambito_figura_como_nueva_en_la_hoja_Empresas()
    {
        var m = Mundo.ConLoExistente();
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        libro.Worksheets.Add("Empresas").Cell(2, 1).Value = EmpresaExistente;
        libro.Worksheets.Add("Centros");
        libro.Worksheets.Add("Trabajadores");
        var servicio = new ClosedXmlPlantillaCombinadaService(m.Centros, m.Empresas, m.Trabajadores);
        using var flujo = Flujo(libro);

        var plan = await servicio.AnalizarAsync(flujo, await Recorte(m, visible: false).ObtenerExistentesAsync(default));

        plan.Empresas.Single().YaExiste.Should().BeFalse();
    }

    [Fact]
    public async Task Combinada_un_Centro_homonimo_de_otro_Cliente_fuera_de_ambito_no_figura_como_existente()
    {
        var m = Mundo.ConLoExistente();
        var otro = Empresa.CrearComoCliente("Otro Cliente S.A.", cif: "B76543214", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        m.Empresas.ListaEmpresas.Add(otro);
        m.Centros.ListaCentros.Add(new Centro(otro.Id, m.Empresa.Id, "Norte"));
        m.Centros.ListaCentros.Add(new Centro(m.Cliente.Id, m.Empresa.Id, "Norte"));
        var visibles = m.Centros.ListaCentros.Where(c => c.ClienteId == m.Cliente.Id).Select(c => c.Id).ToList();
        var alcance = new AlcanceDatosServiceFalso(
            clienteIds: [m.Cliente.Id, otro.Id], trabajadorIds: [m.Trabajador.Id], empresaIdsParaGestion: [m.Empresa.Id], centroIdsParaGestion: visibles);
        var recorte = new RecorteDeAnalisisAlAmbito(
            new AutoridadAcotadaFalsa(), alcance, m.Empresas, m.Centros, m.Trabajadores, new AsignacionesQueryContextFalso());

        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        libro.Worksheets.Add("Empresas");
        var hoja = libro.Worksheets.Add("Centros");
        hoja.Cell(2, 1).Value = "Norte";
        hoja.Cell(2, 2).Value = ClienteExistente;
        hoja.Cell(2, 3).Value = EmpresaExistente;
        hoja.Cell(3, 1).Value = "Norte";
        hoja.Cell(3, 2).Value = "Otro Cliente S.A.";
        hoja.Cell(3, 3).Value = EmpresaExistente;
        libro.Worksheets.Add("Trabajadores");
        var servicio = new ClosedXmlPlantillaCombinadaService(m.Centros, m.Empresas, m.Trabajadores);
        using var flujo = Flujo(libro);

        var plan = await servicio.AnalizarAsync(flujo, await recorte.ObtenerExistentesAsync(default));

        plan.Centros.Single(c => c.RazonSocialCliente == ClienteExistente).YaExiste.Should().BeTrue("el Norte de ese Cliente está en el ámbito");
        plan.Centros.Single(c => c.RazonSocialCliente == "Otro Cliente S.A.").YaExiste.Should()
            .BeFalse("el Norte homónimo del otro Cliente está fuera del ámbito aunque el nombre del Centro sea visible");
    }
}

/// <summary>Autoridad de carga masiva acotada (Coordinador CAE o Dirección CAE) para los tests de la vista previa.</summary>
internal sealed class AutoridadAcotadaFalsa : IAutorizacionEscrituraEfectiva
{
    public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AutoridadCargaMasiva.Acotada);
}
