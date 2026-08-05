using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Configuración global editable por Administrador. Fila única en la base de
/// datos (impuesto por Infrastructure, no por esta clase). Corresponde a la
/// sección "Umbrales de alerta" de la hoja "Parametros" del Excel original.
/// </summary>
public class ParametroSistema : EntidadConTenant
{
    public int UmbralAmbarDias { get; private set; }
    public int UmbralRojoDias { get; private set; }

    /// <summary>Horas hasta el inicio de una Visita a partir de las cuales se considera "gestión urgente" — la mayoría de plataformas de clientes exigen un plazo mínimo de validación (típicamente 24-48h) antes de la entrada.</summary>
    public int HorasAvisoVisita { get; private set; } = 48;

    /// <summary>Horas hasta el inicio a partir de las cuales la urgencia pasa a "crítica" — por debajo de este margen, es probable que la plataforma del cliente ya no llegue a validar a tiempo.</summary>
    public int HorasCriticasVisita { get; private set; } = 24;

    private ParametroSistema()
    {
    }

    public ParametroSistema(int umbralAmbarDias, int umbralRojoDias, int horasAvisoVisita = 48, int horasCriticasVisita = 24)
    {
        Actualizar(umbralAmbarDias, umbralRojoDias);
        ActualizarUmbralesVisita(horasAvisoVisita, horasCriticasVisita);
    }

    public void Actualizar(int umbralAmbarDias, int umbralRojoDias)
    {
        if (umbralRojoDias <= 0)
            throw new ArgumentException("El umbral rojo debe ser mayor que cero.", nameof(umbralRojoDias));
        if (umbralAmbarDias <= umbralRojoDias)
            throw new ArgumentException("El umbral ámbar debe ser mayor que el umbral rojo.", nameof(umbralAmbarDias));

        UmbralAmbarDias = umbralAmbarDias;
        UmbralRojoDias = umbralRojoDias;
    }

    public void ActualizarUmbralesVisita(int horasAvisoVisita, int horasCriticasVisita)
    {
        if (horasCriticasVisita <= 0)
            throw new ArgumentException("Las horas críticas deben ser mayores que cero.", nameof(horasCriticasVisita));
        if (horasAvisoVisita <= horasCriticasVisita)
            throw new ArgumentException("Las horas de aviso deben ser mayores que las horas críticas.", nameof(horasAvisoVisita));

        HorasAvisoVisita = horasAvisoVisita;
        HorasCriticasVisita = horasCriticasVisita;
    }
}
