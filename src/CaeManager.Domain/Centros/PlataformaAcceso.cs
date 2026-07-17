using CaeManager.Domain.Common;

namespace CaeManager.Domain.Centros;

/// <summary>
/// Portal externo de terceros que un Centro exige usar para acreditar
/// documentación (p. ej. CTAIMA CAE). Relación 1:1 con Centro.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo mediante
/// un ValueConverter de EF Core en Infrastructure — este tipo de dominio no
/// conoce el cifrado, solo modela el valor en texto plano en memoria durante
/// el ciclo de vida de la petición.
/// </summary>
public class PlataformaAcceso : Entity
{
    public const int LongitudMaximaNombrePlataforma = 150;

    public Guid CentroId { get; private set; }
    public string NombrePlataforma { get; private set; } = string.Empty;
    public string? UrlAcceso { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }
    public string? Notas { get; private set; }

    private PlataformaAcceso()
    {
    }

    public PlataformaAcceso(
        Guid centroId,
        string nombrePlataforma,
        string? urlAcceso,
        string? usuario,
        string? contrasena,
        string? notas = null)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La plataforma de acceso debe pertenecer a un centro.", nameof(centroId));

        CentroId = centroId;
        EstablecerNombrePlataforma(nombrePlataforma);
        UrlAcceso = urlAcceso;
        Usuario = usuario;
        Contrasena = contrasena;
        Notas = notas;
    }

    public void ActualizarCredenciales(string? usuario, string? contrasena)
    {
        Usuario = usuario;
        Contrasena = contrasena;
    }

    public void Actualizar(string nombrePlataforma, string? urlAcceso, string? notas)
    {
        EstablecerNombrePlataforma(nombrePlataforma);
        UrlAcceso = urlAcceso;
        Notas = notas;
    }

    private void EstablecerNombrePlataforma(string nombrePlataforma)
    {
        if (string.IsNullOrWhiteSpace(nombrePlataforma))
            throw new ArgumentException("El nombre de la plataforma es obligatorio.", nameof(nombrePlataforma));

        var normalizado = nombrePlataforma.Trim();

        if (normalizado.Length > LongitudMaximaNombrePlataforma)
            throw new ArgumentException(
                $"El nombre de la plataforma no puede superar {LongitudMaximaNombrePlataforma} caracteres.",
                nameof(nombrePlataforma));

        NombrePlataforma = normalizado;
    }
}
