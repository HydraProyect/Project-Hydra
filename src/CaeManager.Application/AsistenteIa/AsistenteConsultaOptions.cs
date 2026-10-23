namespace CaeManager.Application.AsistenteIa;

public class AsistenteConsultaOptions
{
    public const string SeccionConfiguracion = "Anthropic:Consulta";

    // Acotan el coste por petición y por hora sin recortar el historial visible de la persona.
    public int MaxCaracteresPregunta { get; set; } = 4000;
    public int MaxTurnosHistorial { get; set; } = 10;
    public int MaxCaracteresHistorial { get; set; } = 40000;
    public int MaxPreguntasUsuario { get; set; } = 30;
    public int MaxPreguntasTenant { get; set; } = 300;
    public TimeSpan VentanaCuota { get; set; } = TimeSpan.FromHours(1);

    public bool EsValida() => MaxCaracteresPregunta > 0 && MaxTurnosHistorial > 0
        && MaxCaracteresHistorial >= MaxCaracteresPregunta && MaxPreguntasUsuario > 0
        && MaxPreguntasTenant > 0 && VentanaCuota > TimeSpan.Zero;
}
