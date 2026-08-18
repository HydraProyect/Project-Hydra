namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Qué usuario del operador responde de qué parte del ámbito de una
/// <see cref="AsignacionOperacion"/> — el nivel de persona de la asignación de
/// responsabilidad operativa (ADR-011 § 2.7).
///
/// Sustituye conceptualmente a <c>Cliente.EjecutivoUsuarioId</c> (cartera
/// interna, ámbito {relaciónCliente}, sobre la raíz) y a
/// <c>AsignacionOperadorDelegado</c> de una delegación Comercial (cartera
/// externa, ámbito universal, con rol propio). Durante F1 los dos originales
/// siguen vivos y escritos en paralelo.
///
/// <b>El ámbito efectivo es la intersección</b> del ámbito de esta cartera con
/// el de su operación — no una validación de subconjunto. La contención entre
/// dimensiones distintas no es decidible sin mirar los datos ("¿el trabajador A
/// está dentro de la relación con Iberojet?" depende de sus participaciones de
/// hoy), y no hace falta que lo sea: intersecar da siempre el resultado
/// correcto y no puede conceder de más.
/// </summary>
public class AsignacionCartera : AsignacionResponsabilidad
{
    public Guid AsignacionOperacionId { get; private set; }

    /// <summary>
    /// Guid suelto hacia <c>ApplicationUser</c>: Identity vive en
    /// Infrastructure y Domain no la referencia — mismo patrón que
    /// <c>AsignacionOperadorDelegado.UsuarioId</c>.
    ///
    /// El invariante de que este usuario pertenece a
    /// <c>OperadorTenantId</c> no puede imponerlo el dominio por esa misma
    /// separación de capas: lo garantizan la validación explícita del comando
    /// de alta, un test de integridad dedicado, y el backfill, que comprueba la
    /// pertenencia en vez de confiar (pueden existir filas legadas que ya la
    /// violan).
    /// </summary>
    public Guid UsuarioId { get; private set; }

    /// <summary>
    /// Rol efectivo dentro del workspace. <c>null</c> significa "usar el rol de
    /// Identity del usuario", que es lo correcto en una cartera interna: el
    /// usuario opera en su propio tenant con el rol que ya tiene.
    ///
    /// En una cartera externa es obligatorio y es lo que evita el fallo que
    /// corrigió en su día el rol efectivo: un usuario que es Administrador en
    /// su tenant no puede llevarse ese rol al tenant ajeno que opera. Código en
    /// texto plano, no enum, porque Domain no referencia los roles de Identity;
    /// la validación de que sea un rol conocido vive en el validador del
    /// comando.
    /// </summary>
    public string? Rol { get; private set; }

    public const int LongitudMaximaRol = 50;

    private AsignacionCartera()
    {
        // Requerido por EF Core.
    }

    private AsignacionCartera(
        AsignacionOperacion operacion,
        Guid usuarioId,
        string? rol,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La cartera debe tener un usuario.", nameof(usuarioId));
        if (operacion.Estado == EstadoAsignacion.Cerrada)
            throw new ArgumentException("No se puede colgar una cartera de una operación cerrada.", nameof(operacion));

        AsignacionOperacionId = operacion.Id;
        // Denormalizados desde la operación, no desde el llamante: la FK
        // compuesta (AsignacionOperacionId, PropietarioTenantId) los ata a la
        // operación en la base de datos, así que copiarlos de otro sitio sería
        // un error que la BD rechazaría.
        PropietarioTenantId = operacion.PropietarioTenantId;
        OperadorTenantId = operacion.OperadorTenantId;
        UsuarioId = usuarioId;
        Rol = string.IsNullOrWhiteSpace(rol) ? null : rol.Trim();
        CreadoPorUsuarioId = creadoPorUsuarioId;
        EstablecerAmbito(ambito);
        EstablecerVigencia(vigenciaDesde, vigenciaHasta, ahora);
    }

    /// <summary>
    /// Cartera de un usuario del propio tenant sobre una operación interna
    /// (normalmente la raíz). Sin rol propio: vale el de Identity.
    /// </summary>
    /// <param name="rol">
    /// Normalmente <c>null</c>: en su propio tenant el usuario opera con el rol
    /// que ya tiene. Se admite un rol explícito para el caso de migración en el
    /// que la fila de origen lo traía fijado y perderlo cambiaría lo que ese
    /// usuario puede hacer.
    /// </param>
    public static AsignacionCartera Interna(
        AsignacionOperacion operacion,
        Guid usuarioId,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId = null,
        string? rol = null)
    {
        if (!operacion.EsOperacionInterna)
            throw new ArgumentException(
                "Una cartera interna exige una operación interna.", nameof(operacion));

        return new AsignacionCartera(
            operacion, usuarioId, rol, ambito, vigenciaDesde, vigenciaHasta, ahora, creadoPorUsuarioId);
    }

    /// <summary>
    /// Cartera de un usuario del tenant operador sobre una operación externa.
    /// El rol es obligatorio y acota lo que ese usuario puede hacer dentro del
    /// workspace delegado, con independencia de su rol en su propio tenant.
    /// </summary>
    public static AsignacionCartera Externa(
        AsignacionOperacion operacion,
        Guid usuarioId,
        string rol,
        AmbitoAsignacion ambito,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        DateTime ahora,
        Guid? creadoPorUsuarioId = null)
    {
        if (operacion.EsOperacionInterna)
            throw new ArgumentException(
                "Una cartera externa exige una operación externa.", nameof(operacion));
        if (string.IsNullOrWhiteSpace(rol))
            throw new ArgumentException("Una cartera externa debe fijar un rol efectivo.", nameof(rol));

        return new AsignacionCartera(
            operacion, usuarioId, rol, ambito, vigenciaDesde, vigenciaHasta, ahora, creadoPorUsuarioId);
    }
}
