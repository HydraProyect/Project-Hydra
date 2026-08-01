using CaeManager.Domain.DocumentosIa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// La cola durable que sustituye a la cola en memoria sobre
/// <c>Channel&lt;T&gt;</c> (P2 #22 de docs/business/MATURITY_REVIEW.md) —
/// contra PostgreSQL real, porque lo que importa comprobar es justo lo que
/// una cola en memoria no podía dar: que el trabajo sigue ahí después de
/// cerrar y volver a abrir el contexto (equivalente a un reinicio del
/// proceso), no solo mientras el objeto en memoria vive.
/// </summary>
public class TrabajoAnalisisDocumentoRepositoryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Un_trabajo_agregado_sobrevive_a_cerrar_el_contexto_y_se_puede_reclamar_despues()
    {
        Guid documentoId = Guid.NewGuid(), usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(_tenantA))
        {
            var repositorio = new TrabajoAnalisisDocumentoRepository(contexto);
            repositorio.Agregar(new TrabajoAnalisisDocumento(documentoId, usuarioId, TipoAnalisisDocumento.DeteccionTrabajadores));
            await contexto.SaveChangesAsync();
        }

        // Contexto nuevo, como haría un procesador que arranca de cero tras
        // un redeploy — si el trabajo solo hubiera vivido en memoria, aquí no
        // habría nada que reclamar.
        await using var otroContexto = CrearContexto(_tenantA);
        var otroRepositorio = new TrabajoAnalisisDocumentoRepository(otroContexto);

        var reclamado = await otroRepositorio.ObtenerSiguientePendienteAsync();

        reclamado.Should().NotBeNull();
        reclamado!.DocumentoId.Should().Be(documentoId);
        reclamado.UsuarioSolicitanteId.Should().Be(usuarioId);
        reclamado.Tipo.Should().Be(TipoAnalisisDocumento.DeteccionTrabajadores);
        reclamado.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Pendiente);
    }

    [Fact]
    public async Task ObtenerSiguientePendienteAsync_no_devuelve_trabajos_ya_completados()
    {
        await using var contexto = CrearContexto(_tenantA);
        var repositorio = new TrabajoAnalisisDocumentoRepository(contexto);

        var trabajo = new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa);
        trabajo.MarcarEnProceso();
        trabajo.MarcarCompletado();
        repositorio.Agregar(trabajo);
        await contexto.SaveChangesAsync();

        var siguiente = await repositorio.ObtenerSiguientePendienteAsync();

        siguiente.Should().BeNull();
    }

    [Fact]
    public async Task ObtenerSiguientePendienteAsync_no_cruza_tenants()
    {
        await using (var contextoB = CrearContexto(_tenantB))
        {
            var repositorioB = new TrabajoAnalisisDocumentoRepository(contextoB);
            repositorioB.Agregar(new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa));
            await contextoB.SaveChangesAsync();
        }

        await using var contextoA = CrearContexto(_tenantA);
        var repositorioA = new TrabajoAnalisisDocumentoRepository(contextoA);

        var siguienteParaA = await repositorioA.ObtenerSiguientePendienteAsync();

        siguienteParaA.Should().BeNull("el único trabajo pendiente pertenece al tenant B");
    }

    [Fact]
    public async Task ObtenerEstancadosAsync_devuelve_los_que_llevan_procesando_mas_del_umbral()
    {
        await using var contexto = CrearContexto(_tenantA);
        var repositorio = new TrabajoAnalisisDocumentoRepository(contexto);

        var estancado = new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa);
        estancado.MarcarEnProceso();
        repositorio.Agregar(estancado);

        var pendiente = new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa);
        repositorio.Agregar(pendiente);

        var completado = new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa);
        completado.MarcarEnProceso();
        completado.MarcarCompletado();
        repositorio.Agregar(completado);

        await contexto.SaveChangesAsync();

        // Umbral cero: cualquier "Procesando" con IniciadoEnUtc ya en el
        // pasado (aunque sea por milisegundos, que es lo que tarda este
        // propio SaveChangesAsync) cuenta como estancado — evita depender de
        // esperas reales en el test.
        var estancados = await repositorio.ObtenerEstancadosAsync(TimeSpan.Zero);

        estancados.Should().ContainSingle().Which.Id.Should().Be(estancado.Id);
    }
}
