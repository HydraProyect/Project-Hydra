using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Credenciales de una Subcontrata para acceder a una plataforma externa de
/// gestión documental. Relación 1:1 con Subcontrata — mismo patrón que
/// CredencialAccesoEmpresa.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo
/// mediante un ValueConverter de EF Core en Infrastructure — este tipo de
/// dominio no conoce el cifrado, solo modela el valor en texto plano en
/// memoria durante el ciclo de vida de la petición.
/// </summary>
public class CredencialAccesoSubcontrata : EntidadConTenant
{
    public const int LongitudMaximaUrlAcceso = 500;
    public const int LongitudMaximaCampoEmpresa = 200;
    public const int LongitudMaximaUsuario = 200;
    public const int LongitudMaximaContrasena = 500;
    public const int LongitudMaximaNotas = 1000;

    public Guid SubcontrataId { get; private set; }
    public string? UrlAcceso { get; private set; }
    public string? CampoEmpresa { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }
    public string? Notas { get; private set; }

    private CredencialAccesoSubcontrata()
    {
    }

    public CredencialAccesoSubcontrata(Guid subcontrataId, string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena, string? notas = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("Las credenciales deben pertenecer a una subcontrata.", nameof(subcontrataId));

        SubcontrataId = subcontrataId;
        Actualizar(urlAcceso, campoEmpresa, usuario, contrasena, notas);
    }

    public void Actualizar(string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena, string? notas)
    {
        if (urlAcceso?.Length > LongitudMaximaUrlAcceso)
            throw new ArgumentException($"La URL no puede superar {LongitudMaximaUrlAcceso} caracteres.", nameof(urlAcceso));
        if (campoEmpresa?.Length > LongitudMaximaCampoEmpresa)
            throw new ArgumentException($"Este campo no puede superar {LongitudMaximaCampoEmpresa} caracteres.", nameof(campoEmpresa));
        if (usuario?.Length > LongitudMaximaUsuario)
            throw new ArgumentException($"El usuario no puede superar {LongitudMaximaUsuario} caracteres.", nameof(usuario));
        if (contrasena?.Length > LongitudMaximaContrasena)
            throw new ArgumentException($"La contraseña no puede superar {LongitudMaximaContrasena} caracteres.", nameof(contrasena));
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        UrlAcceso = urlAcceso;
        CampoEmpresa = campoEmpresa;
        Usuario = usuario;
        Contrasena = contrasena;
        Notas = notas;
    }
}
