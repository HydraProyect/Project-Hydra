using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La vista previa de una carga masiva no debe servir de oráculo de existencia: con autoridad acotada, lo existente fuera
/// del ámbito del Coordinador CAE se presenta como nuevo (<c>YaExiste</c> falso) y la ejecución lo omite y lo cuenta con
/// el motivo uniforme, sin decir que «ya existía». El Administrador no se recorta.
/// </summary>
public class RecorteDeAnalisisAlAmbitoTests
{
    private static PlanImportacionDto Plan() => EscenarioImportacion.Plan(
        empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: true)],
        trabajadores:
        [
            new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: true)
        ],
        documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: true)
        ],
        asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: true)
        ]);

    private static EscenarioImportacion Existente() => new EscenarioImportacion()
        .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente()
        .ConTipoDocumentoExistente().ConDocumentoExistente().ConAsignacionActivaExistente();

    [Fact]
    public async Task Coordinador_no_ve_como_existente_lo_que_esta_fuera_de_su_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [], trabajadorIdsVisibles: [], centroIdsParaGestion: []);

        var plan = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(Plan(), default);

        plan.Empresas.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Trabajadores.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Documentos.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Asignaciones.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinador_sigue_viendo_como_existente_lo_que_esta_dentro_de_su_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            empresaIdsParaGestion: [escenario.EmpresasContexto.ListaEmpresas.Single().Id],
            trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id],
            centroIdsParaGestion: [escenario.CentroExistente!.Id]);

        var plan = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(Plan(), default);

        plan.Empresas.Single().YaExiste.Should().BeTrue();
        plan.Trabajadores.Single().YaExiste.Should().BeTrue();
        plan.Documentos.Single().YaExiste.Should().BeTrue();
        plan.Asignaciones.Single().YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Administrador_no_se_recorta()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);

        var plan = await escenario.Recorte("Administrador", alcance).RecortarAsync(Plan(), default);

        plan.Trabajadores.Single().YaExiste.Should().BeTrue();
        plan.Empresas.Single().YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_autoridad_nada_figura_como_existente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true);

        var plan = await escenario.Recorte("GestorCae", alcance).RecortarAsync(Plan(), default);

        plan.Trabajadores.Single().YaExiste.Should().BeFalse("el fallo es cerrado: sin autoridad el ámbito es vacío");
    }

    [Fact]
    public async Task La_vista_previa_combinada_tambien_se_recorta()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [], trabajadorIdsVisibles: []);
        var combinada = new PlanImportacionCombinadaDto(
            Guid.NewGuid(), [], [new EmpresaCombinadaImportadaDto(EscenarioImportacion.EmpresaConocida, [], YaExiste: true)], [],
            [new TrabajadorImportadoDto(EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: true)],
            [], []);

        var plan = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(combinada, default);

        plan.Empresas.Single().YaExiste.Should().BeFalse();
        plan.Trabajadores.Single().YaExiste.Should().BeFalse();
    }

    [Fact]
    public async Task Ejecutar_un_trabajador_existente_fuera_de_ambito_lo_omite_sin_decir_que_ya_existia()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConTrabajadorExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [], empresaIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: false)],
            trabajadores:
            [
                new TrabajadorImportadoDto(
                    EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: false)
            ]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new EjecutarImportacionCommand(plan), default)).Valor;

        resultado.Omitidos.Should().HaveCount(2);
        resultado.Omitidos.Should().OnlyContain(o => o.Motivo.Contains("fuera de tu ámbito"));
        resultado.Omitidos.Should().NotContain(o => o.Motivo.Contains("ya existía"));
    }

    [Fact]
    public async Task Plantilla_de_documentos_trata_un_DNI_fuera_de_ambito_igual_que_uno_inexistente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);
        var plan = EscenarioImportacion.Plan(documentos:
            [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false, Hoja: "Documentos", Fila: 4)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Documentos.Should().BeEmpty();
        var omitido = recortado.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(4);
        omitido.Descripcion.Should().Be(EscenarioImportacion.DniConocido);
        omitido.Motivo.Should().StartWith("No existe ningún trabajador con este DNI.");
    }

    [Fact]
    public async Task Plantilla_de_documentos_conserva_el_DNI_dentro_de_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id]);
        var plan = EscenarioImportacion.Plan(documentos:
            [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false, Hoja: "Documentos", Fila: 4)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Documentos.Should().ContainSingle();
        recortado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Plantilla_de_clientes_trata_un_Cliente_o_Centro_fuera_de_ambito_igual_que_uno_inexistente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(clientesCentros:
            [new ClienteCentroImportadoDto(EscenarioImportacion.CentroConocido, false, null, null, YaExisteCliente: true, YaExisteCentro: true, Hoja: "Centros_Plataformas", Fila: 7)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.ClientesCentros.Should().BeEmpty();
        recortado.Omitidos.Should().ContainSingle().Which.Fila.Should().Be(7);
        recortado.Omitidos.Single().Motivo.Should().Contain("no existe todavía");
    }
}
