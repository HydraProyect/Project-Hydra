using CaeManager.Domain.Common;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Entrada de la cola de recálculo diferido del estado materializado, para los disparadores de abanico grande. Se
/// escribe en la misma transacción que la acción que la provoca y la consume un trabajo por Tenant.
///
/// <para>
/// <see cref="EntidadId"/> null significa «todo el Tenant».
/// </para>
///
/// <para>
/// Sin constructor público ni métodos: esta entidad solo define la tabla.
/// </para>
/// </summary>
public class RecalculoPendiente : EntidadConTenant
{
    public const int LongitudMaximaMotivo = 200;

    public AmbitoEstadoEntidad Ambito { get; private set; }
    public Guid? EntidadId { get; private set; }

    /// <summary>Qué disparador la encoló, para diagnóstico.</summary>
    public string Motivo { get; private set; } = null!;

    public DateTime CreadoEnUtc { get; private set; }

    /// <summary>Último intento de consumirla; null si aún no se ha intentado.</summary>
    public DateTime? IntentoEnUtc { get; private set; }

    public int Intentos { get; private set; }

    private RecalculoPendiente()
    {
        // Requerido por EF Core.
    }
}
