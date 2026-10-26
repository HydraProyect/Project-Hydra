namespace CaeManager.Application.Common.Seguridad;

/// <summary>
/// Única comprobación de «ruta local segura» de TALVEG: solo se aceptan rutas que empiezan por
/// una sola «/» y no llevan caracteres de control. La usan la capa Web (ReturnUrl tras el login,
/// vía <c>CaeManager.Web.Components.Account.RedireccionLocal</c>, que delega aquí) y los comandos
/// que guardan una URL de destino (<c>RegistrarUsoRecienteCommand</c>). Vive en Application para
/// que Web y Application compartan una sola copia sin invertir la dirección de dependencias.
/// <see cref="Sanear"/> devuelve «/» ante cualquier valor sospechoso; un destino es seguro cuando
/// <c>Sanear(destino)</c> lo devuelve intacto.
/// </summary>
public static class RedireccionLocal
{
    public static string Sanear(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        if (returnUrl[0] != '/')
            return "/";

        // Los navegadores eliminan tabulador, CR y LF al interpretar una URL: "/\t/atacante.com"
        // pasa el filtro de la segunda posición y llega como "//atacante.com". Ningún carácter de
        // control tiene sentido en una ruta local, así que se rechazan en cualquier posición.
        foreach (var c in returnUrl)
        {
            if (char.IsControl(c))
                return "/";
        }

        if (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
            return "/";

        return returnUrl;
    }
}
