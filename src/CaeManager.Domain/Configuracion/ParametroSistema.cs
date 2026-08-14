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

    /// <summary>Hora de apertura de la jornada. Doble uso: es la hora de entrada que se asume cuando una Visita no trae <c>HoraEstimadaAcceso</c> (una visita "del jueves" sin hora se atiende desde la apertura del centro), y el inicio de la franja fuera de la cual una entrada se clasifica como Exprés.</summary>
    public TimeOnly HoraInicioJornada { get; private set; } = new(8, 0);

    /// <summary>Hora de cierre de la jornada — ver <see cref="HoraInicioJornada"/>.</summary>
    public TimeOnly HoraFinJornada { get; private set; } = new(18, 0);

    /// <summary>Horas de jornada disponibles al mes por Gestor CAE — denominador del KPI de ocupación. No es un dato contractual: es el divisor que la consultora considera representativo de una jornada completa.</summary>
    public int HorasJornadaMensualGestor { get; private set; } = 160;

    /// <summary>
    /// Feature toggle de empresa para la medición de tiempo de enfoque (art. 87 LOPDGDD).
    /// Nace apagado a propósito: la consultora debe informar previamente a su plantilla y
    /// a la representación legal antes de encenderlo. Con esto en <c>false</c> no se
    /// captura ni se persiste absolutamente nada.
    /// </summary>
    public bool MedicionTiempoActiva { get; private set; }

    /// <summary>Segundos sin foco de ventana tras los cuales el tramo de medición se cierra por inactividad.</summary>
    public int SegundosInactividadPausa { get; private set; } = 120;

    /// <summary>Si el tiempo registrado fuera de la franja de jornada se descarta al agregar métricas. No afecta a la captura — el tramo se guarda igual, solo se excluye del cálculo.</summary>
    public bool ExcluirFueraDeJornadaEnMetricas { get; private set; } = true;

    /// <summary>
    /// Límite blando (no bloqueante) de gasto estimado en IA documental por mes natural, en
    /// USD — mismo criterio de coste que <c>AuditoriaExtraccionIa.CosteEstimado</c>/
    /// <c>CosteEstimadoOcr</c> (Horizonte 2.7 del plan: "antes de que un piloto con mil
    /// documentos lo descubra por la factura de Anthropic"). <c>null</c> significa "sin
    /// presupuesto configurado" — no se avisa de nada, el comportamiento de hoy. Superarlo
    /// no bloquea ni degrada el servicio: solo enciende un aviso visual en el Dashboard
    /// Ejecutivo para el operador de la plataforma, igual que los umbrales de vencimiento
    /// documental no bloquean nada por sí mismos.
    /// </summary>
    public decimal? PresupuestoMensualIaUsd { get; private set; }

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

    public void ActualizarConfiguracionOperativa(
        TimeOnly horaInicioJornada,
        TimeOnly horaFinJornada,
        int horasJornadaMensualGestor,
        bool medicionTiempoActiva,
        int segundosInactividadPausa,
        bool excluirFueraDeJornadaEnMetricas)
    {
        if (horaFinJornada <= horaInicioJornada)
            throw new ArgumentException("La hora de fin de jornada debe ser posterior a la de inicio.", nameof(horaFinJornada));
        if (horasJornadaMensualGestor <= 0)
            throw new ArgumentException("Las horas de jornada mensual deben ser mayores que cero.", nameof(horasJornadaMensualGestor));
        if (segundosInactividadPausa is < 30 or > 600)
            throw new ArgumentException("Los segundos de inactividad deben estar entre 30 y 600.", nameof(segundosInactividadPausa));

        HoraInicioJornada = horaInicioJornada;
        HoraFinJornada = horaFinJornada;
        HorasJornadaMensualGestor = horasJornadaMensualGestor;
        MedicionTiempoActiva = medicionTiempoActiva;
        SegundosInactividadPausa = segundosInactividadPausa;
        ExcluirFueraDeJornadaEnMetricas = excluirFueraDeJornadaEnMetricas;
    }

    /// <summary><paramref name="presupuestoMensualIaUsd"/> en <c>null</c> desactiva el aviso (sin presupuesto configurado).</summary>
    public void ActualizarPresupuestoIa(decimal? presupuestoMensualIaUsd)
    {
        if (presupuestoMensualIaUsd is < 0)
            throw new ArgumentException("El presupuesto mensual de IA no puede ser negativo.", nameof(presupuestoMensualIaUsd));

        PresupuestoMensualIaUsd = presupuestoMensualIaUsd;
    }
}
