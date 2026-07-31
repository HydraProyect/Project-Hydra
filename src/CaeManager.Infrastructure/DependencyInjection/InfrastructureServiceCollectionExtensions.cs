using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Evaluaciones;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Importacion;
using Microsoft.AspNetCore.Authentication;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using Amazon;
using Amazon.KeyManagementService;
using CaeManager.Infrastructure.Backups;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Conversion;
using CaeManager.Infrastructure.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.Email;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
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
        // Sin estado y sin dependencias: una sola instancia sirve.
        services.AddSingleton<ConcurrenciaOptimistaInterceptor>();

        // Motor elegido por configuración mientras dura la migración a
        // PostgreSQL — ver ProveedorBaseDatos para por qué esto es transitorio.
        var proveedor = LeerProveedor(configuration);

        services.AddDbContext<CaeManagerDbContext>((serviceProvider, options) =>
        {
            var cadena = configuration.GetConnectionString("CaeManagerDb");

            if (proveedor == ProveedorBaseDatos.PostgreSql)
            {
                options.UseNpgsql(cadena, npgsql =>
                {
                    // Las migraciones de PostgreSQL viven en su propio ensamblado:
                    // EF Core descubre las migraciones escaneando el ensamblado
                    // entero, así que dos juegos en el mismo sitio se pisarían.
                    npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");

                    // Contra un servidor de red hay errores transitorios que con
                    // un archivo local sencillamente no existían.
                    npgsql.EnableRetryOnFailure();
                });
            }
            else
            {
                options.UseSqlite(cadena);
            }

            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditoriaInterceptor>(),
                serviceProvider.GetRequiredService<TenantSelladoInterceptor>(),
                serviceProvider.GetRequiredService<ConcurrenciaOptimistaInterceptor>());
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

        var constructorDataProtection = services.AddDataProtection()
            .SetApplicationName("CaeManager")
            .PersistKeysToFileSystem(new DirectoryInfo(rutaClavesAbsoluta));

        // Cifrado en reposo de esas claves con AWS KMS. Ver
        // DataProtectionKmsOptions: sin esto, las claves viajan en claro en el
        // mismo backup que la base de datos que protegen.
        var opcionesKms = new DataProtectionKmsOptions();
        configuration.GetSection(DataProtectionKmsOptions.SeccionConfiguracion).Bind(opcionesKms);
        services.Configure<DataProtectionKmsOptions>(
            configuration.GetSection(DataProtectionKmsOptions.SeccionConfiguracion));

        if (opcionesKms.EstaConfigurado)
        {
            services.AddSingleton<IAmazonKeyManagementService>(_ => new AmazonKeyManagementServiceClient(
                opcionesKms.AccessKeyId, opcionesKms.SecretAccessKey, RegionEndpoint.GetBySystemName(opcionesKms.Region)));

            constructorDataProtection.Services.Configure<KeyManagementOptions>(opciones =>
                opciones.XmlEncryptor = new KmsXmlEncryptor(
                    new AmazonKeyManagementServiceClient(
                        opcionesKms.AccessKeyId, opcionesKms.SecretAccessKey, RegionEndpoint.GetBySystemName(opcionesKms.Region)),
                    opcionesKms.KeyId!));

            // Deja dicho en el arranque si el cifrado está realmente operativo:
            // una credencial mal copiada no se notaría hasta la siguiente
            // rotación de clave o al abrir una credencial guardada.
            services.AddHostedService<VerificacionKmsHostedService>();
        }
        else
        {
            // Ruidoso a propósito: un despliegue que cree estar cifrando y no
            // lo esté es peor que uno que sepa que no lo está. Se registra al
            // construir el contenedor, así que sale en el arranque.
            Console.WriteLine(
                "[AVISO] DataProtection:Kms no está configurado — las claves de Data Protection se guardan SIN CIFRAR. " +
                "Con Backups activo viajan en claro junto a la base de datos que protegen (ver RUNBOOK-CLAVES.md).");
        }

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
        services.AddSingleton<IRasterizadorPaginasPdfService, PdfToPngRasterizadorPaginasPdfService>();
        services.AddScoped<INotificacionUsuarioRepository, NotificacionUsuarioRepository>();
        services.AddScoped<IDocumentoRepository, DocumentoRepository>();
        services.AddScoped<IAsignacionRepository, AsignacionRepository>();
        services.AddScoped<IVisitaRepository, VisitaRepository>();
        services.AddScoped<IVisitaTrabajadorRepository, VisitaTrabajadorRepository>();
        services.AddScoped<IVehiculoRepository, VehiculoRepository>();
        services.AddScoped<IParametroSistemaRepository, ParametroSistemaRepository>();
        services.AddScoped<ITarifaClienteRepository, TarifaClienteRepository>();
        services.AddScoped<IProyectoRepository, ProyectoRepository>();
        services.AddScoped<IProyectoTecnicoRepository, ProyectoTecnicoRepository>();
        services.AddScoped<IDelegacionTenantRepository, DelegacionTenantRepository>();
        services.AddScoped<IAsignacionOperadorDelegadoRepository, AsignacionOperadorDelegadoRepository>();
        services.AddScoped<IRegistroActividadSoporteRepository, RegistroActividadSoporteRepository>();
        services.AddScoped<CaeManager.Domain.Retencion.ISolicitudPurgaRepository, SolicitudPurgaRepository>();
        services.AddScoped<CaeManager.Application.Retencion.DeteccionPurgaService>();
        services.AddScoped<CaeManager.Application.Retencion.EjecucionPurgaService>();
        services.AddScoped<IEvaluacionRepository, EvaluacionRepository>();
        services.AddScoped<IIncidenciaRepository, IncidenciaRepository>();
        services.AddScoped<IConversacionCorreoRepository, ConversacionCorreoRepository>();
        services.AddScoped<IMacroRespuestaRepository, MacroRespuestaRepository>();
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IAlcanceDatosService, AlcanceDatosService>();
        services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        // La clase concreta se registra además de la interfaz: las páginas de
        // administración necesitan sus listados, y los Commands solo la
        // comprobación de IDirectorioUsuariosService.
        services.AddScoped<DirectorioUsuariosTenant>();
        services.AddScoped<IDirectorioUsuariosService>(sp => sp.GetRequiredService<DirectorioUsuariosTenant>());

        services.Configure<DiskFileStorageServiceOptions>(configuration.GetSection(DiskFileStorageServiceOptions.SeccionConfiguracion));
        // Scoped (no Singleton): depende de ITenantActual, que es scoped —
        // ver docs/MULTITENANCY.md § 4.6.
        services.AddScoped<IFileStorageService, DiskFileStorageService>();

        services.Configure<LibreOfficeConversorWordPdfServiceOptions>(configuration.GetSection(LibreOfficeConversorWordPdfServiceOptions.SeccionConfiguracion));
        services.AddSingleton<IConversorWordPdfService, LibreOfficeConversorWordPdfService>();

        services.Configure<BackupsOptions>(configuration.GetSection(BackupsOptions.SeccionConfiguracion));
        services.AddHostedService<BackupHostedService>();

        // Política de retención RGPD: los plazos son decisión legal y viven en
        // configuración, no en el código (ver RetencionDatosOptions).
        services.Configure<RetencionDatosOptions>(
            configuration.GetSection(RetencionDatosOptions.SeccionConfiguracion));

        // La cola es singleton porque la comparten el productor (los Commands,
        // scoped) y el consumidor (el hosted service, singleton). Se registra
        // la clase concreta además de la interfaz: el procesador necesita su
        // lector, que no forma parte del contrato de encolado.
        services.AddSingleton<ColaAnalisisDocumentoEnMemoria>();
        services.AddSingleton<IColaAnalisisDocumento>(sp => sp.GetRequiredService<ColaAnalisisDocumentoEnMemoria>());
        services.AddHostedService<ProcesadorAnalisisDocumentoHostedService>();

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

    /// <summary>
    /// Lee <c>Database:Proveedor</c>. Por defecto SQLite: un despliegue que no
    /// diga nada tiene que seguir arrancando exactamente como hasta ahora.
    /// Un valor escrito mal no cae de vuelta en silencio a SQLite — apuntar sin
    /// querer a otro motor que el previsto es la clase de error que acaba
    /// creando una base de datos vacía en paralelo a la de verdad.
    /// </summary>
    private static ProveedorBaseDatos LeerProveedor(IConfiguration configuration)
    {
        var valor = configuration["Database:Proveedor"];

        if (string.IsNullOrWhiteSpace(valor))
            return ProveedorBaseDatos.Sqlite;

        if (!Enum.TryParse<ProveedorBaseDatos>(valor, ignoreCase: true, out var proveedor))
            throw new InvalidOperationException(
                $"Database:Proveedor tiene el valor '{valor}', que no es válido. " +
                $"Valores admitidos: {string.Join(", ", Enum.GetNames<ProveedorBaseDatos>())}.");

        return proveedor;
    }
}
