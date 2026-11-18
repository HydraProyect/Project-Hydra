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
        var ids = await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(clienteId);
    }

    /// <summary>
    /// La cartera de Clientes empresariales visible (<c>null</c> = sin restricción), con su nombre canónico. Es la misma
    /// lista que <see cref="IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync"/>; los consumidores nuevos la piden por
    /// aquí para no extender el identificador legacy (<c>ClienteIdNoSeExtiendeTests</c>).
    /// </summary>
    public static Task<IReadOnlyList<Guid>?> ClientesEmpresarialesVisiblesAsync(
        this IAlcanceDatosService alcance, CancellationToken cancellationToken = default) =>
        alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken);

    /// <summary>
    /// Alcance sobre una entidad no conversacional cuyo cliente es opcional:
    /// una <c>MacroRespuesta</c> genérica del tenant o una
    /// <c>ConexionIntegracion</c> (buzón) sin Cliente empresarial. Ahí "sin
    /// cliente" significa compartido dentro del tenant, así que es visible —
    /// quién llega a esas pantallas lo decide el rol, no la cartera.
    ///
    /// No sirve para una <c>Conversacion</c>: una conversación sin ancla solo
    /// la ve quien cumple <see cref="IAlcanceDatosService.TriajeVisibleAsync"/>
    /// o el propietario del buzón personal por el que entra; su regla es
    /// <see cref="ConversacionVisibleAsync"/> (D-6 b, R-3a).
    ///
    /// Método propio en vez de repetir la condición en cada handler: invertir
    /// por error el caso "sin cliente" abre justo el agujero que esto cierra
    /// (hallazgo N-3 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md).
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

    /// <summary>Alcance de gestión sobre un Centro concreto — ver <see cref="IAlcanceDatosService.ObtenerCentroIdsParaGestionAsync"/>.</summary>
    public static async Task<bool> CentroParaGestionVisibleAsync(
        this IAlcanceDatosService alcance, Guid centroId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        return ids is null || ids.Contains(centroId);
    }

    public static async Task<bool> EmpresaVisibleAsync(
        this IAlcanceDatosService alcance, Guid empresaId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(empresaId);
    }

    /// <summary>Alcance de gestión sobre una Empresa concreta — ver <see cref="IAlcanceDatosService.ObtenerEmpresaIdsParaGestionAsync"/>.</summary>
    public static async Task<bool> EmpresaParaGestionVisibleAsync(
        this IAlcanceDatosService alcance, Guid empresaId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        return ids is null || ids.Contains(empresaId);
    }

    /// <summary>
    /// Todos los <paramref name="clientesEmpresariales"/> están en la cartera (lista
    /// vacía: true). Para los comandos que vinculan algo a varios Clientes a la vez; una sola
    /// consulta de alcance, no una por Id.
    /// </summary>
    public static async Task<bool> TodosLosClientesVisiblesAsync(
        this IAlcanceDatosService alcance, IReadOnlyCollection<Guid> clientesEmpresariales, CancellationToken cancellationToken = default)
    {
        if (clientesEmpresariales.Count == 0) return true;
        var ids = await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken);
        return ids is null || clientesEmpresariales.All(ids.Contains);
    }

    /// <summary>Igual que <see cref="TodosLosClientesVisiblesAsync"/>, con el alcance de gestión de Empresas.</summary>
    public static async Task<bool> TodasLasEmpresasParaGestionVisiblesAsync(
        this IAlcanceDatosService alcance, IReadOnlyCollection<Guid> empresaIds, CancellationToken cancellationToken = default)
    {
        if (empresaIds.Count == 0) return true;
        var ids = await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        return ids is null || empresaIds.All(ids.Contains);
    }

    /// <summary>
    /// La Empresa está en alguno de los alcances de cartera del usuario —Empresa, Cliente empresarial o
    /// Subcontrata—: la regla con la que <c>ContarVencenEstaSemanaDeEmpresaQuery</c>, <c>ObtenerSemanaPorEmpresaQuery</c>
    /// y <c>ObtenerVencimientosMesQuery</c> deciden si pueden nombrar a la Empresa empleadora de un Trabajador visible
    /// (esas tres la escriben todavía en línea; llevarlas a este método es un incremento aparte). Ver a un Trabajador no
    /// basta para ver a quién emplea.
    /// </summary>
    public static async Task<bool> EmpresaEnAlgunAlcanceDeCarteraAsync(
        this IAlcanceDatosService alcance, Guid empresaId, CancellationToken cancellationToken = default) =>
        await alcance.EmpresaVisibleAsync(empresaId, cancellationToken)
        || await alcance.ClienteVisibleAsync(empresaId, cancellationToken)
        || await alcance.SubcontrataVisibleAsync(empresaId, cancellationToken);

    public static async Task<bool> SubcontrataVisibleAsync(
        this IAlcanceDatosService alcance, Guid subcontrataId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        return ids is null || ids.Contains(subcontrataId);
    }

    /// <summary>Alcance de gestión sobre una Subcontrata concreta — ver <see cref="IAlcanceDatosService.ObtenerSubcontrataIdsParaGestionAsync"/>.</summary>
    public static async Task<bool> SubcontrataParaGestionVisibleAsync(
        this IAlcanceDatosService alcance, Guid subcontrataId, CancellationToken cancellationToken = default)
    {
        var ids = await alcance.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
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
    /// las dos la ve quien cumple
    /// <see cref="IAlcanceDatosService.TriajeVisibleAsync"/> o, si entra por
    /// su buzón personal, su propietario
    /// (<see cref="IAlcanceDatosService.BuzonesPersonalesPropiosAsync"/>)— con la misma
    /// regla que la lista de la bandeja (<see cref="VisibilidadConversacion"/>).
    /// No pasa por <see cref="ClienteOpcionalVisibleAsync"/>, que no conoce el
    /// rol y sirve a macros y buzones, donde «sin cliente» sigue siendo
    /// compartido en el Tenant (D-6 b, R-3a).
    /// Y, si el hilo está atado a un buzón personal de un gestor
    /// (<c>ConexionIntegracion.GestorPropietarioId</c>), que sea el propio
    /// dueño. Sin esto, un hilo de un buzón personal ajeno —que también tiene
    /// ClienteId null— se colaba en la cola de triaje como si fuera de
    /// cualquiera.
    ///
    /// <paramref name="empresaId"/> es obligatorio en la firma, y no un
    /// parámetro opcional, a propósito: un llamador que se olvidara de
    /// pasarlo trataría un hilo anclado a una Empresa como triaje —visible
    /// para quien pasa <see cref="IAlcanceDatosService.TriajeVisibleAsync"/>
    /// aunque esa Empresa no esté en su alcance— sin que nada lo delatara. Que el
    /// compilador lo exija es lo que impide esa migración a medias.
    /// </summary>
    public static async Task<bool> ConversacionVisibleAsync(
        this IAlcanceDatosService alcance, Guid? clienteId, Guid? empresaId, Guid? conexionIntegracionId,
        CancellationToken cancellationToken = default)
    {
        if (conexionIntegracionId is { } conexionId && !await alcance.ConexionIntegracionVisibleAsync(conexionId, cancellationToken))
            return false;

        // Ancla Empresa por alcance de GESTIÓN, no de lectura: un hilo con una
        // contratista es un artefacto interno, y la cartera de Empresas de un
        // usuario de portal sale de su propio Cliente (ver
        // ObtenerEmpresaIdsParaGestionAsync). Con EmpresaVisibleAsync, un
        // contacto de una empresa cliente descargaba los adjuntos de esos hilos
        // conociendo el Guid. La regla entera vive en VisibilidadConversacion.
        // Sin ancla, la ve quien pasa el triaje o el propietario de su buzón personal (condición 2 de la regla).
        return await VisibilidadConversacion.PermiteAsync(alcance, clienteId, empresaId, conexionIntegracionId, cancellationToken);
    }
}
