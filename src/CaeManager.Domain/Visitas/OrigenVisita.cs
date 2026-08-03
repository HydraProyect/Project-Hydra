namespace CaeManager.Domain.Visitas;

/// <summary>Cómo llegó la petición de agendar la visita — no cómo se dio de alta en Hydra (ambas rutas usan el mismo formulario/Command).</summary>
public enum OrigenVisita
{
    Plataforma,
    Correo
}
