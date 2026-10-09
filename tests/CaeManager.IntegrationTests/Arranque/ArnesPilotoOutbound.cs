using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Fonts;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// El arranque con la siembra del piloto Outbound, sobre una base nueva y con la
/// composición real de la aplicación: <c>AddApplication</c> + <c>AddInfrastructure</c>
/// y las piezas que aporta la capa Web (<c>TenantActual</c>,
/// <c>CurrentUserService</c>, el proveedor de autenticación del circuito y el
/// accesor de contexto HTTP), con el tráfico autenticado como
/// <c>cae_app_runtime</c> y el <c>DiskFileStorageService</c> real sobre un
/// directorio temporal. Es lo que hace falta para que la autoverificación mida
/// con la identidad que construye ella misma, por el mismo camino que en el
/// arranque: un doble del usuario o del alcance mediría otra cosa.
/// </summary>
internal sealed class ArnesPilotoOutbound : IAsyncDisposable
{
    public const string CorreoDePrueba = "ensayo@destino.example";

    private readonly ArnesDeArranqueRuntime _base;
    private readonly ServiceProvider _servicios;

    private ArnesPilotoOutbound(ArnesDeArranqueRuntime @base, ServiceProvider servicios, string directorioAlmacen)
    {
        _base = @base;
        _servicios = servicios;
        DirectorioAlmacen = directorioAlmacen;
    }

    public IServiceProvider Servicios => _servicios;
    public string DirectorioAlmacen { get; }
    public IServiceScopeFactory FabricaDeAmbitos => _servicios.GetRequiredService<IServiceScopeFactory>();

    public static async Task<ArnesPilotoOutbound> CrearAsync()
    {
        // La misma fuente que registra CaeManager.Web al arrancar: PDFsharp la resuelve de forma global.
        GlobalFontSettings.FontResolver ??= new CaeManager.Web.Reportes.EmbeddedFontResolver();

        // Solo para crear, migrar y, al final, eliminar la base.
        var @base = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var directorio = Path.Combine(Path.GetTempPath(), "piloto-outbound-" + Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CaeManagerDb"] = @base.CadenaPropietario,
            ["ConnectionStrings:CaeManagerDbRuntime"] = BaseDatosPostgresDePruebas.CadenaComoRuntime(@base.CadenaPropietario),
            // WebApplication.CreateBuilder() arranca en Production: sin esto, el registro
            // de Data Protection se niega por falta de certificado, que aquí no se prueba.
            ["DataProtection:PermitirClavesSinCifrar"] = "true",
            ["AlmacenamientoArchivos:Ruta"] = directorio,
        });

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

        // Lo que aporta la capa Web, con sus clases reales.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddScoped<AuthenticationStateProvider, CaeManager.Web.Services.ProveedorAutenticacionRevalidada>();
        builder.Services.AddScoped<IClienteActivoSeleccionado, CaeManager.Web.Services.ClienteActivoSeleccionado>();
        builder.Services.AddScoped<ICurrentUserService, CaeManager.Web.Services.CurrentUserService>();
        builder.Services.AddScoped<IActorAuditoria, CaeManager.Web.Services.ActorAuditoriaDesdeSesion>();
        builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();

        // No se arranca el host: los servicios de fondo de Infrastructure no llegan a ejecutarse.
        return new ArnesPilotoOutbound(@base, builder.Services.BuildServiceProvider(), directorio);
    }

    public async ValueTask DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await _base.DisposeAsync();
        if (Directory.Exists(DirectorioAlmacen))
            Directory.Delete(DirectorioAlmacen, recursive: true);
    }

    /// <summary>La configuración de la siembra; <paramref name="extra"/> añade o pisa claves.</summary>
    public static IConfiguration Configurar(
        DateOnly? fechaDemostracion, bool activo = true, params (string Clave, string? Valor)[] extra)
    {
        var valores = new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true",
            [OpcionesPilotoOutbound.ClaveActivo] = activo ? "true" : "false",
            [OpcionesPilotoOutbound.ClaveFechaDemostracion] = fechaDemostracion?.ToString("yyyy-MM-dd"),
        };
        foreach (var (clave, valor) in extra)
            valores[clave] = valor;

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    /// <summary>La fecha de demostración de los tests: cinco días por delante, así que hoy es D−5.</summary>
    public static DateOnly FechaDemostracion() => DiaDeNegocio.Hoy().AddDays(5);

    public async Task<PilotoOutboundSeeder.Resultado?> SembrarAsync(
        IConfiguration configuracion, IHostEnvironment? entorno = null,
        Func<IFileStorageService, IFileStorageService>? envolverAlmacen = null, CancellationToken cancellationToken = default)
    {
        using var ambito = _servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var almacen = sp.GetRequiredService<IFileStorageService>();

        return await PilotoOutboundSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            envolverAlmacen?.Invoke(almacen) ?? almacen,
            configuracion, entorno ?? EntornoDePrueba.Desarrollo, NullLogger.Instance, cancellationToken);
    }

    public Task<PilotoOutboundAutoverificacion.Informe> MedirAsync(IConfiguration configuracion) =>
        PilotoOutboundAutoverificacion.MedirAsync(FabricaDeAmbitos, OpcionesPilotoOutbound.Leer(configuracion, DiaDeNegocio.Hoy()));

    public async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarAsync()
    {
        // LIMITACIÓN CONOCIDA DEL ARNÉS, no de la retirada (la misma de SiembraDemoDireccionAdministrativaTests):
        // con claves de protección de datos efímeras, RELEER columnas cifradas por el contexto de bootstrap lanza
        // CryptographicException. El canal de plataforma de T2 lleva una credencial cifrada; se borra antes
        // (ExecuteDelete no lee) para que la retirada se mida en todo lo demás. Con el binario real (claves
        // persistentes) esa lectura queda para el ensayo en local.
        await ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            return await b.CanalesGestionDocumental.IgnoreQueryFilters().Where(c => ids.Contains(c.TenantId)).ExecuteDeleteAsync();
        });

        using var ambito = _servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        return await PilotoOutboundRetirada.RetirarLoteAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            () => sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear(),
            sp.GetRequiredService<IFileStorageService>(), NullLogger.Instance);
    }

    /// <summary>Una lectura o escritura sin usuario dentro del ámbito de un Tenant, con el contexto de tráfico (rol restringido).</summary>
    public async Task<T> EnTenantAsync<T>(Guid tenantId, Func<CaeManagerDbContext, IServiceProvider, Task<T>> trabajo)
    {
        using var ambito = _servicios.CreateScope();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await trabajo(ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(), ambito.ServiceProvider);
    }

    /// <summary>Con la identidad administrativa de bootstrap: ve todos los Tenants (para contar, no para medir pantallas).</summary>
    public async Task<T> ComoBootstrapAsync<T>(Func<CaeManagerDbContext, Task<T>> lectura)
    {
        using var ambito = _servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await lectura(bootstrap);
    }

    public Task<Guid> TenantIdAsync(string nombre) =>
        ComoBootstrapAsync(b => b.Tenants.Where(t => t.Nombre == nombre).Select(t => t.Id).SingleAsync());

    /// <summary>El paso del arranque que va entre la siembra y la autoverificación.</summary>
    public Task BackfillAsync() =>
        ComoBootstrapAsync(async b =>
        {
            await AsignacionesOperativasBackfillSeeder.SeedAsync(b, NullLogger.Instance);
            return 0;
        });

    public int FicherosEnElAlmacen() =>
        Directory.Exists(DirectorioAlmacen)
            ? Directory.EnumerateFiles(DirectorioAlmacen, "*", SearchOption.AllDirectories).Count()
            : 0;

    /// <summary>
    /// Lo que existe del piloto, contado con la identidad de bootstrap y sin filtros
    /// (también lo eliminado): Tenants, cuentas, filas por tabla y ficheros del almacén.
    /// </summary>
    public async Task<Dictionary<string, int>> RecuentoAsync()
    {
        var recuento = await ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var ids = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            var cuentas = CuentasPilotoOutbound.Locales.Todas.ToList();

            return new Dictionary<string, int>
            {
                ["Tenants del piloto"] = ids.Count,
                ["Tenants en total"] = await b.Tenants.CountAsync(),
                ["Cuentas del piloto"] = await b.Users.IgnoreQueryFilters().CountAsync(u => cuentas.Contains(u.Email!)),
                ["Cuentas en total"] = await b.Users.IgnoreQueryFilters().CountAsync(),
                ["Empresas"] = await b.Empresas.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Centros"] = await b.Centros.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Trabajadores"] = await b.Trabajadores.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Asignaciones"] = await b.Asignaciones.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Documentos"] = await b.Documentos.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Contactos de agenda"] = await b.ContactosAgenda.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Asignaciones de Operación"] = await b.AsignacionesOperacion.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.PropietarioTenantId)),
                ["Asignaciones de Cartera"] = await (
                    from cartera in b.AsignacionesCartera.IgnoreQueryFilters()
                    join operacion in b.AsignacionesOperacion.IgnoreQueryFilters() on cartera.AsignacionOperacionId equals operacion.Id
                    where ids.Contains(operacion.PropietarioTenantId)
                    select cartera.Id).CountAsync(),
            };
        });

        recuento["Ficheros en el almacén"] = FicherosEnElAlmacen();
        return recuento;
    }

    /// <summary>
    /// Ejecuta una lectura como una cuenta cualquiera, por el mismo mecanismo que
    /// la autoverificación (contexto HTTP con la identidad que emite la fábrica de
    /// claims) pero sin sus guardas: existe solo aquí, en los tests, para mirar lo
    /// que ve una cuenta que la autoverificación se niega a usar.
    /// </summary>
    public async Task<T> ComoCuentaAsync<T>(string nombreTenantDeLaCuenta, string email, Func<IServiceProvider, Task<T>> lectura)
    {
        var tenantId = await TenantIdAsync(nombreTenantDeLaCuenta);

        await using var ambito = _servicios.CreateAsyncScope();
        var sp = ambito.ServiceProvider;

        ClaimsPrincipal identidad;
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var cuenta = await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"El test no encuentra la cuenta {email}.");
            identidad = await sp.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(cuenta);
        }

        var accesor = sp.GetRequiredService<IHttpContextAccessor>();
        accesor.HttpContext = new DefaultHttpContext { User = identidad, RequestServices = sp };
        try
        {
            return await lectura(sp);
        }
        finally
        {
            accesor.HttpContext = null;
        }
    }
}
