using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.DesactivarGestorCaeConCartera;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// FS-25, revisión Codex de la PR #931 (hallazgos 3 y 4): pasar la cartera de un Gestor
/// CAE y desactivar su cuenta es una sola transacción. Contra PostgreSQL real como
/// <c>cae_app_runtime</c> (RLS y <see cref="TenantRlsConnectionInterceptor"/> reales), con
/// <see cref="TransaccionDeComando"/>, <see cref="GestionCuentasUsuarioIdentity"/> y el
/// <c>UserManager</c> de Identity de producción sobre el mismo contexto.
///
/// <para>
/// Lo que solo esta capa prueba: que el guardado de la cartera y el de Identity comparten
/// la transacción, así que un fallo al desactivar deshace también la cartera ya guardada; y
/// que la cartera se relee de la base al confirmar.
/// </para>
/// </summary>
public class DesactivarGestorCaeConCarteraBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario de prueba");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _destino = Guid.NewGuid();

    private readonly FalloAlGuardarCuenta _fallo = new();
    private ServiceProvider _servicios = null!;
    private Guid _uno;
    private Guid _dos;

    public async Task InitializeAsync()
    {
        await using (var contexto = ContextoPropietario())
        {
            await contexto.Database.MigrateAsync();

            var ahora = DateTime.UtcNow;
            contexto.Tenants.Add(_propietario);
            var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

            void Cuenta(Guid id, string rol)
            {
                contexto.Users.Add(new ApplicationUser
                {
                    Id = id,
                    TenantId = _propietario.Id,
                    UserName = $"{id:N}@caemanager.local",
                    NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                    Email = $"{id:N}@caemanager.local",
                    NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                    NombreCompleto = rol,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                });
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
            }

            Cuenta(_administrador, "Administrador");
            Cuenta(_gestor, "GestorCae");
            Cuenta(_destino, "GestorCae");

            var raiz = AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora);
            contexto.AsignacionesOperacion.Add(raiz);

            var uno = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, _gestor);
            var dos = Empresa.CrearComoCliente("Montajes del Norte", "A58818501", false, null, _gestor);
            contexto.Empresas.AddRange(uno, dos);
            foreach (var cliente in new[] { uno, dos })
                contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                    raiz, _gestor, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-1), vigenciaHasta: null, ahora, null));

            await contexto.SaveChangesAsync();
            _uno = uno.Id;
            _dos = dos.Id;
        }

        _servicios = Componer();
    }

    public async Task DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Pasa_la_cartera_entera_y_desactiva_la_cuenta()
    {
        var resultado = await EjecutarAsync([_uno, _dos]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        await using var contexto = ContextoPropietario();
        (await contexto.Empresas.AsNoTracking().Where(e => e.Id == _uno || e.Id == _dos).Select(e => e.EjecutivoUsuarioId).ToListAsync())
            .Should().AllBeEquivalentTo(_destino);
        (await CarteraVigenteAsync(contexto, _gestor)).Should().BeEmpty();
        (await CarteraVigenteAsync(contexto, _destino)).Should().BeEquivalentTo([_uno, _dos]);
        (await contexto.Users.AsNoTracking().SingleAsync(u => u.Id == _gestor)).EstaDesactivada(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    /// <summary>
    /// Revisión puente: la proyección Empresa del segundo Cliente empresarial ya apunta al
    /// destino, pero su Asignación de Cartera sigue siendo de quien se desactiva. Sin alinear la
    /// cartera, la relectura final la encontraba y el traspaso fallaba siempre.
    /// </summary>
    [Fact]
    public async Task Si_la_proyeccion_ya_apunta_al_destino_la_cartera_se_alinea_y_se_desactiva()
    {
        await using (var contexto = ContextoPropietario())
        {
            (await contexto.Empresas.SingleAsync(e => e.Id == _dos)).AsignarEjecutivo(_destino);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync([_uno, _dos]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        await using var comprobacion = ContextoPropietario();
        (await CarteraVigenteAsync(comprobacion, _gestor)).Should().BeEmpty();
        (await CarteraVigenteAsync(comprobacion, _destino)).Should().BeEquivalentTo([_uno, _dos]);
    }

    /// <summary>Hallazgo 3: la cartera se relee al confirmar; si no es la confirmada, no se toca nada.</summary>
    [Fact]
    public async Task Si_la_cartera_no_es_la_confirmada_no_se_escribe_nada()
    {
        var resultado = await EjecutarAsync([_uno]);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.CarteraCambiada);
        await AfirmarIntactoAsync();
    }

    /// <summary>
    /// Hallazgo 4: la cartera llega a guardarse (primer <c>SaveChanges</c>) y la
    /// desactivación de Identity falla después. La transacción deshace las dos.
    /// </summary>
    [Fact]
    public async Task Si_la_desactivacion_falla_despues_de_guardar_la_cartera_se_deshace_todo()
    {
        _fallo.Activo = true;

        var accion = () => EjecutarAsync([_uno, _dos]);

        await accion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*desactivación simulada*");
        _fallo.GuardadosDeCarteraAntesDelFallo.Should().BeGreaterThan(0, "control: la cartera sí llegó a guardarse dentro de la transacción");
        await AfirmarIntactoAsync();
    }

    /// <summary>
    /// El puerto por sí solo: una operación que guarda y después devuelve un fallo (no
    /// lanza) no deja nada escrito, y el contexto queda vacío para el siguiente Command.
    /// </summary>
    [Fact]
    public async Task La_transaccion_deshace_lo_guardado_si_la_operacion_devuelve_un_fallo()
    {
        await using var ambito = _servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var transaccion = ambito.ServiceProvider.GetRequiredService<ITransaccionDeComando>();

        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            contexto.NotificacionesUsuario.Add(new NotificacionUsuario(_gestor, "Prueba", "Guardada y deshecha."));
            await contexto.SaveChangesAsync(ct);
            return Domain.Common.Result.Fallo(Domain.Common.Error.Crear("Prueba.Fallo", "Falla después de guardar."));
        });

        resultado.EsFallido.Should().BeTrue();
        contexto.ChangeTracker.Entries().Should().BeEmpty("el siguiente Command del circuito no puede heredar los restos");
        await using var comprobacion = ContextoPropietario();
        (await comprobacion.NotificacionesUsuario.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private async Task<Domain.Common.Result> EjecutarAsync(IReadOnlyCollection<Guid> confirmados)
    {
        await using var ambito = _servicios.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<DesactivarGestorCaeConCarteraCommandHandler>(ambito.ServiceProvider);
        return await handler.Handle(new DesactivarGestorCaeConCarteraCommand(_gestor, _destino, confirmados), CancellationToken.None);
    }

    private async Task AfirmarIntactoAsync()
    {
        await using var contexto = ContextoPropietario();
        (await contexto.Empresas.AsNoTracking().Where(e => e.Id == _uno || e.Id == _dos).Select(e => e.EjecutivoUsuarioId).ToListAsync())
            .Should().AllBeEquivalentTo(_gestor);
        (await CarteraVigenteAsync(contexto, _gestor)).Should().BeEquivalentTo([_uno, _dos]);
        (await CarteraVigenteAsync(contexto, _destino)).Should().BeEmpty();
        (await contexto.NotificacionesUsuario.AsNoTracking().CountAsync()).Should().Be(0);
        (await contexto.Users.AsNoTracking().SingleAsync(u => u.Id == _gestor)).EstaDesactivada(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    private static Task<List<Guid?>> CarteraVigenteAsync(CaeManagerDbContext contexto, Guid usuarioId) =>
        contexto.AsignacionesCartera.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && c.Estado == EstadoAsignacion.Vigente)
            .Select(c => c.AmbitoRelacionClienteId)
            .ToListAsync();

    private ServiceProvider Componer()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var usuario = new CurrentUserServiceFalso(_administrador, "Administrador", tenantOrigenId: _propietario.Id);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<ICurrentUserService>(usuario);
        servicios.AddSingleton<ISesionPrivilegiadaActual, SesionPrivilegiadaAusente>();
        servicios.AddScoped<PuertaAccesoDatos>();

        // Con reintentos, como producción (ConfiguracionDeContexto): la transacción explícita
        // solo se admite dentro de CreateExecutionStrategy, y eso es lo que se prueba.
        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL").EnableRetryOnFailure())
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor(),
                _fallo));
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>();

        servicios.AddScoped<DirectorioUsuariosTenant>();
        servicios.AddScoped<IDirectorioDestinosCartera>(sp => sp.GetRequiredService<DirectorioUsuariosTenant>());
        servicios.AddScoped<IGestionCuentasUsuario, GestionCuentasUsuarioIdentity>();
        servicios.AddScoped<IEmpresaRepository, EmpresaRepository>();
        servicios.AddScoped<IConfiguracionIaDocumentoClienteRepository, ConfiguracionIaDocumentoClienteRepository>();
        servicios.AddScoped<INotificacionUsuarioRepository, NotificacionUsuarioRepository>();
        servicios.AddScoped<IAlcanceDatosService>(sp => new AlcanceDatosService(
            sp.GetRequiredService<CaeManagerDbContext>(), usuario, tenantActual, new SesionPrivilegiadaAusente()));
        servicios.AddScoped<IAsignacionesOperativasWriter, AsignacionesOperativasWriter>();
        servicios.AddScoped<ReasignadorCarteraCliente>();
        servicios.AddScoped<ITransaccionDeComando, TransaccionDeComando>();

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext ContextoPropietario()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Hace fallar el guardado que desactiva la cuenta (el de Identity), no el de la cartera,
    /// que va antes: así el fallo llega cuando ya hay algo escrito dentro de la transacción.
    /// </summary>
    private sealed class FalloAlGuardarCuenta : SaveChangesInterceptor
    {
        public bool Activo { get; set; }
        public int GuardadosDeCarteraAntesDelFallo { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entradas = eventData.Context!.ChangeTracker.Entries().ToList();
            if (Activo && entradas.Any(e => e.Entity is AsignacionCartera && e.State == EntityState.Added))
                GuardadosDeCarteraAntesDelFallo++;
            if (Activo && entradas.Any(e => e.Entity is ApplicationUser && e.State == EntityState.Modified))
                throw new InvalidOperationException("desactivación simulada: Identity no pudo guardar la cuenta.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
