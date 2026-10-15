using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Documentación de requerimientos para una instalación/obra nueva en un
/// Centro de un Cliente — distinta de la documentación de acceso de
/// mantenimiento tradicional (Trabajador/Cliente/Empresa/Vehículo). Un
/// Proyecto pertenece siempre a un Cliente y a un Centro concreto (nunca a
/// "todos los centros" del cliente): tras cerrar una obra, ese mismo Centro
/// puede tener después mantenimientos o instalaciones nuevas, cada una su
/// propio Proyecto.
///
/// El nombre es único por Cliente (no solo por Centro): dos obras del mismo
/// cliente no pueden compartir nombre aunque estén en centros distintos, ni
/// aunque una ya esté cerrada — evita ambigüedad al referenciar "Proyecto
/// Ampliación Planta 2" en facturación o auditoría (ver
/// <see cref="IProyectoRepository.ExisteNombreParaClienteAsync"/>).
///
/// Igual que <see cref="Asignaciones.Asignacion"/> con FechaBaja, el estado
/// abierto/cerrado no se guarda como enum: se deriva de
/// <see cref="FechaCierreReal"/>. FechaFinPrevista es la vigencia
/// contratada (null = indefinida); FechaCierreReal es el cierre real,
/// independiente de si la vigencia prevista ya venció.
///
/// <see cref="Tipo"/> distingue la obra de siempre de un <see cref="TipoProyecto.Evento"/>: un evento tiene
/// siempre fecha de fin (FechaFinPrevista obligatoria) y está terminado en cuanto se cierra o pasa esa fecha
/// (<see cref="EventoTerminadoEn"/>). El tipo se fija al crear y no cambia.
/// </summary>
public class Proyecto : EntidadBase
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaNotas = 1000;

    public Guid ClienteId { get; private set; }
    public Guid CentroId { get; private set; }
    public TipoProyecto Tipo { get; private set; } = TipoProyecto.Obra;
    public string Nombre { get; private set; } = string.Empty;
    public DateOnly FechaInicio { get; private set; }
    public DateOnly? FechaFinPrevista { get; private set; }
    public DateOnly? FechaCierreReal { get; private set; }
    public string? Notas { get; private set; }

    public bool EstaAbierto => FechaCierreReal is null;

    public bool EsEvento => Tipo == TipoProyecto.Evento;

    /// <summary>
    /// Un evento terminado es uno cerrado o cuya fecha de fin ya pasó (el último día cuenta como vigente).
    /// Una obra nunca está «terminada» en este sentido: su cierre sigue siendo <see cref="FechaCierreReal"/>.
    /// La consulta de Trabajadores del Centro repite esta expresión en LINQ traducible a SQL.
    /// </summary>
    public bool EventoTerminadoEn(DateOnly hoy) =>
        EsEvento && (FechaCierreReal is not null || FechaFinPrevista < hoy);

    /// <summary>
    /// Un evento vigente es uno que ya empezó y no ha terminado (<see cref="EventoTerminadoEn"/>): mientras lo está,
    /// sus requisitos (<see cref="ProyectoRequisito"/>) cuentan para el cumplimiento de los voluntarios que participan.
    /// </summary>
    public bool EventoVigenteEn(DateOnly hoy) =>
        EsEvento && FechaInicio <= hoy && !EventoTerminadoEn(hoy);

    private Proyecto()
    {
    }

    public static Proyecto Crear(
        Guid clienteId, Guid centroId, string nombre, DateOnly fechaInicio, DateOnly? fechaFinPrevista, string? notas,
        TipoProyecto tipo = TipoProyecto.Obra)
    {
        if (!Enum.IsDefined(tipo))
            throw new ArgumentException("Tipo de proyecto no válido.", nameof(tipo));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El proyecto debe pertenecer a un cliente.", nameof(clienteId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("El proyecto debe pertenecer a un centro.", nameof(centroId));
        if (fechaFinPrevista is not null && fechaFinPrevista < fechaInicio)
            throw new ArgumentException("La fecha de fin prevista no puede ser anterior a la de inicio.", nameof(fechaFinPrevista));
        ExigirFinSiEsEvento(tipo, fechaFinPrevista);

        var proyecto = new Proyecto { ClienteId = clienteId, CentroId = centroId, FechaInicio = fechaInicio, Tipo = tipo };
        proyecto.EstablecerNombre(nombre);
        proyecto.FechaFinPrevista = fechaFinPrevista;
        proyecto.EstablecerNotas(notas);
        return proyecto;
    }

    public void Actualizar(string nombre, DateOnly? fechaFinPrevista, string? notas)
    {
        if (fechaFinPrevista is not null && fechaFinPrevista < FechaInicio)
            throw new ArgumentException("La fecha de fin prevista no puede ser anterior a la de inicio.", nameof(fechaFinPrevista));
        ExigirFinSiEsEvento(Tipo, fechaFinPrevista);

        EstablecerNombre(nombre);
        FechaFinPrevista = fechaFinPrevista;
        EstablecerNotas(notas);
    }

    public void Cerrar(DateOnly fechaCierre)
    {
        if (!EstaAbierto)
            throw new InvalidOperationException("El proyecto ya está cerrado.");
        if (fechaCierre < FechaInicio)
            throw new ArgumentException("La fecha de cierre no puede ser anterior a la de inicio.", nameof(fechaCierre));

        FechaCierreReal = fechaCierre;
    }

    public void Reabrir()
    {
        FechaCierreReal = null;
    }

    private static void ExigirFinSiEsEvento(TipoProyecto tipo, DateOnly? fechaFinPrevista)
    {
        if (tipo == TipoProyecto.Evento && fechaFinPrevista is null)
            throw new ArgumentException("Un evento necesita fecha de fin.", nameof(fechaFinPrevista));
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del proyecto es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
