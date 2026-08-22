namespace CaeManager.Application.Plataforma;

/// <summary>
/// ¿Es este usuario la <b>raíz de confianza de bootstrap</b> de la plataforma?
///
/// <para>
/// Una sola pregunta y un solo consumidor: <c>AutoConcederPrivilegioCommand</c>.
/// <c>EsPlataforma</c> deja de ser una autoridad operativa transversal y queda
/// reducido a esto — el punto por el que se crea la <b>primera</b> concesión,
/// cuando todavía no hay ninguna de la que derivar autoridad.
/// </para>
///
/// <para>
/// <b>Por qué hace falta una raíz.</b> Si abrir una sesión exige una concesión y
/// crear una concesión exigiera poder abrir, no habría por dónde empezar. La
/// raíz rompe ese ciclo, y solo eso:
/// </para>
/// <code>
/// EsPlataforma  →  primera concesión  →  concesión  →  abrir sesión
///   la raíz          el acto              la autoridad    la ceremonia
///   (aquí)           fundacional          efectiva
/// </code>
///
/// <para>
/// <b>Lo que esta interfaz NO es.</b> No autoriza a abrir nada. Antes de A0 una
/// sola interfaz servía a los dos consumidores, y eso la convertía en una
/// abstracción cuyo significado cambiaba según quién la invocase: para la
/// auto-concesión respondía "¿eres de la plataforma?" y para la apertura
/// respondía "¿puedes ejercer privilegio sobre este tenant?". Son preguntas
/// distintas y ahora tienen contratos distintos —
/// <see cref="CapacidadesQuePuedenAbrirSesion"/> y
/// <see cref="ReglaTenantObjetivoAjeno"/> responden la segunda.
/// </para>
///
/// <para>
/// <b>Pertenecer a la plataforma ya no basta para abrir una sesión.</b> Es la
/// propiedad que distingue una raíz de bootstrap de un rol monolítico al que
/// solo se le ha cambiado el nombre, y tiene test propio.
/// </para>
/// </summary>
public interface IRaizBootstrapPlataforma
{
    /// <param name="usuarioId">Quién pide crearse la concesión fundacional.</param>
    Task<bool> EsRaizDeConfianzaAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
