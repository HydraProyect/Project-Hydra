using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Inventario de disparadores del estado materializado (diseño § 8): qué entidades son entrada del cálculo y qué raíces
/// (<c>RaicesDeRecalculo</c>) da el alta, el cambio o el borrado de cada una. Da <b>todas</b> las raíces de la acción,
/// también las que dejan de estar relacionadas: por eso mira el valor original además del actual (una Asignación que cambia
/// de Centro, un Trabajador que cambia de Empresa o de Subcontrata, un documento que cambia de titular) y un borrado da las
/// del valor que tenía.
///
/// <para>
/// <c>DisparadoresDeEstadoInventariadosTests</c> exige que toda entidad con <c>TenantId</c> que apunte a una entrada del
/// cálculo esté aquí o en su lista de las que no entran, para que una entidad nueva no se salte el recálculo.
/// </para>
/// </summary>
public static class InventarioDisparadoresEstado
{
    /// <summary>Las entidades de entrada y qué raíces da cada una (la documentación del inventario).</summary>
    public static IReadOnlyDictionary<Type, string> Entradas { get; } = new Dictionary<Type, string>
    {
        [typeof(Documento)] = "Trabajador o Empresa titular (actual y anterior)",
        [typeof(AcreditacionDocumentoPlataforma)] = "Titular de su documento y Centro de su canal (Centro afectado)",
        [typeof(CanalGestionDocumental)] = "Centro (actual y anterior)",
        [typeof(Asignacion)] = "Trabajador y Centro afectado (actuales y anteriores)",
        [typeof(Trabajador)] = "El Trabajador, y su Empresa y su Subcontrata actuales y anteriores (también al borrarlo)",
        [typeof(Proyecto)] = "Si es un evento: su Centro (actual y anterior) y todos sus participantes",
        [typeof(ProyectoTecnico)] = "El participante y el Centro del evento (Centro afectado)",
        [typeof(ProyectoRequisito)] = "El Centro del evento y todos sus participantes",
        [typeof(Centro)] = "El Centro y su Empresa (actual y anterior)",
        [typeof(TipoDocumentoCentro)] = "Centro (actual y anterior)",
        [typeof(Empresa)] = "La Empresa (también Cliente empresarial y Subcontrata)",
        [typeof(TipoDocumento)] = "Tenant entero (cola)",
        [typeof(ToleranciaDocumentoClienteEmpresarial)] = "Tenant entero (cola)",
        [typeof(ParametroSistema)] = "Tenant entero (cola), solo si cambian los umbrales ámbar o rojo",
    };

    /// <summary>
    /// Añade a <paramref name="cambios"/> las raíces de una entrada del <c>ChangeTracker</c> que se va a guardar. Devuelve
    /// si la entidad es entrada del cálculo y cambió algo que cuenta.
    /// </summary>
    public static bool Recoger(EntityEntry entrada, CambiosEstadoDetectados cambios)
    {
        if (entrada.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || entrada.Entity is not EntidadConTenant entidad
            || !Entradas.ContainsKey(entrada.Metadata.ClrType))
            return false;

        if (entidad is ParametroSistema
            && entrada.State == EntityState.Modified
            && !entrada.Property(nameof(ParametroSistema.UmbralAmbarDias)).IsModified
            && !entrada.Property(nameof(ParametroSistema.UmbralRojoDias)).IsModified)
            return false;

        if (entidad is Proyecto
            && !Valores<TipoProyecto>(entrada, nameof(Proyecto.Tipo)).Contains(TipoProyecto.Evento))
            return false;

        var c = cambios.De(entidad.TenantId);
        c.Disparadores.Add(entrada.Metadata.ClrType.Name);

        switch (entidad)
        {
            case Documento:
                c.Trabajadores.UnionWith(Guids(entrada, nameof(Documento.TrabajadorId)));
                c.Empresas.UnionWith(Guids(entrada, nameof(Documento.EmpresaId)));
                break;
            case AcreditacionDocumentoPlataforma:
                foreach (var documento in Guids(entrada, nameof(AcreditacionDocumentoPlataforma.DocumentoId)))
                    foreach (var canal in Guids(entrada, nameof(AcreditacionDocumentoPlataforma.CanalGestionDocumentalId)))
                        c.Acreditaciones.Add((documento, canal));
                break;
            case CanalGestionDocumental:
                c.Centros.UnionWith(Guids(entrada, nameof(CanalGestionDocumental.CentroId)));
                break;
            case Asignacion:
                c.Trabajadores.UnionWith(Guids(entrada, nameof(Asignacion.TrabajadorId)));
                c.CentrosAfectados.UnionWith(Guids(entrada, nameof(Asignacion.CentroId)));
                break;
            case Trabajador trabajador:
                c.Trabajadores.Add(trabajador.Id);
                c.Empresas.UnionWith(Guids(entrada, nameof(Trabajador.EmpresaId)));
                c.Empresas.UnionWith(Guids(entrada, nameof(Trabajador.SubcontrataId)));
                break;
            case Proyecto proyecto:
                c.Eventos.Add(proyecto.Id);
                c.Centros.UnionWith(Guids(entrada, nameof(Proyecto.CentroId)));
                break;
            case ProyectoTecnico:
                foreach (var proyectoId in Guids(entrada, nameof(ProyectoTecnico.ProyectoId)))
                    foreach (var trabajadorId in Guids(entrada, nameof(ProyectoTecnico.TrabajadorId)))
                        c.Participaciones.Add((proyectoId, trabajadorId));
                break;
            case ProyectoRequisito:
                c.Eventos.UnionWith(Guids(entrada, nameof(ProyectoRequisito.ProyectoId)));
                break;
            case Centro centro:
                c.Centros.Add(centro.Id);
                c.Empresas.UnionWith(Guids(entrada, nameof(Centro.EmpresaId)));
                break;
            case TipoDocumentoCentro:
                c.Centros.UnionWith(Guids(entrada, nameof(TipoDocumentoCentro.CentroId)));
                break;
            case Empresa empresa:
                c.Empresas.Add(empresa.Id);
                break;
            case TipoDocumento or ToleranciaDocumentoClienteEmpresarial or ParametroSistema:
                c.TenantEntero ??= entrada.Metadata.ClrType.Name;
                break;
        }

        return true;
    }

    /// <summary>El valor actual y, salvo en un alta, el original: los dos si son distintos.</summary>
    private static IEnumerable<Guid> Guids(EntityEntry entrada, string propiedad) =>
        Valores<Guid?>(entrada, propiedad).OfType<Guid>().Where(g => g != Guid.Empty);

    private static IReadOnlyList<T> Valores<T>(EntityEntry entrada, string propiedad)
    {
        var p = entrada.Property(propiedad);
        var actual = Convertir<T>(p.CurrentValue);
        if (entrada.State == EntityState.Added)
            return [actual];
        var original = Convertir<T>(p.OriginalValue);
        return EqualityComparer<T>.Default.Equals(actual, original) ? [actual] : [actual, original];
    }

    private static T Convertir<T>(object? valor) => valor is null ? default! : (T)valor;
}
