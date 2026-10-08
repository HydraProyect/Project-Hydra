namespace CaeManager.Application.Usuarios;

/// <summary>
/// Catálogo cerrado de avatares de usuario (decisión de producto del 2026-10-08): un
/// emoji de animal sobre uno de cuatro tonos. Es personalización opcional de la
/// <b>cuenta</b> —identidad, no Tenant—: quien no elige sigue viéndose con sus iniciales.
///
/// <para>
/// Cerrado a propósito: no hay subida de imágenes, así que no hay almacenamiento,
/// moderación ni dato personal nuevo. Solo animales: una figura humana obligaría a
/// elegir tono de piel y género, que es representar rasgos de la persona.
/// </para>
///
/// <para>
/// Lo que se guarda es la <b>clave</b> (<c>zorro-verde</c>), nunca el emoji: una clave
/// que deje de existir en el catálogo se lee como «sin avatar» y vuelven las iniciales
/// (<see cref="Resolver"/>), de modo que retirar un motivo no rompe ninguna cuenta.
/// </para>
/// </summary>
public static class CatalogoAvatares
{
    /// <summary>Longitud máxima de la clave guardada (columna <c>AspNetUsers.Avatar</c>).</summary>
    public const int LongitudMaximaClave = 32;

    public static IReadOnlyList<MotivoAvatar> Motivos { get; } =
    [
        new("zorro", "🦊"),
        new("oso", "🐻"),
        new("panda", "🐼"),
        new("koala", "🐨"),
        new("gato", "🐱"),
        new("perro", "🐶"),
        new("leon", "🦁"),
        new("tigre", "🐯"),
        new("conejo", "🐰"),
        new("rana", "🐸"),
        new("pinguino", "🐧"),
        new("buho", "🦉"),
        new("pollito", "🐥"),
        new("tortuga", "🐢"),
        new("pulpo", "🐙"),
        new("ballena", "🐳"),
        new("delfin", "🐬"),
        new("pez", "🐟"),
        new("abeja", "🐝"),
        new("mariposa", "🦋"),
        new("mariquita", "🐞"),
        new("caracol", "🐌"),
        new("unicornio", "🦄"),
        new("erizo", "🦔"),
    ];

    /// <summary>El primero es el tono por defecto del selector: el del avatar de iniciales.</summary>
    public static IReadOnlyList<string> Tonos { get; } = ["azul", "verde", "ambar", "neutro"];

    public static string Clave(string motivo, string tono) => $"{motivo}-{tono}";

    /// <summary>
    /// El avatar que nombra la clave, o <c>null</c> si está vacía o no es del catálogo.
    /// Comparación exacta: las claves las escribe este catálogo, no una persona.
    /// </summary>
    public static AvatarElegido? Resolver(string? clave)
    {
        if (string.IsNullOrEmpty(clave)) return null;

        var corte = clave.LastIndexOf('-');
        if (corte <= 0) return null;

        var tono = clave[(corte + 1)..];
        var motivo = Motivos.FirstOrDefault(m => m.Clave == clave[..corte]);

        return motivo is null || !Tonos.Contains(tono) ? null : new AvatarElegido(motivo, tono);
    }
}

/// <param name="Clave">Identificador estable en ASCII; el nombre visible lo ponen los recursos de Web.</param>
public record MotivoAvatar(string Clave, string Emoji);

public record AvatarElegido(MotivoAvatar Motivo, string Tono)
{
    public string Clave => CatalogoAvatares.Clave(Motivo.Clave, Tono);
}
