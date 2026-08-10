using System.Reflection;
using CaeManager.Application.ApiKeys;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Empresas;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Notificaciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Retencion;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Tenants;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Alertas;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CaeManager.Infrastructure.Persistence;

public class CaeManagerDbContext(
    DbContextOptions<CaeManagerDbContext> options,
    IDataProtectionProvider dataProtectionProvider,
    ITenantActual tenantActual)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IUnitOfWork,
        IClientesQueryContext, IEmpresasQueryContext, ISubcontratasQueryContext, ICentrosQueryContext,
        ITrabajadoresQueryContext, ITiposDocumentoQueryContext, IDocumentosQueryContext, IDocumentosIaQueryContext,
        INotificacionesQueryContext, IAsignacionesQueryContext, IVisitasQueryContext, IVehiculosQueryContext,
        IConfiguracionQueryContext, IAuditoriaQueryContext, ITenantsQueryContext,
        IFacturacionQueryContext, IProyectosQueryContext, IRetencionQueryContext,
        IIncidenciasQueryContext, IComunicacionesQueryContext, IApiKeysQueryContext, IIntegracionesQueryContext,
        IGestionesQueryContext, IProveedoresPlataformaCaeQueryContext, IReclamacionesQueryContext
{
    private readonly IDataProtector _protectorCredenciales =
        dataProtectionProvider.CreateProtector("CaeManager.PlataformaAcceso.Credenciales.v1"); // nombre de protector sin cambiar: renombrar rompería el descifrado de filas ya cifradas.
    private readonly IDataProtector _protectorCredencialesEmpresa =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoEmpresa.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesSubcontrata =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoSubcontrata.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesIntegracion =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialIntegracion.Credenciales.v1");

    // Cacheados una vez por tipo: MakeGenericMethod en cada entidad del
    // bucle de OnModelCreating es barato, pero GetMethod (búsqueda por
    // nombre) no hace falta repetirlo en cada arranque del modelo.
    private static readonly MethodInfo MetodoAplicarFiltroTenant =
        typeof(CaeManagerDbContext).GetMethod(nameof(AplicarFiltroTenant), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo MetodoAplicarFiltroTenantConSoftDelete =
        typeof(CaeManagerDbContext).GetMethod(nameof(AplicarFiltroTenantConSoftDelete), BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Lambdas C# reales (no Expression.Constant manual) — ver el comentario
    // en OnModelCreating sobre por qué esto es lo que hace que el filtro se
    // revincule contra el DbContext real de cada request en vez de quedar
    // congelado en el modelo cacheado.
    private void AplicarFiltroTenant<TEntidad>(ModelBuilder builder) where TEntidad : EntidadConTenant =>
        builder.Entity<TEntidad>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);

    private void AplicarFiltroTenantConSoftDelete<TEntidad>(ModelBuilder builder) where TEntidad : EntidadBase =>
        builder.Entity<TEntidad>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);

    public DbSet<Cliente> Clientes => Set<Cliente>();
    IQueryable<Cliente> IClientesQueryContext.Clientes => Clientes;
    public DbSet<Centro> Centros => Set<Centro>();
    IQueryable<Centro> ICentrosQueryContext.Centros => Centros;
    public DbSet<CanalGestionDocumental> CanalesGestionDocumental => Set<CanalGestionDocumental>();
    IQueryable<CanalGestionDocumental> ICentrosQueryContext.CanalesGestionDocumental => CanalesGestionDocumental;
    public DbSet<Empresa> Empresas => Set<Empresa>();
    IQueryable<Empresa> IEmpresasQueryContext.Empresas => Empresas;
    public DbSet<EmpresaCliente> EmpresasClientes => Set<EmpresaCliente>();
    IQueryable<EmpresaCliente> IEmpresasQueryContext.EmpresasClientes => EmpresasClientes;
    public DbSet<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => Set<CredencialAccesoEmpresa>();
    IQueryable<CredencialAccesoEmpresa> IEmpresasQueryContext.CredencialesAccesoEmpresa => CredencialesAccesoEmpresa;
    public DbSet<Subcontrata> Subcontratas => Set<Subcontrata>();
    IQueryable<Subcontrata> ISubcontratasQueryContext.Subcontratas => Subcontratas;
    public DbSet<SubcontrataCliente> SubcontratasClientes => Set<SubcontrataCliente>();
    IQueryable<SubcontrataCliente> ISubcontratasQueryContext.SubcontratasClientes => SubcontratasClientes;
    public DbSet<SubcontrataEmpresa> SubcontratasEmpresas => Set<SubcontrataEmpresa>();
    IQueryable<SubcontrataEmpresa> ISubcontratasQueryContext.SubcontratasEmpresas => SubcontratasEmpresas;
    public DbSet<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata => Set<CredencialAccesoSubcontrata>();
    IQueryable<CredencialAccesoSubcontrata> ISubcontratasQueryContext.CredencialesAccesoSubcontrata => CredencialesAccesoSubcontrata;
    public DbSet<Trabajador> Trabajadores => Set<Trabajador>();
    IQueryable<Trabajador> ITrabajadoresQueryContext.Trabajadores => Trabajadores;
    public DbSet<DeteccionTrabajador> DeteccionesTrabajador => Set<DeteccionTrabajador>();
    IQueryable<DeteccionTrabajador> ITrabajadoresQueryContext.DeteccionesTrabajador => DeteccionesTrabajador;
    public DbSet<TipoDocumento> TiposDocumento => Set<TipoDocumento>();
    IQueryable<TipoDocumento> ITiposDocumentoQueryContext.TiposDocumento => TiposDocumento;
    public DbSet<TipoDocumentoCentro> TiposDocumentoCentros => Set<TipoDocumentoCentro>();
    IQueryable<TipoDocumentoCentro> ITiposDocumentoQueryContext.TiposDocumentoCentros => TiposDocumentoCentros;
    public DbSet<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => Set<ConfiguracionIaDocumentoCliente>();
    IQueryable<ConfiguracionIaDocumentoCliente> ITiposDocumentoQueryContext.ConfiguracionesIaDocumentoCliente => ConfiguracionesIaDocumentoCliente;
    public DbSet<RevisionIaDocumento> RevisionesIaDocumento => Set<RevisionIaDocumento>();
    IQueryable<RevisionIaDocumento> IDocumentosQueryContext.RevisionesIaDocumento => RevisionesIaDocumento;
    public DbSet<AprobacionDocumento> AprobacionesDocumento => Set<AprobacionDocumento>();
    IQueryable<AprobacionDocumento> IDocumentosQueryContext.AprobacionesDocumento => AprobacionesDocumento;
    public DbSet<ExtraccionIaCache> ExtraccionesIaCache => Set<ExtraccionIaCache>();
    IQueryable<ExtraccionIaCache> IDocumentosIaQueryContext.ExtraccionesIaCache => ExtraccionesIaCache;
    public DbSet<AuditoriaExtraccionIa> AuditoriasExtraccionIa => Set<AuditoriaExtraccionIa>();
    IQueryable<AuditoriaExtraccionIa> IDocumentosIaQueryContext.AuditoriasExtraccionIa => AuditoriasExtraccionIa;
    public DbSet<TrabajoAnalisisDocumento> TrabajosAnalisisDocumento => Set<TrabajoAnalisisDocumento>();
    public DbSet<FirmaDigitalDocumento> FirmasDigitalesDocumento => Set<FirmaDigitalDocumento>();
    IQueryable<FirmaDigitalDocumento> IDocumentosQueryContext.FirmasDigitalesDocumento => FirmasDigitalesDocumento;
    public DbSet<VerificacionDocumentoOficial> VerificacionesDocumentoOficial => Set<VerificacionDocumentoOficial>();
    IQueryable<VerificacionDocumentoOficial> IDocumentosQueryContext.VerificacionesDocumentoOficial => VerificacionesDocumentoOficial;
    public DbSet<AcreditacionDocumentoPlataforma> AcreditacionesDocumentoPlataforma => Set<AcreditacionDocumentoPlataforma>();
    IQueryable<AcreditacionDocumentoPlataforma> IDocumentosQueryContext.AcreditacionesDocumentoPlataforma => AcreditacionesDocumentoPlataforma;
    public DbSet<RechazoAcreditacionDocumentoPlataforma> RechazosAcreditacionDocumentoPlataforma => Set<RechazoAcreditacionDocumentoPlataforma>();
    IQueryable<RechazoAcreditacionDocumentoPlataforma> IDocumentosQueryContext.RechazosAcreditacionDocumentoPlataforma => RechazosAcreditacionDocumentoPlataforma;
    public DbSet<NotificacionUsuario> NotificacionesUsuario => Set<NotificacionUsuario>();
    IQueryable<NotificacionUsuario> INotificacionesQueryContext.NotificacionesUsuario => NotificacionesUsuario;
    public DbSet<Documento> Documentos => Set<Documento>();
    IQueryable<Documento> IDocumentosQueryContext.Documentos => Documentos;
    public DbSet<Asignacion> Asignaciones => Set<Asignacion>();
    IQueryable<Asignacion> IAsignacionesQueryContext.Asignaciones => Asignaciones;
    public DbSet<Visita> Visitas => Set<Visita>();
    IQueryable<Visita> IVisitasQueryContext.Visitas => Visitas;
    public DbSet<VisitaTrabajador> VisitasTrabajadores => Set<VisitaTrabajador>();
    IQueryable<VisitaTrabajador> IVisitasQueryContext.VisitasTrabajadores => VisitasTrabajadores;
    public DbSet<Vehiculo> Vehiculos => Set<Vehiculo>();
    IQueryable<Vehiculo> IVehiculosQueryContext.Vehiculos => Vehiculos;
    public DbSet<Alerta> Alertas => Set<Alerta>();
    public DbSet<ParametroSistema> ParametrosSistema => Set<ParametroSistema>();
    IQueryable<ParametroSistema> IConfiguracionQueryContext.ParametrosSistema => ParametrosSistema;
    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();
    IQueryable<RegistroAuditoria> IAuditoriaQueryContext.RegistrosAuditoria => RegistrosAuditoria;
    public DbSet<Tenant> Tenants => Set<Tenant>();
    IQueryable<Tenant> ITenantsQueryContext.Tenants => Tenants;
    public DbSet<TarifaCliente> TarifasCliente => Set<TarifaCliente>();
    IQueryable<TarifaCliente> IFacturacionQueryContext.TarifasCliente => TarifasCliente;
    public DbSet<Proyecto> Proyectos => Set<Proyecto>();
    IQueryable<Proyecto> IProyectosQueryContext.Proyectos => Proyectos;
    public DbSet<ProyectoTecnico> ProyectosTecnicos => Set<ProyectoTecnico>();
    IQueryable<ProyectoTecnico> IProyectosQueryContext.ProyectosTecnicos => ProyectosTecnicos;
    public DbSet<DelegacionTenant> DelegacionesTenant => Set<DelegacionTenant>();
    IQueryable<DelegacionTenant> ITenantsQueryContext.DelegacionesTenant => DelegacionesTenant;
    public DbSet<CaeManager.Domain.Soporte.RegistroActividadSoporte> RegistrosActividadSoporte => Set<CaeManager.Domain.Soporte.RegistroActividadSoporte>();
    IQueryable<CaeManager.Domain.Soporte.RegistroActividadSoporte> ITenantsQueryContext.RegistrosActividadSoporte => RegistrosActividadSoporte;
    public DbSet<AceptacionTerminos> AceptacionesTerminos => Set<AceptacionTerminos>();
    public DbSet<ReclamacionDocumental> ReclamacionesDocumentales => Set<ReclamacionDocumental>();
    IQueryable<ReclamacionDocumental> IReclamacionesQueryContext.ReclamacionesDocumentales => ReclamacionesDocumentales;
    public DbSet<ReclamacionDocumentalDocumento> ReclamacionesDocumentalesDocumento => Set<ReclamacionDocumentalDocumento>();
    IQueryable<ReclamacionDocumentalDocumento> IReclamacionesQueryContext.ReclamacionesDocumentalesDocumento => ReclamacionesDocumentalesDocumento;
    public DbSet<ReclamacionDocumentalDocumento> ReclamacionesDocumentalesDocumentos => Set<ReclamacionDocumentalDocumento>();
    public DbSet<CaeManager.Domain.Retencion.SolicitudPurga> SolicitudesPurga => Set<CaeManager.Domain.Retencion.SolicitudPurga>();
    IQueryable<CaeManager.Domain.Retencion.SolicitudPurga> IRetencionQueryContext.SolicitudesPurga => SolicitudesPurga;
    public DbSet<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Set<AsignacionOperadorDelegado>();
    IQueryable<AsignacionOperadorDelegado> ITenantsQueryContext.AsignacionesOperadorDelegado => AsignacionesOperadorDelegado;
    public DbSet<Incidencia> Incidencias => Set<Incidencia>();
    IQueryable<Incidencia> IIncidenciasQueryContext.Incidencias => Incidencias;
    public DbSet<Conversacion> Conversaciones => Set<Conversacion>();
    IQueryable<Conversacion> IComunicacionesQueryContext.Conversaciones => Conversaciones;
    public DbSet<Mensaje> Mensajes => Set<Mensaje>();
    IQueryable<Mensaje> IComunicacionesQueryContext.Mensajes => Mensajes;
    public DbSet<ParticipanteConversacion> ParticipantesConversacion => Set<ParticipanteConversacion>();
    IQueryable<ParticipanteConversacion> IComunicacionesQueryContext.ParticipantesConversacion => ParticipantesConversacion;
    public DbSet<MacroRespuesta> MacrosRespuesta => Set<MacroRespuesta>();
    IQueryable<MacroRespuesta> IComunicacionesQueryContext.MacrosRespuesta => MacrosRespuesta;
    public DbSet<AdjuntoMensaje> AdjuntosMensaje => Set<AdjuntoMensaje>();
    IQueryable<AdjuntoMensaje> IComunicacionesQueryContext.AdjuntosMensaje => AdjuntosMensaje;
    public DbSet<SugerenciaVisitaCorreo> SugerenciasVisitaCorreo => Set<SugerenciaVisitaCorreo>();
    IQueryable<SugerenciaVisitaCorreo> IComunicacionesQueryContext.SugerenciasVisitaCorreo => SugerenciasVisitaCorreo;
    public DbSet<SugerenciaGestionCorreo> SugerenciasGestionCorreo => Set<SugerenciaGestionCorreo>();
    IQueryable<SugerenciaGestionCorreo> IComunicacionesQueryContext.SugerenciasGestionCorreo => SugerenciasGestionCorreo;
    public DbSet<DetalleSugerenciaGestionCorreo> DetallesSugerenciaGestionCorreo => Set<DetalleSugerenciaGestionCorreo>();
    IQueryable<DetalleSugerenciaGestionCorreo> IComunicacionesQueryContext.DetallesSugerenciaGestionCorreo => DetallesSugerenciaGestionCorreo;
    public DbSet<ClasificacionRuidoDetalleGestion> ClasificacionesRuidoDetalleGestion => Set<ClasificacionRuidoDetalleGestion>();
    IQueryable<ClasificacionRuidoDetalleGestion> IComunicacionesQueryContext.ClasificacionesRuidoDetalleGestion => ClasificacionesRuidoDetalleGestion;
    public DbSet<UltimoResumenNotificacionPlataforma> UltimosResumenesNotificacionPlataforma => Set<UltimoResumenNotificacionPlataforma>();
    public DbSet<SolicitudPrioridadDocumento> SolicitudesPrioridadDocumento => Set<SolicitudPrioridadDocumento>();
    IQueryable<SolicitudPrioridadDocumento> IComunicacionesQueryContext.SolicitudesPrioridadDocumento => SolicitudesPrioridadDocumento;
    public DbSet<EventoConversacion> EventosConversacion => Set<EventoConversacion>();
    IQueryable<EventoConversacion> IComunicacionesQueryContext.EventosConversacion => EventosConversacion;
    public DbSet<ClasificacionRuidoMensaje> ClasificacionesRuidoMensaje => Set<ClasificacionRuidoMensaje>();
    IQueryable<ClasificacionRuidoMensaje> IComunicacionesQueryContext.ClasificacionesRuidoMensaje => ClasificacionesRuidoMensaje;
    public DbSet<ClasificacionRelevanciaCae> ClasificacionesRelevanciaCae => Set<ClasificacionRelevanciaCae>();
    IQueryable<ClasificacionRelevanciaCae> IComunicacionesQueryContext.ClasificacionesRelevanciaCae => ClasificacionesRelevanciaCae;
    public DbSet<Gestion> Gestiones => Set<Gestion>();
    IQueryable<Gestion> IGestionesQueryContext.Gestiones => Gestiones;
    public DbSet<ClaveApi> ClavesApi => Set<ClaveApi>();
    IQueryable<ClaveApi> IApiKeysQueryContext.ClavesApi => ClavesApi;
    public DbSet<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => Set<PreferenciaDashboardUsuario>();
    IQueryable<PreferenciaDashboardUsuario> IConfiguracionQueryContext.PreferenciasDashboardUsuario => PreferenciasDashboardUsuario;
    public DbSet<FiltroGuardado> FiltrosGuardados => Set<FiltroGuardado>();
    IQueryable<FiltroGuardado> IConfiguracionQueryContext.FiltrosGuardados => FiltrosGuardados;
    public DbSet<ConexionIntegracion> ConexionesIntegracion => Set<ConexionIntegracion>();
    IQueryable<ConexionIntegracion> IIntegracionesQueryContext.ConexionesIntegracion => ConexionesIntegracion;
    public DbSet<CredencialIntegracion> CredencialesIntegracion => Set<CredencialIntegracion>();
    public DbSet<SuscripcionWebhook> SuscripcionesWebhook => Set<SuscripcionWebhook>();
    public DbSet<EventoWebhook> EventosWebhook => Set<EventoWebhook>();
    public DbSet<LineaWhatsApp> LineasWhatsApp => Set<LineaWhatsApp>();
    IQueryable<LineaWhatsApp> IIntegracionesQueryContext.LineasWhatsApp => LineasWhatsApp;
    public DbSet<MiembroPoolLinea> MiembrosPoolLinea => Set<MiembroPoolLinea>();
    IQueryable<MiembroPoolLinea> IIntegracionesQueryContext.MiembrosPoolLinea => MiembrosPoolLinea;
    public DbSet<ProveedorPlataformaCae> ProveedoresPlataformaCae => Set<ProveedorPlataformaCae>();
    IQueryable<ProveedorPlataformaCae> IProveedoresPlataformaCaeQueryContext.ProveedoresPlataformaCae => ProveedoresPlataformaCae;
    public DbSet<DominioProveedorPlataformaCae> DominiosProveedorPlataformaCae => Set<DominioProveedorPlataformaCae>();
    IQueryable<DominioProveedorPlataformaCae> IProveedoresPlataformaCaeQueryContext.DominiosProveedorPlataformaCae => DominiosProveedorPlataformaCae;
    public DbSet<ContactoWhatsApp> ContactosWhatsApp => Set<ContactoWhatsApp>();
    IQueryable<ContactoWhatsApp> IComunicacionesQueryContext.ContactosWhatsApp => ContactosWhatsApp;


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CaeManagerDbContext).Assembly);

        // Cifrado en reposo de credenciales de plataformas externas (ver ARCHITECTURE.md, "Datos sensibles").
        var conversorCredenciales = new ValueConverter<string?, string?>(
            valorPlano => valorPlano == null ? null : _protectorCredenciales.Protect(valorPlano),
            valorCifrado => valorCifrado == null ? null : _protectorCredenciales.Unprotect(valorCifrado));

        builder.Entity<CanalGestionDocumental>().Property(c => c.Usuario).HasConversion(conversorCredenciales);
        builder.Entity<CanalGestionDocumental>().Property(c => c.Contrasena).HasConversion(conversorCredenciales);

        var conversorCredencialesEmpresa = new ValueConverter<string?, string?>(
            valorPlano => valorPlano == null ? null : _protectorCredencialesEmpresa.Protect(valorPlano),
            valorCifrado => valorCifrado == null ? null : _protectorCredencialesEmpresa.Unprotect(valorCifrado));

        builder.Entity<CredencialAccesoEmpresa>().Property(c => c.Usuario).HasConversion(conversorCredencialesEmpresa);
        builder.Entity<CredencialAccesoEmpresa>().Property(c => c.Contrasena).HasConversion(conversorCredencialesEmpresa);

        var conversorCredencialesSubcontrata = new ValueConverter<string?, string?>(
            valorPlano => valorPlano == null ? null : _protectorCredencialesSubcontrata.Protect(valorPlano),
            valorCifrado => valorCifrado == null ? null : _protectorCredencialesSubcontrata.Unprotect(valorCifrado));

        builder.Entity<CredencialAccesoSubcontrata>().Property(c => c.Usuario).HasConversion(conversorCredencialesSubcontrata);
        builder.Entity<CredencialAccesoSubcontrata>().Property(c => c.Contrasena).HasConversion(conversorCredencialesSubcontrata);

        // Un mismo protector para las dos: RefreshToken y ClientState viven
        // bajo el mismo agregado (ConexionIntegracion) — mismo criterio que
        // PlataformaAcceso, compartido entre varias entidades relacionadas.
        var conversorCredencialesIntegracion = new ValueConverter<string, string>(
            valorPlano => _protectorCredencialesIntegracion.Protect(valorPlano),
            valorCifrado => _protectorCredencialesIntegracion.Unprotect(valorCifrado));

        builder.Entity<CredencialIntegracion>().Property(c => c.RefreshToken).HasConversion(conversorCredencialesIntegracion);
        builder.Entity<SuscripcionWebhook>().Property(s => s.ClientState).HasConversion(conversorCredencialesIntegracion);
        // El System User token de WhatsApp vive bajo el mismo agregado
        // (LineaWhatsApp es satélite de ConexionIntegracion) — mismo
        // protector, mismo criterio que RefreshToken/ClientState.
        builder.Entity<LineaWhatsApp>().Property(l => l.TokenAcceso).HasConversion(conversorCredencialesIntegracion);

        builder.Entity<IdentityRole<Guid>>().HasData(IdentityRoleSeedData.Filas());

        // Filtro global de aislamiento por tenant, aplicado por reflexión
        // sobre el modelo (P2 #27 de docs/business/MATURITY_REVIEW.md — ver
        // docs/MULTITENANCY.md § 4.2). Antes eran ~40 líneas de
        // HasQueryFilter enumeradas a mano, una trampa ya demostrada dos
        // veces: TarifaCliente y AprobacionDocumento se quedaron sin filtro
        // porque alguien olvidó su línea (hallazgos A-1 y M-1 de
        // docs/archive/INFORME-AUDITORIA-TECNICA.md). Recorrer el modelo en
        // busca de EntidadConTenant — mismo patrón que ya usaba el bucle de
        // Version de aquí abajo — hace estructuralmente imposible que una
        // entidad nueva se quede fuera: basta con heredar de
        // EntidadConTenant, no hay una segunda línea que recordar añadir.
        // Los agregados con soft delete (EntidadBase) combinan ambos
        // filtros; el resto (tablas de unión/satélite) solo lleva el de
        // tenant. AislamientoPorAgregadoTests sigue cubriendo el
        // comportamiento por agregado; ModeloTenantTests verifica que
        // ninguna EntidadConTenant se quede sin filtro en el modelo, sea
        // cual sea la forma en que se aplique.
        //
        // EF Core solo admite un filtro (sin nombre) por entidad — igual que
        // antes, este es el único sitio del código que llama a
        // SetQueryFilter, para que un HasQueryFilter futuro en otro lugar no
        // lo reemplace en silencio sin que nadie lo note.
        //
        // AspNetUsers queda deliberadamente sin filtro (no hereda de
        // EntidadConTenant) — el login necesita poder resolver el usuario
        // (y por tanto su tenant) antes de conocerlo, ver
        // TenantClaimsPrincipalFactory. Tenant, DelegacionTenant,
        // AsignacionOperadorDelegado, ProveedorPlataformaCae y
        // DominioProveedorPlataformaCae tampoco heredan de EntidadConTenant:
        // son catálogos globales por diseño (docs/MULTITENANCY.md § 7-8),
        // no un olvido.
        //
        // El filtro de cada entidad se construye con un método genérico real
        // (AplicarFiltroTenant/ConSoftDelete), invocado por reflexión — nunca
        // con Expression.Constant(tenantActual). Una lambda C# escrita a mano
        // que referencia "tenantActual" cierra sobre el campo de ESTA
        // instancia de DbContext, y EF Core reconoce ese patrón para
        // revincular el filtro contra la instancia real en cada consulta.
        // Expression.Constant(tenantActual), en cambio, hornea la instancia
        // de ITenantActual de la primera vez que se construyó el modelo como
        // constante del modelo cacheado (por tipo de DbContext, no por
        // instancia) — todo DbContext posterior, con un tenant scoped
        // distinto, seguiría evaluando contra ese ITenantActual congelado.
        // Regresión real encontrada en auditoría de PR #49 (43/53 tests de
        // AislamientoPorAgregadoTests fallando), confirmada reproduciendo el
        // fallo. La reflexión aquí solo elige a qué método genérico llamar
        // por cada tipo — el árbol de expresión en sí lo construye el
        // compilador de C#, no código manual.
        foreach (var tipoEntidadTenant in builder.Model.GetEntityTypes()
                     .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t.ClrType)))
        {
            var metodo = (typeof(EntidadBase).IsAssignableFrom(tipoEntidadTenant.ClrType)
                    ? MetodoAplicarFiltroTenantConSoftDelete
                    : MetodoAplicarFiltroTenant)
                .MakeGenericMethod(tipoEntidadTenant.ClrType);

            metodo.Invoke(this, [builder]);
        }

        // Concurrencia optimista sobre todo agregado con ciclo de vida
        // propio. Se recorre el modelo en vez de enumerar las 15 entidades
        // una a una a propósito: así una entidad nueva que herede de
        // EntidadBase queda protegida sin que nadie tenga que acordarse —
        // justo lo contrario de lo que pasó con los filtros globales, donde
        // olvidar una línea costó el hallazgo A-1.
        //
        // El valor lo renueva ConcurrenciaOptimistaInterceptor en cada
        // modificación; marcar la propiedad aquí es lo que hace que EF la
        // incluya en el WHERE del UPDATE.
        foreach (var tipoEntidad in builder.Model.GetEntityTypes()
                     .Where(t => typeof(EntidadBase).IsAssignableFrom(t.ClrType)))
        {
            builder.Entity(tipoEntidad.ClrType)
                .Property(nameof(EntidadBase.Version))
                .IsConcurrencyToken();
        }
    }
}
