using CaeManager.Domain.Common;

namespace CaeManager.Domain.Seguridad;

/// <summary>
/// Bloqueo de inicio de sesión por país, por Tenant: lista de países desde los
/// que se permite entrar a las cuentas del Tenant propietario.
///
/// <para>
/// <b>Desactivado por defecto.</b> Un Tenant sin fila, o con
/// <see cref="Activa"/> en falso, no bloquea nada. La pantalla de
/// configuración todavía no existe (pasa antes por la maqueta de Design); hasta
/// entonces solo se activa a mano.
/// </para>
///
/// <para>
/// <b>País desconocido no bloquea.</b> Sin base de geolocalización, o con una
/// IP que la base no ubica, el país es desconocido y la entrada se permite (y
/// queda registrada como desconocida): bloquear lo desconocido dejaría fuera a
/// todo el Tenant el día que falte la base, y la aplicación debe funcionar igual
/// sin ella.
/// </para>
/// </summary>
public class PoliticaPaisesInicioSesion : EntidadConTenant
{
    private List<string> _paisesPermitidos = [];

    public bool Activa { get; private set; }

    /// <summary>Códigos ISO 3166-1 alfa-2 en mayúsculas, sin repetir.</summary>
    public IReadOnlyList<string> PaisesPermitidos => _paisesPermitidos;

    public DateTime ActualizadaEnUtc { get; private set; }

    private PoliticaPaisesInicioSesion()
    {
    }

    public PoliticaPaisesInicioSesion(bool activa, IEnumerable<string> paisesPermitidos)
    {
        Configurar(activa, paisesPermitidos);
    }

    /// <summary>
    /// Fija la lista y el estado. Activar con la lista vacía se rechaza: dejaría
    /// fuera a todas las cuentas del Tenant, incluido quien la configura.
    /// </summary>
    public void Configurar(bool activa, IEnumerable<string> paisesPermitidos)
    {
        var paises = paisesPermitidos
            .Select(p => p.Trim().ToUpperInvariant())
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (paises.Any(p => p.Length != 2 || !p.All(char.IsAsciiLetterUpper)))
            throw new ArgumentException("Cada país debe ser un código ISO 3166-1 alfa-2.", nameof(paisesPermitidos));
        if (activa && paises.Count == 0)
            throw new ArgumentException("No se puede activar el bloqueo por país sin ningún país permitido.", nameof(paisesPermitidos));

        Activa = activa;
        _paisesPermitidos = paises;
        ActualizadaEnUtc = DateTime.UtcNow;
    }

    /// <summary>¿Se permite entrar desde <paramref name="paisIso"/>? Nulo = desconocido = se permite.</summary>
    public bool Permite(string? paisIso)
        => !Activa || paisIso is null || _paisesPermitidos.Contains(paisIso.ToUpperInvariant());
}
