using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
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
/// Asignar y retirar Tenants beneficiarios enteros de la cartera de un Gestor CAE que ya existe
/// (<see cref="AsignarCarteraGestorCaeCommand"/>) contra PostgreSQL real, autenticando como
/// <c>cae_app_runtime</c> (RLS siempre aplica), con la estrategia de reintento de producción, los
/// interceptores de producción y el catálogo, el candado de cartera y las lecturas de Identity reales.
///
/// <para>
/// Lo que solo esta capa puede probar: que cada cartera se escribe con la política RLS de las
/// carteras (Tenant beneficiario) dentro de una sola transacción iniciada desde el Tenant del
/// Operador CAE; que un fallo a mitad no deja nada; que retirar cierra la cartera, revoca la solicitud
/// que la creó y borra la fila heredada solo si no queda otra cartera; que el predicado excluye la
/// operación caducada, la raíz y la de otro Operador CAE; y que la auditoría separa Actor real y
/// Usuario simulado.
/// </para>
/// </summary>
public class AsignarCarteraGestorCaeBajoRuntimeTests : IAsyncLifetime
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
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Guid _otroCoordinador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _gestorAjeno = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario(_operador.Id);
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

        var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

        void Cuenta(Guid id, Guid tenantId, string rol, Guid? coordinadorId = null)
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = id,
                TenantId = tenantId,
                UserName = $"{id:N}@caemanager.local",
                NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                Email = $"{id:N}@caemanager.local",
                NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                NombreCompleto = rol,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CoordinadorUsuarioId = coordinadorId,
            });
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_administrador, _operador.Id, Roles.Administrador);
        Cuenta(_coordinador, _operador.Id, Roles.CoordinadorCae);
        Cuenta(_otroCoordinador, _operador.Id, Roles.CoordinadorCae);
        Cuenta(_gestor, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_administradorAjeno, _otroOperador.Id, Roles.Administrador);
        Cuenta(_gestorAjeno, _otroOperador.Id, Roles.GestorCae);

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Asignar_dos_Tenants_a_un_Gestor_CAE_existente_los_escribe_juntos_y_audita_al_actor_real_y_al_simulado()
    {
        // Sesión Privilegiada: el Actor real es Soporte TALVEG y el Usuario simulado, el Administrador.
        var soporte = Guid.NewGuid();
        var actor = new ActorAuditoria(soporte, _administrador, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());

        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioA.Id, _beneficiarioB.Id], null), actor);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        await using var propietario = ContextoPropietario(_operador.Id);
        var carteras = await propietario.AsignacionesCartera.AsNoTracking().Where(c => c.UsuarioId == _gestor).ToListAsync();
        carteras.Select(c => c.PropietarioTenantId).Should().BeEquivalentTo([_beneficiarioA.Id, _beneficiarioB.Id]);
        carteras.Should().AllSatisfy(c =>
        {
            c.OperadorTenantId.Should().Be(_operador.Id);
            c.Rol.Should().Be(Roles.GestorCae, "la Operación nunca concede roles de Propiedad");
            c.Estado.Should().Be(EstadoAsignacion.Vigente);
            c.AmbitoRelacionClienteId.Should().BeNull("cartera del Tenant entero");
            c.VigenciaHasta.Should().BeNull();
        });
        (await propietario.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == _gestor)).Should().Be(2);
        (await RolesDeAsync(propietario, _gestor)).Should().BeEquivalentTo(new[] { Roles.GestorCae }, "su rol no cambia");

        var registros = await propietario.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadTipo == nameof(AsignacionCartera)).ToListAsync();
        registros.Select(r => r.EntidadId).Should().BeEquivalentTo(carteras.Select(c => c.Id));
        registros.Should().AllSatisfy(r =>
        {
            r.UsuarioId.Should().Be(_administrador, "el Usuario simulado");
            r.ActorRealUsuarioId.Should().Be(soporte, "quien estaba de verdad detrás del teclado");
        });
    }

    [Fact]
    public async Task Un_Coordinador_CAE_asigna_a_su_equipo_pero_no_a_un_Gestor_CAE_de_otro_equipo()
    {
        var propio = await Ejecutar(_coordinador, Roles.CoordinadorCae, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioA.Id], null));
        propio.EsExitoso.Should().BeTrue(propio.EsFallido ? propio.Error.Codigo : null);

        var ajeno = await Ejecutar(_otroCoordinador, Roles.CoordinadorCae, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioB.Id], null));
        ajeno.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);

        await using var propietario = ContextoPropietario(_operador.Id);
        (await propietario.AsignacionesCartera.Where(c => c.UsuarioId == _gestor).Select(c => c.PropietarioTenantId).ToListAsync())
            .Should().Equal(_beneficiarioA.Id);
    }

    [Fact]
    public async Task Retirar_cierra_solo_esa_cartera_borra_solo_su_fila_heredada_y_revoca_la_solicitud_que_la_creo()
    {
        var solicitudId = await SembrarCarteraPorSolicitudAceptadaAsync(_gestor, _beneficiarioA);
        (await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioB.Id], null))).EsExitoso.Should().BeTrue();

        var soporte = Guid.NewGuid();
        var actor = new ActorAuditoria(soporte, _administrador, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, null, [_beneficiarioA.Id]), actor);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        await using var propietario = ContextoPropietario(_operador.Id);
        var carteras = await propietario.AsignacionesCartera.AsNoTracking().Where(c => c.UsuarioId == _gestor).ToListAsync();
        carteras.Single(c => c.PropietarioTenantId == _beneficiarioA.Id).Estado.Should().NotBe(EstadoAsignacion.Vigente);
        carteras.Single(c => c.PropietarioTenantId == _beneficiarioB.Id).Estado.Should().Be(EstadoAsignacion.Vigente);

        var delegadas = await (from f in propietario.AsignacionesOperadorDelegado
                               join d in propietario.DelegacionesTenant on f.DelegacionTenantId equals d.Id
                               where f.UsuarioId == _gestor
                               select d.TenantClienteId).ToListAsync();
        delegadas.Should().Equal(_beneficiarioB.Id);

        var solicitud = await propietario.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
        solicitud.Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Revocada);
        solicitud.RevocadaPorUsuarioId.Should().Be(_administrador);

        (await RolesDeAsync(propietario, _gestor)).Should().BeEquivalentTo(new[] { Roles.GestorCae }, "retirar una cartera no toca la cuenta");
        var cierre = await propietario.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadTipo == nameof(AsignacionCartera) && r.EntidadId == carteras.Single(c => c.PropietarioTenantId == _beneficiarioA.Id).Id)
            .OrderByDescending(r => r.Id).FirstAsync();
        cierre.UsuarioId.Should().Be(_administrador);
        cierre.ActorRealUsuarioId.Should().Be(soporte);
    }

    [Fact]
    public async Task Retirar_conserva_la_fila_heredada_si_le_queda_un_reparto_por_Cliente_empresarial_en_ese_Tenant()
    {
        await SembrarCarteraPorSolicitudAceptadaAsync(_gestor, _beneficiarioA);
        await SembrarCarteraDeClienteAsync(_gestor, _beneficiarioA);

        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, null, [_beneficiarioA.Id]));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        await using var propietario = ContextoPropietario(_operador.Id);
        var vigentes = await propietario.AsignacionesCartera.AsNoTracking()
            .Where(c => c.UsuarioId == _gestor && c.Estado == EstadoAsignacion.Vigente).ToListAsync();
        vigentes.Should().ContainSingle().Which.AmbitoRelacionClienteId.Should().NotBeNull("el reparto por Cliente empresarial no se toca");
        (await propietario.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == _gestor)).Should().Be(1,
            "sin su fila heredada el Tenant dejaría de aparecerle");
    }

    [Fact]
    public async Task No_se_retira_lo_que_el_Gestor_CAE_no_tiene_entero_ni_la_cartera_de_otro()
    {
        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, null, [_beneficiarioA.Id]));

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoEnCartera);
    }

    [Fact]
    public async Task Si_una_cartera_se_anula_a_mitad_se_deshacen_las_anteriores_y_la_retirada()
    {
        await SembrarCarteraPorSolicitudAceptadaAsync(_gestor, _beneficiarioA);

        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioB.Id], [_beneficiarioA.Id]),
            antesDeIncorporar: async propietarioTenantId =>
            {
                // La operación del Tenant a asignar se suspende entre la validación y la escritura,
                // cuando la retirada del otro Tenant ya se guardó dentro de la transacción.
                await using var propietario = ContextoPropietario(_operador.Id);
                await propietario.AsignacionesOperacion
                    .Where(o => o.PropietarioTenantId == propietarioTenantId && !o.EsRaiz)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, EstadoAsignacion.Suspendida));
            });

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoAsignable);
        await using var check = ContextoPropietario(_operador.Id);
        var carteras = await check.AsignacionesCartera.AsNoTracking().Where(c => c.UsuarioId == _gestor).ToListAsync();
        carteras.Should().ContainSingle("no se creó la de B").Which.Estado.Should().Be(EstadoAsignacion.Vigente, "la retirada de A se deshizo");
        (await check.AsignacionesOperadorDelegado.CountAsync(a => a.UsuarioId == _gestor)).Should().Be(1);
        (await check.SolicitudesIncorporacionCartera.AsNoTracking().SingleAsync())
            .Estado.Should().Be(EstadoSolicitudIncorporacionCartera.Aceptada);
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

        var resultado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [tenant], null));

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoAsignable);
        await using var propietario = ContextoPropietario(_operador.Id);
        (await propietario.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestor)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_Administrador_de_otro_Operador_CAE_no_asigna_ni_toca_la_cuenta_de_este()
    {
        var resultado = await Ejecutar(_administradorAjeno, Roles.Administrador, _otroOperador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioAjeno.Id], null));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorNoAlcanzable,
            "la cuenta es de otro Tenant y no es un Operador CAE externo delegado suyo");
        await using var propietario = ContextoPropietario(_operador.Id);
        (await propietario.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestor)).Should().BeFalse();
    }

    [Fact]
    public async Task No_se_asigna_cartera_a_una_cuenta_de_Propiedad_ni_a_un_Gestor_CAE_desactivado()
    {
        var alAdministrador = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_coordinador, [_beneficiarioA.Id], null));
        alAdministrador.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.CuentaNoEsGestorCae);

        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            await propietario.Users.Where(u => u.Id == _gestor)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(100)));
        }

        var alDesactivado = await Ejecutar(_administrador, Roles.Administrador, _operador.Id,
            new AsignarCarteraGestorCaeCommand(_gestor, [_beneficiarioA.Id], null));
        alDesactivado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.GestorDesactivado);
    }

    // ── Siembra ───────────────────────────────────────────────────────────

    /// <summary>Una cartera universal con su fila heredada y la solicitud aceptada que la creó.</summary>
    private async Task<Guid> SembrarCarteraPorSolicitudAceptadaAsync(Guid gestor, Tenant beneficiario)
    {
        await using var contexto = ContextoPropietario(_operador.Id);
        var ahora = DateTime.UtcNow;
        var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.PropietarioTenantId == beneficiario.Id && !o.EsRaiz);
        var vinculo = await contexto.DelegacionesTenant.SingleAsync(d => d.TenantClienteId == beneficiario.Id && d.TenantConsultoraId == _operador.Id);

        var cartera = AsignacionCartera.Externa(operacion, gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
        var fila = new AsignacionOperadorDelegado(vinculo.Id, gestor, Roles.GestorCae);
        var solicitud = SolicitudIncorporacionCartera.Crear(operacion, gestor, "Por favor", ahora);
        solicitud.Aceptar(_coordinador, cartera, fila.Id, ahora);

        contexto.AsignacionesCartera.Add(cartera);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
        contexto.SolicitudesIncorporacionCartera.Add(solicitud);
        await contexto.SaveChangesAsync();
        return solicitud.Id;
    }

    /// <summary>Un reparto por Cliente empresarial concreto del Tenant beneficiario.</summary>
    private async Task SembrarCarteraDeClienteAsync(Guid gestor, Tenant beneficiario)
    {
        Guid empresaId;
        await using (var enBeneficiario = ContextoPropietario(beneficiario.Id))
        {
            var empresa = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
            enBeneficiario.Empresas.Add(empresa);
            await enBeneficiario.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var contexto = ContextoPropietario(beneficiario.Id);
        var ahora = DateTime.UtcNow;
        var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.PropietarioTenantId == beneficiario.Id && !o.EsRaiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
            operacion, gestor, Roles.GestorCae, AmbitoAsignacion.DeRelacionCliente(empresaId), ahora, null, ahora));
        await contexto.SaveChangesAsync();
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private static async Task<List<string>> RolesDeAsync(CaeManagerDbContext contexto, Guid usuarioId) =>
        await (from ur in contexto.UserRoles
               join r in contexto.Roles on ur.RoleId equals r.Id
               where ur.UserId == usuarioId
               select r.Name!).ToListAsync();

    /// <summary>Handler de producción sobre un contexto de runtime, en una sesión del Operador CAE.</summary>
    private async Task<CaeManager.Domain.Common.Result> Ejecutar(
        Guid usuarioId, string rolDeSesion, Guid origen, AsignarCarteraGestorCaeCommand comando,
        ActorAuditoria? actor = null, Func<Guid, Task>? antesDeIncorporar = null)
    {
        var usuario = new UsuarioDeSesion(usuarioId, rolDeSesion, origen);
        var tenantActual = new TenantSegunAmbito(origen);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Como producción: la transacción explícita del Command convive con la estrategia de reintento.
                npgsql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null);
            })
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(actor ?? ActorAuditoria.Normal(usuarioId))),
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
        var catalogo = new CatalogoIncorporacionCartera(contexto, usuario);

        var handler = new AsignarCarteraGestorCaeCommandHandler(
            usuario, directorio, directorio,
            antesDeIncorporar is null ? catalogo : new CatalogoConGancho(catalogo, antesDeIncorporar),
            new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto));
        return await handler.Handle(comando, CancellationToken.None);
    }

    private sealed class CatalogoConGancho(CatalogoIncorporacionCartera real, Func<Guid, Task> antes) : ICatalogoIncorporacionCartera
    {
        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCandidatosAsync(o, u, c);
        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(Guid o, CancellationToken c = default) => real.ObtenerAsignablesAsync(o, c);
        public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(Guid id, CancellationToken c = default) => real.ObtenerOperacionVigenteAsync(id, c);
        public Task<ResultadoIncorporacionCartera> IncorporarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.IncorporarAsync(s, c);

        public async Task<ResultadoIncorporacionCartera> IncorporarAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default)
        {
            await antes(p);
            return await real.IncorporarAsync(p, o, op, u, c);
        }

        public Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCarteraUniversalAsync(o, u, c);
        public Task<bool> RetirarCarteraUniversalAsync(Guid p, Guid o, Guid u, Guid a, CancellationToken c = default) => real.RetirarCarteraUniversalAsync(p, o, u, a, c);
        public Task RetirarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.RetirarAsync(s, c);
        public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken c = default) => real.GuardarDetectandoCarreraAsync(c);
        public void DescartarPendientes() => real.DescartarPendientes();
        public Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(IReadOnlyCollection<Guid> ids, CancellationToken c = default) => real.FiltrarCarterasVigentesAsync(ids, c);
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

    /// <summary>La sesión de una cuenta del Operador CAE, en su Tenant de origen.</summary>
    private sealed class UsuarioDeSesion(Guid usuarioId, string rol, Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolEfectivoAsync() =>
            Task.FromResult<string?>(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito != origen ? null : rol);
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
