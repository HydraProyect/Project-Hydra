using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

public class CerrarSesionPrivilegiadaCommandTests
{
    [Theory]
    [InlineData(true, 1, false, true)]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 1, true, false)]
    public async Task El_cierre_exige_el_actor_de_la_concesion_y_una_fila_persistida(
        bool esSuya, int filas, bool concurrencia, bool exito)
    {
        var usuario = Guid.NewGuid();
        var contexto = new Contexto(usuario);
        var unidad = new Unidad(filas, concurrencia);
        var handler = new CerrarSesionPrivilegiadaCommandHandler(contexto, unidad,
            new CurrentUserServiceFalso(esSuya ? usuario : Guid.NewGuid()));

        var resultado = await handler.Handle(new(contexto.Sesion.Id), default);

        resultado.EsExitoso.Should().Be(exito);
        if (!exito)
            resultado.Error.Codigo.Should().Be("SesionPrivilegiada.CierreNoPermitido");
        unidad.Llamadas.Should().Be(esSuya ? 1 : 0);
        contexto.Sesion.EstaAbierta.Should().Be(!esSuya);
    }

    private sealed class Unidad(int filas, bool concurrencia) : IUnitOfWork
    {
        public int Llamadas { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Llamadas++;
            if (concurrencia)
                throw new DbUpdateConcurrencyException("Cero filas actualizadas bajo RLS.");
            return Task.FromResult(filas);
        }
    }

    private sealed class Contexto : IPlataformaQueryContext
    {
        private readonly ConcesionPrivilegio _concesion;
        public SesionPrivilegiada Sesion { get; }

        public Contexto(Guid usuario)
        {
            var ahora = DateTime.UtcNow;
            _concesion = ConcesionPrivilegio.SoporteLecturaGlobal(usuario, ahora.AddHours(-1), ahora.AddHours(2));
            Sesion = SesionPrivilegiada.Abrir(_concesion, Guid.NewGuid(), "Incidencia", ahora, TimeSpan.FromHours(1));
        }

        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio => Consulta<ConcesionPrivilegio>([_concesion]);
        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas => Consulta<SesionPrivilegiada>([Sesion]);
        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma => Consulta<EstadoBootstrapPlataforma>([]);
        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion => Consulta<TenantAlcanzadoPorConcesion>([]);
        private static IQueryable<T> Consulta<T>(T[] elementos) => new TestAsyncQueryable<T>(elementos.AsQueryable());
    }
}
