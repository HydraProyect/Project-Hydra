using System.Text;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Contrato «nada se descarta en silencio» (Project-Hydra-Negocio/tecnico/IMPORTACION.md § 3 bis, ratificado
/// por DCR-12 decisión B, propietario 2026-08-24) sobre el paso de ANÁLISIS
/// (<see cref="ClosedXmlImportacionParser.AnalizarAsync"/>) — la misma
/// invariante que REC-045 (PR #419) cerró en el handler de escritura.
///
/// Un test por causal, no una comprobación genérica: la decisión instruye no
/// uniformizar las ramas de descarte, así que cada test exige el motivo de SU
/// rama. Los tests de "caso legítimo" prueban lo contrario y con la misma
/// importancia: que la fila de plantilla vacía, la celda de fecha vacía y el
/// centro sin marca siguen sin generar ninguna entrada — si dejaran de estarlo,
/// cualquier Excel real produciría decenas de avisos no accionables.
/// </summary>
public class ClosedXmlImportacionParserTests
{
    private const string DniValido = "12345678Z";
    private const string HojaFilial = "Trabajadores de la filial";
    private const string EmpresaMatriz = "Empresa Matriz de Prueba S.A.";
    private const string EmpresaFilial = "Empresa Filial de Prueba GmbH";
    private const string TipoDocumentoConocido = "Certificado de aptitud médica"; // Columna G (7) — ver ColumnasDocumentos.

    [Fact]
    public async Task Fila_de_Empleados_sin_nombre_ni_apellidos_pero_con_DNI_queda_omitida_con_su_motivo()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        hoja.Cell(4, 1).Value = 1;
        hoja.Cell(4, 4).Value = DniValido; // Solo el DNI: nombre y apellidos vacíos.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Empleados").Subject;
        omitido.Fila.Should().Be(4);
        omitido.Motivo.Should().Contain("Faltan datos obligatorios");
        plan.Trabajadores.Should().BeEmpty();
    }

    [Theory]
    [InlineData(5, null)] // Fecha de nacimiento.
    [InlineData(6, "ana@ejemplo.com")] // Email.
    [InlineData(7, null)] // Fecha del primer documento (Certificado de aptitud médica).
    public async Task Fila_de_Empleados_sin_nombre_ni_apellidos_ni_DNI_pero_con_otro_dato_queda_omitida(int columna, string? valorTexto)
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        hoja.Cell(4, 1).Value = 1;
        if (valorTexto is not null) hoja.Cell(4, columna).Value = valorTexto;
        else hoja.Cell(4, columna).Value = CaeManager.Domain.Common.DiaDeNegocio.Hoy().ToDateTime(TimeOnly.MinValue).AddDays(-30);

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Empleados").Subject;
        omitido.Fila.Should().Be(4);
        omitido.Motivo.Should().Contain("Faltan datos obligatorios");
        plan.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_Empleados_totalmente_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        hoja.Cell(4, 1).Value = 1; // Solo el número de fila — el resto de la fila, en blanco.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Fecha_de_documento_ilegible_queda_omitida_nombrando_el_valor_bruto()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 7).Value = "No aplica"; // Columna G: texto, no una fecha.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Descripcion.Contains(TipoDocumentoConocido)).Subject;
        omitido.Hoja.Should().Be("Empleados");
        omitido.Fila.Should().Be(4);
        omitido.Motivo.Should().Contain("No aplica");
        omitido.Motivo.Should().Contain("no se pudo interpretar");
        plan.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Celda_de_fecha_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4); // Ninguna columna de documento recibe valor.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Advertencias.Should().BeEmpty();
        plan.Documentos.Should().BeEmpty();
    }

    /// <summary>REC-128, absorbido y cerrado en REC-129 con el mismo ayudante que usan los otros tres analizadores.</summary>
    [Fact]
    public async Task Fecha_de_nacimiento_ilegible_no_bloquea_al_trabajador_y_queda_como_aviso_nombrando_el_valor_bruto()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 5).Value = "hace treinta años"; // Columna E: texto, no una fecha.

        var plan = await AnalizarAsync(libro);

        var trabajador = plan.Trabajadores.Should().ContainSingle().Subject;
        trabajador.FechaNacimiento.Should().BeNull();

        // El trabajador sí se crea, así que no es una omisión sino un aviso (como la
        // fecha de contrato en la plantilla combinada).
        plan.Omitidos.Should().NotContain(o => o.Hoja == "Empleados");
        var aviso = plan.Advertencias.Should().ContainSingle(a => a.Hoja == "Empleados").Subject;
        aviso.Fila.Should().Be(4);
        aviso.Motivo.Should().Be("La fecha de nacimiento «hace treinta años» no se pudo interpretar; el trabajador se importó sin ese dato.");
    }

    /// <summary>Caso legítimo gemelo del anterior: celda vacía, ningún registro, y el trabajador se importa igual.</summary>
    [Fact]
    public async Task Fecha_de_nacimiento_vacia_no_genera_ninguna_entrada_y_el_trabajador_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4); // Columna 5 (fecha de nacimiento) queda sin valor.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var trabajador = plan.Trabajadores.Should().ContainSingle().Subject;
        trabajador.FechaNacimiento.Should().BeNull();
    }

    /// <summary>
    /// El documento del Trabajador es obligatorio y solo admite DNI, NIE o Pasaporte
    /// (<see cref="CaeManager.Domain.Trabajadores.CriterioDocumentoIdentidadTrabajador"/>): los marcadores que llegan en
    /// los Excel reales en lugar del número no crean un Trabajador y quedan omitidos con el motivo de su rama.
    /// </summary>
    [Theory]
    [InlineData("PENDIENTE", "debe llevar algún número")]
    [InlineData("SIN DNI", "no puede llevar espacios")]
    [InlineData("12345678A", "La letra de control del DNI no es válida: debería ser Z")]
    public async Task Fila_de_Empleados_con_un_marcador_en_lugar_del_documento_queda_omitida_con_su_motivo(string documento, string motivo)
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 4).Value = documento;

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Empleados").Subject;
        omitido.Fila.Should().Be(4);
        omitido.Descripcion.Should().Contain(documento);
        omitido.Motivo.Should().Contain(motivo);
        plan.Trabajadores.Should().BeEmpty();
    }

    /// <summary>Sin forma de DNI ni de NIE, el documento se importa como Pasaporte y la fila lo avisa.</summary>
    [Fact]
    public async Task Documento_sin_forma_de_DNI_ni_NIE_se_importa_como_Pasaporte_con_aviso()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 4).Value = "ab-123.4567";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.Trabajadores.Should().ContainSingle().Which.Dni.Should().Be("AB1234567");
        var aviso = plan.Advertencias.Should().ContainSingle(a => a.Hoja == "Empleados").Subject;
        aviso.Fila.Should().Be(4);
        aviso.Motivo.Should().Contain(CaeManager.Domain.Trabajadores.CriterioDocumentoIdentidadTrabajador.AvisoPasaporte);
    }

    [Fact]
    public async Task Tipo_de_documento_fuera_del_catalogo_queda_omitido_y_no_en_Advertencias()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 7).Value = CaeManager.Domain.Common.DiaDeNegocio.Hoy().ToDateTime(TimeOnly.MinValue).AddDays(-30); // Fecha válida, pero el tipo no está en el catálogo (fake sin sembrar).

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Descripcion.Contains(TipoDocumentoConocido)).Subject;
        omitido.Motivo.Should().Contain("No existe este tipo de documento en el catálogo");
        plan.Advertencias.Should().NotContain(a => a.Descripcion.Contains(TipoDocumentoConocido));
        plan.Advertencias.Should().NotContain(a => a.Motivo.Contains("se omitió"));
        plan.Documentos.Should().BeEmpty();
    }

    /// <summary>
    /// El documento lleva la hoja de trabajadores de la que salió (la de
    /// empleados o la de extranjeros, posiciones 2 y 3 de
    /// <see cref="NuevoLibroBase"/>), para que un omitido al escribir se
    /// reporte en su hoja real.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Documento_lleva_la_hoja_de_trabajadores_de_la_que_salio(int posicionHoja)
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheet(posicionHoja);
        EscribirTrabajadorValido(hoja, fila: 4);
        hoja.Cell(4, 7).Value = CaeManager.Domain.Common.DiaDeNegocio.Hoy().ToDateTime(TimeOnly.MinValue).AddDays(-30);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(new CaeManager.Domain.Documentos.TipoDocumento(
            TipoDocumentoConocido, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            ambitoAplicacion: CaeManager.Domain.Documentos.AmbitoAplicacion.Trabajador));

        var plan = await AnalizarAsync(libro, new EmpresasQueryContextFalso(), tipos);

        var documento = plan.Documentos.Should().ContainSingle().Subject;
        documento.NombreTipoDocumento.Should().Be(TipoDocumentoConocido);
        documento.Hoja.Should().Be(hoja.Name);
    }

    [Fact]
    public async Task Fila_de_Asignaciones_sin_nombre_ni_apellidos_pero_con_marca_de_centro_queda_omitida()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 4).Value = "X"; // Marca la única columna de centro, sin nombre ni apellidos.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Asignaciones").Subject;
        omitido.Fila.Should().Be(5);
        omitido.Motivo.Should().Contain("Faltan nombre o apellidos");
        plan.Advertencias.Should().BeEmpty();
        plan.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_de_Asignaciones_totalmente_vacia_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1; // Solo el número de fila — sin nombre, apellidos ni marcas.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().NotContain(o => o.Hoja == "Asignaciones" && o.Fila == 5);
        plan.Advertencias.Should().BeEmpty();
        plan.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Columna_de_centro_sin_marca_no_genera_ninguna_entrada()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaEmpleados = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hojaEmpleados, fila: 4, nombre: "Ana", apellidos: "García");

        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 2).Value = "Ana";
        hojaAsignaciones.Cell(5, 3).Value = "García";
        // Columna 4 (única columna de centro) queda sin marcar a propósito.

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().NotContain(o => o.Hoja == "Asignaciones");
        plan.Asignaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Mismo defecto que <c>ClosedXmlPlantillaClientesServiceTests.Empresa_homonima_...</c>:
    /// Centros_Plataformas también compara por nombre contra CUALQUIER Empresa,
    /// no contra Cliente empresarial (<c>EsCritico != null</c>) como hace
    /// EjecutarImportacionCommandHandler — una Subcontrata homónima no puede
    /// marcar la fila como "cliente ya existente".
    /// </summary>
    [Fact]
    public async Task Centro_cuyo_nombre_coincide_con_una_Empresa_que_no_es_Cliente_no_se_marca_como_cliente_existente()
    {
        var libro = NuevoLibroBase();
        var hojaCentros = libro.Worksheets.Worksheet("Centros_Plataformas");
        hojaCentros.Cell(5, 2).Value = "Subcontrata Homónima S.L.";

        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(new Empresa("Subcontrata Homónima S.L.")); // EsCritico == null: no es Cliente.

        var plan = await AnalizarAsync(libro, empresas);

        var clienteCentro = plan.ClientesCentros.Should().ContainSingle().Subject;
        clienteCentro.YaExisteCliente.Should().BeFalse();
    }

    /// <summary>
    /// La Empresa de cada hoja de Trabajadores sale de la configuración, no del código: cada Trabajador
    /// y cada alta de Empresa llevan la razón social y la fila real de la hoja de la que salen.
    /// </summary>
    [Fact]
    public async Task La_Empresa_de_cada_hoja_de_Trabajadores_sale_de_la_configuracion_y_la_fila_viaja_en_el_plan()
    {
        var libro = NuevoLibroBase();
        var hojaEmpleados = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hojaEmpleados, fila: 4);
        EscribirTrabajadorValido(hojaEmpleados, fila: 5, nombre: "Luis", apellidos: "Mora");
        hojaEmpleados.Cell(5, 4).Value = "99999999R";
        EscribirTrabajadorValido(libro.Worksheets.Worksheet(HojaFilial), fila: 4, nombre: "Pedro", apellidos: "Soler");
        libro.Worksheets.Worksheet(HojaFilial).Cell(4, 4).Value = "11111111H";

        var plan = await AnalizarAsync(libro);

        plan.Empresas.Select(e => (e.RazonSocial, e.Hoja, e.Fila)).Should().BeEquivalentTo(
            new[] { (EmpresaMatriz, "Empleados", 4), (EmpresaFilial, HojaFilial, 4) });
        plan.Trabajadores.Select(t => (t.RazonSocialEmpresa, t.Hoja, t.Fila)).Should().BeEquivalentTo(
            new[] { (EmpresaMatriz, "Empleados", 4), (EmpresaMatriz, "Empleados", 5), (EmpresaFilial, HojaFilial, 4) });
    }

    [Fact]
    public async Task Los_documentos_y_las_asignaciones_del_plan_llevan_la_fila_real_de_su_hoja()
    {
        var libro = NuevoLibroBase();
        ConfigurarUnCentro(libro, "Centro Norte");
        var hojaEmpleados = libro.Worksheets.Worksheet("Empleados");
        EscribirTrabajadorValido(hojaEmpleados, fila: 4, nombre: "Ana", apellidos: "García");
        hojaEmpleados.Cell(4, 7).Value = CaeManager.Domain.Common.DiaDeNegocio.Hoy().ToDateTime(TimeOnly.MinValue).AddDays(-30);
        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 2).Value = "Ana";
        hojaAsignaciones.Cell(5, 3).Value = "García";
        hojaAsignaciones.Cell(5, 4).Value = "X";
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(new CaeManager.Domain.Documentos.TipoDocumento(
            TipoDocumentoConocido, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            ambitoAplicacion: CaeManager.Domain.Documentos.AmbitoAplicacion.Trabajador));

        var plan = await AnalizarAsync(libro, new EmpresasQueryContextFalso(), tipos);

        plan.Documentos.Should().ContainSingle().Which.Should().Match<CaeManager.Application.Importacion.DocumentoImportadoDto>(
            d => d.Hoja == "Empleados" && d.Fila == 4);
        plan.Asignaciones.Should().ContainSingle().Which.Should().Match<CaeManager.Application.Importacion.AsignacionImportadaDto>(
            a => a.Hoja == "Asignaciones" && a.Fila == 5);
        plan.ClientesCentros.Should().ContainSingle().Which.Fila.Should().Be(5);
    }

    /// <summary>Sin Empresa declarada el lector no inventa una: lo dice y no lee Trabajadores.</summary>
    [Fact]
    public async Task Sin_hojas_de_Trabajadores_configuradas_no_se_lee_ningun_Trabajador_y_se_dice_en_Omitidos()
    {
        var libro = NuevoLibroBase();
        EscribirTrabajadorValido(libro.Worksheets.Worksheet("Empleados"), fila: 4);

        var plan = await AnalizarAsync(libro, new EmpresasQueryContextFalso(), opciones: new OpcionesPlantillaCaeCompleta());

        plan.Trabajadores.Should().BeEmpty();
        plan.Empresas.Should().BeEmpty();
        var omitido = plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Configuración").Subject;
        omitido.Motivo.Should().Contain("no tiene configuradas").And.Contain("Trabajadores");
    }

    [Fact]
    public async Task Una_entrada_de_configuracion_sin_razon_social_no_se_lee()
    {
        var libro = NuevoLibroBase();
        EscribirTrabajadorValido(libro.Worksheets.Worksheet("Empleados"), fila: 4);
        var opciones = new OpcionesPlantillaCaeCompleta { HojasTrabajadores = [new() { Hoja = "Empleados", RazonSocialEmpresa = "  " }] };

        var plan = await AnalizarAsync(libro, new EmpresasQueryContextFalso(), opciones: opciones);

        plan.Trabajadores.Should().BeEmpty();
        plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Configuración");
    }

    [Fact]
    public async Task Dos_hojas_de_la_misma_Empresa_dan_una_sola_alta_de_Empresa()
    {
        var libro = NuevoLibroBase();
        EscribirTrabajadorValido(libro.Worksheets.Worksheet("Empleados"), fila: 4);
        var hojaFilial = libro.Worksheets.Worksheet(HojaFilial);
        EscribirTrabajadorValido(hojaFilial, fila: 4, nombre: "Pedro", apellidos: "Soler");
        hojaFilial.Cell(4, 4).Value = "99999999R";
        var opciones = new OpcionesPlantillaCaeCompleta
        {
            HojasTrabajadores = [new() { Hoja = "Empleados", RazonSocialEmpresa = EmpresaMatriz }, new() { Hoja = HojaFilial, RazonSocialEmpresa = EmpresaMatriz.ToUpperInvariant() }],
        };

        var plan = await AnalizarAsync(libro, new EmpresasQueryContextFalso(), opciones: opciones);

        plan.Empresas.Should().ContainSingle();
        plan.Trabajadores.Should().HaveCount(2);
    }

    /// <summary>
    /// Las cuatro hojas que <see cref="ClosedXmlImportacionParser.AnalizarAsync"/>
    /// espera siempre, vacías: sin esto, la ausencia de cualquiera de ellas
    /// registraría su propio "Hoja completa no encontrada" en Omitidos y
    /// contaminaría el test de la causal que de verdad se quiere aislar.
    /// </summary>
    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Centros_Plataformas");
        libro.Worksheets.Add("Empleados");
        libro.Worksheets.Add(HojaFilial);
        libro.Worksheets.Add("Asignaciones");
        return libro;
    }

    private static void EscribirTrabajadorValido(IXLWorksheet hoja, int fila, string nombre = "Marta", string apellidos = "Ruiz")
    {
        hoja.Cell(fila, 1).Value = fila - 3;
        hoja.Cell(fila, 2).Value = nombre;
        hoja.Cell(fila, 3).Value = apellidos;
        hoja.Cell(fila, 4).Value = DniValido;
    }

    /// <summary>
    /// Da de alta un único Centro en Centros_Plataformas y su columna
    /// correspondiente (por posición) en la cabecera de Asignaciones — el
    /// mínimo que exige <c>AnalizarAsignaciones</c> para no descartar la hoja
    /// entera por descuadre de columnas.
    /// </summary>
    private static void ConfigurarUnCentro(XLWorkbook libro, string nombreCentro)
    {
        var hojaCentros = libro.Worksheets.Worksheet("Centros_Plataformas");
        hojaCentros.Cell(5, 2).Value = nombreCentro;

        var hojaAsignaciones = libro.Worksheets.Worksheet("Asignaciones");
        hojaAsignaciones.Cell(4, 4).Value = "Centro 1";
        hojaAsignaciones.Cell(4, 5).Value = "TOTAL CENTROS";
    }

    /// <summary>Las dos hojas de Trabajadores del libro base, cada una con la razón social que declara la configuración.</summary>
    private static OpcionesPlantillaCaeCompleta OpcionesDePrueba() => new()
    {
        HojasTrabajadores =
        [
            new() { Hoja = "Empleados", RazonSocialEmpresa = EmpresaMatriz },
            new() { Hoja = HojaFilial, RazonSocialEmpresa = EmpresaFilial },
        ],
    };

    private static Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(XLWorkbook libro) =>
        AnalizarAsync(libro, new EmpresasQueryContextFalso());

    private static async Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(
        XLWorkbook libro, EmpresasQueryContextFalso empresas, TiposDocumentoQueryContextFalso? tipos = null,
        OpcionesPlantillaCaeCompleta? opciones = null)
    {
        var parser = new ClosedXmlImportacionParser(
            new AsignacionesQueryContextFalso(), new CentrosQueryContextFalso(), new DocumentosQueryContextFalso(),
            empresas, tipos ?? new TiposDocumentoQueryContextFalso(), new TrabajadoresQueryContextFalso(),
            Options.Create(opciones ?? OpcionesDePrueba()));

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await parser.AnalizarAsync(flujo);
    }
}
