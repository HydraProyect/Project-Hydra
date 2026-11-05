using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comercial;

/// <summary>
/// Producto Contratado mínimo del plano 4, comercial (RQ-30, D-6): QUÉ se contrató, para qué Tenant y quién
/// lo paga. Pertenece al Tenant beneficiario, que es su <see cref="EntidadConTenant.TenantId"/> (sellado al
/// guardar, como en toda entidad con Tenant) y lo aísla la política <c>aislamiento_tenant</c>.
/// <para>
/// El Pagador TALVEG es una referencia al Customer de Stripe y nada más: no se modela su organización, y no
/// es por definición el Tenant propietario ni tiene por qué coincidir con <c>Tenant.StripeCustomerId</c>.
/// </para>
/// <para>
/// No duplica el estado de cobro. <c>Tenant.EstadoComercial</c> sigue siendo la única fuente de si el
/// Tenant está al corriente (ADR-009) y lo único que lee el gate comercial; <see cref="Estado"/> dice si lo
/// contratado sigue en vigor. Por eso no guarda la Subscription de Stripe ni su estado.
/// </para>
/// <para>
/// Invariantes: los valores de enumeración son de la lista; el Pagador TALVEG no está vacío;
/// <see cref="VigenteDesdeUtc"/> y <see cref="VigenteHastaUtc"/> van en UTC;
/// <see cref="EstadoProductoContratado.Vigente"/> no tiene fecha de fin y
/// <see cref="EstadoProductoContratado.Finalizado"/> la tiene, no anterior al inicio; lo contratado
/// (línea, Pagador TALVEG, tramo, modalidad e inicio) no cambia después de contratar, y Finalizado es final.
/// La base repite los CHECK de valores y de coherencia entre estado y vigencia.
/// </para>
/// <para>
/// Ningún flujo lo crea todavía: lo hará <c>CompletarAltaAutonomaCommand</c> (A6), que abrirá también la vía
/// de escritura en la base. Hasta entonces la migración solo concede lectura.
/// </para>
/// </summary>
public class ProductoContratado : EntidadBase
{
    /// <summary>Longitud máxima del Id de Customer de Stripe, la misma que <c>Tenants.StripeCustomerId</c>.</summary>
    public const int LongitudMaximaPagadorStripeCustomerId = 100;

    private ProductoContratado()
    {
    }

    public LineaProductoContratado Linea { get; private set; }

    /// <summary>Pagador TALVEG: Id del Customer de Stripe que paga este Producto Contratado.</summary>
    public string PagadorStripeCustomerId { get; private set; } = null!;

    /// <summary>Tramo por Trabajadores activos declarado al contratar (DA-6).</summary>
    public TramoTrabajadoresActivos Tramo { get; private set; }

    /// <summary>Modalidad de pago (DA-8).</summary>
    public ModalidadSuscripcionAlta Modalidad { get; private set; }

    public DateTime VigenteDesdeUtc { get; private set; }

    /// <summary>Fin de la vigencia; null mientras está <see cref="EstadoProductoContratado.Vigente"/>.</summary>
    public DateTime? VigenteHastaUtc { get; private set; }

    public EstadoProductoContratado Estado { get; private set; }

    /// <summary>Contrata un Producto Contratado, vigente desde <paramref name="vigenteDesdeUtc"/> y sin fecha de fin.</summary>
    public static ProductoContratado Contratar(
        LineaProductoContratado linea,
        string pagadorStripeCustomerId,
        TramoTrabajadoresActivos tramo,
        ModalidadSuscripcionAlta modalidad,
        DateTime vigenteDesdeUtc)
    {
        if (!Enum.IsDefined(linea))
            throw new ArgumentOutOfRangeException(nameof(linea), linea, "Línea de producto desconocida.");
        if (!Enum.IsDefined(tramo))
            throw new ArgumentOutOfRangeException(nameof(tramo), tramo, "Tramo desconocido.");
        if (!Enum.IsDefined(modalidad))
            throw new ArgumentOutOfRangeException(nameof(modalidad), modalidad, "Modalidad desconocida.");
        if (string.IsNullOrWhiteSpace(pagadorStripeCustomerId))
            throw new ArgumentException("El Customer de Stripe del Pagador TALVEG es obligatorio.", nameof(pagadorStripeCustomerId));
        if (pagadorStripeCustomerId.Trim() != pagadorStripeCustomerId)
            throw new ArgumentException("El Customer de Stripe del Pagador TALVEG no admite espacios al principio ni al final.", nameof(pagadorStripeCustomerId));
        if (pagadorStripeCustomerId.Length > LongitudMaximaPagadorStripeCustomerId)
            throw new ArgumentException(
                $"El Customer de Stripe del Pagador TALVEG no puede pasar de {LongitudMaximaPagadorStripeCustomerId} caracteres.",
                nameof(pagadorStripeCustomerId));
        ExigirUtc(vigenteDesdeUtc, nameof(vigenteDesdeUtc));

        return new ProductoContratado
        {
            Linea = linea,
            PagadorStripeCustomerId = pagadorStripeCustomerId,
            Tramo = tramo,
            Modalidad = modalidad,
            VigenteDesdeUtc = vigenteDesdeUtc,
            VigenteHastaUtc = null,
            Estado = EstadoProductoContratado.Vigente,
        };
    }

    /// <summary>
    /// Termina la vigencia en <paramref name="vigenteHastaUtc"/>, que no puede ser anterior al inicio. Solo
    /// desde <see cref="EstadoProductoContratado.Vigente"/>: un Producto Contratado finalizado no se reabre.
    /// </summary>
    public void Finalizar(DateTime vigenteHastaUtc)
    {
        if (Estado != EstadoProductoContratado.Vigente)
            throw new TransicionProductoContratadoNoPermitidaException(Estado, nameof(Finalizar));
        ExigirUtc(vigenteHastaUtc, nameof(vigenteHastaUtc));
        if (vigenteHastaUtc < VigenteDesdeUtc)
            throw new ArgumentOutOfRangeException(nameof(vigenteHastaUtc), vigenteHastaUtc,
                "El fin de la vigencia no puede ser anterior a su inicio.");

        VigenteHastaUtc = vigenteHastaUtc;
        Estado = EstadoProductoContratado.Finalizado;
    }

    /// <summary>
    /// Si lo contratado estaba en vigor en <paramref name="instanteUtc"/>: desde el inicio, incluido, hasta el
    /// fin, excluido. No dice nada del cobro (<c>Tenant.EstadoComercial</c>).
    /// </summary>
    public bool EstaVigenteEn(DateTime instanteUtc)
    {
        ExigirUtc(instanteUtc, nameof(instanteUtc));
        return instanteUtc >= VigenteDesdeUtc && (VigenteHastaUtc is null || instanteUtc < VigenteHastaUtc);
    }

    private static void ExigirUtc(DateTime valor, string parametro)
    {
        if (valor.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La fecha debe ir en UTC.", parametro);
    }
}
