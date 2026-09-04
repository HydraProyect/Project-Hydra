using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Registro evidenciario de que TALVEG tiene, para este Tenant propietario
/// (el responsable del tratamiento, ADR-011 § 2.1 — nunca el Pagador TALVEG
/// ni el Tenant beneficiario: coinciden a menudo, no siempre), una
/// instrucción documentada que cubre el tratamiento de datos personales
/// mediante IA (DEC-33, REC-035, acta
/// <c>decisiones/DEC-33-36-lote-D-2026-09-02.md</c> del repositorio de
/// negocio).
///
/// <b>Por qué es por tenant y la política técnica no.</b> DEC-33 separa dos
/// planos que este REC ya confundió una vez: el <b>proveedor, la región, las
/// transferencias, el DPA del proveedor, la retención técnica, los modelos y
/// el entrenamiento</b> son configuración técnica común de plataforma —
/// TALVEG elige el proveedor con sus propias credenciales, sin BYOK, y es
/// idéntica para todo tenant (documentada en
/// <c>tecnico/docs/POLITICA-TECNICA-IA.md</c> del repositorio de negocio, no
/// en código). Lo que varía por tenant no es la técnica: es que TALVEG, como
/// encargado del tratamiento, solo puede procesar datos personales de un
/// tenant conforme a instrucciones documentadas de SU responsable — y esa
/// instrucción (qué versión de DPA y de subencargados aceptó, cuándo, bajo
/// qué relación contractual) es, por definición, una afirmación distinta
/// para cada Tenant propietario. Esta entidad NO lleva proveedor, región,
/// modelo ni retención — ningún campo que pudiera convertirla en la
/// `PoliticaIA` configurable por tenant que DEC-33 prohíbe explícitamente.
///
/// <b>Extiende <see cref="EntidadConTenant"/></b> — a diferencia de
/// <see cref="AceptacionTerminos"/> y <see cref="Tenants.DelegacionTenant"/>
/// (catálogo global sin RLS, porque una aceptación es de un usuario que
/// puede operar varios tenants, o una delegación enlaza dos tenants a la
/// vez), esta fila describe a un único Tenant propietario y solo a él: no
/// hay ninguna relación cruzada que impida el aislamiento estándar. Lleva
/// RLS y su ratchet como cualquier tabla tenantizada (ver
/// <c>CoberturaRlsDelModeloTests</c>/<c>PoliticasRlsCubrenModeloTests</c>) —
/// exactamente lo que un registro evidenciario de cumplimiento necesita: que
/// ningún otro tenant, ni un rol restringido, pueda leerlo o escribirlo.
///
/// <b>Se registra desde la plataforma, no la autogestiona el tenant</b> (§
/// 4.5 de <c>POLITICA-TECNICA-IA.md</c>, mismo criterio de alcance que
/// <c>RegistrarSuscripcionTenantCommand</c>/<c>GenerarClaveApiCommand</c>):
/// el comando administrativo escribe esta fila sobre el Tenant propietario
/// elegido usando <c>AmbitoTenantExplicito.Establecer</c>, no la sesión
/// ambiental del administrador.
///
/// <b>Append-only.</b> Nunca se edita ni se borra — una re-aceptación tras
/// una versión nueva de DPA/Anexo II crea una fila nueva, mismo criterio que
/// <see cref="AceptacionTerminos"/>. A diferencia de esa, aquí SÍ hay
/// <see cref="Revocar"/> explícito: el Tenant propietario puede retirar la
/// autorización sin esperar a que cambie ninguna versión — cerrar la fila
/// vigente, nunca borrarla.
/// </summary>
public class InstruccionTratamientoIaTenantPropietario : EntidadConTenant
{
    public const int LongitudMaximaVersion = 20;
    public const int LongitudMaximaMotivoRevocacion = 500;

    /// <summary>Versión de <c>legal/DPA.md</c> aceptada — análogo a <see cref="VersionTerminos.Actual"/>.</summary>
    public string VersionDpaAceptada { get; private set; } = string.Empty;

    /// <summary>
    /// Versión de la tabla de <c>legal/LISTA_SUBENCARGADOS.md</c> § 3
    /// aceptada — incluye, desde HO-035-01, las filas Draft de los tres
    /// proveedores de IA.
    /// </summary>
    public string VersionAnexoSubencargadosAceptada { get; private set; } = string.Empty;

    public DateTime FechaAceptacionUtc { get; private set; }

    /// <summary>De dónde sale la instrucción — nunca "elegida por el tenant" (ver <see cref="OrigenInstruccionTratamientoIa"/>).</summary>
    public OrigenInstruccionTratamientoIa OrigenInstruccion { get; private set; }

    /// <summary>
    /// Quién, dentro de la plataforma, registró esta fila — nunca el Tenant
    /// propietario: es la coordenada de accountability que
    /// <see cref="AceptacionTerminos"/> no necesita (allí el usuario que
    /// acepta y el sujeto de la fila son la misma persona; aquí no).
    /// </summary>
    public Guid RegistradaPorUsuarioId { get; private set; }

    public DateTime? RevocadaEnUtc { get; private set; }
    public string? MotivoRevocacion { get; private set; }

    /// <summary>Vigente ⇔ no revocada. Ver <c>IInstruccionTratamientoIaService</c> (Application) para el único punto de consulta.</summary>
    public bool EstaVigente => RevocadaEnUtc is null;

    private InstruccionTratamientoIaTenantPropietario()
    {
        // Requerido por EF Core.
    }

    public InstruccionTratamientoIaTenantPropietario(
        string versionDpaAceptada,
        string versionAnexoSubencargadosAceptada,
        DateTime fechaAceptacionUtc,
        OrigenInstruccionTratamientoIa origenInstruccion,
        Guid registradaPorUsuarioId)
    {
        VersionDpaAceptada = ValidarVersion(versionDpaAceptada, nameof(versionDpaAceptada));
        VersionAnexoSubencargadosAceptada = ValidarVersion(versionAnexoSubencargadosAceptada, nameof(versionAnexoSubencargadosAceptada));

        if (registradaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La instrucción tiene que quedar atribuida a quién la registró en plataforma.", nameof(registradaPorUsuarioId));

        FechaAceptacionUtc = fechaAceptacionUtc;
        OrigenInstruccion = origenInstruccion;
        RegistradaPorUsuarioId = registradaPorUsuarioId;
    }

    /// <summary>
    /// Cierra la fila vigente. No la borra: la fila revocada sigue
    /// demostrando que hubo una instrucción y cuándo dejó de valer — el
    /// mismo criterio de conservación que <c>DelegacionTenant.Desactivar</c>.
    /// </summary>
    public void Revocar(string motivo, DateTime ahoraUtc)
    {
        if (RevocadaEnUtc is not null)
            throw new InvalidOperationException("Esta instrucción ya estaba revocada.");

        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Retirar la autorización de tratamiento con IA exige dejar dicho por qué.", nameof(motivo));

        var normalizado = motivo.Trim();
        if (normalizado.Length > LongitudMaximaMotivoRevocacion)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivoRevocacion} caracteres.", nameof(motivo));

        RevocadaEnUtc = ahoraUtc;
        MotivoRevocacion = normalizado;
    }

    private static string ValidarVersion(string version, string nombreParametro)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("La versión aceptada es obligatoria.", nombreParametro);

        var normalizada = version.Trim();
        if (normalizada.Length > LongitudMaximaVersion)
            throw new ArgumentException($"La versión no puede superar {LongitudMaximaVersion} caracteres.", nombreParametro);

        return normalizada;
    }
}
