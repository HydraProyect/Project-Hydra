using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence.Interceptors;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>El arranque tal como corre en producción, pero contra una base de pruebas.</b>
/// Un contenedor mínimo con el <c>DbContext</c> conectado <b>autenticando como
/// <c>cae_app_runtime</c></b>, su interceptor de sesión, e Identity encima.
///
/// <para>
/// Existe para responder una sola pregunta: ¿el arranque que queda funciona bajo
/// RLS efectiva sin que Identity ni los seeders tenant-scoped se rompan? Todo lo
/// anterior —que la conexión autentique y que RLS filtre— está demostrado aparte
/// (#259) y aquí se da por bueno.
/// </para>
///
/// <para>
/// <b>Fidelidad, no comodidad.</b> El <c>ITenantActual</c> de este arnés lee
/// <c>AmbitoTenantExplicito</c> y nada más, y eso <b>reproduce producción</b> en
/// vez de simplificarla: el <c>TenantActual</c> real devuelve el ámbito ambiental
/// <i>antes</i> de consultar claims o <c>HttpContext</c>, y en el arranque no hay
/// ninguna de las dos cosas. Sin esta pieza, el interceptor no fijaría
/// <c>app.tenant_id</c> al ámbito que establecen los seeders y todo fallaría
/// cerrado — que es el modo de fallo más fácil de confundir con "el dato no
/// existe".
/// </para>
/// </summary>
internal sealed class ArnesDeArranqueRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _servicios;

    private ArnesDeArranqueRuntime(ServiceProvider servicios, string cadenaPropietario)
    {
        _servicios = servicios;
        CadenaPropietario = cadenaPropietario;
    }

    /// <summary>La cadena del propietario, para migrar y para los controles negativos.</summary>
    internal string CadenaPropietario { get; }

    internal IServiceProvider Servicios => _servicios;

    /// <summary>
    /// Migra como propietario —las migraciones necesitan DDL que el rol
    /// restringido no tiene, igual que en producción— y devuelve el contenedor ya
    /// apuntando a la identidad de tráfico.
    /// </summary>
    internal static async Task<ArnesDeArranqueRuntime> CrearAsync(
        bool datosDePruebaActivos,
        bool segundoTenantActivo = false,
        ITenantActual? tenantActualPersonalizado = null,
        IActorAuditoria? actorAuditoriaPersonalizado = null,
        ICurrentUserService? currentUserServicePersonalizado = null)
    {
        var cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();

        {
            var construccion = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .Options;

            await using var contexto = new CaeManagerDbContext(
                construccion,
                new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
                new TenantActualDeArranque());

            await contexto.Database.MigrateAsync();
        }

        var servicios = new ServiceCollection();

        servicios.AddLogging();
        // Proveedor efimero en vez de AddDataProtection(): el real arrastra
        // almacenamiento de claves y logging propios, y su ciclo de vida no
        // sobrevive al del contenedor de un test. Los campos cifrados del dominio
        // se protegen igual; lo que no se ejercita es la persistencia de claves,
        // que no es lo que este arnes mide.
        servicios.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        servicios.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatosPrueba:Activo"] = datosDePruebaActivos ? "true" : "false",
                ["SegundoTenant:Activo"] = segundoTenantActivo ? "true" : "false",
                // Para FabricaContextoDeBootstrap: la misma identidad
                // administrativa que Program.cs usa para los seeders
                // cross-tenant (backfill, retirada) — sin esto, un test que
                // pida FabricaContextoDeBootstrap del arnés fallaría con
                // "Falta el connection string CaeManagerDb" en vez de
                // reproducir producción.
                ["ConnectionStrings:CaeManagerDb"] = cadenaPropietario,
            })
            .Build());

        // Las tres dependencias del interceptor. Sin sesión de usuario ni
        // workspace: es el arranque, no una petición — salvo que el
        // llamante pase un ITenantActual propio para simular justo eso (ver
        // PropagacionTenantRlsSinSesionTests: un Workspace operativo
        // derivado seleccionado devuelve un tenant DISTINTO del propietario
        // real de la cuenta de Identity que se está auditando).
        servicios.AddSingleton<ITenantActual>(tenantActualPersonalizado ?? new TenantActualDeArranque());
        servicios.AddSingleton<IClienteActivoSeleccionado>(new SinClienteActivo());
        // Sin usuario por defecto, como el arranque. Un test que mida una
        // política por persona (app.usuario_id, que el interceptor toma de
        // aquí) pasa el suyo — mismo patrón que tenantActualPersonalizado.
        servicios.AddSingleton<ICurrentUserService>(currentUserServicePersonalizado ?? new CurrentUserServiceFalso());
        // LOS CUATRO interceptores de produccion, no solo el de sesion. Montar
        // solo TenantRlsConnectionInterceptor dejaba las filas SIN TenantId
        // —lo sella TenantSelladoInterceptor— y cualquier escritura tenantizada
        // moria con 42501 contra su propia politica. El sintoma apuntaba a RLS y
        // la causa era el arnes: exactamente el fallo que la regla "el arnes debe
        // reproducir el cableado de produccion" existe para evitar.
        // El arranque no tiene sesión, pero un test que mida QUÉ AUTORÍA
        // queda escrita —la separación entre Actor real y Usuario simulado de
        // una Sesión Privilegiada, ADR-011 § 8.5— necesita un actor resuelto:
        // mismo patrón que tenantActualPersonalizado, y por el mismo motivo
        // (reproducir una condición real que el arranque no tiene, sin
        // desmontar el resto del cableado de producción).
        servicios.AddSingleton<IActorAuditoria>(actorAuditoriaPersonalizado ?? new ActorDeArranque());
        servicios.AddScoped<AuditoriaInterceptor>();
        servicios.AddScoped<TenantSelladoInterceptor>();
        servicios.AddScoped<TenantRlsConnectionInterceptor>();
        servicios.AddSingleton<ConcurrenciaOptimistaInterceptor>();

        servicios.AddDbContext<CaeManagerDbContext>((sp, opciones) =>
        {
            // Se reutiliza el cableado de produccion en vez de reconstruirlo:
            // ConfiguracionDeContexto existe justamente para que los dos contextos
            // no divergan, y montar la lista a mano aqui ya costo un 42501 cuya
            // causa estaba dos capas por encima de RLS.
            ConfiguracionDeContexto.Aplicar(
                opciones, sp, BaseDatosPostgresDePruebas.CadenaComoRuntime(cadenaPropietario));
        });

        // La identidad de bootstrap real (no una reconstrucción a mano en el
        // test): los seeders cross-tenant (AsignacionesOperativasBackfillSeeder,
        // y desde este incidente RetiradaTenantDemoService) tienen que correr
        // exactamente con esta fábrica, no con una equivalente hecha a mano —
        // ver CaeManager.Infrastructure.Persistence.FabricaContextoDeBootstrap.
        servicios.AddScoped<CaeManager.Infrastructure.Persistence.FabricaContextoDeBootstrap>();

        // Mismo registro que producción (InfrastructureServiceCollectionExtensions):
        // los jobs de fondo recorren los Tenants con esta vista antes de abrir un
        // AmbitoTenantExplicito por cada uno.
        servicios.AddScoped<CaeManager.Application.Tenants.ITenantsQueryContext>(
            sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>();

        return new ArnesDeArranqueRuntime(servicios.BuildServiceProvider(), cadenaPropietario);
    }

    public async ValueTask DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(CadenaPropietario);
    }

    /// <summary>
    /// Reproduce la resolución de tenant del arranque: el ámbito ambiental y nada
    /// más. Es lo que hace el <c>TenantActual</c> real cuando no hay claim ni
    /// <c>HttpContext</c>, que es exactamente la situación del arranque.
    /// </summary>
    private sealed class TenantActualDeArranque : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>
    /// El arranque no tiene sesión, así que la autoría queda sin resolver — que es
    /// lo que devuelve el <c>IActorAuditoria</c> real en ese momento.
    /// </summary>
    private sealed class ActorDeArranque : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.SinResolver);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.SinResolver;
    }

    /// <summary>Sin workspace ni sesión privilegiada: el arranque no tiene ninguno.</summary>
    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
