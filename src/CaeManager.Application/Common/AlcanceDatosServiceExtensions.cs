namespace CaeManager.Application.Common;

/// <summary>
/// Envuelve <see cref="IAlcanceDatosService"/> con comprobaciones booleanas
/// "¿es visible este Id concreto para el usuario actual?" — centraliza la
/// semántica null=sin restricción / lista=cartera restringida (incluida
/// lista vacía = cartera sin asignar todavía) para que cada consulta
/// `*PorId*` no tenga que repetirla a mano ni arriesgarse a invertirla por
/// error (ver Issue #18: antes de esto, ninguna consulta `*PorId*`
/// comprobaba el alcance del usuario sobre la entidad pedida — un Gestor
/// CAE o un usuario Cliente podían leer cualquier fila fuera de su cartera
/// con solo conocer/adivinar el Guid).
///
/// Devuelve `false` — nunca lanza — cuando el Id no es visible; el handler
/// que llama debe tratarlo igual que "no existe" (devolver null), nunca
/// como un error de autorización explícito, para no confirmar a quien no
/// debería verla que la fila sí existe.
/// </summary>
public static class AlcanceDatosServiceExtensions
{
    public static async Task<bool> ClienteVisibleAsync(
        this IAlcanceDatosService alcance, Guid clienteId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(clienteId);
    }

    /// <summary>
    /// Alcance sobre una entidad cuyo cliente es opcional: una
    /// <c>Conversacion</c> todavía en la cola de triage (§ 12.4) o una
    /// <c>MacroRespuesta</c> genérica del tenant. En ambos casos "sin cliente"
    /// significa compartido dentro del tenant, así que es visible — quién
    /// llega a esas pantallas lo decide el rol, no la cartera (ver
    /// <c>RolesComunicaciones</c>).
    ///
    /// Método propio en vez de repetir la condición en cada handler: invertir
    /// por error el caso "sin cliente" abre justo el agujero que esto cierra
    /// (hallazgo N-3 de INFORME-AUDITORIA-2.md).
    /// </summary>
    public static async Task<bool> ClienteOpcionalVisibleAsync(
        this IAlcanceDatosService alcance, Guid? clienteId, CancellationToken cancellationToken = default)
    {
        if (clienteId is null) return true;
        return await alcance.ClienteVisibleAsync(clienteId.Value, cancellationToken);
    }

    public static async Task<bool> CentroVisibleAsync(
        this IAlcanceDatosService alcance, Guid centroId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(centroId);
    }

    public static async Task<bool> EmpresaVisibleAsync(
        this IAlcanceDatosService alcance, Guid empresaId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(empresaId);
    }

    public static async Task<bool> SubcontrataVisibleAsync(
        this IAlcanceDatosService alcance, Guid subcontrataId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(subcontrataId);
    }

    public static async Task<bool> TrabajadorVisibleAsync(
        this IAlcanceDatosService alcance, Guid trabajadorId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(trabajadorId);
    }

    public static async Task<bool> VehiculoVisibleAsync(
        this IAlcanceDatosService alcance, Guid vehiculoId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(vehiculoId);
    }

    /// <summary>
    /// Alcance completo de una <c>Conversacion</c> existente: la cartera de
    /// su ancla —Cliente (<paramref name="clienteId"/>) o Empresa contraparte
    /// (<paramref name="empresaId"/>), excluyentes entre sí; sin ninguna de
    /// las dos es triage compartido, vía <see cref="ClienteOpcionalVisibleAsync"/>—
    /// Y, si el hilo está atado a un buzón personal de un gestor
    /// (<c>ConexionIntegracion.GestorPropietarioId</c>), que sea el propio
    /// dueño. Sin esto, un hilo de un buzón personal ajeno —que también tiene
    /// ClienteId null— se colaba en la cola de triage compartida como si
    /// fuera de cualquiera.
    ///
    /// <paramref name="empresaId"/> es obligatorio en la firma, y no un
    /// parámetro opcional, a propósito: un llamador que se olvidara de
    /// pasarlo trataría un hilo anclado a una Empresa como triage compartido
    /// —visible para toda la gestión CAE— sin que nada lo delatara. Que el
    /// compilador lo exija es lo que impide esa migración a medias.
    /// </summary>
    public static async Task<bool> ConversacionVisibleAsync(
        this IAlcanceDatosService alcance, Guid? clienteId, Guid? empresaId, Guid? conexionIntegracionId,
        CancellationToken cancellationToken = default)
    {
        if (conexionIntegracionId is { } conexionId && !await alcance.ConexionIntegracionVisibleAsync(conexionId, cancellationToken))
            return false;

        if (empresaId is { } id)
            return await alcance.EmpresaVisibleAsync(id, cancellationToken);

        return await alcance.ClienteOpcionalVisibleAsync(clienteId, cancellationToken);
    }
}
