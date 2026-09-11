using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Los requisitos de contraseña que se anuncian al escribirla, con la MISMA
/// semántica que el <see cref="PasswordValidator{TUser}"/> de Identity, que es
/// quien decide al guardar: mayúscula, minúscula y dígito son solo ASCII
/// (A-Z, a-z, 0-9) y «símbolo» es cualquier otro carácter. Antes se anunciaban
/// con <c>char.IsUpper</c>: una «Á» marcaba la mayúscula como cumplida y el
/// servidor rechazaba la contraseña.
///
/// <c>wwwroot/js/acceso-contrasena.js</c> repite estas reglas en el navegador para
/// marcarlas en vivo, por la clave de cada requisito: si cambian aquí, cambian allí.
/// </summary>
public static class ReglasContrasena
{
    /// <param name="Clave">Nombre de la regla que entiende el guion del navegador.</param>
    /// <param name="Minimo">Solo en las reglas que llevan un número.</param>
    public sealed record Requisito(string Clave, string Etiqueta, bool Cumplido, int? Minimo = null);

    public static IReadOnlyList<Requisito> Evaluar(PasswordOptions politica, string? contrasena)
    {
        var p = contrasena ?? string.Empty;

        var requisitos = new List<Requisito>
        {
            new("longitud", $"Al menos {politica.RequiredLength} caracteres", p.Length >= politica.RequiredLength, politica.RequiredLength),
        };

        if (politica.RequireUppercase)
            requisitos.Add(new("mayuscula", "Una mayúscula", p.Any(EsMayuscula)));
        if (politica.RequireLowercase)
            requisitos.Add(new("minuscula", "Una minúscula", p.Any(EsMinuscula)));
        if (politica.RequireDigit)
            requisitos.Add(new("numero", "Un número", p.Any(EsDigito)));
        if (politica.RequireNonAlphanumeric)
            requisitos.Add(new("simbolo", "Un símbolo", p.Any(c => !EsMayuscula(c) && !EsMinuscula(c) && !EsDigito(c))));
        if (politica.RequiredUniqueChars > 1)
            requisitos.Add(new("distintos", $"Al menos {politica.RequiredUniqueChars} caracteres distintos",
                p.Distinct().Count() >= politica.RequiredUniqueChars, politica.RequiredUniqueChars));

        return requisitos;
    }

    private static bool EsMayuscula(char c) => c is >= 'A' and <= 'Z';
    private static bool EsMinuscula(char c) => c is >= 'a' and <= 'z';
    private static bool EsDigito(char c) => c is >= '0' and <= '9';
}
