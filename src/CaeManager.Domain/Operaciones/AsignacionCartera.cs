namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Qué usuario del operador responde de qué parte del ámbito de una
/// <see cref="AsignacionOperacion"/> — el nivel de persona de la asignación de
/// responsabilidad operativa (ADR-011 § 2.7).
///
/// <b>La cartera de un Gestor CAE es siempre el Tenant entero</b> (ámbito
/// universal; ADR-011 § 2.7, enmienda 2026-09-23 y D-7, 2026-10-02): el reparto
/// por Cliente empresarial (ámbito {relaciónCliente}) está retirado y ningún
/// productor lo escribe ya. <c>Empresa.EjecutivoUsuarioId</c> es solo la
/// referencia del Gestor CAE de un Cliente empresarial y no concede alcance.
/// Sustituye conceptualmente a <c>AsignacionOperadorDelegado</c> de una
/// delegación Comercial (cartera externa, con rol propio), que durante F1
/// sigue viva y escrita en paralelo.
///
/// <b>El ámbito efectivo es la intersección</b> del ámbito de esta cartera con
/// el de su operación — no una validación de subconjunto. La contención entre
/// dimensiones distintas no es decidible sin mirar los datos ("¿el trabajador A
/// está dentro de la relación con Iberojet?" depende de sus participaciones de
/// hoy), y no hace falta que lo sea: intersecar da siempre el resultado
/// correcto y no puede conceder de más.
///
/// <b>Qué es inmutable y qué no</b>: usuario, rol, ámbito y operador no cambian
/// nunca en una cartera; cambiarlos es cerrarla y abrir otra. Lo único mutable,
/// además del estado, es <see cref="EsPrincipal"/> (ADR-011 § 2.7, enmienda
/// 2026-10-08): la marca del Gestor CAE principal —o Coordinador CAE principal—
/// de la Asignación de Operación. La marca <b>no concede nada en datos</b>: una
/// cartera principal y una de apoyo tienen el mismo ámbito efectivo.
/// <see cref="EraPrincipalAlCerrarsePorCascada"/> no es mutable en ese sentido: se
/// escribe una sola vez, con el cierre.
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

    /// <summary>
    /// Roles de cartera que pueden llevar la marca de principal. Texto plano por el mismo
    /// motivo que <see cref="Rol"/> (Domain no referencia los roles de Identity); los repite
    /// el CHECK <c>CK_AsignacionesCartera_PrincipalSoloGestorCaeTenantEntero</c> y un test de
    /// integración los ata a las constantes de Identity. Consulta nunca es principal.
    /// </summary>
    public static readonly IReadOnlyList<string> RolesQuePuedenSerPrincipal = ["GestorCae", "CoordinadorCae"];

    /// <summary>
    /// Marca del Gestor CAE principal (o Coordinador CAE principal) de la Asignación de
    /// Operación: quién responde de ese Tenant ante el Operador CAE. <b>Como máximo una
    /// cartera no cerrada por operación</b> la lleva; lo impone la base de datos con el
    /// índice único parcial <c>IX_AsignacionesCartera_PrincipalPorOperacion</c>, porque el
    /// dominio ve una cartera y no sus hermanas. No es un rol ni amplía el ámbito efectivo.
    ///
    /// Ese índice no es diferible: pasar la marca de una cartera a otra exige apagar la
    /// primera y guardar, y encender la segunda y guardar, dentro de una transacción.
    /// </summary>
    public bool EsPrincipal { get; private set; }

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
        // Invariante de D-7 (2026-10-02): la cartera de un Gestor CAE es siempre el Tenant entero. Una
        // cartera NO cerrada nunca se reparte por Cliente empresarial. Lo repite la base de datos con el
        // CHECK CK_AsignacionesCartera_TenantEnteroSalvoCerrada (una cartera por Cliente empresarial
        // solo puede existir ya como historia cerrada, que EF rehidrata sin pasar por este constructor).
        // Lo acotado vive en la Asignación de Operación, no en la cartera.
        if (ambito.RelacionClienteId is not null)
            throw new ArgumentException(
                "La cartera de un Gestor CAE es siempre el Tenant entero: no se reparte por Cliente empresarial " +
                "(D-7). Para acotar el alcance se acota la Asignación de Operación.", nameof(ambito));

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
    /// Enciende la marca de principal. Solo sobre una cartera no cerrada, del Tenant entero
    /// y con rol Gestor CAE o Coordinador CAE; sin rol propio solo en una operación interna,
    /// donde vale el de Identity y quien llama responde de que sea uno de esos dos. Que no
    /// haya ya otra principal bajo la misma operación lo comprueba quien llama y lo impone
    /// el índice único. Idempotente.
    /// </summary>
    public void DesignarPrincipal()
    {
        if (Estado == EstadoAsignacion.Cerrada)
            throw new InvalidOperationException("Una cartera cerrada no puede ser la principal.");
        if (!Ambito.EsUniversal)
            throw new InvalidOperationException("Solo una cartera del Tenant entero puede ser la principal.");
        if (Rol is null ? !EsOperacionInterna : !RolesQuePuedenSerPrincipal.Contains(Rol))
            throw new InvalidOperationException(
                $"Solo una cartera de Gestor CAE o de Coordinador CAE puede ser la principal (rol: {Rol ?? "sin rol"}).");

        EsPrincipal = true;
    }

    /// <summary>Apaga la marca de principal. Idempotente; la cartera sigue viva y con el mismo ámbito efectivo.</summary>
    public void DejarDeSerPrincipal() => EsPrincipal = false;

    /// <summary>
    /// Si esta cartera llevaba la marca de principal en el instante en que la cerró la cascada
    /// de su Asignación de Operación (la delegación se desactivó). Es <b>histórico</b>, no la
    /// marca: una cartera cerrada sigue sin ser la principal de nada y no ocupa el índice
    /// único. Sirve para que, al reactivar la delegación, vuelva a ser principal quien lo era
    /// (decisión del propietario, 2026-10-08, D-9).
    ///
    /// Solo lo escribe <see cref="CerrarPorCascadaDeLaOperacion"/>, y solo en una cartera
    /// <see cref="RestaurableAlReactivar"/>: lo que no vuelve no tiene marca que devolver. La retirada individual, la
    /// expiración por fecha y cualquier otro cierre lo dejan en <c>false</c>: ahí el principal
    /// se releva en el acto y no hay nada que restaurar. Lo repite la base de datos con el
    /// CHECK <c>CK_AsignacionesCartera_EraPrincipalSoloCerrada</c>.
    /// </summary>
    public bool EraPrincipalAlCerrarsePorCascada { get; private set; }

    /// <summary>
    /// Histórico de una cartera cerrada: el estado efectivo de su autorización en el instante del
    /// cierre. Es el suyo, salvo en la cascada de una operación que no estaba Vigente: entonces una
    /// cartera Vigente lleva el de su operación, porque no autorizaba nada (una Vigente bajo una
    /// operación Suspendida estaba, en la práctica, suspendida). <c>null</c> en una cartera viva y en
    /// las cerradas antes de que existiera la columna (ronda 2 de Codex en la PR #2223).
    ///
    /// Lo lee quien emite una cartera nueva desde una fila heredada (<see cref="QuedoSuspendida"/>):
    /// lo suspendido nunca vuelve como vigente. Lo repite la base de datos con el CHECK
    /// <c>CK_AsignacionesCartera_EstadoAlCerrarseSoloCerrada</c>.
    /// </summary>
    public EstadoAsignacion? EstadoAlCerrarse { get; private set; }

    /// <summary>
    /// Marca explícita de lo que la reactivación de la delegación puede reponer: solo la escribe
    /// <see cref="CerrarPorCascadaDeLaOperacion"/>, y solo cuando la cartera y su operación estaban
    /// las dos Vigentes y el motivo es <see cref="MotivoCierreAsignacion.Revocada"/>. Una cartera Suspendida o Programada, una Vigente bajo una operación Suspendida
    /// o Programada, y cualquier cierre individual (retirada, expiración, reconciliación) la dejan en
    /// <c>false</c> y no vuelven. Sustituye a la inferencia por fechas («comparte el fin de la
    /// operación»), que confundía con la cascada una cartera Suspendida con la misma fecha de fin ya
    /// pasada (ronda 2 de Codex en la PR #2223). Lo repite la base de datos con el CHECK
    /// <c>CK_AsignacionesCartera_RestaurableSoloCerradaVigente</c>.
    /// </summary>
    public bool RestaurableAlReactivar { get; private set; }

    /// <summary>
    /// Una cartera cerrada no es la principal de nada: el cierre apaga la marca. Recuerda además el
    /// estado que tenía (<see cref="EstadoAlCerrarse"/>).
    /// </summary>
    public override void Cerrar(MotivoCierreAsignacion motivo, DateTime ahora) => CerrarConEstado(motivo, ahora, Estado);

    private void CerrarConEstado(MotivoCierreAsignacion motivo, DateTime ahora, EstadoAsignacion estadoEfectivo)
    {
        base.Cerrar(motivo, ahora);
        EsPrincipal = false;
        EstadoAlCerrarse = estadoEfectivo;
    }

    /// <summary>
    /// El cierre que arrastra la cascada de su Asignación de Operación, antes de cerrar esta. Cierra
    /// igual que <see cref="Cerrar"/> —la marca se apaga—, recuerda el estado efectivo
    /// (<see cref="EstadoAlCerrarse"/>) y, si es una revocación y la cartera y la operación
    /// estaban las dos Vigentes, la marca como <see cref="RestaurableAlReactivar"/> y recuerda si era
    /// la principal (<see cref="EraPrincipalAlCerrarsePorCascada"/>, D-9). En cualquier otro caso se
    /// cierra sin nada que restaurar: en duda, falla cerrada.
    /// </summary>
    public void CerrarPorCascadaDeLaOperacion(
        AsignacionOperacion operacion, MotivoCierreAsignacion motivo, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (operacion.Id != AsignacionOperacionId)
            throw new ArgumentException("La cascada solo la arrastra la operación de la que cuelga la cartera.", nameof(operacion));
        if (operacion.Estado == EstadoAsignacion.Cerrada)
            throw new InvalidOperationException(
                "La cascada cierra las carteras antes que su operación: con la operación ya cerrada no se sabe en qué estado estaba.");

        var eraPrincipal = EsPrincipal;
        var efectivo = Estado == EstadoAsignacion.Vigente ? operacion.Estado : Estado;
        CerrarConEstado(motivo, ahora, efectivo);

        // Solo una revocación se deshace al reactivar la delegación: una cascada por caducidad o por la
        // reconciliación del backfill recuerda el estado efectivo, pero no deja nada que reponer.
        RestaurableAlReactivar = efectivo == EstadoAsignacion.Vigente && motivo == MotivoCierreAsignacion.Revocada;
        EraPrincipalAlCerrarsePorCascada = RestaurableAlReactivar && eraPrincipal;
    }

    /// <summary>
    /// Si esta cartera estaba, en la práctica, suspendida: viva y Suspendida, viva y Vigente bajo una
    /// operación Suspendida, o cerrada desde un estado efectivo Suspendido
    /// (<see cref="EstadoAlCerrarse"/>). Quien emite una cartera desde una fila heredada lo pregunta a
    /// la última cartera de la persona en el par para no devolver como vigente lo suspendido.
    /// </summary>
    public bool QuedoSuspendida(AsignacionOperacion operacion)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (operacion.Id != AsignacionOperacionId)
            throw new ArgumentException("La operación no es la de la cartera.", nameof(operacion));

        return Estado switch
        {
            EstadoAsignacion.Suspendida => true,
            EstadoAsignacion.Vigente => operacion.Estado == EstadoAsignacion.Suspendida,
            EstadoAsignacion.Cerrada => EstadoAlCerrarse == EstadoAsignacion.Suspendida,
            _ => false,
        };
    }

    /// <summary>
    /// Si, entre las carteras de una persona en un par (Tenant propietario, Operador CAE externo), hay
    /// alguna viva que <see cref="QuedoSuspendida"/> o la última —por creación— quedó suspendida. Es la
    /// guarda común del writer y del backfill antes de emitir una cartera desde la fila heredada: lo
    /// suspendido nunca vuelve como vigente (ronda 2 de Codex en la PR #2223).
    /// </summary>
    public static bool AlgunaQuedoSuspendida(
        IEnumerable<(AsignacionCartera Cartera, AsignacionOperacion Operacion)> carterasDeLaPersonaEnElPar)
    {
        ArgumentNullException.ThrowIfNull(carterasDeLaPersonaEnElPar);
        var lista = carterasDeLaPersonaEnElPar.ToList();

        if (lista.Any(x => x.Cartera.Estado != EstadoAsignacion.Cerrada && x.Cartera.QuedoSuspendida(x.Operacion)))
            return true;

        var ultima = lista
            .OrderByDescending(x => x.Cartera.CreadoEnUtc)
            .ThenByDescending(x => x.Cartera.VigenciaDesde)
            .FirstOrDefault();
        return ultima.Cartera is not null && ultima.Cartera.QuedoSuspendida(ultima.Operacion);
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
