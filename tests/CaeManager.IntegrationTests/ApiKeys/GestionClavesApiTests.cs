using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.ApiKeys;

/// <summary>
/// GenerarClaveApiCommand/RevocarClaveApiCommand/ObtenerClavesApiQuery (P3-29)
/// — mismo criterio de autorización que AbrirAccesoSoporteCommand: solo el
/// Administrador del tenant plataforma, sobre una delegación de soporte que
/// lo autorice sobre ese cliente. Lo que estos tests protegen es justo lo que
/// más fácil sería romper por accidente: que una clave del tenant A nunca
/// resulte visible/revocable desde una delegación que no le pertenece, y que
/// el lookup por hash (que ignora el filtro global a propósito, ver
/// ClaveApiRepository) siga acotado a esa única operación.
/// </summary>
public class GestionClavesApiTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioPlataforma = Guid.NewGuid();
    private Guid _tenantPlataformaId;
    private Guid _tenantClienteId;
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var tenantPlataforma = new Tenant("Hydra", esPlataforma: true);
        var tenantCliente = new Tenant("Ibertec S.A.");
        contexto.Tenants.AddRange(tenantPlataforma, tenantCliente);

        var delegacion = DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantCliente.Id);
        contexto.DelegacionesTenant.Add(delegacion);

        await contexto.SaveChangesAsync();

        _tenantPlataformaId = tenantPlataforma.Id;
        _tenantClienteId = tenantCliente.Id;
        _delegacionId = delegacion.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        // A diferencia de TenantActualAmbiental (tenant fijo asignado desde
        // fuera), esta implementación lee AmbitoTenantExplicito en cada
        // acceso — igual que la TenantActual real de Web — porque los
        // handlers bajo prueba abren y cierran ese ámbito ellos mismos
        // (AmbitoTenantExplicito.Establecer) para sellar/leer contra el
        // tenant cliente, no contra un tenant fijado de antemano.
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    [Fact]
    public async Task El_administrador_de_la_plataforma_genera_una_clave_para_el_cliente_delegado()
    {
        await using var contexto = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), contexto,
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contexto);

        var resultado = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Integración de prueba"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClaveEnClaro.Should().StartWith("hydra_");
        resultado.Valor.PrefijoVisible.Should().Be(resultado.Valor.ClaveEnClaro[..12]);
    }

    [Fact]
    public async Task La_clave_generada_pertenece_al_tenant_cliente_no_al_de_plataforma()
    {
        await using var contextoEscritura = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoEscritura), new ClaveApiRepository(contextoEscritura), contextoEscritura,
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoEscritura);
        var generada = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Integración de prueba"), CancellationToken.None);

        // Sin AmbitoTenantExplicito, el filtro global la escondería si
        // hubiera quedado sellada contra el tenant equivocado.
        await using var contextoComoCliente = CrearContexto();
        using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var visible = await contextoComoCliente.ClavesApi.FirstOrDefaultAsync(c => c.Id == generada.Valor.Id);
            visible.Should().NotBeNull();
            visible!.TenantId.Should().Be(_tenantClienteId);
        }
    }

    [Fact]
    public async Task Un_tenant_que_no_es_la_plataforma_no_puede_generar_claves()
    {
        await using var contexto = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), contexto,
            // tenantOrigenId = el propio cliente, no la plataforma.
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantClienteId), contexto);

        var resultado = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Intento no autorizado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
    }

    [Fact]
    public async Task Revocar_desactiva_la_clave_y_deja_de_autenticar()
    {
        await using var contextoGenerar = CrearContexto();
        var handlerGenerar = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoGenerar), new ClaveApiRepository(contextoGenerar), contextoGenerar,
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoGenerar);
        var generada = await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "A revocar"), CancellationToken.None);

        await using var contextoRevocar = CrearContexto();
        var handlerRevocar = new RevocarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoRevocar), new ClaveApiRepository(contextoRevocar), contextoRevocar,
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoRevocar);
        var resultadoRevocar = await handlerRevocar.Handle(
            new RevocarClaveApiCommand(_delegacionId, generada.Valor.Id), CancellationToken.None);

        resultadoRevocar.EsExitoso.Should().BeTrue();

        // El repositorio de autenticación (ignora el filtro global a propósito,
        // ver ClaveApiRepository) debe seguir encontrando la fila pero marcada
        // inactiva — es el handler de autenticación quien la rechaza, no la
        // ausencia de la fila.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generada.Valor.ClaveEnClaro))).ToLowerInvariant();
        await using var contextoAuth = CrearContexto();
        var repoAuth = new ClaveApiRepository(contextoAuth);
        var claveTrasRevocar = await repoAuth.ObtenerPorHashAsync(hash);

        claveTrasRevocar.Should().NotBeNull();
        claveTrasRevocar!.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public async Task Obtener_lista_solo_las_claves_del_cliente_de_esa_delegacion()
    {
        await using var contextoGenerar = CrearContexto();
        var handlerGenerar = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoGenerar), new ClaveApiRepository(contextoGenerar), contextoGenerar,
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoGenerar);
        await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "Primera"), CancellationToken.None);
        await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "Segunda"), CancellationToken.None);

        await using var contextoConsulta = CrearContexto();
        var handlerConsulta = new ObtenerClavesApiQueryHandler(
            contextoConsulta, contextoConsulta, new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId));

        var claves = await handlerConsulta.Handle(new ObtenerClavesApiQuery(_delegacionId), CancellationToken.None);

        claves.Should().HaveCount(2);
        claves.Select(c => c.NombreDescriptivo).Should().BeEquivalentTo("Primera", "Segunda");
    }
}
