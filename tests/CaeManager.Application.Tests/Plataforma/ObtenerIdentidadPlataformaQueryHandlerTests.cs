using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Queries.ObtenerIdentidadPlataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma;

public class ObtenerIdentidadPlataformaQueryHandlerTests
{
    [Fact]
    public async Task Una_sesion_privilegiada_conserva_el_actor_real_y_muestra_por_separado_el_simulado()
    {
        var actorReal = Guid.NewGuid();
        var usuarioSimulado = Guid.NewGuid();
        var sesionId = Guid.NewGuid();
        var handler = new ObtenerIdentidadPlataformaQueryHandler(
            new ActorAuditoriaFalso(new ActorAuditoria(actorReal, null, TipoViaAcceso.Normal, null)),
            new SesionPrivilegiadaActualFalsa(new SesionPrivilegiadaActiva(
                sesionId, Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.Impersonacion, usuarioSimulado)));

        var resultado = await handler.Handle(new ObtenerIdentidadPlataformaQuery(), CancellationToken.None);

        resultado.ActorRealUsuarioId.Should().Be(actorReal);
        resultado.UsuarioSimuladoId.Should().Be(usuarioSimulado);
        resultado.UsuarioSimuladoId.Should().NotBe(actorReal);
        resultado.ViaAcceso.Should().Be(TipoViaAcceso.SesionPrivilegiada);
        resultado.ViaAccesoId.Should().Be(sesionId);
        resultado.CapacidadActiva.Should().Be(CapacidadPrivilegio.Impersonacion);
    }

    [Fact]
    public async Task Una_via_delegada_sin_sesion_no_se_convierte_en_capacidad()
    {
        var actorReal = Guid.NewGuid();
        var asignacionId = Guid.NewGuid();
        var handler = new ObtenerIdentidadPlataformaQueryHandler(
            new ActorAuditoriaFalso(new ActorAuditoria(
                actorReal, null, TipoViaAcceso.OperacionDelegada, asignacionId)),
            new SesionPrivilegiadaActualFalsa(null));

        var resultado = await handler.Handle(new ObtenerIdentidadPlataformaQuery(), CancellationToken.None);

        resultado.ActorRealUsuarioId.Should().Be(actorReal);
        resultado.UsuarioSimuladoId.Should().BeNull();
        resultado.ViaAcceso.Should().Be(TipoViaAcceso.OperacionDelegada);
        resultado.ViaAccesoId.Should().Be(asignacionId);
        resultado.CapacidadActiva.Should().BeNull();
    }

    [Fact]
    public async Task La_identidad_de_lectura_usa_la_ultima_sesion_resuelta_y_no_revalida()
    {
        var sesionResuelta = new SesionPrivilegiadaActiva(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.Impersonacion, Guid.NewGuid());
        var sesion = new SesionPrivilegiadaActualFalsa(sesionResuelta, null);
        var handler = new ObtenerIdentidadPlataformaQueryHandler(
            new ActorAuditoriaFalso(new ActorAuditoria(Guid.NewGuid(), null, TipoViaAcceso.Normal, null)), sesion);

        var resultado = await handler.Handle(new ObtenerIdentidadPlataformaQuery(), CancellationToken.None);

        resultado.ViaAcceso.Should().Be(TipoViaAcceso.SesionPrivilegiada);
        resultado.ViaAccesoId.Should().Be(sesionResuelta.SesionId);
        resultado.CapacidadActiva.Should().Be(CapacidadPrivilegio.Impersonacion);
        sesion.Lecturas.Should().Be(1);
        sesion.Revalidaciones.Should().Be(0);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SesionPrivilegiadaActualFalsa(
        SesionPrivilegiadaActiva? sesionObtenida,
        SesionPrivilegiadaActiva? sesionRevalidada = null) : ISesionPrivilegiadaActual
    {
        public int Lecturas { get; private set; }
        public int Revalidaciones { get; private set; }

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(sesionObtenida);
        }

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default)
        {
            Revalidaciones++;
            return Task.FromResult(sesionRevalidada);
        }
    }
}
