using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Common;

/// <summary>
/// Acceso de solo lectura para Queries: proyectan directamente vía LINQ sin
/// pasar por el repositorio del agregado (ver ARCHITECTURE.md, "CQRS ligero
/// con MediatR"). IQueryable&lt;T&gt; es de System.Linq, no de EF Core, así
/// que Application no depende de ningún paquete de persistencia concreto.
/// </summary>
public interface IApplicationDbContext
{
    IQueryable<Cliente> Clientes { get; }
    IQueryable<Empresa> Empresas { get; }
    IQueryable<EmpresaCliente> EmpresasClientes { get; }
    IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa { get; }
    IQueryable<Subcontrata> Subcontratas { get; }
    IQueryable<SubcontrataCliente> SubcontratasClientes { get; }
    IQueryable<SubcontrataEmpresa> SubcontratasEmpresas { get; }
    IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata { get; }
    IQueryable<Centro> Centros { get; }
    IQueryable<Trabajador> Trabajadores { get; }
    IQueryable<DeteccionTrabajador> DeteccionesTrabajador { get; }
    IQueryable<TipoDocumento> TiposDocumento { get; }
    IQueryable<TipoDocumentoCentro> TiposDocumentoCentros { get; }
    IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente { get; }
    IQueryable<NotificacionUsuario> NotificacionesUsuario { get; }
    IQueryable<Documento> Documentos { get; }
    IQueryable<Asignacion> Asignaciones { get; }
    IQueryable<Visita> Visitas { get; }
    IQueryable<VisitaTrabajador> VisitasTrabajadores { get; }
    IQueryable<Vehiculo> Vehiculos { get; }
    IQueryable<ParametroSistema> ParametrosSistema { get; }
    IQueryable<RegistroAuditoria> RegistrosAuditoria { get; }
}
