namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Estado de una concesión de privilegio de plataforma. Igual que las
/// asignaciones operativas, una concesión no se edita ni se borra: se revoca y,
/// si hace falta otra vez, se concede de nuevo. El histórico de quién pudo qué
/// y cuándo es la mitad del valor de este plano.
/// </summary>
public enum EstadoConcesionPrivilegio
{
    Vigente = 0,

    /// <summary>Retirada por decisión explícita. Estado final.</summary>
    Revocada = 1,

    /// <summary>Llegó su fecha de fin. Estado final; lo aplica el barrido de vigencias.</summary>
    Expirada = 2
}
