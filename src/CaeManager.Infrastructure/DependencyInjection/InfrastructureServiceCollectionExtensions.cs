using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Importacion;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Backups;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment entorno)
    {
        services.AddScoped<AuditoriaInterceptor>();

        services.AddDbContext<CaeManagerDbContext>((serviceProvider, options) =>
        {
            options.UseSqlite(configuration.GetConnectionString("CaeManagerDb"));
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditoriaInterceptor>());
        });

        services
            .AddIdentityCore<ApplicationUser>(opciones =>
            {
                opciones.Password.RequiredLength = 10;
                opciones.Password.RequireNonAlphanumeric = false;
                opciones.User.RequireUniqueEmail = true;
                opciones.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddSignInManager<SignInManager<ApplicationUser>>()
            .AddDefaultTokenProviders();

        // Sin persistir las claves, cada reinicio del proceso genera unas nuevas
        // y todo lo cifrado con las anteriores (credenciales de Empresa/Centro,
        // Fase 0/20) deja de poder descifrarse — silenciosamente, hasta que
        // alguien intenta abrir una credencial guardada. Ruta configurable para
        // apuntar a un volumen persistente en despliegues en contenedor (ver
        // DEPLOY.md); en desarrollo local, relativa al content root como el
        // resto de rutas de almacenamiento de la app.
        var rutaClavesDataProtection = configuration["DataProtection:RutaClaves"] ?? "App_Data/dataprotection-keys";
        var rutaClavesAbsoluta = Path.IsPathRooted(rutaClavesDataProtection)
            ? rutaClavesDataProtection
            : Path.Combine(entorno.ContentRootPath, rutaClavesDataProtection);

        services.AddDataProtection()
            .SetApplicationName("CaeManager")
            .PersistKeysToFileSystem(new DirectoryInfo(rutaClavesAbsoluta));

        services.AddScoped<IClienteRepository, ClienteRepository>();
        services.AddScoped<IEmpresaRepository, EmpresaRepository>();
        services.AddScoped<IEmpresaClienteRepository, EmpresaClienteRepository>();
        services.AddScoped<ICredencialAccesoEmpresaRepository, CredencialAccesoEmpresaRepository>();
        services.AddScoped<ISubcontrataRepository, SubcontrataRepository>();
        services.AddScoped<ISubcontrataClienteRepository, SubcontrataClienteRepository>();
        services.AddScoped<ISubcontrataEmpresaRepository, SubcontrataEmpresaRepository>();
        services.AddScoped<ICredencialAccesoSubcontrataRepository, CredencialAccesoSubcontrataRepository>();
        services.AddScoped<ICentroRepository, CentroRepository>();
        services.AddScoped<ITrabajadorRepository, TrabajadorRepository>();
        services.AddScoped<ITipoDocumentoRepository, TipoDocumentoRepository>();
        services.AddScoped<ITipoDocumentoCentroRepository, TipoDocumentoCentroRepository>();
        services.AddScoped<IDocumentoRepository, DocumentoRepository>();
        services.AddScoped<IAsignacionRepository, AsignacionRepository>();
        services.AddScoped<IVisitaRepository, VisitaRepository>();
        services.AddScoped<IVisitaTrabajadorRepository, VisitaTrabajadorRepository>();
        services.AddScoped<IVehiculoRepository, VehiculoRepository>();
        services.AddScoped<IParametroSistemaRepository, ParametroSistemaRepository>();
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IAlcanceDatosService, AlcanceDatosService>();

        services.Configure<DiskFileStorageServiceOptions>(configuration.GetSection(DiskFileStorageServiceOptions.SeccionConfiguracion));
        services.AddSingleton<IFileStorageService, DiskFileStorageService>();

        services.Configure<BackupsOptions>(configuration.GetSection(BackupsOptions.SeccionConfiguracion));
        services.AddHostedService<BackupHostedService>();

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SeccionConfiguracion));
        services.AddHttpClient<IAsistenteIaService, AnthropicAsistenteIaService>();
        services.AddScoped<IExcelImportacionParser, ClosedXmlImportacionParser>();
        services.AddScoped<IPlantillaClientesService, ClosedXmlPlantillaClientesService>();
        services.AddScoped<IPlantillaDocumentosService, ClosedXmlPlantillaDocumentosService>();
        services.AddScoped<IPlantillaCombinadaService, ClosedXmlPlantillaCombinadaService>();

        return services;
    }
}
