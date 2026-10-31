using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma.ConcederPrivilegio;

public class AutoConcederPrivilegioCommandHandlerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    public async Task La_vigencia_de_SoporteLectura_depende_de_los_dias_solicitados(int dias)
    {
        var concesion = await ConcederAsync(CapacidadPrivilegio.SoporteLectura, dias);

        concesion.VigenciaHasta.Should().Be(concesion.VigenciaDesde.AddDays(dias));
    }

    /// <summary>
    /// La concesión raíz de AdminPlataforma nace sin fecha de fin y no usa
    /// <c>DiasDeVigencia</c> (decisión de #1870: el validador no lo exige y la
    /// documentación del parámetro dice que se ignora). Si alguien la hiciera
    /// depender de los días, este caso falla.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    public async Task La_concesion_raiz_de_AdminPlataforma_no_tiene_fecha_de_fin_sean_cuales_sean_los_dias(int dias)
    {
        var concesion = await ConcederAsync(CapacidadPrivilegio.AdminPlataforma, dias);

        concesion.VigenciaHasta.Should().BeNull();
    }

    private static async Task<ConcesionPrivilegio> ConcederAsync(CapacidadPrivilegio capacidad, int dias)
    {
        var usuario = Guid.NewGuid();
        var writer = new PlataformaWriterFalso();
        var handler = new AutoConcederPrivilegioCommandHandler(
            writer, new AutorizacionFalsa(), new Contexto(usuario),
            new CurrentUserServiceFalso(usuarioId: usuario), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new AutoConcederPrivilegioCommand(capacidad, dias), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        return writer.ConcesionAnadida!;
    }
    private sealed class AutorizacionFalsa : IAutorizacionAutoConcesion
    {
        public Task<bool> PuedeAutoConcederseAsync(Guid usuarioId, CapacidadPrivilegio capacidad,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class Contexto(Guid usuario) : IPlataformaQueryContext
    {
        public IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma =>
            new TestAsyncQueryable<EstadoBootstrapPlataforma>(
                new[] { Domain.Plataforma.EstadoBootstrapPlataforma.Designar(usuario, DateTime.UtcNow) }.AsQueryable());
        public IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio => throw new NotSupportedException();
        public IQueryable<SesionPrivilegiada> SesionesPrivilegiadas => throw new NotSupportedException();
        public IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion => throw new NotSupportedException();
    }
}
