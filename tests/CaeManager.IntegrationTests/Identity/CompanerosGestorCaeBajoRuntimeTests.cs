using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// «Contactar con un compañero» contra PostgreSQL real, autenticando como <c>cae_app_runtime</c> (la RLS de
/// <c>AspNetUsers</c> siempre aplica), con el handler de producción, el directorio real y los interceptores de producción.
///
/// <para>
/// Lo que solo esta capa puede probar: que un Gestor CAE del Operador CAE A ve a sus compañeros de A y a nadie más;
/// que no ve a los del Operador CAE B ni a los de un Tenant propietario, ni siquiera cuando la RLS SÍ deja ver una
/// cuenta de B (Operador Delegado sobre A: el filtro por Operador CAE de C# es el que la excluye); que quien no es
/// Gestor CAE no recibe nada; que sigue funcionando dentro del Workspace operativo derivado de un Tenant propietario;
/// y que el puerto, llamado sin el ámbito de su Operador CAE o con el de otro, falla cerrado.
/// </para>
/// </summary>
public class CompanerosGestorCaeBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _operadorA = new("Operador CAE A de prueba");
    private readonly Tenant _operadorB = new("Operador CAE B de prueba");
    private readonly Tenant _propietario = new("Tenant propietario de prueba");

    private readonly Guid _gestorA1 = Guid.NewGuid();
    private readonly Guid _gestorA2 = Guid.NewGuid();
    private readonly Guid _gestorA3 = Guid.NewGuid();
    private readonly Guid _gestorADesactivado = Guid.NewGuid();
    private readonly Guid _coordinadorA = Guid.NewGuid();
    private readonly Guid _administradorA = Guid.NewGuid();
    private readonly Guid _consultaA = Guid.NewGuid();
    private readonly Guid _cuentaSinRolA = Guid.NewGuid();
    private readonly Guid _gestorB1 = Guid.NewGuid();
    private readonly Guid _gestorB2 = Guid.NewGuid();
    private readonly Guid _gestorDelPropietario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario(_operadorA.Id);
        await contexto.Database.MigrateAsync();

        contexto.Tenants.AddRange(_operadorA, _operadorB, _propietario);

        // El Operador CAE B también opera sobre el Operador CAE A (Operador Delegado): la RLS deja ver desde A la
        // cuenta _gestorB1 (rama E1). Es el caso en que SOLO el filtro por Operador CAE de C# la excluye.
        var delegacion = new DelegacionTenant(_operadorB.Id, _operadorA.Id);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _gestorB1, Roles.GestorCae));

        var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

        void Cuenta(Guid id, Guid tenantId, string alias, string? rol, string? telefono = null, bool desactivada = false)
        {
            var cuenta = new ApplicationUser
            {
                Id = id,
                TenantId = tenantId,
                UserName = $"{alias}.{id:N}@talveg.test",
                NormalizedUserName = $"{alias}.{id:N}@TALVEG.TEST".ToUpperInvariant(),
                Email = $"{alias}.{id:N}@talveg.test",
                NormalizedEmail = $"{alias}.{id:N}@TALVEG.TEST".ToUpperInvariant(),
                NombreCompleto = alias,
                PhoneNumber = telefono,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            };
            if (desactivada) cuenta.Desactivar();
            contexto.Users.Add(cuenta);
            if (rol is not null)
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_gestorA1, _operadorA.Id, "Ana", Roles.GestorCae, "+34 600 000 001");
        Cuenta(_gestorA2, _operadorA.Id, "Bea", Roles.GestorCae, "+34 600 000 002");
        Cuenta(_gestorA3, _operadorA.Id, "Carla", Roles.GestorCae);
        Cuenta(_gestorADesactivado, _operadorA.Id, "Dora", Roles.GestorCae, "+34 600 000 004", desactivada: true);
        Cuenta(_coordinadorA, _operadorA.Id, "Elena", Roles.CoordinadorCae, "+34 600 000 005");
        Cuenta(_administradorA, _operadorA.Id, "Flor", Roles.Administrador, "+34 600 000 006");
        Cuenta(_consultaA, _operadorA.Id, "Gema", Roles.Consulta, "+34 600 000 007");
        Cuenta(_cuentaSinRolA, _operadorA.Id, "Hilda", null, "+34 600 000 008");
        Cuenta(_gestorB1, _operadorB.Id, "Irene", Roles.GestorCae, "+34 600 000 011");
        Cuenta(_gestorB2, _operadorB.Id, "Julia", Roles.GestorCae, "+34 600 000 012");
        Cuenta(_gestorDelPropietario, _propietario.Id, "Karen", Roles.GestorCae, "+34 600 000 021");

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_Gestor_CAE_ve_a_sus_companeros_del_mismo_Operador_CAE_y_a_nadie_mas()
    {
        var lista = await Companeros(_gestorA1, _operadorA.Id);

        // Ni él mismo, ni la cuenta desactivada, ni el Coordinador, el Administrador, Consulta o la cuenta sin rol del
        // mismo Operador CAE; ni los de B (_gestorB1 incluido, que la RLS SÍ deja ver); ni el del Tenant propietario.
        lista.Select(c => c.UsuarioId).Should().BeEquivalentTo(new[] { _gestorA2, _gestorA3 });

        var bea = lista.Single(c => c.UsuarioId == _gestorA2);
        bea.Nombre.Should().Be("Bea");
        bea.Correo.Should().Be($"Bea.{_gestorA2:N}@talveg.test");
        bea.Telefono.Should().Be("+34 600 000 002");
        lista.Single(c => c.UsuarioId == _gestorA3).Telefono.Should().BeNull("la cuenta no declara teléfono");
    }

    [Fact]
    public async Task Los_Gestores_CAE_de_otro_Operador_CAE_ven_solo_a_los_suyos()
    {
        var lista = await Companeros(_gestorB1, _operadorB.Id);

        lista.Select(c => c.UsuarioId).Should().BeEquivalentTo(new[] { _gestorB2 });
    }

    [Fact]
    public async Task Dentro_del_Workspace_operativo_derivado_de_un_Tenant_propietario_ve_los_mismos_companeros()
    {
        var lista = await Companeros(_gestorA1, _operadorA.Id, tenantDeSesion: _propietario.Id);

        lista.Select(c => c.UsuarioId).Should().BeEquivalentTo(
            new[] { _gestorA2, _gestorA3 },
            "el Operador CAE sale de su Tenant de origen, no del Tenant propietario activo, y no se cuela el Gestor CAE de la casa");
    }

    [Theory]
    [InlineData("coordinador")]
    [InlineData("administrador")]
    [InlineData("consulta")]
    [InlineData("sinrol")]
    public async Task Quien_no_es_Gestor_CAE_no_recibe_nada(string quien)
    {
        var usuario = quien switch
        {
            "coordinador" => _coordinadorA,
            "administrador" => _administradorA,
            "consulta" => _consultaA,
            _ => _cuentaSinRolA,
        };

        (await Companeros(usuario, _operadorA.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cuenta_desactivada_de_Gestor_CAE_no_recibe_nada()
    {
        (await Companeros(_gestorADesactivado, _operadorA.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task El_puerto_llamado_fuera_del_ambito_de_su_Operador_CAE_o_con_el_de_otro_falla_cerrado()
    {
        // Control positivo: con el ámbito correcto el puerto sí devuelve a los compañeros.
        var bien = await EnArnes(_gestorA1, _operadorA.Id, _operadorA.Id, async (_, _, directorio) =>
        {
            using (AmbitoTenantExplicito.Establecer(_operadorA.Id))
                return await directorio.ObtenerGestoresCaeDelOperadorAsync(_operadorA.Id, _gestorA1);
        });
        bien.Should().NotBeEmpty();

        // Sin abrir el ámbito: el Tenant activo es otro (el del Workspace), y el puerto no lee. Medido con la guarda
        // retirada: este caso se pone en rojo, porque la RLS (rama D, Tenant de origen) deja leer las cuentas de A también
        // desde el Workspace; es la guarda del puerto la que lo impide, no la política.
        var sinAmbito = await EnArnes(_gestorA1, _operadorA.Id, _propietario.Id,
            (_, _, directorio) => directorio.ObtenerGestoresCaeDelOperadorAsync(_operadorA.Id, _gestorA1));
        sinAmbito.Should().BeEmpty();

        // Con el ámbito de A pidiendo B: tampoco.
        var pidiendoOtro = await EnArnes(_gestorA1, _operadorA.Id, _operadorA.Id, async (_, _, directorio) =>
        {
            using (AmbitoTenantExplicito.Establecer(_operadorA.Id))
                return await directorio.ObtenerGestoresCaeDelOperadorAsync(_operadorB.Id, _gestorA1);
        });
        pidiendoOtro.Should().BeEmpty();
    }

    [Fact]
    public async Task La_RLS_sola_ya_oculta_a_los_Gestores_CAE_de_otro_Operador_CAE_y_deja_ver_solo_al_delegado()
    {
        // Instrumento: lo que el runtime ve en AspNetUsers desde el ámbito de A, sin pasar por ningún filtro de C#.
        var visibles = await EnArnes(_gestorA1, _operadorA.Id, _operadorA.Id, async (_, contexto, _) =>
        {
            using (AmbitoTenantExplicito.Establecer(_operadorA.Id))
                return await contexto.Users.AsNoTracking().Select(u => u.Id).ToListAsync();
        });

        visibles.Should().Contain(_gestorA2);
        visibles.Should().NotContain(_gestorB2, "la RLS oculta las cuentas de otro Operador CAE sin relación con A");
        visibles.Should().NotContain(_gestorDelPropietario);
        // Control positivo de la premisa del primer test: la cuenta de B con delegación sobre A SÍ es legible por RLS,
        // así que lo que la excluye de la lista es el filtro por Operador CAE del puerto.
        visibles.Should().Contain(_gestorB1);
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<CompaneroGestorCaeDto>> Companeros(Guid usuarioId, Guid origen, Guid? tenantDeSesion = null) =>
        EnArnes(usuarioId, origen, tenantDeSesion ?? origen, (usuario, _, directorio) =>
            new ObtenerCompanerosGestorCaeQueryHandler(usuario, directorio, directorio)
                .Handle(new ObtenerCompanerosGestorCaeQuery(), CancellationToken.None));

    private async Task<T> EnArnes<T>(
        Guid usuarioId, Guid origen, Guid tenantDeSesion,
        Func<AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion, CaeManagerDbContext, DirectorioUsuariosTenant, Task<T>> ejecutar)
    {
        var usuario = new AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion(usuarioId, Roles.GestorCae, origen);
        var tenantActual = new TenantSegunAmbito(tenantDeSesion);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                npgsql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null);
            })
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(ActorAuditoria.Normal(usuarioId))),
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        await using var contexto = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddSingleton(contexto);
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddScoped<DirectorioUsuariosTenant>();
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddDefaultTokenProviders();
        await using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();
        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();

        return await ejecutar(usuario, contexto, directorio);
    }

    private CaeManagerDbContext ContextoPropietario(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Como el <c>TenantActual</c> de la web: el ámbito explícito manda sobre el de la sesión.</summary>
    private sealed class TenantSegunAmbito(Guid tenantDeSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeSesion;
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
