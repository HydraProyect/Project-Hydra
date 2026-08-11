using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
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
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using CaeManager.Infrastructure.Backups;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Conversion;
using CaeManager.Infrastructure.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.Firmas;
using CaeManager.Infrastructure.Email;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Infrastructure.Integraciones;
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
using Microsoft.Extensions.Http.Resilience;
using StackExchange.Redis;

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

        // Llavero compartido entre réplicas (P3-30 de docs/business/MATURITY_REVIEW.md):
        // reemplaza el XmlRepository de disco local configurado arriba por uno
        // en S3 — mismo patrón que el XmlEncryptor de KMS, la última
        // Configure<KeyManagementOptions> que se registra es la que gana.
        // Apagado por defecto: sin AWS provisionado, sigue en disco local
        // (correcto para una sola réplica, ver PersistKeysToFileSystem arriba).
        var opcionesDataProtectionS3 = new DataProtectionS3Options();
        configuration.GetSection(DataProtectionS3Options.SeccionConfiguracion).Bind(opcionesDataProtectionS3);
        services.Configure<DataProtectionS3Options>(
            configuration.GetSection(DataProtectionS3Options.SeccionConfiguracion));

        if (opcionesDataProtectionS3.EstaConfigurado)
        {
            constructorDataProtection.Services.Configure<KeyManagementOptions>(opciones =>
                opciones.XmlRepository = new S3XmlRepository(
                    new AmazonS3Client(
                        opcionesDataProtectionS3.AccessKeyId, opcionesDataProtectionS3.SecretAccessKey,
                        RegionEndpoint.GetBySystemName(opcionesDataProtectionS3.Region)),
                    opcionesDataProtectionS3));

            services.AddHostedService<VerificacionDataProtectionS3HostedService>();
        }

        // Backplane de SignalR (P3-30 de docs/business/MATURITY_REVIEW.md).
        // AddSignalR() aquí y AddInteractiveServerComponents() en Program.cs
        // (Web) apuntan al mismo registro interno — el orden entre ambas
        // llamadas no importa. Apagado por defecto: sin Redis provisionado,
        // SignalR sigue con su backplane en memoria del proceso (correcto
        // para una sola réplica).
        var opcionesSignalRRedis = new SignalRRedisOptions();
        configuration.GetSection(SignalRRedisOptions.SeccionConfiguracion).Bind(opcionesSignalRRedis);
        services.Configure<SignalRRedisOptions>(
            configuration.GetSection(SignalRRedisOptions.SeccionConfiguracion));

        if (opcionesSignalRRedis.EstaConfigurado)
        {
            services.AddSignalR().AddStackExchangeRedis(opcionesSignalRRedis.CadenaConexion!, opciones =>
                opciones.Configuration.ChannelPrefix = RedisChannel.Literal("CaeManager"));

            services.AddHostedService<VerificacionSignalRRedisHostedService>();
        }

        // Primer conector de integración (P3-33 de docs/business/MATURITY_REVIEW.md
        // — Microsoft 365, correo bidireccional para Comunicaciones, ver
        // ARQUITECTURA-INTEGRACIONES.md § 12). Apagado por defecto: sin App
        // Registration de Entra ID, el endpoint de conectar buzón devuelve
        // un error explícito en vez de arrancar un flujo OAuth roto — el
        // resto de Comunicaciones (bandeja con datos sembrados) sigue
        // funcionando igual.
        var opcionesMicrosoft365 = new Microsoft365GraphOptions();
        configuration.GetSection(Microsoft365GraphOptions.SeccionConfiguracion).Bind(opcionesMicrosoft365);
        services.Configure<Microsoft365GraphOptions>(configuration.GetSection(Microsoft365GraphOptions.SeccionConfiguracion));

        services.AddHttpClient<CaeManager.Application.Integraciones.IMicrosoft365GraphClient, Microsoft365GraphClient>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(30));

        if (opcionesMicrosoft365.EstaConfigurado)
        {
            services.AddHostedService<IngestaWebhookHostedService>();
            services.AddHostedService<RenovacionSuscripcionWebhookHostedService>();
        }

        // Segundo conector de mensajería: WhatsApp Cloud API (Meta). Mismo
        // patrón "inerte por defecto": sin AppSecret/VerifyToken no se
        // registra el consumidor y el webhook rechaza todo. El cliente HTTP
        // se registra siempre (las pantallas de configuración lo necesitan
        // para validar el alta aunque el webhook aún no esté configurado).
        var opcionesWhatsApp = new WhatsAppCloudApiOptions();
        configuration.GetSection(WhatsAppCloudApiOptions.SeccionConfiguracion).Bind(opcionesWhatsApp);
        services.Configure<WhatsAppCloudApiOptions>(configuration.GetSection(WhatsAppCloudApiOptions.SeccionConfiguracion));

        services.AddHttpClient<CaeManager.Application.Integraciones.IWhatsAppCloudApiClient, WhatsAppCloudApiClient>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(30));

        if (opcionesWhatsApp.EstaConfigurado)
            services.AddHostedService<IngestaWebhookWhatsAppHostedService>();

        services.AddScoped<IClienteRepository, ClienteRepository>();
        services.AddScoped<IEmpresaRepository, EmpresaRepository>();
        services.AddScoped<IEmpresaClienteRepository, EmpresaClienteRepository>();
        services.AddScoped<ICredencialAccesoEmpresaRepository, CredencialAccesoEmpresaRepository>();
        services.AddScoped<ISubcontrataRepository, SubcontrataRepository>();
        services.AddScoped<IVerificacionExternaSubcontrataRepository, VerificacionExternaSubcontrataRepository>();
        services.AddScoped<ISubcontrataClienteRepository, SubcontrataClienteRepository>();
        services.AddScoped<ISubcontrataEmpresaRepository, SubcontrataEmpresaRepository>();
        services.AddScoped<ICredencialAccesoSubcontrataRepository, CredencialAccesoSubcontrataRepository>();
        services.AddScoped<ICentroRepository, CentroRepository>();
        services.AddScoped<ICanalGestionDocumentalRepository, CanalGestionDocumentalRepository>();
        services.AddScoped<ITrabajadorRepository, TrabajadorRepository>();
        services.AddScoped<IDeteccionTrabajadorRepository, DeteccionTrabajadorRepository>();
        services.AddScoped<ITipoDocumentoRepository, TipoDocumentoRepository>();
        services.AddScoped<ITipoDocumentoCentroRepository, TipoDocumentoCentroRepository>();
        services.AddScoped<IConfiguracionIaDocumentoClienteRepository, ConfiguracionIaDocumentoClienteRepository>();
        services.AddScoped<IRevisionIaDocumentoRepository, RevisionIaDocumentoRepository>();
        services.AddScoped<IAprobacionDocumentoRepository, AprobacionDocumentoRepository>();
        services.AddScoped<IFirmaDigitalDocumentoRepository, FirmaDigitalDocumentoRepository>();
        services.AddScoped<IVerificacionDocumentoOficialRepository, VerificacionDocumentoOficialRepository>();
        services.AddScoped<IExtraccionIaCacheRepository, ExtraccionIaCacheRepository>();
        services.AddScoped<IAuditoriaExtraccionIaRepository, AuditoriaExtraccionIaRepository>();
        services.AddSingleton<IClasificadorDocumentoService, PdfSharpClasificadorDocumentoService>();
        services.AddSingleton<IExtractorTextoDigitalService, PdfSharpExtractorTextoDigitalService>();
        services.AddSingleton<IRasterizadorPaginasPdfService, PdfToPngRasterizadorPaginasPdfService>();
        services.AddSingleton(AlmacenConfianzaFirmas.AdministracionEspanola());
        services.AddSingleton<IVerificadorFirmaPdfService, VerificadorFirmaPdfService>();
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
        services.AddScoped<IFiltroGuardadoRepository, FiltroGuardadoRepository>();
        services.AddScoped<IRegistroActividadSoporteRepository, RegistroActividadSoporteRepository>();
        services.AddScoped<CaeManager.Domain.Retencion.ISolicitudPurgaRepository, SolicitudPurgaRepository>();
        services.AddScoped<CaeManager.Application.Retencion.DeteccionPurgaService>();
        services.AddScoped<CaeManager.Application.Retencion.EjecucionPurgaService>();
        services.AddScoped<IIncidenciaRepository, IncidenciaRepository>();
        services.AddScoped<IConversacionRepository, ConversacionRepository>();
        services.AddScoped<IMacroRespuestaRepository, MacroRespuestaRepository>();
        services.AddScoped<ISugerenciaVisitaCorreoRepository, SugerenciaVisitaCorreoRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IEventoConversacionRepository, EventoConversacionRepository>();
        services.AddScoped<CaeManager.Domain.Telemetria.IRegistroTiempoGestionRepository, RegistroTiempoGestionRepository>();
        services.AddScoped<CaeManager.Domain.Documentos.IAcreditacionDocumentoPlataformaRepository, AcreditacionDocumentoPlataformaRepository>();
        services.AddScoped<CaeManager.Domain.Cumplimiento.IAceptacionTerminosRepository, AceptacionTerminosRepository>();
        services.AddScoped<CaeManager.Domain.Reclamaciones.IReclamacionDocumentalRepository, ReclamacionDocumentalRepository>();
        services.AddScoped<CaeManager.Application.Reclamaciones.IReclamacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IContactosClienteService, ContactosClienteTenant>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.ISugerenciaGestionCorreoRepository, SugerenciaGestionCorreoRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IDetalleSugerenciaGestionCorreoRepository, DetalleSugerenciaGestionCorreoRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.ISolicitudPrioridadDocumentoRepository, SolicitudPrioridadDocumentoRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IClasificacionRuidoMensajeRepository, ClasificacionRuidoMensajeRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IClasificacionRuidoDetalleGestionRepository, ClasificacionRuidoDetalleGestionRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IUltimoResumenNotificacionPlataformaRepository, UltimoResumenNotificacionPlataformaRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IClasificacionRelevanciaCaeRepository, ClasificacionRelevanciaCaeRepository>();
        services.AddScoped<CaeManager.Domain.Gestiones.IGestionRepository, GestionRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.IConexionIntegracionRepository, ConexionIntegracionRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.ICredencialIntegracionRepository, CredencialIntegracionRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.ISuscripcionWebhookRepository, SuscripcionWebhookRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.IEventoWebhookRepository, EventoWebhookRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.ILineaWhatsAppRepository, LineaWhatsAppRepository>();
        services.AddScoped<CaeManager.Domain.Integraciones.IProveedorPlataformaCaeRepository, ProveedorPlataformaCaeRepository>();
        services.AddScoped<CaeManager.Domain.Comunicaciones.IContactoWhatsAppRepository, ContactoWhatsAppRepository>();
        services.AddScoped<CaeManager.Application.Integraciones.AccesoGraphService>();
        services.AddScoped<CaeManager.Application.Integraciones.IngestaWebhookService>();
        services.AddScoped<CaeManager.Application.Integraciones.IWebhookTenantResolver, WebhookTenantResolver>();
        services.AddScoped<CaeManager.Application.Integraciones.IWebhookWhatsAppTenantResolver, WebhookWhatsAppTenantResolver>();
        services.AddScoped<CaeManager.Application.Integraciones.IngestaWebhookWhatsAppService>();
        services.AddSingleton<CaeManager.Application.Integraciones.ISenalIngestaWhatsApp, SenalIngestaWhatsApp>();
        services.AddSingleton<CaeManager.Application.Comunicaciones.Eventos.INotificadorMensajesTiempoReal, NotificadorMensajesTiempoReal>();
        services.AddScoped<CaeManager.Domain.ApiKeys.IClaveApiRepository, ClaveApiRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Clientes.IClientesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Empresas.IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Subcontratas.ISubcontratasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Centros.ICentrosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Documentos.IDocumentosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Notificaciones.INotificacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Visitas.IVisitasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Vehiculos.IVehiculosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Auditoria.IAuditoriaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Tenants.ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Facturacion.IFacturacionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Proyectos.IProyectosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Retencion.IRetencionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Incidencias.IIncidenciasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Telemetria.ITelemetriaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.ApiKeys.IApiKeysQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Integraciones.IIntegracionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Gestiones.IGestionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IAlcanceDatosService, AlcanceDatosService>();
        services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        // Sin estado propio (abre una conexión Npgsql nueva por llamada) — una sola instancia sirve.
        services.AddSingleton<IEleccionLiderService, EleccionLiderPostgresService>();
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

        // Mismo criterio que el kill switch de arriba, para el flujo
        // "Actualizar documentación desde conversación" (ver
        // ExtraccionDocumentoAdjuntoOptions) — decisión del usuario, 2026-08-08.
        services.Configure<ExtraccionDocumentoAdjuntoOptions>(
            configuration.GetSection(ExtraccionDocumentoAdjuntoOptions.SeccionConfiguracion));

        // Cola durable en PostgreSQL (P2 #22 de docs/business/MATURITY_REVIEW.md
        // — antes, Channel<T> en memoria: un reinicio del proceso perdía los
        // encargos pendientes sin dejar rastro). Scoped como cualquier otro
        // repositorio: el hosted service abre su propio scope de DI por
        // tenant/ciclo de sondeo, igual que ya hacía con la cola en memoria.
        services.AddScoped<ITrabajoAnalisisDocumentoRepository, TrabajoAnalisisDocumentoRepository>();
        services.AddHostedService<ProcesadorAnalisisDocumentoHostedService>();

        // Fase F: aviso por hora (solo campana, sin correo en v1) de
        // gestiones urgentes de visita — reutiliza ObtenerBandejaGestorQuery,
        // sin interruptor de configuración propio (a diferencia del resumen
        // de alertas por correo, no manda nada fuera de la aplicación).
        services.AddHostedService<Visitas.VigilanciaVisitasUrgentesHostedService>();

        // Timeouts explícitos en todos los HttpClient de IA/Graph (P0-9 de
        // docs/business/MATURITY_REVIEW.md): el procesador de la cola de IA es
        // secuencial, así que una llamada colgada al proveedor detenía la cola
        // de TODOS los tenants durante los 100 s del default de HttpClient.
        // 60 s para el chat (interactivo: si tarda más, ya está roto para el
        // usuario) y 120 s para OCR/extracción sobre PDFs grandes.
        //
        // Reintento + circuit breaker (P1-16 de docs/business/MATURITY_REVIEW.md,
        // AddStandardResilienceHandler sobre Polly — mismo paquete que
        // ARQUITECTURA-INTEGRACIONES.md § 6.1 ya preveía para la futura
        // Plataforma de Integraciones). HttpClient.Timeout pasa a
        // Timeout.InfiniteTimeSpan: con el handler de resiliencia añadido,
        // ese timeout envolvería TODO el pipeline (reintentos incluidos) y
        // cortaría el primer reintento a medias — el límite real ahora lo
        // pone AplicarResilienciaHttp. MaxRetryAttempts baja a 2 (no el 3
        // por defecto): con el intento inicial serían hasta 4 intentos
        // completos, y para el cliente de chat (60 s/intento) eso son ~4
        // minutos de colgado — justo lo que el Timeout de P0-9 quería
        // evitar. TotalRequestTimeout al doble del intento acota el peor
        // caso a ~2x en vez de dejar que reintentos + backoff se disparen.
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SeccionConfiguracion));
        services.AddHttpClient<IAsistenteIaService, AnthropicAsistenteIaService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(60));
        services.AddHttpClient<IExtraccionTrabajadoresIaService, AnthropicExtraccionTrabajadoresIaService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(120));
        services.AddHttpClient<IDeteccionVisitaCorreoService, AnthropicDeteccionVisitaCorreoService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(60));
        services.AddHttpClient<CaeManager.Application.Comunicaciones.Deteccion.IDeteccionGestionCorreoService, AnthropicDeteccionGestionCorreoService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(60));
        services.AddHttpClient<CaeManager.Application.Comunicaciones.Deteccion.IDeteccionRelevanciaCaeService, AnthropicDeteccionRelevanciaCaeService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(60));
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
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<MistralOcrDocumentAIProvider>());

        services.AddHttpClient<AnthropicDocumentAIProvider>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<AnthropicDocumentAIProvider>());

        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SeccionConfiguracion));
        services.AddHttpClient<GeminiDocumentAIProvider>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<GeminiDocumentAIProvider>());

        // Graph SendMail no es idempotente: un reintento tras un 5xx/timeout
        // transitorio podría duplicar el correo si el envío ya se había
        // procesado del lado de Microsoft antes de que la respuesta se
        // perdiera. Riesgo aceptado (igual que en cualquier integración
        // estándar de este tipo) frente al beneficio de no fallar todo el
        // envío por un fallo de red puntual — no hay deduplicación aquí,
        // sería sobre-ingeniería para P1-16.
        services.Configure<GraphEmailOptions>(configuration.GetSection(GraphEmailOptions.SeccionConfiguracion));
        services.AddHttpClient<IEmailService, GraphEmailService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(30));

        // Resumen diario de alertas de vencimiento por correo (Issue #2):
        // apagado por defecto — ver AlertasPorCorreoOptions. Independiente
        // de si Graph:* está configurado (IEmailService ya degrada solo si
        // no lo está); este interruptor decide si el job en sí corre.
        var opcionesAlertasPorCorreo = new AlertasPorCorreoOptions();
        configuration.GetSection(AlertasPorCorreoOptions.SeccionConfiguracion).Bind(opcionesAlertasPorCorreo);
        services.Configure<AlertasPorCorreoOptions>(configuration.GetSection(AlertasPorCorreoOptions.SeccionConfiguracion));

        if (opcionesAlertasPorCorreo.Activo)
            services.AddHostedService<EnvioAlertasVencimientoHostedService>();

        // Comunicaciones (P2 #26): apagado por defecto — ver ComunicacionesOptions.
        services.Configure<ComunicacionesOptions>(configuration.GetSection(ComunicacionesOptions.SeccionConfiguracion));
        // Fase G: mismo "Comunicaciones" — ver ComunicacionesRemitenteOptions (Application.Common).
        services.Configure<ComunicacionesRemitenteOptions>(configuration.GetSection(ComunicacionesRemitenteOptions.SeccionConfiguracion));
        services.AddScoped<IExcelImportacionParser, ClosedXmlImportacionParser>();
        services.AddScoped<IPlantillaClientesService, ClosedXmlPlantillaClientesService>();
        services.AddScoped<IPlantillaDocumentosService, ClosedXmlPlantillaDocumentosService>();
        services.AddScoped<IPlantillaCombinadaService, ClosedXmlPlantillaCombinadaService>();

        return services;
    }

    /// <summary>
    /// Reintento + circuit breaker estándar (Polly vía
    /// <c>AddStandardResilienceHandler</c>, P1-16 de
    /// docs/business/MATURITY_REVIEW.md) para un HttpClient cuyo Timeout ya
    /// se dejó en <see cref="Timeout.InfiniteTimeSpan"/> por el llamador —
    /// el límite de tiempo real lo pone este método, no
    /// <c>HttpClient.Timeout</c> (que envolvería todo el pipeline,
    /// reintentos incluidos, y cortaría el primero a medias).
    ///
    /// <see cref="HttpTimeoutStrategyOptions.Timeout"/> de intento = el
    /// timeout que tenía cada cliente antes de P1-16 (no cambia cuánto
    /// puede tardar un intento). <c>MaxRetryAttempts</c> baja a 2 frente al
    /// valor por defecto (3) para no reintroducir el colgado largo que
    /// P0-9 cerró en el cliente de chat interactivo. El total se acota al
    /// doble del intento, y el <c>SamplingDuration</c> del circuit breaker
    /// al triple — la validación de <c>HttpStandardResilienceOptions</c>
    /// exige que sea al menos el doble del timeout de intento.
    /// </summary>
    private static IHttpStandardResiliencePipelineBuilder AplicarResilienciaHttp(this IHttpClientBuilder constructor, TimeSpan timeoutPorIntento) =>
        constructor.AddStandardResilienceHandler(opciones =>
        {
            opciones.AttemptTimeout.Timeout = timeoutPorIntento;
            opciones.TotalRequestTimeout.Timeout = timeoutPorIntento * 2;
            opciones.CircuitBreaker.SamplingDuration = timeoutPorIntento * 3;
            opciones.Retry.MaxRetryAttempts = 2;
        });
}
