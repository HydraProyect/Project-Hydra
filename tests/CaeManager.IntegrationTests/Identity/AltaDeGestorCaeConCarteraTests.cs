using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.CrearUsuario;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
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
/// Alta de un Gestor CAE con su cartera ya elegida (<see cref="CrearUsuarioCommand.TenantsCartera"/>)
/// contra PostgreSQL real, autenticando como <c>cae_app_runtime</c> (RLS siempre aplica), con la
/// estrategia de reintento de producción y los interceptores de producción: sellado de tenant,
/// coordenadas RLS, concurrencia y auditoría. Handler, catálogo, transacción e Identity son los de
/// producción.
///
/// <para>
/// Lo que solo esta capa puede probar: que la cuenta (política de <c>AspNetUsers</c>, Tenant del
/// Operador CAE) y cada cartera (política de las carteras, Tenant beneficiario) se escriben en una
/// sola transacción aunque cada una exija un Tenant de sesión distinto; que un fallo a mitad —una
/// anulación o un rechazo de RLS— no deja ni la cuenta ni ninguna cartera; que el predicado de
/// asignables excluye de verdad la operación caducada, la raíz y la de otro Operador CAE; y que la
/// auditoría separa Actor real y Usuario simulado.
/// </para>
/// </summary>
public class AltaDeGestorCaeConCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _operador = new("Operador CAE de prueba");
    private readonly Tenant _otroOperador = new("Otro Operador CAE de prueba");
    private readonly Tenant _beneficiarioA = new("Beneficiario A de prueba");
    private readonly Tenant _beneficiarioB = new("Beneficiario B de prueba");
    private readonly Tenant _beneficiarioCaducado = new("Beneficiario con operación caducada");
    private readonly Tenant _beneficiarioAjeno = new("Beneficiario de otro Operador CAE");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_operador, _otroOperador, _beneficiarioA, _beneficiarioB, _beneficiarioCaducado, _beneficiarioAjeno);

        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_operador.Id, ServicioCae.Outbound, ahora.AddDays(-30), ahora));
        foreach (var (beneficiario, operador, hasta) in new[]
                 {
                     (_beneficiarioA, _operador, (DateTime?)null),
                     (_beneficiarioB, _operador, null),
                     (_beneficiarioCaducado, _operador, ahora.AddMinutes(-1)),
                     (_beneficiarioAjeno, _otroOperador, null),
                 })
        {
            contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                beneficiario.Id, operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                vigenciaDesde: ahora.AddDays(-30), vigenciaHasta: hasta, ahora));
            contexto.DelegacionesTenant.Add(new DelegacionTenant(operador.Id, beneficiario.Id));
        }

        // Los roles de Identity ya los siembra la migración.
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_cuenta_su_rol_y_una_cartera_por_Tenant_beneficiario_se_escriben_juntas()
    {
        // Sesión Privilegiada: el Actor real es Soporte TALVEG y el Usuario simulado, el Administrador.
        var soporte = Guid.NewGuid();
        var actor = new ActorAuditoria(soporte, _administrador, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());

        await using var arnes = Arnes(_administrador, _operador.Id, actor);
        var resultado = await arnes.Handler().Handle(
            Alta("gestora@prueba.test", _beneficiarioA.Id, _beneficiarioB.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        var usuarioId = resultado.Valor.UsuarioId;
        resultado.Valor.FalloAlAsignarRol.Should().BeNull();

        await using var propietario = ContextoPropietario();
        var cuenta = await propietario.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == usuarioId);
        cuenta.TenantId.Should().Be(_operador.Id, "la cuenta es del Operador CAE, no de los Tenants de su cartera");
        (await RolesDeAsync(propietario, usuarioId)).Should().Equal(Roles.GestorCae);

        var carteras = await propietario.AsignacionesCartera.AsNoTracking().Where(c => c.UsuarioId == usuarioId).ToListAsync();
        carteras.Select(c => c.PropietarioTenantId).Should().BeEquivalentTo([_beneficiarioA.Id, _beneficiarioB.Id]);
        carteras.Should().AllSatisfy(c =>
        {
            c.OperadorTenantId.Should().Be(_operador.Id);
            c.Rol.Should().Be(Roles.GestorCae);
            c.Estado.Should().Be(EstadoAsignacion.Vigente);
            c.AmbitoRelacionClienteId.Should().BeNull("cartera del Tenant entero");
            c.AmbitoCentroId.Should().BeNull();
            c.VigenciaHasta.Should().BeNull();
        });

        (await propietario.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == usuarioId)).Should().Be(2,
            "la fila heredada de F1 acompaña a cada cartera, igual que al aceptar una solicitud");

        // Auditoría: cada cartera deja rastro con el Usuario simulado y el Actor real por separado.
        var registros = await propietario.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadTipo == nameof(AsignacionCartera))
            .ToListAsync();
        registros.Select(r => r.EntidadId).Should().BeEquivalentTo(carteras.Select(c => c.Id));
        registros.Should().AllSatisfy(r =>
        {
            r.UsuarioId.Should().Be(_administrador, "el Usuario simulado");
            r.ActorRealUsuarioId.Should().Be(soporte, "quien estaba de verdad detrás del teclado");
        });
    }

    [Fact]
    public async Task Si_una_cartera_se_anula_a_mitad_no_queda_ni_la_cuenta_ni_la_cartera_anterior()
    {
        await using var arnes = Arnes(_administrador, _operador.Id);

        // La operación del segundo Tenant se cierra entre la validación y su incorporación: el primer
        // Tenant ya se guardó dentro de la transacción cuando el segundo se anula.
        arnes.AntesDeIncorporar = async propietarioTenantId =>
        {
            if (propietarioTenantId != _beneficiarioB.Id) return;
            await using var propietario = ContextoPropietario();
            await propietario.AsignacionesOperacion
                .Where(o => o.PropietarioTenantId == _beneficiarioB.Id && !o.EsRaiz)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, EstadoAsignacion.Suspendida));
        };

        var resultado = await arnes.Handler().Handle(
            Alta("amedias@prueba.test", _beneficiarioA.Id, _beneficiarioB.Id), CancellationToken.None);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.EmpresaNoAsignable);
        arnes.CarterasGuardadas.Should().Be(1, "la primera cartera llegó a guardarse dentro de la transacción");
        await AfirmarQueNoQuedaNadaAsync("amedias@prueba.test");
    }

    [Fact]
    public async Task Si_RLS_rechaza_una_cartera_a_mitad_no_queda_ni_la_cuenta_ni_la_cartera_anterior()
    {
        await using var arnes = Arnes(_administrador, _operador.Id);

        // El segundo guardado se hace, a propósito, con el Tenant del Operador CAE como Tenant
        // activo: la política de las carteras lo rechaza (42501) y la transacción entera cae.
        arnes.GuardarSegundaEnTenantEquivocado = true;

        var resultado = await arnes.Handler().Handle(
            Alta("rls@prueba.test", _beneficiarioA.Id, _beneficiarioB.Id), CancellationToken.None);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.AltaNoGuardada);
        arnes.CarterasGuardadas.Should().Be(1);
        await AfirmarQueNoQuedaNadaAsync("rls@prueba.test");
    }

    [Theory]
    [InlineData("caducada")]
    [InlineData("raiz")]
    [InlineData("ajena")]
    public async Task Solo_se_asignan_Tenants_de_una_operacion_externa_vigente_del_propio_Operador_CAE(string caso)
    {
        var tenant = caso switch
        {
            "caducada" => _beneficiarioCaducado.Id,
            "raiz" => _operador.Id,
            _ => _beneficiarioAjeno.Id,
        };

        await using (var arnes = Arnes(_administrador, _operador.Id))
        {
            var resultado = await arnes.Handler().Handle(Alta($"{caso}@prueba.test", tenant), CancellationToken.None);
            resultado.Error.Should().Be(CrearUsuarioCommandHandler.EmpresaNoAsignable);
        }

        await AfirmarQueNoQuedaNadaAsync($"{caso}@prueba.test");
    }

    [Fact]
    public async Task La_lista_de_asignables_de_cada_Operador_CAE_es_solo_suya()
    {
        await using (var arnes = Arnes(_administrador, _operador.Id))
        using (AmbitoTenantExplicito.Establecer(_operador.Id))
        {
            (await arnes.Catalogo.ObtenerAsignablesAsync(_operador.Id)).Select(a => a.PropietarioTenantId)
                .Should().BeEquivalentTo([_beneficiarioA.Id, _beneficiarioB.Id],
                    "ni la caducada, ni la raíz, ni la de otro Operador CAE");
        }

        // Aunque un handler defectuoso pidiera los del Operador CAE ajeno, RLS no deja leer sus operaciones.
        await using (var ajeno = Arnes(_administradorAjeno, _otroOperador.Id))
        using (AmbitoTenantExplicito.Establecer(_otroOperador.Id))
        {
            (await ajeno.Catalogo.ObtenerAsignablesAsync(_operador.Id)).Should().BeEmpty();
            (await ajeno.Catalogo.ObtenerAsignablesAsync(_otroOperador.Id)).Select(a => a.PropietarioTenantId)
                .Should().Equal(_beneficiarioAjeno.Id);
        }
    }

    /// <summary>
    /// El catálogo no se fía del Id de operación que recibe: tiene que ser la externa de este
    /// Operador CAE sobre este Tenant propietario. Ni la de otro Tenant beneficiario del mismo
    /// Operador CAE, ni la raíz, aunque las dos estén vigentes.
    /// </summary>
    [Theory]
    [InlineData("de otro Tenant")]
    [InlineData("raiz")]
    public async Task El_catalogo_no_incorpora_con_una_operacion_que_no_es_la_de_ese_Tenant(string caso)
    {
        Guid operacionId;
        await using (var propietario = ContextoPropietario())
        {
            operacionId = caso == "raiz"
                ? await propietario.AsignacionesOperacion.Where(o => o.EsRaiz && o.PropietarioTenantId == _operador.Id).Select(o => o.Id).SingleAsync()
                : await propietario.AsignacionesOperacion.Where(o => !o.EsRaiz && o.PropietarioTenantId == _beneficiarioB.Id).Select(o => o.Id).SingleAsync();
        }

        await using var arnes = Arnes(_administrador, _operador.Id);
        using (AmbitoTenantExplicito.Establecer(_beneficiarioA.Id))
        {
            var resultado = await arnes.Catalogo.IncorporarAsync(_beneficiarioA.Id, _operador.Id, operacionId, Guid.NewGuid());
            resultado.MotivoAnulacion.Should().Be(MotivoAnulacionSolicitudCartera.OperacionNoVigente);
            resultado.Cartera.Should().BeNull();
        }
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private static CrearUsuarioCommand Alta(string email, params Guid[] tenants) =>
        new(email, "Gestora de prueba", Roles.GestorCae, null, null, false, tenants);

    private async Task AfirmarQueNoQuedaNadaAsync(string email)
    {
        await using var propietario = ContextoPropietario();
        (await propietario.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
            .Should().BeFalse("la cuenta se deshace con la transacción");
        (await propietario.AsignacionesCartera.AnyAsync(c => c.Rol == Roles.GestorCae))
            .Should().BeFalse("ninguna cartera sobrevive");
        (await propietario.AsignacionesOperadorDelegado.AnyAsync())
            .Should().BeFalse("ni su fila heredada");
    }

    private static async Task<List<string>> RolesDeAsync(CaeManagerDbContext contexto, Guid usuarioId) =>
        await (from ur in contexto.UserRoles
               join r in contexto.Roles on ur.RoleId equals r.Id
               where ur.UserId == usuarioId
               select r.Name!).ToListAsync();

    private ArnesAlta Arnes(Guid usuarioId, Guid origen, ActorAuditoria? actor = null)
    {
        var usuario = new UsuarioDelOperador(usuarioId, origen);
        var tenantActual = new TenantSegunAmbito(origen);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Como producción (ConfiguracionDeContexto): la transacción explícita del
                // Command tiene que convivir con la estrategia de reintento.
                npgsql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null);
            })
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(actor ?? ActorAuditoria.Normal(usuarioId))),
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        var contexto = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);

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

        return new ArnesAlta(servicios.BuildServiceProvider(), contexto, usuario, tenantActual, _operador.Id);
    }

    /// <summary>
    /// Handler de producción sobre un contexto de runtime. El catálogo real va envuelto para poder
    /// provocar un fallo a mitad del alta; sin tocar sus ganchos, delega sin cambiar nada.
    /// </summary>
    private sealed class ArnesAlta(
        ServiceProvider servicios, CaeManagerDbContext contexto, ICurrentUserService usuario,
        ITenantActual tenantActual, Guid operadorTenantId) : IAsyncDisposable
    {
        private readonly IServiceScope _ambito = servicios.CreateScope();

        public CatalogoIncorporacionCartera Catalogo { get; } = new(contexto, usuario);
        public Func<Guid, Task>? AntesDeIncorporar { get; set; }
        public bool GuardarSegundaEnTenantEquivocado { get; set; }
        public int CarterasGuardadas { get; private set; }

        public CrearUsuarioCommandHandler Handler()
        {
            var sp = _ambito.ServiceProvider;
            var cuentas = new GestionCuentasUsuarioIdentity(
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<PuertaAccesoDatos>(),
                sp.GetRequiredService<DirectorioUsuariosTenant>(),
                contexto);
            return new CrearUsuarioCommandHandler(
                cuentas, usuario, tenantActual, new CatalogoConGanchos(this), new TransaccionDeComando(contexto));
        }

        public async ValueTask DisposeAsync()
        {
            _ambito.Dispose();
            await servicios.DisposeAsync();
            await contexto.DisposeAsync();
        }

        private sealed class CatalogoConGanchos(ArnesAlta arnes) : ICatalogoIncorporacionCartera
        {
            private ICatalogoIncorporacionCartera Real => arnes.Catalogo;

            public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(
                Guid operadorTenantId, CancellationToken cancellationToken = default) =>
                Real.ObtenerAsignablesAsync(operadorTenantId, cancellationToken);

            public async Task<ResultadoIncorporacionCartera> IncorporarAsync(
                Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId,
                CancellationToken cancellationToken = default)
            {
                if (arnes.AntesDeIncorporar is { } gancho) await gancho(propietarioTenantId);
                return await Real.IncorporarAsync(propietarioTenantId, operadorTenantId, asignacionOperacionId, usuarioId, cancellationToken);
            }

            public async Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
            {
                if (arnes.GuardarSegundaEnTenantEquivocado && arnes.CarterasGuardadas == 1)
                {
                    using (AmbitoTenantExplicito.Establecer(arnes.OperadorTenantId))
                        return await Real.GuardarDetectandoCarreraAsync(cancellationToken);
                }

                var guardado = await Real.GuardarDetectandoCarreraAsync(cancellationToken);
                if (guardado) arnes.CarterasGuardadas++;
                return guardado;
            }

            public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(
                Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
                Real.ObtenerCandidatosAsync(operadorTenantId, usuarioId, cancellationToken);
            public Task<IReadOnlyList<CarteraVivaDeOperacion>> ObtenerCarterasVivasAsync(Guid o, Guid? p, CancellationToken c = default) => Real.ObtenerCarterasVivasAsync(o, p, c);
            public Task<IReadOnlyList<OperacionConPrincipal>> ObtenerOperacionesDondeEsPrincipalAsync(Guid o, Guid u, CancellationToken c = default) => Real.ObtenerOperacionesDondeEsPrincipalAsync(o, u, c);
            public Task<bool> ApagarPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default) => Real.ApagarPrincipalAsync(o, op, u, c);
            public Task<bool> EncenderPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default) => Real.EncenderPrincipalAsync(o, op, u, c);
            public Task<ResultadoRelevoPrincipal> RelevarPrincipalAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default) => Real.RelevarPrincipalAsync(p, o, op, u, c);
            public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(
                Guid asignacionOperacionId, CancellationToken cancellationToken = default) =>
                Real.ObtenerOperacionVigenteAsync(asignacionOperacionId, cancellationToken);
            public Task<ResultadoIncorporacionCartera> IncorporarAsync(
                SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default) =>
                Real.IncorporarAsync(solicitud, cancellationToken);
            public Task RetirarAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default) =>
                Real.RetirarAsync(solicitud, cancellationToken);
            public Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(
                Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
                Real.ObtenerCarteraUniversalAsync(operadorTenantId, usuarioId, cancellationToken);
            public Task<bool> RetirarCarteraUniversalAsync(
                Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, Guid actorUsuarioId,
                CancellationToken cancellationToken = default) =>
                Real.RetirarCarteraUniversalAsync(propietarioTenantId, operadorTenantId, usuarioId, actorUsuarioId, cancellationToken);
            public void DescartarPendientes() => Real.DescartarPendientes();
            public Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(
                IReadOnlyCollection<Guid> asignacionCarteraIds, CancellationToken cancellationToken = default) =>
                Real.FiltrarCarterasVigentesAsync(asignacionCarteraIds, cancellationToken);
        }

        private Guid OperadorTenantId => operadorTenantId;
    }

    private CaeManagerDbContext ContextoPropietario()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _operador.Id });
    }

    /// <summary>Como el <c>TenantActual</c> de la web: el ámbito explícito manda sobre el de la sesión.</summary>
    private sealed class TenantSegunAmbito(Guid tenantDeSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeSesion;
    }

    /// <summary>El Administrador del Operador CAE, en su Tenant de origen.</summary>
    private sealed class UsuarioDelOperador(Guid usuarioId, Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(Roles.Administrador);
        public Task<string?> ObtenerRolEfectivoAsync() =>
            Task.FromResult<string?>(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito != origen ? null : Roles.Administrador);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
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
