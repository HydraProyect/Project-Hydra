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
/// Invariantes: los valores de enumeración son de la lista; el Pagador TALVEG no está vacío y solo lleva
/// <c>[A-Za-z0-9_]</c>; el inicio y el fin de la vigencia se registran cuando ya han ocurrido, nunca por
/// adelantado, así que el estado coincide siempre con <see cref="EstaVigenteEn"/> en el instante actual;
/// <see cref="VigenteDesdeUtc"/> y <see cref="VigenteHastaUtc"/> van en UTC;
/// <see cref="EstadoProductoContratado.Vigente"/> no tiene fecha de fin y
/// <see cref="EstadoProductoContratado.Finalizado"/> la tiene, no anterior al inicio; lo contratado
/// (línea, Pagador TALVEG, tramo, modalidad e inicio) no cambia después de contratar, y Finalizado es final.
/// La base repite los CHECK de valores y de coherencia entre estado y vigencia.
/// </para>
/// <para>
/// Un solo flujo lo crea hoy: <c>ContratarLineaInboundCommand</c> (B-04a), que contrata la línea Inbound bajo una
/// Sesión Privilegiada de Aprovisionamiento sobre el Tenant beneficiario, con un Producto Contratado de esa línea
/// vigente como mucho por Tenant (D-B). <c>CompletarAltaAutonomaCommand</c> (A6) creará las de Outbound y abrirá su
/// propia vía. Ninguna vía puede dejar que el propio Tenant beneficiario se conceda la línea Inbound (detalle en
/// <c>ProductoContratadoConfiguration</c>).
/// </para>
/// <para>
/// Gate comercial de Inbound (DN-7, opción (a)): las funciones de lectura entre Tenants <c>app_inbound_*</c> solo
/// responden al Tenant lector que tenga un Producto Contratado de <see cref="LineaProductoContratado.Inbound"/>
/// <see cref="EstadoProductoContratado.Vigente"/>, no eliminado y con el inicio ya alcanzado; lo comprueba la base,
/// en <c>app_inbound_tenant_contratista_del_contrato</c>. Cuenta el Tenant beneficiario (el
/// <see cref="EntidadConTenant.TenantId"/>), nunca el Pagador TALVEG. La línea Inbound es «a presupuesto» (D-1): sin
/// precio de catálogo; lleva los mismos datos obligatorios que cualquier línea, porque qué tramo y modalidad declara un
/// presupuesto Inbound no está decidido.
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

    /// <summary>
    /// Contrata un Producto Contratado, vigente desde <paramref name="vigenteDesdeUtc"/> y sin fecha de fin. El
    /// inicio no puede ser posterior a <paramref name="ahoraUtc"/>: un inicio futuro dejaría
    /// <see cref="EstadoProductoContratado.Vigente"/> algo que aún no está en vigor, y cómo se representa una
    /// contratación con inicio diferido no está decidido.
    /// </summary>
    public static ProductoContratado Contratar(
        LineaProductoContratado linea,
        string pagadorStripeCustomerId,
        TramoTrabajadoresActivos tramo,
        ModalidadSuscripcionAlta modalidad,
        DateTime vigenteDesdeUtc,
        DateTime ahoraUtc)
    {
        if (!Enum.IsDefined(linea))
            throw new ArgumentOutOfRangeException(nameof(linea), linea, "Línea de producto desconocida.");
        if (!Enum.IsDefined(tramo))
            throw new ArgumentOutOfRangeException(nameof(tramo), tramo, "Tramo desconocido.");
        if (!Enum.IsDefined(modalidad))
            throw new ArgumentOutOfRangeException(nameof(modalidad), modalidad, "Modalidad desconocida.");
        if (string.IsNullOrEmpty(pagadorStripeCustomerId) || !EsFormatoDeCustomerStripe(pagadorStripeCustomerId))
            throw new ArgumentException(
                "El Customer de Stripe del Pagador TALVEG es obligatorio y solo admite letras ASCII, dígitos y guion bajo.",
                nameof(pagadorStripeCustomerId));
        if (pagadorStripeCustomerId.Length > LongitudMaximaPagadorStripeCustomerId)
            throw new ArgumentException(
                $"El Customer de Stripe del Pagador TALVEG no puede pasar de {LongitudMaximaPagadorStripeCustomerId} caracteres.",
                nameof(pagadorStripeCustomerId));
        ExigirUtc(vigenteDesdeUtc, nameof(vigenteDesdeUtc));
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        if (vigenteDesdeUtc > ahoraUtc)
            throw new ArgumentOutOfRangeException(nameof(vigenteDesdeUtc), vigenteDesdeUtc,
                "El inicio de la vigencia no puede ser futuro.");

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
    /// Registra que la vigencia terminó en <paramref name="vigenteHastaUtc"/>: no anterior al inicio ni
    /// posterior a <paramref name="ahoraUtc"/>. Un fin futuro (por ejemplo, una baja al cierre del periodo
    /// pagado) no se registra por adelantado: dejaría <see cref="EstadoProductoContratado.Finalizado"/> un
    /// Producto Contratado que aún está en vigor, y cómo se programa una baja no está decidido. Solo desde
    /// <see cref="EstadoProductoContratado.Vigente"/>: un Producto Contratado finalizado no se reabre.
    /// </summary>
    public void Finalizar(DateTime vigenteHastaUtc, DateTime ahoraUtc)
    {
        if (Estado != EstadoProductoContratado.Vigente)
            throw new TransicionProductoContratadoNoPermitidaException(Estado, nameof(Finalizar));
        ExigirUtc(vigenteHastaUtc, nameof(vigenteHastaUtc));
        ExigirUtc(ahoraUtc, nameof(ahoraUtc));
        if (vigenteHastaUtc < VigenteDesdeUtc)
            throw new ArgumentOutOfRangeException(nameof(vigenteHastaUtc), vigenteHastaUtc,
                "El fin de la vigencia no puede ser anterior a su inicio.");
        if (vigenteHastaUtc > ahoraUtc)
            throw new ArgumentOutOfRangeException(nameof(vigenteHastaUtc), vigenteHastaUtc,
                "El fin de la vigencia no puede ser futuro: se registra cuando ya ha ocurrido.");

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

    /// <summary>
    /// Si <see cref="Contratar"/> aceptaría este Customer de Stripe como Pagador TALVEG: no vacío, solo
    /// <c>[A-Za-z0-9_]</c> y como mucho <see cref="LongitudMaximaPagadorStripeCustomerId"/> caracteres. No comprueba que
    /// el Customer exista en Stripe.
    /// </summary>
    public static bool EsPagadorStripeCustomerIdValido(string? pagadorStripeCustomerId) =>
        !string.IsNullOrEmpty(pagadorStripeCustomerId)
        && pagadorStripeCustomerId.Length <= LongitudMaximaPagadorStripeCustomerId
        && EsFormatoDeCustomerStripe(pagadorStripeCustomerId);

    /// <summary>
    /// Mismo criterio que el CHECK <c>CK_ProductosContratados_PagadorFormato</c> de la base: solo
    /// <c>[A-Za-z0-9_]</c>, el alfabeto de los Id de Stripe. Sin espacios, ni siquiera en los extremos.
    /// </summary>
    private static bool EsFormatoDeCustomerStripe(string valor)
    {
        foreach (var c in valor)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    private static void ExigirUtc(DateTime valor, string parametro)
    {
        if (valor.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La fecha debe ir en UTC.", parametro);
    }
}
