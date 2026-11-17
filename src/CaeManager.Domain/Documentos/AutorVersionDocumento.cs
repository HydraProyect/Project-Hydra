namespace CaeManager.Domain.Documentos;

/// <summary>
/// Quién subió una versión de un Documento (D-12): el usuario efectivo y el Actor real, con el mismo reparto que
/// <c>RegistroAuditoria</c>. Fuera de una impersonación los dos son la misma persona; en una impersonación el usuario
/// efectivo es el Usuario simulado y el Actor real es quien estaba detrás del teclado. En una Sesión Privilegiada sin
/// Usuario simulado los dos son el Actor de Plataforma TALVEG.
///
/// <para>
/// <see cref="Desconocido"/> (el valor por defecto) es una versión sin persona detrás: un seeder, un proceso de sistema
/// o una integración externa. Esas versiones, como las anteriores a la columna, caen en D-12.1: no tienen autor y se
/// pueden validar. Por eso solo se construye un autor conocido con los dos identificadores a la vez.
/// </para>
/// </summary>
public readonly record struct AutorVersionDocumento
{
    private AutorVersionDocumento(Guid usuarioId, Guid actorRealUsuarioId)
    {
        UsuarioId = usuarioId;
        ActorRealUsuarioId = actorRealUsuarioId;
    }

    /// <summary>El usuario efectivo, o null si la versión no tiene persona detrás.</summary>
    public Guid? UsuarioId { get; }

    /// <summary>El Actor real, o null si la versión no tiene persona detrás.</summary>
    public Guid? ActorRealUsuarioId { get; }

    public bool EsConocido => UsuarioId is not null;

    /// <summary>Sin persona detrás: seeders, procesos de sistema e integraciones externas.</summary>
    public static AutorVersionDocumento Desconocido => default;

    /// <summary>Una persona: el usuario efectivo y el Actor real, que coinciden salvo en una impersonación.</summary>
    public static AutorVersionDocumento Persona(Guid usuarioEfectivoId, Guid actorRealUsuarioId)
    {
        if (usuarioEfectivoId == Guid.Empty)
            throw new ArgumentException("El usuario efectivo del autor no puede estar vacío.", nameof(usuarioEfectivoId));
        if (actorRealUsuarioId == Guid.Empty)
            throw new ArgumentException("El Actor real del autor no puede estar vacío.", nameof(actorRealUsuarioId));
        return new AutorVersionDocumento(usuarioEfectivoId, actorRealUsuarioId);
    }
}
