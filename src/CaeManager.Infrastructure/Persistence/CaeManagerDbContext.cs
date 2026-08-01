using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Alertas;
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
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext, IUnitOfWork
{
    private readonly IDataProtector _protectorCredenciales =
        dataProtectionProvider.CreateProtector("CaeManager.PlataformaAcceso.Credenciales.v1"); // nombre de protector sin cambiar: renombrar rompería el descifrado de filas ya cifradas.
    private readonly IDataProtector _protectorCredencialesEmpresa =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoEmpresa.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesSubcontrata =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoSubcontrata.Credenciales.v1");

    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Centro> Centros => Set<Centro>();
    public DbSet<CanalGestionDocumental> CanalesGestionDocumental => Set<CanalGestionDocumental>();
    IQueryable<CanalGestionDocumental> IApplicationDbContext.CanalesGestionDocumental => CanalesGestionDocumental;
    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<EmpresaCliente> EmpresasClientes => Set<EmpresaCliente>();
    public DbSet<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => Set<CredencialAccesoEmpresa>();
    public DbSet<Subcontrata> Subcontratas => Set<Subcontrata>();
    public DbSet<SubcontrataCliente> SubcontratasClientes => Set<SubcontrataCliente>();
    public DbSet<SubcontrataEmpresa> SubcontratasEmpresas => Set<SubcontrataEmpresa>();
    public DbSet<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata => Set<CredencialAccesoSubcontrata>();
    public DbSet<Trabajador> Trabajadores => Set<Trabajador>();
    public DbSet<DeteccionTrabajador> DeteccionesTrabajador => Set<DeteccionTrabajador>();
    public DbSet<TipoDocumento> TiposDocumento => Set<TipoDocumento>();
    public DbSet<TipoDocumentoCentro> TiposDocumentoCentros => Set<TipoDocumentoCentro>();
    public DbSet<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => Set<ConfiguracionIaDocumentoCliente>();
    public DbSet<RevisionIaDocumento> RevisionesIaDocumento => Set<RevisionIaDocumento>();
    public DbSet<AprobacionDocumento> AprobacionesDocumento => Set<AprobacionDocumento>();
    public DbSet<ExtraccionIaCache> ExtraccionesIaCache => Set<ExtraccionIaCache>();
    public DbSet<AuditoriaExtraccionIa> AuditoriasExtraccionIa => Set<AuditoriaExtraccionIa>();
    public DbSet<TrabajoAnalisisDocumento> TrabajosAnalisisDocumento => Set<TrabajoAnalisisDocumento>();
    public DbSet<NotificacionUsuario> NotificacionesUsuario => Set<NotificacionUsuario>();
    public DbSet<Documento> Documentos => Set<Documento>();
    public DbSet<Asignacion> Asignaciones => Set<Asignacion>();
    public DbSet<Visita> Visitas => Set<Visita>();
    public DbSet<VisitaTrabajador> VisitasTrabajadores => Set<VisitaTrabajador>();
    public DbSet<Vehiculo> Vehiculos => Set<Vehiculo>();
    public DbSet<RequisitoDocumental> RequisitosDocumentales => Set<RequisitoDocumental>();
    IQueryable<RequisitoDocumental> IApplicationDbContext.RequisitosDocumentales => RequisitosDocumentales;
    public DbSet<Alerta> Alertas => Set<Alerta>();
    public DbSet<ParametroSistema> ParametrosSistema => Set<ParametroSistema>();
    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TarifaCliente> TarifasCliente => Set<TarifaCliente>();
    public DbSet<Proyecto> Proyectos => Set<Proyecto>();
    public DbSet<ProyectoTecnico> ProyectosTecnicos => Set<ProyectoTecnico>();
    public DbSet<DelegacionTenant> DelegacionesTenant => Set<DelegacionTenant>();
    public DbSet<CaeManager.Domain.Soporte.RegistroActividadSoporte> RegistrosActividadSoporte => Set<CaeManager.Domain.Soporte.RegistroActividadSoporte>();
    public DbSet<CaeManager.Domain.Retencion.SolicitudPurga> SolicitudesPurga => Set<CaeManager.Domain.Retencion.SolicitudPurga>();
    public DbSet<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Set<AsignacionOperadorDelegado>();
    public DbSet<Evaluacion> Evaluaciones => Set<Evaluacion>();
    public DbSet<Incidencia> Incidencias => Set<Incidencia>();
    public DbSet<ConversacionCorreo> ConversacionesCorreo => Set<ConversacionCorreo>();
    public DbSet<MensajeCorreo> MensajesCorreo => Set<MensajeCorreo>();
    public DbSet<ParticipanteConversacion> ParticipantesConversacion => Set<ParticipanteConversacion>();
    public DbSet<MacroRespuesta> MacrosRespuesta => Set<MacroRespuesta>();

    IQueryable<Cliente> IApplicationDbContext.Clientes => Clientes;
    IQueryable<Empresa> IApplicationDbContext.Empresas => Empresas;
    IQueryable<EmpresaCliente> IApplicationDbContext.EmpresasClientes => EmpresasClientes;
    IQueryable<CredencialAccesoEmpresa> IApplicationDbContext.CredencialesAccesoEmpresa => CredencialesAccesoEmpresa;
    IQueryable<Subcontrata> IApplicationDbContext.Subcontratas => Subcontratas;
    IQueryable<SubcontrataCliente> IApplicationDbContext.SubcontratasClientes => SubcontratasClientes;
    IQueryable<SubcontrataEmpresa> IApplicationDbContext.SubcontratasEmpresas => SubcontratasEmpresas;
    IQueryable<CredencialAccesoSubcontrata> IApplicationDbContext.CredencialesAccesoSubcontrata => CredencialesAccesoSubcontrata;
    IQueryable<Centro> IApplicationDbContext.Centros => Centros;
    IQueryable<Trabajador> IApplicationDbContext.Trabajadores => Trabajadores;
    IQueryable<DeteccionTrabajador> IApplicationDbContext.DeteccionesTrabajador => DeteccionesTrabajador;
    IQueryable<TipoDocumento> IApplicationDbContext.TiposDocumento => TiposDocumento;
    IQueryable<TipoDocumentoCentro> IApplicationDbContext.TiposDocumentoCentros => TiposDocumentoCentros;
    IQueryable<ConfiguracionIaDocumentoCliente> IApplicationDbContext.ConfiguracionesIaDocumentoCliente => ConfiguracionesIaDocumentoCliente;
    IQueryable<RevisionIaDocumento> IApplicationDbContext.RevisionesIaDocumento => RevisionesIaDocumento;
    IQueryable<AprobacionDocumento> IApplicationDbContext.AprobacionesDocumento => AprobacionesDocumento;
    IQueryable<ExtraccionIaCache> IApplicationDbContext.ExtraccionesIaCache => ExtraccionesIaCache;
    IQueryable<AuditoriaExtraccionIa> IApplicationDbContext.AuditoriasExtraccionIa => AuditoriasExtraccionIa;
    IQueryable<TrabajoAnalisisDocumento> IApplicationDbContext.TrabajosAnalisisDocumento => TrabajosAnalisisDocumento;
    IQueryable<NotificacionUsuario> IApplicationDbContext.NotificacionesUsuario => NotificacionesUsuario;
    IQueryable<Documento> IApplicationDbContext.Documentos => Documentos;
    IQueryable<Asignacion> IApplicationDbContext.Asignaciones => Asignaciones;
    IQueryable<Visita> IApplicationDbContext.Visitas => Visitas;
    IQueryable<VisitaTrabajador> IApplicationDbContext.VisitasTrabajadores => VisitasTrabajadores;
    IQueryable<Vehiculo> IApplicationDbContext.Vehiculos => Vehiculos;
    IQueryable<ParametroSistema> IApplicationDbContext.ParametrosSistema => ParametrosSistema;
    IQueryable<RegistroAuditoria> IApplicationDbContext.RegistrosAuditoria => RegistrosAuditoria;
    IQueryable<Tenant> IApplicationDbContext.Tenants => Tenants;
    IQueryable<TarifaCliente> IApplicationDbContext.TarifasCliente => TarifasCliente;
    IQueryable<Proyecto> IApplicationDbContext.Proyectos => Proyectos;
    IQueryable<ProyectoTecnico> IApplicationDbContext.ProyectosTecnicos => ProyectosTecnicos;
    IQueryable<DelegacionTenant> IApplicationDbContext.DelegacionesTenant => DelegacionesTenant;
    IQueryable<CaeManager.Domain.Soporte.RegistroActividadSoporte> IApplicationDbContext.RegistrosActividadSoporte => RegistrosActividadSoporte;
    IQueryable<CaeManager.Domain.Retencion.SolicitudPurga> IApplicationDbContext.SolicitudesPurga => SolicitudesPurga;
    IQueryable<AsignacionOperadorDelegado> IApplicationDbContext.AsignacionesOperadorDelegado => AsignacionesOperadorDelegado;
    IQueryable<Evaluacion> IApplicationDbContext.Evaluaciones => Evaluaciones;
    IQueryable<Incidencia> IApplicationDbContext.Incidencias => Incidencias;
    IQueryable<ConversacionCorreo> IApplicationDbContext.ConversacionesCorreo => ConversacionesCorreo;
    IQueryable<MensajeCorreo> IApplicationDbContext.MensajesCorreo => MensajesCorreo;
    IQueryable<ParticipanteConversacion> IApplicationDbContext.ParticipantesConversacion => ParticipantesConversacion;
    IQueryable<MacroRespuesta> IApplicationDbContext.MacrosRespuesta => MacrosRespuesta;

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
        // TenantClaimsPrincipalFactory. Tenant, DelegacionTenant y
        // AsignacionOperadorDelegado tampoco heredan de EntidadConTenant:
        // son catálogos globales por diseño (docs/MULTITENANCY.md § 7-8),
        // no un olvido.
        foreach (var tipoEntidadTenant in builder.Model.GetEntityTypes()
                     .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t.ClrType)))
        {
            var parametro = Expression.Parameter(tipoEntidadTenant.ClrType, "e");

            // e.TenantId == tenantActual.TenantId — TenantId de la entidad es
            // Guid, tenantActual.TenantId es Guid?; se convierte a Guid?
            // antes de comparar para reproducir exactamente la igualdad
            // "levantada" que ya hacía el operador == de C# en las líneas
            // manuales (sin tenant resuelto, la comparación da false, nunca
            // una excepción — fallo cerrado, docs/MULTITENANCY.md § 4.5).
            var accesoTenantIdEntidad = Expression.Convert(
                Expression.Property(parametro, nameof(EntidadConTenant.TenantId)), typeof(Guid?));
            var accesoTenantActual = Expression.Property(Expression.Constant(tenantActual), nameof(ITenantActual.TenantId));
            Expression cuerpoFiltro = Expression.Equal(accesoTenantIdEntidad, accesoTenantActual);

            if (typeof(EntidadBase).IsAssignableFrom(tipoEntidadTenant.ClrType))
            {
                var noEstaEliminado = Expression.Not(Expression.Property(parametro, nameof(EntidadBase.EstaEliminado)));
                cuerpoFiltro = Expression.AndAlso(noEstaEliminado, cuerpoFiltro);
            }

            tipoEntidadTenant.SetQueryFilter(Expression.Lambda(cuerpoFiltro, parametro));
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
