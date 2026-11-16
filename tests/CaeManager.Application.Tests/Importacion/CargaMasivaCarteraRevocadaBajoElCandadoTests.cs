using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Empresas;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Issue #2118 en las cargas masivas: el ámbito (<see cref="AmbitoCargaMasiva"/>) se resuelve al empezar, pero el guardado
/// espera un candado (el de la regla A-1 si hay Asignaciones, el de Empresa propia si se crean Empresas propias). Una
/// Asignación de Cartera revocada durante esa espera no puede dejar pasar lo que ya no está en ella: bajo el candado se
/// invalida el alcance y se relee. <see cref="AlcanceMemoizadoRevocableFalso"/> responde con la memoización hasta que
/// alguien invalida; el candado de prueba revoca la cartera al tomarse.
/// </summary>
public class CargaMasivaCarteraRevocadaBajoElCandadoTests
{
    // ---------------------------------------------------------------- EjecutarImportacionCommand (candado A-1)

    [Fact]
    public async Task Una_asignacion_cuyo_centro_sale_de_la_cartera_mientras_espera_el_candado_se_omite_y_no_se_escribe()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConCentroExistente();
        var alcance = new AlcanceMemoizadoRevocableFalso(
            new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false,
                trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id], centroIdsParaGestion: [escenario.CentroExistente!.Id]),
            new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false,
                trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id], centroIdsParaGestion: []));
        // La primera pregunta, antes de esperar el candado, memoiza la cartera de entonces.
        await alcance.ObtenerCentroIdsParaGestionAsync();
        escenario.Serializacion.AlTomarElCandado = alcance.Revocar;
        var plan = EscenarioImportacion.Plan(asignaciones:
            [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false)]);

        var resultado = await escenario.Handler("CoordinadorCae", alcance, alcance)
            .Handle(new EjecutarImportacionCommand(plan), default);

        escenario.Serializacion.Llamadas.Should().Be(1);
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.AsignacionesCreadas.Should().Be(0);
        resultado.Valor.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
        alcance.Invalidaciones.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Si_la_empresa_de_un_trabajador_ya_admitido_sale_de_la_cartera_mientras_espera_el_candado_no_se_escribe_nada()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConCentroExistente();
        var empresaId = escenario.EmpresasContexto.ListaEmpresas.Single().Id;
        var alcance = new AlcanceMemoizadoRevocableFalso(new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            empresaIdsParaGestion: [empresaId], centroIdsParaGestion: [escenario.CentroExistente!.Id]));
        escenario.Serializacion.AlTomarElCandado = alcance.Revocar;
        var guardado = false;
        escenario.OperacionImportacionRepositorio.AlGuardar = () => guardado = true;

        var resultado = await escenario.Handler("CoordinadorCae", alcance, alcance)
            .Handle(new EjecutarImportacionCommand(PlanConTrabajadorYSuAsignacion()), default);

        escenario.Serializacion.Llamadas.Should().Be(1);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresCargaMasiva.AmbitoCambiadoAlConfirmar.Codigo);
        escenario.Serializacion.UltimoResultado!.EsFallido.Should().BeTrue("un fallo deshace la transacción del candado real");
        guardado.Should().BeFalse("la marca de la operación no se guarda: no se confirma nada");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_la_cartera_intacta_tras_el_candado_el_trabajador_y_su_asignacion_se_escriben()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConCentroExistente();
        var empresaId = escenario.EmpresasContexto.ListaEmpresas.Single().Id;
        var alcance = new AlcanceMemoizadoRevocableFalso(new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            empresaIdsParaGestion: [empresaId], centroIdsParaGestion: [escenario.CentroExistente!.Id]));

        var resultado = await escenario.Handler("CoordinadorCae", alcance, alcance)
            .Handle(new EjecutarImportacionCommand(PlanConTrabajadorYSuAsignacion()), default);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TrabajadoresCreados.Should().Be(1);
        resultado.Valor.AsignacionesCreadas.Should().Be(1);
        alcance.Invalidaciones.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bajo_el_candado_de_Empresa_propia_la_importacion_vuelve_a_decidir_la_cartera(bool revocar)
    {
        // Una Empresa nueva en el archivo hace que el guardado espere también el candado de Empresa propia (D-P11),
        // la última espera antes del COMMIT; sin Asignaciones, el de la regla A-1 no se toma.
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        var empresaId = escenario.EmpresasContexto.ListaEmpresas.Single().Id;
        var alcance = new AlcanceMemoizadoRevocableFalso(new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            empresaIdsParaGestion: [empresaId]));
        var candado = new CandadoConGancho();
        if (revocar) candado.AlTomarElCandado = alcance.Revocar;
        escenario.Limite = LimitesDePrueba.Sobre(escenario.EmpresasContexto, new AlertaOperativaFalsa(), candado: candado.Candado);
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto("Empresa Nueva S.L.", YaExiste: false)],
            trabajadores:
            [
                new TrabajadorImportadoDto(
                    EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández", EscenarioImportacion.DniDesconocido, null, null,
                    YaExiste: false)
            ]);

        var resultado = await escenario.Handler("CoordinadorCae", alcance, alcance)
            .Handle(new EjecutarImportacionCommand(plan), default);

        candado.Candado.Ejecuciones.Should().Be(1);
        if (revocar)
        {
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be(ErroresCargaMasiva.AmbitoCambiadoAlConfirmar.Codigo);
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.TrabajadoresCreados.Should().Be(1);
        }
    }

    private static PlanImportacionDto PlanConTrabajadorYSuAsignacion() => EscenarioImportacion.Plan(
        trabajadores:
        [
            new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaConocida, "Lucía", "Fernández", EscenarioImportacion.DniDesconocido, null, null,
                YaExiste: false)
        ],
        asignaciones:
            [new AsignacionImportadaDto(EscenarioImportacion.DniDesconocido, EscenarioImportacion.CentroConocido, YaExiste: false)]);

    // ---------------------------------------------------------------- EjecutarImportacionCombinadaCommand (candado de Empresa propia)

    [Fact]
    public async Task Carga_combinada_cuyo_Cliente_empresarial_sale_de_la_cartera_mientras_espera_el_candado_no_escribe_nada()
    {
        var (escenario, dentro, alcance, candado) = EscenarioCombinado();
        candado.AlTomarElCandado = alcance.Revocar;

        var resultado = await escenario.Handler(alcance, AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(PlanCombinado(), false), default);

        candado.Candado.Ejecuciones.Should().Be(1);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresCargaMasiva.AmbitoCambiadoAlConfirmar.Codigo);
        escenario.OperacionImportacionRepositorio.ExisteAsync(PlanCombinado().OperacionId).Result.Should().BeFalse();
        alcance.Invalidaciones.Should().BeGreaterThan(0);
        _ = dentro;
    }

    [Fact]
    public async Task Carga_combinada_con_la_cartera_intacta_tras_el_candado_crea_la_empresa()
    {
        var (escenario, _, alcance, candado) = EscenarioCombinado();

        var resultado = await escenario.Handler(alcance, AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(PlanCombinado(), false), default);

        candado.Candado.Ejecuciones.Should().Be(1);
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EmpresasCreadas.Should().Be(1);
    }

    private static readonly Guid OperacionCombinada = Guid.NewGuid();

    private static PlanImportacionCombinadaDto PlanCombinado() => EscenarioImportacionCombinada.Plan(
        operacionId: OperacionCombinada,
        empresas: [new EmpresaCombinadaImportadaDto("Empresa Nueva S.L.", ["Cliente Dentro S.A."], YaExiste: false, Fila: 2)]);

    private static (EscenarioImportacionCombinada Escenario, Empresa Dentro, AlcanceMemoizadoRevocableFalso Alcance, CandadoConGancho Candado)
        EscenarioCombinado()
    {
        var escenario = new EscenarioImportacionCombinada();
        var dentro = Empresa.CrearComoCliente("Cliente Dentro S.A.", "B12345674", false, notas: null, ejecutivoUsuarioId: null);
        escenario.EmpresaRepositorio.Empresas.Add(dentro);
        escenario.Relaciones.Permitir = true;
        var alcance = new AlcanceMemoizadoRevocableFalso(
            new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [dentro.Id], empresaIdsParaGestion: []));
        var candado = new CandadoConGancho();
        escenario.Limite = LimitesDePrueba.Sobre(
            new Documentos.EmpresasQueryContextFalso(), new AlertaOperativaFalsa(), candado: candado.Candado);
        return (escenario, dentro, alcance, candado);
    }

    /// <summary>El candado en memoria con un gancho que se puede fijar después de construirlo.</summary>
    private sealed class CandadoConGancho
    {
        public Action? AlTomarElCandado { get; set; }

        public LimitesDePrueba.CandadoEnMemoria Candado { get; }

        public CandadoConGancho() => Candado = new LimitesDePrueba.CandadoEnMemoria { AlTomarlo = () => AlTomarElCandado?.Invoke() };
    }
}
