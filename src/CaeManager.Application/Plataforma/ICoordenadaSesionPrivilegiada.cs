namespace CaeManager.Application.Plataforma;

/// <summary>
/// El circuito tiene seleccionada una Sesión Privilegiada, viva o ya caducada. Es la coordenada en
/// bruto que decide la identidad de PostgreSQL: <see cref="ISesionPrivilegiadaActual"/> deja de
/// devolver una sesión caducada mientras el circuito aún la nombra. Una coordenada no es autoridad:
/// sirve para negar, nunca para conceder.
/// </summary>
public interface ICoordenadaSesionPrivilegiada
{
    bool HaySeleccionada { get; }
}
