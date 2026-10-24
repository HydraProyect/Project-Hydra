using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Estado materializado de un par exigido: Centro × titular × Tipo de documento que el Centro exige (o que exige un evento
/// del Centro), con el estado en TALVEG del documento efectivo <b>en ese Centro</b>. De estas filas se agregan las demás
/// lecturas (Mi trabajo, bandeja, fichas 360).
///
/// <para>
/// Es dato derivado del Tenant propietario, nunca de quien lo mira: el alcance del usuario (Asignación de Cartera,
/// Centros visibles) se aplica al leer, filtrando filas. Una fila por par, garantizada por el índice único
/// <c>(TenantId, CentroId, AmbitoTitular, TitularId, TipoDocumentoId)</c>; sin FK, porque el titular es polimórfico
/// (Trabajador o Empresa) y la fila se rehace entera en cada recálculo.
/// </para>
///
/// <para>
/// El estado en la Plataforma CAE va aparte, en <see cref="EstadoEnPlataforma"/>, una fila por acreditación;
/// <see cref="EstadoPlataforma"/> es solo un resumen para leer rápido.
/// </para>
///
/// <para>
/// Sin constructor público ni métodos: esta entidad solo define la tabla. El servicio de recálculo que la escribe llega
/// después, con sus propias fábricas.
/// </para>
/// </summary>
public class EstadoRequisito : EntidadConTenant
{
    public Guid CentroId { get; private set; }

    /// <summary><see cref="AmbitoAplicacion.Trabajador"/> o <see cref="AmbitoAplicacion.Empresa"/> (una Subcontrata es una Empresa).</summary>
    public AmbitoAplicacion AmbitoTitular { get; private set; }

    public Guid TitularId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    /// <summary>El Documento que resuelve <c>DocumentoEfectivo</c> para el par; null si no hay ninguno.</summary>
    public Guid? DocumentoEfectivoId { get; private set; }

    /// <summary>Estado en TALVEG del documento efectivo en este Centro, con su contexto (tolerancia incluida).</summary>
    public EstadoDocumento EstadoTalveg { get; private set; }

    /// <summary>Copia de <see cref="TipoDocumentoCentro.BloqueaAcceso"/>: con <see cref="EstadoTalveg"/> decide rojo o ámbar.</summary>
    public bool BloqueaAcceso { get; private set; }

    /// <summary>Inclusión explícita del Centro o requisito de un evento: el par entra en el cumplimiento.</summary>
    public bool CuentaParaCumplimiento { get; private set; }

    /// <summary>Resumen del estado en la Plataforma CAE; el detalle por canal está en <see cref="EstadoEnPlataforma"/>.</summary>
    public EstadoAcreditacion? EstadoPlataforma { get; private set; }

    /// <summary>Vencimiento con la periodicidad del Centro aplicada.</summary>
    public DateOnly? VencimientoEfectivo { get; private set; }

    /// <summary>Primer día en que el estado cambia solo por el paso del tiempo; null si no cambia.</summary>
    public DateOnly? ProximoCambioEl { get; private set; }

    /// <summary>Día de negocio con el que se calculó: la pasada de medianoche repite lo calculado con un día anterior.</summary>
    public DateOnly DiaCalculo { get; private set; }

    public DateTime CalculadoEnUtc { get; private set; }

    /// <summary>Versión de la regla de cálculo: sube cuando cambia la regla, para rehacer todo en un despliegue.</summary>
    public int VersionCalculo { get; private set; }

    private EstadoRequisito()
    {
        // Requerido por EF Core.
    }
}
