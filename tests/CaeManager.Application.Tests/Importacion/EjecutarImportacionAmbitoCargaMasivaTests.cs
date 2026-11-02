using CaeManager.Application.Importacion;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Ámbito efectivo del Coordinador CAE en una carga masiva (decisión de Chris, 2026-11-01): lo que cae fuera de su
/// Asignación de Cartera se omite y se cuenta, y NO se escribe — mismas dos mitades del contrato de DCR-12 B que
/// <see cref="EjecutarImportacionOmitidosTests"/>. El Administrador no se recorta: el ámbito solo acota a los roles
/// con cartera.
/// </summary>
public class EjecutarImportacionAmbitoCargaMasivaTests
{
    private static PlanImportacionDto PlanConUnTrabajador() => EscenarioImportacion.Plan(trabajadores:
    [
        new TrabajadorImportadoDto(
            EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández",
            EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)
    ]);

    [Fact]
    public async Task Coordinador_no_da_de_alta_un_trabajador_de_una_empresa_fuera_de_su_ambito()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(PlanConUnTrabajador()), default)).Valor;

        resultado.TrabajadoresCreados.Should().Be(0);
        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("fuera de tu ámbito");
        escenario.TrabajadorRepositorio.Trabajadores.Should().BeEmpty("la fila fuera de ámbito se cuenta, no se escribe");
    }

    [Fact]
    public async Task Coordinador_da_de_alta_un_trabajador_de_una_empresa_dentro_de_su_ambito()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        var empresaId = escenario.EmpresasContexto.ListaEmpresas.Single().Id;
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [empresaId]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(PlanConUnTrabajador()), default)).Valor;

        resultado.TrabajadoresCreados.Should().Be(1);
        resultado.Omitidos.Should().BeEmpty();
        escenario.TrabajadorRepositorio.Trabajadores.Should().ContainSingle();
    }

    [Fact]
    public async Task Coordinador_no_asigna_a_un_centro_fuera_de_su_ambito_aunque_el_trabajador_este_dentro()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id], centroIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(asignaciones:
            [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.AsignacionesCreadas.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinador_no_adjunta_documentos_a_un_trabajador_fuera_de_su_ambito()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConTipoDocumentoExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);
        var plan = EscenarioImportacion.Plan(documentos:
            [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.DocumentosCreados.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        escenario.DocumentoRepositorio.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Administrador_no_se_recorta_por_el_ambito_de_cartera()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);

        var resultado = (await escenario.Handler("Administrador", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(PlanConUnTrabajador()), default)).Valor;

        resultado.TrabajadoresCreados.Should().Be(1);
        resultado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinador_asigna_a_un_trabajador_dentro_de_su_ambito_a_un_centro_dentro()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id],
            centroIdsParaGestion: [escenario.CentroExistente!.Id]);
        var plan = EscenarioImportacion.Plan(asignaciones:
            [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.AsignacionesCreadas.Should().Be(1);
        resultado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinador_no_asigna_a_un_trabajador_fuera_de_su_ambito_aunque_el_centro_este_dentro()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [], centroIdsParaGestion: [escenario.CentroExistente!.Id]);
        var plan = EscenarioImportacion.Plan(asignaciones:
            [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.AsignacionesCreadas.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_trabajador_creado_por_la_carga_dentro_del_ambito_puede_recibir_su_documento_en_el_mismo_fichero()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConTipoDocumentoExistente();
        var empresaId = escenario.EmpresasContexto.ListaEmpresas.Single().Id;
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [empresaId], trabajadorIdsVisibles: []);
        var plan = EscenarioImportacion.Plan(
            trabajadores: [new TrabajadorImportadoDto(EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández",
                EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)],
            documentos: [new DocumentoImportadoDto(EscenarioImportacion.DniDesconocido,
                EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.TrabajadoresCreados.Should().Be(1);
        resultado.DocumentosCreados.Should().Be(1, "el trabajador se creó con su Empresa dentro del ámbito, así que está dentro");
        resultado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task D7_una_empresa_creada_por_el_fichero_no_amplia_el_ambito_y_su_trabajador_se_omite()
    {
        var escenario = new EscenarioImportacion();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto("Empresa Nueva S.L.", YaExiste: false)],
            trabajadores: [new TrabajadorImportadoDto("Empresa Nueva S.L.", "Lucía", "Fernández",
                EscenarioImportacion.DniDesconocido, null, null, YaExiste: false)]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default)).Valor;

        resultado.EmpresasCreadas.Should().Be(1);
        resultado.TrabajadoresCreados.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Direccion_CAE_con_alcance_sin_restriccion_no_se_recorta()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true);

        var resultado = (await escenario.Handler("DireccionCae", alcance)
            .Handle(new Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(PlanConUnTrabajador()), default)).Valor;

        resultado.TrabajadoresCreados.Should().Be(1);
    }

    [Fact]
    public async Task Sin_autoridad_el_ambito_falla_cerrado()
    {
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true);

        var ambito = await AmbitoCargaMasiva.ResolverAsync(
            CaeManager.Application.Common.AutoridadCargaMasiva.Ninguna, alcance, default);

        ambito.Empresa(Guid.NewGuid()).Should().BeFalse();
        ambito.Centro(Guid.NewGuid()).Should().BeFalse();
        ambito.Cliente(Guid.NewGuid()).Should().BeFalse();
        ambito.Trabajador(Guid.NewGuid()).Should().BeFalse();
    }
}
