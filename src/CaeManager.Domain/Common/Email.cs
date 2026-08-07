using System.Net.Mail;

namespace CaeManager.Domain.Common;

/// <summary>
/// Value object de una dirección de correo (P2 #27 de
/// docs/business/MATURITY_REVIEW.md). A diferencia de <see cref="Dni"/>/
/// <see cref="Cif"/>, no sustituye ninguna validación de formato que ya
/// existiera: hoy <c>Trabajador.Email</c>/<c>ParticipanteConversacion.Email</c>/
/// <c>Mensaje.Remitente</c> solo comprueban que no esté vacío,
/// ningún constructor valida el formato — este tipo cierra ese hueco para
/// quien lo use, sin migrar todavía ninguna de esas tres propiedades (mismo
/// motivo que <see cref="Dni"/>: no se puede compilar/ejecutar los tests en
/// este entorno para verificar una migración con seguridad).
///
/// Validación vía <see cref="MailAddress"/> (biblioteca estándar de .NET,
/// no una regexp propia mantenida a mano) — suficientemente estricta para
/// rechazar valores claramente inválidos sin rechazar direcciones raras
/// pero legítimas.
/// </summary>
public readonly record struct Email
{
    public string Valor { get; }

    private Email(string valor) => Valor = valor;

    public static Email Crear(string valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valor);

        var normalizado = valor.Trim();

        MailAddress direccion;
        try
        {
            direccion = new MailAddress(normalizado);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"\"{valor}\" no es un email válido.", nameof(valor), ex);
        }

        if (direccion.Address != normalizado)
            throw new ArgumentException($"\"{valor}\" no es un email válido.", nameof(valor));

        return new Email(normalizado);
    }

    public override string ToString() => Valor;

    public static implicit operator string(Email email) => email.Valor;
}
