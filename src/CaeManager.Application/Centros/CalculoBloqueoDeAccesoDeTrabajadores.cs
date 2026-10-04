using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Centros;

/// <summary>Una Asignación activa de un Trabajador a un Centro, con la Empresa de la que es el Trabajador.</summary>
/// <param name="EmpresaDelTrabajadorId">
/// La Empresa del Trabajador sea cual sea la columna que la guarda (<c>Trabajador.EmpresaId</c> o, en un
/// subcontratista, <c>Trabajador.SubcontrataId</c>: las dos son un <c>Empresa.Id</c>; deuda terminológica
/// heredada, el Trabajador no tiene «Subcontrata» como tipo de Empresa). <c>null</c> solo si el dato falta.
/// </param>
public readonly record struct AsignacionParaBloqueo(Guid CentroId, Guid TrabajadorId, Guid? EmpresaDelTrabajadorId);

/// <summary>
/// Un requisito bloqueante de UN Centro: el Tipo de documento que ese Centro marca como bloqueante, de quién es
/// (Trabajador o Empresa) y las condiciones de ese Centro (vigencia propia y tolerancia ya resuelta).
/// </summary>
public readonly record struct RequisitoBloqueanteDelCentro(
    Guid CentroId, Guid TipoDocumentoId, AmbitoAplicacion Ambito, CondicionesDeAccesoDelCentro Condiciones);

/// <summary>Un Documento de un Trabajador (<paramref name="TrabajadorId"/>) o de una Empresa (<paramref name="EmpresaId"/>).</summary>
/// <param name="UltimaPresentacionPorCentro">
/// Fecha de la última presentación del Documento en cada Centro donde se presentó (<c>PresentacionDocumentoEnCentro</c>), por
/// Centro. <see cref="Evaluar"/> toma la del Centro de cada requisito: la periodicidad especial de un Centro cuenta desde
/// la presentación EN ESE Centro. <c>null</c> o sin entrada = nunca se presentó allí (el ancla es la emisión).
/// </param>
public readonly record struct DocumentoParaBloqueo(
    Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId, DocumentoParaAcceso Documento,
    IReadOnlyDictionary<Guid, DateOnly>? UltimaPresentacionPorCentro = null)
{
    /// <summary>El Documento tal como lo ve la regla en el Centro dado: con la fecha de su última presentación allí, si la hay.</summary>
    public DocumentoParaAcceso EnElCentro(Guid centroId) =>
        UltimaPresentacionPorCentro is { } porCentro && porCentro.TryGetValue(centroId, out var fecha)
            ? Documento with { UltimaPresentacionEnElCentro = fecha }
            : Documento;
}

/// <summary>
/// Un requisito bloqueante evaluado para un Trabajador en un Centro, bloquee o no. Lleva la tolerancia con la que se
/// evaluó para que una vista pueda decir «en tolerancia hasta X» sin recalcular (<see cref="ResultadoDeRequisito.EnToleranciaHasta"/>).
/// </summary>
/// <param name="Ambito">Quién es el sujeto del requisito: el propio Trabajador o su Empresa.</param>
/// <param name="EmpresaId">La Empresa dueña del requisito cuando <paramref name="Ambito"/> es Empresa; si no, <c>null</c>.</param>
public readonly record struct RequisitoEvaluado(
    Guid CentroId, Guid TrabajadorId, Guid TipoDocumentoId, AmbitoAplicacion Ambito, Guid? EmpresaId,
    ResultadoDeRequisito Resultado, int ToleranciaDias);

/// <summary>Un Trabajador bloqueado en un Centro por un requisito bloqueante concreto.</summary>
/// <param name="Ambito">Quién es el sujeto del requisito: el propio Trabajador o su Empresa.</param>
/// <param name="EmpresaId">La Empresa dueña del requisito cuando <paramref name="Ambito"/> es Empresa; si no, <c>null</c>.</param>
/// <param name="ToleranciaDias">La tolerancia que rige en ese Centro para el Tipo: con valor mayor que 0 un «Vencido» es, en rigor, un «fin de tolerancia».</param>
/// <param name="VencimientoEfectivo">Desde cuándo no vale el documento en este Centro; <c>null</c> si está ausente.</param>
public record BloqueoDeAccesoDeTrabajador(
    Guid CentroId, Guid TrabajadorId, Guid TipoDocumentoId, AmbitoAplicacion Ambito,
    Guid? EmpresaId, SituacionDeRequisitoBloqueante Situacion, int ToleranciaDias, DateOnly? VencimientoEfectivo);

/// <summary>
/// Aplica <see cref="ReglaBloqueoDeAcceso"/> a un conjunto de Asignaciones, POR CENTRO: qué Trabajadores están
/// bloqueados en qué Centros y por qué requisito. Función pura (sin base de datos) para que la regla se pruebe sola y
/// las superficies que necesiten la respuesta la pidan aquí, no la recalculen. La carga de datos, la resolución de la
/// tolerancia de cada Centro y el alcance de cartera (Centros visibles, sin gestión CAE) son de quien la llama; el
/// aislamiento entre Tenants lo da RLS al cargar.
///
/// <list type="bullet">
/// <item>Trabajador bloqueado en el Centro C: le falta, o no vale en C, algún documento bloqueante de Trabajador que C exige.</item>
/// <item>Todos los Trabajadores de la Empresa E bloqueados en C: a E le falta, o no vale en C, algún documento bloqueante de
/// Empresa que C exige. Un requisito de Empresa que C no exige no bloquea en C.</item>
/// <item>Un Trabajador sin ningún documento está bloqueado en los Centros que exigen algo: no hay «alta nueva» exenta.</item>
/// </list>
/// </summary>
public static class CalculoBloqueoDeAccesoDeTrabajadores
{
    /// <summary>Solo los requisitos que bloquean.</summary>
    public static IReadOnlyList<BloqueoDeAccesoDeTrabajador> Calcular(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyCollection<RequisitoBloqueanteDelCentro> requisitos,
        IReadOnlyCollection<DocumentoParaBloqueo> documentos,
        DateOnly hoy) =>
        Evaluar(asignaciones, requisitos, documentos, hoy)
            .Where(e => ReglaBloqueoDeAcceso.Bloquea(e.Resultado.Situacion))
            .Select(e => new BloqueoDeAccesoDeTrabajador(
                e.CentroId, e.TrabajadorId, e.TipoDocumentoId, e.Ambito, e.EmpresaId,
                e.Resultado.Situacion, e.ToleranciaDias, e.Resultado.VencimientoEfectivo))
            .ToList();

    /// <summary>Todos los requisitos evaluados (cumplidos, en tolerancia y bloqueantes), para las vistas que enseñan el detalle por Trabajador.</summary>
    public static IReadOnlyList<RequisitoEvaluado> Evaluar(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyCollection<RequisitoBloqueanteDelCentro> requisitos,
        IReadOnlyCollection<DocumentoParaBloqueo> documentos,
        DateOnly hoy)
    {
        var documentosDeTrabajador = documentos
            .Where(d => d.TrabajadorId is not null)
            .ToLookup(d => (d.TrabajadorId!.Value, d.TipoDocumentoId));
        var documentosDeEmpresa = documentos
            .Where(d => d.EmpresaId is not null)
            .ToLookup(d => (d.EmpresaId!.Value, d.TipoDocumentoId));
        var requisitosPorCentro = requisitos
            .Where(r => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(r.Ambito))
            .ToLookup(r => r.CentroId);

        var resultado = new List<RequisitoEvaluado>();

        // Una fila por Trabajador × Centro: el Id de la fila de la Bandeja (IdDeFilaDeCola.Requisito) es ese trío
        // más el Tipo, así que dos Asignaciones del mismo Trabajador al mismo Centro darían dos filas con el mismo Id
        // (@key duplicada: el circuito de Blazor muere). La base lo impide (EXCLUDE de vigencias solapadas); aquí
        // no se depende de ello: el resultado es único por (Centro, Trabajador, Tipo) por construcción.
        foreach (var asignacion in asignaciones.DistinctBy(a => (a.CentroId, a.TrabajadorId)))
        {
            foreach (var requisito in requisitosPorCentro[asignacion.CentroId])
            {
                if (requisito.Ambito == AmbitoAplicacion.Trabajador)
                {
                    resultado.Add(new RequisitoEvaluado(
                        asignacion.CentroId, asignacion.TrabajadorId, requisito.TipoDocumentoId, AmbitoAplicacion.Trabajador, EmpresaId: null,
                        ReglaBloqueoDeAcceso.Evaluar(
                            documentosDeTrabajador[(asignacion.TrabajadorId, requisito.TipoDocumentoId)].Select(d => d.EnElCentro(asignacion.CentroId)),
                            requisito.Condiciones, hoy),
                        requisito.Condiciones.ToleranciaDias));
                }
                else if (asignacion.EmpresaDelTrabajadorId is { } empresaId)
                {
                    resultado.Add(new RequisitoEvaluado(
                        asignacion.CentroId, asignacion.TrabajadorId, requisito.TipoDocumentoId, AmbitoAplicacion.Empresa, empresaId,
                        ReglaBloqueoDeAcceso.Evaluar(
                            documentosDeEmpresa[(empresaId, requisito.TipoDocumentoId)].Select(d => d.EnElCentro(asignacion.CentroId)),
                            requisito.Condiciones, hoy),
                        requisito.Condiciones.ToleranciaDias));
                }
            }
        }

        return resultado;
    }
}
