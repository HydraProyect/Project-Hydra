using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Asignaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Asignaciones;

/// <summary>
/// Dos Gestores CAE sobre la misma cartera dan de baja la misma asignación: la segunda baja no
/// puede re-fechar en silencio la primera (Asignacion no lleva token de versión, así que se
/// detecta por su estado).
/// </summary>
public class DarDeBajaAsignacionCommandHandlerTests
{
    private static readonly DateOnly Alta = new(2026, 7, 2);
    private static readonly DateOnly PrimeraBaja = new(2026, 9, 25);
    private static readonly DateOnly SegundaBaja = new(2026, 10, 30);

    private readonly AsignacionRepositorioFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private Asignacion Nueva(bool deBaja)
    {
        var asignacion = new Asignacion(Guid.NewGuid(), Guid.NewGuid(), Alta);
        if (deBaja)
            asignacion.DarDeBaja(PrimeraBaja);
        _repositorio.Asignaciones.Add(asignacion);
        return asignacion;
    }

    [Fact]
    public async Task Da_de_baja_una_asignacion_activa()
    {
        var asignacion = Nueva(deBaja: false);

        var resultado = await new DarDeBajaAsignacionCommandHandler(_repositorio, new AutoridadTotal(), _unitOfWork)
            .Handle(new DarDeBajaAsignacionCommand(asignacion.Id, PrimeraBaja), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacion.FechaBaja.Should().Be(PrimeraBaja);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_re_fecha_una_baja_ya_dada_por_otra_persona()
    {
        var asignacion = Nueva(deBaja: true);

        var resultado = await new DarDeBajaAsignacionCommandHandler(_repositorio, new AutoridadTotal(), _unitOfWork)
            .Handle(new DarDeBajaAsignacionCommand(asignacion.Id, SegundaBaja), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Asignacion.YaDeBaja");
        asignacion.FechaBaja.Should().Be(PrimeraBaja);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_lote_da_de_baja_las_activas_y_senala_la_que_otra_persona_ya_cerro()
    {
        var activa = Nueva(deBaja: false);
        var yaCerrada = Nueva(deBaja: true);

        var resultado = await new DarDeBajaAsignacionesCommandHandler(_repositorio, new AutoridadTotal(), _unitOfWork)
            .Handle(new DarDeBajaAsignacionesCommand([activa.Id, yaCerrada.Id], SegundaBaja), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DadasDeBaja.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle().Which.Should().Contain("ya estaba dada de baja");
        activa.FechaBaja.Should().Be(SegundaBaja);
        yaCerrada.FechaBaja.Should().Be(PrimeraBaja, "la baja de la otra persona no se pisa");
    }

    private sealed class AutoridadTotal : IAutoridadAsignacionesService
    {
        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(centroIds);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
            IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(trabajadorIds);
    }
}
