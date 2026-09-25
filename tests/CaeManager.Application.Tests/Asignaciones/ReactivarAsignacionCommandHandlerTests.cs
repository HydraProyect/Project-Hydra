using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Asignaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Asignaciones;

/// <summary>
/// FS-13: «Deshacer» de la baja de una asignación. Reabrir es volver a dar de
/// alta, así que exige la autoridad del alta (trabajador y centro) y respeta
/// DEC-19 sin tropezar con su propia fila.
/// </summary>
public class ReactivarAsignacionCommandHandlerTests
{
    private static readonly DateOnly Alta = new(2026, 7, 2);
    private static readonly DateOnly Baja = new(2026, 9, 25);

    private readonly Guid _trabajadorId = Guid.NewGuid();
    private readonly Guid _centroId = Guid.NewGuid();
    private readonly AsignacionRepositorioFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private Asignacion Cerrada(DateOnly alta, DateOnly baja)
    {
        var asignacion = new Asignacion(_trabajadorId, _centroId, alta);
        asignacion.DarDeBaja(baja);
        _repositorio.Asignaciones.Add(asignacion);
        return asignacion;
    }

    private Task<Domain.Common.Result> Reactivar(Guid id, AutoridadFalsa? autoridad = null) =>
        new ReactivarAsignacionCommandHandler(_repositorio, autoridad ?? new AutoridadFalsa(), _unitOfWork)
            .Handle(new ReactivarAsignacionCommand(id), CancellationToken.None);

    [Fact]
    public async Task Reabre_la_asignacion_cerrada_sin_tropezar_con_su_propia_fila()
    {
        var asignacion = Cerrada(Alta, Baja);

        var resultado = await Reactivar(asignacion.Id);

        resultado.EsExitoso.Should().BeTrue(
            "su propio rango cerrado cae dentro del que se comprueba y no puede contar como solape");
        asignacion.FechaBaja.Should().BeNull();
        asignacion.FechaAlta.Should().Be(Alta, "deshacer conserva la fecha de alta de siempre");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_autoridad_sobre_el_centro_no_la_reabre_y_no_confirma_que_exista()
    {
        var asignacion = Cerrada(Alta, Baja);

        var resultado = await Reactivar(asignacion.Id, new AutoridadFalsa { SobreCentro = false });

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");
        asignacion.FechaBaja.Should().Be(Baja);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_autoridad_sobre_el_trabajador_no_la_reabre()
    {
        var asignacion = Cerrada(Alta, Baja);

        var resultado = await Reactivar(asignacion.Id, new AutoridadFalsa { SobreTrabajador = false });

        resultado.EsFallido.Should().BeTrue(
            "reabrir es dar de alta: sin esto volvería a la cartera un trabajador que ya no está en ella");
        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");
        asignacion.FechaBaja.Should().Be(Baja);
    }

    [Fact]
    public async Task Una_asignacion_ya_activa_no_se_reabre()
    {
        var asignacion = new Asignacion(_trabajadorId, _centroId, Alta);
        _repositorio.Asignaciones.Add(asignacion);

        var resultado = await Reactivar(asignacion.Id);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.YaActiva");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_ya_hay_otra_alta_activa_en_el_mismo_centro_no_se_reabre()
    {
        var asignacion = Cerrada(Alta, Baja);
        _repositorio.Asignaciones.Add(new Asignacion(_trabajadorId, _centroId, Baja.AddDays(1)));

        var resultado = await Reactivar(asignacion.Id);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.YaActiva");
        asignacion.FechaBaja.Should().Be(Baja);
    }

    [Fact]
    public async Task Si_otra_asignacion_cerrada_posterior_pisa_el_rango_reabierto_no_se_reabre()
    {
        var asignacion = Cerrada(Alta, Baja);
        Cerrada(Baja.AddDays(10), Baja.AddDays(20));

        var resultado = await Reactivar(asignacion.Id);

        resultado.EsFallido.Should().BeTrue("reabierta ocuparía [alta, ∞) y pisaría la posterior (DEC-19)");
        resultado.Error.Codigo.Should().Be("Asignacion.SolapaConOtra");
        asignacion.FechaBaja.Should().Be(Baja);
    }

    [Fact]
    public async Task Una_asignacion_que_no_existe_da_no_encontrada()
    {
        var resultado = await Reactivar(Guid.NewGuid());

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");
    }

    private sealed class AutoridadFalsa : IAutoridadAsignacionesService
    {
        public bool SobreCentro { get; init; } = true;
        public bool SobreTrabajador { get; init; } = true;

        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreCentro);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreCentro ? centroIds : (IReadOnlyList<Guid>)[]);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreTrabajador);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
            IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreTrabajador ? trabajadorIds : (IReadOnlyList<Guid>)[]);
    }
}
