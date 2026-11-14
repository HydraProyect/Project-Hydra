using CaeManager.Domain.Common;

namespace CaeManager.Domain.Facturacion;

/// <summary>
/// Lo que el emisor anota a mano sobre el cobro de una liquidación mensual (decisión D-F5, 2026-11-14). Es una marca
/// informativa para el propio emisor: TALVEG no cobra por cuenta de terceros ni suspende la gestión por impago, así que
/// ningún estado dispara una acción sobre la Asignación de Operación ni sobre el acceso.
/// </summary>
public enum EstadoCobroLiquidacion
{
    /// <summary>Sin marca: el estado de toda liquidación que el emisor no ha tocado.</summary>
    Pendiente = 1,

    Cobrada = 2,

    Impagada = 3,
}

/// <summary>
/// La marca de cobro que el Operador CAE externo (<see cref="EntidadConTenant.TenantId"/>, el Tenant propietario de la
/// fila y emisor de la liquidación) pone a la liquidación mensual de un Tenant beneficiario
/// (<see cref="PropietarioTenantId"/>, el Tenant propietario de la Asignación de Operación) para un mes.
///
/// <para>
/// <b>Documento interno del emisor, no factura</b> (D-F3): la liquidación no se numera ni se emite desde TALVEG. La
/// factura real la emite el Operador CAE con su propio programa; <see cref="ReferenciaFacturaEmisor"/> guarda, si el
/// emisor quiere, el número que le dio ese programa, para casar la marca con su factura.
/// </para>
///
/// <para>
/// <b>Plano Comercial</b>: la fila es del Operador CAE, como su tarifa (<c>TarifaOperadorCae</c>). La RLS estándar por
/// <c>TenantId</c> la deja fuera del Tenant beneficiario y de Soporte TALVEG; solo la marcan el Administrador o la
/// Dirección CAE del Operador, y el Coordinador CAE la lee (D-F9). Una fila por Tenant beneficiario y mes; sin fila, la
/// liquidación está <see cref="EstadoCobroLiquidacion.Pendiente"/>.
/// </para>
/// </summary>
public class CobroLiquidacionOperadorCae : EntidadConTenant, IVersionable
{
    public const int LongitudMaximaReferencia = 60;

    /// <summary>
    /// Token de concurrencia optimista: dos personas del emisor pueden marcar el mismo cobro a la vez, y sin él la segunda
    /// pisaría a la primera o dejaría una mezcla de las dos. Lo renueva <c>ConcurrenciaOptimistaInterceptor</c>.
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    public Guid PropietarioTenantId { get; private set; }
    public int Anyo { get; private set; }
    public int Mes { get; private set; }
    public EstadoCobroLiquidacion Estado { get; private set; }

    /// <summary>El número de la factura real que el emisor hizo con su programa; null si no lo anotó.</summary>
    public string? ReferenciaFacturaEmisor { get; private set; }

    public Guid MarcadaPorUsuarioId { get; private set; }
    public DateTime MarcadaEnUtc { get; private set; }

    private CobroLiquidacionOperadorCae() { }

    public static CobroLiquidacionOperadorCae Registrar(
        Guid propietarioTenantId, int anyo, int mes, EstadoCobroLiquidacion estado, string? referenciaFacturaEmisor,
        Guid marcadaPorUsuarioId, DateTime ahoraUtc)
    {
        if (propietarioTenantId == Guid.Empty)
            throw new ArgumentException("La liquidación es de un Tenant beneficiario.", nameof(propietarioTenantId));
        if (anyo is < 2000 or > 9999 || mes is < 1 or > 12)
            throw new ArgumentException("Mes de liquidación no válido.", nameof(mes));

        var cobro = new CobroLiquidacionOperadorCae { PropietarioTenantId = propietarioTenantId, Anyo = anyo, Mes = mes };
        cobro.Marcar(estado, referenciaFacturaEmisor, marcadaPorUsuarioId, ahoraUtc);
        return cobro;
    }

    public void Marcar(EstadoCobroLiquidacion estado, string? referenciaFacturaEmisor, Guid marcadaPorUsuarioId, DateTime ahoraUtc)
    {
        if (!Enum.IsDefined(estado))
            throw new ArgumentException("Estado de cobro desconocido.", nameof(estado));
        var referencia = string.IsNullOrWhiteSpace(referenciaFacturaEmisor) ? null : referenciaFacturaEmisor.Trim();
        if (referencia is { Length: > LongitudMaximaReferencia })
            throw new ArgumentException($"La referencia de la factura no pasa de {LongitudMaximaReferencia} caracteres.", nameof(referenciaFacturaEmisor));
        if (marcadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La marca la pone una persona.", nameof(marcadaPorUsuarioId));
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La marca se fecha en UTC.", nameof(ahoraUtc));

        Estado = estado;
        ReferenciaFacturaEmisor = referencia;
        MarcadaPorUsuarioId = marcadaPorUsuarioId;
        MarcadaEnUtc = ahoraUtc;
    }
}
