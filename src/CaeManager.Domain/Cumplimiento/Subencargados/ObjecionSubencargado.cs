using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento.Subencargados;

/// <summary>
/// La objeción del Administrador de un Tenant propietario a un subencargado (#2010, D-7). Es del Tenant propietario
/// (<see cref="EntidadConTenant"/>, con RLS) y solo se añade: nunca se edita ni se borra.
///
/// <para>
/// No frena el subencargado para los demás Tenants. Su <see cref="Efecto"/> se resuelve al registrarla con lo que el
/// subencargado admite en ese momento (<see cref="Subencargado.EfectoDeUnaObjecion"/>): con
/// <see cref="EfectoObjecionSubencargado.ExcluidoParaElTenant"/>, este Tenant deja de usarlo por el
/// <see cref="Conmutador"/> copiado del subencargado; con
/// <see cref="EfectoObjecionSubencargado.ResolucionSinPenalizacionDisponible"/>, la fila es la constancia de que este
/// Tenant puede resolver sin penalización.
/// </para>
/// </summary>
public class ObjecionSubencargado : EntidadConTenant
{
    public const int LongitudMaximaMotivo = 1_000;
    public const int LongitudMaximaEfecto = 40;
    public const int LongitudMaximaConmutador = 20;

    public Guid SubencargadoId { get; private set; }

    /// <summary>La cuenta del Administrador del Tenant propietario que objeta.</summary>
    public Guid UsuarioId { get; private set; }

    public string Motivo { get; private set; } = string.Empty;
    public DateTime ObjetadaEnUtc { get; private set; }
    public EfectoObjecionSubencargado Efecto { get; private set; }

    /// <summary>Solo con <see cref="EfectoObjecionSubencargado.ExcluidoParaElTenant"/>.</summary>
    public ConmutadorExclusionSubencargado? Conmutador { get; private set; }

    private ObjecionSubencargado()
    {
        // Requerido por EF Core.
    }

    public ObjecionSubencargado(Subencargado subencargado, Guid usuarioId, string motivo, DateTime objetadaEnUtc)
    {
        ArgumentNullException.ThrowIfNull(subencargado);
        if (!subencargado.AdmiteObjecion)
            throw new InvalidOperationException("Este subencargado no admite objeciones: no se ha avisado o ya está retirado.");
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La objeción tiene que quedar atribuida a quien la registra.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Indica el motivo de la objeción.", nameof(motivo));
        var normalizado = motivo.Trim();
        if (normalizado.Length > LongitudMaximaMotivo)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.", nameof(motivo));

        SubencargadoId = subencargado.Id;
        UsuarioId = usuarioId;
        Motivo = normalizado;
        ObjetadaEnUtc = Subencargado.Utc(objetadaEnUtc, nameof(objetadaEnUtc));
        Efecto = subencargado.EfectoDeUnaObjecion;
        Conmutador = Efecto == EfectoObjecionSubencargado.ExcluidoParaElTenant ? subencargado.Conmutador : null;
    }
}
