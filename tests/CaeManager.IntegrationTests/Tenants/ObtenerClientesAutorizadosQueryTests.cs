using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Regresión encontrada en CI (auditoría de af823ba/840eff7/1f53b53,
/// confirmada por FlujoSoporteTests E2E): un operador con dos
/// <c>AsignacionOperadorDelegado</c> activas hacia el mismo Cliente
/// Delegante — una Comercial (la de la siembra de demo) y una de Soporte
/// (al abrir un acceso) — obtenía dos filas idénticas del mismo tenant. El
/// join original no deduplicaba, así que <c>SelectorClienteActivo.razor</c>
/// pintaba dos &lt;option&gt; iguales y Playwright reventaba con "strict
/// mode violation: resolved to 2 elements" al intentar seleccionar una.
/// </summary>
public class ObtenerClientesAutorizadosQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _consultora;
    private Guid _clienteDelegante;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();

        var consultora = new Tenant("Consultora demo");
        var clienteDelegante = new Tenant("Cliente Delegante demo");
        contexto.Tenants.Add(consultora);
        contexto.Tenants.Add(clienteDelegante);

        _consultora = consultora.Id;
        _clienteDelegante = clienteDelegante.Id;

        // Dos delegaciones distintas hacia el mismo Cliente Delegante, las
        // dos activas, las dos con el mismo operador asignado — exactamente
        // lo que deja la siembra de demo (Comercial) más abrir un acceso de
        // soporte (Soporte) sobre el mismo tenant y el mismo Administrador.
        var comercial = new DelegacionTenant(_consultora, _clienteDelegante);
        var soporte = DelegacionTenant.ParaSoporte(_consultora, _clienteDelegante);
        soporte.ActivarParaSoporte("Incidencia de prueba", DateTime.UtcNow.AddDays(1), DateTime.UtcNow);

        contexto.DelegacionesTenant.Add(comercial);
        contexto.DelegacionesTenant.Add(soporte);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(comercial.Id, _usuario, "Administrador"));
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(soporte.Id, _usuario, "Administrador"));

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_delegaciones_activas_hacia_el_mismo_cliente_no_duplican_la_entrada()
    {
        await using var contexto = CrearContexto(_consultora);
        var handler = new ObtenerClientesAutorizadosQueryHandler(
            contexto, contexto, new CurrentUserServiceFalso(_usuario, _consultora));

        var resultado = await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);

        resultado.Should().HaveCount(2, "origen + un único Cliente Delegante, aunque haya dos delegaciones activas hacia él");
        resultado.Should().ContainSingle(c => c.TenantId == _clienteDelegante && !c.EsOrigen);
        resultado.Should().ContainSingle(c => c.TenantId == _consultora && c.EsOrigen);
    }

    /// <summary>
    /// Una visita pedía la lista cinco veces en el mismo ámbito (NavMenu y SelectorClienteActivo en el
    /// prerenderizado y en el circuito, más TenantBeneficiarioPorDefectoMiddleware). Con la memoización Scoped, las
    /// cinco leen la base lo mismo que una sola; otro Tenant efectivo o un Command lee de nuevo, y otro usuario con
    /// el mismo memo obtiene SU lista, no la del primero.
    /// </summary>
    [Fact]
    public async Task Cinco_peticiones_en_el_mismo_ambito_leen_la_base_lo_mismo_que_una()
    {
        var contador = new ContadorLecturas();
        var tenantActual = new TenantActualAmbiental { TenantId = _consultora };
        await using var contexto = CrearContexto(tenantActual, contador);
        var usuario = new CurrentUserServiceFalso(_usuario, _consultora);

        await new ObtenerClientesAutorizadosQueryHandler(contexto, contexto, usuario)
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        var lecturasDeUna = contador.Total;
        lecturasDeUna.Should().BeGreaterThan(0);

        contador.Total = 0;
        var memo = new MemoClientesAutorizados();
        var handler = new ObtenerClientesAutorizadosQueryHandler(contexto, contexto, usuario, null, tenantActual, memo);
        IReadOnlyList<ClienteAutorizadoDto> ultima = [];
        for (var i = 0; i < 5; i++)
            ultima = await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);

        contador.Total.Should().Be(lecturasDeUna, "las cinco peticiones de la visita comparten una sola lectura");
        ultima.Should().HaveCount(2);

        var antes = contador.Total;
        tenantActual.TenantId = _clienteDelegante;
        await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        contador.Total.Should().BeGreaterThan(antes, "otro Tenant efectivo es otro contexto RLS: se lee de nuevo");
        tenantActual.TenantId = _consultora;

        antes = contador.Total;
        using (AmbitoTenantExplicito.Establecer(_clienteDelegante))
            await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        contador.Total.Should().BeGreaterThan(antes, "un fan-out con ámbito explícito es otro contexto");

        antes = contador.Total;
        await handler.Handle(new ObtenerClientesAutorizadosQuery { LecturaVigente = true }, CancellationToken.None);
        contador.Total.Should().BeGreaterThan(antes, "la lectura vigente de las puertas que fallan cerradas no usa el memo");

        antes = contador.Total;
        memo.Invalidar();
        await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        contador.Total.Should().BeGreaterThan(antes, "tras un Command se lee de nuevo");

        antes = contador.Total;
        await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        contador.Total.Should().Be(antes, "y lo releído vuelve a servir al resto de la visita");

        var otroUsuario = await new ObtenerClientesAutorizadosQueryHandler(
                contexto, contexto, new CurrentUserServiceFalso(Guid.NewGuid(), _consultora), null, tenantActual, memo)
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        otroUsuario.Should().ContainSingle("otro usuario sin delegaciones no hereda la lista del primero")
            .Which.EsOrigen.Should().BeTrue();
    }

    private sealed class ContadorLecturas : DbCommandInterceptor
    {
        public int Total { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Total++;
            return ValueTask.FromResult(result);
        }
    }

    private CaeManagerDbContext CrearContexto(TenantActualAmbiental tenantActual, DbCommandInterceptor contador)
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), contador)
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantSellado)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class CurrentUserServiceFalso(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
