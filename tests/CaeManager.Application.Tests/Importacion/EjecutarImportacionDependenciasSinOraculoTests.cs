using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Confirmación de la plantilla genérica con autoridad acotada: las filas que NO intentan crear lo que citan (una
/// Asignación, que no crea Trabajador ni Centro; la hoja Centros_Plataformas, que no crea Cliente empresarial ni Centro;
/// un Trabajador cuya Empresa no viene en el fichero; un documento o una Asignación de un DNI en la Papelera) se omiten
/// con el MISMO motivo cuando lo citado no existe y cuando existe fuera del ámbito. Si no, el resultado sería un oráculo
/// de existencia distinto del de clave única que Chris aceptó (que solo cubre el intento de alta de un DNI, CIF, razón
/// social o Centro). El Administrador conserva los motivos accionables.
/// </summary>
public class EjecutarImportacionDependenciasSinOraculoTests
{
    private const string Coordinador = "CoordinadorCae";
    private const string ClienteConocido = "Cadena Industrial Iberia S.A.";

    private static async Task<ResultadoImportacionDto> EjecutarAsync(
        EscenarioImportacion escenario, PlanImportacionDto plan, string rol, ClientesFalsos.AlcanceDatosServiceFalso alcance) =>
        (await escenario.Handler(rol, alcance).Handle(new EjecutarImportacionCommand(plan), default)).Valor;

    private static ClientesFalsos.AlcanceDatosServiceFalso SinNadaEnElAmbito() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [], trabajadorIdsVisibles: [], empresaIdsParaGestion: [], centroIdsParaGestion: []);

    private static PlanImportacionDto PlanConUnaAsignacion() => EscenarioImportacion.Plan(asignaciones:
        [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false, Fila: 2)]);

    [Fact]
    public async Task Una_asignacion_de_un_DNI_inexistente_se_omite_igual_que_la_de_un_trabajador_fuera_de_ambito()
    {
        var conTrabajador = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var sinTrabajador = new EscenarioImportacion().ConCentroExistente();

        var fueraDeAmbito = await EjecutarAsync(conTrabajador, PlanConUnaAsignacion(), Coordinador,
            new(tieneAccesoTotal: false, trabajadorIdsVisibles: [], centroIdsParaGestion: [conTrabajador.CentroExistente!.Id]));
        var inexistente = await EjecutarAsync(sinTrabajador, PlanConUnaAsignacion(), Coordinador,
            new(tieneAccesoTotal: false, trabajadorIdsVisibles: [], centroIdsParaGestion: [sinTrabajador.CentroExistente!.Id]));

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito, "«no existe» frente a «fuera de tu ámbito» delataría que el DNI existe en el Tenant");
        (await new EscenarioImportacion().ConCentroExistente().EjecutarAsync(PlanConUnaAsignacion())).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe", "el Administrador ve el Tenant entero y conserva el motivo accionable");
    }

    [Fact]
    public async Task Una_asignacion_de_un_trabajador_fuera_de_ambito_a_un_centro_ambiguo_no_delata_al_trabajador()
    {
        // Sin el orden (ámbito del Trabajador antes que las causales del Centro), el DNI existente fuera del ámbito
        // recibiría «hay varios centros llamados…» y el inexistente el motivo uniforme.
        static (EscenarioImportacion Escenario, ClientesFalsos.AlcanceDatosServiceFalso Alcance) ConDosCentrosHomonimosVisibles(EscenarioImportacion escenario)
        {
            escenario.ConCentroExistente().ConCentroExistente();
            return (escenario, new(tieneAccesoTotal: false, trabajadorIdsVisibles: [],
                centroIdsParaGestion: [.. escenario.CentrosContexto.ListaCentros.Select(c => c.Id)]));
        }

        var (conTrabajador, alcanceConTrabajador) = ConDosCentrosHomonimosVisibles(new EscenarioImportacion().ConTrabajadorExistente());
        var (sinTrabajador, alcanceSinTrabajador) = ConDosCentrosHomonimosVisibles(new EscenarioImportacion());

        var fueraDeAmbito = await EjecutarAsync(conTrabajador, PlanConUnaAsignacion(), Coordinador, alcanceConTrabajador);
        var inexistente = await EjecutarAsync(sinTrabajador, PlanConUnaAsignacion(), Coordinador, alcanceSinTrabajador);

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito);
    }

    [Fact]
    public async Task Una_asignacion_a_un_centro_inexistente_se_omite_igual_que_a_uno_fuera_de_ambito()
    {
        var conCentro = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var sinCentro = new EscenarioImportacion().ConTrabajadorExistente();

        var fueraDeAmbito = await EjecutarAsync(conCentro, PlanConUnaAsignacion(), Coordinador,
            new(tieneAccesoTotal: false, trabajadorIdsVisibles: [conCentro.TrabajadorExistente!.Id], centroIdsParaGestion: []));
        var inexistente = await EjecutarAsync(sinCentro, PlanConUnaAsignacion(), Coordinador,
            new(tieneAccesoTotal: false, trabajadorIdsVisibles: [sinCentro.TrabajadorExistente!.Id], centroIdsParaGestion: []));

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito, "«el centro no existe» frente a «fuera de tu ámbito» delataría que el Centro existe");
        (await new EscenarioImportacion().ConTrabajadorExistente().EjecutarAsync(PlanConUnaAsignacion())).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe en el sistema");
    }

    [Fact]
    public async Task Una_asignacion_o_un_documento_de_un_DNI_en_la_Papelera_se_omite_igual_que_uno_inexistente()
    {
        var plan = EscenarioImportacion.Plan(
            documentos: [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false)],
            asignaciones: [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false, Fila: 2)]);
        var enPapelera = new EscenarioImportacion().ConTipoDocumentoExistente();
        enPapelera.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);
        var inexistente = new EscenarioImportacion().ConTipoDocumentoExistente();

        var resultadoPapelera = await EjecutarAsync(enPapelera, plan, Coordinador, SinNadaEnElAmbito());
        var resultadoInexistente = await EjecutarAsync(inexistente, plan, Coordinador, SinNadaEnElAmbito());

        resultadoPapelera.Omitidos.Should().HaveCount(2);
        resultadoPapelera.Should().BeEquivalentTo(resultadoInexistente, "un motivo propio de la Papelera delataría que el DNI existe en el Tenant");
        (await enPapelera.EjecutarAsync(plan with { OperacionId = Guid.NewGuid() })).Omitidos
            .Should().OnlyContain(o => o.Motivo.Contains("Papelera"), "el Administrador conserva el accionable «restáuralo»");
    }

    [Fact]
    public async Task Un_trabajador_cuya_empresa_no_existe_se_omite_igual_que_uno_cuya_empresa_queda_fuera_de_ambito()
    {
        var plan = EscenarioImportacion.Plan(trabajadores:
        [
            new TrabajadorImportadoDto(EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández",
                EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)
        ]);

        var fueraDeAmbito = await EjecutarAsync(new EscenarioImportacion().ConEmpresaExistente(), plan, Coordinador, SinNadaEnElAmbito());
        var inexistente = await EjecutarAsync(new EscenarioImportacion(), plan, Coordinador, SinNadaEnElAmbito());

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito, "esta fila no intenta crear la Empresa que cita");
        (await new EscenarioImportacion().EjecutarAsync(plan)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe");
    }

    private static ClienteCentroImportadoDto FilaDeCentros(string nombre) =>
        new(nombre, EsCritico: false, Direccion: null, Contacto: null, YaExisteCliente: false, YaExisteCentro: false, Fila: 2);

    private static EscenarioImportacion ConCliente(EscenarioImportacion escenario, out Guid clienteId)
    {
        var cliente = Empresa.CrearComoCliente(ClienteConocido, "A58818501", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        escenario.EmpresasContexto.ListaEmpresas.Add(cliente);
        clienteId = cliente.Id;
        return escenario;
    }

    [Fact]
    public async Task La_hoja_de_centros_omite_igual_un_Cliente_empresarial_inexistente_que_uno_fuera_de_ambito()
    {
        // El Cliente empresarial y el Centro se llaman igual (la hoja Centros_Plataformas busca ambos por la misma columna).
        var plan = EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]);
        var conCliente = ConCliente(new EscenarioImportacion(), out var clienteId).ConCentroExistente(ClienteConocido, clienteId);

        var fueraDeAmbito = await EjecutarAsync(conCliente, plan, Coordinador, SinNadaEnElAmbito());
        var inexistente = await EjecutarAsync(new EscenarioImportacion(), plan, Coordinador, SinNadaEnElAmbito());

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito",
            "un Cliente empresarial existente fuera del ámbito no puede pasar en silencio mientras el inexistente dice «no existe»");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito);
        (await new EscenarioImportacion().EjecutarAsync(plan)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("Este Cliente no existe todavía");
    }

    [Fact]
    public async Task La_hoja_de_centros_omite_igual_un_Centro_inexistente_que_uno_fuera_de_ambito()
    {
        var plan = EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]);
        var conCentro = ConCliente(new EscenarioImportacion(), out var clienteId).ConCentroExistente(ClienteConocido, clienteId);
        var sinCentro = ConCliente(new EscenarioImportacion(), out var otroClienteId);

        var fueraDeAmbito = await EjecutarAsync(conCentro, plan, Coordinador,
            new(tieneAccesoTotal: false, clienteIdsVisibles: [clienteId], trabajadorIdsVisibles: [], centroIdsParaGestion: []));
        var inexistente = await EjecutarAsync(sinCentro, plan, Coordinador,
            new(tieneAccesoTotal: false, clienteIdsVisibles: [otroClienteId], trabajadorIdsVisibles: [], centroIdsParaGestion: []));

        fueraDeAmbito.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        inexistente.Should().BeEquivalentTo(fueraDeAmbito, "«este centro no existe» frente a silencio delataría que el Centro existe");
        (await ConCliente(new EscenarioImportacion(), out _).EjecutarAsync(plan)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("Este centro no existe todavía");
    }

    [Theory]
    [InlineData(Coordinador)]
    [InlineData("DireccionCae")]
    public async Task Con_autoridad_acotada_que_no_recorta_se_conservan_los_motivos_accionables(string rol)
    {
        // Revisor-puente: sin lista que recorte no hay nada fuera del ámbito que ocultar, y el motivo uniforme sería
        // falso («queda fuera de tu ámbito») y no accionable.
        var sinRecorte = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true);
        var trabajadorSinEmpresa = EscenarioImportacion.Plan(trabajadores:
        [
            new TrabajadorImportadoDto(EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández",
                EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)
        ]);

        (await EjecutarAsync(new EscenarioImportacion(), PlanConUnaAsignacion(), rol, sinRecorte)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe").And.NotContain("fuera de tu ámbito");
        (await EjecutarAsync(new EscenarioImportacion().ConTrabajadorExistente(), PlanConUnaAsignacion(), rol, sinRecorte)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe en el sistema");
        (await EjecutarAsync(new EscenarioImportacion(), trabajadorSinEmpresa, rol, sinRecorte)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe");
        (await EjecutarAsync(new EscenarioImportacion(), EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]), rol, sinRecorte)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("Este Cliente no existe todavía");
        (await EjecutarAsync(ConCliente(new EscenarioImportacion(), out _), EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]), rol, sinRecorte)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("Este centro no existe todavía");
    }

    /// <summary>Asignación de Cartera universal: listas explícitas (aquí vacías) y, aun así, el Tenant entero alcanzado.</summary>
    private sealed class CarteraUniversalConListasExplicitas()
        : ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [], trabajadorIdsVisibles: [],
            empresaIdsParaGestion: [], centroIdsParaGestion: []), IAlcanceDatosService
    {
        public Task<bool> AlcanzaElTenantEnteroAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Fact]
    public async Task Con_cartera_universal_una_empresa_creada_por_el_fichero_tampoco_entra_en_el_ambito_de_la_misma_carga()
    {
        // Las listas explícitas de la cartera universal se leyeron antes de crear la Empresa: no entra hasta la carga siguiente.
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto("Empresa Nueva S.L.", YaExiste: false)],
            trabajadores: [new TrabajadorImportadoDto("Empresa Nueva S.L.", "Lucía", "Fernández",
                EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)]);

        var resultado = await EjecutarAsync(new EscenarioImportacion(), plan, Coordinador, new CarteraUniversalConListasExplicitas());

        resultado.EmpresasCreadas.Should().Be(1);
        resultado.TrabajadoresCreados.Should().Be(0, "ni la cartera universal amplía el ámbito con lo que crea la propia carga");
        resultado.Advertencias.Should().ContainSingle().Which.Motivo.Should().Contain("no queda en tu ámbito durante esta carga");
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Be(
            AmbitoCargaMasiva.MotivoEmpresaCreadaFueraDeCartera("Empresa Nueva S.L."), "el motivo uniforme de «fuera de tu ámbito» no oculta aquí nada");
    }

    [Fact]
    public async Task Con_cartera_universal_de_listas_explicitas_se_conservan_los_motivos_accionables()
    {
        // Codex sobre 7ae0fddfd (BAJA): una lista explícita no basta para decir que hay algo fuera del ámbito.
        var universal = new CarteraUniversalConListasExplicitas();

        (await EjecutarAsync(new EscenarioImportacion(), PlanConUnaAsignacion(), Coordinador, universal)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("no existe").And.NotContain("fuera de tu ámbito");
        (await EjecutarAsync(new EscenarioImportacion(), EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]), Coordinador, universal)).Omitidos
            .Should().ContainSingle().Which.Motivo.Should().Contain("Este Cliente no existe todavía");
    }

    [Fact]
    public async Task Sin_lista_que_recorte_un_DNI_en_la_Papelera_conserva_el_motivo_restauralo()
    {
        // Codex sobre 7ae0fddfd (MEDIA): sin recorte, «fuera de tu ámbito» para la Papelera y «no existe» para el DNI
        // inexistente distinguían la existencia en la Papelera, y además eran falsos.
        var plan = EscenarioImportacion.Plan(
            documentos: [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false)],
            asignaciones: [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false, Fila: 2)]);
        var enPapelera = new EscenarioImportacion().ConTipoDocumentoExistente();
        enPapelera.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);

        (await EjecutarAsync(enPapelera, plan, Coordinador, new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true))).Omitidos
            .Should().HaveCount(2).And.OnlyContain(o => o.Motivo.Contains("Papelera") && !o.Motivo.Contains("fuera de tu ámbito"));
    }

    [Fact]
    public async Task La_hoja_de_centros_no_omite_nada_si_el_Cliente_empresarial_y_el_Centro_existen_dentro_del_ambito()
    {
        var plan = EscenarioImportacion.Plan(clientesCentros: [FilaDeCentros(ClienteConocido)]);
        var escenario = ConCliente(new EscenarioImportacion(), out var clienteId).ConCentroExistente(ClienteConocido, clienteId);

        var resultado = await EjecutarAsync(escenario, plan, Coordinador,
            new(tieneAccesoTotal: false, clienteIdsVisibles: [clienteId], trabajadorIdsVisibles: [],
                centroIdsParaGestion: [escenario.CentroExistente!.Id]));

        resultado.Omitidos.Should().BeEmpty("lo que está dentro del ámbito se reutiliza como antes");
    }
}
