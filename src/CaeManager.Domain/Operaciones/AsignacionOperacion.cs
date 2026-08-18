namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Qué organización opera un ámbito de la operación CAE de un tenant, en qué
/// servicio y durante cuánto tiempo — el nivel de organización de la asignación
/// de responsabilidad operativa (ADR-011 § 2.7).
///
/// <b>Una operación no concede acceso a ningún usuario por sí sola.</b> Que
/// ArcosSPA tenga una operación sobre Refrielectric no significa que todos los
/// usuarios de ArcosSPA vean Refrielectric: significa que los usuarios de
/// ArcosSPA <i>con <see cref="AsignacionCartera"/> vigente bajo esa operación</i>
/// ven su ámbito efectivo. La cadena completa está en el plan de migración § 0
/// (P2) y la comprueba el middleware de revalidación en cada petición.
///
/// Generaliza a la vez los dos mecanismos que hoy están separados y que no
/// sabían expresar ámbito: <c>DelegacionTenant</c> de propósito Comercial (una
/// operación externa de ámbito universal) y <c>Cliente.EjecutivoUsuarioId</c>
/// (una cartera sobre la operación raíz). Durante F1 ambos siguen vivos como
/// proyección de compatibilidad.
/// </summary>
public class AsignacionOperacion : AsignacionResponsabilidad
{
    /// <summary>
    /// Marca la operación que el propietario tiene sobre sí mismo. Existe una
    /// por (tenant, servicio) mientras el producto esté activo, y se materializa
    /// como fila real — no como regla implícita — porque las carteras internas
    /// necesitan una FK de verdad a la que colgarse.
    ///
    /// <b>La raíz es el fallback del propietario, no un operador que compita.</b>
    /// Su cobertura efectiva es "todo lo que ninguna otra asignación vigente
    /// cubre", así que queda fuera de la detección de conflictos: si participara,
    /// delegar todo a una consultora — el caso más común del negocio — sería un
    /// choque permanente de cobertura idéntica contra ella. Tampoco es un
    /// workspace seleccionable: la entrada del tenant propio sale del claim de
    /// sesión, como siempre.
    /// </summary>
    public bool EsRaiz { get; private set; }

    public ServicioCae Servicio { get; private set; }

    private AsignacionOperacion()
    {
        // Requerido por EF Core.
    }

    private AsignacionOperacion(
        Guid propietarioTenantId,
        Guid operadorTenantId,
        ServicioCae servicio,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId)
    {
        if (propietarioTenantId == Guid.Empty)
            throw new ArgumentException("La operación debe tener un tenant propietario.", nameof(propietarioTenantId));
        if (operadorTenantId == Guid.Empty)
            throw new ArgumentException("La operación debe tener un tenant operador.", nameof(operadorTenantId));

        PropietarioTenantId = propietarioTenantId;
        OperadorTenantId = operadorTenantId;
        Servicio = servicio;
        CreadoPorUsuarioId = creadoPorUsuarioId;
        EstablecerAmbito(ambito);
        EstablecerVigencia(vigenciaDesde, vigenciaHasta, ahora);
    }

    /// <summary>
    /// La operación del propietario sobre sí mismo. Siempre universal, siempre
    /// interna, sin fecha de fin: se cierra cuando se retira el producto.
    /// </summary>
    public static AsignacionOperacion Raiz(
        Guid propietarioTenantId,
        ServicioCae servicio,
        DateTime vigenciaDesde,
        DateTime ahora) =>
        new(propietarioTenantId, propietarioTenantId, servicio,
            AmbitoAsignacion.Universal, vigenciaDesde, vigenciaHasta: null, ahora, creadoPorUsuarioId: null)
        {
            EsRaiz = true
        };

    /// <summary>
    /// El propietario opera una parte acotada de su propia operación con su
    /// propio equipo, junto a operadores externos que llevan el resto — el caso
    /// "Cliente C lo llevamos nosotros, Iberojet lo lleva ArcosSPA".
    /// </summary>
    public static AsignacionOperacion Interna(
        Guid propietarioTenantId,
        ServicioCae servicio,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId = null)
    {
        if (ambito.EsUniversal)
            throw new ArgumentException(
                "Una operación interna de ámbito universal es la raíz: use AsignacionOperacion.Raiz.", nameof(ambito));

        return new AsignacionOperacion(
            propietarioTenantId, propietarioTenantId, servicio,
            ambito, vigenciaDesde, vigenciaHasta, ahora, creadoPorUsuarioId);
    }

    /// <summary>
    /// Otro tenant opera en nombre del propietario. El ámbito universal aquí sí
    /// es legítimo y es el caso habitual: la delegación completa a una
    /// consultora, que es lo que hoy representa una <c>DelegacionTenant</c>
    /// Comercial.
    /// </summary>
    public static AsignacionOperacion Externa(
        Guid propietarioTenantId,
        Guid operadorTenantId,
        ServicioCae servicio,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId = null)
    {
        if (operadorTenantId == propietarioTenantId)
            throw new ArgumentException(
                "Una operación externa exige un operador distinto del propietario.", nameof(operadorTenantId));

        return new AsignacionOperacion(
            propietarioTenantId, operadorTenantId, servicio,
            ambito, vigenciaDesde, vigenciaHasta, ahora, creadoPorUsuarioId);
    }
}
