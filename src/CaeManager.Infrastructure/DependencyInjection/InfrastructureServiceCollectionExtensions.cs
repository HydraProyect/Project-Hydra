using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Importacion;
using Microsoft.AspNetCore.Authentication;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Backups;
using CaeManager.Infrastructure.Conversion;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.Email;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Infrastructure.MultiTenancy;
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
        services.AddScoped<TenantSelladoInterceptor>();

        services.AddDbContext<CaeManagerDbContext>((serviceProvider, options) =>
        {
            options.UseSqlite(configuration.GetConnectionString("CaeManagerDb"));
            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditoriaInterceptor>(),
                serviceProvider.GetRequiredService<TenantSelladoInterceptor>());
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
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        services.Configure<AzureAdOptions>(configuration.GetSection(AzureAdOptions.SeccionConfiguracion));
        services.AddTransient<IClaimsTransformation, RestriccionLoginLocalClaimsTransformation>();

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
        services.AddScoped<IDeteccionTrabajadorRepository, DeteccionTrabajadorRepository>();
        services.AddScoped<ITipoDocumentoRepository, TipoDocumentoRepository>();
        services.AddScoped<ITipoDocumentoCentroRepository, TipoDocumentoCentroRepository>();
        services.AddScoped<IConfiguracionIaDocumentoClienteRepository, ConfiguracionIaDocumentoClienteRepository>();
        services.AddScoped<IRevisionIaDocumentoRepository, RevisionIaDocumentoRepository>();
        services.AddScoped<IAprobacionDocumentoRepository, AprobacionDocumentoRepository>();
        services.AddScoped<IExtraccionIaCacheRepository, ExtraccionIaCacheRepository>();
        services.AddScoped<IAuditoriaExtraccionIaRepository, AuditoriaExtraccionIaRepository>();
        services.AddSingleton<IClasificadorDocumentoService, PdfSharpClasificadorDocumentoService>();
        services.AddSingleton<IExtractorTextoDigitalService, PdfSharpExtractorTextoDigitalService>();
#pragma warning disable CA1416
        services.AddSingleton<IRasterizadorPaginasPdfService, PdfToPngRasterizadorPaginasPdfService>();
#pragma warning restore CA1416
        services.AddScoped<INotificacionUsuarioRepository, NotificacionUsuarioRepository>();
        services.AddScoped<IDocumentoRepository, DocumentoRepository>();
        services.AddScoped<IAsignacionRepository, AsignacionRepository>();
        services.AddScoped<IVisitaRepository, VisitaRepository>();
        services.AddScoped<IVisitaTrabajadorRepository, VisitaTrabajadorRepository>();
        services.AddScoped<IVehiculoRepository, VehiculoRepository>();
        services.AddScoped<IParametroSistemaRepository, ParametroSistemaRepository>();
        services.AddScoped<ITarifaClienteRepository, TarifaClienteRepository>();
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IAlcanceDatosService, AlcanceDatosService>();

        services.Configure<DiskFileStorageServiceOptions>(configuration.GetSection(DiskFileStorageServiceOptions.SeccionConfiguracion));
        // Scoped (no Singleton): depende de ITenantActual, que es scoped —
        // ver docs/MULTITENANCY.md § 4.6.
        services.AddScoped<IFileStorageService, DiskFileStorageService>();

        services.Configure<LibreOfficeConversorWordPdfServiceOptions>(configuration.GetSection(LibreOfficeConversorWordPdfServiceOptions.SeccionConfiguracion));
        services.AddSingleton<IConversorWordPdfService, LibreOfficeConversorWordPdfService>();

        services.Configure<BackupsOptions>(configuration.GetSection(BackupsOptions.SeccionConfiguracion));
        services.AddHostedService<BackupHostedService>();

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SeccionConfiguracion));
        services.AddHttpClient<IAsistenteIaService, AnthropicAsistenteIaService>();
        services.AddHttpClient<IExtraccionTrabajadoresIaService, AnthropicExtraccionTrabajadoresIaService>();
        // IExtraccionMetadatosDocumentoIaService (Fase 38) ya no tiene una
        // implementación directa de Anthropic aquí — RouterExtraccionMetadatosDocumentoIaService
        // (Application) la satisface delegando en IDocumentAIRouterService,
        // registrada en ApplicationServiceCollectionExtensions.
        //
        // IDocumentAIProvider: registro por interfaz general (no un typed
        // client dedicado) — así IEnumerable<IDocumentAIProvider> recoge
        // todos los proveedores para la Factory (ver
        // docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2). El ORDEN de estos
        // registros importa: DocumentAIProviderFactory.ObtenerPorCapacidad
        // conserva el orden de registro, y DocumentAIRouterService usa el
        // primero de la lista como proveedor OCR sin reintento (a
        // diferencia de la extracción estructurada, que sí reintenta con
        // el segundo si el primero da poca confianza — ver Fase 41). Por
        // eso Mistral (proveedor OCR especializado, registrado primero) se
        // usa antes que Anthropic para OCR, mientras que para extracción
        // estructurada Anthropic sigue siendo el primario (Fase 38-40,
        // antes de tener claves reales) y Gemini el candidato de
        // reintento — cambiar cuál es "primario" para estructuración es
        // una decisión de benchmark, no algo que se cambie por tener una
        // clave nueva (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1).
        services.Configure<MistralOcrOptions>(configuration.GetSection(MistralOcrOptions.SeccionConfiguracion));
        services.AddHttpClient<MistralOcrDocumentAIProvider>();
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<MistralOcrDocumentAIProvider>());

        services.AddHttpClient<AnthropicDocumentAIProvider>();
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<AnthropicDocumentAIProvider>());

        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SeccionConfiguracion));
        services.AddHttpClient<GeminiDocumentAIProvider>();
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<GeminiDocumentAIProvider>());

        services.Configure<GraphEmailOptions>(configuration.GetSection(GraphEmailOptions.SeccionConfiguracion));
        services.AddHttpClient<IEmailService, GraphEmailService>();
        services.AddScoped<IExcelImportacionParser, ClosedXmlImportacionParser>();
        services.AddScoped<IPlantillaClientesService, ClosedXmlPlantillaClientesService>();
        services.AddScoped<IPlantillaDocumentosService, ClosedXmlPlantillaDocumentosService>();
        services.AddScoped<IPlantillaCombinadaService, ClosedXmlPlantillaCombinadaService>();

        return services;
    }
}
