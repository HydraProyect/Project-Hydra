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
using CaeManager.Application.Evaluaciones;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Notificaciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.RequisitosDocumentales;
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
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Evaluaciones;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.RequisitosDocumentales;
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
        IConfiguracionQueryContext, IAuditoriaQueryContext, IRequisitosDocumentalesQueryContext, ITenantsQueryContext,
        IFacturacionQueryContext, IProyectosQueryContext, IRetencionQueryContext, IEvaluacionesQueryContext,
        IIncidenciasQueryContext, IComunicacionesQueryContext, IApiKeysQueryContext
{
    private readonly IDataProtector _protectorCredenciales =
        dataProtectionProvider.CreateProtector("CaeManager.PlataformaAcceso.Credenciales.v1"); // nombre de protector sin cambiar: renombrar rompería el descifrado de filas ya cifradas.
    private readonly IDataProtector _protectorCredencialesEmpresa =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoEmpresa.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesSubcontrata =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoSubcontrata.Credenciales.v1");

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
    public DbSet<RequisitoDocumental> RequisitosDocumentales => Set<RequisitoDocumental>();
    IQueryable<RequisitoDocumental> IRequisitosDocumentalesQueryContext.RequisitosDocumentales => RequisitosDocumentales;
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
    public DbSet<CaeManager.Domain.Retencion.SolicitudPurga> SolicitudesPurga => Set<CaeManager.Domain.Retencion.SolicitudPurga>();
    IQueryable<CaeManager.Domain.Retencion.SolicitudPurga> IRetencionQueryContext.SolicitudesPurga => SolicitudesPurga;
    public DbSet<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Set<AsignacionOperadorDelegado>();
    IQueryable<AsignacionOperadorDelegado> ITenantsQueryContext.AsignacionesOperadorDelegado => AsignacionesOperadorDelegado;
    public DbSet<Evaluacion> Evaluaciones => Set<Evaluacion>();
    IQueryable<Evaluacion> IEvaluacionesQueryContext.Evaluaciones => Evaluaciones;
    public DbSet<Incidencia> Incidencias => Set<Incidencia>();
    IQueryable<Incidencia> IIncidenciasQueryContext.Incidencias => Incidencias;
    public DbSet<ConversacionCorreo> ConversacionesCorreo => Set<ConversacionCorreo>();
    IQueryable<ConversacionCorreo> IComunicacionesQueryContext.ConversacionesCorreo => ConversacionesCorreo;
    public DbSet<MensajeCorreo> MensajesCorreo => Set<MensajeCorreo>();
    IQueryable<MensajeCorreo> IComunicacionesQueryContext.MensajesCorreo => MensajesCorreo;
    public DbSet<ParticipanteConversacion> ParticipantesConversacion => Set<ParticipanteConversacion>();
    IQueryable<ParticipanteConversacion> IComunicacionesQueryContext.ParticipantesConversacion => ParticipantesConversacion;
    public DbSet<MacroRespuesta> MacrosRespuesta => Set<MacroRespuesta>();
    IQueryable<MacroRespuesta> IComunicacionesQueryContext.MacrosRespuesta => MacrosRespuesta;
    public DbSet<ClaveApi> ClavesApi => Set<ClaveApi>();
    IQueryable<ClaveApi> IApiKeysQueryContext.ClavesApi => ClavesApi;
    public DbSet<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => Set<PreferenciaDashboardUsuario>();
    IQueryable<PreferenciaDashboardUsuario> IConfiguracionQueryContext.PreferenciasDashboardUsuario => PreferenciasDashboardUsuario;


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

        builder.Entity<IdentityRole<Guid>>().HasData(IdentityRoleSeedData.Filas());

        // Filtro global de aislamiento por tenant, centralizado aquí (no en
        // cada *Configuration.cs) — ver docs/MULTITENANCY.md § 4.2: EF Core
        // solo admite un HasQueryFilter por entidad, así que ponerlo en un
        // único lugar evita que un segundo HasQueryFilter futuro reemplace
        // silenciosamente este sin que nadie lo note. Los 16 agregados con
        // soft delete combinan ambos filtros; los 23 restantes (tablas de
        // unión/satélite sin ciclo de vida propio) solo llevan el de tenant.
        // Las dos listas cubren las 39 entidades que heredan de
        // EntidadConTenant/EntidadBase, sin excepción: es la invariante que
        // enuncia docs/MULTITENANCY.md ("ninguna tabla sin filtro global") y
        // la cubre AislamientoPorAgregadoTests. Toda entidad nueva añade aquí
        // su línea y allí su test.
        // AspNetUsers queda deliberadamente sin filtro — el login necesita
        // poder resolver el usuario (y por tanto su tenant) antes de
        // conocerlo, ver TenantClaimsPrincipalFactory.
        builder.Entity<Cliente>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Centro>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Documento>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Empresa>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<RequisitoDocumental>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Subcontrata>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Trabajador>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Vehiculo>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Visita>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Proyecto>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Evaluacion>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<Incidencia>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<TarifaCliente>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<ConversacionCorreo>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<MacroRespuesta>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
        builder.Entity<ClaveApi>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);

        builder.Entity<Alerta>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<Asignacion>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<RegistroAuditoria>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<CanalGestionDocumental>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<ParametroSistema>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<ConfiguracionIaDocumentoCliente>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<TipoDocumento>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<TipoDocumentoCentro>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<CredencialAccesoEmpresa>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<EmpresaCliente>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<NotificacionUsuario>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<CredencialAccesoSubcontrata>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<SubcontrataCliente>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<SubcontrataEmpresa>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<DeteccionTrabajador>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<ExtraccionIaCache>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<AuditoriaExtraccionIa>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<VisitaTrabajador>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<RevisionIaDocumento>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<ProyectoTecnico>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<AprobacionDocumento>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<MensajeCorreo>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<ParticipanteConversacion>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<CaeManager.Domain.Soporte.RegistroActividadSoporte>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
        builder.Entity<CaeManager.Domain.Retencion.SolicitudPurga>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);

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
