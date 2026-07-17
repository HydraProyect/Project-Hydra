using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Credenciales de una Empresa para acceder a una plataforma externa de
/// gestión documental. Relación 1:1 con Empresa — mismo patrón que
/// PlataformaAcceso (Centro), pero como aquí no hay varias plataformas por
/// registrar no hace falta un nombre para distinguirlas.
///
/// <see cref="CampoEmpresa"/> cubre un dato que piden algunas plataformas
/// externas además de usuario/contraseña — según la plataforma lo llaman
/// "Empresa", "Cliente" o "Proveedor", así que se modela como un campo de
/// texto libre sin significado propio para el dominio.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo
/// mediante un ValueConverter de EF Core en Infrastructure — este tipo de
/// dominio no conoce el cifrado, solo modela el valor en texto plano en
/// memoria durante el ciclo de vida de la petición.
/// </summary>
public class CredencialAccesoEmpresa : Entity
{
    public const int LongitudMaximaUrlAcceso = 500;
    public const int LongitudMaximaCampoEmpresa = 200;
    public const int LongitudMaximaUsuario = 200;
    public const int LongitudMaximaContrasena = 500;

    public Guid EmpresaId { get; private set; }
    public string? UrlAcceso { get; private set; }
    public string? CampoEmpresa { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }

    private CredencialAccesoEmpresa()
    {
    }

    public CredencialAccesoEmpresa(Guid empresaId, string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("Las credenciales deben pertenecer a una empresa.", nameof(empresaId));

        EmpresaId = empresaId;
        Actualizar(urlAcceso, campoEmpresa, usuario, contrasena);
    }

    public void Actualizar(string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena)
    {
        if (urlAcceso?.Length > LongitudMaximaUrlAcceso)
            throw new ArgumentException($"La URL no puede superar {LongitudMaximaUrlAcceso} caracteres.", nameof(urlAcceso));
        if (campoEmpresa?.Length > LongitudMaximaCampoEmpresa)
            throw new ArgumentException($"Este campo no puede superar {LongitudMaximaCampoEmpresa} caracteres.", nameof(campoEmpresa));
        if (usuario?.Length > LongitudMaximaUsuario)
            throw new ArgumentException($"El usuario no puede superar {LongitudMaximaUsuario} caracteres.", nameof(usuario));
        if (contrasena?.Length > LongitudMaximaContrasena)
            throw new ArgumentException($"La contraseña no puede superar {LongitudMaximaContrasena} caracteres.", nameof(contrasena));

        UrlAcceso = urlAcceso;
        CampoEmpresa = campoEmpresa;
        Usuario = usuario;
        Contrasena = contrasena;
    }
}
