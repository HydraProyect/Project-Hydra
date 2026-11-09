using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;

namespace CaeManager.Application.AsistenteIa.Edicion;

/// <summary>
/// Cómo completa el Gestor CAE un dato del plan a mano. Se deriva del campo de la
/// orden (<see cref="CampoDeOrden"/>), no se declara aparte: así una orden nueva no
/// puede quedarse sin control de edición sin que se vea en este fichero.
/// </summary>
public enum TipoDatoPlan
{
    /// <summary>Un valor de la cartera (Empresa, Cliente empresarial, Centro…).</summary>
    Seleccion,

    /// <summary>Varios valores de la cartera (las personas de una Visita).</summary>
    SeleccionMultiple,

    Fecha,

    /// <summary>Texto libre corto (motivo, nombre, dirección, CIF…).</summary>
    Texto,

    /// <summary>La persona que se da de alta: nombre, apellidos y documento de identidad.</summary>
    TrabajadorNuevo,

    SiNo,

    /// <summary>
    /// El plan todavía no sabe ofrecerlo a mano (p. ej. los documentos de una
    /// reclamación). La orden que lo pida no se ejecuta desde el asistente.
    /// </summary>
    SinEdicion,
}

/// <summary>Los datos de la persona que se da de alta en la orden de alta de Trabajador.</summary>
public sealed record TrabajadorNuevoDto(string Nombre, string Apellidos, string Documento)
{
    public bool Completo =>
        !string.IsNullOrWhiteSpace(Nombre) && !string.IsNullOrWhiteSpace(Apellidos) && !string.IsNullOrWhiteSpace(Documento);
}

/// <summary>
/// El valor de un dato del plan, tal como lo fijó el Gestor CAE (o el asistente, en
/// los datos de selección). Solo va informado el miembro que corresponde a su
/// <see cref="TipoDatoPlan"/>.
/// </summary>
public sealed record ValorDatoPlanDto(
    string Campo,
    Guid? Id = null,
    IReadOnlyList<Guid>? Ids = null,
    string? Texto = null,
    DateOnly? Fecha = null,
    bool? SiNo = null,
    TrabajadorNuevoDto? Trabajador = null)
{
    /// <summary>El dato está completo: un obligatorio con este valor ya no falta.</summary>
    public bool Completo =>
        Id is not null
        || Ids is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(Texto)
        || Fecha is not null
        || SiNo is not null
        || Trabajador is { Completo: true };
}

/// <summary>
/// Una opción que el plan ofrece a mano para un dato de selección. Sale de la misma
/// lectura que los candidatos del asistente (<see cref="LectorCandidatosAsistente"/>).
/// </summary>
public sealed record OpcionDatoPlanDto(Guid Id, string Etiqueta, Guid? GrupoId = null, bool EsSubcontrata = false);

public static class TiposDatoPlan
{
    public const string CampoTrabajador = "trabajador";
    public const string CampoFechaInicio = "fecha_inicio";
    public const string CampoFechaFin = "fecha_fin";
    public const string CampoMotivo = "motivo";
    public const string CampoNombre = "nombre";
    public const string CampoDireccion = "direccion";
    public const string CampoRazonSocial = "razon_social";
    public const string CampoCif = "cif";
    public const string CampoEsCritico = "es_critico";

    /// <summary>Longitud máxima de un texto libre del plan (motivo, dirección…).</summary>
    public const int LongitudMaximaTexto = 500;

    public static TipoDatoPlan De(string ordenId, CampoDeOrden campo)
    {
        ArgumentNullException.ThrowIfNull(campo);
        if (campo.Nombre == CampoTrabajador && ordenId == CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion)
            return TipoDatoPlan.TrabajadorNuevo;
        if (campo.Nombre == CampoEsCritico)
            return TipoDatoPlan.SiNo;
        if (campo.Nombre == ObtenerCandidatosAsistenteQueryHandler.CampoTrabajadores)
            return TipoDatoPlan.SeleccionMultiple;

        return campo.Forma switch
        {
            FormaDeExtraccion.SeleccionDeCatalogo when ObtenerCandidatosAsistenteQueryHandler.CamposConCandidatos.Contains(campo.Nombre)
                => TipoDatoPlan.Seleccion,
            FormaDeExtraccion.SeleccionDeCatalogo => TipoDatoPlan.SinEdicion,
            FormaDeExtraccion.PartesCerradas => TipoDatoPlan.Fecha,
            _ => TipoDatoPlan.Texto,
        };
    }

    /// <summary>Los campos de la orden cuyas opciones salen de la cartera (selección simple o múltiple).</summary>
    public static IReadOnlyList<string> CamposConOpciones(OrdenAsistida orden) =>
        orden.Campos
            .Where(c => De(orden.Id, c) is TipoDatoPlan.Seleccion or TipoDatoPlan.SeleccionMultiple)
            .Select(c => c.Nombre)
            .Where(n => n != ObtenerCandidatosAsistenteQueryHandler.CampoTenant)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
