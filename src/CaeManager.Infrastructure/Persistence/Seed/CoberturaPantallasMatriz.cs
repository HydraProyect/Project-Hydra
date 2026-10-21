namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Una casilla de la matriz de cobertura: qué debe poder verse en pantalla (<see cref="Dimension"/> + <see cref="Caso"/>) y
/// cuántos sujetos, como mínimo, lo ejercitan en lo que deja <see cref="CoberturaPantallasSeeder"/>.
/// </summary>
public sealed record CeldaDeCobertura(string Dimension, string Caso, int Minimo)
{
    public string Clave => $"{Dimension} · {Caso}";
}

/// <summary>
/// La matriz declarada de <see cref="CoberturaPantallasSeeder"/>, para que la revisión visual 1:1 sepa qué cuenta y qué
/// Tenant contiene cada caso, y para que un test la compare con lo que el sembrador deja de verdad
/// (<c>CoberturaPantallasTests</c>): una casilla declarada sin medir, o medida sin declarar, deja el test en rojo.
/// </summary>
public static class CoberturaPantallasMatriz
{
    /// <summary>Los siete estados de un documento tal y como los pinta la interfaz.</summary>
    public static readonly IReadOnlyList<string> EstadosDeDocumento =
        ["Bloqueado", "Vencido", "Pendiente", "En tolerancia", "Por vencer", "Sin confirmar", "Vigente"];

    /// <summary>
    /// Un Vehículo no puede bloquear (<c>ReglaBloqueoDeAcceso.AmbitoPuedeBloquear</c>) y la tolerancia es una regla de
    /// acceso de Trabajador y Empresa: dos de los siete estados no existen para él por regla, no por falta de datos.
    /// </summary>
    public static readonly IReadOnlySet<string> EstadosSinSentidoEnVehiculo = new HashSet<string> { "Bloqueado", "En tolerancia" };

    public const int TrabajadoresDelCentroMasivo = 100;
    public const int DocumentosPendientesDeLaEmpresaMasiva = 300;
    public const int MensajesDeLaConversacionLarga = 40;

    public static IReadOnlyList<CeldaDeCobertura> Celdas { get; } = Construir();

    private static List<CeldaDeCobertura> Construir()
    {
        var celdas = new List<CeldaDeCobertura>();

        foreach (var ambito in new[] { "Trabajador", "Empresa", "Vehículo" })
            foreach (var estado in EstadosDeDocumento)
                if (ambito != "Vehículo" || !EstadosSinSentidoEnVehiculo.Contains(estado))
                    celdas.Add(new CeldaDeCobertura($"Documento de {ambito}", estado, 1));

        celdas.AddRange(
        [
            new("Centro", "Bloqueado (plataforma CAE)", 1),
            new("Centro", "Sin requisitos", 1),
            new("Centro", "Sin gestión CAE", 1),
            new("Centro", "Requisitos solo para algunos Trabajadores", 1),
            new("Empresa", "Con delegaciones y Oficios", 1),
            new("Empresa", "Delegación con contacto de reclamación", 2),
            new("Empresa", "Delegación sin contacto de reclamación", 1),
            new("Empresa", "Sin contactos", 1),
            new("Volumen", "Trabajadores en un Centro", TrabajadoresDelCentroMasivo),
            new("Volumen", "Documentos pendientes de una Empresa", DocumentosPendientesDeLaEmpresaMasiva),
            new("Comunicaciones", "Conversación con muchos mensajes", 1),
            new("Comunicaciones", "Mensaje con adjunto", 1),
            new("Comunicaciones", "Remitente conocido", 1),
            new("Comunicaciones", "Remitente desconocido", 1),
            new("Visita", "Futura con Trabajador sin Asignación activa en el Centro", 1),
            new("Visita", "Trabajador extranjero en el mes", 3),
            new("Facturación", "Tarifa fijada", 4),
            new("Usuario", "Administrador", 1),
            new("Usuario", "Dirección CAE", 1),
            new("Usuario", "Coordinador CAE", 1),
            new("Usuario", "Gestor CAE con Asignación de Cartera", 1),
            new("Usuario", "Gestor CAE sin Asignación de Cartera", 1),
            new("Usuario", "Consulta", 1),
            new("Usuario", "Cliente", 1),
        ]);

        foreach (var estado in new[] { "Abierta", "Pendiente", "Resuelta", "Cerrada", "EsperandoRespuestaExterna", "EnEsperaDeTercero" })
            celdas.Add(new("Comunicaciones", $"Conversación {estado}", 1));

        return celdas;
    }
}
