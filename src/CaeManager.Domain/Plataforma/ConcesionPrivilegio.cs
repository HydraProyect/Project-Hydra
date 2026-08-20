using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Autorización durable de un usuario de la plataforma para ejercer una
/// capacidad sobre uno o varios tenants — el <i>grant</i> del plano 3
/// (ADR-011 § 8.1).
///
/// <b>No es una delegación CAE.</b> Que un técnico de soporte pueda abrir los
/// datos de Refrielectric para resolver una incidencia no convierte a TALVEG en
/// operador CAE de Refrielectric, ni al técnico en usuario de su tenant. Es una
/// capacidad administrativa de la plataforma, ortogonal a la propiedad de los
/// datos y a quién los opera — y por eso vive en su propia entidad y no como
/// una <c>DelegacionTenant</c> más, que es justo el error que hoy arrastra el
/// acceso de soporte.
///
/// <b>Conceder no es acceder.</b> Una concesión vigente no toca nada por sí
/// sola: abrir el acceso es un acto aparte, con motivo y ventana, que produce
/// una <see cref="SesionPrivilegiada"/>. Igual que la delegación de soporte
/// actual nace inactiva, aquí el grant y su uso están separados a propósito: si
/// la cuenta se ve comprometida, no abre ninguna puerta por sí misma.
///
/// Extiende <see cref="Entity"/>: es un catálogo global de autorización que
/// cruza tenants por naturaleza, mismo tratamiento que <c>Tenant</c> y las
/// asignaciones operativas. Estar fuera del filtro global <b>no</b> la hace
/// legible sin restricción.
/// </summary>
public class ConcesionPrivilegio : Entity, IVersionable
{
    public const int LongitudMaximaMotivo = 500;

    /// <summary>
    /// El usuario de la plataforma que recibe la capacidad. Guid suelto hacia
    /// <c>ApplicationUser</c> — Domain no referencia Identity, mismo patrón que
    /// <c>AsignacionCartera.UsuarioId</c>. Que pertenezca al tenant marcado
    /// como plataforma lo comprueba el comando de alta: aquí no se puede.
    /// </summary>
    public Guid UsuarioPlataformaId { get; private set; }

    public CapacidadPrivilegio Capacidad { get; private set; }

    /// <summary>
    /// Alcance global sobre todos los tenants. <b>Solo se admite para
    /// <see cref="CapacidadPrivilegio.AdminPlataforma"/></b>, y ni siquiera
    /// entonces implica leer contenido: administrar la plataforma es otra cosa
    /// que abrir los datos de un cliente (ADR-011 § 8.9). Para el resto de
    /// capacidades el alcance es siempre una lista explícita de tenants.
    /// </summary>
    public bool EsAlcanceGlobal { get; private set; }

    private readonly List<TenantAlcanzadoPorConcesion> _tenantsAlcanzados = [];
    public IReadOnlyCollection<TenantAlcanzadoPorConcesion> TenantsAlcanzados => _tenantsAlcanzados;

    public DateTime VigenciaDesde { get; private set; }
    public DateTime? VigenciaHasta { get; private set; }
    public EstadoConcesionPrivilegio Estado { get; private set; }

    /// <summary>
    /// Quién la concedió. Se registra desde el primer día aunque hoy sea
    /// habitual la auto-concesión —el equipo de plataforma es unipersonal, y
    /// ADR-011 § 8.5 la acepta con auditoría íntegra—: el día que haya equipo,
    /// la segregación de funciones se activa sobre un dato que ya existe.
    /// </summary>
    public Guid? ConcedidaPorUsuarioId { get; private set; }

    public string? MotivoConcesion { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private ConcesionPrivilegio()
    {
        // Requerido por EF Core.
    }

    private ConcesionPrivilegio(
        Guid usuarioPlataformaId,
        CapacidadPrivilegio capacidad,
        bool esAlcanceGlobal,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        Guid? concedidaPorUsuarioId,
        string? motivoConcesion)
    {
        if (usuarioPlataformaId == Guid.Empty)
            throw new ArgumentException("La concesión debe tener un usuario de plataforma.", nameof(usuarioPlataformaId));
        if (vigenciaHasta is not null && vigenciaHasta <= vigenciaDesde)
            throw new ArgumentException("La vigencia debe terminar después de empezar.", nameof(vigenciaHasta));
        if (motivoConcesion is { Length: > LongitudMaximaMotivo })
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.", nameof(motivoConcesion));

        UsuarioPlataformaId = usuarioPlataformaId;
        Capacidad = capacidad;
        EsAlcanceGlobal = esAlcanceGlobal;
        VigenciaDesde = vigenciaDesde;
        VigenciaHasta = vigenciaHasta;
        ConcedidaPorUsuarioId = concedidaPorUsuarioId;
        MotivoConcesion = string.IsNullOrWhiteSpace(motivoConcesion) ? null : motivoConcesion.Trim();
        Estado = EstadoConcesionPrivilegio.Vigente;
    }

    /// <summary>
    /// Concesión acotada a una lista explícita de tenants. Es la forma normal:
    /// "soporte A puede entrar en Refrielectric", no "soporte A puede entrar
    /// donde sea" (ADR-011 § 8.9).
    /// </summary>
    public static ConcesionPrivilegio SobreTenants(
        Guid usuarioPlataformaId,
        CapacidadPrivilegio capacidad,
        IReadOnlyCollection<Guid> tenantIds,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        Guid? concedidaPorUsuarioId = null,
        string? motivoConcesion = null)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        if (tenantIds.Count == 0)
            throw new ArgumentException("Una concesión acotada necesita al menos un tenant.", nameof(tenantIds));
        if (tenantIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("Ningún tenant del alcance puede ser vacío.", nameof(tenantIds));

        var concesion = new ConcesionPrivilegio(
            usuarioPlataformaId, capacidad, esAlcanceGlobal: false,
            vigenciaDesde, vigenciaHasta, concedidaPorUsuarioId, motivoConcesion);

        foreach (var tenantId in tenantIds.Distinct())
            concesion._tenantsAlcanzados.Add(new TenantAlcanzadoPorConcesion(concesion.Id, tenantId));

        return concesion;
    }

    /// <summary>
    /// Concesión sobre todos los tenants. Reservada a
    /// <see cref="CapacidadPrivilegio.AdminPlataforma"/>: un alcance global de
    /// lectura sería precisamente la cuenta de soporte omnipotente que el
    /// principio de mínimo privilegio prohíbe (ADR-011 § 8.8).
    /// </summary>
    public static ConcesionPrivilegio Global(
        Guid usuarioPlataformaId,
        DateTime vigenciaDesde,
        DateTime? vigenciaHasta,
        Guid? concedidaPorUsuarioId = null,
        string? motivoConcesion = null) =>
        new(usuarioPlataformaId, CapacidadPrivilegio.AdminPlataforma, esAlcanceGlobal: true,
            vigenciaDesde, vigenciaHasta, concedidaPorUsuarioId, motivoConcesion);

    /// <summary>
    /// Si la concesión permite actuar sobre ese tenant en ese instante. Las
    /// tres condiciones —estado, ventana y alcance— se comprueban juntas a
    /// propósito: comprobar solo el alcance dejaría vivas las concesiones
    /// caducadas, que es el fallo que la ventana viene a evitar.
    /// </summary>
    public bool CubreEn(Guid tenantId, DateTime momento) =>
        Estado == EstadoConcesionPrivilegio.Vigente
        && VigenciaDesde <= momento
        && (VigenciaHasta is null || momento < VigenciaHasta)
        && (EsAlcanceGlobal || _tenantsAlcanzados.Any(t => t.TenantId == tenantId));

    public bool HaExpiradoEn(DateTime ahora) =>
        Estado == EstadoConcesionPrivilegio.Vigente
        && VigenciaHasta is not null && ahora >= VigenciaHasta;

    public void Revocar(DateTime ahora)
    {
        if (Estado != EstadoConcesionPrivilegio.Vigente)
            throw new InvalidOperationException($"Solo se puede revocar una concesión vigente (estaba {Estado}).");

        Estado = EstadoConcesionPrivilegio.Revocada;
        AdelantarFinDeVigencia(ahora);
    }

    public void MarcarExpirada(DateTime ahora)
    {
        if (Estado != EstadoConcesionPrivilegio.Vigente)
            throw new InvalidOperationException($"Solo se puede expirar una concesión vigente (estaba {Estado}).");

        Estado = EstadoConcesionPrivilegio.Expirada;
        AdelantarFinDeVigencia(ahora);
    }

    /// <summary>
    /// Nunca alarga la ventana: una concesión terminada no puede aparecer como
    /// vigente en una consulta histórica posterior a su fin.
    /// </summary>
    private void AdelantarFinDeVigencia(DateTime ahora)
    {
        if (VigenciaHasta is null || VigenciaHasta > ahora)
            VigenciaHasta = ahora > VigenciaDesde ? ahora : VigenciaDesde;
    }
}
