using System.Globalization;
using System.Text.Json;

namespace CaeManager.Application.Seguridad.Incidentes;

/// <summary>
/// Evidencia estructurada de un incidente de seguridad sospechado, <b>sin datos
/// personales en claro</b> por construcción: solo admite identificadores, recuentos,
/// marcas de tiempo, booleanos y valores de enumeraciones. No hay ningún método que
/// acepte un texto libre, así que un nombre, un correo o una IP no pueden entrar por
/// descuido; si un incremento posterior lo necesitara, tendría que añadir el método a
/// la vista de todos.
/// </summary>
public sealed class EvidenciaIncidenteSeguridad
{
    private readonly SortedDictionary<string, object> _campos = new(StringComparer.Ordinal);

    public EvidenciaIncidenteSeguridad Con(string clave, Guid valor) => Poner(clave, valor.ToString("D"));

    public EvidenciaIncidenteSeguridad Con(string clave, long valor) => Poner(clave, valor);

    public EvidenciaIncidenteSeguridad Con(string clave, bool valor) => Poner(clave, valor);

    public EvidenciaIncidenteSeguridad Con(string clave, DateTime valorUtc)
        => Poner(clave, DateTime.SpecifyKind(valorUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));

    public EvidenciaIncidenteSeguridad Con<TEnum>(string clave, TEnum valor) where TEnum : struct, Enum
        => Poner(clave, valor.ToString());

    public EvidenciaIncidenteSeguridad Con(string clave, IEnumerable<Guid> valores)
        => Poner(clave, valores.Distinct().Order().Select(v => v.ToString("D")).ToArray());

    /// <summary>Claves leídas, para los tests.</summary>
    public IReadOnlyCollection<string> Claves => _campos.Keys;

    public string ComoJson() => JsonSerializer.Serialize(_campos);

    private EvidenciaIncidenteSeguridad Poner(string clave, object valor)
    {
        if (!EsClaveValida(clave))
            throw new ArgumentException("La clave de la evidencia tiene que ser un identificador corto (letras, dígitos y guion bajo).", nameof(clave));
        _campos[clave] = valor;
        return this;
    }

    private static bool EsClaveValida(string clave)
        => !string.IsNullOrEmpty(clave) && clave.Length <= 40 && clave.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
