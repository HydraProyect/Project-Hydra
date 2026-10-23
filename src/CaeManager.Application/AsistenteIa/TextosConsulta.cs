using System.Globalization;
using System.Resources;

namespace CaeManager.Application.AsistenteIa;

internal static class TextosConsulta
{
    private static readonly ResourceManager Recursos = new("CaeManager.Application.AsistenteIa.TextosConsulta", typeof(TextosConsulta).Assembly);

    public static string Obtener(string clave) => Recursos.GetString(clave, CultureInfo.CurrentUICulture)!;
}
