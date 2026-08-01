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
        services.AddScoped<TenantRlsConnectionInterceptor>();
        // Sin estado y sin dependencias: una sola instancia sirve.
        services.AddSingleton<ConcurrenciaOptimistaInterceptor>();

        services.AddDbContext<CaeManagerDbContext>((serviceProvider, options) =>
        {
            // CaeManagerDbRuntime es opcional y, ausente (el caso de hoy en
            // todos los entornos), cae en la misma cadena de siempre — cero
            // cambio de comportamiento. Solo tras provisionar el rol
            // restringido de RUNBOOK-RLS.md tiene sentido configurarla: es el
            // rol que hace que las políticas RLS de la migración
            // HabilitarRlsPostgres empiecen a restringir de verdad (RLS no
            // aplica al propietario de la tabla ni a un superusuario). Las
            // migraciones (Program.cs) siguen conectando con CaeManagerDb sin
            // pasar por aquí — ese rol necesita DDL que el de runtime no tiene.
            var cadena = configuration.GetConnectionString("CaeManagerDbRuntime")
                ?? configuration.GetConnectionString("CaeManagerDb");

            options.UseNpgsql(cadena, npgsql =>
            {
                // Las migraciones viven en su propio ensamblado, separado de
                // Infrastructure — EF Core descubre las migraciones escaneando
                // el ensamblado entero, así que conviene que sea uno dedicado.
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");

                // Contra un servidor de red hay errores transitorios que con
                // un archivo local sencillamente no existían.
                npgsql.EnableRetryOnFailure();
            });

            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditoriaInterceptor>(),
                serviceProvider.GetRequiredService<TenantSelladoInterceptor>(),
                serviceProvider.GetRequiredService<TenantRlsConnectionInterceptor>(),
                serviceProvider.GetRequiredService<ConcurrenciaOptimistaInterceptor>());
        });

        services
            .AddIdentityCore<ApplicationUser>(opciones =>
            {
                opciones.Password.RequiredLength = 10;
                opciones.Password.RequireNonAlphanumeric = false;
                opciones.User.RequireUniqueEmail = true;
                opciones.SignIn.RequireConfirmedAccount = false;

                // Bloqueo temporal por intentos fallidos — solo surte efecto
                // porque Login.razor pasa lockoutOnFailure: true (hallazgo
                // P0-2 de docs/business/MATURITY_REVIEW.md: fuerza bruta sin
                // fricción). Ventana corta: frena un ataque de credenciales
                // sin dejar fuera medio día a un usuario legítimo que
                // tropieza con su gestor de contraseñas. La desactivación
                // manual de usuarios (LockoutEnd = MaxValue, Usuarios.razor)
                // sigue funcionando igual: es el mismo mecanismo con ventana
                // indefinida.
                opciones.Lockout.MaxFailedAccessAttempts = 5;
                opciones.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                opciones.Lockout.AllowedForNewUsers = true;
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
        services.AddScoped<CaeManager.Domain.Tenants.ITenantRepository, TenantRepository>();
        services.AddScoped<IDelegacionTenantRepository, DelegacionTenantRepository>();
        services.AddScoped<IAsignacionOperadorDelegadoRepository, AsignacionOperadorDelegadoRepository>();
        services.AddScoped<IPreferenciaDashboardUsuarioRepository, PreferenciaDashboardUsuarioRepository>();
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

        var opcionesS3 = new AlmacenamientoS3Options();
        configuration.GetSection(AlmacenamientoS3Options.SeccionConfiguracion).Bind(opcionesS3);
        services.Configure<AlmacenamientoS3Options>(configuration.GetSection(AlmacenamientoS3Options.SeccionConfiguracion));

        // Scoped en los dos casos (no Singleton): dependen de ITenantActual,
        // que es scoped — ver docs/MULTITENANCY.md § 4.6. AlmacenamientoS3:Activo
        // apagado por defecto (mismo patrón que Backups/DataProtection:Kms):
        // sin cuenta de AWS provisionada, sigue en disco local — ver DEPLOY.md.
        if (opcionesS3.EstaConfigurado)
        {
            services.AddScoped<IFileStorageService, S3FileStorageService>();
            services.AddHostedService<VerificacionAlmacenamientoS3HostedService>();
        }
        else
        {
            if (opcionesS3.Activo)
            {
                // Ruidoso a propósito, mismo criterio que DataProtection:Kms:
                // un despliegue que cree estar guardando en S3 y en realidad
                // siga en disco local (por una variable mal copiada) es peor
                // que uno que sepa que sigue en disco.
                Console.WriteLine(
                    "[AVISO] AlmacenamientoS3:Activo está en true pero faltan variables de AWS " +
                    "(AccessKeyId/SecretAccessKey/BucketName/Region) — los archivos siguen guardándose en disco local.");
            }

            services.AddScoped<IFileStorageService, DiskFileStorageService>();
        }

        services.Configure<LibreOfficeConversorWordPdfServiceOptions>(configuration.GetSection(LibreOfficeConversorWordPdfServiceOptions.SeccionConfiguracion));
        services.AddSingleton<IConversorWordPdfService, LibreOfficeConversorWordPdfService>();

        services.Configure<BackupsOptions>(configuration.GetSection(BackupsOptions.SeccionConfiguracion));
        services.AddHostedService<BackupHostedService>();

        // Política de retención RGPD: los plazos son decisión legal y viven en
        // configuración, no en el código (ver RetencionDatosOptions).
        services.Configure<RetencionDatosOptions>(
            configuration.GetSection(RetencionDatosOptions.SeccionConfiguracion));

        // Kill switch de la detección previa a clasificación de Documento
        // (ver DeteccionPreviaDocumentoOptions) — apagado por defecto hasta
        // que exista DPA de subencargado para datos de salud (P0-4 de
        // docs/business/MATURITY_REVIEW.md).
        services.Configure<DeteccionPreviaDocumentoOptions>(
            configuration.GetSection(DeteccionPreviaDocumentoOptions.SeccionConfiguracion));

        // Cola durable en PostgreSQL (P2 #22 de docs/business/MATURITY_REVIEW.md
        // — antes, Channel<T> en memoria: un reinicio del proceso perdía los
        // encargos pendientes sin dejar rastro). Scoped como cualquier otro
        // repositorio: el hosted service abre su propio scope de DI por
        // tenant/ciclo de sondeo, igual que ya hacía con la cola en memoria.
        services.AddScoped<ITrabajoAnalisisDocumentoRepository, TrabajoAnalisisDocumentoRepository>();
        services.AddHostedService<ProcesadorAnalisisDocumentoHostedService>();

        // Timeouts explícitos en todos los HttpClient de IA/Graph (P0-9 de
        // docs/business/MATURITY_REVIEW.md): el procesador de la cola de IA es
        // secuencial, así que una llamada colgada al proveedor detenía la cola
        // de TODOS los tenants durante los 100 s del default de HttpClient.
        // 60 s para el chat (interactivo: si tarda más, ya está roto para el
        // usuario) y 120 s para OCR/extracción sobre PDFs grandes. Retry y
        // circuit breaker (AddStandardResilienceHandler) quedan como P1-16 —
        // este Timeout es compatible: la resiliencia estándar se encadena a
        // estos mismos registros sin tocarlos.
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SeccionConfiguracion));
        services.AddHttpClient<IAsistenteIaService, AnthropicAsistenteIaService>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<IExtraccionTrabajadoresIaService, AnthropicExtraccionTrabajadoresIaService>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(120));
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
        services.AddHttpClient<MistralOcrDocumentAIProvider>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<MistralOcrDocumentAIProvider>());

        services.AddHttpClient<AnthropicDocumentAIProvider>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<AnthropicDocumentAIProvider>());

        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SeccionConfiguracion));
        services.AddHttpClient<GeminiDocumentAIProvider>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<GeminiDocumentAIProvider>());

        services.Configure<GraphEmailOptions>(configuration.GetSection(GraphEmailOptions.SeccionConfiguracion));
        services.AddHttpClient<IEmailService, GraphEmailService>(
            cliente => cliente.Timeout = TimeSpan.FromSeconds(30));

        // Comunicaciones (P2 #26): apagado por defecto — ver ComunicacionesOptions.
        services.Configure<ComunicacionesOptions>(configuration.GetSection(ComunicacionesOptions.SeccionConfiguracion));
        services.AddScoped<IExcelImportacionParser, ClosedXmlImportacionParser>();
        services.AddScoped<IPlantillaClientesService, ClosedXmlPlantillaClientesService>();
        services.AddScoped<IPlantillaDocumentosService, ClosedXmlPlantillaDocumentosService>();
        services.AddScoped<IPlantillaCombinadaService, ClosedXmlPlantillaCombinadaService>();

        return services;
    }
}
