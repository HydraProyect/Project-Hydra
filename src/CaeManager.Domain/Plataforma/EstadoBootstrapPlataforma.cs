using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Estado del bootstrap de la plataforma: quién fue designado identidad raíz y
/// si el acto fundacional ya se consumió. <b>Una única fila canónica</b> en todo
/// el despliegue.
///
/// <para>
/// <b>No es una fuente de autoridad.</b> Responde a "¿quién fue designado?" y
/// "¿queda bootstrap?"; la autoridad sigue viviendo exclusivamente en la
/// concesión. Confundir ambas cosas devolvería el problema que A2 elimina: una
/// fila que, por existir, permite mandar.
/// </para>
///
/// <para>
/// <b>Las dos piezas van juntas porque son una misma máquina de estado:</b>
/// </para>
/// <code>
/// SIN DESIGNAR ──Designar(raíz)──▶ LISTO ──Consumir()──▶ CONSUMIDO
/// </code>
/// <para>
/// y las transiciones prohibidas son las que dan la garantía:
/// <c>CONSUMIDO → LISTO</c>, <c>raíz A → raíz B</c>, y volver a
/// <c>SIN DESIGNAR</c>. Todas imposibles desde el dominio: no hay setters
/// públicos y los dos métodos lanzan si se invocan fuera de su transición.
/// </para>
///
/// <para>
/// <b>Consumido es consumido.</b> Si la concesión raíz se revoca o caduca, el
/// bootstrap <b>no</b> se reabre: eso dejaría una autoridad de emergencia
/// permanente escondida tras la ausencia de una fila. La consecuencia aceptada a
/// propósito es que perder la única <c>AdminPlataforma</c> deja la plataforma sin
/// autoridad recuperable <i>desde el producto</i>; la recuperación es un
/// procedimiento administrativo externo, y no está construido.
/// </para>
/// </summary>
public class EstadoBootstrapPlataforma : Entity
{
    /// <summary>
    /// Clave fija: la unicidad de la fila no puede depender de que nadie inserte
    /// una segunda por descuido. Con un Id constante, el segundo INSERT choca
    /// contra la clave primaria — la garantía la da la base, no una comprobación
    /// leída antes en memoria, que dos arranques simultáneos pasarían a la vez.
    /// </summary>
    public static readonly Guid ClaveCanonica = new("b0075742-0000-4000-8000-000000000001");

    public Guid UsuarioRaizId { get; private set; }
    public DateTime DesignadaEnUtc { get; private set; }

    public bool Consumido { get; private set; }
    public DateTime? ConsumidoEnUtc { get; private set; }

    /// <summary>
    /// Token de concurrencia. Dos invocaciones simultáneas del usuario raíz no
    /// pueden consumir el bootstrap las dos: la segunda pierde contra la base.
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    private EstadoBootstrapPlataforma()
    {
    }

    /// <summary>
    /// Designa la identidad raíz. Lo invoca el bootstrap de la aplicación con el
    /// usuario que el despliegue nombró por configuración — <b>no</b> un comando
    /// de aplicación, y desde luego no una sesión de usuario.
    /// </summary>
    public static EstadoBootstrapPlataforma Designar(Guid usuarioRaizId, DateTime ahoraUtc)
    {
        if (usuarioRaizId == Guid.Empty)
            throw new ArgumentException("La identidad raíz no puede ser vacía.", nameof(usuarioRaizId));

        var estado = new EstadoBootstrapPlataforma
        {
            UsuarioRaizId = usuarioRaizId,
            DesignadaEnUtc = ahoraUtc,
            Consumido = false,
        };

        typeof(Entity).GetProperty(nameof(Id))!.SetValue(estado, ClaveCanonica);
        return estado;
    }

    /// <summary>
    /// ¿Puede este usuario ejecutar el acto fundacional? Las dos condiciones
    /// juntas, porque por separado ninguna basta: ser la raíz no sirve si ya se
    /// usó, y quedar bootstrap no sirve si no eres tú.
    /// </summary>
    public bool PuedeArrancar(Guid usuarioId) => !Consumido && usuarioId == UsuarioRaizId;

    /// <summary>
    /// Marca el bootstrap como consumido. Se guarda en el MISMO SaveChanges que
    /// crea la concesión fundacional: si fueran dos operaciones, un fallo entre
    /// medias dejaría o una concesión con el bootstrap todavía abierto —dos
    /// raíces posibles— o el bootstrap gastado sin concesión, que con la regla de
    /// no reapertura es irreversible.
    /// </summary>
    public void Consumir(DateTime ahoraUtc)
    {
        if (Consumido)
            throw new InvalidOperationException(
                "El bootstrap de plataforma ya se consumió y no vuelve a abrirse.");

        Consumido = true;
        ConsumidoEnUtc = ahoraUtc;
        Version = Guid.NewGuid();
    }
}
