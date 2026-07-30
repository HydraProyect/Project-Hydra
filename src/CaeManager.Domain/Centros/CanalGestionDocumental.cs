using CaeManager.Domain.Common;

namespace CaeManager.Domain.Centros;

/// <summary>Cómo se acredita/entrega la documentación exigida por un Centro.</summary>
public enum TipoCanalGestion
{
    /// <summary>Portal externo de terceros que el Centro exige usar (p. ej. CTAIMA CAE) — requiere usuario/contraseña.</summary>
    Plataforma,

    /// <summary>Sin portal — la documentación se envía por correo a un contacto del Centro.</summary>
    Email
}

/// <summary>
/// Cómo un Centro exige acreditar/recibir su documentación CAE — o bien un
/// portal externo con login (<see cref="TipoCanalGestion.Plataforma"/>, el
/// caso original de esta entidad, antes llamada <c>PlataformaAcceso</c>), o
/// bien gestión por correo a un contacto concreto
/// (<see cref="TipoCanalGestion.Email"/>, sin portal ni credenciales) — visto
/// al diseñar el importador masivo de documentos (ver ROADMAP.md, Épico
/// "Migración masiva de datos vía ZIP + DIE"): no todos los clientes que una
/// consultora gestiona usan una plataforma, algunos solo requieren enviar la
/// documentación por email. Relación 1:1 con Centro, en ambos casos.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo mediante
/// un ValueConverter de EF Core en Infrastructure — este tipo de dominio no
/// conoce el cifrado, solo modela el valor en texto plano en memoria durante
/// el ciclo de vida de la petición.
/// </summary>
public class CanalGestionDocumental : EntidadConTenant
{
    public const int LongitudMaximaNombrePlataforma = 150;
    public const int LongitudMaximaUrlAcceso = 500;
    public const int LongitudMaximaEmailsDestinatarios = 500;
    public const int LongitudMaximaNombreContacto = 150;
    public const int LongitudMaximaNotas = 1000;

    public Guid CentroId { get; private set; }
    public TipoCanalGestion Tipo { get; private set; }

    // Solo aplica cuando Tipo == Plataforma.
    public string? NombrePlataforma { get; private set; }
    public string? UrlAcceso { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }

    // Solo aplica cuando Tipo == Email.
    public string? EmailsDestinatarios { get; private set; }
    public string? NombreContacto { get; private set; }

    public string? Notas { get; private set; }

    private CanalGestionDocumental()
    {
    }

    private CanalGestionDocumental(Guid centroId, TipoCanalGestion tipo, string? notas)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("El canal de gestión debe pertenecer a un centro.", nameof(centroId));

        CentroId = centroId;
        Tipo = tipo;
        EstablecerNotas(notas);
    }

    public static CanalGestionDocumental DePlataforma(
        Guid centroId, string nombrePlataforma, string? urlAcceso, string? usuario, string? contrasena, string? notas = null)
    {
        var canal = new CanalGestionDocumental(centroId, TipoCanalGestion.Plataforma, notas);
        canal.EstablecerNombrePlataforma(nombrePlataforma);
        canal.EstablecerUrlAcceso(urlAcceso);
        canal.ActualizarCredenciales(usuario, contrasena);
        return canal;
    }

    public static CanalGestionDocumental PorEmail(
        Guid centroId, string emailsDestinatarios, string? nombreContacto, string? notas = null)
    {
        var canal = new CanalGestionDocumental(centroId, TipoCanalGestion.Email, notas);
        canal.EstablecerEmailsDestinatarios(emailsDestinatarios);
        canal.EstablecerNombreContacto(nombreContacto);
        return canal;
    }

    public void ActualizarPlataforma(string nombrePlataforma, string? urlAcceso, string? notas)
    {
        RequerirTipo(TipoCanalGestion.Plataforma);
        EstablecerNombrePlataforma(nombrePlataforma);
        EstablecerUrlAcceso(urlAcceso);
        EstablecerNotas(notas);
    }

    public void ActualizarCredenciales(string? usuario, string? contrasena)
    {
        RequerirTipo(TipoCanalGestion.Plataforma);
        Usuario = usuario;
        Contrasena = contrasena;
    }

    public void ActualizarEmail(string emailsDestinatarios, string? nombreContacto, string? notas)
    {
        RequerirTipo(TipoCanalGestion.Email);
        EstablecerEmailsDestinatarios(emailsDestinatarios);
        EstablecerNombreContacto(nombreContacto);
        EstablecerNotas(notas);
    }

    private void RequerirTipo(TipoCanalGestion tipo)
    {
        if (Tipo != tipo)
            throw new InvalidOperationException($"Este canal de gestión es de tipo {Tipo}, no {tipo}.");
    }

    private void EstablecerNombrePlataforma(string nombrePlataforma)
    {
        if (string.IsNullOrWhiteSpace(nombrePlataforma))
            throw new ArgumentException("El nombre de la plataforma es obligatorio.", nameof(nombrePlataforma));

        var normalizado = nombrePlataforma.Trim();

        if (normalizado.Length > LongitudMaximaNombrePlataforma)
            throw new ArgumentException(
                $"El nombre de la plataforma no puede superar {LongitudMaximaNombrePlataforma} caracteres.", nameof(nombrePlataforma));

        NombrePlataforma = normalizado;
    }

    private void EstablecerUrlAcceso(string? urlAcceso)
    {
        if (urlAcceso?.Length > LongitudMaximaUrlAcceso)
            throw new ArgumentException($"La URL no puede superar {LongitudMaximaUrlAcceso} caracteres.", nameof(urlAcceso));

        UrlAcceso = urlAcceso;
    }

    private void EstablecerEmailsDestinatarios(string emailsDestinatarios)
    {
        if (string.IsNullOrWhiteSpace(emailsDestinatarios))
            throw new ArgumentException("Los correos de destino son obligatorios.", nameof(emailsDestinatarios));

        var normalizado = emailsDestinatarios.Trim();

        if (normalizado.Length > LongitudMaximaEmailsDestinatarios)
            throw new ArgumentException(
                $"Los correos de destino no pueden superar {LongitudMaximaEmailsDestinatarios} caracteres.", nameof(emailsDestinatarios));

        EmailsDestinatarios = normalizado;
    }

    private void EstablecerNombreContacto(string? nombreContacto)
    {
        if (nombreContacto?.Length > LongitudMaximaNombreContacto)
            throw new ArgumentException(
                $"El nombre de contacto no puede superar {LongitudMaximaNombreContacto} caracteres.", nameof(nombreContacto));

        NombreContacto = nombreContacto;
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
