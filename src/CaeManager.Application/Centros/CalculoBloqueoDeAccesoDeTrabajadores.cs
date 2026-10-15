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
public readonly record struct DocumentoParaBloqueo(
    Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId, DocumentoParaAcceso Documento);

/// <summary>
/// Un documento pendiente en la plataforma CAE de un Centro (<see cref="ReglaPendienteEnPlataforma"/>), ya filtrado por su
/// contexto (<c>PendientesEnPlataformaDeCentros</c>): de un Trabajador (<paramref name="TrabajadorId"/>) o de una Empresa
/// (<paramref name="EmpresaId"/>).
/// </summary>
/// <param name="EstadoAcreditacion">Sin subir o subido sin validar: dice cuál de las dos situaciones de pendiente es.</param>
public readonly record struct PendienteParaBloqueo(
    Guid CentroId, Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId, EstadoAcreditacion EstadoAcreditacion);

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
/// <item>Pendiente en la plataforma CAE de C (decisión 2026-10-10): un documento de Trabajador sin subir o sin validar allí
/// bloquea a ese Trabajador en C; uno de Empresa, a todos los Trabajadores de esa Empresa asignados a C. <b>Solo si C marca
/// el tipo como bloqueante</b> (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>): un tipo que C no marca no bloquea aunque
/// esté pendiente. Sin tolerancia. Si el requisito ya bloquea por ausente o vencido, manda esa situación; si lo cumple, pasa
/// a <see cref="SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma"/> o
/// <see cref="SituacionDeRequisitoBloqueante.SinValidarEnPlataforma"/>.</item>
/// </list>
/// </summary>
public static class CalculoBloqueoDeAccesoDeTrabajadores
{
    /// <summary>Solo los requisitos que bloquean.</summary>
    public static IReadOnlyList<BloqueoDeAccesoDeTrabajador> Calcular(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyCollection<RequisitoBloqueanteDelCentro> requisitos,
        IReadOnlyCollection<DocumentoParaBloqueo> documentos,
        DateOnly hoy,
        IReadOnlyCollection<PendienteParaBloqueo>? pendientesEnPlataforma = null) =>
        Evaluar(asignaciones, requisitos, documentos, hoy, pendientesEnPlataforma)
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
        DateOnly hoy,
        IReadOnlyCollection<PendienteParaBloqueo>? pendientesEnPlataforma = null)
    {
        var documentosDeTrabajador = documentos
            .Where(d => d.TrabajadorId is not null)
            .ToLookup(d => (d.TrabajadorId!.Value, d.TipoDocumentoId), d => d.Documento);
        var documentosDeEmpresa = documentos
            .Where(d => d.EmpresaId is not null)
            .ToLookup(d => (d.EmpresaId!.Value, d.TipoDocumentoId), d => d.Documento);
        var requisitosPorCentro = requisitos
            .Where(r => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(r.Ambito))
            .ToLookup(r => r.CentroId);
        var pendientesPorCentro = (pendientesEnPlataforma ?? []).ToLookup(p => p.CentroId);

        var resultado = new List<RequisitoEvaluado>();

        // Una fila por Trabajador × Centro: el Id de la fila de la Bandeja (IdDeFilaDeCola.Requisito) es ese trío
        // más el Tipo, así que dos Asignaciones del mismo Trabajador al mismo Centro darían dos filas con el mismo Id
        // (@key duplicada: el circuito de Blazor muere). La base lo impide (EXCLUDE de vigencias solapadas); aquí
        // no se depende de ello: el resultado es único por (Centro, Trabajador, Tipo) por construcción.
        foreach (var asignacion in asignaciones.DistinctBy(a => (a.CentroId, a.TrabajadorId)))
        {
            var inicioDeLaAsignacion = resultado.Count;
            foreach (var requisito in requisitosPorCentro[asignacion.CentroId])
            {
                if (requisito.Ambito == AmbitoAplicacion.Trabajador)
                {
                    resultado.Add(new RequisitoEvaluado(
                        asignacion.CentroId, asignacion.TrabajadorId, requisito.TipoDocumentoId, AmbitoAplicacion.Trabajador, EmpresaId: null,
                        ReglaBloqueoDeAcceso.Evaluar(
                            documentosDeTrabajador[(asignacion.TrabajadorId, requisito.TipoDocumentoId)], requisito.Condiciones, hoy),
                        requisito.Condiciones.ToleranciaDias));
                }
                else if (asignacion.EmpresaDelTrabajadorId is { } empresaId)
                {
                    resultado.Add(new RequisitoEvaluado(
                        asignacion.CentroId, asignacion.TrabajadorId, requisito.TipoDocumentoId, AmbitoAplicacion.Empresa, empresaId,
                        ReglaBloqueoDeAcceso.Evaluar(
                            documentosDeEmpresa[(empresaId, requisito.TipoDocumentoId)], requisito.Condiciones, hoy),
                        requisito.Condiciones.ToleranciaDias));
                }
            }

            AplicarPendientesEnPlataforma(asignacion, pendientesPorCentro[asignacion.CentroId], resultado, inicioDeLaAsignacion);
        }

        return resultado;
    }

    /// <summary>
    /// Los pendientes en la plataforma del Centro que afectan a esta Asignación: los del propio Trabajador y los de su
    /// Empresa. Solo bloquean donde el Centro marca el tipo como bloqueante (corrección del propietario, 2026-10-10): se
    /// aplican sobre la fila ya evaluada del requisito bloqueante (<see cref="ReglaBloqueoDeAcceso.AplicarPendienteEnPlataforma"/>),
    /// y un pendiente sin esa fila —tipo sin <c>BloqueaAcceso</c> en el Centro— no añade nada. Una sola fila por (Centro,
    /// Trabajador, Tipo), por construcción.
    /// </summary>
    private static void AplicarPendientesEnPlataforma(
        AsignacionParaBloqueo asignacion, IEnumerable<PendienteParaBloqueo> pendientesDelCentro, List<RequisitoEvaluado> resultado,
        int inicioDeLaAsignacion)
    {
        foreach (var pendiente in pendientesDelCentro)
        {
            AmbitoAplicacion ambito;
            if (pendiente.TrabajadorId is { } trabajadorId)
            {
                if (trabajadorId != asignacion.TrabajadorId) continue;
                ambito = AmbitoAplicacion.Trabajador;
            }
            else if (pendiente.EmpresaId is { } empresaDelPendiente && empresaDelPendiente == asignacion.EmpresaDelTrabajadorId)
            {
                ambito = AmbitoAplicacion.Empresa;
            }
            else
            {
                continue;
            }

            // Las filas de esta Asignación son las últimas añadidas: se busca solo entre ellas.
            var indice = resultado.FindIndex(
                inicioDeLaAsignacion, r => r.TipoDocumentoId == pendiente.TipoDocumentoId && r.Ambito == ambito);
            var requisitoBloqueante = indice < 0 ? (ResultadoDeRequisito?)null : resultado[indice].Resultado;
            if (ReglaBloqueoDeAcceso.AplicarPendienteEnPlataforma(requisitoBloqueante, pendiente.EstadoAcreditacion) is not { } aplicado)
                continue;

            if (aplicado != resultado[indice].Resultado)
                resultado[indice] = resultado[indice] with { Resultado = aplicado, ToleranciaDias = 0 };
        }
    }
}
