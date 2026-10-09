using CaeManager.Domain.Documentos;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;

/// <summary>
/// Las palabras de estado con las que la página «Tipo de documento 360» agrupa sus filas (decisión del 2026-10-08): una
/// sola «Por vencer» para <see cref="EstadoDocumento.Urgente"/> y <see cref="EstadoDocumento.Proximo"/>, y «Vigente»
/// también para <see cref="EstadoDocumento.SinCaducidad"/>. El orden del enum es el de los contadores, del peor al mejor.
/// «Bloqueado» no está: depende del bloqueo por Centro, que esta consulta todavía no lee.
/// </summary>
public enum GrupoEstadoTipoDocumento
{
    Vencido = 0,
    Pendiente = 1,
    EnTolerancia = 2,
    PorVencer = 3,
    SinConfirmar = 4,
    Vigente = 5
}

/// <summary>Un par exigido (Centro × Trabajador) del tipo, ya con los nombres y el estado visto desde ese Centro.</summary>
public sealed record ParDeTipoDocumento(
    Guid TrabajadorId,
    string TrabajadorNombre,
    string? Dni,
    Guid? EmpresaId,
    string? EmpresaNombre,
    Guid CentroId,
    string CentroNombre,
    Guid ClienteEmpresarialId,
    EstadoDocumento Estado,
    DateOnly? EnToleranciaHasta,
    Guid? DocumentoId,
    DateOnly? FechaEmision,
    DateOnly? FechaVencimiento);

/// <summary>
/// Agrupación y recuento de la página «Tipo de documento 360». Función pura sobre los pares exigidos del tipo:
/// el anillo cuenta pares (un Trabajador al día en 3 de 4 Centros suma 3 de 4), la fila lleva el peor de sus Centros y
/// los contadores cuentan filas por ese peor estado.
/// </summary>
public static class EstadoTipoDocumentoCalculo
{
    /// <summary>
    /// Del peor al mejor. No es el orden numérico de <see cref="EstadoDocumento"/> (congelado por ordinales publicados):
    /// «Sin confirmar» va detrás de «Por vencer» y «En tolerancia» delante.
    /// </summary>
    private static readonly EstadoDocumento[] DelPeorAlMejor =
    [
        EstadoDocumento.Vencido, EstadoDocumento.Faltante, EstadoDocumento.EnTolerancia, EstadoDocumento.Urgente,
        EstadoDocumento.Proximo, EstadoDocumento.SinConfirmar, EstadoDocumento.Vigente, EstadoDocumento.SinCaducidad
    ];

    public static int Gravedad(EstadoDocumento estado)
    {
        var posicion = Array.IndexOf(DelPeorAlMejor, estado);
        return posicion < 0 ? DelPeorAlMejor.Length : posicion;
    }

    public static GrupoEstadoTipoDocumento Grupo(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => GrupoEstadoTipoDocumento.Vencido,
        EstadoDocumento.Faltante => GrupoEstadoTipoDocumento.Pendiente,
        EstadoDocumento.EnTolerancia => GrupoEstadoTipoDocumento.EnTolerancia,
        EstadoDocumento.Urgente or EstadoDocumento.Proximo => GrupoEstadoTipoDocumento.PorVencer,
        EstadoDocumento.SinConfirmar => GrupoEstadoTipoDocumento.SinConfirmar,
        _ => GrupoEstadoTipoDocumento.Vigente
    };

    /// <summary>
    /// Las filas (una por Trabajador), del peor estado al mejor y, a igualdad, por nombre. Cada fila lleva sus Centros
    /// en ese mismo orden.
    /// </summary>
    public static IReadOnlyList<FilaTrabajadorTipoDocumentoDto> Agrupar(IEnumerable<ParDeTipoDocumento> pares) =>
        pares
            .GroupBy(p => p.TrabajadorId)
            .Select(grupo =>
            {
                var centros = grupo
                    .OrderBy(p => Gravedad(p.Estado))
                    .ThenBy(p => p.CentroNombre, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => new EstadoEnCentroDto(p.CentroId, p.CentroNombre, p.ClienteEmpresarialId, p.Estado, p.EnToleranciaHasta))
                    .ToList();
                var primero = grupo.First();
                return new FilaTrabajadorTipoDocumentoDto(
                    primero.TrabajadorId, primero.TrabajadorNombre, primero.Dni, primero.EmpresaId, primero.EmpresaNombre,
                    primero.DocumentoId, primero.FechaEmision, primero.FechaVencimiento,
                    PeorEstado: centros[0].Estado,
                    CentrosAlDia: centros.Count(c => CumplimientoDocumental.EsConforme(c.Estado)),
                    Centros: centros);
            })
            .OrderBy(f => Gravedad(f.PeorEstado))
            .ThenBy(f => f.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(f => f.TrabajadorId)
            .ToList();

    /// <summary>Filas por grupo de su peor estado; solo los grupos con al menos una fila, en el orden de los contadores.</summary>
    public static IReadOnlyList<RecuentoGrupoEstadoDto> Recuentos(IEnumerable<FilaTrabajadorTipoDocumentoDto> filas) =>
        filas
            .GroupBy(f => Grupo(f.PeorEstado))
            .OrderBy(g => g.Key)
            .Select(g => new RecuentoGrupoEstadoDto(g.Key, g.Count()))
            .ToList();
}
