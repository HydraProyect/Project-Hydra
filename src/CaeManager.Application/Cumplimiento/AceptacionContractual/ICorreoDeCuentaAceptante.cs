namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <summary>
/// Si la cuenta a la que se va a atribuir una aceptación contractual (#2009) es la del correo que se
/// verificó en la Solicitud de alta.
///
/// <para>
/// Devuelve un sí o un no, nunca el correo: el llamador ya tiene el de la Solicitud de alta y no
/// necesita leer el de una cuenta a partir de un Guid (el mismo cuidado que
/// <see cref="Common.ICorreoDelActorReal"/>). La lectura va sin rastreo, para no desenganchar ni
/// sustituir la instancia de la cuenta que el Command del alta acaba de crear en su misma unidad de
/// trabajo.
/// </para>
/// </summary>
public interface ICorreoDeCuentaAceptante
{
    /// <summary>
    /// <c>true</c> solo si la cuenta existe, es propia de <paramref name="tenantId"/> y su correo,
    /// normalizado con <see cref="Domain.AltaAutonoma.SolicitudAlta.NormalizarCorreo"/>, es
    /// <paramref name="correoNormalizado"/>.
    /// </summary>
    Task<bool> EsCorreoDeLaCuentaAsync(
        Guid usuarioId, Guid tenantId, string correoNormalizado, CancellationToken cancellationToken = default);
}
