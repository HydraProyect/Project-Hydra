using CaeManager.Domain.Common;

namespace CaeManager.Domain.Exportacion;

/// <summary>
/// Exportación completa de un Tenant propietario pedida en autoservicio por su Administrador (issue #2008,
/// decisión D-8 de 2026-11-15): un zip cifrado con todos sus datos, que genera un servicio de fondo bajo la RLS
/// del propio Tenant y que se puede descargar durante <see cref="Vigencia"/>, o hasta que otra más nueva del mismo
/// Tenant queda lista (<see cref="CaducarPorSustitucion"/>). Como mucho una cada <see cref="IntervaloMinimo"/> por
/// Tenant, sin contar las fallidas.
///
/// <para>
/// <b>Contraseña.</b> Se genera al pedirla y se devuelve una sola vez a quien la pide. Hasta que el servicio
/// de fondo cifra el fichero, la fila la guarda protegida con ASP.NET Data Protection
/// (<see cref="ContrasenaProtegida"/>), y la borra en el mismo guardado que marca la solicitud
/// <see cref="EstadoSolicitudExportacionCompleta.Lista"/> o <see cref="EstadoSolicitudExportacionCompleta.Fallida"/>:
/// después no queda forma de recuperarla.
/// </para>
/// </summary>
public class SolicitudExportacionCompleta : EntidadConTenant
{
    /// <summary>Lo que dura disponible el fichero desde que está listo (D-8).</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromDays(7);

    /// <summary>Separación mínima entre dos solicitudes del mismo Tenant que no fallaron (D-8).</summary>
    public static readonly TimeSpan IntervaloMinimo = TimeSpan.FromHours(24);

    public const int LongitudMaximaMotivoFallo = 200;
    public const int LongitudMaximaUbicacion = 500;

    /// <summary>El Actor real que la pidió: el Administrador del Tenant propietario, nunca un Usuario simulado.</summary>
    public Guid SolicitadaPorUsuarioId { get; private set; }

    public DateTime SolicitadaEnUtc { get; private set; }

    public EstadoSolicitudExportacionCompleta Estado { get; private set; } = EstadoSolicitudExportacionCompleta.Pendiente;

    /// <summary>
    /// La contraseña del zip protegida con Data Protection, solo mientras la solicitud está
    /// <see cref="EstadoSolicitudExportacionCompleta.Pendiente"/> o <see cref="EstadoSolicitudExportacionCompleta.EnCurso"/>.
    /// Nunca se exporta ni se audita.
    /// </summary>
    public string? ContrasenaProtegida { get; private set; }

    /// <summary>Clave del fichero en el almacén de exportaciones, solo mientras está <see cref="EstadoSolicitudExportacionCompleta.Lista"/>.</summary>
    public string? UbicacionFichero { get; private set; }

    public long? TamanoBytes { get; private set; }
    public DateTime? IniciadaEnUtc { get; private set; }
    public DateTime? DisponibleEnUtc { get; private set; }

    /// <summary><see cref="DisponibleEnUtc"/> más <see cref="Vigencia"/>.</summary>
    public DateTime? CaducaEnUtc { get; private set; }

    /// <summary>Cuándo falló o caducó.</summary>
    public DateTime? FinalizadaEnUtc { get; private set; }

    /// <summary>Código breve del fallo, sin datos del Tenant ni texto de excepciones.</summary>
    public string? MotivoFallo { get; private set; }

    private SolicitudExportacionCompleta()
    {
    }

    public SolicitudExportacionCompleta(Guid solicitadaPorUsuarioId, string contrasenaProtegida, DateTime ahoraUtc)
    {
        if (solicitadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La solicitud necesita a quien la pide.", nameof(solicitadaPorUsuarioId));
        if (string.IsNullOrWhiteSpace(contrasenaProtegida))
            throw new ArgumentException("La solicitud necesita la contraseña protegida del fichero.", nameof(contrasenaProtegida));

        SolicitadaPorUsuarioId = solicitadaPorUsuarioId;
        ContrasenaProtegida = contrasenaProtegida;
        SolicitadaEnUtc = ahoraUtc;
    }

    /// <summary>Cuenta para el límite de una cada <see cref="IntervaloMinimo"/>: todas salvo las fallidas.</summary>
    public bool CuentaParaElLimite => Estado != EstadoSolicitudExportacionCompleta.Fallida;

    public bool EstaDisponibleParaDescarga(DateTime ahoraUtc) =>
        Estado == EstadoSolicitudExportacionCompleta.Lista && UbicacionFichero is not null &&
        CaducaEnUtc is { } caduca && ahoraUtc < caduca;

    public void MarcarEnCurso(DateTime ahoraUtc)
    {
        Exigir(EstadoSolicitudExportacionCompleta.Pendiente);
        Estado = EstadoSolicitudExportacionCompleta.EnCurso;
        IniciadaEnUtc = ahoraUtc;
    }

    public void MarcarLista(string ubicacionFichero, long tamanoBytes, DateTime ahoraUtc)
    {
        Exigir(EstadoSolicitudExportacionCompleta.EnCurso);
        if (string.IsNullOrWhiteSpace(ubicacionFichero))
            throw new ArgumentException("Una exportación lista necesita su fichero.", nameof(ubicacionFichero));
        if (tamanoBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(tamanoBytes), "Una exportación lista no puede estar vacía.");

        Estado = EstadoSolicitudExportacionCompleta.Lista;
        UbicacionFichero = ubicacionFichero;
        TamanoBytes = tamanoBytes;
        DisponibleEnUtc = ahoraUtc;
        CaducaEnUtc = ahoraUtc + Vigencia;
        ContrasenaProtegida = null;
    }

    public void MarcarFallida(string motivo, DateTime ahoraUtc)
    {
        if (Estado is not (EstadoSolicitudExportacionCompleta.Pendiente or EstadoSolicitudExportacionCompleta.EnCurso))
            throw new InvalidOperationException($"Una solicitud {Estado} no puede fallar.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Un fallo debe decir su motivo.", nameof(motivo));

        Estado = EstadoSolicitudExportacionCompleta.Fallida;
        MotivoFallo = motivo.Length > LongitudMaximaMotivoFallo ? motivo[..LongitudMaximaMotivoFallo] : motivo;
        FinalizadaEnUtc = ahoraUtc;
        ContrasenaProtegida = null;
        UbicacionFichero = null;
    }

    public void MarcarCaducada(DateTime ahoraUtc)
    {
        Exigir(EstadoSolicitudExportacionCompleta.Lista);
        if (CaducaEnUtc is not { } caduca || ahoraUtc < caduca)
            throw new InvalidOperationException("La exportación todavía está vigente.");

        Estado = EstadoSolicitudExportacionCompleta.Caducada;
        UbicacionFichero = null;
        FinalizadaEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Caduca antes de su fecha porque el Tenant ya tiene otra lista más nueva: como mucho una exportación lista
    /// viva por Tenant (OWASP API4:2023, ASVS 4.0.3 V12.1), para acotar lo que ocupa en el volumen.
    /// </summary>
    public void CaducarPorSustitucion(DateTime ahoraUtc)
    {
        Exigir(EstadoSolicitudExportacionCompleta.Lista);
        Estado = EstadoSolicitudExportacionCompleta.Caducada;
        UbicacionFichero = null;
        FinalizadaEnUtc = ahoraUtc;
    }

    private void Exigir(EstadoSolicitudExportacionCompleta esperado)
    {
        if (Estado != esperado)
            throw new InvalidOperationException($"La solicitud está {Estado}; se esperaba {esperado}.");
    }
}
