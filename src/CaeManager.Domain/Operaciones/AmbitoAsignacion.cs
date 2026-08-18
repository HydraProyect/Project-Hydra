namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Sobre qué parte de la operación responde una asignación — ADR-011 § 2.7.
/// Conjunción de dimensiones opcionales: una asignación cubre un objeto cuando
/// <b>toda</b> dimensión presente coincide con las coordenadas de ese objeto.
///
/// <b>El ámbito vacío no significa "todo el tenant"</b>: significa todo el
/// ámbito operativo de <i>esa</i> asignación, y una asignación existe siempre
/// dentro de una terna (propietario, servicio, asignación). <c>Universal</c> en
/// una operación Outbound de Refrielectric es todo su Outbound; el mismo valor
/// en una Inbound de Iberojet es todo su Inbound. Ningún ámbito se interpreta
/// jamás fuera de su Tenant y su Servicio.
///
/// Es un tipo de paso: la entidad guarda las cuatro columnas sueltas (y no un
/// tipo <i>owned</i> de EF) porque cada una participa además en una FK
/// compuesta <c>(PropietarioTenantId, XxxId)</c> contra la clave alternativa
/// del agregado, que es lo que hace <b>físicamente imposible</b> apuntar a
/// datos de otro tenant.
///
/// <b>Alcance de F1</b>: solo se emiten <see cref="Universal"/> y
/// <see cref="DeRelacionCliente"/>. Las otras tres dimensiones existen como
/// columnas para no tener que migrar el esquema cuando entren, pero no hay UI
/// ni comandos que las creen — y habilitarlas exige antes la revisión de la
/// arquitectura de consulta que fija el plan de migración (§ 5).
/// </summary>
/// <param name="RelacionClienteId">La relación comercial con un cliente. Hoy, el <c>Cliente</c> del modelo actual.</param>
/// <param name="CentroId">Un centro de trabajo concreto. Reservado, sin uso en F1.</param>
/// <param name="TrabajadorId">Un trabajador concreto. Reservado, sin uso en F1.</param>
/// <param name="ProyectoId">Un proyecto o trabajo concreto. Reservado, sin uso en F1.</param>
public readonly record struct AmbitoAsignacion(
    Guid? RelacionClienteId = null,
    Guid? CentroId = null,
    Guid? TrabajadorId = null,
    Guid? ProyectoId = null)
{
    /// <summary>
    /// Sin dimensión ninguna: toda la operación. Es el ámbito de la operación
    /// raíz y el de una delegación total a una consultora.
    /// </summary>
    public static AmbitoAsignacion Universal => new();

    /// <summary>El ámbito de F1: la cartera de un cliente concreto.</summary>
    public static AmbitoAsignacion DeRelacionCliente(Guid relacionClienteId)
    {
        if (relacionClienteId == Guid.Empty)
            throw new ArgumentException("La relación con el cliente no puede ser vacía.", nameof(relacionClienteId));

        return new AmbitoAsignacion(RelacionClienteId: relacionClienteId);
    }

    public bool EsUniversal =>
        RelacionClienteId is null && CentroId is null && TrabajadorId is null && ProyectoId is null;

    /// <summary>
    /// Cuántas dimensiones concreta. <b>No</b> es el criterio de precedencia
    /// entre asignaciones: esa es la contención extensional de coberturas
    /// (ADR-011 § 4.2), porque contar dimensiones declara empatados a
    /// <c>{centro Barcelona}</c> y <c>{relaciónCliente Iberojet}</c> cuando el
    /// primero está de hecho contenido en el segundo. Sirve para diagnóstico y
    /// para distinguir el ámbito universal, nada más.
    /// </summary>
    public int DimensionesConcretas =>
        (RelacionClienteId is null ? 0 : 1)
        + (CentroId is null ? 0 : 1)
        + (TrabajadorId is null ? 0 : 1)
        + (ProyectoId is null ? 0 : 1);

    /// <summary>
    /// Si este ámbito usa alguna de las dimensiones que F1 no habilita. Los
    /// comandos lo comprueban para rechazar en el alta lo que el sistema
    /// todavía no sabe expandir ni resolver.
    /// </summary>
    public bool UsaDimensionesDiferidas =>
        CentroId is not null || TrabajadorId is not null || ProyectoId is not null;
}
