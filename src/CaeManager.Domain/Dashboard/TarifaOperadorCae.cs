using CaeManager.Domain.Common;

namespace CaeManager.Domain.Dashboard;

/// <summary>
/// Lo que un Operador CAE externo cobra a una Empresa gestionada (Tenant beneficiario) cada mes. No es
/// <see cref="Facturacion.ConceptoFacturable"/>: aquel es lo que un Tenant cobra a sus Clientes empresariales
/// (pantalla «Facturación»); este es la relación comercial del Operador CAE con cada Empresa que gestiona
/// (decisión de Chris, 2026-10-20). El Cliente empresarial nuevo no se cobra: es un indicador de crecimiento.
/// </summary>
public enum ConceptoTarifaOperadorCae
{
    /// <summary>Trabajadores extranjeros gestionados en el mes (la cifra «Extranjeros» del Cuadro de dirección).</summary>
    TrabajadorExtranjeroGestionado = 1,

    /// <summary>Centros de trabajo dados de alta en el mes.</summary>
    CentroNuevo = 2,

    /// <summary>Proyectos de tipo Obra dados de alta en el mes; los Eventos no cuentan.</summary>
    ObraNueva = 3,
}

/// <summary>
/// Una versión de la tarifa que el Operador CAE externo (<see cref="EntidadConTenant.TenantId"/>, el Tenant
/// propietario de la fila) aplica a una Empresa gestionada (<see cref="PropietarioTenantId"/>, el Tenant
/// beneficiario) por un <see cref="ConceptoTarifaOperadorCae"/>.
///
/// <para>
/// <b>Plano Comercial</b>: la fila es del Operador CAE, no de la Empresa gestionada. La RLS estándar por
/// <c>TenantId</c> la deja fuera del alcance del Tenant beneficiario, y solo el Administrador o la Dirección CAE
/// del Operador la leen o la cambian (<c>AutorizacionTarifasOperadorCae</c>).
/// </para>
///
/// <para>
/// <b>Historial, nunca edición</b>: cambiar el precio, la moneda o activar y desactivar el cobro añade una versión
/// con <see cref="VigenteDesdeUtc"/>; ninguna versión se modifica ni se borra. Así el cierre mensual congela la
/// tarifa sin copiarla a una fila del Tenant beneficiario: un mes cerrado se valora con la versión vigente en el
/// instante de su cierre, y un cambio posterior no lo altera.
/// </para>
/// </summary>
public class TarifaOperadorCae : EntidadConTenant
{
    public const int LongitudMonedaIso = 3;

    public Guid PropietarioTenantId { get; private set; }
    public ConceptoTarifaOperadorCae Concepto { get; private set; }
    public decimal PrecioUnitario { get; private set; }
    public string MonedaIso { get; private set; } = "EUR";

    /// <summary>Si el concepto se cobra. Desactivado, las unidades se siguen mostrando pero no se valoran.</summary>
    public bool Activa { get; private set; }

    public DateTime VigenteDesdeUtc { get; private set; }
    public Guid RegistradaPorUsuarioId { get; private set; }

    private TarifaOperadorCae() { }

    public static TarifaOperadorCae Registrar(
        Guid propietarioTenantId, ConceptoTarifaOperadorCae concepto, decimal precioUnitario, string monedaIso,
        bool activa, Guid registradaPorUsuarioId, DateTime ahoraUtc)
    {
        if (propietarioTenantId == Guid.Empty)
            throw new ArgumentException("La tarifa debe ser de una Empresa gestionada.", nameof(propietarioTenantId));
        if (!Enum.IsDefined(concepto))
            throw new ArgumentException("Concepto de tarifa desconocido.", nameof(concepto));
        if (precioUnitario < 0)
            throw new ArgumentException("El precio unitario no puede ser negativo.", nameof(precioUnitario));
        if (decimal.Round(precioUnitario, 2) != precioUnitario || precioUnitario > 999_999.99m)
            throw new ArgumentException("El precio unitario lleva como mucho dos decimales y no pasa de 999.999,99.", nameof(precioUnitario));
        if (string.IsNullOrWhiteSpace(monedaIso) || monedaIso.Trim().Length != LongitudMonedaIso)
            throw new ArgumentException("La moneda debe ser un código ISO de 3 caracteres (p. ej. EUR).", nameof(monedaIso));
        if (registradaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La tarifa la registra una persona.", nameof(registradaPorUsuarioId));
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La vigencia se fija en UTC.", nameof(ahoraUtc));

        return new TarifaOperadorCae
        {
            PropietarioTenantId = propietarioTenantId,
            Concepto = concepto,
            PrecioUnitario = precioUnitario,
            MonedaIso = monedaIso.Trim().ToUpperInvariant(),
            Activa = activa,
            VigenteDesdeUtc = ahoraUtc,
            RegistradaPorUsuarioId = registradaPorUsuarioId,
        };
    }
}
