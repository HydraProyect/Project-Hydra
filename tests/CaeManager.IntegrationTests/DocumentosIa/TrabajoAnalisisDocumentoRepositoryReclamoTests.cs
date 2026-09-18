using CaeManager.Application.Common;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
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
///
/// <para>
/// <b>Autentica como <c>cae_app_runtime</c>, no como el propietario</b>
/// (2026-09-18, hallazgo del diseño de REC-022/outbox). Antes,
/// <see cref="CrearContexto"/> conectaba con la cadena del propietario y solo
/// registraba <see cref="TenantSelladoInterceptor"/> — nunca
/// <see cref="TenantRlsConnectionInterceptor"/>. RLS no restringe al
/// propietario de la tabla ni con <c>FORCE ROW LEVEL SECURITY</c>
/// (<c>TenantRlsConnectionInterceptor</c>, comentario de clase), así que
/// <c>app.tenant_id</c> nunca llegaba a fijarse y el aislamiento de tenant que
/// este archivo creía demostrar era, en realidad, el del filtro global de EF
/// (<c>HasQueryFilter</c>), no el de la política de PostgreSQL. Migrar
/// (DDL) sigue exigiendo el propietario — <see cref="CrearContextoPropietario"/>
/// lo aísla para eso — pero el reclamo bajo prueba corre bajo la identidad
/// real de tráfico, con los dos interceptores de producción.
/// </para>
/// </summary>
public class TrabajoAnalisisDocumentoRepositoryReclamoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContextoPropietario(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Solo para DDL (migrar). Las migraciones necesitan privilegios que
    /// <c>cae_app_runtime</c> no tiene, igual que en producción
    /// (<c>FabricaContextoDeBootstrap</c>) — nunca se usa para ejercitar el
    /// reclamo bajo prueba.
    /// </summary>
    private CaeManagerDbContext CrearContextoPropietario(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            // EnableRetryOnFailure como en ConfiguracionDeContexto (producción)
            // a propósito: sin esto, este test no habría detectado que
            // BeginTransactionAsync sin pasar por CreateExecutionStrategy()
            // revienta bajo NpgsqlRetryingExecutionStrategy — auditoría de
            // colas, 2026-08-30, hallazgo E2E (el reclamo fallaba en cada
            // sondeo de la app real mientras este mismo test, sin reintentos
            // activados, pasaba en verde).
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                npgsql.EnableRetryOnFailure();
            })
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                // El interceptor que faltaba: sin él, app.tenant_id nunca se
                // fija y RLS nunca actúa — ver el doc-comment de la clase.
                // Sin sesión privilegiada ni usuario (proceso de fondo), igual
                // que ProcesadorAnalisisDocumentoHostedService en producción.
                new TenantRlsConnectionInterceptor(
                    tenantActual, new ClienteActivoSeleccionadoAusente(), new CurrentUserServiceFalso()))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoAusente : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
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

    /// <summary>
    /// Aísla RLS del filtro global de EF (revisión de Codex, 2026-09-18, § 10 de
    /// la PR): el test de arriba conecta como <c>cae_app_runtime</c>, pero
    /// consulta a través de <c>TrabajosAnalisisDocumento</c>, que conserva
    /// <c>HasQueryFilter</c>. Con las dos capas activas, un tenant B que no ve
    /// nada podría deberse solo al filtro de EF y decir cero sobre si
    /// PostgreSQL está aplicando la política — exactamente la confusión de
    /// capas que este archivo tenía antes del fix (ver el doc-comment de la
    /// clase). <c>IgnoreQueryFilters()</c> quita la capa de EF; lo único que
    /// puede seguir ocultando la fila del tenant A es RLS.
    /// </summary>
    [Fact]
    public async Task ReclamarSiguientePendienteAsync_bajo_IgnoreQueryFilters_RLS_sigue_ocultando_el_tenant_ajeno()
    {
        await using (var contexto = CrearContexto(_tenantA))
        {
            var repositorio = new TrabajoAnalisisDocumentoRepository(contexto);
            repositorio.Agregar(new TrabajoAnalisisDocumento(Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa));
            await contexto.SaveChangesAsync();
        }

        await using var contextoB = CrearContexto(_tenantB);
        var vistosSinFiltroDeEf = await contextoB.TrabajosAnalisisDocumento
            .IgnoreQueryFilters()
            .Where(t => t.Estado == EstadoTrabajoAnalisisDocumento.Pendiente)
            .ToListAsync();

        vistosSinFiltroDeEf.Should().BeEmpty(
            "sin el filtro de EF, lo único que puede seguir ocultando la fila del tenant A es RLS: " +
            "si esta aserción fallara con la anterior en verde, el aislamiento real sería el de EF, no el de PostgreSQL");
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
        // Sin EnableRetryOnFailure a propósito, a diferencia de CrearContexto:
        // esta conexión solo sostiene el lock desde fuera para la simulación,
        // no ejercita el código de producción bajo prueba (ese es contexto2,
        // más abajo) — abrir la transacción aquí a mano con reintentos
        // activos tropezaría con la misma restricción de
        // NpgsqlRetryingExecutionStrategy que el fix de abajo existe para evitar.
        var tenantActual1 = new TenantActualAmbiental { TenantId = _tenantA };
        await using var contexto1 = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .AddInterceptors(new TenantSelladoInterceptor(tenantActual1))
                .Options,
            new EphemeralDataProtectionProvider(), tenantActual1);
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
