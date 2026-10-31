using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Identity;
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
/// — emisión fuera de Sesión Privilegiada, con autoridad AdminPlataforma, 2FA,
/// motivo, caducidad y acceso de soporte vigente sobre el Tenant propietario objetivo. Lo que estos tests protegen es justo lo que
/// más fácil sería romper por accidente: que una clave del tenant A nunca
/// resulte visible/revocable desde una delegación que no le pertenece, y que
/// el lookup por hash siga acotado a esa única operación — desde 2026-09-21,
/// resolviendo primero el tenant y leyendo la fila después dentro de él (ver
/// ClaveApiRepository). Estos tests conectan como propietario, así que NO
/// observan RLS: lo que la API pública hace bajo <c>cae_app_runtime</c> lo
/// mide <see cref="ApiPublicaBajoRolRuntimeTests"/>.
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
        delegacion.ActivarParaSoporte("Prueba de integración", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.Users.Add(new ApplicationUser { Id = _usuarioPlataforma, TenantId = tenantPlataforma.Id, UserName = "emisor-alfa@ejemplo.test", Email = "emisor-alfa@ejemplo.test" });

        await contexto.SaveChangesAsync();

        _tenantPlataformaId = tenantPlataforma.Id;
        _tenantClienteId = tenantCliente.Id;
        _delegacionId = delegacion.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(ActorAuditoria? actorAuditoria = null)
    {
        // A diferencia de TenantActualAmbiental (tenant fijo asignado desde
        // fuera), esta implementación lee AmbitoTenantExplicito en cada
        // acceso — igual que la TenantActual real de Web — porque los
        // handlers bajo prueba abren y cierran ese ámbito ellos mismos
        // (AmbitoTenantExplicito.Establecer) para sellar/leer contra el
        // tenant cliente, no contra un tenant fijado de antemano.
        //
        // Con actorAuditoria, el contexto lleva además el AuditoriaInterceptor real, para observar
        // el registro de auditoría que deja el alta de la clave.
        var tenantActual = new TenantActualPorAmbito();
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (actorAuditoria is { } actor)
            builder.AddInterceptors(new AuditoriaInterceptor(new ActorAuditoriaFijo(actor)), new TenantSelladoInterceptor(tenantActual));
        else
            builder.AddInterceptors(new TenantSelladoInterceptor(tenantActual));

        return new CaeManagerDbContext(builder.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class ActorAuditoriaFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    [Fact]
    public async Task El_motivo_de_la_emision_queda_en_la_auditoria_del_Tenant_propietario()
    {
        const string motivo = "Integración del ERP solicitada por escrito";
        await using var contexto = CrearContexto(ActorAuditoria.Normal(_usuarioPlataforma));
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contexto);

        var generada = await handler.Handle(
            new GenerarClaveApiCommand(_delegacionId, "Integración auditada", DateTime.UtcNow.AddDays(30), motivo), CancellationToken.None);
        generada.EsExitoso.Should().BeTrue();

        // Se lee dentro del Tenant propietario, con el filtro global puesto: si el registro hubiera
        // quedado sellado contra el Tenant de plataforma, no aparecería aquí.
        await using var contextoLectura = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var registro = await contextoLectura.RegistrosAuditoria
                .SingleAsync(r => r.EntidadTipo == nameof(ClaveApi) && r.EntidadId == generada.Valor.Id);

            registro.TenantId.Should().Be(_tenantClienteId, "el alta de la clave se audita en el Tenant propietario");
            // Se lee el JSON y no su texto: el serializador escapa lo que no es ASCII («ó» → ó).
            using var datos = System.Text.Json.JsonDocument.Parse(registro.DatosDespues!);
            datos.RootElement.GetProperty(nameof(ClaveApi.MotivoEmision)).GetString().Should().Be(motivo);
            registro.ActorRealUsuarioId.Should().Be(_usuarioPlataforma);
        }

        await using var contextoPlataforma = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantPlataformaId))
        {
            (await contextoPlataforma.RegistrosAuditoria.AnyAsync(r => r.EntidadId == generada.Valor.Id))
                .Should().BeFalse("el motivo no queda en el Tenant de plataforma");
        }
    }

    [Fact]
    public async Task El_administrador_de_la_plataforma_genera_una_clave_para_el_cliente_delegado()
    {
        await using var contexto = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contexto);

        var resultado = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Integración de prueba", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClaveEnClaro.Should().StartWith("hydra_");
        resultado.Valor.PrefijoVisible.Should().Be(resultado.Valor.ClaveEnClaro[..12]);
    }

    [Fact]
    public async Task La_clave_generada_pertenece_al_tenant_cliente_no_al_de_plataforma()
    {
        await using var contextoEscritura = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoEscritura), new ClaveApiRepository(contextoEscritura), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoEscritura);
        var generada = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Integración de prueba", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

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
    public async Task Una_cuenta_ajena_a_la_organizacion_de_origen_no_puede_generar_claves()
    {
        await using var contexto = CrearContexto();
        var handler = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            // tenantOrigenId = el propio cliente, no la plataforma.
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantClienteId), contexto);

        var resultado = await handler.Handle(new GenerarClaveApiCommand(_delegacionId, "Intento no autorizado", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
    }

    [Fact]
    public async Task Sin_concesion_sobre_el_Tenant_propietario_no_se_revoca()
    {
        await using var contextoGenerar = CrearContexto();
        var handlerGenerar = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoGenerar), new ClaveApiRepository(contextoGenerar), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoGenerar);
        var generada = await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "No revocable", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        // La cuenta es del Tenant marcado como plataforma y es el origen de la delegación: sin
        // concesión sobre el Tenant propietario, eso solo ya no autoriza.
        await using var contextoRevocar = CrearContexto();
        var handlerRevocar = new RevocarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoRevocar), new ClaveApiRepository(contextoRevocar), new AutorizacionClavesApiDePrueba(Guid.NewGuid()),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoRevocar);
        var resultado = await handlerRevocar.Handle(new RevocarClaveApiCommand(_delegacionId, generada.Valor.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClaveApi.NoAutorizado");
    }

    [Fact]
    public async Task Revocar_desactiva_la_clave_y_deja_de_autenticar()
    {
        await using var contextoGenerar = CrearContexto();
        var handlerGenerar = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoGenerar), new ClaveApiRepository(contextoGenerar), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoGenerar);
        var generada = await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "A revocar", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        await using var contextoRevocar = CrearContexto();
        var handlerRevocar = new RevocarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoRevocar), new ClaveApiRepository(contextoRevocar), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoRevocar);
        var resultadoRevocar = await handlerRevocar.Handle(
            new RevocarClaveApiCommand(_delegacionId, generada.Valor.Id), CancellationToken.None);

        resultadoRevocar.EsExitoso.Should().BeTrue();

        // El camino de autenticación debe seguir encontrando la fila pero
        // marcada inactiva — es el handler quien la rechaza, no la ausencia de
        // la fila. Son los dos pasos que hace ApiKeyAuthenticationHandler:
        // primero de quién es la clave (sin tenant, lo único que no se puede
        // saber de otro modo), y solo entonces la fila, ya dentro del tenant.
        // Una clave REVOCADA sigue resolviendo su tenant a propósito: la
        // vigencia la decide EstaActiva, no la visibilidad.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generada.Valor.ClaveEnClaro))).ToLowerInvariant();
        await using var contextoAuth = CrearContexto();
        var repoAuth = new ClaveApiRepository(contextoAuth);

        var tenantDeLaClave = await repoAuth.ObtenerTenantPorHashAsync(hash);
        tenantDeLaClave.Should().Be(_tenantClienteId,
            "sin resolver el tenant no hay forma de leer la fila, así que un null aquí haría que la " +
            "comprobación de abajo midiera otra cosa");

        using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(tenantDeLaClave!.Value))
        {
            var claveTrasRevocar = await repoAuth.ObtenerPorHashAsync(hash);

            claveTrasRevocar.Should().NotBeNull();
            claveTrasRevocar!.EstaActiva.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Obtener_lista_solo_las_claves_del_cliente_de_esa_delegacion()
    {
        await using var contextoGenerar = CrearContexto();
        var handlerGenerar = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contextoGenerar), new ClaveApiRepository(contextoGenerar), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId), contextoGenerar);
        await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "Primera", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);
        await handlerGenerar.Handle(new GenerarClaveApiCommand(_delegacionId, "Segunda", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        await using var contextoConsulta = CrearContexto();
        var handlerConsulta = new ObtenerClavesApiQueryHandler(
            contextoConsulta, contextoConsulta, new CurrentUserServiceFalso(_usuarioPlataforma, tenantOrigenId: _tenantPlataformaId));

        var claves = await handlerConsulta.Handle(new ObtenerClavesApiQuery(_delegacionId), CancellationToken.None);

        claves.Should().HaveCount(2);
        claves.Select(c => c.NombreDescriptivo).Should().BeEquivalentTo("Primera", "Segunda");
    }
}
