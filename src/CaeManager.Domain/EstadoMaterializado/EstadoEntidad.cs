using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Estado materializado de <b>su</b> contexto para cada Centro, Trabajador, Empresa y Subcontrata del Tenant propietario:
/// nunca el peor de otro nivel. Una fila por entidad, garantizada por el índice único <c>(TenantId, Ambito, EntidadId)</c>.
///
/// <para>
/// Los recuentos de <b>otros</b> niveles (en una Empresa, cuántos de sus Trabajadores están bloqueados en algún Centro) no
/// se guardan: dependen de qué Centros ve cada Gestor CAE, así que se recomponen al leer desde las filas visibles. Ningún
/// dato de esta tabla depende de un usuario.
/// </para>
///
/// <para>
/// Sin constructor público ni métodos: esta entidad solo define la tabla.
/// </para>
/// </summary>
public class EstadoEntidad : EntidadConTenant
{
    public AmbitoEstadoEntidad Ambito { get; private set; }
    public Guid EntidadId { get; private set; }

    /// <summary>
    /// Ordinal del enum de estado de su ámbito (hoy se calcula con <c>EstadoCentro</c> para un Centro y con
    /// <see cref="EstadoDocumento"/> en los listados de Empresa y Trabajador). Se guarda como número porque el enum cambia
    /// con <see cref="Ambito"/>; cuál usa cada ámbito lo fija el servicio de recálculo que escribe la fila.
    /// </summary>
    public short Estado { get; private set; }

    /// <summary>Solo para un Centro: resumen del estado en la Plataforma CAE.</summary>
    public EstadoAcreditacion? EstadoPlataforma { get; private set; }

    public int Vencidos { get; private set; }

    /// <summary>Vencidos de un Tipo que ningún Centro marca «bloquea el acceso»: avisan en ámbar, no en rojo.</summary>
    public int VencidosSinBloqueo { get; private set; }

    public int Urgentes { get; private set; }
    public int Proximos { get; private set; }
    public int Faltan { get; private set; }
    public int SinConfirmar { get; private set; }
    public int PendientesEnPlataforma { get; private set; }

    /// <summary>Denominador del cumplimiento de su contexto.</summary>
    public int Requeridos { get; private set; }

    /// <summary>Numerador del cumplimiento de su contexto.</summary>
    public int AlDia { get; private set; }

    /// <summary>Primer día en que el estado cambia solo por el paso del tiempo; null si no cambia.</summary>
    public DateOnly? ProximoCambioEl { get; private set; }

    /// <summary>Día de negocio con el que se calculó: la pasada de medianoche repite lo calculado con un día anterior.</summary>
    public DateOnly DiaCalculo { get; private set; }

    public DateTime CalculadoEnUtc { get; private set; }
    public int VersionCalculo { get; private set; }

    private EstadoEntidad()
    {
        // Requerido por EF Core.
    }
}
