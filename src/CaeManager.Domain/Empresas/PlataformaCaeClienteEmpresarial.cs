using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// La Plataforma CAE (Nalanda, Dokify, CTAIMA…) en la que un Cliente empresarial
/// exige la documentación a quienes le prestan servicio, con la dirección de su
/// portal y el código con el que la plataforma identifica a ese Cliente empresarial.
/// Caja «Plataforma CAE» de Cliente 360 (maqueta del 17-10).
///
/// <para>
/// Decisión del propietario (17-10 y 18-10): la plataforma es del Cliente empresarial
/// y la comparten todos sus Centros; el código de cliente también es suyo. El usuario
/// y la contraseña son de cada Empresa y viven aparte, cifrados, en
/// <see cref="AccesoPlataformaCaeEmpresa"/>. Una fila como mucho por
/// (Tenant propietario, Cliente empresarial).
/// </para>
///
/// <para>
/// <see cref="ClienteEmpresarialId"/> es el <c>Empresa.Id</c> de la contraparte en
/// posición cliente de la Relación Empresarial, el mismo que referencia el Centro
/// (<c>Centro.ClienteId</c>, nombre heredado). Nada de esta fila es secreto ni va cifrado:
/// el rol Consulta ve la plataforma y «Abrir portal». El código de cliente, aunque tampoco
/// es secreto, solo lo reciben los roles con escritura (<c>ObtenerPlataformaCaeClienteQuery</c>).
/// </para>
/// </summary>
public class PlataformaCaeClienteEmpresarial : EntidadConTenant
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaUrlPortal = 500;
    public const int LongitudMaximaCodigoCliente = 200;

    public Guid ClienteEmpresarialId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string? UrlPortal { get; private set; }
    public string? CodigoCliente { get; private set; }

    private PlataformaCaeClienteEmpresarial()
    {
    }

    public PlataformaCaeClienteEmpresarial(Guid clienteEmpresarialId, string nombre, string? urlPortal, string? codigoCliente)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("La plataforma debe pertenecer a un Cliente.", nameof(clienteEmpresarialId));

        ClienteEmpresarialId = clienteEmpresarialId;
        Actualizar(nombre, urlPortal, codigoCliente);
    }

    public void Actualizar(string nombre, string? urlPortal, string? codigoCliente)
    {
        var nombreLimpio = nombre?.Trim();
        if (string.IsNullOrEmpty(nombreLimpio))
            throw new ArgumentException("Indica la plataforma.", nameof(nombre));
        if (nombreLimpio.Length > LongitudMaximaNombre)
            throw new ArgumentException($"La plataforma no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        var urlLimpia = string.IsNullOrWhiteSpace(urlPortal) ? null : urlPortal.Trim();
        if (urlLimpia is not null)
        {
            if (urlLimpia.Length > LongitudMaximaUrlPortal)
                throw new ArgumentException($"La dirección no puede superar {LongitudMaximaUrlPortal} caracteres.", nameof(urlPortal));
            if (!EsUrlDePortalValida(urlLimpia))
                throw new ArgumentException("La dirección del portal debe empezar por https:// o http://.", nameof(urlPortal));
        }

        var codigoLimpio = string.IsNullOrWhiteSpace(codigoCliente) ? null : codigoCliente.Trim();
        if (codigoLimpio?.Length > LongitudMaximaCodigoCliente)
            throw new ArgumentException($"El código de cliente no puede superar {LongitudMaximaCodigoCliente} caracteres.", nameof(codigoCliente));

        Nombre = nombreLimpio;
        UrlPortal = urlLimpia;
        CodigoCliente = codigoLimpio;
    }

    /// <summary>
    /// La dirección se pinta como enlace «Abrir portal ↗»: solo http y https, para que
    /// un valor guardado no pueda convertirse en un <c>javascript:</c> ejecutable.
    /// </summary>
    public static bool EsUrlDePortalValida(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
