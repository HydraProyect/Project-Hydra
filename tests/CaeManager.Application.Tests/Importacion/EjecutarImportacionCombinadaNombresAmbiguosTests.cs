using CaeManager.Application.Importacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La Combinada ante Clientes empresariales cuya razón social solo difiere en mayúsculas (C-06): nunca se resuelve al
/// azar (ni gana la última fila), el nombre ambiguo omite las filas que lo citan, y una Relación Empresarial vigente
/// no se cierra por un nombre que la importación no ha podido resolver.
/// </summary>
public class EjecutarImportacionCombinadaNombresAmbiguosTests
{
    private const string CifUno = "B12345674";
    private const string CifDos = "P1234567D";
    private const string EmpresaProveedora = "Instalaciones Únicas S.L.";
    private const string ClienteUnico = "Cliente Único S.A.";

    private static Empresa Cliente(string razonSocial, string cif) =>
        Empresa.CrearComoCliente(razonSocial, cif, false, notas: null, ejecutivoUsuarioId: null);

    private static EscenarioImportacionCombinada ConDosClientesHomonimos(out Empresa acme, out Empresa acmeMayusculas)
    {
        var escenario = new EscenarioImportacionCombinada();
        acme = Cliente("Acme S.L.", CifUno);
        acmeMayusculas = Cliente("ACME S.L.", CifDos);
        escenario.EmpresaRepositorio.Empresas.Add(acme);
        escenario.EmpresaRepositorio.Empresas.Add(acmeMayusculas);
        escenario.EmpresaRepositorio.Empresas.Add(new Empresa(EmpresaProveedora));
        return escenario;
    }

    [Fact]
    public async Task Con_Reemplazar_un_Cliente_ambiguo_no_cierra_las_Relaciones_Empresariales_vigentes_ni_cuenta_la_fila()
    {
        var escenario = ConDosClientesHomonimos(out var acme, out _);
        var proveedora = escenario.EmpresaRepositorio.Empresas.Single(e => e.RazonSocial == EmpresaProveedora);
        escenario.EmpresaRepositorio.Empresas.Add(Cliente(ClienteUnico, "A58818501"));
        // La Relación vigente es con el Cliente que el nombre ambiguo no deja resolver: al quedar fuera de los
        // "deseados", Reemplazar la cerraría por ausencia.
        escenario.RelacionesExistentes.Add(RelacionEmpresarial.Crear(proveedora.Id, acme.Id, DateTime.UtcNow));
        escenario.Relaciones.Permitir = true;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto(EmpresaProveedora, ["Acme S.L.", ClienteUnico], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true);

        escenario.Relaciones.Cerradas.Should().BeEmpty(
            "la fila no pudo resolver uno de sus Clientes: cerrar las demás Relaciones por 'ausencia' destruiría datos vigentes");
        escenario.Relaciones.Agregadas.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios Clientes empresariales");
        resultado.EmpresasActualizadas.Should().Be(0, "una fila omitida no se cuenta además como actualizada");
    }

    [Fact]
    public async Task Una_Empresa_nueva_cuya_fila_cita_un_Cliente_ambiguo_no_se_crea()
    {
        var escenario = ConDosClientesHomonimos(out _, out _);
        var antes = escenario.EmpresaRepositorio.Empresas.Count;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto("Empresa Nueva S.L.", ["Acme S.L."], YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true);

        escenario.EmpresaRepositorio.Empresas.Should().HaveCount(antes);
        resultado.EmpresasCreadas.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios Clientes empresariales");
    }

    [Fact]
    public async Task La_hoja_Clientes_con_los_dos_CIF_homonimos_no_deja_que_gane_la_ultima_fila()
    {
        var escenario = ConDosClientesHomonimos(out _, out _);
        var plan = EscenarioImportacionCombinada.Plan(
            clientes:
            [
                new ClienteImportadoDto("Acme S.L.", CifUno, false, YaExiste: true, Fila: 2),
                new ClienteImportadoDto("ACME S.L.", CifDos, false, YaExiste: true, Fila: 3),
            ],
            centros: [new CentroImportadoDto("Planta Norte", "Acme S.L.", EmpresaProveedora, null, null, null, null, YaExiste: false, Fila: 4)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.CentrosCreados.Should().Be(0);
        escenario.CentroRepositorio.Centros.Should().BeEmpty("el Centro no puede colgar del último Cliente que la hoja nombró");
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios Clientes empresariales");
    }

    [Fact]
    public async Task La_hoja_Clientes_que_identifica_por_CIF_desambigua_el_nombre_que_declara()
    {
        var escenario = ConDosClientesHomonimos(out var acme, out _);
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Acme S.L.", CifUno, false, YaExiste: true, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Norte", "Acme S.L.", EmpresaProveedora, null, null, null, null, YaExiste: false, Fila: 3)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().BeEmpty();
        resultado.CentrosCreados.Should().Be(1);
        escenario.CentroRepositorio.Centros.Should().ContainSingle().Which.ClienteId.Should().Be(acme.Id);
    }

    [Fact]
    public async Task Un_nombre_declarado_en_la_hoja_Clientes_no_pisa_a_otro_Cliente_existente_que_ya_lo_llevaba()
    {
        // Fusionar no renombra: el Cliente de CifUno sigue llamándose "Antiguo S.L." aunque el archivo lo llame "Nuevo S.L.",
        // y otro Cliente existente ya se llama "Nuevo S.L.". El nombre no puede resolverse en silencio a ninguno de los dos.
        var escenario = new EscenarioImportacionCombinada();
        escenario.EmpresaRepositorio.Empresas.Add(Cliente("Antiguo S.L.", CifUno));
        escenario.EmpresaRepositorio.Empresas.Add(Cliente("Nuevo S.L.", CifDos));
        escenario.EmpresaRepositorio.Empresas.Add(new Empresa(EmpresaProveedora));
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Nuevo S.L.", CifUno, false, YaExiste: true, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Norte", "Nuevo S.L.", EmpresaProveedora, null, null, null, null, YaExiste: false, Fila: 3)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.CentrosCreados.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios Clientes empresariales");
    }

    [Fact]
    public async Task Dos_Centros_existentes_del_mismo_Cliente_que_solo_difieren_en_mayusculas_omiten_la_fila_y_no_abortan()
    {
        var escenario = new EscenarioImportacionCombinada();
        var cliente = Cliente(ClienteUnico, CifUno);
        var proveedora = new Empresa(EmpresaProveedora);
        escenario.EmpresaRepositorio.Empresas.Add(cliente);
        escenario.EmpresaRepositorio.Empresas.Add(proveedora);
        escenario.CentrosContexto.ListaCentros.Add(new Centro(cliente.Id, proveedora.Id, "Planta Norte"));
        escenario.CentrosContexto.ListaCentros.Add(new Centro(cliente.Id, proveedora.Id, "PLANTA NORTE"));
        var plan = EscenarioImportacionCombinada.Plan(centros:
        [
            new CentroImportadoDto("Planta Norte", ClienteUnico, EmpresaProveedora, null, null, null, null, YaExiste: true, Fila: 2),
            new CentroImportadoDto("Planta Sur", ClienteUnico, EmpresaProveedora, null, null, null, null, YaExiste: false, Fila: 3),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios centros");
        resultado.CentrosCreados.Should().Be(1, "el resto del archivo sigue");
    }

    [Fact]
    public async Task Trabajador_con_DNI_en_la_Papelera_y_Empresa_inexistente_dice_Papelera_como_el_Importador_simple()
    {
        var escenario = new EscenarioImportacionCombinada();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add("12345678Z");
        var plan = EscenarioImportacionCombinada.Plan(trabajadores:
            [new TrabajadorImportadoDto("Empresa que no existe S.L.", "Lucía", "Fernández", "12345678Z", null, null, YaExiste: false, Hoja: "Trabajadores", Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Papelera");
    }
}
