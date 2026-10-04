namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Las iniciales del avatar del menú de usuario, sacadas del nombre que expone la identidad
/// (<c>Identity.Name</c>: un nombre completo o, si la cuenta no lo tiene, el correo). Solo
/// presentación: no identifica a nadie ni autoriza nada.
/// </summary>
public static class InicialesDeUsuario
{
    private static readonly char[] Separadores = [' ', '.', '_', '-', '@', '+'];

    /// <summary>Hasta dos letras en mayúscula; «?» si el nombre no tiene ninguna.</summary>
    public static string De(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return "?";

        // Un correo se corta en la arroba: el dominio no es el nombre de la persona.
        var local = nombre.Contains(' ') ? nombre : nombre.Split('@')[0];
        var iniciales = local
            .Split(Separadores, StringSplitOptions.RemoveEmptyEntries)
            .Select(parte => parte.FirstOrDefault(char.IsLetter))
            .Where(letra => letra != default)
            .Take(2)
            .Select(char.ToUpperInvariant)
            .ToArray();

        return iniciales.Length == 0 ? "?" : new string(iniciales);
    }
}
