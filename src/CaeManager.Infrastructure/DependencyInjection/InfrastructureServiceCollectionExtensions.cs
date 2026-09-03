using System.Net;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Importacion;
using Microsoft.AspNetCore.Authentication;
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.AlertasOperativas;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using CaeManager.Infrastructure.Comercial;
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
using CaeManager.Infrastructure.Plantillas;
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

        // La identidad administrativa del arranque, distinta de la del trafico
        // normal (ver FabricaContextoDeBootstrap).
        services.AddScoped<Persistence.FabricaContextoDeBootstrap>();

        services.AddDbContext<CaeManagerDbContext>((serviceProvider, options) =>
        {
            // CaeManagerDbRuntime es la conexión del TRÁFICO, con el rol
            // restringido cae_app_runtime (NOSUPERUSER NOBYPASSRLS, ver
            // deploy/bootstrap/roles-de-cluster.sql). Es el rol que hace que
            // las políticas de HabilitarRlsPostgres restrinjan de verdad: RLS
            // no aplica ni al propietario de la tabla ni a un superusuario, así
            // que conectar con CaeManagerDb —el rol propietario, `postgres` en
            // el compose de producción— deja RLS decorativa. Las migraciones
            // (Program.cs) siguen usando CaeManagerDb sin pasar por aquí: ese
            // rol necesita DDL que el de runtime no tiene.
            //
            // Fallo CERRADO. Antes esta caída al rol administrativo era
            // silenciosa y automática, así que una variable de entorno ausente
            // apagaba entera la segunda línea de aislamiento sin que nada lo
            // dijera — y el comentario que vivía aquí daba por hecho que la
            // conexión restringida no estaba puesta "en ningún entorno",
            // mientras deploy/local/.env.example la documenta como activa en
            // producción desde 2026-08-14. Dos afirmaciones contradictorias
            // sobre el mismo hecho es exactamente el estado en el que un
            // arranque tiene que negarse a adivinar.
            Persistence.ConfiguracionDeContexto.Aplicar(
                options, serviceProvider, ResolverCadenaDeTrafico(configuration, entorno));
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

        // Vigencia de los tokens de restablecimiento y de activación. NO es
        // cosmética: el correo de "olvidé mi contraseña" lleva desde siempre
        // escrito que el enlace "caduca en 60 minutos", y su constante decía
        // ser "el valor por defecto de DataProtectorTokenProvider". No lo es —
        // el de Identity es UN DÍA, y esto no se configuraba en ninguna parte.
        // Es decir, la aplicación prometía una hora y daba veinticuatro: un
        // enlace reenviado o filtrado seguía abriendo la cuenta al día
        // siguiente, cuando el usuario creía que había caducado hacía mucho.
        //
        // Se alinea la configuración con lo prometido, y no al revés: entre
        // cambiar el texto y cambiar la vigencia, lo correcto es que el sistema
        // haga lo que dice, no que diga lo que hace.
        //
        // Si se cambia aquí, hay que cambiarlo también en las dos constantes
        // que lo anuncian al usuario: OlvideContrasena.MinutosCaducidad y
        // Usuarios.MinutosCaducidadActivacion.
        services.Configure<DataProtectionTokenProviderOptions>(
            opciones => opciones.TokenLifespan = TimeSpan.FromMinutes(60));

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
                "El backup (scripts/backup-borg.sh) las incluye junto a la base de datos que protegen, así que viajan en claro también ahí (ver RUNBOOK-CLAVES.md).");
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

        // Retención del payload crudo de EventoWebhook (auditoría módulo 6):
        // registrado siempre, no solo si Microsoft365 está configurado —
        // redacta eventos de cualquier proveedor (WhatsApp incluido). Apagada
        // por defecto (ver RetencionEventosWebhookOptions), mismo criterio
        // que RetencionDatosOptions.
        services.Configure<RetencionEventosWebhookOptions>(
            configuration.GetSection(RetencionEventosWebhookOptions.SeccionConfiguracion));
        services.AddHostedService<RedaccionPayloadWebhookHostedService>();

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

        // F3b: IClienteRepository/ClienteRepository e
        // ISubcontrataRepository/SubcontrataRepository retirados — Cliente y
        // Subcontrata pasan a ser Empresa contraparte (EsPropia=false), ver
        // Empresa.CrearComoCliente/CrearComoSubcontrata. F3c (2026-08-28)
        // retiró además las tablas Clientes/Subcontratas y sus tipos de
        // dominio: ya no existe ninguna fuente legacy que registrar aquí.
        services.AddScoped<IEmpresaRepository, EmpresaRepository>();
        services.AddScoped<ICredencialAccesoEmpresaRepository, CredencialAccesoEmpresaRepository>();
        services.AddScoped<IVerificacionExternaSubcontrataRepository, VerificacionExternaSubcontrataRepository>();
        services.AddScoped<IRelacionEmpresarialRepository, RelacionEmpresarialRepository>();
        services.AddScoped<CaeManager.Application.RelacionesEmpresariales.IGuardDeCierreDeArista,
            CaeManager.Application.RelacionesEmpresariales.GuardDeCierreDeArista>();
        services.AddScoped<ICredencialAccesoSubcontrataRepository, CredencialAccesoSubcontrataRepository>();
        services.AddScoped<CaeManager.Domain.Blindaje42.ISolicitudCertificacionTgssRepository, SolicitudCertificacionTgssRepository>();
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
        services.AddScoped<IFirmaEnCampoDocumentoRepository, FirmaEnCampoDocumentoRepository>();
        services.AddScoped<IFirmaGuardadaUsuarioRepository, FirmaGuardadaUsuarioRepository>();
        services.AddScoped<ISelloEmpresaRepository, SelloEmpresaRepository>();
        services.AddScoped<IPlantillaDocumentoRepository, PlantillaDocumentoRepository>();
        services.AddScoped<IPlantillaDocumentoVersionRepository, PlantillaDocumentoVersionRepository>();
        services.AddScoped<IDocumentoGeneradoRepository, DocumentoGeneradoRepository>();
        services.AddScoped<ILoteGeneracionDocumentoRepository, LoteGeneracionDocumentoRepository>();
        services.AddScoped<IItemGeneracionDocumentoRepository, ItemGeneracionDocumentoRepository>();
        services.AddSingleton<IRellenadorPlantillaPdfService, RellenadorPlantillaPdfService>();
        services.AddSingleton<IExtractorCamposAcroFormService, ExtractorCamposAcroFormService>();
        services.AddScoped<IVerificacionDocumentoOficialRepository, VerificacionDocumentoOficialRepository>();
        services.AddScoped<IExtraccionIaCacheRepository, ExtraccionIaCacheRepository>();
        services.AddScoped<IAuditoriaExtraccionIaRepository, AuditoriaExtraccionIaRepository>();
        services.AddSingleton<IClasificadorDocumentoService, PdfSharpClasificadorDocumentoService>();
        services.AddSingleton<IExtractorTextoDigitalService, PdfSharpExtractorTextoDigitalService>();
        services.AddSingleton<IRasterizadorPaginasPdfService, PdfToPngRasterizadorPaginasPdfService>();
        services.AddSingleton(AlmacenConfianzaFirmas.AdministracionEspanola());
        services.AddSingleton<IVerificadorFirmaPdfService, VerificadorFirmaPdfService>();
        services.AddSingleton<IEstampadoFirmaEnCampoPdfService, EstampadoFirmaEnCampoPdfService>();
        services.AddSingleton<IConversorImagenSelloService, SkiaConversorImagenSelloService>();
        services.AddScoped<INotificacionUsuarioRepository, NotificacionUsuarioRepository>();
        services.AddScoped<IDocumentoRepository, DocumentoRepository>();
        services.AddScoped<IAsignacionRepository, AsignacionRepository>();
        services.AddScoped<IVisitaRepository, VisitaRepository>();
        services.AddScoped<IVisitaTrabajadorRepository, VisitaTrabajadorRepository>();
        services.AddScoped<IVehiculoRepository, VehiculoRepository>();
        services.AddScoped<IParametroSistemaRepository, ParametroSistemaRepository>();
        services.AddScoped<IEstadoAutomatizacionRepository, EstadoAutomatizacionRepository>();
        services.AddScoped<CaeManager.Infrastructure.Configuracion.IRegistroAutomatizacionesService, CaeManager.Infrastructure.Configuracion.RegistroAutomatizacionesService>();
        services.AddScoped<CaeManager.Domain.Importacion.IHistorialImportacionRepository, HistorialImportacionRepository>();
        services.AddScoped<CaeManager.Domain.Importacion.IOperacionImportacionRepository, OperacionImportacionRepository>();
        services.AddScoped<CaeManager.Domain.Reportes.IHistorialInformeRepository, HistorialInformeRepository>();
        services.AddScoped<CaeManager.Domain.BusquedaGlobal.IEventoRecienteUsuarioRepository, CaeManager.Infrastructure.Persistence.Repositories.EventoRecienteUsuarioRepository>();
        services.AddScoped<ITarifaClienteRepository, TarifaClienteRepository>();
        services.AddScoped<IProyectoRepository, ProyectoRepository>();
        services.AddScoped<IProyectoTecnicoRepository, ProyectoTecnicoRepository>();
        services.AddScoped<CaeManager.Domain.Tenants.ITenantRepository, TenantRepository>();
        services.AddScoped<IDelegacionTenantRepository, DelegacionTenantRepository>();
        services.AddScoped<CaeManager.Domain.VigilanciaNormativa.IAvisoRevisionNormativaRepository, CaeManager.Infrastructure.Persistence.Repositories.AvisoRevisionNormativaRepository>();
        services.AddScoped<CaeManager.Application.VigilanciaNormativa.IVigilanciaNormativaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
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
        services.AddScoped<CaeManager.Domain.Contactos.IContactoAgendaRepository, ContactoAgendaRepository>();
        services.AddScoped<CaeManager.Application.Contactos.IContactosAgendaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
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
        services.AddScoped<CaeManager.Domain.Integraciones.ISolicitudConexionMicrosoft365Repository, SolicitudConexionMicrosoft365Repository>();
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
        services.AddScoped<CaeManager.Application.Empresas.IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Subcontratas.ISubcontratasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Blindaje42.IBlindaje42QueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
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
        services.AddScoped<CaeManager.Application.Importacion.IImportacionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Reportes.IReportesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.BusquedaGlobal.IBusquedaGlobalQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
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
        services.AddScoped<CaeManager.Application.Plantillas.IPlantillasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<IAlcanceDatosService, AlcanceDatosService>();
        // Eje distinto del anterior a proposito: alcance de LECTURA frente a
        // autoridad para MODIFICAR. Ver IAutoridadAsignacionesService.
        services.AddScoped<IAutoridadAsignacionesService, AutoridadAsignacionesService>();
        services.AddScoped<CaeManager.Application.Operaciones.IOperacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        services.AddScoped<CaeManager.Application.Plataforma.IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // Revalida contra la base la sesión privilegiada que el token nombre —
        // sesión abierta y en ventana, concesión vigente, tenant en alcance —
        // y la memoiza por petición. Scoped y no singleton porque su respuesta
        // depende de la sesión: es del alcance del usuario, no del proceso.
        services.AddScoped<CaeManager.Application.Plataforma.ISesionPrivilegiadaActual,
            CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual>();
        // Raíz de confianza de bootstrap, y nada más: crear la PRIMERA
        // concesión, cuando todavía no hay ninguna de la que derivar autoridad.
        // Abrir una sesión ya no pasa por aquí — lo autoriza la concesión.
        //
        // Desde A2 la raíz es una PERSONA designada por el despliegue, no el
        // tenant de plataforma: ese tenant es también el operativo de la empresa,
        // así que cualquiera de sus miembros podía acuñarse autoridad.
        // Autoridad para EJERCER AdminPlataforma, distinta de la de adquirirla.
        // Dos preguntas separadas —sobre un tenant y globalmente— para que el
        // llamante declare qué alcance necesita en vez de recibir el más amplio
        // por omisión.
        services.AddScoped<CaeManager.Application.Plataforma.IAutorizacionAdminPlataforma,
            CaeManager.Infrastructure.Plataforma.AutorizacionAdminPlataformaPorConcesion>();

        // Matriz de auto-concesión: qué capacidad puede darse cada quien a sí
        // mismo. La raíz solo interviene en el acto fundacional; después, quien
        // tiene AdminPlataforma vigente puede darse SoporteLectura.
        services.AddScoped<CaeManager.Application.Plataforma.IAutorizacionAutoConcesion,
            CaeManager.Infrastructure.Plataforma.AutorizacionAutoConcesionPorMatriz>();
        services.AddScoped<CaeManager.Application.Plataforma.IRaizBootstrapPlataforma,
            CaeManager.Infrastructure.Plataforma.RaizBootstrapPorIdentidadDesignada>();
        services.AddScoped<CaeManager.Application.Plataforma.IPlataformaWriter,
            CaeManager.Infrastructure.Plataforma.PlataformaWriter>();
        // Escribe en el mismo DbContext scoped que el comando que lo invoca:
        // así la doble escritura entra en el SaveChanges del comando y es
        // transaccional sin transacción explícita (F1 del plan de migración).
        services.AddScoped<CaeManager.Application.Operaciones.IAsignacionesOperativasWriter,
            CaeManager.Infrastructure.Operaciones.AsignacionesOperativasWriter>();
        services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        // Sin estado propio (abre una conexión Npgsql nueva por llamada) — una sola instancia sirve.
        services.AddSingleton<IEleccionLiderService, EleccionLiderPostgresService>();
        // Horizonte 2.4: sin estado propio (delega en el Hub global de
        // Sentry ya inicializado por Program.cs), así que un singleton es
        // suficiente y evita crear una instancia por request.
        services.AddSingleton<IAlertaOperativa, SentryAlertaOperativa>();
        // La clase concreta se registra además de la interfaz: las páginas de
        // administración necesitan sus listados, y los Commands solo la
        // comprobación de IDirectorioUsuariosService.
        services.AddScoped<DirectorioUsuariosTenant>();
        services.AddScoped<IDirectorioUsuariosService>(sp => sp.GetRequiredService<DirectorioUsuariosTenant>());
        // Autoridad para vincular tenants: Administrador DEL CLIENTE DELEGANTE
        // (ADR-004 § 12.2). No consulta EsPlataforma a propósito — Hydra nunca
        // inicia una delegación (§ 11.1).
        services.AddScoped<CaeManager.Application.Tenants.IAutorizacionDelegacionTenant,
            AutorizacionDelegacionPorAdministradorDelCliente>();

        services.Configure<DiskFileStorageServiceOptions>(configuration.GetSection(DiskFileStorageServiceOptions.SeccionConfiguracion));

        // Único backend de almacenamiento de Documentos: disco local.
        //
        // El backend de S3 se retiró (auditoría del Módulo 2). Existía para
        // desbloquear multi-réplica, y eso no está en juego: producción corre un
        // solo contenedor caemanager-app sin réplicas, y la durabilidad ya la
        // cubre el respaldo Borg de /data/documentos contra el Storage Box.
        //
        // A cambio traía riesgo real: no cifraba el contenido —quedó fuera del
        // cifrado en reposo y del formato versionado por tenant que sí tiene el
        // disco—, no tenía ni una prueba frente a las once del backend de disco
        // (aislamiento entre tenants y manipulación incluidos), y usaba
        // credenciales estáticas compartidas por todos los tenants. Bastaba una
        // variable de entorno para cambiar el almacén a esas condiciones sin que
        // nada avisara.
        //
        // Si algún día hace falta almacenamiento compartido, el sustituto debe
        // nacer con el formato v2 de DiskFileStorageService, no retrofitado.
        //
        // Scoped, no Singleton: depende de ITenantActual, que es scoped — ver
        // docs/MULTITENANCY.md § 4.6.
        services.AddScoped<IFileStorageService, DiskFileStorageService>();

        // El despliegue real vive en un .env que no está en el repositorio, así
        // que desde aquí no se puede descartar que alguno siga declarando la
        // opción retirada. Un despliegue que crea estar guardando en S3 y en
        // realidad guarde en disco es peor que uno que lo sepa: mismo criterio
        // ruidoso que tenía el aviso anterior.
        if (configuration.GetValue<bool>("AlmacenamientoS3:Activo"))
        {
            Console.WriteLine(
                "[AVISO] AlmacenamientoS3:Activo sigue definido en la configuración, pero ese backend " +
                "se retiró: los documentos se guardan en disco local. Retira la variable del despliegue.");
        }

        services.Configure<LibreOfficeConversorWordPdfServiceOptions>(configuration.GetSection(LibreOfficeConversorWordPdfServiceOptions.SeccionConfiguracion));
        services.AddSingleton<IConversorWordPdfService, LibreOfficeConversorWordPdfService>();

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
        // HO-084-01 (REC-084, DEC-35): barrido automático de retención. Sin
        // interruptor de configuración propio, igual que arriba — con
        // política activa detecta y propone (como el botón "Buscar" manual);
        // sin política, diagnostica sin crear nada. Ver el comentario de
        // clase de RetencionHostedService para la interpretación de DEC-35
        // fijada aquí (no automatiza la destrucción, solo el barrido).
        services.AddHostedService<Retencion.RetencionHostedService>();
        // Mueve el estado de las asignaciones operativas según su vigencia. Es
        // requisito del esquema, no comodidad: los índices únicos parciales
        // filtran por Estado, así que una vigente caducada bloquearía el alta
        // de su sustituta (F1 del plan de migración).
        services.AddHostedService<Operaciones.ExpiracionAsignacionesHostedService>();

        // Tramo 1 bis del MVP-1 de formatos (corte mínimo, 2026-08-14):
        // sondeo del sumario diario del BOE. El cliente HTTP se registra
        // siempre (barato, sin llamadas hasta que alguien lo invoca); el
        // BackgroundService que sí llama a boe.es en cada ciclo se registra
        // solo si está activo — apagado por defecto (ver
        // VigilanciaNormativaBoeOptions), mismo motivo que Graph/WhatsApp:
        // manda algo fuera de la aplicación.
        services.AddHttpClient<CaeManager.Application.VigilanciaNormativa.IBoeSumarioClient, CaeManager.Infrastructure.VigilanciaNormativa.BoeSumarioClient>(
                cliente => cliente.BaseAddress = new Uri("https://www.boe.es/"))
            .AplicarResilienciaHttp(TimeSpan.FromSeconds(30));

        var opcionesVigilanciaNormativaBoe = new CaeManager.Infrastructure.VigilanciaNormativa.VigilanciaNormativaBoeOptions();
        configuration.GetSection(CaeManager.Infrastructure.VigilanciaNormativa.VigilanciaNormativaBoeOptions.SeccionConfiguracion)
            .Bind(opcionesVigilanciaNormativaBoe);
        services.Configure<CaeManager.Infrastructure.VigilanciaNormativa.VigilanciaNormativaBoeOptions>(
            configuration.GetSection(CaeManager.Infrastructure.VigilanciaNormativa.VigilanciaNormativaBoeOptions.SeccionConfiguracion));

        if (opcionesVigilanciaNormativaBoe.Activa)
            services.AddHostedService<CaeManager.Infrastructure.VigilanciaNormativa.VigilanciaNormativaBoeHostedService>();

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
        // Transitorio: AddHttpMessageHandler<T> resuelve una instancia por cliente HTTP.
        services.AddTransient<ContadorLlamadasProveedorIaHandler>();
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SeccionConfiguracion));
        services.AddHttpClient<IAsistenteIaService, AnthropicAsistenteIaService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(60));
        services.AddHttpClient<IExtraccionTrabajadoresIaService, AnthropicExtraccionTrabajadoresIaService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(120));
        services.AddHttpClient<IDeteccionVisitaCorreoService, AnthropicDeteccionVisitaCorreoService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(60));
        services.AddHttpClient<CaeManager.Application.Comunicaciones.Deteccion.IDeteccionGestionCorreoService, AnthropicDeteccionGestionCorreoService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(60));
        services.AddHttpClient<CaeManager.Application.Comunicaciones.Deteccion.IDeteccionRelevanciaCaeService, AnthropicDeteccionRelevanciaCaeService>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(60));
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
        //
        // ProveedorFalsoDocumentAI (Horizonte 1.6, ciclo documental E2E):
        // SIEMPRE antes que los reales, gateado por
        // "DocumentosIa:ProveedorFalsoActivo" — apagado en cualquier sitio
        // que no sea WebAppFixture (ver ese archivo), así que este bloque es
        // inerte en producción. Ir primero es lo que lo convierte en el
        // proveedor que de verdad usa DocumentAIRouterService, tanto para
        // OCR como para extracción estructurada — ver el comentario de esa
        // clase.
        if (configuration.GetValue<bool>("DocumentosIa:ProveedorFalsoActivo"))
            services.AddSingleton<IDocumentAIProvider, ProveedorFalsoDocumentAI>();

        services.Configure<MistralOcrOptions>(configuration.GetSection(MistralOcrOptions.SeccionConfiguracion));
        services.AddHttpClient<MistralOcrDocumentAIProvider>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<MistralOcrDocumentAIProvider>());

        services.AddHttpClient<AnthropicDocumentAIProvider>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<AnthropicDocumentAIProvider>());

        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SeccionConfiguracion));
        services.AddHttpClient<GeminiDocumentAIProvider>(
                cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .AplicarResilienciaHttpIa(TimeSpan.FromSeconds(120));
        services.AddScoped<IDocumentAIProvider>(sp => sp.GetRequiredService<GeminiDocumentAIProvider>());

        // Horizonte 1.7 ("Billing mínimo viable") — StripePaymentProvider es
        // el único IPaymentProvider real (ver su comentario sobre por qué no
        // hay una implementación de GoCardless todavía). Sin
        // StripeOptions.ApiKey/WebhookSecret configurados, cada método
        // devuelve un Result fallido controlado — mismo patrón "inerte por
        // defecto" que los proveedores de IA de arriba. No usa
        // AddHttpClient<T>: StripeClient gestiona su propio HttpClient
        // internamente (SDK oficial), igual que el resto de SDKs con typed
        // client propio de este proyecto (AWSSDK.*).
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SeccionConfiguracion));
        services.AddScoped<IPaymentProvider, StripePaymentProvider>();

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

    /// <summary>
    /// Resiliencia para un cliente que llama a un proveedor de IA de pago.
    /// Igual que <see cref="AplicarResilienciaHttp"/> salvo en cuándo se
    /// reintenta, y esa diferencia es el punto.
    ///
    /// El reintento estándar trata igual un 429 que un 500 que un timeout,
    /// porque para un servicio idempotente da lo mismo. Aquí no da lo mismo:
    /// ninguno de los tres proveedores garantiza idempotencia en estos
    /// endpoints, así que un POST que se reintenta tras un timeout o un 5xx
    /// puede haberse procesado ya del otro lado — y entonces el reintento no
    /// recupera nada, duplica el cobro y vuelve a transmitir el documento. Con
    /// hasta tres intentos HTTP multiplicados por los tres del trabajo durable,
    /// un solo encargo podía llegar a nueve ejecuciones facturables.
    ///
    /// Solo se reintenta el <b>429</b> (ver <see cref="ReintentoProveedorIa"/>,
    /// donde vive el predicado para que sea comprobable): es la única respuesta
    /// que dice de forma explícita que la petición NO se procesó, y trae
    /// <c>Retry-After</c>, que el manejador estándar respeta. Un intento
    /// adicional basta — si el segundo también choca contra el límite, insistir
    /// en el mismo instante solo empeora la congestión.
    ///
    /// Lo demás no se pierde: sigue habiendo reintento, pero en las capas donde
    /// es visible y queda contabilizado — el fallback al siguiente proveedor
    /// del router (que además cambia de destinatario, así que no repite una
    /// petición que quizá ya se cobró) y los intentos de
    /// <c>TrabajoAnalisisDocumento</c>, que se registran en la cola. Mover el
    /// reintento de un nivel invisible a otro auditable es justamente lo que
    /// permite ver el gasto en vez de descubrirlo en la factura.
    ///
    /// <see cref="ContadorLlamadasProveedorIaHandler"/> se añade DESPUÉS del
    /// manejador de resiliencia para quedar por dentro de él, y así contar
    /// también los reintentos.
    /// </summary>
    private static IHttpClientBuilder AplicarResilienciaHttpIa(this IHttpClientBuilder constructor, TimeSpan timeoutPorIntento)
    {
        constructor.AddStandardResilienceHandler(opciones =>
        {
            opciones.AttemptTimeout.Timeout = timeoutPorIntento;
            opciones.TotalRequestTimeout.Timeout = timeoutPorIntento * 2;
            opciones.CircuitBreaker.SamplingDuration = timeoutPorIntento * 3;
            opciones.Retry.MaxRetryAttempts = 1;
            opciones.Retry.ShouldHandle = argumentos => ValueTask.FromResult(
                ReintentoProveedorIa.EsSeguroReintentar(argumentos.Outcome.Result));
        });

        return constructor.AddHttpMessageHandler<ContadorLlamadasProveedorIaHandler>();
    }

    /// <summary>
    /// Clave de configuración que permite arrancar el tráfico con la identidad
    /// administrativa. Es una opción <b>insegura</b> y se llama como se llama
    /// para que nadie la ponga sin leer qué apaga.
    /// </summary>
    internal const string ClaveDegradacionInsegura = "Rls:PermitirIdentidadAdministrativaInsegura";

    /// <summary>
    /// Con qué identidad conecta el tráfico. Función pura de (configuración,
    /// entorno) para que la decisión se pueda probar sin montar un host.
    ///
    /// <para>
    /// Si hay <c>CaeManagerDbRuntime</c>, esa. Si no, en desarrollo se cae al
    /// rol propietario sin ceremonia —es el flujo de <c>docker compose up</c> y
    /// del arnés E2E, que arranca con <c>ASPNETCORE_ENVIRONMENT=Development</c>
    /// y solo define <c>ConnectionStrings__CaeManagerDb</c>—, y fuera de
    /// desarrollo hace falta decirlo a propósito con
    /// <see cref="ClaveDegradacionInsegura"/>; sin esa declaración, el arranque
    /// se niega.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué abortar y no seguir avisando.</b> Un log de advertencia en el
    /// arranque no lo lee nadie a las tres semanas, y el sistema queda
    /// aparentemente sano mientras su segunda línea de aislamiento no existe.
    /// El coste de equivocarse en cada dirección no es simétrico: un arranque
    /// que falla se ve en el acto y se arregla en minutos; un aislamiento
    /// apagado en silencio no se ve hasta que alguien lee datos de otro tenant.
    /// </para>
    /// </summary>
    internal static string ResolverCadenaDeTrafico(IConfiguration configuration, IHostEnvironment entorno)
    {
        var cadenaRuntime = configuration.GetConnectionString("CaeManagerDbRuntime");
        if (!string.IsNullOrWhiteSpace(cadenaRuntime))
            return cadenaRuntime;

        var cadenaPropietario = configuration.GetConnectionString("CaeManagerDb");

        if (!entorno.IsDevelopment() && !configuration.GetValue(ClaveDegradacionInsegura, defaultValue: false))
            throw new InvalidOperationException(
                $"Falta ConnectionStrings:CaeManagerDbRuntime en el entorno '{entorno.EnvironmentName}'. " +
                "El tráfico se ejecutaría con el rol propietario de la base, al que PostgreSQL nunca " +
                "somete a RLS (ni siquiera con FORCE ROW LEVEL SECURITY), dejando el aislamiento por " +
                "tenant a merced únicamente del filtro global de EF Core. Configura la conexión del rol " +
                "restringido cae_app_runtime (deploy/bootstrap/roles-de-cluster.sql) o, si de verdad " +
                $"quieres arrancar sin esa protección, declara {ClaveDegradacionInsegura}=true.");

        return cadenaPropietario
            ?? throw new InvalidOperationException(
                "No hay ninguna conexión PostgreSQL configurada: ni ConnectionStrings:CaeManagerDbRuntime " +
                "ni ConnectionStrings:CaeManagerDb.");
    }
}
