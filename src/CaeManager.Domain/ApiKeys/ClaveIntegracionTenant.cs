using CaeManager.Domain.Common;

namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Clave de Integración del Tenant: credencial duradera con la que el sistema de un Tenant
/// propietario, por ejemplo su ERP, lee datos de ese Tenant. La emite y la revoca su Administrador
/// del Tenant, no depende de ninguna delegación y no es la Clave de Soporte TALVEG
/// (<see cref="ClaveApi"/>): otra entidad, otra tabla y otros cortes, para que ninguna pueda hacerse
/// pasar por la otra (decisiones D-1 a D-3b de la vía de integración, 2026-10-31).
///
/// <para>
/// El secreto lo acuña siempre TALVEG, porque el BYOK de credenciales sigue prohibido, y solo se
/// guarda su hash, como una contraseña. El Tenant sale de esta fila, nunca de un parámetro: la clave de
/// un Operador CAE externo lee su Tenant propietario, nunca sus Tenants beneficiarios
/// (tenant isolation ≠ operational delegation ≠ platform privilege).
/// </para>
///
/// <para>
/// <see cref="CreadaPorUsuarioId"/> es una traza, no un respaldo: la clave es de la organización y
/// sobrevive a la baja de quien la emitió (D-3b).
/// </para>
/// </summary>
public class ClaveIntegracionTenant : EntidadBase
{
    public const string PrefijoSecreto = "talveg_int_";
    public const int LongitudMaximaPrefijoVisible = 24;
    public const int LongitudHashClave = 64;
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaSistemaConsumidor = 200;
    public const int LongitudMaximaMotivoSuspension = 500;
    public const int DiasCaducidadPorDefecto = 180;
    public const int DiasCaducidadMaxima = 365;
    public const int DiasConvivenciaTrasRotar = 14;

    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Texto libre que dice qué sistema la usa, por ejemplo «ERP de nóminas».</summary>
    public string SistemaConsumidor { get; private set; } = string.Empty;

    /// <summary>Primeros caracteres del secreto, en claro, solo para reconocerla en un listado.</summary>
    public string PrefijoVisible { get; private set; } = string.Empty;

    public string HashClave { get; private set; } = string.Empty;

    /// <summary>Lo que persiste EF. Es propiedad, no campo, porque la exportación del Tenant solo lee propiedades.</summary>
    private List<string> AlcancesConcedidos { get; set; } = [];
    public IReadOnlyList<string> Alcances => AlcancesConcedidos.AsReadOnly();

    public DateTime CaducaEnUtc { get; private set; }
    public DateTime? SuspendidaEnUtc { get; private set; }
    public string? MotivoSuspension { get; private set; }
    public DateTime? RevocadaEnUtc { get; private set; }
    public DateTime? InvalidadaDefinitivamenteEnUtc { get; private set; }
    public Guid? SucedeAClaveId { get; private set; }
    public Guid CreadaPorUsuarioId { get; private set; }
    public DateTime? UltimoUsoUtc { get; private set; }

    private ClaveIntegracionTenant()
    {
        // Requerido por EF Core.
    }

    public ClaveIntegracionTenant(
        string nombre, string sistemaConsumidor, string prefijoVisible, string hashClave,
        IEnumerable<string> alcances, DateTime caducaEnUtc, Guid creadaPorUsuarioId)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > LongitudMaximaNombre)
            throw new ArgumentException("La clave necesita un nombre de 200 caracteres como máximo.", nameof(nombre));
        if (string.IsNullOrWhiteSpace(sistemaConsumidor) || sistemaConsumidor.Trim().Length > LongitudMaximaSistemaConsumidor)
            throw new ArgumentException("Indica el sistema que la usa, con 200 caracteres como máximo.", nameof(sistemaConsumidor));
        if (string.IsNullOrWhiteSpace(prefijoVisible)
            || !prefijoVisible.StartsWith(PrefijoSecreto, StringComparison.Ordinal)
            || prefijoVisible.Length > LongitudMaximaPrefijoVisible)
            throw new ArgumentException("El prefijo visible no es válido: Soporte TALVEG lo lee, no puede llevar el secreto.", nameof(prefijoVisible));
        if (hashClave is null || hashClave.Length != LongitudHashClave || !hashClave.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("El hash de la clave debe ser SHA-256 en hexadecimal.", nameof(hashClave));
        if (creadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La clave debe registrar quién la emitió.", nameof(creadaPorUsuarioId));
        var ahora = DateTime.UtcNow;
        if (caducaEnUtc.Kind != DateTimeKind.Utc || caducaEnUtc <= ahora || caducaEnUtc > ahora.AddDays(DiasCaducidadMaxima))
            throw new ArgumentException("La caducidad debe estar en el futuro y no superar 365 días.", nameof(caducaEnUtc));

        Nombre = nombre.Trim();
        SistemaConsumidor = sistemaConsumidor.Trim();
        PrefijoVisible = prefijoVisible;
        HashClave = hashClave;
        AlcancesConcedidos = ValidarAlcances(alcances);
        CaducaEnUtc = caducaEnUtc;
        CreadaPorUsuarioId = creadaPorUsuarioId;
    }

    /// <summary>
    /// Rotar emite una sucesora con los mismos alcances o menos, y esta clave sigue valiendo como
    /// mucho <see cref="DiasConvivenciaTrasRotar"/> días más. Rotar nunca alarga su vida.
    /// </summary>
    public ClaveIntegracionTenant EmitirSucesora(
        string prefijoVisible, string hashClave, IEnumerable<string> alcances, DateTime caducaEnUtc, Guid emitidaPorUsuarioId)
    {
        var ahora = DateTime.UtcNow;
        if (!PuedeAutenticar(ahora))
            throw new InvalidOperationException("Solo se rota una clave que sigue vigente.");

        var sucesora = new ClaveIntegracionTenant(Nombre, SistemaConsumidor, prefijoVisible, hashClave, alcances, caducaEnUtc, emitidaPorUsuarioId)
        {
            SucedeAClaveId = Id,
        };
        if (!sucesora.AlcancesConcedidos.All(AlcancesConcedidos.Contains))
            throw new ArgumentException("Rotar no puede ampliar los alcances de la clave.", nameof(alcances));

        var finConvivencia = ahora.AddDays(DiasConvivenciaTrasRotar);
        if (finConvivencia < CaducaEnUtc)
            CaducaEnUtc = finConvivencia;
        return sucesora;
    }

    public void Revocar()
    {
        RevocadaEnUtc ??= DateTime.UtcNow;
    }

    /// <summary>Corte reversible, por incidente o por decisión del Administrador del Tenant propietario.</summary>
    public void Suspender(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > LongitudMaximaMotivoSuspension)
            throw new ArgumentException("Indica el motivo de la suspensión, con un máximo de 500 caracteres.", nameof(motivo));
        if (SuspendidaEnUtc is not null || RevocadaEnUtc is not null || InvalidadaDefinitivamenteEnUtc is not null) return;

        SuspendidaEnUtc = DateTime.UtcNow;
        MotivoSuspension = motivo.Trim();
    }

    public void Reactivar()
    {
        if (RevocadaEnUtc is not null || InvalidadaDefinitivamenteEnUtc is not null)
            throw new InvalidOperationException("Una clave revocada o invalidada no se reactiva.");
        if (DateTime.UtcNow >= CaducaEnUtc)
            throw new InvalidOperationException("Una clave caducada no se reactiva: hay que emitir otra.");

        SuspendidaEnUtc = null;
        MotivoSuspension = null;
    }

    /// <summary>Corte que ninguna reactivación deshace.</summary>
    public void InvalidarDefinitivamente()
    {
        InvalidadaDefinitivamenteEnUtc ??= DateTime.UtcNow;
    }

    public void RegistrarUso() => UltimoUsoUtc = DateTime.UtcNow;

    /// <summary>
    /// Lo que la fila decide por sí sola. Los cortes del Tenant propietario se evalúan aparte, con
    /// <see cref="Tenants.Tenant.AdmiteClaveEmitidaEn"/> e <see cref="Tenants.Tenant.IntegracionHabilitada"/>.
    /// </summary>
    public bool PuedeAutenticar(DateTime ahoraUtc) =>
        RevocadaEnUtc is null
        && SuspendidaEnUtc is null
        && InvalidadaDefinitivamenteEnUtc is null
        && ahoraUtc < CaducaEnUtc;

    private static List<string> ValidarAlcances(IEnumerable<string> alcances)
    {
        var lista = alcances?.ToList() ?? throw new ArgumentNullException(nameof(alcances));
        if (lista.Count == 0)
            throw new ArgumentException("La clave necesita al menos un alcance: ninguno se concede solo.", nameof(alcances));
        if (lista.Any(a => !AlcancesIntegracion.Todos.Contains(a)))
            throw new ArgumentException("La clave solo admite alcances conocidos.", nameof(alcances));
        if (lista.Distinct(StringComparer.Ordinal).Count() != lista.Count)
            throw new ArgumentException("La clave no admite alcances repetidos.", nameof(alcances));
        if (lista.Contains(AlcancesIntegracion.TrabajadoresDatosPersonalesLeer) && !lista.Contains(AlcancesIntegracion.TrabajadoresLeer))
            throw new ArgumentException("Los datos personales de Trabajador necesitan también el alcance de lectura de Trabajadores.", nameof(alcances));
        return lista;
    }
}
