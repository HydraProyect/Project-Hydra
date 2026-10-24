using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Estado materializado de una acreditación en la Plataforma CAE: Centro × canal de Plataforma × titular × Tipo de
/// documento. Tabla <c>EstadosPlataforma</c>. Va aparte de <see cref="EstadoRequisito"/> por dos razones del código: un
/// Centro puede tener varios canales de Plataforma, y la causa «vencido en la Plataforma CAE» no exige que el Tipo esté
/// entre los requisitos del Centro, solo que el Trabajador siga asignado. Así quedan además físicamente separados el
/// estado en TALVEG y el estado en la Plataforma CAE.
///
/// <para>
/// Una fila por acreditación, garantizada por el índice único
/// <c>(TenantId, CentroId, CanalGestionDocumentalId, AmbitoTitular, TitularId, TipoDocumentoId)</c>. Sin constructor
/// público ni métodos: esta entidad solo define la tabla.
/// </para>
/// </summary>
public class EstadoEnPlataforma : EntidadConTenant
{
    public Guid CentroId { get; private set; }

    /// <summary>El canal de Plataforma del Centro (<see cref="AcreditacionDocumentoPlataforma.CanalGestionDocumentalId"/>).</summary>
    public Guid CanalGestionDocumentalId { get; private set; }

    /// <summary><see cref="AmbitoAplicacion.Trabajador"/> o <see cref="AmbitoAplicacion.Empresa"/>.</summary>
    public AmbitoAplicacion AmbitoTitular { get; private set; }

    public Guid TitularId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    public EstadoAcreditacion Estado { get; private set; }

    /// <summary>
    /// Qué se sabe de la vigencia en la Plataforma CAE. Hace falta junto a <see cref="FechaVencimientoEnPlataforma"/>:
    /// sin él, «nadie lo ha anotado» y «aquí no caduca» se guardarían igual, con la fecha nula.
    /// </summary>
    public EstadoVigenciaEnPlataforma EstadoVigencia { get; private set; }

    public DateOnly? FechaVencimientoEnPlataforma { get; private set; }

    /// <summary>Primer día en que el estado cambia solo por el paso del tiempo; null si no cambia.</summary>
    public DateOnly? ProximoCambioEl { get; private set; }

    public DateTime CalculadoEnUtc { get; private set; }
    public int VersionCalculo { get; private set; }

    private EstadoEnPlataforma()
    {
        // Requerido por EF Core.
    }
}
