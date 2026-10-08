using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.AnadirTrabajadorAVisita;
using CaeManager.Application.Visitas.Commands.QuitarTrabajadorDeVisita;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Añadir y quitar un Trabajador de una Visita desde el panel: la misma barrera que
/// <c>EditarVisitaCommand</c> (alcance de GESTIÓN, cancelada, concurrencia) y la regla de que una
/// Visita no se queda sin Trabajadores. Quitar se prueba entero con dobles en memoria; de Añadir
/// solo lo que se decide antes de consultar la base general del Tenant (el contexto de
/// Trabajadores va a null: si el handler pasara la barrera, el test reventaría en vez de dar un
/// falso verde). El alta real, con Trabajadores y RLS, se prueba en Integration
/// (<c>TrabajadoresDeVisitaDesdePanelBajoRlsTests</c>).
/// </summary>
public class TrabajadoresDeVisitaCommandHandlerTests
{
    private static readonly DateOnly Fecha = new(2026, 1, 1);

    private sealed class VisitaTrabajadorRepositorioFalso : IVisitaTrabajadorRepository
    {
        public List<VisitaTrabajador> Filas { get; } = [];

        public Task<IReadOnlyList<VisitaTrabajador>> ObtenerPorVisitaAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VisitaTrabajador>>(Filas.Where(f => f.VisitaId == visitaId).ToList());

        public void Agregar(VisitaTrabajador visitaTrabajador) => Filas.Add(visitaTrabajador);

        public void Eliminar(VisitaTrabajador visitaTrabajador) => Filas.Remove(visitaTrabajador);
    }

    private sealed class EvaluadorFalso : IEvaluadorExpedienteVisitaService
    {
        public List<Guid> Evaluadas { get; } = [];

        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
        {
            Evaluadas.Add(visitaId);
            return Task.FromResult(false);
        }

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Escenario
    {
        public Visita Visita { get; } = new(Guid.NewGuid(), Fecha, Fecha.AddDays(1), null);
        public VisitaRepositorioFalso Visitas { get; } = new();
        public VisitaTrabajadorRepositorioFalso Union { get; } = new();
        public EvaluadorFalso Evaluador { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public AlcanceDatosServiceFalso Alcance { get; set; } = new();

        public Escenario(params Guid[] trabajadorIds)
        {
            Visitas.Agregar(Visita);
            foreach (var id in trabajadorIds)
                Union.Agregar(new VisitaTrabajador(Visita.Id, id));
        }

        public Task<Domain.Common.Result> QuitarAsync(Guid trabajadorId, Guid version = default) =>
            new QuitarTrabajadorDeVisitaCommandHandler(
                    Visitas, Union, Evaluador, UnitOfWork, NullLogger<QuitarTrabajadorDeVisitaCommandHandler>.Instance, Alcance)
                .Handle(new QuitarTrabajadorDeVisitaCommand(Visita.Id, trabajadorId, version), CancellationToken.None);

        public Task<Domain.Common.Result> AnadirAsync(Guid trabajadorId, Guid version = default) =>
            new AnadirTrabajadorAVisitaCommandHandler(
                    Visitas, Union, null!, Evaluador, UnitOfWork, NullLogger<AnadirTrabajadorAVisitaCommandHandler>.Instance, Alcance)
                .Handle(new AnadirTrabajadorAVisitaCommand(Visita.Id, trabajadorId, version), CancellationToken.None);

        public static AlcanceDatosServiceFalso FueraDeAlcance() => new(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
    }

    [Fact]
    public async Task Quitar_saca_al_trabajador_guarda_y_reevalua_el_expediente()
    {
        var (sale, queda) = (Guid.NewGuid(), Guid.NewGuid());
        var escenario = new Escenario(sale, queda);

        var resultado = await escenario.QuitarAsync(sale);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Union.Filas.Select(f => f.TrabajadorId).Should().BeEquivalentTo([queda]);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
        escenario.Evaluador.Evaluadas.Should().BeEquivalentTo([escenario.Visita.Id]);
    }

    [Fact]
    public async Task Quitar_al_ultimo_trabajador_se_rechaza_y_no_guarda()
    {
        var unico = Guid.NewGuid();
        var escenario = new Escenario(unico);

        var resultado = await escenario.QuitarAsync(unico);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(QuitarTrabajadorDeVisitaCommandHandler.CodigoUltimoTrabajador,
            "es la misma regla que el validador de EditarVisitaCommand: una visita incluye al menos un trabajador");
        escenario.Union.Filas.Should().HaveCount(1);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Quitar_a_quien_ya_no_entra_es_exito_sin_guardar()
    {
        var escenario = new Escenario(Guid.NewGuid(), Guid.NewGuid());

        var resultado = await escenario.QuitarAsync(Guid.NewGuid());

        resultado.EsExitoso.Should().BeTrue();
        escenario.Union.Filas.Should().HaveCount(2);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Anadir_a_quien_ya_entra_es_exito_sin_duplicar_ni_guardar()
    {
        var yaEntra = Guid.NewGuid();
        var escenario = new Escenario(yaEntra);

        var resultado = await escenario.AnadirAsync(yaEntra);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Union.Filas.Should().HaveCount(1);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fuera_del_alcance_de_gestion_responde_no_encontrada_y_no_toca_nada(bool anadir)
    {
        var (uno, dos) = (Guid.NewGuid(), Guid.NewGuid());
        var escenario = new Escenario(uno, dos) { Alcance = Escenario.FueraDeAlcance() };

        // Con versión desfasada: el alcance va antes que la concurrencia, para no confirmar que existe.
        var resultado = anadir
            ? await escenario.AnadirAsync(Guid.NewGuid(), version: Guid.NewGuid())
            : await escenario.QuitarAsync(uno, version: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        escenario.Union.Filas.Should().HaveCount(2);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Con_alcance_de_lectura_sin_alcance_de_gestion_no_se_cambia(bool anadir)
    {
        var (uno, dos) = (Guid.NewGuid(), Guid.NewGuid());
        var escenario = new Escenario(uno, dos);
        escenario.Alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.Visita.CentroId], centroIdsParaGestion: []);

        var resultado = anadir ? await escenario.AnadirAsync(Guid.NewGuid()) : await escenario.QuitarAsync(uno);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        escenario.Union.Filas.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_visita_cancelada_no_cambia_de_trabajadores(bool anadir)
    {
        var (uno, dos) = (Guid.NewGuid(), Guid.NewGuid());
        var escenario = new Escenario(uno, dos);
        escenario.Visita.Cancelar(DateTime.UtcNow, null);

        var resultado = anadir ? await escenario.AnadirAsync(Guid.NewGuid()) : await escenario.QuitarAsync(uno);

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        escenario.Union.Filas.Should().HaveCount(2);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_version_desfasada_se_rechaza_por_concurrencia(bool anadir)
    {
        var (uno, dos) = (Guid.NewGuid(), Guid.NewGuid());
        var escenario = new Escenario(uno, dos);

        var resultado = anadir
            ? await escenario.AnadirAsync(Guid.NewGuid(), version: Guid.NewGuid())
            : await escenario.QuitarAsync(uno, version: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be(CaeManager.Application.Common.ConcurrenciaOptimista.CodigoConflicto);
        escenario.Union.Filas.Should().HaveCount(2);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }
}
