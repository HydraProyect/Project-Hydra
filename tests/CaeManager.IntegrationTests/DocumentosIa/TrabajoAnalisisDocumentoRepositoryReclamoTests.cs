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
/// Prueba de sensibilidad de la auditoría de colas (2026-08-30, hallazgo
/// crítico #1): antes de <c>ReclamarSiguientePendienteAsync</c>, la
/// reclamación era un SELECT sin bloqueo de fila seguido de un UPDATE por
/// separado — dos conexiones podían leer el mismo <see cref="TrabajoAnalisisDocumento"/>
/// "Pendiente" a la vez si el advisory lock de elección de líder fallaba
/// (p. ej. su conexión se cae a mitad de un lote). Esta clase prueba
/// justo la propiedad que <c>FOR UPDATE SKIP LOCKED</c> garantiza: una
/// segunda conexión que intenta reclamar mientras la primera tiene la fila
/// bloqueada (transacción sin confirmar) no puede tomar el mismo trabajo —
/// tiene que esperar a que se libere.
/// </summary>
public class TrabajoAnalisisDocumentoRepositoryReclamoTests : IAsyncLifetime
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
    public async Task ReclamarSiguientePendienteAsync_lo_marca_procesando_y_respeta_el_ambito_de_tenant()
    {
        Guid documentoId = Guid.NewGuid();

        await using (var contexto = CrearContexto(_tenantA))
        {
            var repositorio = new TrabajoAnalisisDocumentoRepository(contexto);
            repositorio.Agregar(new TrabajoAnalisisDocumento(documentoId, null, TipoAnalisisDocumento.VerificacionIa));
            await contexto.SaveChangesAsync();
        }

        await using var contextoB = CrearContexto(_tenantB);
        (await new TrabajoAnalisisDocumentoRepository(contextoB).ReclamarSiguientePendienteAsync())
            .Should().BeNull("el único trabajo pendiente pertenece al tenant A");

        await using var contextoA = CrearContexto(_tenantA);
        var reclamado = await new TrabajoAnalisisDocumentoRepository(contextoA).ReclamarSiguientePendienteAsync();

        reclamado.Should().NotBeNull();
        reclamado!.DocumentoId.Should().Be(documentoId);
        reclamado.Estado.Should().Be(EstadoTrabajoAnalisisDocumento.Procesando);
    }

    [Fact]
    public async Task ReclamarSiguientePendienteAsync_no_reclama_uno_todavia_en_backoff()
    {
        await using (var contexto = CrearContexto(_tenantA))
        {
            var trabajo = new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa);
            trabajo.RegistrarFallo("fallo transitorio simulado");
            new TrabajoAnalisisDocumentoRepository(contexto).Agregar(trabajo);
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto(_tenantA);
        var reclamado = await new TrabajoAnalisisDocumentoRepository(contexto2).ReclamarSiguientePendienteAsync();

        reclamado.Should().BeNull("SiguienteIntentoEnUtc todavía está en el futuro tras el backoff");
    }

    [Fact]
    public async Task ReclamarSiguientePendienteAsync_salta_una_fila_bloqueada_por_otra_transaccion_en_vez_de_esperar()
    {
        Guid documentoId = Guid.NewGuid();
        Guid trabajoId;
        await using (var contexto = CrearContexto(_tenantA))
        {
            var trabajo = new TrabajoAnalisisDocumento(documentoId, null, TipoAnalisisDocumento.VerificacionIa);
            new TrabajoAnalisisDocumentoRepository(contexto).Agregar(trabajo);
            await contexto.SaveChangesAsync();
            trabajoId = trabajo.Id;
        }

        // Conexión 1: simula el reclamo de una réplica que sigue "en curso" —
        // abre una transacción, bloquea la fila con FOR UPDATE, y NO confirma
        // todavía. Esto reproduce el estado en el que quedaría la fila si
        // ReclamarSiguientePendienteAsync estuviera a mitad de ejecutarse.
        await using var contexto1 = CrearContexto(_tenantA);
        await using var tx1 = await contexto1.Database.BeginTransactionAsync();
        await contexto1.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT * FROM "TrabajosAnalisisDocumento" WHERE "Id" = {trabajoId} FOR UPDATE""");

        // Conexión 2 (físicamente distinta — otro DbContext, otro socket):
        // SKIP LOCKED debe saltar la fila bloqueada por la conexión 1 y no
        // encontrar nada más que reclamar, en vez de bloquearse esperando el
        // lock (que es justo el comportamiento de FOR UPDATE sin SKIP LOCKED).
        await using var contexto2 = CrearContexto(_tenantA);
        var repositorio2 = new TrabajoAnalisisDocumentoRepository(contexto2);

        using var limiteEspera = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reclamadoMientrasBloqueado = await repositorio2.ReclamarSiguientePendienteAsync(limiteEspera.Token);

        reclamadoMientrasBloqueado.Should().BeNull(
            "SKIP LOCKED debe saltar la fila que la conexión 1 tiene bloqueada, no esperarla ni reclamarla dos veces");

        // Suelta el bloqueo — ahora sí debe poder reclamarse.
        await tx1.RollbackAsync();

        await using var contexto3 = CrearContexto(_tenantA);
        var reclamadoTrasLiberar = await new TrabajoAnalisisDocumentoRepository(contexto3).ReclamarSiguientePendienteAsync();

        reclamadoTrasLiberar.Should().NotBeNull("liberado el lock, el trabajo vuelve a ser reclamable");
        reclamadoTrasLiberar!.DocumentoId.Should().Be(documentoId);
    }
}
