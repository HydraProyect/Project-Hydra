using CaeManager.Application.Common;
using CaeManager.Domain.Alertas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.RequisitosDocumentales;
using CaeManager.Domain.Subcontratas;
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
    IDataProtectionProvider dataProtectionProvider)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext, IUnitOfWork
{
    private readonly IDataProtector _protectorCredenciales =
        dataProtectionProvider.CreateProtector("CaeManager.PlataformaAcceso.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesEmpresa =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoEmpresa.Credenciales.v1");
    private readonly IDataProtector _protectorCredencialesSubcontrata =
        dataProtectionProvider.CreateProtector("CaeManager.CredencialAccesoSubcontrata.Credenciales.v1");

    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Centro> Centros => Set<Centro>();
    public DbSet<PlataformaAcceso> PlataformasAcceso => Set<PlataformaAcceso>();
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
    public DbSet<NotificacionUsuario> NotificacionesUsuario => Set<NotificacionUsuario>();
    public DbSet<Documento> Documentos => Set<Documento>();
    public DbSet<Asignacion> Asignaciones => Set<Asignacion>();
    public DbSet<Visita> Visitas => Set<Visita>();
    public DbSet<VisitaTrabajador> VisitasTrabajadores => Set<VisitaTrabajador>();
    public DbSet<Vehiculo> Vehiculos => Set<Vehiculo>();
    public DbSet<RequisitoDocumental> RequisitosDocumentales => Set<RequisitoDocumental>();
    public DbSet<Alerta> Alertas => Set<Alerta>();
    public DbSet<ParametroSistema> ParametrosSistema => Set<ParametroSistema>();
    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();

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
    IQueryable<NotificacionUsuario> IApplicationDbContext.NotificacionesUsuario => NotificacionesUsuario;
    IQueryable<Documento> IApplicationDbContext.Documentos => Documentos;
    IQueryable<Asignacion> IApplicationDbContext.Asignaciones => Asignaciones;
    IQueryable<Visita> IApplicationDbContext.Visitas => Visitas;
    IQueryable<VisitaTrabajador> IApplicationDbContext.VisitasTrabajadores => VisitasTrabajadores;
    IQueryable<Vehiculo> IApplicationDbContext.Vehiculos => Vehiculos;
    IQueryable<ParametroSistema> IApplicationDbContext.ParametrosSistema => ParametrosSistema;
    IQueryable<RegistroAuditoria> IApplicationDbContext.RegistrosAuditoria => RegistrosAuditoria;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CaeManagerDbContext).Assembly);

        // Cifrado en reposo de credenciales de plataformas externas (ver ARCHITECTURE.md, "Datos sensibles").
        var conversorCredenciales = new ValueConverter<string?, string?>(
            valorPlano => valorPlano == null ? null : _protectorCredenciales.Protect(valorPlano),
            valorCifrado => valorCifrado == null ? null : _protectorCredenciales.Unprotect(valorCifrado));

        builder.Entity<PlataformaAcceso>().Property(p => p.Usuario).HasConversion(conversorCredenciales);
        builder.Entity<PlataformaAcceso>().Property(p => p.Contrasena).HasConversion(conversorCredenciales);

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
    }
}
