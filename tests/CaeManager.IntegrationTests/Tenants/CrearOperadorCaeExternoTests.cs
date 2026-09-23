using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cobertura de PD-A9: alta de un Tenant Operador CAE externo (perfil
/// Consultora) nuevo, raíz — sin ninguna <see cref="DelegacionTenant"/> ni
/// <see cref="AsignacionOperadorDelegado"/> entrante. Contra Postgres real,
/// mismo motivo que <c>CrearClienteDeleganteTests</c>: hay que probar tanto
/// la autorización global como el sellado real de <c>TenantId</c> en la fila
/// de <c>ParametroSistema</c> del tenant nuevo vía
/// <see cref="TenantSelladoInterceptor"/>.
/// </summary>
public class CrearOperadorCaeExternoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ITenantActual _tenantActual = new TenantActualDesdeAmbitoExplicito();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private static async Task SembrarAdminPlataformaGlobalAsync(CaeManagerDbContext contexto, Guid usuarioId)
    {
        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            usuarioId, vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: null));

        await contexto.SaveChangesAsync();
    }

    private CrearOperadorCaeExternoCommandHandler CrearHandler(CaeManagerDbContext contexto, Guid? usuarioId) =>
        new(
            new TenantRepository(contexto),
            new ParametroSistemaRepository(contexto),
            new AutorizacionAdminPlataformaPorConcesion(contexto),
            new CurrentUserServiceFalso(usuarioId),
            new AsignacionesOperativasWriter(
                contexto, _tenantActual, new CurrentUserServiceFalso(usuarioId)),
            contexto);

    [Fact]
    public async Task Un_administrador_de_plataforma_crea_el_tenant_raiz_con_perfil_consultora()
    {
        await using var contexto = CrearContexto();

        var usuarioId = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, usuarioId);
        var handler = CrearHandler(contexto, usuarioId);

        var resultado = await handler.Handle(
            new CrearOperadorCaeExternoCommand($"ArcosSPA de prueba {Guid.NewGuid():N}"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        var tenantOperadorId = resultado.Valor;
        var tenantOperador = await contexto.Tenants.SingleAsync(t => t.Id == tenantOperadorId);
        tenantOperador.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora);
        tenantOperador.EsPlataforma.Should().BeFalse();
        tenantOperador.PuedeActuarComoOperadorCaeExterno.Should().BeTrue(
            "el comando concede la capacidad explícita, ya no basta el perfil Consultora");

        // IgnoreQueryFilters: mismo motivo que en CrearClienteDeleganteTests —
        // ya salimos del AmbitoTenantExplicito que el propio Command abrió.
        var parametro = await contexto.ParametrosSistema.IgnoreQueryFilters()
            .SingleAsync(p => p.TenantId == tenantOperadorId);
        parametro.Should().NotBeNull();

        // Nace raíz: ninguna delegación lo trae al mundo, ninguna asignación
        // de operador delegado apunta a él como Cliente.
        (await contexto.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == tenantOperadorId))
            .Should().BeFalse("un Operador CAE externo no nace delegado desde nadie");

        // Sí tiene su operación raíz — el mismo ancla que cualquier tenant.
        (await contexto.AsignacionesOperacion.AnyAsync(o => o.EsRaiz && o.PropietarioTenantId == tenantOperadorId))
            .Should().BeTrue();

        // Y NINGUNA operación delegada lo tiene como propietario: nada lo opera
        // desde fuera todavía.
        (await contexto.AsignacionesOperacion.AnyAsync(o => !o.EsRaiz && o.PropietarioTenantId == tenantOperadorId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Rechaza_el_alta_sin_concesion_de_administrador_de_plataforma()
    {
        await using var contexto = CrearContexto();

        var handler = CrearHandler(contexto, Guid.NewGuid());

        var resultado = await handler.Handle(
            new CrearOperadorCaeExternoCommand("Operador rechazado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OperadorCaeExterno.SinPermiso");

        // 1: solo el tenant #1 (EsPlataforma = true) que la migración siembra
        // en toda base de datos nueva — ninguno más debió crearse tras el rechazo.
        (await contexto.Tenants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_un_nombre_de_tenant_duplicado()
    {
        await using var contexto = CrearContexto();

        var nombreDuplicado = $"Operador duplicado {Guid.NewGuid():N}";
        var tenantExistente = new Tenant(nombreDuplicado);
        contexto.Tenants.Add(tenantExistente);
        await contexto.SaveChangesAsync();

        var usuarioAutorizado = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, usuarioAutorizado);
        var handler = CrearHandler(contexto, usuarioAutorizado);

        var resultado = await handler.Handle(new CrearOperadorCaeExternoCommand(nombreDuplicado), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OperadorCaeExterno.NombreDuplicado");
    }

    private sealed class CurrentUserServiceFalso(Guid? usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantActualDesdeAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
