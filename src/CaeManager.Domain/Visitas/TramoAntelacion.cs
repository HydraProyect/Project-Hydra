namespace CaeManager.Domain.Visitas;

/// <summary>
/// Clasificación de una Visita según el margen real del que dispuso el gestor
/// (la antelación <b>efectiva</b>, no la que el cliente cree haber dado). Los
/// cortes son los mismos <c>HorasAvisoVisita</c>/<c>HorasCriticasVisita</c> de
/// <see cref="Configuracion.ParametroSistema"/> que ya usa
/// <see cref="CalculadoraUrgenciaVisita"/>, para que no convivan dos escalas
/// de urgencia distintas en el sistema.
/// </summary>
public enum TramoAntelacion
{
    /// <summary>Más de HorasAvisoVisita (48h por defecto) de margen efectivo.</summary>
    Estandar = 0,

    /// <summary>Entre HorasCriticasVisita y HorasAvisoVisita (24-48h por defecto).</summary>
    Urgente = 1,

    /// <summary>Menos de HorasCriticasVisita (24h por defecto), o entrada fuera de la franja de jornada.</summary>
    Expres = 2
}
